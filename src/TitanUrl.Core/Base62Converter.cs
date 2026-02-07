using System.Text;

namespace TitanUrl.Core;

public static class Base62Converter
{
    // The "Alphabet" of allowed characters. 
    // 0-9 (10) + a-z (26) + A-Z (26) = 62 characters.
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private static readonly int Base = Alphabet.Length;

    /// <summary>
    /// Converts a Snowflake ID (long) into a short string.
    /// </summary>
    public static string Encode(long id)
    {
        if (id < 0) throw new ArgumentOutOfRangeException(nameof(id), "ID must be non-negative");
        if (id == 0) return Alphabet[0].ToString();

        var sb = new StringBuilder();

        while (id > 0)
        {
            // 1. Get the remainder (modulo) - this is the "digit" in Base62
            var remainder = (int)(id % Base);
            
            // 2. Prepend the corresponding character
            sb.Insert(0, Alphabet[remainder]);
            
            // 3. Divide by 62 to move to the next digit
            id /= Base;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Converts a short string back into a Snowflake ID (long).
    /// </summary>
    public static long Decode(string shortCode)
    {
        if (string.IsNullOrEmpty(shortCode)) throw new ArgumentNullException(nameof(shortCode));

        long id = 0;

        // Process each character
        foreach (var c in shortCode)
        {
            var index = Alphabet.IndexOf(c);
            if (index == -1)
            {
                throw new ArgumentException($"Invalid character '{c}' in short code.");
            }

            // Standard base conversion logic:
            // Result = (Result * Base) + Value
            id = (id * Base) + index;
        }

        return id;
    }
}