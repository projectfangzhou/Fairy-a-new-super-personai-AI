using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using MyAiAssistant.Services;

static JsonSerializerOptions IndentedOptions() => new()
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

// Offline update manifest signer for FairyAI.
// The ECDSA P-256 private key never enters the repository; keep it on offline media.

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0) { Usage(); return 2; }
    try
    {
        return args[0] switch
        {
            "genkey" => GenKey(args),
            "config" => WriteConfig(args),
            "sign" => Sign(args),
            _ => Unknown(args[0])
        };
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"unknown command: {command}");
    Usage();
    return 2;
}

static void Usage()
{
    Console.Error.WriteLine("""
        Usage:
          UpdateSigner genkey <outDir>
              Creates ecdsa-p256-private.pem (PKCS#8) and ecdsa-p256-public.key (base64 0x04||X||Y) in outDir.
              Move the private key to offline media; never commit it.

          UpdateSigner config <outFile> --manifest-url <https-url> --public-key <base64>
              Writes the client-side updates.json consumed by SignedUpdateService.

          UpdateSigner sign --key <privateKeyFile> --sequence <n> --expires-days <1..90> --out <file>
                            [--windows <artifact> <version> <url>]
                            [--android <artifact> <version> <url>]
                            [--ubuntu  <artifact> <version> <url>]
              Hashes each artifact, signs the manifest, self-verifies, then writes the envelope.
        """);
}

static string Require(string[] args, ref int i)
{
    if (i + 1 >= args.Length) throw new ArgumentException($"missing value for {args[i]}");
    return args[++i];
}

static byte[] PublicKeyBlob(ECDsa key)
{
    var parameters = key.ExportParameters(includePrivateParameters: false);
    var blob = new byte[SignedUpdateManifest.PublicKeyBytes];
    blob[0] = 0x04;
    parameters.Q.X!.CopyTo(blob, 1);
    parameters.Q.Y!.CopyTo(blob, 33);
    return blob;
}

static byte[] Sign(ECDsa key, byte[] message) =>
    key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

static int GenKey(string[] args)
{
    if (args.Length < 2) throw new ArgumentException("genkey requires an output directory.");
    var dir = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(dir);
    var privatePath = Path.Combine(dir, "ecdsa-p256-private.pem");
    var publicPath = Path.Combine(dir, "ecdsa-p256-public.key");
    if (File.Exists(privatePath) || File.Exists(publicPath))
        throw new InvalidOperationException("Refusing to overwrite an existing keypair.");

    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var publicRaw = PublicKeyBlob(key);
    if (publicRaw.Length != SignedUpdateManifest.PublicKeyBytes)
        throw new InvalidOperationException("Unexpected public key length.");

    File.WriteAllText(privatePath, key.ExportPkcs8PrivateKeyPem(), new UTF8Encoding(false));
    File.WriteAllText(publicPath, Convert.ToBase64String(publicRaw), new UTF8Encoding(false));
    TryRestrict(privatePath);

    Console.WriteLine($"private key: {privatePath} (PKCS#8 PEM, keep it offline)");
    Console.WriteLine($"public key : {publicPath}");
    Console.WriteLine($"public key (base64): {Convert.ToBase64String(publicRaw)}");
    Console.WriteLine("Store the private key offline. Anyone holding it can sign fake updates.");
    return 0;
}

static int WriteConfig(string[] args)
{
    if (args.Length < 2) throw new ArgumentException("config requires an output file.");
    var outFile = Path.GetFullPath(args[1]);
    string? manifestUrl = null, publicKey = null;
    for (var i = 2; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--manifest-url": manifestUrl = Require(args, ref i); break;
            case "--public-key": publicKey = Require(args, ref i); break;
            default: throw new ArgumentException($"unknown option {args[i]}");
        }
    }
    if (manifestUrl is null || publicKey is null) throw new ArgumentException("both --manifest-url and --public-key are required.");
    SignedUpdateManifest.SafeUrl(manifestUrl);
    SignedUpdateManifest.DecodeBase64(publicKey, SignedUpdateManifest.PublicKeyBytes);

    var node = new JsonObject { ["manifest_url"] = manifestUrl, ["public_key"] = publicKey };
    Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
    File.WriteAllText(outFile, node.ToJsonString(IndentedOptions()), new UTF8Encoding(false));
    Console.WriteLine($"wrote client config: {outFile}");
    return 0;
}

static int Sign(string[] args)
{
    string? keyPath = null, outPath = null;
    long? sequence = null;
    var expiresDays = 7;
    var releases = new List<(string Platform, string Artifact, string Version, string Url)>();

    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--key": keyPath = Require(args, ref i); break;
            case "--out": outPath = Require(args, ref i); break;
            case "--sequence": sequence = long.Parse(Require(args, ref i), CultureInfo.InvariantCulture); break;
            case "--expires-days": expiresDays = int.Parse(Require(args, ref i), CultureInfo.InvariantCulture); break;
            case "--windows": releases.Add(ReadRelease("windows", args, ref i)); break;
            case "--android": releases.Add(ReadRelease("android", args, ref i)); break;
            case "--ubuntu": releases.Add(ReadRelease("ubuntu", args, ref i)); break;
            default: throw new ArgumentException($"unknown option {args[i]}");
        }
    }
    if (keyPath is null || outPath is null || sequence is null)
        throw new ArgumentException("--key, --sequence and --out are required.");
    if (releases.Count == 0) throw new ArgumentException("at least one of --windows/--android/--ubuntu is required.");
    if (sequence < 1 || sequence > SignedUpdateManifest.MaxSequence) throw new ArgumentException("sequence out of range.");
    if (expiresDays is < 1 or > 90) throw new ArgumentException("--expires-days must be 1..90.");

    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    key.ImportFromPem(File.ReadAllText(keyPath));
    var publicKey = Convert.ToBase64String(PublicKeyBlob(key));

    var now = DateTimeOffset.UtcNow;
    var issued = now.AddMinutes(-1);
    var manifest = new JsonObject
    {
        ["schemaVersion"] = 1,
        ["sequence"] = sequence.Value,
        ["issuedAt"] = Format(issued),
        ["expiresAt"] = Format(issued.AddDays(expiresDays)),
        ["releases"] = new JsonArray()
    };

    foreach (var (platform, artifact, version, url) in releases)
    {
        SignedUpdateManifest.SafeUrl(url);
        if (!File.Exists(artifact)) throw new FileNotFoundException($"artifact not found: {artifact}");
        var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifact))).ToLowerInvariant();
        var release = new JsonObject
        {
            ["platform"] = platform,
            ["version"] = version,
            ["url"] = url,
            ["sha256"] = sha256
        };
        release["signature"] = Convert.ToBase64String(Sign(key, Canonical(release)));
        ((JsonArray)manifest["releases"]!).Add(release);
        Console.WriteLine($"{platform}: {version} sha256={sha256}");
    }

    var envelope = new JsonObject { ["manifest"] = manifest };
    envelope["signature"] = Convert.ToBase64String(Sign(key, Canonical(manifest)));

    var bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString(IndentedOptions()));
    // Self-verify so the tool can never emit an envelope the client would reject.
    var verified = SignedUpdateManifest.Verify(bytes, publicKey, now);
    if (verified.Sequence != sequence.Value) throw new InvalidOperationException("self-verification produced an unexpected sequence.");

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    File.WriteAllBytes(outPath, bytes);
    Console.WriteLine($"wrote signed manifest: {outPath} (sequence {verified.Sequence}, expires {verified.ExpiresAt:u})");
    return 0;
}

static (string, string, string, string) ReadRelease(string platform, string[] args, ref int i)
{
    if (i + 3 >= args.Length) throw new ArgumentException($"{platform} requires <artifact> <version> <url>");
    var artifact = args[++i];
    var version = args[++i];
    var url = args[++i];
    return (platform, artifact, version, url);
}

static string Format(DateTimeOffset value) =>
    value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

static byte[] Canonical(JsonNode node)
{
    using var document = JsonDocument.Parse(node.ToJsonString());
    return SignedUpdateManifest.Canonical(document.RootElement);
}

static void TryRestrict(string path)
{
    try
    {
        if (OperatingSystem.IsWindows())
            File.SetAttributes(path, FileAttributes.ReadOnly);
        else
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    catch { /* best effort; the operator is told to move the key offline anyway */ }
}