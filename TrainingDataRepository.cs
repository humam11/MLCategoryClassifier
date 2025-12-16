using MongoDB.Driver;

namespace MLCategoryClassifier;

// Custom exception for repository errors
public class RepositoryException : Exception
{
    public RepositoryException(string message) : base(message) { }
    public RepositoryException(string message, Exception innerException) : base(message, innerException) { }
}

// Repository for MongoDB operations on TrainingDataCollection
public class TrainingDataRepository
{
    private readonly IMongoCollection<TrainingDataDocument> _collection;
    private readonly ILogger<TrainingDataRepository> _logger;

    public TrainingDataRepository(IMongoDatabase database, ILogger<TrainingDataRepository> logger)
    {
        _collection = database.GetCollection<TrainingDataDocument>("trainingdata");
        _logger = logger;
    }

    // Gets a training data document by category ID
    public async Task<TrainingDataDocument?> GetByCategoryIdAsync(ushort categoryId)
    {
        try
        {
            var filter = Builders<TrainingDataDocument>.Filter.Eq(d => d.CategoryId, categoryId);
            return await _collection.Find(filter).FirstOrDefaultAsync();
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex, "MongoDB error fetching category {CategoryId}", categoryId);
            throw new RepositoryException($"Failed to fetch category {categoryId}", ex);
        }
    }

    // Gets all training data documents
    public async Task<List<TrainingDataDocument>> GetAllAsync()
    {
        try
        {
            return await _collection.Find(Builders<TrainingDataDocument>.Filter.Empty).ToListAsync();
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex, "MongoDB error fetching all training data");
            throw new RepositoryException("Failed to fetch all training data", ex);
        }
    }

    // Gets all training data documents where IsLeaf is true
    public async Task<List<TrainingDataDocument>> GetLeafCategoriesAsync()
    {
        try
        {
            var filter = Builders<TrainingDataDocument>.Filter.Eq(d => d.IsLeaf, true);
            return await _collection.Find(filter).ToListAsync();
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex, "MongoDB error fetching leaf categories");
            throw new RepositoryException("Failed to fetch leaf categories", ex);
        }
    }

    // Creates or updates a training data document (idempotent upsert)
    public async Task UpsertAsync(TrainingDataDocument document)
    {
        try
        {
            var filter = Builders<TrainingDataDocument>.Filter.Eq(d => d.CategoryId, document.CategoryId);
            
            // Check if document exists to preserve its _id
            var existing = await _collection.Find(filter).FirstOrDefaultAsync();
            if (existing != null)
            {
                // Preserve the existing _id to avoid immutable field error
                document.Id = existing.Id;
            }
            else if (string.IsNullOrEmpty(document.Id))
            {
                // For new documents, set Id based on CategoryId
                document.Id = document.CategoryId.ToString();
            }
            
            var options = new ReplaceOptions { IsUpsert = true };
            await _collection.ReplaceOneAsync(filter, document, options);
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex, "MongoDB error upserting category {CategoryId}", document.CategoryId);
            throw new RepositoryException($"Failed to upsert category {document.CategoryId}", ex);
        }
    }

    // Deletes a training data document by category ID
    public async Task DeleteAsync(ushort categoryId)
    {
        try
        {
            var filter = Builders<TrainingDataDocument>.Filter.Eq(d => d.CategoryId, categoryId);
            await _collection.DeleteOneAsync(filter);
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex, "MongoDB error deleting category {CategoryId}", categoryId);
            throw new RepositoryException($"Failed to delete category {categoryId}", ex);
        }
    }

    // Adds a brand/model to a category's brandsModels array
    public async Task AddBrandModelAsync(ushort categoryId, BrandModelInfo model)
    {
        try
        {
            var filter = Builders<TrainingDataDocument>.Filter.Eq(d => d.CategoryId, categoryId);
            var update = Builders<TrainingDataDocument>.Update
                .Push(d => d.BrandsModels, model)
                .Set(d => d.HasModels, true);
            await _collection.UpdateOneAsync(filter, update);
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex, "MongoDB error adding brand/model {BrandModelId} to category {CategoryId}", 
                model.BrandModelId, categoryId);
            throw new RepositoryException($"Failed to add brand/model {model.BrandModelId} to category {categoryId}", ex);
        }
    }

    // Updates a brand/model in a category's brandsModels array
    public async Task UpdateBrandModelAsync(ushort categoryId, ushort brandModelId, BrandModelInfo model)
    {
        try
        {
            var filter = Builders<TrainingDataDocument>.Filter.And(
                Builders<TrainingDataDocument>.Filter.Eq(d => d.CategoryId, categoryId),
                Builders<TrainingDataDocument>.Filter.ElemMatch(d => d.BrandsModels, 
                    bm => bm.BrandModelId == brandModelId)
            );
            
            var update = Builders<TrainingDataDocument>.Update
                .Set("brandsModels.$.nameEnglish", model.NameEnglish)
                .Set("brandsModels.$.nameArabic", model.NameArabic)
                .Set("brandsModels.$.nameKurdish", model.NameKurdish);
            
            await _collection.UpdateOneAsync(filter, update);
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex, "MongoDB error updating brand/model {BrandModelId} in category {CategoryId}", 
                brandModelId, categoryId);
            throw new RepositoryException($"Failed to update brand/model {brandModelId} in category {categoryId}", ex);
        }
    }

    // Removes a brand/model from a category's brandsModels array
    public async Task RemoveBrandModelAsync(ushort categoryId, ushort brandModelId)
    {
        try
        {
            var filter = Builders<TrainingDataDocument>.Filter.Eq(d => d.CategoryId, categoryId);
            var update = Builders<TrainingDataDocument>.Update
                .PullFilter(d => d.BrandsModels, bm => bm.BrandModelId == brandModelId);
            
            await _collection.UpdateOneAsync(filter, update);
            
            // Check if any models remain and update hasModels flag
            var document = await GetByCategoryIdAsync(categoryId);
            if (document != null && document.BrandsModels.Count == 0)
            {
                var updateHasModels = Builders<TrainingDataDocument>.Update.Set(d => d.HasModels, false);
                await _collection.UpdateOneAsync(filter, updateHasModels);
            }
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex, "MongoDB error removing brand/model {BrandModelId} from category {CategoryId}", 
                brandModelId, categoryId);
            throw new RepositoryException($"Failed to remove brand/model {brandModelId} from category {categoryId}", ex);
        }
    }
}
