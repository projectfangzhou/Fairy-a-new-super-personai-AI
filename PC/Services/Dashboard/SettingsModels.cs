namespace MyAiAssistant.Services.Dashboard;

/// <summary>
/// One endpoint's editable settings. <see cref="HasApiKey"/> is a boolean on purpose:
/// the key itself is never read back out, matching the rule that credentials are
/// write-only from the control surface.
/// </summary>
public sealed record EndpointSettings(
    string Provider,
    string BaseUrl,
    string Model,
    string? Voice,
    bool HasApiKey);

public sealed record SettingsSnapshot(
    EndpointSettings Llm,
    EndpointSettings FallbackLlm,
    EndpointSettings Asr,
    EndpointSettings Tts,
    EndpointSettings Vision,
    string PersonalityName,
    string SystemPrompt,
    string WakeWord);

/// <summary>
/// A partial update. Null or blank fields mean "leave unchanged", so a page that only
/// edits the base URL cannot accidentally wipe the stored key.
/// </summary>
public sealed record EndpointUpdate(
    string? Provider = null,
    string? BaseUrl = null,
    string? Model = null,
    string? Voice = null,
    string? ApiKey = null);

public sealed record SettingsUpdate(
    EndpointUpdate? Llm = null,
    EndpointUpdate? FallbackLlm = null,
    EndpointUpdate? Asr = null,
    EndpointUpdate? Tts = null,
    EndpointUpdate? Vision = null,
    string? PersonalityName = null,
    string? SystemPrompt = null,
    string? WakeWord = null);

public interface ISettingsStore
{
    SettingsSnapshot Read();
    void Update(SettingsUpdate update);
}