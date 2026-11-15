using System.Text.Json.Nodes;

namespace FairyAI_Android.Services;

/// <summary>
/// The tool contract, kept free of anything Android- or MAUI-specific so the dispatch and protocol
/// logic can be exercised without a device.
/// </summary>
public interface ITool
{
    string Name { get; }

    string Description { get; }

    List<ToolParameter> Parameters { get; }

    Task<string> ExecuteAsync(JsonObject args);
}

public sealed class ToolParameter
{
    public string Name { get; set; } = "";

    /// <summary>JSON schema type: string, number, integer, boolean.</summary>
    public string Type { get; set; } = "string";

    public string Description { get; set; } = "";

    public bool Required { get; set; } = true;
}

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (string.IsNullOrWhiteSpace(tool.Name)) throw new ArgumentException("tool has no name", nameof(tool));

        _tools[tool.Name] = tool;
    }

    public ITool? GetTool(string name) =>
        _tools.TryGetValue(name ?? "", out var tool) ? tool : null;

    public bool Unregister(string name) => _tools.Remove(name ?? "");

    public IReadOnlyCollection<ITool> GetAll() => _tools.Values;

    public int Count => _tools.Count;
}

/// <summary>One tool call exactly as the model asked for it, including the id it must be answered with.</summary>
public sealed record ToolCall(string Id, string Name, string Arguments);
