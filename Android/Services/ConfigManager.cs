using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FairyAI_Android.Services;

public class AppConfig
{
    public LLMConfig LLM { get; set; } = new();
    public LLMConfig FallbackLLM { get; set; } = new();
    public ASRConfig ASR { get; set; } = new();
    public TTSConfig TTS { get; set; } = new();
    public VisionConfig Vision { get; set; } = new();
    public SyncConfig Sync { get; set; } = new();
    public PersonalityConfig Personality { get; set; } = new();
    public WakeWordConfig WakeWord { get; set; } = new();
    public ContextConfig Context { get; set; } = new();
    public PerformanceConfig Performance { get; set; } = new();
    public CustomPlatformConfig CustomPlatform { get; set; } = new();
    public OfflineAsrConfig OfflineASR { get; set; } = new();
    public LocalModelConfig LocalModel { get; set; } = new();
    public Live2DConfig Live2D { get; set; } = new();
    public ScreenControlConfig ScreenControl { get; set; } = new();

    /// <summary>
    /// Set once the setup wizard has run to the end. Persisted so a blank API key (allowed on step 1)
    /// does not make the app ask for setup again on the next launch.
    /// </summary>
    public bool SetupCompleted { get; set; }
}

/// <summary>
/// Phone-local screen interaction. Off by default and gated twice: this switch, then one grant per
/// capability. A Shizuku grant is not a stand-in for either — each action still needs its own
/// approval at the moment it happens.
/// </summary>
public class ScreenControlConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Capabilities the user allowed one by one, stored by name. Unknown names are ignored on read,
    /// so a hand-edited config cannot invent a capability the whitelist does not have.
    /// </summary>
    public string[] GrantedCapabilities { get; set; } = Array.Empty<string>();
}

public class LLMConfig
{
    public string Provider { get; set; } = "Kimi";
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.moonshot.cn/v1/chat/completions";
    public string Model { get; set; } = "kimi-k2.6";

    /// <summary>
    /// Set after a probe has proven this model can actually see an image. Null means "not tried"; a
    /// model name says nothing about it, so this is a measurement, not a capability guess.
    /// </summary>
    public bool? SeesImages { get; set; }
}

public class ASRConfig
{
    public string Provider { get; set; } = "System";
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "";
}

public class TTSConfig
{
    public string Provider { get; set; } = "System";
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string Voice { get; set; } = "";
    public string VoiceCloneAudioPath { get; set; } = "";
    public string VoiceClonePromptText { get; set; } = "";
    public string VoiceCloneLang { get; set; } = "zh";
}

public class ContextConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Token budget for the history sent to the model. Above this, older turns are folded into one
    /// summary instead of being dropped. 8K is a phone-sized budget: high enough that ordinary
    /// conversations go out whole, low enough that a long session cannot quietly cost a fortune.
    /// </summary>
    public int AutoCompressTokens { get; set; } = 8000;

    public string LastSyncDate { get; set; } = "";
}

public class PerformanceConfig
{
    public string Mode { get; set; } = "balanced";
    public bool AutoDetectGames { get; set; } = true;
}

public class CustomPlatformConfig
{
    public bool Enabled { get; set; } = false;
    public string TextBaseUrl { get; set; } = "";
    public string TextModel { get; set; } = "";
    public string TextApiKey { get; set; } = "";
    public bool MultimodalEnabled { get; set; } = false;
    public string MultimodalBaseUrl { get; set; } = "";
    public string MultimodalModel { get; set; } = "";
    public string MultimodalApiKey { get; set; } = "";
}

public class VisionConfig
{
    public string Provider { get; set; } = "OpenAI";
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>Result of the last "can this endpoint see an image" probe. Null means never tried.</summary>
    public bool? SeesImages { get; set; }

    /// <summary>When the probe last ran, for the settings screen.</summary>
    public string ProbedAt { get; set; } = "";
}

public class SyncConfig
{
    public bool Enabled { get; set; } = false;
    public string DeviceName { get; set; } = "FairyAI-Android";
    public bool EnableBluetooth { get; set; } = true;
    public bool EnableLan { get; set; } = true;
    public string SignalRUrl { get; set; } = "";
    public int LanPort { get; set; } = 9876;
    public string PairingCode { get; set; } = "";

    /// <summary>
    /// Task summary sync from a paired PC. Off by default: the user has to turn it on here, and the
    /// PC has to allow it too. Only id/title/state/time travel — no prompts, outputs or files.
    /// </summary>
    public bool SummarySyncEnabled { get; set; } = false;

    /// <summary>Base address of the paired PC, filled in when pairing succeeds.</summary>
    public string SummarySyncEndpoint { get; set; } = "";

    /// <summary>Per-device token issued by the PC at pairing time. Stored encrypted by ConfigManager.</summary>
    public string SummarySyncToken { get; set; } = "";

    /// <summary>
    /// Note sync with the paired computer. Off by default and independent of the summary switch:
    /// reading task titles and exchanging your notes are different permissions.
    /// </summary>
    public bool NotesSyncEnabled { get; set; } = false;
}

/// <summary>
/// AI personality configuration. The system prompt defines the AI's character, tone, and behavior.
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

public class OfflineAsrConfig
{
    public bool Enabled { get; set; } = false;
    public string ModelName { get; set; } = "base";
    public string ModelPath { get; set; } = "";
}

public class LocalModelConfig
{
    public bool Enabled { get; set; } = false;
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string ModelName { get; set; } = "llama3";
}

public class Live2DConfig
{
    public bool Enabled { get; set; } = false;
    public string ModelName { get; set; } = "Fairy";
    public string ModelPath { get; set; } = "";
}

public static class ConfigManager
{
    private static readonly string ConfigPath = Path.Combine(
        FileSystem.AppDataDirectory, "config.json");

    public static readonly Dictionary<string, (string BaseUrl, string Model)> LLMProviders = new()
    {
        ["Kimi"] = ("https://api.moonshot.cn/v1/chat/completions", "kimi-k2.6"),
        ["DeepSeek"] = ("https://api.deepseek.com/v1/chat/completions", "deepseek-chat"),
        ["MiMo"] = ("https://api.xiaomimimo.com/v1/chat/completions", "mimo-v2.5"),
        ["OpenAI"] = ("https://api.openai.com/v1/chat/completions", "gpt-4o-mini"),
        ["OpenRouter"] = ("https://openrouter.ai/api/v1/chat/completions", "openai/gpt-4o-mini"),
        ["自定义平台"] = ("", "custom"),
    };

    public static readonly Dictionary<string, (string BaseUrl, string Model, string[] Voices)> TTSProviders = new()
    {
        ["System"] = ("", "", Array.Empty<string>()),
        ["MiMo TTS"] = ("https://api.xiaomimimo.com/v1/chat/completions", "mimo-v2.5-tts",
            new[] { "茉莉", "冰糖", "苏打", "白桦" }),
        ["MiMo TTS-VoiceClone"] = ("https://api.xiaomimimo.com/v1/chat/completions", "mimo-v2.5-tts-voiceclone",
            new[] { "自定义" }),
        ["OpenAI TTS"] = ("https://api.openai.com/v1/audio/speech", "tts-1",
            new[] { "alloy", "echo", "fable", "onyx", "nova", "shimmer" }),
        ["Azure TTS"] = ("", "zh-CN-XiaoxiaoNeural",
            new[] { "zh-CN-XiaoxiaoNeural", "zh-CN-YunxiNeural", "zh-CN-XiaoyiNeural", "en-US-JennyNeural" }),
        ["Google TTS"] = ("https://texttospeech.googleapis.com/v1/text:synthesize", "",
            new[] { "zh-CN-Wavenet-A", "zh-CN-Wavenet-B", "en-US-Wavenet-A" }),
        ["GPT-SoVITS"] = ("http://localhost:9880/tts", "gpt-sovits",
            new[] { "自定义" }),
        ["OpenRouter"] = ("https://openrouter.ai/api/v1/audio/speech", "openai/tts-1",
            new[] { "alloy", "echo", "fable", "onyx", "nova", "shimmer" }),
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                // Decrypt API keys using Android secure storage
                config.LLM.ApiKey = UnprotectKey(config.LLM.ApiKey);
                config.FallbackLLM.ApiKey = UnprotectKey(config.FallbackLLM.ApiKey);
                config.ASR.ApiKey = UnprotectKey(config.ASR.ApiKey);
                config.TTS.ApiKey = UnprotectKey(config.TTS.ApiKey);
                config.Vision.ApiKey = UnprotectKey(config.Vision.ApiKey);
                config.CustomPlatform.TextApiKey = UnprotectKey(config.CustomPlatform.TextApiKey);
                config.CustomPlatform.MultimodalApiKey = UnprotectKey(config.CustomPlatform.MultimodalApiKey);
                config.Sync.SummarySyncToken = UnprotectKey(config.Sync.SummarySyncToken);
                return config;
            }
        }
        catch { }
        return new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        // Encrypt API keys before saving
        config.LLM.ApiKey = ProtectKey(config.LLM.ApiKey);
        config.FallbackLLM.ApiKey = ProtectKey(config.FallbackLLM.ApiKey);
        config.ASR.ApiKey = ProtectKey(config.ASR.ApiKey);
        config.TTS.ApiKey = ProtectKey(config.TTS.ApiKey);
        config.Vision.ApiKey = ProtectKey(config.Vision.ApiKey);
        config.CustomPlatform.TextApiKey = ProtectKey(config.CustomPlatform.TextApiKey);
        config.CustomPlatform.MultimodalApiKey = ProtectKey(config.CustomPlatform.MultimodalApiKey);
        config.Sync.SummarySyncToken = ProtectKey(config.Sync.SummarySyncToken);
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }

    /// <summary>Encrypt API key using AES-256-GCM with PBKDF2-derived key.</summary>
    private static string ProtectKey(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        try
        {
            var key = GetDeviceKey();
            var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var plainBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
            var encrypted = new byte[plainBytes.Length];

            using var aes = new System.Security.Cryptography.AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plainBytes, encrypted, tag);

            // Format: nonce(12) + tag(16) + ciphertext
            var result = new byte[nonce.Length + tag.Length + encrypted.Length];
            Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
            Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
            Buffer.BlockCopy(encrypted, 0, result, nonce.Length + tag.Length, encrypted.Length);
            return Convert.ToBase64String(result);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to protect API key", ex);
        }
    }

    /// <summary>Decrypt API key using AES-256-GCM.</summary>
    private static string UnprotectKey(string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return "";
        try
        {
            var key = GetDeviceKey();
            var data = Convert.FromBase64String(ciphertext);
            if (data.Length < 28) return "";

            var nonce = new byte[12];
            var tag = new byte[16];
            Buffer.BlockCopy(data, 0, nonce, 0, 12);
            Buffer.BlockCopy(data, 12, tag, 0, 16);
            var encrypted = new byte[data.Length - 28];
            Buffer.BlockCopy(data, 28, encrypted, 0, encrypted.Length);
            var decrypted = new byte[encrypted.Length];

            using var aes = new System.Security.Cryptography.AesGcm(key, tag.Length);
            aes.Decrypt(nonce, encrypted, tag, decrypted);
            return System.Text.Encoding.UTF8.GetString(decrypted);
        }
        catch { return ""; }
    }

    /// <summary>
    /// Derive the device key using PBKDF2 (100k iterations, SHA256).
    ///
    /// Cached for the life of the process: every key in the config is protected with this same key, so
    /// a single Load()/Save() derives it eight times. On a mid-range phone one derivation costs tens of
    /// milliseconds, which made the first screen after startup trip Android's ANR watchdog when the
    /// config was read on the UI thread. The derivation itself is unchanged.
    /// </summary>
    private static byte[] GetDeviceKey()
    {
        var cached = _deviceKey;
        if (cached is not null) return cached;

        lock (DeviceKeyLock)
        {
            if (_deviceKey is not null) return _deviceKey;

            var androidId = Android.Provider.Settings.Secure.GetString(
                Android.App.Application.Context.ContentResolver,
                Android.Provider.Settings.Secure.AndroidId);
            if (string.IsNullOrEmpty(androidId))
                throw new InvalidOperationException("Cannot get Android ID for key derivation");

            // Same derivation as before (UTF-8 password, same salt, same iteration count), so config
            // files written by earlier builds still decrypt; only the obsolete constructor is gone.
            _deviceKey = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
                "FairyAI_AE256_" + androidId,
                System.Text.Encoding.UTF8.GetBytes("FairyAI_Salt_v2"),
                100000,
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                32);
            return _deviceKey;
        }
    }

    private static volatile byte[]? _deviceKey;
    private static readonly object DeviceKeyLock = new();

    public static bool NeedsSetup()
    {
        // "No API key" is not the same as "never set up". The key is optional in the wizard, so
        // gating on it sent users through all 13 steps again on every launch.
        if (!File.Exists(ConfigPath)) return true;

        try
        {
            return !Load().SetupCompleted;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// Generate a 6-digit pairing code for device verification.
    /// </summary>
    public static string GeneratePairingCode()
    {
        var bytes = new byte[4];
        RandomNumberGenerator.Fill(bytes);
        var code = BitConverter.ToUInt32(bytes) % 1000000;
        return code.ToString("D6");
    }

    /// <summary>
    /// Hash a pairing code for secure storage (never store plaintext).
    /// </summary>
    /// <summary>Generate or load a random per-device salt.</summary>
    private static string GetOrCreateSalt()
    {
        var saltPath = Path.Combine(FileSystem.AppDataDirectory, "pairing.salt");
        if (File.Exists(saltPath))
            return File.ReadAllText(saltPath);
        var salt = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(saltPath, salt);
        return salt;
    }

    /// <summary>Hash a pairing code for secure storage (never store plaintext).</summary>
    public static string HashPairingCode(string code)
    {
        var salt = GetOrCreateSalt();
        var bytes = Encoding.UTF8.GetBytes(code + salt);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Verify a pairing code against a stored hash.
    /// </summary>
    public static bool VerifyPairingCode(string code, string storedHash)
    {
        var computed = Convert.FromHexString(HashPairingCode(code));
        var stored = Convert.FromHexString(storedHash);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(computed, stored);
    }

    /// <summary>
    /// Export config for sync (excludes sensitive local-only fields).
    /// </summary>
    public static string ExportForSync(AppConfig config)
    {
        var syncData = new
        {
            personality = config.Personality,
            llm = new { config.LLM.Provider, config.LLM.BaseUrl, config.LLM.Model },
            tts = new { config.TTS.Provider, config.TTS.BaseUrl, config.TTS.Model, config.TTS.Voice },
            sync = new { config.Sync.DeviceName }
        };
        return JsonSerializer.Serialize(syncData, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Apply synced config from PC (merges personality and API keys).
    /// </summary>
    public static AppConfig ApplySyncedConfig(AppConfig current, string syncedJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(syncedJson);
            var root = doc.RootElement;

            // Sync personality
            if (root.TryGetProperty("personality", out var personality))
            {
                if (personality.TryGetProperty("name", out var name))
                    current.Personality.Name = name.GetString() ?? current.Personality.Name;
                if (personality.TryGetProperty("systemPrompt", out var prompt))
                    current.Personality.SystemPrompt = prompt.GetString() ?? current.Personality.SystemPrompt;
                if (personality.TryGetProperty("language", out var lang))
                    current.Personality.Language = lang.GetString() ?? current.Personality.Language;
                if (personality.TryGetProperty("enableEmotion", out var emotion))
                    current.Personality.EnableEmotion = emotion.GetBoolean();
            }

            // Sync LLM config (keep API key from current device)
            if (root.TryGetProperty("llm", out var llm))
            {
                if (llm.TryGetProperty("provider", out var p)) current.LLM.Provider = p.GetString() ?? current.LLM.Provider;
                if (llm.TryGetProperty("baseUrl", out var url)) current.LLM.BaseUrl = url.GetString() ?? current.LLM.BaseUrl;
                if (llm.TryGetProperty("model", out var m)) current.LLM.Model = m.GetString() ?? current.LLM.Model;
            }

            // Sync TTS config
            if (root.TryGetProperty("tts", out var tts))
            {
                if (tts.TryGetProperty("provider", out var p)) current.TTS.Provider = p.GetString() ?? current.TTS.Provider;
                if (tts.TryGetProperty("baseUrl", out var url)) current.TTS.BaseUrl = url.GetString() ?? current.TTS.BaseUrl;
                if (tts.TryGetProperty("model", out var m)) current.TTS.Model = m.GetString() ?? current.TTS.Model;
                if (tts.TryGetProperty("voice", out var v)) current.TTS.Voice = v.GetString() ?? current.TTS.Voice;
            }
        }
        catch { }

        return current;
    }
}
