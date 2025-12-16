namespace MLCategoryClassifier;

public class BrandModel
{
    public ushort BrandModelId { get; set; }
    public string NameEnglish { get; set; } = string.Empty;
    public string NameArabic { get; set; } = string.Empty;
    public string NameKurdish { get; set; } = string.Empty;
    public bool IsBrand { get; set; }
    public ushort CategoryId { get; set; }
    public ushort? ParentId { get; set; }

    public Category Category { get; set; } = null!;
}
