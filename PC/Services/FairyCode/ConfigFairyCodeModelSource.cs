using MyAiAssistant.Services.Dashboard;

namespace MyAiAssistant.Services.FairyCode;

/// <summary>
/// Reads the live configuration. The key is only ever handed to the engine process's
/// environment — never to the control-centre page and never on a command line.
/// </summary>
public sealed class ConfigFairyCodeModelSource : IFairyCodeModelSource
{
    public (EndpointSettings Endpoint, string? ApiKey)? Read()
    {
        var llm = ConfigManager.Load().LLM;
        var endpoint = new EndpointSettings(
            llm.Provider, llm.BaseUrl, llm.Model, null, !string.IsNullOrEmpty(llm.ApiKey));
        return (endpoint, string.IsNullOrEmpty(llm.ApiKey) ? null : llm.ApiKey);
    }
}
