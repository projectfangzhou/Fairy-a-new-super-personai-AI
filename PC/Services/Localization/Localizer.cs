namespace MyAiAssistant.Services.Localization;

/// <summary>
/// Small string catalogue with a hard fallback chain (requested → zh-CN → the key itself).
/// Only the strings that exist here are translated: an untranslated surface keeps its Chinese
/// text rather than showing an empty label.
/// </summary>
public sealed class Localizer
{
    public const string DefaultLanguage = "zh-CN";

    private static readonly Dictionary<string, Dictionary<string, string>> Catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh-CN"] = new(StringComparer.Ordinal)
        {
            ["messaging.title"] = "消息平台",
            ["messaging.subtitle"] = "已实现的平台可以直接检测；其余仅登记，接入前不会显示为可用。",
            ["messaging.status.ready"] = "可用",
            ["messaging.status.needsConfiguration"] = "需要配置",
            ["messaging.status.unavailable"] = "尚未实现",
            ["messaging.status.failed"] = "不可用",
            ["messaging.state.implemented"] = "已实现",
            ["messaging.state.planned"] = "仅登记",
            ["messaging.capability.dm"] = "私聊",
            ["messaging.capability.group"] = "群聊",
            ["messaging.capability.media"] = "媒体",
            ["messaging.probe"] = "检测",
            ["common.language"] = "语言",
            ["common.save"] = "保存",
            ["common.cancel"] = "取消"
        },
        ["en-US"] = new(StringComparer.Ordinal)
        {
            ["messaging.title"] = "Messaging platforms",
            ["messaging.subtitle"] = "Implemented platforms can be probed; the rest are listed only and never shown as available.",
            ["messaging.status.ready"] = "Ready",
            ["messaging.status.needsConfiguration"] = "Needs configuration",
            ["messaging.status.unavailable"] = "Not implemented",
            ["messaging.status.failed"] = "Unavailable",
            ["messaging.state.implemented"] = "Implemented",
            ["messaging.state.planned"] = "Listed only",
            ["messaging.capability.dm"] = "Direct",
            ["messaging.capability.group"] = "Group",
            ["messaging.capability.media"] = "Media",
            ["messaging.probe"] = "Check",
            ["common.language"] = "Language",
            ["common.save"] = "Save",
            ["common.cancel"] = "Cancel"
        },
        ["ja-JP"] = new(StringComparer.Ordinal)
        {
            ["messaging.title"] = "メッセージプラットフォーム",
            ["messaging.subtitle"] = "実装済みのプラットフォームのみ検出できます。その他は登録のみで、利用可能とは表示されません。",
            ["messaging.status.ready"] = "利用可能",
            ["messaging.status.needsConfiguration"] = "設定が必要",
            ["messaging.status.unavailable"] = "未実装",
            ["messaging.status.failed"] = "利用不可",
            ["messaging.state.implemented"] = "実装済み",
            ["messaging.state.planned"] = "登録のみ",
            ["messaging.capability.dm"] = "個人チャット",
            ["messaging.capability.group"] = "グループ",
            ["messaging.capability.media"] = "メディア",
            ["messaging.probe"] = "確認",
            ["common.language"] = "言語",
            ["common.save"] = "保存",
            ["common.cancel"] = "キャンセル"
        },
        ["ru-RU"] = new(StringComparer.Ordinal)
        {
            ["messaging.title"] = "Платформы обмена сообщениями",
            ["messaging.subtitle"] = "Проверять можно только реализованные платформы; остальные лишь перечислены и не показываются как доступные.",
            ["messaging.status.ready"] = "Готово",
            ["messaging.status.needsConfiguration"] = "Требуется настройка",
            ["messaging.status.unavailable"] = "Не реализовано",
            ["messaging.status.failed"] = "Недоступно",
            ["messaging.state.implemented"] = "Реализовано",
            ["messaging.state.planned"] = "Только в списке",
            ["messaging.capability.dm"] = "Личные",
            ["messaging.capability.group"] = "Группы",
            ["messaging.capability.media"] = "Медиа",
            ["messaging.probe"] = "Проверить",
            ["common.language"] = "Язык",
            ["common.save"] = "Сохранить",
            ["common.cancel"] = "Отмена"
        },
    };

    private readonly string _language;

    public Localizer(string? language = null)
    {
        var requested = string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language.Trim();
        _language = Catalog.ContainsKey(requested) ? requested : DefaultLanguage;
    }

    public string Language => _language;

    public static IReadOnlyList<string> SupportedLanguages => Catalog.Keys.ToArray();

    public string this[string key] => Get(key);

    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;

        if (Catalog[_language].TryGetValue(key, out var value)) return value;
        if (Catalog[DefaultLanguage].TryGetValue(key, out var fallback)) return fallback;
        return key;
    }
}