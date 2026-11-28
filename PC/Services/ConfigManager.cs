using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyAiAssistant.Services;

public class AppConfig
{
    public LLMConfig LLM { get; set; } = new();
    public LLMConfig FallbackLLM { get; set; } = new();
    public ASRConfig ASR { get; set; } = new();
    public TTSConfig TTS { get; set; } = new();
    public VisionConfig Vision { get; set; } = new();
    public LocalVisionConfig LocalVision { get; set; } = new();
    public TavilyConfig Tavily { get; set; } = new();
    public Live2DConfig Live2D { get; set; } = new();
    public SyncConfig Sync { get; set; } = new();
    public RelayQuotaConfig Relay { get; set; } = new();
    public LocalSendConfig LocalSend { get; set; } = new();
    public PairingConfig Pairing { get; set; } = new();
    public NotificationConfig Notifications { get; set; } = new();
    public HomeAssistantConfig HomeAssistant { get; set; } = new();
    public McpConfig Mcp { get; set; } = new();
    public NatTraversalConfig NatTraversal { get; set; } = new();
    public PersonalityConfig Personality { get; set; } = new();
    public CustomPlatformConfig CustomPlatform { get; set; } = new();
    public WakeWordConfig WakeWord { get; set; } = new();
    public ContextConfig Context { get; set; } = new();
    public PerformanceConfig Performance { get; set; } = new();
    public IslandUIConfig IslandUI { get; set; } = new();

    /// <summary>外观：主题与相关偏好。</summary>
    public AppearanceConfig Appearance { get; set; } = new();

    /// <summary>Configured chat-platform accounts. Secrets are stored encrypted per account.</summary>
    public List<Messaging.MessagingAccount> MessagingAccounts { get; set; } = new();
}

/// <summary>外观设置。</summary>
public class AppearanceConfig
{
    /// <summary>主题名（FairyTheme 枚举）。空值按墨黑处理。</summary>
    public string Theme { get; set; } = "MonoDark";

    /// <summary>是否启用玻璃层。关掉后退回纯色面，给低性能设备留一条路。</summary>
    public bool GlassEnabled { get; set; } = true;
}

/// <summary>
/// AI personality configuration.
/// </summary>
public class PersonalityConfig
{
    public string Name { get; set; } = "Fairy";
    public string SystemPrompt { get; set; } = "你是 Fairy，一个温柔、友善的AI助手。你说话亲切自然，像朋友一样和用户交流。你喜欢用轻松的语气回答问题，偶尔会关心用户的状态。";
    public string AvatarUrl { get; set; } = "";
    public string Language { get; set; } = "zh-CN";
    public bool EnableEmotion { get; set; } = true;
    public string IdleSoundStyle { get; set; } = "gentle";
    public bool IsGameCharacter { get; set; } = false;
    public string GameCharacterName { get; set; } = "";
}

public class LLMConfig
{
    public string Provider { get; set; } = "Kimi";
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.moonshot.cn/v1/chat/completions";
    public string Model { get; set; } = "kimi-k2.6";
    public bool RequireModelName { get; set; } = true;
}

public class WakeWordConfig
{
    public string WakeWord { get; set; } = "Fairy";
    public bool VoiceprintEnabled { get; set; } = false;
    public string VoiceprintEmbeddingPath { get; set; } = "";
    public float VoiceprintThreshold { get; set; } = 0.75f;
}

public class ContextConfig
{
    public bool Enabled { get; set; } = true;
    public int MaxTokens { get; set; } = 256000;
    public int AutoCompressTokens { get; set; } = 256000;
    public string LastSyncDate { get; set; } = "";
}

public class PerformanceConfig
{
    /// <summary>high / balanced / low</summary>
    public string Mode { get; set; } = "balanced";
    public bool AutoDetectGames { get; set; } = true;
}

public class IslandUIConfig
{
    public bool ShowInputBox { get; set; } = true;
    /// <summary>left-bottom / right-bottom / center-bottom</summary>
    public string InputBoxPosition { get; set; } = "right-bottom";
}

public class ASRConfig
{
    public string Provider { get; set; } = "MiMo";
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.xiaomimimo.com/v1/audio/transcriptions";
    /// <summary>Additional ASR providers with their endpoints.</summary>
    public Dictionary<string, AsrPreset> Providers { get; set; } = new();
}

public class AsrPreset
{
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
}

public class TTSConfig
{
    public string Provider { get; set; } = "MiMo";
    public string ApiKey { get; set; } = "";
    public string Voice { get; set; } = "茉莉";
    public string BaseUrl { get; set; } = "https://api.xiaomimimo.com/v1/audio/speech";
    public string Model { get; set; } = "mi-tts-v2.5";
    /// <summary>GPT-SoVITS voice clone reference audio file path.</summary>
    public string VoiceCloneAudioPath { get; set; } = "";
    /// <summary>GPT-SoVITS prompt text (optional, description of the reference audio).</summary>
    public string VoiceClonePromptText { get; set; } = "";
    /// <summary>GPT-SoVITS text language for reference audio: zh / en / ja.</summary>
    public string VoiceCloneLang { get; set; } = "zh";
    /// <summary>Path to ffmpeg executable (for audio format conversion).</summary>
    public string FfmpegPath { get; set; } = "";
    /// <summary>Additional TTS providers with their endpoints.</summary>
    public Dictionary<string, TtsPreset> Providers { get; set; } = new();
}

public class TtsPreset
{
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
}

public class VisionConfig
{
    public string Provider { get; set; } = "OpenAI";
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string Model { get; set; } = "gpt-4o-mini";
    /// <summary>Additional vision providers with their endpoints.</summary>
    public Dictionary<string, VisionPreset> Providers { get; set; } = new();
}

public class VisionPreset
{
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>Optional endpoint override for image uploads (e.g. Google files API).</summary>
    public string? ImageEndpoint { get; set; }
}

public class LocalVisionConfig
{
    public bool Enabled { get; set; } = false;
    public string ModelPath { get; set; } = "";
    public string ModelName { get; set; } = "llava-phi3";
    public int MaxTokens { get; set; } = 2048;
    public int Threads { get; set; } = 4;
}

public class TavilyConfig
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.tavily.com/search";
    public string Model { get; set; } = "basic";
    /// <summary>Additional web search providers with their endpoints.</summary>
    public Dictionary<string, WebSearchPreset> Providers { get; set; } = new();
}

public class WebSearchPreset
{
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
}

public class Live2DConfig
{
    public bool Enabled { get; set; } = false;
    public string ModelFolder { get; set; } = "";
    public string ModelJsonPath { get; set; } = "";
    public int WindowWidth { get; set; } = 350;
    public int WindowHeight { get; set; } = 500;
    public double Opacity { get; set; } = 1.0;
    public bool Topmost { get; set; } = true;
    public bool ClickThrough { get; set; } = false;
}

public class SyncConfig
{
    public bool Enabled { get; set; } = false;
    public string DeviceName { get; set; } = "FairyAI-PC";
    public string PairingCode { get; set; } = "";
    public bool EnableBluetooth { get; set; } = true;
    public bool EnableLan { get; set; } = true;
    public bool EnableSignalR { get; set; } = false;
    public string SignalRUrl { get; set; } = "https://fairyai-sync.azurewebsites.net/hub";
    public int LanPort { get; set; } = 9876;
    public int FileServerPort { get; set; } = 9877;

    /// <summary>
    /// Let a paired phone read task summaries. Off by default: it is a deliberate opt-in, and even
    /// then only the summary shape leaves this machine (no prompts, no outputs, no file paths).
    /// </summary>
    public bool SummarySyncEnabled { get; set; } = false;

    /// <summary>
    /// Let a paired phone see tasks and answer pending approvals. Off by default and separate from
    /// the summary feed: answering approvals from the phone is a bigger permission than reading them.
    /// </summary>
    public bool PairControlEnabled { get; set; } = false;

    /// <summary>
    /// Let a paired phone sync notes with this machine. Off by default and separate from both
    /// switches above: the user's own notes are not something to share because task titles are.
    /// </summary>
    public bool NotesSyncEnabled { get; set; } = false;
}

/// <summary>
/// Limits for relayed shares. The defaults are the public free-tier figures, deliberately below
/// R2's 10 GB-month storage allowance so one share cannot consume the whole month; only numbers
/// the user verified against their own account should raise them.
/// </summary>
public class RelayQuotaConfig
{
    public bool UserConfigured { get; set; } = false;
    public long SingleFileLimitBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public long ShareTotalLimitBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    public long StorageBudgetBytes { get; set; } = 10L * 1024 * 1024 * 1024;
}

/// <summary>
/// LAN receiving. Off by default: listening on all interfaces is an outside-facing port, so it
/// is only opened when the user turns it on.
/// </summary>
public class LocalSendConfig
{
    public bool Enabled { get; set; } = false;
    public string Alias { get; set; } = "仙灵";
    public string Fingerprint { get; set; } = "";
    public string SaveDirectory { get; set; } = "";
    public int Port { get; set; } = 53317;
    public long MaxFileBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public long MaxSessionBytes { get; set; } = 5L * 1024 * 1024 * 1024;
}

/// <summary>
/// Pairing. Off by default for the same reason as the LAN receiver: a device joins over a port
/// other machines can reach, so it is only opened when the user turns it on.
/// </summary>
public class PairingConfig
{
    public bool Enabled { get; set; } = false;
    public int Port { get; set; } = 9879;

    /// <summary>Label shown to the other device. Empty means use the machine name.</summary>
    public string DisplayName { get; set; } = "";
}

/// <summary>
/// Notification preferences. Quiet hours still let approvals through: staying silent about a
/// blocked task during the night would just stop the work until morning.
/// </summary>
public class NotificationConfig
{
    public bool Enabled { get; set; } = true;
    public bool TaskCompleted { get; set; } = true;
    public bool TaskFailed { get; set; } = true;
    public bool ApprovalPending { get; set; } = true;
    public bool QuestionPending { get; set; } = true;
    public bool TransferIncoming { get; set; } = true;
    public int MaxEntries { get; set; } = 200;
    public bool QuietHoursEnabled { get; set; } = false;
    public int QuietHoursStart { get; set; } = 23;
    public int QuietHoursEnd { get; set; } = 7;

    /// <summary>Ordinary notifications may be delivered by the operating system too.</summary>
    public bool SystemEnabled { get; set; } = true;

    /// <summary>Also mirror task events to the OS. Off: key task notifications stay in the app's layer.</summary>
    public bool MirrorKeyEvents { get; set; } = false;

    /// <summary>Let the OS notification carry the real text. Off: only a generic sentence leaves.</summary>
    public bool SystemDetails { get; set; } = false;
}

/// <summary>
/// Home Assistant client settings. The access token is deliberately absent: it lives in the
/// encrypted token file, so copying or syncing config.json cannot carry a credential along.
/// </summary>
public class HomeAssistantConfig
{
    public string ServerUrl { get; set; } = "";

    /// <summary>
    /// OAuth client id. Home Assistant checks the redirect address against it using IndieAuth
    /// rules (same origin), so the default is the loopback redirect itself. If an instance
    /// refuses the pair, change both here.
    /// </summary>
    public string ClientId { get; set; } = "http://127.0.0.1:8137";

    public string RedirectUri { get; set; } = "http://127.0.0.1:8137/callback";
}

/// <summary>
/// An optional MCP server as the Windows perception/action backend.
///
/// The command is a fixed path the user chose — started directly, never through a shell and never
/// resolved from a modified PATH — because the checklist requires a pinned, verified interpreter
/// rather than "whatever python is first on PATH today".
/// </summary>
public class McpConfig
{
    public bool Enabled { get; set; } = false;

    public string ServerCommand { get; set; } = "";

    /// <summary>
    /// Arguments for the server. A JSON array (<c>["-m","windows_mcp"]</c>) is read as-is;
    /// anything else is split on whitespace. No shell expansion happens either way.
    /// </summary>
    public string ServerArguments { get; set; } = "";

    public int StartupSeconds { get; set; } = 30;
}

public class NatTraversalConfig
{    public bool Enabled { get; set; } = false;
    public string Provider { get; set; } = "ngrok";
    public string AuthToken { get; set; } = "";
    public string Region { get; set; } = "ap";
    public string? PublicUrl { get; set; }
}

public static class ConfigManager
{
    private static readonly string ConfigPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "config.json");

    private static readonly Dictionary<string, (string BaseUrl, string Model)> ProviderPresets = new()
    {
        ["Kimi"] = ("https://api.moonshot.cn/v1/chat/completions", "kimi-k2.6"),
        ["DeepSeek"] = ("https://api.deepseek.com/v1/chat/completions", "deepseek-chat"),
        ["MiMo"] = ("https://api.xiaomimimo.com/v1/chat/completions", "mimo-v2.5"),
        ["OpenAI"] = ("https://api.openai.com/v1/chat/completions", "gpt-4o-mini"),
        ["Ollama"] = ("http://localhost:11434/v1/chat/completions", "llama3"),
        ["OpenRouter"] = ("https://openrouter.ai/api/v1/chat/completions", "openai/gpt-4o-mini"),
        ["Groq"] = ("https://api.groq.com/openai/v1/chat/completions", "llama-3.3-70b-versatile"),
        ["Mistral"] = ("https://api.mistral.ai/v1/chat/completions", "mistral-large-latest"),
        ["xAI"] = ("https://api.x.ai/v1/chat/completions", "grok-2-latest"),
        ["Google Gemini"] = ("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", "gemini-2.0-flash"),
        ["智谱 GLM"] = ("https://open.bigmodel.cn/api/paas/v4/chat/completions", "glm-4-plus"),
        ["通义千问"] = ("https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", "qwen-plus"),
        ["硅基流动"] = ("https://api.siliconflow.cn/v1/chat/completions", "deepseek-ai/DeepSeek-V3"),
        ["火山方舟"] = ("https://ark.cn-beijing.volces.com/api/v3/chat/completions", "doubao-pro-32k"),
        ["NVIDIA NIM"] = ("https://integrate.api.nvidia.com/v1/chat/completions", "meta/llama-3.3-70b-instruct"),
        ["零一万物"] = ("https://api.lingyiwanwu.com/v1/chat/completions", "yi-large"),
        ["自定义平台"] = ("", "custom"),
    };

    /// <summary>Visual model (screen understanding) provider presets.</summary>
    public static readonly Dictionary<string, (string BaseUrl, string Model)> VisionPresets = new()
    {
        ["OpenAI"] = ("https://api.openai.com/v1/chat/completions", "gpt-4o-mini"),
        ["Google Gemini"] = ("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", "gemini-2.0-flash"),
        ["DeepSeek"] = ("https://api.deepseek.com/v1/chat/completions", "deepseek-chat"),
        ["MiMo"] = ("https://api.xiaomimimo.com/v1/chat/completions", "mimo-v2.5"),
        ["Azure OpenAI"] = ("https://{resource}.openai.azure.com/openai/deployments/{deployment}/chat/completions", "gpt-4o-mini"),
        ["OpenRouter"] = ("https://openrouter.ai/api/v1/chat/completions", "openai/gpt-4o-mini"),
        ["自定义平台"] = ("", "custom"),
    };

    /// <summary>ASR provider presets.</summary>
    public static readonly Dictionary<string, (string BaseUrl, string Model)> AsrPresets = new()
    {
        ["MiMo"] = ("https://api.xiaomimimo.com/v1/audio/transcriptions", "mi-v2.5-asr"),
        ["OpenAI"] = ("https://api.openai.com/v1/audio/transcriptions", "whisper-1"),
        ["Google"] = ("https://speech.googleapis.com/v1/speech:recognize", ""),
        ["Microsoft Azure"] = ("https://{region}.stt.speech.microsoft.com/speech/recognition/conversation", "zh-CN"),
    };

    /// <summary>TTS provider presets.</summary>
    public static readonly Dictionary<string, (string BaseUrl, string Model)> TtsPresets = new()
    {
        ["MiMo"] = ("https://api.xiaomimimo.com/v1/audio/speech", "mi-tts-v2.5"),
        ["OpenAI"] = ("https://api.openai.com/v1/audio/speech", "tts-1"),
        ["Microsoft Azure"] = ("https://{region}.tts.speech.microsoft.com/cognitiveservice/v1", "zh-CN-XiaoxiaoNeural"),
        ["Google"] = ("https://texttospeech.googleapis.com/v1/text:synthesize", "google-tts"),
        ["GPT-SoVITS"] = ("http://localhost:9880/tts", "gpt-sovits"),
        ["OpenRouter"] = ("https://openrouter.ai/api/v1/audio/speech", "openai/tts-1"),
        ["自定义平台"] = ("", "custom"),
    };

    /// <summary>Web search provider presets.</summary>
    public static readonly Dictionary<string, (string BaseUrl, string Model)> WebSearchPresets = new()
    {
        ["Tavily"] = ("https://api.tavily.com/search", "basic"),
        ["Google"] = ("https://www.googleapis.com/customsearch/v1", ""),
        ["Bing"] = ("https://api.bing.microsoft.com/v7.0/search", ""),
        ["DuckDuckGo"] = ("https://api.duckduckgo.com", ""),
        ["Baidu"] = ("https://www.baidu.com/s", ""),
    };

    /// <summary>Local vision model presets (for offline use).</summary>
    public static readonly Dictionary<string, (string ModelPath, string ModelName)> LocalVisionPresets = new()
    {
        ["llava-phi3"] = ("models/llava-phi3-Q4_K_M.gguf", "llava-phi3"),
        ["llava-1.5-7b"] = ("models/llava-1.5-7b-Q4_K_M.gguf", "llava-1.5-7b"),
        ["bakllava"] = ("models/bakllava-Q4_K_M.gguf", "bakllava"),
    };

    public static AppConfig Load()
    {
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                config.LLM.ApiKey = ApiKeyProtector.Unprotect(config.LLM.ApiKey);
                config.FallbackLLM.ApiKey = ApiKeyProtector.Unprotect(config.FallbackLLM.ApiKey);
                config.ASR.ApiKey = ApiKeyProtector.Unprotect(config.ASR.ApiKey);
                config.TTS.ApiKey = ApiKeyProtector.Unprotect(config.TTS.ApiKey);
                config.Vision.ApiKey = ApiKeyProtector.Unprotect(config.Vision.ApiKey);
                config.Tavily.ApiKey = ApiKeyProtector.Unprotect(config.Tavily.ApiKey);
                config.CustomPlatform.TextApiKey = ApiKeyProtector.Unprotect(config.CustomPlatform.TextApiKey);
                config.CustomPlatform.SpeechApiKey = ApiKeyProtector.Unprotect(config.CustomPlatform.SpeechApiKey);
                config.CustomPlatform.VisionApiKey = ApiKeyProtector.Unprotect(config.CustomPlatform.VisionApiKey);
                config.CustomPlatform.MultimodalApiKey = ApiKeyProtector.Unprotect(config.CustomPlatform.MultimodalApiKey);
                return config;
            }
            catch { }
        }
        return new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        // Encrypt a copy, never the caller's object. The app keeps using the object it passed in to
        // talk to providers, so encrypting it in place would send a ciphertext as the key — and a
        // second Save would encrypt the ciphertext again, which no Load could undo.
        var stored = Snapshot(config);

        stored.LLM.ApiKey = ApiKeyProtector.Protect(stored.LLM.ApiKey);
        stored.FallbackLLM.ApiKey = ApiKeyProtector.Protect(stored.FallbackLLM.ApiKey);
        stored.ASR.ApiKey = ApiKeyProtector.Protect(stored.ASR.ApiKey);
        stored.TTS.ApiKey = ApiKeyProtector.Protect(stored.TTS.ApiKey);
        stored.Vision.ApiKey = ApiKeyProtector.Protect(stored.Vision.ApiKey);
        stored.Tavily.ApiKey = ApiKeyProtector.Protect(stored.Tavily.ApiKey);
        stored.CustomPlatform.TextApiKey = ApiKeyProtector.Protect(stored.CustomPlatform.TextApiKey);
        stored.CustomPlatform.SpeechApiKey = ApiKeyProtector.Protect(stored.CustomPlatform.SpeechApiKey);
        stored.CustomPlatform.VisionApiKey = ApiKeyProtector.Protect(stored.CustomPlatform.VisionApiKey);
        stored.CustomPlatform.MultimodalApiKey = ApiKeyProtector.Protect(stored.CustomPlatform.MultimodalApiKey);

        var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }

    /// <summary>A detached copy, so protecting one of them cannot reach back into the caller.</summary>
    private static AppConfig Snapshot(AppConfig config) =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config)) ?? new AppConfig();



    public static bool NeedsSetup()
    {
        if (!File.Exists(ConfigPath)) return true;
        var config = Load();
        return string.IsNullOrWhiteSpace(config.LLM.ApiKey);
    }

    public static (string BaseUrl, string Model)? GetProviderInfo(string providerName)
    {
        return ProviderPresets.TryGetValue(providerName, out var info) ? info : null;
    }

    public static List<string> GetProviderNames() => ProviderPresets.Keys.ToList();

    public static (string BaseUrl, string Model)? GetVisionProviderInfo(string providerName)
    {
        return VisionPresets.TryGetValue(providerName, out var info) ? info : null;
    }

    public static (string BaseUrl, string Model)? GetAsrProviderInfo(string providerName)
    {
        return AsrPresets.TryGetValue(providerName, out var info) ? info : null;
    }

    public static (string BaseUrl, string Model)? GetTtsProviderInfo(string providerName)
    {
        return TtsPresets.TryGetValue(providerName, out var info) ? info : null;
    }

    public static List<string> GetVisionProviderNames() => VisionPresets.Keys.ToList();
    public static List<string> GetAsrProviderNames() => AsrPresets.Keys.ToList();
    public static List<string> GetTtsProviderNames() => TtsPresets.Keys.ToList();
    public static List<string> GetLocalVisionModelNames() => LocalVisionPresets.Keys.ToList();

    public static (string ModelPath, string ModelName)? GetLocalVisionModelInfo(string modelName)
    {
        return LocalVisionPresets.TryGetValue(modelName, out var info) ? info : null;
    }

    public static bool HasLive2D()
    {
        var config = Load();
        return config.Live2D.Enabled && !string.IsNullOrWhiteSpace(config.Live2D.ModelJsonPath)
               && File.Exists(config.Live2D.ModelJsonPath);
    }

    /// <summary>Check if fallback LLM is configured.</summary>
    public static bool HasFallbackLLM()
    {
        var config = Load();
        return !string.IsNullOrWhiteSpace(config.FallbackLLM.ApiKey) &&
               !string.IsNullOrWhiteSpace(config.FallbackLLM.BaseUrl);
    }

    /// <summary>Get fallback LLM config, returns null if not configured.</summary>
    public static LLMConfig? GetFallbackLLMConfig()
    {
        var config = Load();
        if (string.IsNullOrWhiteSpace(config.FallbackLLM.ApiKey) ||
            string.IsNullOrWhiteSpace(config.FallbackLLM.BaseUrl))
            return null;
        return config.FallbackLLM;
    }

    /// <summary>
    /// Generate a 6-digit pairing code for device verification.
    /// </summary>
    public static string GeneratePairingCode()
    {
        // Use rejection sampling to eliminate modulo bias
        uint value;
        do
        {
            var bytes = new byte[4];
            RandomNumberGenerator.Fill(bytes);
            value = BitConverter.ToUInt32(bytes);
        } while (value >= uint.MaxValue - (uint.MaxValue % 1000000));
        return (value % 1000000).ToString("D6");
    }

    /// <summary>
    /// Hash a pairing code for secure storage.
    /// </summary>
    /// <summary>Generate or load a random per-device salt (not hardcoded).</summary>
    private static string GetOrCreateSalt()
    {
        var saltPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pairing.salt");
        if (File.Exists(saltPath))
            return File.ReadAllText(saltPath);
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(saltPath, salt);
        File.SetAttributes(saltPath, FileAttributes.Hidden | FileAttributes.System);
        return salt;
    }

    /// <summary>
    /// Hash a pairing code using a random per-device salt.
    /// Salt is generated on first use and stored locally.
    /// </summary>
    public static string HashPairingCode(string code)
    {
        var salt = GetOrCreateSalt();
        var saltedBytes = Encoding.UTF8.GetBytes(code + salt);
        var hash = SHA256.HashData(saltedBytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Verify a pairing code against a stored hash.
    /// </summary>
    public static bool VerifyPairingCode(string code, string storedHash)
    {
        // Use constant-time comparison to prevent timing attacks
        var computed = Convert.FromHexString(HashPairingCode(code));
        var stored = Convert.FromHexString(storedHash);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(computed, stored);
    }

    /// <summary>
    /// Export config for sync (personality + provider info, no API keys).
    /// </summary>
    public static string ExportForSync()
    {
        var config = Load();
        var syncData = new
        {
            personality = config.Personality,
            llm = new { config.LLM.Provider, config.LLM.BaseUrl, config.LLM.Model },
            tts = new { config.TTS.Provider, config.TTS.BaseUrl, config.TTS.Model, config.TTS.Voice },
            sync = new { config.Sync.DeviceName }
        };
        return JsonSerializer.Serialize(syncData, new JsonSerializerOptions { WriteIndented = true });
    }
}
