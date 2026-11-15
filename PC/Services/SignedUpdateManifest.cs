using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NSec.Cryptography;

namespace MyAiAssistant.Services;

public sealed record VerifiedUpdateManifest(long Sequence, string Digest, DateTimeOffset ExpiresAt,
    string? WindowsVersion);

public static class SignedUpdateManifest
{
    public const int MaxBytes = 32768;
    public const long MaxSequence = 9007199254740991;

    public static JsonDocument Parse(byte[] bytes)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Update JSON is too large.");
        _ = new UTF8Encoding(false, true).GetString(bytes);
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        try { RejectDuplicates(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON field.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    internal static void Exact(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Count() != names.Length ||
            names.Any(name => !element.TryGetProperty(name, out _)))
            throw new InvalidDataException("Invalid update schema.");
    }

    internal static string Text(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String) throw new InvalidDataException("Expected a string.");
        return element.GetString()!;
    }

    public static byte[] DecodeBase64(string value, int length)
    {
        if (value.Length > 128 || !Regex.IsMatch(value, @"\A[A-Za-z0-9+/]+={0,2}\z"))
            throw new InvalidDataException("Invalid key or signature encoding.");
        var bytes = Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='));
        if (bytes.Length != length) throw new InvalidDataException("Invalid key or signature length.");
        return bytes;
    }

    public static Uri SafeUrl(string value)
    {
        if (value.Length is < 1 or > 2048 || value.Any(c => c <= 32 || c == '\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || value.Contains('#') || uri.Port != 443 ||
            !Regex.IsMatch(uri.Host, @"\A(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,63}\z") ||
            Regex.IsMatch(uri.Host, @"(?:^|\.)(?:localhost|local|localdomain|internal|intranet|lan|home|arpa|test|invalid|example|onion)\z"))
            throw new InvalidDataException("A public HTTPS URL without credentials or redirects is required.");
        return uri;
    }

    private static DateTimeOffset Timestamp(JsonElement element)
    {
        var text = Text(element);
        if (!Regex.IsMatch(text, @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,3})?(?:Z|[+-]\d{2}:\d{2})\z") ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
            throw new InvalidDataException("Invalid manifest timestamp.");
        return result;
    }

    internal static long Sequence(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value) || value < 1 || value > MaxSequence)
            throw new InvalidDataException("Invalid manifest sequence.");
        return value;
    }

    public static VerifiedUpdateManifest Verify(byte[] envelopeBytes, string publicKey, DateTimeOffset now)
    {
        using var document = Parse(envelopeBytes);
        var envelope = document.RootElement;
        if (envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("releases", out _))
            Exact(envelope, "manifest", "signature", "releases");
        else Exact(envelope, "manifest", "signature");
        var manifest = envelope.GetProperty("manifest");
        Exact(manifest, "schemaVersion", "sequence", "issuedAt", "expiresAt", "releases");
        if (Sequence(manifest.GetProperty("schemaVersion")) != 1) throw new InvalidDataException("Unsupported schema.");
        var sequence = Sequence(manifest.GetProperty("sequence"));
        var issued = Timestamp(manifest.GetProperty("issuedAt"));
        var expires = Timestamp(manifest.GetProperty("expiresAt"));
        if (issued > now.AddMinutes(1) || expires <= now || expires <= issued || expires - issued > TimeSpan.FromDays(90))
            throw new InvalidDataException("Expired or invalid manifest lifetime.");
        var releases = manifest.GetProperty("releases");
        if (releases.ValueKind != JsonValueKind.Array || releases.GetArrayLength() is < 1 or > 3)
            throw new InvalidDataException("Invalid releases.");
        var algorithm = SignatureAlgorithm.Ed25519;
        var key = PublicKey.Import(algorithm, DecodeBase64(publicKey, 32), KeyBlobFormat.RawPublicKey);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? windowsVersion = null;
        foreach (var release in releases.EnumerateArray())
        {
            Exact(release, "platform", "version", "url", "sha256", "signature");
            var platform = Text(release.GetProperty("platform"));
            var version = Text(release.GetProperty("version"));
            if (platform is not ("windows" or "android" or "ubuntu") || !seen.Add(platform) ||
                !Regex.IsMatch(version, @"\A[0-9A-Za-z][0-9A-Za-z._+\-]{0,63}\z") ||
                !Regex.IsMatch(Text(release.GetProperty("sha256")), @"\A[a-f0-9]{64}\z"))
                throw new InvalidDataException("Invalid release fields.");
            SafeUrl(Text(release.GetProperty("url")));
            if (!algorithm.Verify(key, Canonical(release, "signature"), DecodeBase64(Text(release.GetProperty("signature")), 64)))
                throw new InvalidDataException("Invalid release signature.");
            if (platform == "windows") windowsVersion = version;
        }
        var canonical = Canonical(manifest);
        if (!algorithm.Verify(key, canonical, DecodeBase64(Text(envelope.GetProperty("signature")), 64)))
            throw new InvalidDataException("Invalid envelope signature.");
        return new(sequence, Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant(), expires, windowsVersion);
    }

    // JSON.stringify emits non-ASCII strings literally; the default .NET encoder does not.
    public static byte[] Canonical(JsonElement element, string? omitRootProperty = null)
    {
        var builder = new StringBuilder();
        Append(element, builder, omitRootProperty);
        return new UTF8Encoding(false, true).GetBytes(builder.ToString());
    }

    private static void Quote(string value, StringBuilder builder)
    {
        builder.Append('"');
        foreach (var c in value)
            builder.Append(c switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                < ' ' => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture), _ => c.ToString()
            });
        builder.Append('"');
    }

    private static void Append(JsonElement element, StringBuilder builder, string? omit = null)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject().Where(p => p.Name != omit).OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) builder.Append(',');
                    first = false;
                    Quote(property.Name, builder); builder.Append(':'); Append(property.Value, builder);
                }
                builder.Append('}'); break;
            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem) builder.Append(',');
                    firstItem = false; Append(item, builder);
                }
                builder.Append(']'); break;
            case JsonValueKind.String: Quote(Text(element), builder); break;
            case JsonValueKind.Number:
                builder.Append(element.GetInt64().ToString(CultureInfo.InvariantCulture)); break;
            default: throw new InvalidDataException("Unsupported canonical JSON value.");
        }
    }
}
