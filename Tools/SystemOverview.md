# ML Category Classifier - System Overview

## What Does This System Do?

The ML Category Classifier is a .NET 9 Web API that intelligently routes users to appropriate category pages in a classified ads platform. When a user types something like "أريد بيع آيفون" (I want to sell iPhone), the system:

1. **Detects Intent**: Recognizes "أريد بيع" (I want to sell) as a publish intent
2. **Classifies Category**: Uses ML to predict "Smartphones" as the most relevant category
3. **Returns URL**: Provides the full path like `الالكترونيات/موبايلات/موبايلات-ذكية/ads/create`

The system supports both Arabic and Kurdish languages, combining substring matching with machine learning for accurate predictions.

---

## System Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    REST API Layer                            │
│              POST /api/ml/classify/{langCode}                │
└────────────────────────┬────────────────────────────────────┘
                         │
┌────────────────────────┴────────────────────────────────────┐
│              CategoryClassificationService                   │
│  ┌──────────────┐  ┌─────────────┐  ┌──────────────────┐   │
│  │IntentDetector│  │SubstringMatcher│  │   MLClassifier   │   │
│  │(Keyword Match)│  │ (LIKE Match)  │  │(Logistic Regress)│   │
│  └──────────────┘  └─────────────┘  └──────────────────┘   │
└────────────────────────┬────────────────────────────────────┘
                         │
┌────────────────────────┴────────────────────────────────────┐
│              Data Layer (Startup Sync)                       │
│  PostgreSQL ──────────► TrainingDataSyncService ──► MongoDB  │
│  (Source of Truth)           (On Startup)        (ML Training)│
└─────────────────────────────────────────────────────────────┘
```

---

## Core Classification Flow

When a user submits text for classification, the system follows this pipeline:

### Step 1: Intent Detection
The `IntentDetector` scans input for publish keywords. If found, the user wants to create an ad (filtered to leaf categories only). Otherwise, they're browsing.

```csharp
// IntentDetector.cs - Core logic
public UserIntent DetectIntent(string inputText, string languageCode)
{
    if (!_intentKeywords.TryGetValue(languageCode, out var keywords))
        return UserIntent.Browse;

    // Check if any publish keyword exists in input
    foreach (var keyword in keywords)
    {
        if (inputText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            return UserIntent.Publish;
    }
    return UserIntent.Browse;
}
```

**Configured Keywords (appsettings.json):**
- Arabic: `["نشر", "للبيع", "بيع", "اريد بيع", "اريد نشر"]`
- Kurdish: `["فرۆشتن", "دەمەوێ بفرۆشم"]`

### Step 2: Text Preprocessing
Arabic/Kurdish text requires special handling. The `TextPreprocessor` strips common prefixes to improve matching accuracy.

```csharp
// TextPreprocessor.cs - Prefix stripping
public string[] StripPrefixes(string word, string languageCode)
{
    var results = new HashSet<string> { word }; // Keep original

    if (!_prefixes.TryGetValue(languageCode, out var prefixes))
        return results.ToArray();

    // Strip longest matching prefix first
    foreach (var prefix in prefixes) // Sorted by length descending
    {
        if (word.StartsWith(prefix) && word.Length > prefix.Length)
        {
            var stripped = word.Substring(prefix.Length);
            results.Add(stripped);
            break; // Only strip one prefix
        }
    }
    return results.ToArray();
}
```

**Arabic Prefixes:** `["وال", "بال", "كال", "فال", "لل", "ال", "و", "ب", "ك", "ف", "ل"]`

Example: "والسيارات" → ["والسيارات", "سيارات"]

### Step 3: Dual Classification Strategy

The system uses two complementary approaches:

#### A. Substring Matching (LIKE-style)
Fast, exact matching with a fixed 25% confidence score:

```csharp
// SubstringMatcher.cs - LIKE matching
public const float FixedMatchScore = 0.25f;

public List<SubstringMatchResult> FindMatches(string input, List<TrainingDataDocument> categories, string languageCode)
{
    var normalizedInput = input.Trim().ToLowerInvariant();
    var strippedInputs = _textPreprocessor.StripPrefixes(normalizedInput, languageCode);

    foreach (var category in categories)
    {
        var categoryName = languageCode == "ar" ? category.NameArabic : category.NameKurdish;
        var normalizedCategoryName = categoryName.ToLowerInvariant();

        foreach (var inputVariation in strippedInputs)
        {
            if (normalizedCategoryName.Contains(inputVariation))
            {
                return new List<SubstringMatchResult> 
                {
                    new SubstringMatchResult 
                    { 
                        CategoryId = category.CategoryId,
                        Score = FixedMatchScore  // Always 25%
                    }
                };
            }
        }
    }
    return new List<SubstringMatchResult>();
}
```

#### B. ML Classification (Logistic Regression)
Uses ML.NET's SdcaMaximumEntropy for probabilistic predictions:

```csharp
// MLClassifier.cs - ML Pipeline construction
var pipeline = _mlContext.Transforms.Conversion.MapValueToKey("Label", "Category")
    .Append(_mlContext.Transforms.Text.NormalizeText("NormalizedText", "Text"))
    .Append(_mlContext.Transforms.Text.TokenizeIntoWords("Tokens", "NormalizedText"))
    .Append(_mlContext.Transforms.Text.RemoveStopWords("TokensClean", "Tokens", stopwords: stopwords))
    .Append(_mlContext.Transforms.Conversion.MapValueToKey("TokensKey", "TokensClean"))
    .Append(_mlContext.Transforms.Text.ProduceNgrams("Features", "TokensKey",
        ngramLength: 2,      // Bigrams
        useAllLengths: true)) // Include unigrams too
    .Append(_mlContext.MulticlassClassification.Trainers.SdcaMaximumEntropy(
        labelColumnName: "Label",
        featureColumnName: "Features",
        maximumNumberOfIterations: 100))
    .Append(_mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel"));
```

**Softmax Probability Conversion:**
```csharp
// MLClassifier.cs - Convert raw scores to probabilities
public static float[] ApplySoftmax(float[] scores)
{
    var maxScore = scores.Max();
    var expScores = scores.Select(s => (float)Math.Exp(s - maxScore)).ToArray();
    var sumExp = expScores.Sum();
    
    return expScores.Select(e => e / sumExp).ToArray(); // Sum to 1.0
}
```

### Step 4: Result Combination
The orchestrator combines both approaches following strict rules:

```csharp
// CategoryClassificationService.cs - Result combination
private List<CategoryPredictionResponse> CombineResults(
    List<SubstringMatchResult> substringMatches,
    List<PredictionResult> mlPredictions,
    List<TrainingDataDocument> trainingData,
    string languageCode,
    UserIntent intent)
{
    var results = new List<CategoryPredictionResponse>();
    var usedCategoryIds = new HashSet<ushort>();
    var urlSuffix = intent == UserIntent.Publish ? "/ads/create" : "/ads";

    // Rule: 1 LIKE match + 2 ML predictions, OR 3 ML predictions if no LIKE match
    if (substringMatches.Count > 0)
    {
        var match = substringMatches[0];
        var doc = trainingData.FirstOrDefault(d => d.CategoryId == match.CategoryId);
        if (doc != null)
        {
            results.Add(CreatePredictionResponse(doc, match.Score, "LIKE", languageCode, urlSuffix));
            usedCategoryIds.Add(match.CategoryId);
        }
    }

    // Add ML predictions (excluding duplicates)
    foreach (var prediction in mlPredictions.OrderByDescending(p => p.Score))
    {
        if (results.Count >= 3) break;
        if (usedCategoryIds.Contains(prediction.CategoryId)) continue;

        var doc = trainingData.FirstOrDefault(d => d.CategoryId == prediction.CategoryId);
        if (doc != null)
        {
            results.Add(CreatePredictionResponse(doc, prediction.Score, "ML", languageCode, urlSuffix));
            usedCategoryIds.Add(prediction.CategoryId);
        }
    }
    return results;
}
```

---

## Training Data Structure

MongoDB documents combine category info with brand/model names for ML training:

```csharp
// TrainingDataDocument.cs
public class TrainingDataDocument
{
    public ushort CategoryId { get; set; }
    public string NameArabic { get; set; }
    public string NameKurdish { get; set; }
    public string UrlSlugArabic { get; set; }    // Full path: "الالكترونيات/موبايلات"
    public string UrlSlugKurdish { get; set; }
    public bool IsLeaf { get; set; }              // Can publish ads here?
    public bool HasModels { get; set; }           // Has brand/model data?
    public List<BrandModelInfo> BrandsModels { get; set; }
    public List<string> ArabicTrainingExamples { get; set; }
    public List<string> KurdishTrainingExamples { get; set; }
}
```

**Training Data Construction:**
```csharp
// MLClassifier.cs - Build training examples
private List<TrainingInput> BuildTrainingData(List<TrainingDataDocument> documents, string languageCode)
{
    foreach (var doc in documents)
    {
        var examples = new List<string>();
        
        // Add language-specific training examples
        examples.AddRange(languageCode == "ar" ? doc.ArabicTrainingExamples : doc.KurdishTrainingExamples);

        // Add brand/model names if available (e.g., "iPhone", "Samsung")
        if (doc.HasModels && doc.BrandsModels != null)
        {
            foreach (var brandModel in doc.BrandsModels)
            {
                examples.Add(brandModel.NameEnglish);  // Always add English
                examples.Add(languageCode == "ar" ? brandModel.NameArabic : brandModel.NameKurdish);
            }
        }

        // Always add category name as fallback
        examples.Add(languageCode == "ar" ? doc.NameArabic : doc.NameKurdish);
        
        // Preprocess and add to training set
        foreach (var example in examples.Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            trainingData.Add(new TrainingInput 
            { 
                Text = _textPreprocessor.Preprocess(example, languageCode),
                Category = doc.CategoryId.ToString()
            });
        }
    }
}
```

---

## API Endpoint

```
POST /api/ml/classify/{langCode}
```

**Request:**
```json
{
  "text": "أريد بيع آيفون 15"
}
```

**Response:**
```json
{
  "predictions": [
    {
      "categoryId": 42,
      "categoryName": "موبايلات ذكية",
      "url": "الالكترونيات/موبايلات/موبايلات-ذكية/ads/create",
      "score": 0.87,
      "matchType": "ML"
    },
    {
      "categoryId": 15,
      "categoryName": "اكسسوارات موبايل",
      "url": "الالكترونيات/موبايلات/اكسسوارات/ads/create",
      "score": 0.08,
      "matchType": "ML"
    }
  ]
}
```

---

## Startup Sequence

```csharp
// Program.cs - Application initialization
static async Task InitializeApplicationAsync(IServiceProvider services)
{
    // Step 1: Sync PostgreSQL → MongoDB
    var syncService = services.GetRequiredService<TrainingDataSyncService>();
    await syncService.SyncAllCategoriesAsync();
    
    // Step 2: Train ML models for both languages
    var trainingDocuments = await trainingDataRepo.GetAllAsync();
    mlClassifier.Train(trainingDocuments, "ar");
    mlClassifier.Train(trainingDocuments, "kr");
    
    // Step 3: PostgresNotificationListener starts automatically (IHostedService)
}
```

---

## Error Handling & Graceful Degradation

The system continues operating even during partial failures:

```csharp
// CategoryClassificationService.cs - Cached fallback
private async Task<List<TrainingDataDocument>> GetFilteredTrainingDataWithFallbackAsync(UserIntent intent)
{
    try
    {
        var data = await GetFilteredTrainingDataAsync(intent);
        _cachedTrainingData = data;  // Update cache on success
        return data;
    }
    catch (Exception ex)
    {
        // Use cached data if database fails
        if (_cachedTrainingData != null && DateTime.UtcNow - _cacheTimestamp < _cacheExpiry)
        {
            _logger.LogWarning("Using cached training data due to database failure");
            return intent == UserIntent.Publish 
                ? _cachedTrainingData.Where(d => d.IsLeaf).ToList()
                : _cachedTrainingData;
        }
        throw new ClassificationException("Database connection failed", ClassificationErrorType.DatabaseError, ex);
    }
}
```

---

## Key Design Decisions

1. **Dual Classification**: Substring matching provides fast, exact matches; ML handles fuzzy/semantic matching
2. **Fixed LIKE Score (25%)**: Prevents substring matches from dominating when ML has higher confidence
3. **Separate Models per Language**: Arabic and Kurdish have different linguistic patterns
4. **Startup Sync**: PostgreSQL data is synced to MongoDB on application startup; restart to pick up changes
5. **Graceful Degradation**: Cached data and fallback strategies keep the system running during failures
6. **Simple Architecture**: No interfaces or abstractions - just concrete classes for maintainability
