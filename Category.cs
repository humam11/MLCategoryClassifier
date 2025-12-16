namespace MLCategoryClassifier;

public class Category
{
    public ushort CategoryId { get; set; }
    public string NameArabic { get; set; } = string.Empty;
    public string NameKurdish { get; set; } = string.Empty;
    public string UrlSlugArabic { get; set; } = string.Empty;
    public string UrlSlugKurdish { get; set; } = string.Empty;
    public bool IsLeaf { get; set; }
    public string? HierarchyPath { get; set; }
    public ushort? ParentId { get; set; }

    public ICollection<BrandModel> BrandModels { get; set; } = new List<BrandModel>();
}
