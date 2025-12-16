namespace MLCategoryClassifier;

/// <summary>
/// Handles text preprocessing for Arabic and Kurdish languages.
/// Strips language-specific prefixes and manages stopwords for ML pipeline.
/// </summary>
public class TextPreprocessor
{
    private readonly Dictionary<string, string[]> _prefixes;
    private readonly Dictionary<string, string[]> _stopwords;

    public TextPreprocessor(IConfiguration configuration)
    {
        // Load prefixes from configuration
        _prefixes = new Dictionary<string, string[]>
        {
            ["ar"] = configuration.GetSection("Prefixes:Arabic").Get<string[]>() ?? Array.Empty<string>(),
            ["kr"] = configuration.GetSection("Prefixes:Kurdish").Get<string[]>() ?? Array.Empty<string>()
        };

        // Load stopwords from configuration
        _stopwords = new Dictionary<string, string[]>
        {
            ["ar"] = configuration.GetSection("Stopwords:Arabic").Get<string[]>() ?? Array.Empty<string>(),
            ["kr"] = configuration.GetSection("Stopwords:Kurdish").Get<string[]>() ?? Array.Empty<string>()
        };

        // Sort prefixes by length descending to match longest prefix first
        foreach (var key in _prefixes.Keys.ToList())
        {
            _prefixes[key] = _prefixes[key].OrderByDescending(p => p.Length).ToArray();
        }
    }

    /// <summary>
    /// Preprocesses text by stripping prefixes from each word.
    /// Returns both original and stripped versions of words.
    /// </summary>
    /// <param name="text">The input text to preprocess</param>
    /// <param name="languageCode">Language code: "ar" for Arabic, "kr" for Kurdish</param>
    /// <returns>Preprocessed text with both original and stripped word versions</returns>
    public string Preprocess(string text, string languageCode)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var words = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var processedWords = new List<string>();

        foreach (var word in words)
        {
            var strippedVersions = StripPrefixes(word, languageCode);
            processedWords.AddRange(strippedVersions);
        }

        return string.Join(" ", processedWords.Distinct());
    }

    /// <summary>
    /// Strips language-specific prefixes from a word.
    /// Returns both the original word and the stripped version(s).
    /// </summary>
    /// <param name="word">The word to process</param>
    /// <param name="languageCode">Language code: "ar" for Arabic, "kr" for Kurdish</param>
    /// <returns>Array containing original word and stripped version(s)</returns>
    public string[] StripPrefixes(string word, string languageCode)
    {
        if (string.IsNullOrWhiteSpace(word))
            return Array.Empty<string>();

        var results = new HashSet<string> { word };

        if (!_prefixes.TryGetValue(languageCode, out var prefixes))
            return results.ToArray();

        // Try to strip each prefix (sorted by length descending)
        foreach (var prefix in prefixes)
        {
            if (word.StartsWith(prefix, StringComparison.Ordinal) && word.Length > prefix.Length)
            {
                var stripped = word.Substring(prefix.Length);
                if (!string.IsNullOrWhiteSpace(stripped))
                {
                    results.Add(stripped);
                    // Only strip one prefix per word to avoid over-stripping
                    break;
                }
            }
        }

        return results.ToArray();
    }

    /// <summary>
    /// Gets the stopwords array for the specified language.
    /// Used by the ML pipeline for stopword removal.
    /// </summary>
    /// <param name="languageCode">Language code: "ar" for Arabic, "kr" for Kurdish</param>
    /// <returns>Array of stopwords for the specified language</returns>
    public string[] GetStopwords(string languageCode)
    {
        return _stopwords.TryGetValue(languageCode, out var stopwords)
            ? stopwords
            : Array.Empty<string>();
    }

    /// <summary>
    /// Gets the prefixes array for the specified language.
    /// </summary>
    /// <param name="languageCode">Language code: "ar" for Arabic, "kr" for Kurdish</param>
    /// <returns>Array of prefixes for the specified language</returns>
    public string[] GetPrefixes(string languageCode)
    {
        return _prefixes.TryGetValue(languageCode, out var prefixes)
            ? prefixes
            : Array.Empty<string>();
    }
}
