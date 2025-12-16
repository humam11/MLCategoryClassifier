using Microsoft.EntityFrameworkCore;

namespace MLCategoryClassifier;

public class PostgresDbContext : DbContext
{
    public PostgresDbContext(DbContextOptions<PostgresDbContext> options) : base(options) { }

    public DbSet<Category> Categories { get; set; }
    public DbSet<BrandModel> BrandModels { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configure Category entity
        modelBuilder.Entity<Category>(entity =>
        {
            entity.ToTable("categories");
            entity.HasKey(e => e.CategoryId);
            entity.Property(e => e.CategoryId).HasColumnName("category_id");
            entity.Property(e => e.NameArabic).HasColumnName("name_arabic");
            entity.Property(e => e.NameKurdish).HasColumnName("name_kurdish");
            entity.Property(e => e.UrlSlugArabic).HasColumnName("url_slug_arabic");
            entity.Property(e => e.UrlSlugKurdish).HasColumnName("url_slug_kurdish");
            entity.Property(e => e.HierarchyPath).HasColumnName("hierarchy_path").HasColumnType("ltree");
            entity.Property(e => e.IsLeaf).HasColumnName("is_leaf");
            entity.Property(e => e.ParentId).HasColumnName("parent_id");
        });

        // Configure BrandModel entity
        modelBuilder.Entity<BrandModel>(entity =>
        {
            entity.ToTable("brands_models");
            entity.HasKey(e => e.BrandModelId);
            entity.Property(e => e.BrandModelId).HasColumnName("brand_model_id");
            entity.Property(e => e.NameEnglish).HasColumnName("name_english");
            entity.Property(e => e.NameArabic).HasColumnName("name_arabic");
            entity.Property(e => e.NameKurdish).HasColumnName("name_kurdish");
            entity.Property(e => e.IsBrand).HasColumnName("is_brand");
            entity.Property(e => e.ParentId).HasColumnName("parent_id");
            entity.Property(e => e.CategoryId).HasColumnName("category_id");
            entity.HasOne(e => e.Category).WithMany(e => e.BrandModels).HasForeignKey(e => e.CategoryId);
        });
    }
}
