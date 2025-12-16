using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MLCategoryClassifier;

/// <summary>
/// Background service that listens for PostgreSQL NOTIFY events on categories_channel 
/// and brands_models_channel, then updates MongoDB via TrainingDataSyncService.
/// Implements reconnection logic with exponential backoff.
/// Requirements: 2.1, 2.8
/// </summary>
public class PostgresNotificationListener : IHostedService, IDisposable
{
    private readonly TrainingDataSyncService _syncService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PostgresNotificationListener> _logger;
    
    private NpgsqlConnection? _connection;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _listenerTask;
    
    // Exponential backoff settings
    private const int InitialRetryDelayMs = 1000;
    private const int MaxRetryDelayMs = 60000;
    private const int MaxReconnectAttempts = 5;
    
    // Channel names
    private const string CategoriesChannel = "categories_channel";
    private const string BrandsModelsChannel = "brands_models_channel";

    public PostgresNotificationListener(
        TrainingDataSyncService syncService,
        IConfiguration configuration,
        ILogger<PostgresNotificationListener> logger)
    {
        _syncService = syncService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PostgresNotificationListener starting...");
        
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        
        // Start the listener in a background task
        _listenerTask = Task.Run(() => ListenForNotificationsAsync(_cancellationTokenSource.Token), cancellationToken);
        
        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PostgresNotificationListener stopping...");
        
        // Signal cancellation
        _cancellationTokenSource?.Cancel();
        
        // Wait for the listener task to complete
        if (_listenerTask != null)
        {
            try
            {
                await Task.WhenAny(_listenerTask, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }
        
        // Close the connection
        await CloseConnectionAsync();
        
        _logger.LogInformation("PostgresNotificationListener stopped");
    }


    private async Task ListenForNotificationsAsync(CancellationToken cancellationToken)
    {
        var retryCount = 0;
        var retryDelay = InitialRetryDelayMs;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await EstablishConnectionAsync(cancellationToken);
                
                // Reset retry counters on successful connection
                retryCount = 0;
                retryDelay = InitialRetryDelayMs;
                
                // Execute LISTEN commands for both channels
                await ExecuteListenCommandsAsync(cancellationToken);
                
                _logger.LogInformation(
                    "Successfully connected and listening on {CategoriesChannel} and {BrandsModelsChannel}",
                    CategoriesChannel, BrandsModelsChannel);
                
                // Process notifications until connection drops or cancellation
                await ProcessNotificationsAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Notification listener cancelled");
                break;
            }
            catch (Exception ex)
            {
                retryCount++;
                
                if (retryCount > MaxReconnectAttempts)
                {
                    _logger.LogError(ex, 
                        "Failed to reconnect after {MaxAttempts} attempts. Notification listener stopping.",
                        MaxReconnectAttempts);
                    break;
                }
                
                _logger.LogWarning(ex, 
                    "Connection lost. Attempting reconnection {RetryCount}/{MaxAttempts} in {RetryDelay}ms",
                    retryCount, MaxReconnectAttempts, retryDelay);
                
                await CloseConnectionAsync();
                
                try
                {
                    await Task.Delay(retryDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                
                // Exponential backoff with cap
                retryDelay = Math.Min(retryDelay * 2, MaxRetryDelayMs);
            }
        }
    }

    private async Task EstablishConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionString = _configuration.GetConnectionString("PostgreSQL");
        
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException("PostgreSQL connection string is not configured");
        }
        
        _connection = new NpgsqlConnection(connectionString);
        await _connection.OpenAsync(cancellationToken);
        
        // Subscribe to notification event
        _connection.Notification += OnNotificationReceived;
        
        _logger.LogDebug("PostgreSQL connection established for notification listening");
    }

    private async Task ExecuteListenCommandsAsync(CancellationToken cancellationToken)
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Connection is not established");
        }
        
        // Execute LISTEN commands for both channels
        await using var cmdCategories = new NpgsqlCommand($"LISTEN {CategoriesChannel}", _connection);
        await cmdCategories.ExecuteNonQueryAsync(cancellationToken);
        
        await using var cmdBrandsModels = new NpgsqlCommand($"LISTEN {BrandsModelsChannel}", _connection);
        await cmdBrandsModels.ExecuteNonQueryAsync(cancellationToken);
    }


    private async Task ProcessNotificationsAsync(CancellationToken cancellationToken)
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Connection is not established");
        }
        
        // Wait for notifications - this will block until a notification arrives or connection drops
        while (!cancellationToken.IsCancellationRequested && _connection.State == System.Data.ConnectionState.Open)
        {
            try
            {
                // Wait for notifications with a timeout to allow checking cancellation
                await _connection.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (NpgsqlException ex)
            {
                _logger.LogError(ex, "Error while waiting for notifications");
                throw;
            }
        }
    }

    private void OnNotificationReceived(object sender, NpgsqlNotificationEventArgs e)
    {
        _logger.LogDebug(
            "Received notification on channel {Channel}: {Payload}",
            e.Channel, e.Payload);
        
        // Process notification asynchronously to not block the notification handler
        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessNotificationPayloadAsync(e.Channel, e.Payload);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, 
                    "Error processing notification from channel {Channel}: {Payload}",
                    e.Channel, e.Payload);
            }
        });
    }

    private async Task ProcessNotificationPayloadAsync(string channel, string payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            _logger.LogWarning("Received empty notification payload on channel {Channel}", channel);
            return;
        }
        
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        
        try
        {
            switch (channel)
            {
                case CategoriesChannel:
                    await HandleCategoryNotificationAsync(payload, jsonOptions);
                    break;
                    
                case BrandsModelsChannel:
                    await HandleBrandModelNotificationAsync(payload, jsonOptions);
                    break;
                    
                default:
                    _logger.LogWarning("Received notification on unknown channel: {Channel}", channel);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, 
                "Failed to parse notification JSON from channel {Channel}: {Payload}",
                channel, payload);
        }
    }

    private async Task HandleCategoryNotificationAsync(string payload, JsonSerializerOptions jsonOptions)
    {
        var notification = JsonSerializer.Deserialize<CategoryNotification>(payload, jsonOptions);
        
        if (notification == null)
        {
            _logger.LogWarning("Failed to deserialize category notification: {Payload}", payload);
            return;
        }
        
        _logger.LogInformation(
            "Processing category notification: Operation={Operation}, CategoryId={CategoryId}",
            notification.Operation, notification.CategoryId);
        
        await _syncService.HandleCategoryNotificationAsync(notification);
    }

    private async Task HandleBrandModelNotificationAsync(string payload, JsonSerializerOptions jsonOptions)
    {
        var notification = JsonSerializer.Deserialize<BrandModelNotification>(payload, jsonOptions);
        
        if (notification == null)
        {
            _logger.LogWarning("Failed to deserialize brand/model notification: {Payload}", payload);
            return;
        }
        
        _logger.LogInformation(
            "Processing brand/model notification: Operation={Operation}, BrandModelId={BrandModelId}, CategoryId={CategoryId}",
            notification.Operation, notification.BrandModelId, notification.CategoryId);
        
        await _syncService.HandleBrandModelNotificationAsync(notification);
    }


    private async Task CloseConnectionAsync()
    {
        if (_connection != null)
        {
            try
            {
                _connection.Notification -= OnNotificationReceived;
                
                if (_connection.State != System.Data.ConnectionState.Closed)
                {
                    await _connection.CloseAsync();
                }
                
                await _connection.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while closing PostgreSQL connection");
            }
            finally
            {
                _connection = null;
            }
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _connection?.Dispose();
    }
}
