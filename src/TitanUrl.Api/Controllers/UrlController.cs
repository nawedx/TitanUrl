using Microsoft.AspNetCore.Mvc;
using TitanUrl.Api.Models;
using TitanUrl.Core;
using Dapper;
using Npgsql;
using StackExchange.Redis;
using System.Diagnostics;

namespace TitanUrl.Api.Controllers;

[ApiController]
[Route("api")]
public class UrlController : ControllerBase
{
    private readonly SnowflakeIdGenerator _idGenerator;
    private readonly string _dbConnectionString;
    private readonly IDatabase _redis;
    private readonly ILogger<UrlController> _logger;

    public UrlController(
        SnowflakeIdGenerator idGenerator,
        DatabaseConfig dbConfig,
        IConnectionMultiplexer redisMux,
        ILogger<UrlController> logger)
    {
        _idGenerator = idGenerator;
        _dbConnectionString = dbConfig.ConnectionString;
        _redis = redisMux.GetDatabase();
        _logger = logger;
    }

    [HttpPost("shorten")]
    public async Task<IActionResult> Shorten([FromBody] ShortenUrlRequest request)
    {
        var correlationId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        var activity = Activity.Current;

        // 1. Validate URL (Basic check)
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out _))
        {
            _logger.LogWarning("Invalid URL format: {Url} (CorrelationId: {CorrelationId})", request.Url, correlationId);
            return BadRequest("Invalid URL format.");
        }

        // 2. Generate Unique ID (Local Memory Operation - 0.0001ms)
        // This is the "Magic" step that prevents collisions.
        long id = _idGenerator.NextId();

        // 3. Convert to Short Code (Base62 Math - 0.0001ms)
        string shortCode = Base62Converter.Encode(id);

        if (activity != null)
        {
            activity.SetTag("correlation_id", correlationId);
            activity.SetTag("short_code", shortCode);
            activity.SetTag("url_length", request.Url.Length);
        }

        // 4. Save to Database (The only blocking I/O)
        // We save both the ID and the Code for fast lookups later.
        using (var conn = new NpgsqlConnection(_dbConnectionString))
        {
            var sql = @"
                INSERT INTO url_mappings (id, original_url, short_code)
                VALUES (@Id, @OriginalUrl, @ShortCode)";

            await conn.ExecuteAsync(sql, new
            {
                Id = id,
                OriginalUrl = request.Url,
                ShortCode = shortCode
            });
        }

        // 5. Optimization: "Read-After-Write" Cache
        // If the user clicks the link 1 second later, we want it in Redis already.
        // We set a 24-hour TTL (Time To Live).
        await _redis.StringSetAsync(
            key: $"url:{shortCode}",
            value: request.Url,
            expiry: TimeSpan.FromHours(24)
        );

        // 6. Return Result
        var response = new ShortenUrlResponse(
            ShortCode: shortCode,
            ShortUrl: $"{Request.Scheme}://{Request.Host}/{shortCode}"
        );

        _logger.LogInformation("URL shortened successfully - ShortCode: {ShortCode}, CorrelationId: {CorrelationId}", shortCode, correlationId);

        return Ok(response);
    }
}
