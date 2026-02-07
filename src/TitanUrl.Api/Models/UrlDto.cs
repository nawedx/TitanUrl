namespace TitanUrl.Api.Models;

public record ShortenUrlRequest(string Url);
public record ShortenUrlResponse(string ShortCode, string ShortUrl);
