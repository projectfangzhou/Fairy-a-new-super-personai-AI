using System.Text.Json;
using MyAiAssistant.Services;

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
}

Check(DeepSeekHarnessService.NormalizeBaseUrl("https://gateway.example/v1/chat/completions/") == "https://gateway.example/v1", "Remove chat completion suffix");
Check(DeepSeekHarnessService.NormalizeBaseUrl("https://gateway.example/custom/v2/") == "https://gateway.example/custom/v2", "Preserve custom base path");
Check(DeepSeekHarnessService.NormalizeBaseUrl("http://127.0.0.1:8080/v1/chat/completions") == "http://127.0.0.1:8080/v1", "Allow local HTTP");
foreach (var url in new[] { "http://gateway.example/v1", "https://user:secret@gateway.example/v1", "https://gateway.example/v1?key=secret", "https://gateway.example/v1#token", "not a URI" })
{
    var rejected = false;
    try { DeepSeekHarnessService.NormalizeBaseUrl(url); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, "Reject unsafe endpoint: " + url);
}

using var patch = JsonDocument.Parse(DeepSeekHarnessService.CreatePatch("https://gateway.example/v1", "model\"quoted", @"C:\work"));
var rows = patch.RootElement.EnumerateArray().ToArray();
foreach (var id in new[] { "llm-deepseek", "session-log-deepseek", "persistent-bash", "persistent-pwsh", "terminal-bash", "terminal-pwsh", "mcp-resources" })
    Check(rows.Any(row => row.TryGetProperty("id", out var name) && name.GetString() == id && row.GetProperty("disabled").GetBoolean()), "Disable " + id);
var sandbox = rows.Single(row => row.TryGetProperty("id", out var name) && name.GetString() == "sandbox-policy").GetProperty("config");
Check(sandbox.GetProperty("mode").GetString() == "read-only", "Read only policy");
Check(sandbox.GetProperty("workspaceRoot").GetString() == @"C:\work", "Explicit workspace root");
var provider = rows.Single(row => row.TryGetProperty("insert", out _)).GetProperty("insert")[0];
Check(provider.GetProperty("name").GetString() == "@deepseek-ai/dsh-llm-pi-ai", "Actually insert adapter in minimal profile");
var route = provider.GetProperty("config").GetProperty("providers").GetProperty("fairy-gateway");
Check(route.GetProperty("apiKeyEnv").GetString() == "FAIRY_DSH_API_KEY", "Reference environment credential");
Check(!route.TryGetProperty("apiKey", out _), "No inline key in patch");
Check(route.GetProperty("models")[0].GetProperty("id").GetString() == "model\"quoted", "Model serialized safely");
var root = Path.Combine(Path.GetTempPath(), "Fairy Harness launch tests " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "lib"));
try
{
    var dsh = Path.Combine(root, "dsh.exe");
    var node = Path.Combine(root, "node.exe");
    var entry = Path.Combine(root, "lib", "bin.js");
    var patchPath = Path.Combine(root, "profile with spaces.yml");
    foreach (var path in new[] { dsh, node, entry, Path.Combine(root, "other.exe"), Path.Combine(root, "dsh.cmd"), Path.Combine(root, "lib", "bin.ts") })
        File.WriteAllText(path, "");
    var direct = DeepSeekHarnessService.CreateStartInfo(new(dsh, root, "text"), patchPath);
    Check(direct.FileName == dsh && direct.WorkingDirectory == root, "Direct executable and workspace");
    Check(direct.ArgumentList.SequenceEqual(new[] { "--profile", "sdk-minimal", "--patch", patchPath }), "Direct profile argument boundaries");
    var hosted = DeepSeekHarnessService.CreateStartInfo(new(node, root, "text", entry), patchPath);
    Check(hosted.FileName == node, "Explicit Node executable");
    Check(hosted.ArgumentList.SequenceEqual(new[] { entry, "--profile", "sdk-minimal", "--patch", patchPath }), "Node entry precedes profile arguments without shell quoting");
    Check(!hosted.UseShellExecute && hosted.RedirectStandardInput && hosted.RedirectStandardOutput && hosted.RedirectStandardError, "JSON-RPC uses redirected pipes without shell");
    foreach (var request in new HarnessRequest[]
    {
        new(node, root, "text"),
        new(dsh, root, "text", entry),
        new(Path.Combine(root, "other.exe"), root, "text"),
        new(Path.Combine(root, "dsh.cmd"), root, "text"),
        new(node, root, "text", Path.Combine(root, "lib", "bin.ts")),
        new(node, root, "text", "lib/bin.js"),
        new(node, root, "text", Path.Combine(root, "missing", "lib", "bin.js")),
        new(dsh, Path.Combine(root, "missing"), "text"),
        new("dsh.exe", root, "text")
    })
    {
        var rejected = false;
        try { DeepSeekHarnessService.CreateStartInfo(request, patchPath); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "Reject unsupported launch: " + request);
    }
}
finally
{
    Directory.Delete(root, recursive: true);
}
Console.WriteLine("Harness endpoint, profile and executable/Node launch tests passed.");

namespace MyAiAssistant.Services
{
    internal sealed class TestConfig
    {
        public TestLlm LLM { get; } = new();
    }
    internal sealed class TestLlm
    {
        public string BaseUrl { get; set; } = "";
        public string Model { get; set; } = "";
        public string ApiKey { get; set; } = "";
    }
    internal static class ConfigManager
    {
        public static TestConfig Load() => new();
    }
}
