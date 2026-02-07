namespace TitanUrl.Core.Tests;

public class Base62Tests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1")]
    [InlineData(10, "a")]
    [InlineData(61, "Z")]
    [InlineData(62, "10")] // Rollover
    [InlineData(12345, "3d7")] 
    public void Encode_ShouldReturnExpectedString(long input, string expected)
    {
        var result = Base62Converter.Encode(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("a", 10)]
    [InlineData("Z", 61)]
    [InlineData("10", 62)]
    [InlineData("3d7", 12345)]
    public void Decode_ShouldReturnExpectedId(string input, long expected)
    {
        var result = Base62Converter.Decode(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void RoundTrip_ShouldPreserveValue()
    {
        // Test with a massive Snowflake-like ID
        long originalId = 188394029100032; 
        
        string shortCode = Base62Converter.Encode(originalId);
        long decodedId = Base62Converter.Decode(shortCode);

        Assert.Equal(originalId, decodedId);
    }
}