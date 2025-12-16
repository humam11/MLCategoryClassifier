using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using MLCategoryClassifier;
using MLCategoryClassifier.Tools;

// Configure MongoDB conventions before any MongoDB operations (Requirements 8.2)
MongoDbConfiguration.Configure();

var builder = WebApplication.CreateBuilder(args);

// Check for debug command before building the full app
if (args.Length > 0 && args[0] == "debug")
{
    await CategoryDebugger.RunAsync(args, builder.Configuration);
    return;
}

// Add controllers
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Configure PostgreSQL DbContext with connection string (Requirements 8.1)
builder.Services.AddDbContext<PostgresDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("PostgreSQL")));

// Configure MongoDB client and database (Requirements 8.2)
builder.Services.AddSingleton<IMongoClient>(sp =>
{
    var connectionString = builder.Configuration.GetConnectionString("MongoDB");
    return new MongoClient(connectionString);
});

builder.Services.AddSingleton<IMongoDatabase>(sp =>
{
    var client = sp.GetRequiredService<IMongoClient>();
    var databaseName = builder.Configuration.GetValue<string>("MongoDB:DatabaseName") ?? "ClassifiedAdsDb";
    return client.GetDatabase(databaseName);
});

// Register all concrete services (no interfaces)
builder.Services.AddSingleton<TextPreprocessor>();
builder.Services.AddSingleton<IntentDetector>();
builder.Services.AddSingleton<MLClassifier>();
builder.Services.AddScoped<TrainingDataRepository>();
builder.Services.AddScoped<SubstringMatcher>();
builder.Services.AddSingleton<TrainingDataSyncService>(); // Singleton because it uses IServiceScopeFactory internally
builder.Services.AddScoped<CategoryClassificationService>();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();

// Perform startup initialization: sync training data and train ML models (Requirements 1.1, 1.7, 6.8)
await InitializeApplicationAsync(app.Services);

app.Run();

// Initializes the application by syncing training data and training ML models
static async Task InitializeApplicationAsync(IServiceProvider services)
{
    var logger = services.GetRequiredService<ILogger<Program>>();
    
    try
    {
        logger.LogInformation("Starting application initialization...");
        
        // Step 1: Sync all categories from PostgreSQL to MongoDB (Requirements 1.1, 1.7)
        var syncService = services.GetRequiredService<TrainingDataSyncService>();
        await syncService.SyncAllCategoriesAsync();
        logger.LogInformation("Training data synchronization completed");
        
        // Step 2: Train ML models for both Arabic and Kurdish (Requirement 6.8)
        using var scope = services.CreateScope();
        var trainingDataRepo = scope.ServiceProvider.GetRequiredService<TrainingDataRepository>();
        var mlClassifier = services.GetRequiredService<MLClassifier>();
        
        var trainingDocuments = await trainingDataRepo.GetAllAsync();
        
        if (trainingDocuments.Count > 0)
        {
            // Train Arabic model
            logger.LogInformation("Training Arabic ML model...");
            mlClassifier.Train(trainingDocuments, "ar");
            
            // Train Kurdish model
            logger.LogInformation("Training Kurdish ML model...");
            mlClassifier.Train(trainingDocuments, "kr");
            
            logger.LogInformation("ML model training completed for both languages");
        }
        else
        {
            logger.LogWarning("No training documents found. ML models will not be trained until data is available");
        }
        
        // Step 3: PostgresNotificationListener starts automatically as a hosted service (Requirement 2.1)
        logger.LogInformation("Application initialization completed. PostgresNotificationListener will start listening for real-time updates");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Application initialization failed");
        throw;
    }
}


