using System.Text;

namespace MyAiAssistant.Services.Pairing;

/// <summary>
/// Base32 without the letters I, L, O and U. Identifiers here are meant to be read off one
/// screen and typed or compared on another, so the alphabet avoids the pairs people confuse
/// (1/I/l, 0/O) and the letters that turn short codes into unfortunate words.
/// </summary>
internal static class Base32
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;

        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                builder.Append(Alphabet[(buffer >> bits) & 0x1F]);
            }
        }

        if (bits > 0) builder.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);
        return builder.ToString();
    }

    /// <summary>Splits an identifier into dash-separated groups so it can be read aloud.</summary>
    public static string Group(string value, int size = 4)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (size <= 0 || value.Length <= size) return value;

        var builder = new StringBuilder(value.Length + value.Length / size);
        for (var index = 0; index < value.Length; index++)
        {
            if (index > 0 && index % size == 0) builder.Append('-');
            builder.Append(value[index]);
        }
        return builder.ToString();
    }
}
