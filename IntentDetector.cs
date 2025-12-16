namespace MLCategoryClassifier;

// Represents the detected user intent based on input text analysis
public enum UserIntent
{
    Browse,  // User wants to browse/search existing ads
    Publish  // User wants to publish/create a new ad
}

// Detects user intent (browse vs publish) by checking for language-specific keywords
public class IntentDetector
{
    private readonly Dictionary<string, string[]> _intentKeywords;
    private readonly ILogger<IntentDetector> _logger;

    public IntentDetector(IConfiguration configuration, ILogger<IntentDetector> logger)
    {
        _logger = logger;
        _intentKeywords = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Load Arabic intent keywords from configuration
            var arabicKeywords = configuration.GetSection("IntentKeywords:Arabic").Get<string[]>();
            _intentKeywords["ar"] = arabicKeywords ?? Array.Empty<string>();

            // Load Kurdish intent keywords from configuration
            var kurdishKeywords = configuration.GetSection("IntentKeywords:Kurdish").Get<string[]>();
            _intentKeywords["kr"] = kurdishKeywords ?? Array.Empty<string>();
            
            _logger.LogInformation("IntentDetector initialized with {ArabicCount} Arabic and {KurdishCount} Kurdish keywords",
                _intentKeywords["ar"].Length, _intentKeywords["kr"].Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading intent keywords from configuration, using empty defaults");
            _intentKeywords["ar"] = Array.Empty<string>();
            _intentKeywords["kr"] = Array.Empty<string>();
        }
    }

    // Detects intent by checking if input contains any intent keyword
    public UserIntent DetectIntent(string inputText, string languageCode)
    {
        if (string.IsNullOrWhiteSpace(inputText))
            return UserIntent.Browse;

        try
        {
            if (!_intentKeywords.TryGetValue(languageCode, out var keywords) || keywords.Length == 0)
            {
                _logger.LogDebug("No intent keywords configured for language {Language}, defaulting to Browse", languageCode);
                return UserIntent.Browse;
            }

            // Iterate through keywords to check for matches
            foreach (var keyword in keywords)
            {
                if (inputText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Detected publish intent via keyword '{Keyword}' for language {Language}", 
                        keyword, languageCode);
                    return UserIntent.Publish;
                }
            }

            return UserIntent.Browse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting intent for language {Language}, defaulting to Browse", languageCode);
            return UserIntent.Browse;
        }
    }
    
    // Gets the intent keywords for a specific language (for testing/debugging)
    public string[] GetKeywords(string languageCode)
    {
        return _intentKeywords.TryGetValue(languageCode, out var keywords) 
            ? keywords 
            : Array.Empty<string>();
    }
}
