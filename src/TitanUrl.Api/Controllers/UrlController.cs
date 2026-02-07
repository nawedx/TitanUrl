using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using StackExchange.Redis;
using TitanUrl.Api.Models;
using TitanUrl.Core;

namespace TitanUrl.Api.Controllers;

[ApiController]
[Route("api")]
public class UrlController : ControllerBase
{
    private readonly SnowflakeIdGenerator _idGenerator;
    private readonly string _dbConnectionString;
    private readonly IDatabase _redis;

    public UrlController(
        SnowflakeIdGenerator idGenerator,
        DatabaseConfig dbConfig,
        IConnectionMultiplexer redisMux)
    {
        _idGenerator = idGenerator;
        _dbConnectionString = dbConfig.ConnectionString;
        _redis = redisMux.GetDatabase();
    }

    [HttpPost("shorten")]
    public async Task<IActionResult> Shorten([FromBody] ShortenUrlRequest request)
    {
        // 1. Validate URL (Basic check)
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out _))
        {
            return BadRequest("Invalid URL format.");
        }

        // 2. Generate Unique ID (Local Memory Operation - 0.0001ms)
        // This is the "Magic" step that prevents collisions.
        long id = _idGenerator.NextId();

        // 3. Convert to Short Code (Base62 Math - 0.0001ms)
        string shortCode = Base62Converter.Encode(id);

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

        return Ok(response);
    }

    [HttpGet("{shortCode}")]
    public async Task<IActionResult> RedirectToUrl(string shortCode)
    {
        var db = _redis;
        string? originalUrl = null;

        // 1. FAST PATH: Check Redis Cache (Memory)
        // Key: "url:k9J3z"
        var cachedUrl = await db.StringGetAsync($"url:{shortCode}");

        if (cachedUrl.HasValue)
        {
            originalUrl = cachedUrl.ToString();
        }
        else
        {
            // 2. SLOW PATH: Check Database (Disk)
            // If it's not in cache, we decode the ID and look it up.
            long id = Base62Converter.Decode(shortCode);

            using (var conn = new NpgsqlConnection(_dbConnectionString))
            {
                originalUrl = await conn.QuerySingleOrDefaultAsync<string>(
                    "SELECT original_url FROM url_mappings WHERE id = @Id",
                    new { Id = id });
            }

            // 3. Populate Cache (Read Repair)
            // Next time, this will be a cache hit.
            if (originalUrl != null)
            {
                await db.StringSetAsync(
                    $"url:{shortCode}",
                    originalUrl,
                    TimeSpan.FromHours(24));
            }
        }

        if (originalUrl == null)
        {
            return NotFound();
        }

        // 4. ANALYTICS (Fire-and-Forget)
        // We write to a Redis Stream named "clicks".
        // This takes ~0.5ms. The user doesn't wait for the SQL UPDATE.
        // Format: Key="id", Value=Base62ID (or SnowflakeID)
        long urlId = Base62Converter.Decode(shortCode);

        await db.StreamAddAsync(
            key: "stream:clicks",
            streamField: "urlId",
            streamValue: urlId.ToString());

        // 5. Redirect User
        return Redirect(originalUrl);
    }
}
