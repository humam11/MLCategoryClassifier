using Microsoft.AspNetCore.Mvc;

namespace MLCategoryClassifier;

// API error response model
public class ApiErrorResponse
{
    public string Error { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? Code { get; set; }
}

// API controller for ML-based category classification
[ApiController]
[Route("api/ml")]
public class ClassificationController : ControllerBase
{
    private readonly CategoryClassificationService _classificationService;
    private readonly ILogger<ClassificationController> _logger;

    // Valid language codes for classification
    private static readonly HashSet<string> ValidLanguageCodes = new(StringComparer.OrdinalIgnoreCase) { "ar", "kr" };
    
    // Maximum allowed input text length
    private const int MaxInputLength = 500;

    public ClassificationController(
        CategoryClassificationService classificationService,
        ILogger<ClassificationController> logger)
    {
        _classificationService = classificationService;
        _logger = logger;
    }

    // POST /api/ml/classify/{langCode} - Classifies user input and returns top 3 category predictions
    [HttpPost("classify/{langCode}")]
    public async Task<ActionResult<ClassificationResponse>> ClassifyCategory(
        [FromRoute] string langCode,
        [FromBody] ClassificationRequest request)
    {
        // Validate language code (Requirements 9.2, 9.3)
        if (string.IsNullOrWhiteSpace(langCode) || !ValidLanguageCodes.Contains(langCode))
        {
            _logger.LogWarning("Invalid language code: {LangCode}", langCode);
            return BadRequest(new ApiErrorResponse 
            { 
                Error = "Invalid language code",
                Details = "Supported values: ar, kr",
                Code = "INVALID_LANGUAGE"
            });
        }

        // Validate request body (Requirement 9.5)
        if (request == null)
        {
            _logger.LogWarning("Null classification request received");
            return BadRequest(new ApiErrorResponse 
            { 
                Error = "Request body is required",
                Code = "MISSING_BODY"
            });
        }
        
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            _logger.LogWarning("Empty text in classification request");
            return BadRequest(new ApiErrorResponse 
            { 
                Error = "Request body must contain non-empty 'text' field",
                Code = "EMPTY_TEXT"
            });
        }
        
        // Validate input length for security
        if (request.Text.Length > MaxInputLength)
        {
            _logger.LogWarning("Input text exceeds maximum length: {Length} > {MaxLength}", 
                request.Text.Length, MaxInputLength);
            return BadRequest(new ApiErrorResponse 
            { 
                Error = $"Input text exceeds maximum length of {MaxInputLength} characters",
                Code = "TEXT_TOO_LONG"
            });
        }

        // Check if model is ready (Requirement 9.7)
        if (!_classificationService.IsModelReady(langCode.ToLowerInvariant()))
        {
            _logger.LogWarning("ML model not ready for language: {LangCode}", langCode);
            return StatusCode(503, new ApiErrorResponse 
            { 
                Error = "Classification service is not ready",
                Details = "The ML model is still being trained. Please try again later.",
                Code = "MODEL_NOT_READY"
            });
        }

        try
        {
            // Call classification service (Requirements 9.1, 9.4, 9.6)
            var predictions = await _classificationService.ClassifyAsync(request.Text, langCode.ToLowerInvariant());

            // Map internal response to API response model
            var response = new ClassificationResponse
            {
                Predictions = predictions.Select(p => new CategoryPrediction
                {
                    CategoryId = p.CategoryId,
                    CategoryName = p.CategoryName,
                    Url = p.Url,
                    Score = p.Score,
                    MatchType = p.MatchType
                }).Take(3).ToList()
            };

            _logger.LogInformation("Classification successful for language {LangCode}, returned {Count} predictions", 
                langCode, response.Predictions.Count);

            return Ok(response);
        }
        catch (ClassificationException ex)
        {
            _logger.LogError(ex, "Classification error for language {LangCode}: {ErrorType}", 
                langCode, ex.ErrorType);
            
            return ex.ErrorType switch
            {
                ClassificationErrorType.InvalidInput => BadRequest(new ApiErrorResponse 
                { 
                    Error = ex.Message,
                    Code = "INVALID_INPUT"
                }),
                ClassificationErrorType.ModelNotReady => StatusCode(503, new ApiErrorResponse 
                { 
                    Error = "Classification service is not ready",
                    Details = ex.Message,
                    Code = "MODEL_NOT_READY"
                }),
                ClassificationErrorType.DatabaseError => StatusCode(503, new ApiErrorResponse 
                { 
                    Error = "Service temporarily unavailable",
                    Details = "Database connection issue. Please try again later.",
                    Code = "DATABASE_ERROR"
                }),
                ClassificationErrorType.PredictionError => StatusCode(500, new ApiErrorResponse 
                { 
                    Error = "Prediction failed",
                    Details = "An error occurred during ML prediction.",
                    Code = "PREDICTION_ERROR"
                }),
                _ => StatusCode(500, new ApiErrorResponse 
                { 
                    Error = "An unexpected error occurred",
                    Code = "INTERNAL_ERROR"
                })
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during classification for language {LangCode}", langCode);
            return StatusCode(500, new ApiErrorResponse 
            { 
                Error = "An unexpected error occurred during classification",
                Code = "INTERNAL_ERROR"
            });
        }
    }
    
    // GET /api/ml/health - Health check endpoint
    [HttpGet("health")]
    public ActionResult<object> HealthCheck()
    {
        var arabicReady = _classificationService.IsModelReady("ar");
        var kurdishReady = _classificationService.IsModelReady("kr");
        
        var status = new
        {
            Status = arabicReady && kurdishReady ? "healthy" : "degraded",
            Models = new
            {
                Arabic = arabicReady ? "ready" : "not_ready",
                Kurdish = kurdishReady ? "ready" : "not_ready"
            },
            Timestamp = DateTime.UtcNow
        };
        
        if (!arabicReady && !kurdishReady)
        {
            return StatusCode(503, status);
        }
        
        return Ok(status);
    }
}
