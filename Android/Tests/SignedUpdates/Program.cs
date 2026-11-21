using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using FairyAI_Android.Services;

// The update signature scheme uses the platform's ECDSA P-256 rather than a third-party Ed25519
// library, so the fixtures sign with the same primitive the verifier uses.
var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var publicKey = PublicKeyBlob(key);

static string PublicKeyBlob(ECDsa signer)
{
    var parameters = signer.ExportParameters(includePrivateParameters: false);
    var blob = new byte[65];
    blob[0] = 0x04;
    parameters.Q.X!.CopyTo(blob, 1);
    parameters.Q.Y!.CopyTo(blob, 33);
    return Convert.ToBase64String(blob);
}
var now = DateTimeOffset.UtcNow;
var issued = now.AddMinutes(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
var expires = now.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
var checks = 0;
var jsonOptions = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS: " + name);
}
string Sign(byte[] message) => Convert.ToBase64String(key.SignData(
    message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
// Fixtures are serialized in protocol order independently of production Canonical().
byte[] Envelope(long sequence = 5, string version = "2.0.0", string? expiration = null,
    bool badRelease = false, bool badEnvelope = false, string platform = "android")
{
    var release = new SortedDictionary<string, object>
    {
        ["platform"] = platform, ["version"] = version, ["url"] = "https://github.com/releases/app.apk",
        ["sha256"] = new string('a', 64)
    };
    release["signature"] = badRelease ? Convert.ToBase64String(new byte[64]) : Sign(JsonSerializer.SerializeToUtf8Bytes(release));
    var manifest = new SortedDictionary<string, object>
    {
        ["schemaVersion"] = 1, ["sequence"] = sequence, ["issuedAt"] = issued,
        ["expiresAt"] = expiration ?? expires, ["releases"] = new[] { release }
    };
    return JsonSerializer.SerializeToUtf8Bytes(new
    {
        manifest,
        signature = badEnvelope ? Convert.ToBase64String(new byte[64]) : Sign(JsonSerializer.SerializeToUtf8Bytes(manifest, jsonOptions))
    });
}
void Reject(byte[] data, string name, string? verificationKey = null)
{
    try { SignedUpdateManifest.Verify(data, verificationKey ?? publicKey, now); }
    catch (Exception ex) when (ex is InvalidDataException or JsonException or FormatException or ArgumentException)
    { Assert(true, name); return; }
    throw new Exception("Accepted invalid fixture: " + name);
}
// A signature is 64 bytes (P1363 r‖s) and verifies against the key that produced it.
Assert(Convert.FromBase64String(Sign([])).Length == SignedUpdateManifest.SignatureBytes, "signatures are P1363 r||s");
Assert(Convert.FromBase64String(publicKey).Length == SignedUpdateManifest.PublicKeyBytes, "public keys are 0x04||X||Y");
using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
{
    var message = Encoding.UTF8.GetBytes("self-check");
    var signature = key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    Assert(other.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation) == false,
        "a signature does not verify under another key");
    Assert(key.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
        "a signature verifies under its own key");
}
Assert(SignedUpdateManifest.Verify(Envelope(), publicKey, now).AndroidVersion == "2.0.0", "real release and envelope signatures");
Reject(Envelope(badRelease: true), "bad release signature despite valid envelope");
Reject(Envelope(badEnvelope: true), "bad envelope signature");
Reject(Envelope(expiration: now.AddSeconds(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")), "expired manifest");
Reject(Envelope(), "wrong public key", PublicKeyBlob(ECDsa.Create(ECCurve.NamedCurves.nistP256)));
var tampered = JsonNode.Parse(Envelope())!;
tampered["manifest"]!["releases"]![0]!["version"] = "9.0.0";
Reject(Encoding.UTF8.GetBytes(tampered.ToJsonString()), "tampered version");
Reject(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Envelope()).Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")), "duplicate fields");
Reject(new byte[SignedUpdateManifest.MaxBytes + 1], "oversized envelope");
Assert(SignedUpdateService.IsNewer("1.2", "1.2.0") == false && SignedUpdateService.IsNewer("1.3", "1.2.0") == true &&
       SignedUpdateService.IsNewer("2.0-beta", "1.0") is null, "numeric version comparison");
Assert(SignedUpdateService.CheckInterval == TimeSpan.FromHours(6), "six hour foreground interval");

var directory = Path.Combine(Path.GetTempPath(), "signed-updates-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var handler = new FakeHandler { Body = Envelope() };
    var notifications = 0;
    Task Notify(string _, CancellationToken token) { token.ThrowIfCancellationRequested(); notifications++; return Task.CompletedTask; }
    SignedUpdateService Create(FakeHandler? transport = null) => new("1.0.0", Notify, directory, transport ?? handler, publicKey);
    async Task Configure(string url = "https://github.com/updates.json", string? configuredKey = null) =>
        await File.WriteAllTextAsync(Path.Combine(directory, "updates.json"), JsonSerializer.Serialize(new { manifest_url = url, public_key = configuredKey ?? publicKey }));
    using (var service = Create())
    {
        await service.CheckOnceAsync();
        Assert(service.Status == "disabled" && handler.Calls == 0, "missing config never networks");
        await Configure("");
        await service.CheckOnceAsync();
        Assert(service.Status == "disabled" && handler.Calls == 0, "empty config never networks");
        await Configure("http://github.com/updates.json");
        await service.CheckOnceAsync();
        Assert(service.Status == "error" && handler.Calls == 0, "unsafe config rejected before network");
        await Configure();
        await service.CheckOnceAsync();
        Assert(service.Status == "available" && notifications == 1, "verified update notifies");
        await service.CheckOnceAsync();
        Assert(notifications == 1, "same version notification deduplicated");
    }
    var savedState = await File.ReadAllTextAsync(Path.Combine(directory, "updates-state.json"));
    async Task ErrorCase(byte[] body, string label, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json")
    {
        var transport = new FakeHandler { Body = body, ResponseStatus = status, MediaType = mediaType };
        using var service = Create(transport);
        await service.CheckOnceAsync();
        Assert(service.Status == "error" && notifications == 1, label);
        Assert(await File.ReadAllTextAsync(Path.Combine(directory, "updates-state.json")) == savedState, label + " preserves state");
    }
    await ErrorCase(Envelope(4), "rollback rejected after service restart");
    await ErrorCase(Envelope(5, "3.0.0"), "same sequence changed digest rejected");
    await ErrorCase(Envelope(6, badRelease: true), "invalid signature cannot advance state");
    await ErrorCase(Envelope(6), "redirect response rejected", HttpStatusCode.Redirect);
    await ErrorCase(Envelope(6), "wrong content type rejected", mediaType: "text/html");
    await ErrorCase(new byte[SignedUpdateManifest.MaxBytes + 1], "oversized response rejected");
    using (var service = Create(new FakeHandler { Body = Envelope(6, platform: "windows") }))
    {
        await service.CheckOnceAsync();
        Assert(service.Status == "current" && notifications == 1, "windows-only release never prompts Android");
    }
    await File.WriteAllTextAsync(Path.Combine(directory, "updates-state.json"), "{}");
    using (var service = Create(new FakeHandler { Body = Envelope(7) }))
    {
        await service.CheckOnceAsync();
        Assert(service.Status == "error" && notifications == 1, "corrupted state fails closed");
    }
    var foreignState = JsonNode.Parse(savedState)!;
    foreignState["key"] = new string('0', 64);
    await File.WriteAllTextAsync(Path.Combine(directory, "updates-state.json"), foreignState.ToJsonString());
    using (var service = Create(new FakeHandler { Body = Envelope(7) }))
    {
        await service.CheckOnceAsync();
        Assert(service.Status == "error" && notifications == 1, "persisted signing key change fails closed");
    }
    File.Delete(Path.Combine(directory, "updates-state.json"));

    // Anyone able to write updates.json must not be able to swap the trust anchor.
    var attackerKey = PublicKeyBlob(ECDsa.Create(ECCurve.NamedCurves.nistP256));
    var callsBefore = handler.Calls;
    await Configure(configuredKey: attackerKey);
    using (var pinnedService = Create())
    {
        await pinnedService.CheckOnceAsync();
        Assert(pinnedService.Status == "key-mismatch" && handler.Calls == callsBefore,
            "configured key must match the pinned key");
    }
    await Configure();
    var blocking = new FakeHandler { Block = true };
    using (var service = Create(blocking))
    {
        service.StartForeground();
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.StartForeground();
        Assert(blocking.Calls == 1, "duplicate foreground activation keeps one loop");
        service.StopForeground();
        await blocking.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(notifications == 1 && !File.Exists(Path.Combine(directory, "updates-state.json")), "background cancels HTTP without notification or persistence");
        blocking.Block = false;
        blocking.Body = Envelope(8);
        service.StartForeground();
        await blocking.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 100 && notifications == 1; i++) await Task.Delay(20);
        Assert(notifications == 2, "foreground resume restarts checking");
        service.StopForeground();
    }
}
finally { Directory.Delete(directory, true); }
Console.WriteLine($"All {checks} checks passed.");

sealed class FakeHandler : HttpMessageHandler
{
    public byte[] Body { get; set; } = [];
    public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;
    public string MediaType { get; set; } = "application/json";
    public volatile bool Block;
    public int Calls;
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        Entered.TrySetResult();
        if (Block)
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
        }
        var response = new HttpResponseMessage(ResponseStatus) { Content = new ByteArrayContent(Body) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(MediaType);
        Completed.TrySetResult();
        return response;
    }
}

// Only the MAUI platform path is substituted; all verification and persistence code is linked.
static class FileSystem
{
    public static string AppDataDirectory => throw new InvalidOperationException("Tests must supply an isolated directory.");
}
