using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyAiAssistant.Services;

/// <summary>
/// The tool contract the agent sees. Deliberately dependency-free: the registry, the schemas it
/// serialises and the MCP adapter can all be exercised without dragging in the concrete tools.
/// </summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }
    List<ToolParameter> Parameters { get; }
    Task<string> ExecuteAsync(JsonObject args);
}

public class ToolParameter
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string";
    public string Description { get; set; } = "";
    public bool Required { get; set; } = true;
}

public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    public void Register(ITool tool) => _tools[tool.Name] = tool;

    /// <summary>Takes a tool back out. Used when an optional backend is stopped, so a dead server
    /// cannot keep advertising functions the agent would then fail to call.</summary>
    public bool Unregister(string name) => _tools.Remove(name);
    public ITool? GetTool(string name) => _tools.TryGetValue(name, out var t) ? t : null;
    public IReadOnlyCollection<ITool> GetAll() => _tools.Values;

    public string GetToolsJson()
    {
        var tools = _tools.Values.Select(tool => new
        {
            type = "function",
            function = new
            {
                name = tool.Name,
                description = tool.Description,
                parameters = new
                {
                    type = "object",
                    properties = tool.Parameters.ToDictionary(p => p.Name, p => new { type = p.Type, description = p.Description }),
                    required = tool.Parameters.Where(p => p.Required).Select(p => p.Name).ToList()
                }
            }
        });
        return JsonSerializer.Serialize(tools);
    }

    public async Task<string> ExecuteToolCallAsync(string functionName, string argumentsJson)
    {
        var tool = GetTool(functionName);
        if (tool == null) return $"Error: unknown function '{functionName}'";
        try
        {
            var args = JsonSerializer.Deserialize<JsonObject>(argumentsJson) ?? new();
            return await tool.ExecuteAsync(args);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
