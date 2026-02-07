using Dapper;
using Npgsql;
using StackExchange.Redis;
using System.Diagnostics.Metrics;

namespace TitanUrl.Api.Workers;

public class AnalyticsWorker : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _dbConnectionString;
    private readonly ILogger<AnalyticsWorker> _logger;
    private const string StreamName = "stream:clicks";
    private const string ConsumerGroup = "analytics_group";
    private const string ConsumerName = "worker_1";

    // Custom Metrics
    private static readonly Meter _meter = new Meter("TitanUrl.Worker");
    private static readonly Counter<long> _clicksProcessed = _meter.CreateCounter<long>("clicks_processed", description: "Total clicks processed by worker");
    private static readonly Histogram<double> _batchSize = _meter.CreateHistogram<double>("worker_batch_size", description: "Size of each batch processed");
    private static readonly ObservableCounter<long> _streamLag = _meter.CreateObservableCounter("stream_lag", GetStreamLag, description: "Number of pending messages in click stream");

    private IConnectionMultiplexer _redisForLag;

    private static long GetStreamLag()
    {
        // This will be populated when initialized
        return 0;
    }

    public AnalyticsWorker(
        IConnectionMultiplexer redis,
        DatabaseConfig dbConfig,
        ILogger<AnalyticsWorker> logger)
    {
        _redis = redis;
        _redisForLag = redis;
        _dbConnectionString = dbConfig.ConnectionString;
        _logger = logger;
    }

    // Initialize the Consumer Group on startup
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var db = _redis.GetDatabase();
        try
        {
            // Create the group if it doesn't exist.
            // "0-0" means start reading from the beginning of the stream.
            await db.StreamCreateConsumerGroupAsync(StreamName, ConsumerGroup, "0-0");
        }
        catch (RedisServerException)
        {
            // Group likely already exists, ignore.
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = _redis.GetDatabase();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. Read up to 100 events from the stream
                var results = await db.StreamReadGroupAsync(
                    StreamName,
                    ConsumerGroup,
                    ConsumerName,
                    count: 100);

                if (results.Length == 0)
                {
                    // No clicks? Sleep for 1 second to save CPU.
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                // Record batch size metric
                _batchSize.Record(results.Length);

                // 2. Aggregation (In-Memory Map Reduce)
                // We turn 100 stream entries into a Dictionary of unique URL updates.
                var clicksMap = new Dictionary<long, int>();
                var messageIds = new List<RedisValue>(); // To acknowledge later

                foreach (var entry in results)
                {
                    messageIds.Add(entry.Id);

                    // Parse the ID we stored in Step 2
                    if (long.TryParse(entry.Values[0].Value, out long urlId))
                    {
                        clicksMap.TryAdd(urlId, 0);
                        clicksMap[urlId]++;
                    }
                }

                // 3. Batch Update Database (The "Write-Behind")
                if (clicksMap.Count > 0)
                {
                    await using (var conn = new NpgsqlConnection(_dbConnectionString))
                    {
                        await conn.OpenAsync(stoppingToken);
                        await using (var trans = await conn.BeginTransactionAsync(stoppingToken))
                        {
                            foreach (var (urlId, count) in clicksMap)
                            {
                                // Upsert: Insert if new, Update if exists
                                var sql = @"
                                    INSERT INTO url_analytics (url_id, click_count)
                                    VALUES (@Id, @Count)
                                    ON CONFLICT (url_id)
                                    DO UPDATE SET click_count = url_analytics.click_count + @Count,
                                                  last_updated = NOW()";

                                await conn.ExecuteAsync(sql, new { Id = urlId, Count = count }, trans);
                            }
                            await trans.CommitAsync(stoppingToken);
                        }
                    }

                    _logger.LogInformation($"Flushed {clicksMap.Count} unique URL updates to DB.");

                    // Record clicks processed metric
                    _clicksProcessed.Add(results.Length);
                }

                // 4. Acknowledge (Tell Redis we are done with these messages)
                await db.StreamAcknowledgeAsync(StreamName, ConsumerGroup, messageIds.ToArray());

                // Log stream lag for monitoring
                try
                {
                    var streamInfo = await db.ExecuteAsync($"XINFO STREAM {StreamName}");
                    _logger.LogDebug($"Stream info: {streamInfo}");
                }
                catch { /* Stream info not available */ }

                // Optional: Delete processed messages to keep Redis memory low
                // await db.StreamDeleteAsync(StreamName, messageIds.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing analytics stream");
                await Task.Delay(5000, stoppingToken); // Backoff on error
            }
        }
    }
}
