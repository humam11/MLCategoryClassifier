namespace MLCategoryClassifier;

// API response model for category classification results
public class ClassificationResponse
{
    public List<CategoryPrediction> Predictions { get; set; } = new List<CategoryPrediction>();
}
