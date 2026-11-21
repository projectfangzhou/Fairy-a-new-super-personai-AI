using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyAiAssistant.Services;
using System.Security.Cryptography;

var now = DateTimeOffset.UtcNow;
using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var publicKey = PublicKeyBlob(signingKey);

static string PublicKeyBlob(ECDsa key)
{
    var parameters = key.ExportParameters(includePrivateParameters: false);
    var blob = new byte[SignedUpdateManifest.PublicKeyBytes];
    blob[0] = 0x04;
    parameters.Q.X!.CopyTo(blob, 1);
    parameters.Q.Y!.CopyTo(blob, 33);
    return Convert.ToBase64String(blob);
}

static string Sign(ECDsa key, byte[] message) => Convert.ToBase64String(
    key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}
byte[] Canonical(JsonNode node, string? omit = null)
{
    using var document = JsonDocument.Parse(node.ToJsonString());
    return SignedUpdateManifest.Canonical(document.RootElement, omit);
}
void SignEnvelope(JsonObject envelope, ECDsa? key = null)
{
    envelope["signature"] = Sign(key ?? signingKey, Canonical(envelope["manifest"]!));
}
JsonObject Envelope(long sequence = 2, string version = "2.1.0", DateTimeOffset? expires = null, ECDsa? key = null)
{
    var release = new JsonObject
    {
        ["platform"] = "windows", ["version"] = version,
        ["url"] = "https://downloads.fairyai.com/FairyAI.exe", ["sha256"] = new string('a', 64)
    };
    release["signature"] = Sign(key ?? signingKey, Canonical(release));
    var manifest = new JsonObject
    {
        ["schemaVersion"] = 1, ["sequence"] = sequence,
        ["issuedAt"] = now.AddMinutes(-5).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
        ["expiresAt"] = (expires ?? now.AddDays(1)).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
        ["releases"] = new JsonArray(release)
    };
    var envelope = new JsonObject { ["manifest"] = manifest };
    SignEnvelope(envelope, key);
    return envelope;
}
byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());
void Reject(JsonObject envelope, string name)
{
    var rejected = false;
    try { SignedUpdateManifest.Verify(Bytes(envelope), publicKey, now); }
    catch (Exception) { rejected = true; }
    Assert(rejected, name);
}
var valid = Envelope();
Assert(SignedUpdateManifest.Verify(Bytes(valid), publicKey, now).WindowsVersion == "2.1.0", "a real signature validates");
valid["releases"] = new JsonArray(new JsonObject { ["version"] = "999.0" });
Assert(SignedUpdateManifest.Verify(Bytes(valid), publicKey, now).WindowsVersion == "2.1.0", "ignore unsigned top-level releases");
var tampered = Envelope();
tampered["manifest"]!["sequence"] = 3;
Reject(tampered, "envelope tamper");
tampered = Envelope();
tampered["manifest"]!["releases"]![0]!["version"] = "9.0.0";
SignEnvelope(tampered);
Reject(tampered, "entry tamper even with valid envelope signature");
Reject(Envelope(expires: now.AddMinutes(-1)), "expired");
tampered = Envelope();
tampered["manifest"]!["issuedAt"] = now.AddMinutes(2).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
SignEnvelope(tampered);
Reject(tampered, "future issuance");
Reject(Envelope(expires: now.AddDays(91)), "excess lifetime");
using (var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)) Reject(Envelope(key: wrongKey), "wrong key");
tampered = Envelope();
tampered["manifest"]!["releases"]![0]!["url"] = "http://downloads.fairyai.com/app.exe";
SignEnvelope(tampered);
Reject(tampered, "unsafe release URL");
tampered = Envelope(sequence: SignedUpdateManifest.MaxSequence + 1);
Reject(tampered, "unsafe integer");
try
{
    using var duplicate = SignedUpdateManifest.Parse(Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}"));
    throw new Exception("duplicate JSON accepted");
}
catch (InvalidDataException) { checks++; }
using (var canonicalDocument = JsonDocument.Parse("{\"z\":\"汉字<&/\",\"a\":1}"))
    Assert(Encoding.UTF8.GetString(SignedUpdateManifest.Canonical(canonicalDocument.RootElement)) == "{\"a\":1,\"z\":\"汉字<&/\"}", "JS-compatible string canonicalization");
Assert(SignedUpdateService.IsNewer("2.0.0", "2.0.0.0") == false, "normalized version equality");
Assert(SignedUpdateService.IsNewer("2.1.0", "2.0.0.0") == true, "newer version");
Assert(SignedUpdateService.IsNewer("2.1.0-preview", "2.0") is null, "unsupported version fails closed");
Assert(SignedUpdateService.CheckInterval == TimeSpan.FromHours(6), "six-hour interval");
var directory = Path.Combine(Path.GetTempPath(), "fairyai-updates-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var handler = new FakeHandler { Body = Bytes(Envelope()) };
    using (var service = new SignedUpdateService("2.0.0", directory, handler, publicKey))
    {
        await service.CheckOnceAsync();
        Assert(service.Status == "disabled" && handler.Requests == 0, "unconfigured performs no network");
        await File.WriteAllTextAsync(Path.Combine(directory, "updates.json"), JsonSerializer.Serialize(new
        {
            manifest_url = "http://updates.fairyai.com/api/releases", public_key = publicKey
        }));
        await service.CheckOnceAsync();
        Assert(service.Status == "error" && handler.Requests == 0, "HTTP config performs no network");
        await File.WriteAllTextAsync(Path.Combine(directory, "updates.json"), JsonSerializer.Serialize(new
        {
            manifest_url = "https://updates.fairyai.com/api/releases", public_key = publicKey
        }));
        var notifications = 0;
        service.UpdateAvailable += _ => notifications++;
        await service.CheckOnceAsync();
        Assert(service.Status == "available" && notifications == 1 && File.Exists(Path.Combine(directory, "updates-state.json")), "persist before notification");
        await service.CheckOnceAsync();
        Assert(service.Status == "available" && notifications == 1, "identical manifest accepted without repeat notice");
        handler.Body = Bytes(Envelope(sequence: 1));
        await service.CheckOnceAsync();
        Assert(service.Status == "error" && notifications == 1, "reject rollback");
        handler.Body = Bytes(Envelope(version: "2.2.0"));
        await service.CheckOnceAsync();
        Assert(service.Status == "error", "reject same sequence different digest");
        handler.Body = Bytes(Envelope(sequence: 3, version: "2.2.0"));
        await service.CheckOnceAsync();
        Assert(service.Status == "available" && notifications == 2, "higher sequence accepted");
        handler.Code = HttpStatusCode.Redirect;
        await service.CheckOnceAsync();
        Assert(service.Status == "error", "redirect rejected");
        handler.Code = HttpStatusCode.OK;
        handler.Body = new byte[SignedUpdateManifest.MaxBytes + 1];
        await service.CheckOnceAsync();
        Assert(service.Status == "error", "oversized response rejected");
    }
    using (var restarted = new SignedUpdateService("2.0.0", directory, new FakeHandler { Body = Bytes(Envelope()) }, publicKey))
    {
        await restarted.CheckOnceAsync();
        Assert(restarted.Status == "error", "rollback protection survives restart");
    }
    using (var replacementKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "updates.json"), JsonSerializer.Serialize(new
        {
            manifest_url = "https://updates.fairyai.com/api/releases",
            public_key = PublicKeyBlob(replacementKey)
        }));
        using var changedKey = new SignedUpdateService("2.0.0", directory,
            new FakeHandler { Body = Bytes(Envelope(sequence: 4, key: replacementKey)) },
            PublicKeyBlob(replacementKey));
        await changedKey.CheckOnceAsync();
        Assert(changedKey.Status == "error", "key rotation requires explicit state reset");
    }
    await File.WriteAllTextAsync(Path.Combine(directory, "updates.json"), JsonSerializer.Serialize(new
    {
        manifest_url = "https://updates.fairyai.com/api/releases", public_key = publicKey
    }));
    await File.WriteAllTextAsync(Path.Combine(directory, "updates-state.json"), "{}");
    using (var damaged = new SignedUpdateService("2.0.0", directory, new FakeHandler { Body = Bytes(Envelope(sequence: 4)) }, publicKey))
    {
        await damaged.CheckOnceAsync();
        Assert(damaged.Status == "error", "damaged state fails closed");
    }
    var blocking = new BlockingHandler();
    using (var canceled = new SignedUpdateService("2.0.0", directory, blocking, publicKey))
    {
        var pending = canceled.CheckOnceAsync();
        await blocking.Started.Task;
        canceled.Dispose();
        try { await pending; throw new Exception("cancellation ignored"); }
        catch (OperationCanceledException) { checks++; }
    }

    // Anyone able to write updates.json must not be able to swap the trust anchor.
    using (var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "updates.json"), JsonSerializer.Serialize(new
        {
            manifest_url = "https://updates.fairyai.com/api/releases",
            public_key = PublicKeyBlob(attackerKey)
        }));
        var pinHandler = new FakeHandler { Body = Bytes(Envelope(sequence: 9, key: attackerKey)) };
        using var pinned = new SignedUpdateService("2.0.0", directory, pinHandler, publicKey);
        await pinned.CheckOnceAsync();
        Assert(pinned.Status == "key-mismatch" && pinHandler.Requests == 0, "configured key must match the pinned key");
    }
}
finally { Directory.Delete(directory, true); }
Console.WriteLine($"PASS: {checks} signed-update checks");

sealed class FakeHandler : HttpMessageHandler
{
    public byte[] Body { get; set; } = [];
    public HttpStatusCode Code { get; set; } = HttpStatusCode.OK;
    public int Requests { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests++;
        if (request.Headers.Authorization is not null) throw new Exception("Unexpected credentials");
        var response = new HttpResponseMessage(Code) { Content = new ByteArrayContent(Body) };
        response.Content.Headers.ContentType = new("application/json");
        return Task.FromResult(response);
    }
}
sealed class BlockingHandler : HttpMessageHandler
{
    public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Started.TrySetResult(true);
        await Task.Delay(Timeout.Infinite, token);
        throw new InvalidOperationException();
    }
}
