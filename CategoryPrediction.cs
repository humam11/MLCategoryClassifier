namespace MLCategoryClassifier;

// API response model for a single category prediction
public class CategoryPrediction
{
    public ushort CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public float Score { get; set; }
    public string MatchType { get; set; } = string.Empty; // "LIKE" or "ML"
}
