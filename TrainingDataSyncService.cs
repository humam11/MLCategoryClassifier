using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MLCategoryClassifier;

// Custom exception for sync service errors
public class SyncException : Exception
{
    public SyncException(string message) : base(message) { }
    public SyncException(string message, Exception innerException) : base(message, innerException) { }
}

// Service for synchronizing training data from PostgreSQL to MongoDB on startup
public class TrainingDataSyncService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TrainingDataSyncService> _logger;

    public TrainingDataSyncService(
        IServiceScopeFactory scopeFactory,
        ILogger<TrainingDataSyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // Synchronizes all categories from PostgreSQL to MongoDB
    public async Task SyncAllCategoriesAsync()
    {
        _logger.LogInformation("Starting full category synchronization from PostgreSQL to MongoDB");

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var postgresDb = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
            var trainingDataRepo = scope.ServiceProvider.GetRequiredService<TrainingDataRepository>();

            List<Category> categories;
            try
            {
                categories = await postgresDb.Categories
                    .AsNoTracking()
                    .ToListAsync();
            }
            catch (NpgsqlException ex)
            {
                _logger.LogError(ex, "PostgreSQL connection failed during category sync");
                throw new SyncException("Failed to connect to PostgreSQL for category sync", ex);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Database context error during category sync");
                throw new SyncException("Database context error during category sync", ex);
            }

            _logger.LogInformation("Found {Count} categories to synchronize", categories.Count);

            var syncedCount = 0;
            var errorCount = 0;

            foreach (var category in categories)
            {
                try
                {
                    await SyncCategoryAsync(category, postgresDb, trainingDataRepo);
                    syncedCount++;
                }
                catch (RepositoryException ex)
                {
                    errorCount++;
                    _logger.LogError(ex, "MongoDB error syncing category {CategoryId}: {CategoryName}", 
                        category.CategoryId, category.NameArabic);
                }
                catch (Exception ex)
                {
                    errorCount++;
                    _logger.LogError(ex, "Failed to sync category {CategoryId}: {CategoryName}", 
                        category.CategoryId, category.NameArabic);
                }
            }

            _logger.LogInformation(
                "Category synchronization completed. Synced: {SyncedCount}, Errors: {ErrorCount}", 
                syncedCount, errorCount);
            
            if (errorCount > 0 && syncedCount == 0)
            {
                throw new SyncException($"Category sync failed completely. {errorCount} errors occurred.");
            }
        }
        catch (SyncException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during category synchronization");
            throw new SyncException("Unexpected error during category synchronization", ex);
        }
    }

    // Synchronizes a single category from PostgreSQL to MongoDB
    private async Task SyncCategoryAsync(
        Category category, 
        PostgresDbContext postgresDb, 
        TrainingDataRepository trainingDataRepo)
    {
        // Check if document already exists to preserve training examples
        var existingDoc = await trainingDataRepo.GetByCategoryIdAsync(category.CategoryId);
        
        var document = new TrainingDataDocument
        {
            Id = category.CategoryId.ToString(),
            CategoryId = category.CategoryId,
            NameArabic = category.NameArabic,
            NameKurdish = category.NameKurdish,
            UrlSlugArabic = category.UrlSlugArabic,
            UrlSlugKurdish = category.UrlSlugKurdish,
            IsLeaf = category.IsLeaf,
            HasModels = false,
            BrandsModels = new List<BrandModelInfo>(),
            // Preserve existing training examples if they exist
            ArabicTrainingExamples = existingDoc?.ArabicTrainingExamples ?? new List<string>(),
            KurdishTrainingExamples = existingDoc?.KurdishTrainingExamples ?? new List<string>()
        };

        // Only query brands_models for leaf categories
        if (category.IsLeaf)
        {
            var brandModels = await postgresDb.BrandModels
                .AsNoTracking()
                .Where(bm => bm.CategoryId == category.CategoryId)
                .ToListAsync();

            if (brandModels.Count > 0)
            {
                document.HasModels = true;
                document.BrandsModels = brandModels.Select(bm => new BrandModelInfo
                {
                    BrandModelId = bm.BrandModelId,
                    NameEnglish = bm.NameEnglish,
                    NameArabic = bm.NameArabic,
                    NameKurdish = bm.NameKurdish
                }).ToList();

                _logger.LogDebug(
                    "Category {CategoryId} has {ModelCount} brand/models", 
                    category.CategoryId, brandModels.Count);
            }
        }

        // Upsert to MongoDB (idempotent)
        await trainingDataRepo.UpsertAsync(document);
    }
}
