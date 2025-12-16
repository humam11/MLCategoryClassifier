namespace MLCategoryClassifier;

// Custom exception for classification service errors
public class ClassificationException : Exception
{
    public ClassificationErrorType ErrorType { get; }
    
    public ClassificationException(string message, ClassificationErrorType errorType) 
        : base(message)
    {
        ErrorType = errorType;
    }
    
    public ClassificationException(string message, ClassificationErrorType errorType, Exception innerException) 
        : base(message, innerException)
    {
        ErrorType = errorType;
    }
}

// Types of classification errors for proper HTTP status code mapping
public enum ClassificationErrorType
{
    InvalidInput,       // 400 Bad Request
    ModelNotReady,      // 503 Service Unavailable
    DatabaseError,      // 503 Service Unavailable
    PredictionError,    // 500 Internal Server Error
    InternalError       // 500 Internal Server Error
}

// Orchestrates the full category classification flow including intent detection, substring matching, and ML prediction
public class CategoryClassificationService
{
    private readonly MLClassifier _mlClassifier;
    private readonly TextPreprocessor _textPreprocessor;
    private readonly SubstringMatcher _substringMatcher;
    private readonly IntentDetector _intentDetector;
    private readonly TrainingDataRepository _trainingDataRepository;
    private readonly ILogger<CategoryClassificationService> _logger;
    
    // Cached training data for graceful degradation during database failures
    private List<TrainingDataDocument>? _cachedTrainingData;
    private DateTime _cacheTimestamp = DateTime.MinValue;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

    public CategoryClassificationService(
        MLClassifier mlClassifier,
        TextPreprocessor textPreprocessor,
        SubstringMatcher substringMatcher,
        IntentDetector intentDetector,
        TrainingDataRepository trainingDataRepository,
        ILogger<CategoryClassificationService> logger)
    {
        _mlClassifier = mlClassifier;
        _textPreprocessor = textPreprocessor;
        _substringMatcher = substringMatcher;
        _intentDetector = intentDetector;
        _trainingDataRepository = trainingDataRepository;
        _logger = logger;
    }

    // Classifies user input and returns top 3 category predictions with full URL paths
    public async Task<List<CategoryPredictionResponse>> ClassifyAsync(string inputText, string languageCode)
    {
        if (string.IsNullOrWhiteSpace(inputText))
        {
            _logger.LogWarning("Empty input text provided for classification");
            throw new ClassificationException(
                "Input text cannot be empty", 
                ClassificationErrorType.InvalidInput);
        }

        try
        {
            // Detect user intent (publish vs browse)
            var intent = _intentDetector.DetectIntent(inputText, languageCode);
            _logger.LogDebug("Detected intent: {Intent} for input: {Input}", intent, inputText);

            // Get training data filtered by intent with graceful degradation
            var trainingData = await GetFilteredTrainingDataWithFallbackAsync(intent);
            if (trainingData.Count == 0)
            {
                _logger.LogWarning("No training data available for classification");
                throw new ClassificationException(
                    "No training data available for classification",
                    ClassificationErrorType.ModelNotReady);
            }

            // Perform substring matching (LIKE-style)
            var substringMatches = PerformSubstringMatchingSafe(inputText, trainingData, languageCode);

            // Get ML predictions
            var mlPredictions = GetMLPredictionsSafe(inputText, languageCode);

            // Combine results: 1 LIKE + 2 ML or 3 ML (Requirements 3.6)
            var results = CombineResults(substringMatches, mlPredictions, trainingData, languageCode, intent);

            _logger.LogInformation("Classification completed. Intent: {Intent}, Results: {Count}", intent, results.Count);
            return results;
        }
        catch (ClassificationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during classification for input: {InputLength} chars, language: {Language}", 
                inputText?.Length ?? 0, languageCode);
            throw new ClassificationException(
                "An unexpected error occurred during classification",
                ClassificationErrorType.InternalError,
                ex);
        }
    }
    
    // Performs substring matching with error handling
    private List<SubstringMatchResult> PerformSubstringMatchingSafe(
        string inputText, 
        List<TrainingDataDocument> trainingData, 
        string languageCode)
    {
        try
        {
            return _substringMatcher.FindMatches(inputText, trainingData, languageCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Substring matching failed for input, continuing with ML only");
            return new List<SubstringMatchResult>();
        }
    }
    
    // Gets ML predictions with error handling
    private List<PredictionResult> GetMLPredictionsSafe(string inputText, string languageCode)
    {
        try
        {
            return _mlClassifier.GetProbabilities(inputText, languageCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ML prediction failed for language: {Language}", languageCode);
            return new List<PredictionResult>();
        }
    }
    
    // Gets filtered training data with fallback to cached data on database failure
    private async Task<List<TrainingDataDocument>> GetFilteredTrainingDataWithFallbackAsync(UserIntent intent)
    {
        try
        {
            var data = await GetFilteredTrainingDataAsync(intent);
            
            // Update cache on successful fetch
            if (data.Count > 0)
            {
                _cachedTrainingData = data;
                _cacheTimestamp = DateTime.UtcNow;
            }
            
            return data;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database error fetching training data, attempting to use cached data");
            
            // Graceful degradation: use cached data if available and not too old
            if (_cachedTrainingData != null && DateTime.UtcNow - _cacheTimestamp < _cacheExpiry)
            {
                _logger.LogWarning("Using cached training data due to database failure. Cache age: {Age}",
                    DateTime.UtcNow - _cacheTimestamp);
                
                // Filter cached data based on intent
                if (intent == UserIntent.Publish)
                {
                    return _cachedTrainingData.Where(d => d.IsLeaf).ToList();
                }
                return _cachedTrainingData;
            }
            
            throw new ClassificationException(
                "Database connection failed and no cached data available",
                ClassificationErrorType.DatabaseError,
                ex);
        }
    }

    // Gets training data filtered by intent (leaf only for publish, all for browse)
    private async Task<List<TrainingDataDocument>> GetFilteredTrainingDataAsync(UserIntent intent)
    {
        if (intent == UserIntent.Publish)
        {
            // Publish intent: filter to leaf categories only (Requirement 4.5)
            return await _trainingDataRepository.GetLeafCategoriesAsync();
        }
        else
        {
            // Browse intent: use all categories (Requirement 5.2)
            return await _trainingDataRepository.GetAllAsync();
        }
    }

    // Combines substring matches and ML predictions into final results
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

        // Add substring match first if exists (1 LIKE match)
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

        // Determine how many ML predictions to add
        var mlCountNeeded = substringMatches.Count > 0 ? 2 : 3;

        // Add ML predictions (excluding duplicates)
        foreach (var prediction in mlPredictions.OrderByDescending(p => p.Score))
        {
            if (results.Count >= 3)
                break;

            if (usedCategoryIds.Contains(prediction.CategoryId))
                continue;

            var doc = trainingData.FirstOrDefault(d => d.CategoryId == prediction.CategoryId);
            if (doc != null)
            {
                results.Add(CreatePredictionResponse(doc, prediction.Score, "ML", languageCode, urlSuffix));
                usedCategoryIds.Add(prediction.CategoryId);
            }
        }

        return results;
    }

    // Creates a prediction response with full category slug path
    private CategoryPredictionResponse CreatePredictionResponse(
        TrainingDataDocument doc,
        float score,
        string matchType,
        string languageCode,
        string urlSuffix)
    {
        // Get language-specific name and URL slug
        var categoryName = languageCode == "ar" ? doc.NameArabic : doc.NameKurdish;
        var urlSlug = languageCode == "ar" ? doc.UrlSlugArabic : doc.UrlSlugKurdish;

        // Build full URL path with suffix (Requirements 4.6, 4.7, 4.8, 5.3, 5.4, 5.5)
        var fullUrl = BuildFullCategoryPath(urlSlug, urlSuffix);

        return new CategoryPredictionResponse
        {
            CategoryId = doc.CategoryId,
            CategoryName = categoryName,
            Url = fullUrl,
            Score = score,
            MatchType = matchType
        };
    }

    // Builds the full category slug path with the appropriate suffix
    private string BuildFullCategoryPath(string urlSlug, string urlSuffix)
    {
        if (string.IsNullOrWhiteSpace(urlSlug))
            return urlSuffix.TrimStart('/');

        // Ensure no double slashes and proper suffix
        var cleanSlug = urlSlug.TrimEnd('/');
        return $"{cleanSlug}{urlSuffix}";
    }

    // Retrains the ML model for a specific language
    public async Task RetrainModelAsync(string languageCode)
    {
        _logger.LogInformation("Starting model retraining for language: {LanguageCode}", languageCode);

        try
        {
            var trainingData = await _trainingDataRepository.GetAllAsync();
            if (trainingData.Count == 0)
            {
                _logger.LogWarning("No training data available for retraining language: {LanguageCode}", languageCode);
                return;
            }

            _mlClassifier.Train(trainingData, languageCode);
            _logger.LogInformation("Model retraining completed for language: {LanguageCode}", languageCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model retraining failed for language: {LanguageCode}", languageCode);
            throw;
        }
    }

    // Checks if the ML model is ready for predictions
    public bool IsModelReady(string languageCode)
    {
        return _mlClassifier.IsModelReady(languageCode);
    }
}

// Response model for category predictions
public class CategoryPredictionResponse
{
    public ushort CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public float Score { get; set; }
    public string MatchType { get; set; } = string.Empty; // "LIKE" or "ML"
}
