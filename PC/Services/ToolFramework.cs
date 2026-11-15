using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyAiAssistant.Services;

// ===== Built-in Tools =====

public class GetCurrentTimeTool : ITool
{
    public string Name => "get_current_time";
    public string Description => "Get the current date and time";
    public List<ToolParameter> Parameters => new();
    public Task<string> ExecuteAsync(JsonObject args)
        => Task.FromResult(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss dddd"));
}

public class CalculatorTool : ITool
{
    public string Name => "calculate";
    public string Description => "Evaluate a mathematical expression";
    public List<ToolParameter> Parameters => new() { new() { Name = "expression", Description = "The math expression" } };
    public Task<string> ExecuteAsync(JsonObject args)
    {
        var expr = args["expression"]?.GetValue<string>() ?? "";
        try
        {
            var result = new System.Data.DataTable().Compute(expr, null);
            return Task.FromResult(result?.ToString() ?? "null");
        }
        catch (Exception ex) { return Task.FromResult($"Calculation error: {ex.Message}"); }
    }
}

public class SystemInfoTool : ITool
{
    public string Name => "get_system_info";
    public string Description => "Get basic system information";
    public List<ToolParameter> Parameters => new();
    public Task<string> ExecuteAsync(JsonObject args)
    {
        var info = new { os = Environment.OSVersion.ToString(), machine = Environment.MachineName, processorCount = Environment.ProcessorCount, workingSet = $"{Environment.WorkingSet / 1024 / 1024} MB" };
        return Task.FromResult(JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public class ClipboardCopyTool : ITool
{
    public string Name => "clipboard_copy";
    public string Description => "Copy text to the system clipboard";
    public List<ToolParameter> Parameters => new() { new() { Name = "text", Description = "Text to copy" } };
    public Task<string> ExecuteAsync(JsonObject args)
    {
        var text = args["text"]?.GetValue<string>() ?? "";
        System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Clipboard.SetText(text));
        return Task.FromResult($"Copied {text.Length} characters to clipboard");
    }
}

public class WebSearchTool : ITool
{
    private readonly TavilySearchService _search;
    public string Name => "web_search";
    public string Description => "Search the web for information";
    public List<ToolParameter> Parameters => new() { new() { Name = "query", Description = "Search query" } };
    public WebSearchTool(TavilySearchService search) => _search = search;
    public async Task<string> ExecuteAsync(JsonObject args)
    {
        var query = args["query"]?.GetValue<string>() ?? "";
        return await _search.SearchAsync(query);
    }
}

public class OpenAppTool : ITool
{
    private readonly IOpenClawAgentService _agent;
    public string Name => "open_application";
    public string Description => "Open an application or file";
    public List<ToolParameter> Parameters => new() { new() { Name = "name", Description = "Application name or file path" } };
    public OpenAppTool(IOpenClawAgentService agent) => _agent = agent;
    public async Task<string> ExecuteAsync(JsonObject args)
    {
        var name = args["name"]?.GetValue<string>() ?? "";
        var result = await _agent.ExecuteAsync("open " + name);
        return result.Message;
    }
}
