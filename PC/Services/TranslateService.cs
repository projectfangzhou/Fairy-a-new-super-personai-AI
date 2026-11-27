using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyAiAssistant.Services;

/// <summary>
/// Translation tool: calls the LLM to translate text between languages.
/// Acts as both a tool and a standalone service.
/// </summary>
public class TranslateService
{
    private readonly HttpClient _http;

    public TranslateService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Translate text using the configured LLM endpoint.</summary>
    public async Task<string> TranslateAsync(string text, string targetLanguage, string endpoint, string model, string? apiKey = null)
    {
        var prompt = targetLanguage.ToLowerInvariant() switch
        {
            "en" or "english" => $"Translate the following text to English. Only return the translation, nothing else:\n\n{text}",
            "zh" or "chinese" or "cn" => $"将以下文本翻译为中文。只返回翻译结果：\n\n{text}",
            "ja" or "japanese" or "jp" => $"以下のテキストを日本語に翻訳してください。翻訳のみ返してください：\n\n{text}",
            "ko" or "korean" => $"다음 텍스트를 한국어로 번역하세요. 번역 결과만 반환하세요:\n\n{text}",
            "fr" or "french" => $"Traduisez le texte suivant en français. Ne retournez que la traduction :\n\n{text}",
            "de" or "german" => $"Übersetzen Sie den folgenden Text ins Deutsche. Geben Sie nur die Übersetzung zurück:\n\n{text}",
            "es" or "spanish" => $"Traduce el siguiente texto al español. Devuelve solo la traducción:\n\n{text}",
            _ => $"Translate the following text to {targetLanguage}. Only return the translation:\n\n{text}"
        };

        var payload = JsonSerializer.Serialize(new
        {
            model = string.IsNullOrWhiteSpace(model) ? "llama3" : model,
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            stream = false,
            max_tokens = 4096
        });

        var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Add("Authorization", $"Bearer {apiKey}");

        var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString();
        return content ?? "";
    }
}

// ===== Tool implementation =====

public class TranslateTool : ITool
{
    private readonly TranslateService _translator;

    public string Name => "translate";
    public string Description => "Translate text between languages (English, Chinese, Japanese, Korean, French, German, Spanish, etc.)";
    public List<ToolParameter> Parameters => new()
    {
        new ToolParameter { Name = "text", Description = "Text to translate" },
        new ToolParameter { Name = "target_language", Description = "Target language code or name (e.g., 'en', 'zh', 'ja', 'korean')" }
    };

    public TranslateTool(TranslateService translator) => _translator = translator;

    public async Task<string> ExecuteAsync(JsonObject args)
    {
        var text = args["text"]?.GetValue<string>() ?? "";
        var targetLang = args["target_language"]?.GetValue<string>() ?? "en";

        var config = ConfigManager.Load();
        return await _translator.TranslateAsync(text, targetLang, config.LLM.BaseUrl, config.LLM.Model, config.LLM.ApiKey);
    }
}
