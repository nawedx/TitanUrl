using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;
using Npgsql;

namespace TitanUrl.Api.Controllers;

[ApiController]
[Route("api/metrics")]
public class MetricsController : ControllerBase
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _dbConnectionString;

    public MetricsController(
        IConnectionMultiplexer redis,
        DatabaseConfig dbConfig)
    {
        _redis = redis;
        _dbConnectionString = dbConfig.ConnectionString;
    }

    [HttpGet("stream-lag")]
    public async Task<IActionResult> GetStreamLag()
    {
        try
        {
            var db = _redis.GetDatabase();
            var streamInfo = await db.ExecuteAsync("XINFO STREAM stream:clicks");

            var response = new
            {
                stream = "stream:clicks",
                info = streamInfo?.ToString() ?? "N/A",
                timestamp = DateTime.UtcNow
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("redis-info")]
    public IActionResult GetRedisInfo()
    {
        try
        {
            var server = _redis.GetServer(_redis.GetEndPoints().First());

            // Ping to verify connection
            server.Ping();

            var response = new
            {
                connected = true,
                status = "Redis is operational",
                timestamp = DateTime.UtcNow
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message, connected = false });
        }
    }

    [HttpGet("postgres-status")]
    public IActionResult GetPostgresStatus()
    {
        try
        {
            using var conn = new NpgsqlConnection(_dbConnectionString);
            conn.Open();

            // Use NpgsqlCommand instead of Dapper for simple scalar query
            using var cmd = new NpgsqlCommand("SELECT NOW()", conn);
            var result = cmd.ExecuteScalar();

            conn.Close();

            return Ok(new
            {
                connected = true,
                server_time = result?.ToString() ?? "N/A"
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message, connected = false });
        }
    }
}
