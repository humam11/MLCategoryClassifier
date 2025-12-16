using Microsoft.ML;
using Microsoft.ML.Data;

namespace MLCategoryClassifier;

// ML.NET classifier for category prediction using logistic regression (SdcaMaximumEntropy)
public class MLClassifier
{
    private readonly MLContext _mlContext;
    private readonly TextPreprocessor _textPreprocessor;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MLClassifier> _logger;
    private readonly Dictionary<string, PredictionEngine<TrainingInput, CategoryPredictionOutput>> _predictionEngines;
    private readonly Dictionary<string, ITransformer> _trainedModels;
    private readonly Dictionary<string, string[]> _categoryLabels;
    private readonly object _lockObject = new();

    public MLClassifier(
        TextPreprocessor textPreprocessor,
        IConfiguration configuration,
        ILogger<MLClassifier> logger)
    {
        _mlContext = new MLContext(seed: 42);
        _textPreprocessor = textPreprocessor;
        _configuration = configuration;
        _logger = logger;
        _predictionEngines = new Dictionary<string, PredictionEngine<TrainingInput, CategoryPredictionOutput>>();
        _trainedModels = new Dictionary<string, ITransformer>();
        _categoryLabels = new Dictionary<string, string[]>();
    }

    // Trains the ML model for a specific language using training data documents
    public void Train(List<TrainingDataDocument> trainingDocuments, string languageCode)
    {
        if (trainingDocuments == null || trainingDocuments.Count == 0)
        {
            _logger.LogWarning("No training documents provided for language {LanguageCode}", languageCode);
            return;
        }

        _logger.LogInformation("Starting training for language {LanguageCode} with {Count} categories",
            languageCode, trainingDocuments.Count);

        var trainingData = BuildTrainingData(trainingDocuments, languageCode);

        if (trainingData.Count == 0)
        {
            _logger.LogWarning("No training examples generated for language {LanguageCode}", languageCode);
            return;
        }

        var dataView = _mlContext.Data.LoadFromEnumerable(trainingData);

        // Get stopwords for the language
        var stopwords = _textPreprocessor.GetStopwords(languageCode);

        // Get ML configuration
        var maxIterations = _configuration.GetValue<int>("ML:MaxIterations", 100);
        var ngramLength = _configuration.GetValue<int>("ML:NGramLength", 2);
        var useAllLengths = _configuration.GetValue<bool>("ML:UseAllNGramLengths", true);

        // Build ML.NET pipeline
        var pipeline = _mlContext.Transforms.Conversion.MapValueToKey(
                outputColumnName: "Label",
                inputColumnName: nameof(TrainingInput.Category))
            .Append(_mlContext.Transforms.Text.NormalizeText(
                outputColumnName: "NormalizedText",
                inputColumnName: nameof(TrainingInput.Text)))
            .Append(_mlContext.Transforms.Text.TokenizeIntoWords(
                outputColumnName: "Tokens",
                inputColumnName: "NormalizedText"))
            .Append(_mlContext.Transforms.Text.RemoveStopWords(
                outputColumnName: "TokensClean",
                inputColumnName: "Tokens",
                stopwords: stopwords.Length > 0 ? stopwords : null))
            .Append(_mlContext.Transforms.Conversion.MapValueToKey(
                outputColumnName: "TokensKey",
                inputColumnName: "TokensClean"))
            .Append(_mlContext.Transforms.Text.ProduceNgrams(
                outputColumnName: "Features",
                inputColumnName: "TokensKey",
                ngramLength: ngramLength,
                useAllLengths: useAllLengths))
            .Append(_mlContext.MulticlassClassification.Trainers.SdcaMaximumEntropy(
                labelColumnName: "Label",
                featureColumnName: "Features",
                maximumNumberOfIterations: maxIterations))
            .Append(_mlContext.Transforms.Conversion.MapKeyToValue(
                outputColumnName: "PredictedLabel",
                inputColumnName: "PredictedLabel"));

        try
        {
            var model = pipeline.Fit(dataView);

            lock (_lockObject)
            {
                _trainedModels[languageCode] = model;
                _predictionEngines[languageCode] = _mlContext.Model.CreatePredictionEngine<TrainingInput, CategoryPredictionOutput>(model);
                _categoryLabels[languageCode] = trainingDocuments.Select(d => d.CategoryId.ToString()).Distinct().ToArray();
            }

            _logger.LogInformation("Training completed for language {LanguageCode}. Total examples: {Count}",
                languageCode, trainingData.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Training failed for language {LanguageCode}", languageCode);
            throw;
        }
    }

    // Builds training data by combining training examples with brand/model names
    private List<TrainingInput> BuildTrainingData(List<TrainingDataDocument> documents, string languageCode)
    {
        var trainingData = new List<TrainingInput>();

        foreach (var doc in documents)
        {
            var categoryLabel = doc.CategoryId.ToString();
            var examples = new List<string>();

            // Add language-specific training examples
            if (languageCode == "ar")
            {
                examples.AddRange(doc.ArabicTrainingExamples ?? new List<string>());
            }
            else if (languageCode == "kr")
            {
                examples.AddRange(doc.KurdishTrainingExamples ?? new List<string>());
            }

            // Add brand/model names if hasModels is true (Requirements 6.1, 6.2, 6.3)
            if (doc.HasModels && doc.BrandsModels != null && doc.BrandsModels.Count > 0)
            {
                foreach (var brandModel in doc.BrandsModels)
                {
                    // Always add English name
                    if (!string.IsNullOrWhiteSpace(brandModel.NameEnglish))
                    {
                        examples.Add(brandModel.NameEnglish);
                    }

                    // Add language-specific name
                    if (languageCode == "ar" && !string.IsNullOrWhiteSpace(brandModel.NameArabic))
                    {
                        examples.Add(brandModel.NameArabic);
                    }
                    else if (languageCode == "kr" && !string.IsNullOrWhiteSpace(brandModel.NameKurdish))
                    {
                        examples.Add(brandModel.NameKurdish);
                    }
                }
            }
            // If hasModels is false, only training examples are used (Requirement 6.4)

            // Always add category name as a training example (fallback when no other examples exist)
            var categoryName = languageCode == "ar" ? doc.NameArabic : doc.NameKurdish;
            if (!string.IsNullOrWhiteSpace(categoryName))
            {
                examples.Add(categoryName);
            }

            // Preprocess and add each example
            foreach (var example in examples.Where(e => !string.IsNullOrWhiteSpace(e)))
            {
                var preprocessedText = _textPreprocessor.Preprocess(example, languageCode);
                if (!string.IsNullOrWhiteSpace(preprocessedText))
                {
                    trainingData.Add(new TrainingInput
                    {
                        Text = preprocessedText,
                        Category = categoryLabel
                    });
                }
            }
        }

        return trainingData;
    }

    // Predicts the category for the given input text
    public PredictionResult? Predict(string inputText, string languageCode)
    {
        if (string.IsNullOrWhiteSpace(inputText))
            return null;

        PredictionEngine<TrainingInput, CategoryPredictionOutput>? engine;
        lock (_lockObject)
        {
            if (!_predictionEngines.TryGetValue(languageCode, out engine))
            {
                _logger.LogWarning("No prediction engine available for language {LanguageCode}", languageCode);
                return null;
            }
        }

        var preprocessedText = _textPreprocessor.Preprocess(inputText, languageCode);
        var input = new TrainingInput { Text = preprocessedText };

        try
        {
            var prediction = engine.Predict(input);

            if (string.IsNullOrEmpty(prediction.PredictedLabel))
                return null;

            var score = prediction.Score?.Max() ?? 0f;

            return new PredictionResult
            {
                CategoryId = ushort.Parse(prediction.PredictedLabel),
                Score = score,
                PredictedLabel = prediction.PredictedLabel
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prediction failed for input: {Input}", inputText);
            return null;
        }
    }

    // Gets probability scores for all categories using softmax function
    public List<PredictionResult> GetProbabilities(string inputText, string languageCode)
    {
        var results = new List<PredictionResult>();

        if (string.IsNullOrWhiteSpace(inputText))
            return results;

        PredictionEngine<TrainingInput, CategoryPredictionOutput>? engine;
        string[]? labels;

        lock (_lockObject)
        {
            if (!_predictionEngines.TryGetValue(languageCode, out engine))
            {
                _logger.LogWarning("No prediction engine available for language {LanguageCode}", languageCode);
                return results;
            }

            if (!_categoryLabels.TryGetValue(languageCode, out labels) || labels == null)
            {
                return results;
            }
        }

        var preprocessedText = _textPreprocessor.Preprocess(inputText, languageCode);
        var input = new TrainingInput { Text = preprocessedText };

        try
        {
            var prediction = engine.Predict(input);

            if (prediction.Score == null || prediction.Score.Length == 0)
                return results;

            // Apply softmax to convert raw scores to probabilities
            var probabilities = ApplySoftmax(prediction.Score);

            // Map scores to category IDs
            for (int i = 0; i < probabilities.Length && i < labels.Length; i++)
            {
                if (ushort.TryParse(labels[i], out var categoryId))
                {
                    results.Add(new PredictionResult
                    {
                        CategoryId = categoryId,
                        Score = probabilities[i],
                        PredictedLabel = labels[i]
                    });
                }
            }

            return results.OrderByDescending(r => r.Score).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetProbabilities failed for input: {Input}", inputText);
            return results;
        }
    }

    // Applies softmax function to convert raw scores to probabilities
    public static float[] ApplySoftmax(float[] scores)
    {
        if (scores == null || scores.Length == 0)
            return Array.Empty<float>();

        var maxScore = scores.Max();
        var expScores = scores.Select(s => (float)Math.Exp(s - maxScore)).ToArray();
        var sumExp = expScores.Sum();

        if (sumExp == 0)
            return scores.Select(_ => 1f / scores.Length).ToArray();

        return expScores.Select(e => e / sumExp).ToArray();
    }

    // Checks if the model is ready for predictions
    public bool IsModelReady(string languageCode)
    {
        lock (_lockObject)
        {
            return _predictionEngines.ContainsKey(languageCode);
        }
    }

    // Gets the list of supported language codes
    public IEnumerable<string> GetSupportedLanguages()
    {
        lock (_lockObject)
        {
            return _predictionEngines.Keys.ToList();
        }
    }
}

// Input class for ML.NET training and prediction
public class TrainingInput
{
    public string Text { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
}

// Output class for ML.NET predictions
public class CategoryPredictionOutput
{
    [ColumnName("PredictedLabel")]
    public string PredictedLabel { get; set; } = string.Empty;

    [ColumnName("Score")]
    public float[]? Score { get; set; }
}

// Result of a category prediction
public class PredictionResult
{
    public ushort CategoryId { get; set; }
    public float Score { get; set; }
    public string PredictedLabel { get; set; } = string.Empty;
}
