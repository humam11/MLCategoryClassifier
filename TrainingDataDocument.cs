namespace MLCategoryClassifier;

public class TrainingDataDocument
{
    public string Id { get; set; } = string.Empty;
    public ushort CategoryId { get; set; }
    public string NameArabic { get; set; } = string.Empty;
    public string NameKurdish { get; set; } = string.Empty;
    public string UrlSlugArabic { get; set; } = string.Empty;
    public string UrlSlugKurdish { get; set; } = string.Empty;
    public bool IsLeaf { get; set; }
    public bool HasModels { get; set; }
    public List<BrandModelInfo> BrandsModels { get; set; } = new();
    public List<string> ArabicTrainingExamples { get; set; } = new();
    public List<string> KurdishTrainingExamples { get; set; } = new();
}
