using StackExchange.Redis;
using Npgsql;
using TitanUrl.Core;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------
// 1. REGISTER INFRASTRUCTURE
// ---------------------------------------------------------

// Redis (Singleton)
// We use a multiplexer which handles connection management efficiently
var redisConnectionString = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
var redis = ConnectionMultiplexer.Connect(redisConnectionString);
builder.Services.AddSingleton<IConnectionMultiplexer>(redis);

// PostgreSQL (Scoped - created per request usually, but here we just need the string)
// We will use Dapper, so we just need to inject the connection string or factory
var dbConnectionString = builder.Configuration.GetConnectionString("Postgres") ??
                        "Host=localhost;Port=5432;Database=titan_url;Username=admin;Password=password";
builder.Services.AddSingleton(new DatabaseConfig { ConnectionString = dbConnectionString });

// ---------------------------------------------------------
// 2. REGISTER CORE SERVICES
// ---------------------------------------------------------

// Snowflake Generator (Singleton - Critical!)
// MachineID = 1 (In a real distributed system, this comes from config/environment var)
// Epoch = Jan 1, 2024
var epoch = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
var snowflake = new SnowflakeIdGenerator(machineId: 1, customEpoch: epoch);
builder.Services.AddSingleton(snowflake);

// Add Controllers
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register the background worker
builder.Services.AddHostedService<TitanUrl.Api.Workers.AnalyticsWorker>();

var app = builder.Build();

// ---------------------------------------------------------
// 3. INITIALIZE DB SCHEMA (Quick Hack for Dev)
// ---------------------------------------------------------
// In production, use Flyway or DbUp. Here we just create the table on startup.
using (var scope = app.Services.CreateScope())
{
    var dbConfig = scope.ServiceProvider.GetRequiredService<DatabaseConfig>();
    using var conn = new NpgsqlConnection(dbConfig.ConnectionString);
    conn.Open();

    // Create Tables if not exists
    var sql = @"
    CREATE TABLE IF NOT EXISTS url_mappings (
        id BIGINT PRIMARY KEY,
        original_url TEXT NOT NULL,
        short_code VARCHAR(20) NOT NULL,
        created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
    );
    CREATE INDEX IF NOT EXISTS idx_short_code ON url_mappings(short_code);

    CREATE TABLE IF NOT EXISTS url_analytics (
        url_id BIGINT PRIMARY KEY,
        click_count BIGINT DEFAULT 0,
        last_updated TIMESTAMP DEFAULT CURRENT_TIMESTAMP
    );
    ";

    using var cmd = new NpgsqlCommand(sql, conn);
    cmd.ExecuteNonQuery();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();
app.MapControllers();

app.Run();

// Simple Config Class
public class DatabaseConfig
{
    public string ConnectionString { get; set; } = string.Empty;
}
