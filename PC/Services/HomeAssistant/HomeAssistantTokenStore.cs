using System.IO;
using System.Text;
using System.Text.Json;

namespace MyAiAssistant.Services.HomeAssistant;

/// <summary>
/// Where the Home Assistant tokens live between runs.
///
/// It reuses the app's existing secret store (<see cref="ApiKeyProtector"/>: AES-256-GCM with an
/// integrity tag, key file beside the executable) rather than inventing a second mechanism. That
/// protects against a readable config file, a backup, or a log scrape; it does not protect against
/// someone who can already read files next to the app. The token is revocable on the Home
/// Assistant side, which is what keeps that acceptable.
/// </summary>
public sealed class HomeAssistantTokenStore
{
    private const string FileName = "tokens.dat";

    public HomeAssistantTokenStore(string? directory = null)
    {
        Directory = directory ?? DefaultDirectory;
        FilePath = Path.Combine(Directory, FileName);
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FairyAI", "homeassistant");

    public string Directory { get; }

    public string FilePath { get; }

    /// <summary>Why the last <see cref="Load"/> returned nothing, when it did so unexpectedly.</summary>
    public string? LastError { get; private set; }

    public bool Save(HaTokenSet tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        LastError = null;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var payload = JsonSerializer.Serialize(new StoredTokens(
                tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAt));
            var encrypted = ApiKeyProtector.Protect(payload);
            File.WriteAllText(FilePath, encrypted, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"令牌无法保存：{ex.GetType().Name}";
            return false;
        }
    }

    public HaTokenSet? Load()
    {
        LastError = null;

        if (!File.Exists(FilePath))
            return null;

        try
        {
            var encrypted = File.ReadAllText(FilePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(encrypted))
            {
                LastError = "令牌文件为空";
                return null;
            }

            var payload = ApiKeyProtector.Unprotect(encrypted);
            if (string.IsNullOrWhiteSpace(payload))
            {
                LastError = "令牌文件无法解密";
                return null;
            }

            var stored = JsonSerializer.Deserialize<StoredTokens>(payload);
            if (stored is null || string.IsNullOrWhiteSpace(stored.AccessToken))
            {
                LastError = "令牌文件内容不完整";
                return null;
            }

            return new HaTokenSet(stored.AccessToken!, stored.RefreshToken, stored.ExpiresAt);
        }
        catch (Exception ex)
        {
            LastError = $"令牌读取失败：{ex.GetType().Name}";
            return null;
        }
    }

    public bool Clear()
    {
        LastError = null;
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"令牌文件无法删除：{ex.GetType().Name}";
            return false;
        }
    }

    private sealed record StoredTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);
}
