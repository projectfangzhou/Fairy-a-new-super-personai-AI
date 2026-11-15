namespace MyAiAssistant.Services.Dashboard;

/// <summary>
/// Bridges the control centre to the on-disk configuration. Reads hand back a redacted
/// snapshot (never the stored keys) and writes only the fields the user actually filled in.
/// </summary>
public sealed class ConfigSettingsStore : ISettingsStore
{
    public SettingsSnapshot Read()
    {
        var config = ConfigManager.Load();
        return new SettingsSnapshot(
            LlmEndpoint(config.LLM),
            LlmEndpoint(config.FallbackLLM),
            AsrEndpoint(config.ASR),
            TtsEndpoint(config.TTS),
            VisionEndpoint(config.Vision),
            config.Personality.Name,
            config.Personality.SystemPrompt,
            config.WakeWord.WakeWord);
    }

    public void Update(SettingsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var config = ConfigManager.Load();

        ApplyLlm(config.LLM, update.Llm);
        ApplyLlm(config.FallbackLLM, update.FallbackLlm);
        ApplyAsr(config.ASR, update.Asr);
        ApplyTts(config.TTS, update.Tts);
        ApplyVision(config.Vision, update.Vision);

        if (update.PersonalityName is not null) config.Personality.Name = update.PersonalityName;
        if (update.SystemPrompt is not null) config.Personality.SystemPrompt = update.SystemPrompt;
        if (update.WakeWord is not null) config.WakeWord.WakeWord = update.WakeWord;

        ConfigManager.Save(config);
    }

    private static EndpointSettings LlmEndpoint(LLMConfig c) =>
        new(c.Provider, c.BaseUrl, c.Model, null, !string.IsNullOrEmpty(c.ApiKey));

    private static EndpointSettings AsrEndpoint(ASRConfig c) =>
        new(c.Provider, c.BaseUrl, string.Empty, null, !string.IsNullOrEmpty(c.ApiKey));

    private static EndpointSettings TtsEndpoint(TTSConfig c) =>
        new(c.Provider, c.BaseUrl, c.Model, c.Voice, !string.IsNullOrEmpty(c.ApiKey));

    private static EndpointSettings VisionEndpoint(VisionConfig c) =>
        new(c.Provider, c.BaseUrl, c.Model, null, !string.IsNullOrEmpty(c.ApiKey));

    private static void ApplyLlm(LLMConfig target, EndpointUpdate? update)
    {
        if (update is null) return;
        if (update.Provider is not null) target.Provider = update.Provider;
        if (update.BaseUrl is not null) target.BaseUrl = update.BaseUrl;
        if (update.Model is not null) target.Model = update.Model;
        if (update.ApiKey is not null) target.ApiKey = update.ApiKey;
    }

    private static void ApplyAsr(ASRConfig target, EndpointUpdate? update)
    {
        if (update is null) return;
        if (update.Provider is not null) target.Provider = update.Provider;
        if (update.BaseUrl is not null) target.BaseUrl = update.BaseUrl;
        if (update.ApiKey is not null) target.ApiKey = update.ApiKey;
    }

    private static void ApplyTts(TTSConfig target, EndpointUpdate? update)
    {
        if (update is null) return;
        if (update.Provider is not null) target.Provider = update.Provider;
        if (update.BaseUrl is not null) target.BaseUrl = update.BaseUrl;
        if (update.Model is not null) target.Model = update.Model;
        if (update.Voice is not null) target.Voice = update.Voice;
        if (update.ApiKey is not null) target.ApiKey = update.ApiKey;
    }

    private static void ApplyVision(VisionConfig target, EndpointUpdate? update)
    {
        if (update is null) return;
        if (update.Provider is not null) target.Provider = update.Provider;
        if (update.BaseUrl is not null) target.BaseUrl = update.BaseUrl;
        if (update.Model is not null) target.Model = update.Model;
        if (update.ApiKey is not null) target.ApiKey = update.ApiKey;
    }
}