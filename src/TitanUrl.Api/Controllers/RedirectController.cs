using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using StackExchange.Redis;
using TitanUrl.Core;

namespace TitanUrl.Api.Controllers;

[ApiController]
[Route("")]
public class RedirectController : ControllerBase
{
    private readonly string _dbConnectionString;
    private readonly IDatabase _redis;

    public RedirectController(
        DatabaseConfig dbConfig,
        IConnectionMultiplexer redisMux)
    {
        _dbConnectionString = dbConfig.ConnectionString;
        _redis = redisMux.GetDatabase();
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
            try
            {
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
            catch
            {
                // Invalid Base62 encoding
                return NotFound();
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
