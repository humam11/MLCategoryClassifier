namespace MLCategoryClassifier;

// Performs SQL-LIKE pattern matching to find categories containing user input as a substring.
public class SubstringMatcher
{
    private readonly TextPreprocessor _textPreprocessor;
    private readonly ILogger<SubstringMatcher> _logger;

    // Fixed score assigned to substring matches (25%).
    public const float FixedMatchScore = 0.25f;

    public SubstringMatcher(TextPreprocessor textPreprocessor, ILogger<SubstringMatcher> logger)
    {
        _textPreprocessor = textPreprocessor;
        _logger = logger;
    }

    // Finds categories where the category name contains the user input as a substring.
    public List<SubstringMatchResult> FindMatches(
        string input, 
        List<TrainingDataDocument> categories, 
        string languageCode)
    {
        var results = new List<SubstringMatchResult>();

        if (string.IsNullOrWhiteSpace(input) || categories == null || categories.Count == 0)
            return results;

        try
        {
            // Normalize input: lowercase and strip prefixes
            var normalizedInput = input.Trim().ToLowerInvariant();
            var strippedInputs = _textPreprocessor.StripPrefixes(normalizedInput, languageCode)
                .Select(s => s.ToLowerInvariant())
                .Distinct()
                .ToList();

            foreach (var category in categories)
            {
                var categoryName = languageCode == "ar" 
                    ? category.NameArabic 
                    : category.NameKurdish;

                if (string.IsNullOrWhiteSpace(categoryName))
                    continue;

                var normalizedCategoryName = categoryName.ToLowerInvariant();

                // Check if category name contains any of the input variations
                foreach (var inputVariation in strippedInputs)
                {
                    if (normalizedCategoryName.Contains(inputVariation, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new SubstringMatchResult
                        {
                            CategoryId = category.CategoryId,
                            CategoryName = categoryName,
                            UrlSlug = languageCode == "ar" 
                                ? category.UrlSlugArabic 
                                : category.UrlSlugKurdish,
                            Score = FixedMatchScore,
                            IsLeaf = category.IsLeaf
                        });
                        break;
                    }
                }

                // Return only the first match as per requirements
                if (results.Count > 0)
                    break;
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during substring matching for input length {InputLength}, language {Language}",
                input?.Length ?? 0, languageCode);
            return results;
        }
    }
}

// Represents a substring match result with category information and score.
public class SubstringMatchResult
{
    public ushort CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string UrlSlug { get; set; } = string.Empty;
    public float Score { get; set; }
    public bool IsLeaf { get; set; }
}
