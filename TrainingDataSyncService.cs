using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MLCategoryClassifier;

// Custom exception for sync service errors
public class SyncException : Exception
{
    public SyncException(string message) : base(message) { }
    public SyncException(string message, Exception innerException) : base(message, innerException) { }
}

// Service for synchronizing training data from PostgreSQL to MongoDB
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

        // Only query brands_models for leaf categories (Requirements 1.3, 1.4, 1.5)
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

        // Upsert to MongoDB (idempotent - Requirements 1.6)
        await trainingDataRepo.UpsertAsync(document);
    }

    // Handles a category notification from PostgreSQL (INSERT/UPDATE/DELETE)
    public async Task HandleCategoryNotificationAsync(CategoryNotification notification)
    {
        if (notification == null)
        {
            _logger.LogWarning("Received null category notification");
            return;
        }
        
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var postgresDb = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
            var trainingDataRepo = scope.ServiceProvider.GetRequiredService<TrainingDataRepository>();

            switch (notification.Operation.ToUpperInvariant())
            {
                case "INSERT":
                case "UPDATE":
                    try
                    {
                        var category = await postgresDb.Categories
                            .AsNoTracking()
                            .FirstOrDefaultAsync(c => c.CategoryId == notification.CategoryId);
                        
                        if (category != null)
                        {
                            await SyncCategoryAsync(category, postgresDb, trainingDataRepo);
                            _logger.LogInformation(
                                "Processed {Operation} notification for category {CategoryId}", 
                                notification.Operation, notification.CategoryId);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Category {CategoryId} not found in PostgreSQL for {Operation} notification",
                                notification.CategoryId, notification.Operation);
                        }
                    }
                    catch (NpgsqlException ex)
                    {
                        _logger.LogError(ex, "PostgreSQL error processing {Operation} notification for category {CategoryId}",
                            notification.Operation, notification.CategoryId);
                        throw;
                    }
                    break;

                case "DELETE":
                    try
                    {
                        await trainingDataRepo.DeleteAsync(notification.CategoryId);
                        _logger.LogInformation(
                            "Deleted category {CategoryId} from MongoDB", 
                            notification.CategoryId);
                    }
                    catch (RepositoryException ex)
                    {
                        _logger.LogError(ex, "MongoDB error deleting category {CategoryId}", notification.CategoryId);
                        throw;
                    }
                    break;

                default:
                    _logger.LogWarning(
                        "Unknown operation {Operation} for category notification", 
                        notification.Operation);
                    break;
            }
        }
        catch (Exception ex) when (ex is not NpgsqlException && ex is not RepositoryException)
        {
            _logger.LogError(ex, "Unexpected error handling category notification: Operation={Operation}, CategoryId={CategoryId}",
                notification.Operation, notification.CategoryId);
        }
    }

    // Handles a brand/model notification from PostgreSQL (INSERT/UPDATE/DELETE)
    public async Task HandleBrandModelNotificationAsync(BrandModelNotification notification)
    {
        if (notification == null)
        {
            _logger.LogWarning("Received null brand/model notification");
            return;
        }
        
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var trainingDataRepo = scope.ServiceProvider.GetRequiredService<TrainingDataRepository>();

            switch (notification.Operation.ToUpperInvariant())
            {
                case "INSERT":
                    var newModel = new BrandModelInfo
                    {
                        BrandModelId = notification.BrandModelId,
                        NameEnglish = notification.NameEnglish ?? string.Empty,
                        NameArabic = notification.NameArabic ?? string.Empty,
                        NameKurdish = notification.NameKurdish ?? string.Empty
                    };
                    await trainingDataRepo.AddBrandModelAsync(notification.CategoryId, newModel);
                    _logger.LogInformation(
                        "Added brand/model {BrandModelId} to category {CategoryId}", 
                        notification.BrandModelId, notification.CategoryId);
                    break;

                case "UPDATE":
                    var updatedModel = new BrandModelInfo
                    {
                        BrandModelId = notification.BrandModelId,
                        NameEnglish = notification.NameEnglish ?? string.Empty,
                        NameArabic = notification.NameArabic ?? string.Empty,
                        NameKurdish = notification.NameKurdish ?? string.Empty
                    };
                    await trainingDataRepo.UpdateBrandModelAsync(
                        notification.CategoryId, 
                        notification.BrandModelId, 
                        updatedModel);
                    _logger.LogInformation(
                        "Updated brand/model {BrandModelId} in category {CategoryId}", 
                        notification.BrandModelId, notification.CategoryId);
                    break;

                case "DELETE":
                    await trainingDataRepo.RemoveBrandModelAsync(
                        notification.CategoryId, 
                        notification.BrandModelId);
                    _logger.LogInformation(
                        "Removed brand/model {BrandModelId} from category {CategoryId}", 
                        notification.BrandModelId, notification.CategoryId);
                    break;

                default:
                    _logger.LogWarning(
                        "Unknown operation {Operation} for brand/model notification", 
                        notification.Operation);
                    break;
            }
        }
        catch (RepositoryException ex)
        {
            _logger.LogError(ex, "MongoDB error handling brand/model notification: Operation={Operation}, BrandModelId={BrandModelId}, CategoryId={CategoryId}",
                notification.Operation, notification.BrandModelId, notification.CategoryId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error handling brand/model notification: Operation={Operation}, BrandModelId={BrandModelId}, CategoryId={CategoryId}",
                notification.Operation, notification.BrandModelId, notification.CategoryId);
        }
    }
}

// Category notification payload from PostgreSQL
public class CategoryNotification
{
    public string Type { get; set; } = string.Empty;
    public string Op { get; set; } = string.Empty;
    public ushort CategoryId { get; set; }
    public string? NameArabic { get; set; }
    public string? NameKurdish { get; set; }
    
    // Alias for backward compatibility
    public string Operation => Op;
}

// Brand/model notification payload from PostgreSQL
public class BrandModelNotification
{
    public string Type { get; set; } = string.Empty;
    public string Op { get; set; } = string.Empty;
    public ushort BrandModelId { get; set; }
    public ushort CategoryId { get; set; }
    public string? NameEnglish { get; set; }
    public string? NameArabic { get; set; }
    public string? NameKurdish { get; set; }
    
    // Alias for backward compatibility
    public string Operation => Op;
}
