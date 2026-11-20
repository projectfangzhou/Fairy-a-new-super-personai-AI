using FairyAI_Android.Services;
using FairyAI_Android.Services.Vision;

namespace FairyAI_Android.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly ITtsApiService _apiTts;

    public SettingsPage(ITtsApiService apiTts)
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SettingsPage XAML error: {ex}");
        }

        _apiTts = apiTts;

        try
        {
            LanguagePicker.Items.Add("zh-CN");
            LanguagePicker.Items.Add("en-US");
            LanguagePicker.Items.Add("ja-JP");

            foreach (var provider in ConfigManager.LLMProviders.Keys)
                LLMProviderPicker.Items.Add(provider);
            foreach (var provider in ConfigManager.TTSProviders.Keys)
                TTSProviderPicker.Items.Add(provider);

            // Voice clone language
            VoiceCloneLangPicker.Items.Add("zh");
            VoiceCloneLangPicker.Items.Add("en");
            VoiceCloneLangPicker.Items.Add("ja");
            VoiceCloneLangPicker.SelectedIndex = 0;

            // Performance mode
            PerformanceModePicker.Items.Add("高性能");
            PerformanceModePicker.Items.Add("均衡");
            PerformanceModePicker.Items.Add("低占用");
            PerformanceModePicker.SelectedIndex = 1;

            // Fallback LLM providers
            foreach (var provider in ConfigManager.LLMProviders.Keys)
                FallbackLLMProviderPicker.Items.Add(provider);

            LoadConfig();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SettingsPage init error: {ex}");
        }
    }

    private void LoadConfig()
    {
        try
        {
            var config = ConfigManager.Load();
            PersonalityNameEntry.Text = config.Personality.Name ?? "Fairy";
            PersonalityPromptEditor.Text = config.Personality.SystemPrompt ?? "";
            LanguagePicker.SelectedIndex = config.Personality.Language switch { "en-US" => 1, "ja-JP" => 2, _ => 0 };
            EnableEmotionCheckBox.IsChecked = config.Personality.EnableEmotion;
            var llmIdx = Array.IndexOf(ConfigManager.LLMProviders.Keys.ToArray(), config.LLM.Provider);
            LLMProviderPicker.SelectedIndex = llmIdx >= 0 ? llmIdx : 0;
            LLMApiKeyEntry.Text = config.LLM.ApiKey;
            // The save path validates this field, so it has to be filled from the stored value —
            // without it every "save" answered "请填写文本模型名称" on a configured app.
            LLMModelNameEntry.Text = config.LLM.Model;
            FallbackLLMApiKeyEntry.Text = config.FallbackLLM.ApiKey;
            var ttsIdx = Array.IndexOf(ConfigManager.TTSProviders.Keys.ToArray(), config.TTS.Provider);
            TTSProviderPicker.SelectedIndex = ttsIdx >= 0 ? ttsIdx : 0;
            TTSApiKeyEntry.Text = config.TTS.ApiKey;
            TTSModelNameEntry.Text = config.TTS.Model;
            UpdateTTSVoices(config.TTS.Provider);
            if (!string.IsNullOrEmpty(config.TTS.Voice))
            { var vi = TTSVoicePicker.Items.IndexOf(config.TTS.Voice); if (vi >= 0) TTSVoicePicker.SelectedIndex = vi; }

            // Vision model, read back so 识屏's configuration is visible and editable here
            VisionBaseUrlEntry.Text = config.Vision.BaseUrl;
            VisionModelNameEntry.Text = config.Vision.Model;
            VisionApiKeyEntry.Text = config.Vision.ApiKey;
            VisionSettingsStatus.Text = string.IsNullOrWhiteSpace(config.Vision.BaseUrl) ||
                                        string.IsNullOrWhiteSpace(config.Vision.Model)
                ? "现在没有可用的视觉模型：识屏会直接告诉你缺配置，不会去截屏。"
                : $"识屏会用它：{config.Vision.Model}";

            // The budget used to be printed as a fixed "256K" in the label; show the real value so the
            // number on screen is the one the app acts on.
            ContextCompressionCheckBox.IsChecked = config.Context.Enabled;
            ContextBudgetLabel.Text = config.Context.Enabled
                ? $"超过约 {config.Context.AutoCompressTokens:N0} tokens 时，较早的对话会被折叠成一份摘要再发送（你看到的聊天记录不变）。"
                : "关闭后整段对话按原样发送，长会话会更贵。";

            // Knowledge: keyword search always works; the vector half needs a model name.
            VectorSearchCheckBox.IsChecked = config.Knowledge.VectorSearchEnabled;
            EmbeddingsModelEntry.Text = config.Knowledge.EmbeddingsModel;
            EmbeddingsUrlEntry.Text = config.Knowledge.EmbeddingsBaseUrl;
            var embeddings = FairyAI_Android.Services.Knowledge.EmbeddingSettings.From(
                config.Knowledge.EmbeddingsBaseUrl, config.Knowledge.EmbeddingsModel,
                config.Knowledge.EmbeddingsApiKey, config.LLM.BaseUrl, config.LLM.ApiKey);

            VectorStatusLabel.Text = !config.Knowledge.VectorSearchEnabled
                ? "现在只用关键词检索（离线、免费）：说法一致才找得到。"
                : !embeddings.IsUsable
                    ? "打开了向量检索，但还没填向量模型名，暂时仍按关键词检索。"
                    : $"向量检索会调用 {embeddings.BaseUrl} · {embeddings.Model}";

            // Wake word
            var ww = config.WakeWord;
            WakeWordEnabledCheckBox.IsChecked = ww.Enabled;
            WakeWordEntry.Text = string.IsNullOrEmpty(ww.WakeWord) ? "小精灵" : ww.WakeWord;
            WakeWordAltEntry.Text = ww.AlternateWords != null ? string.Join(",", ww.AlternateWords) : "";
            WakeWordTtsCheckBox.IsChecked = ww.UseTtsFeedback;
            WakeWordFeedbackEntry.Text = string.IsNullOrEmpty(ww.FeedbackText) ? "我在" : ww.FeedbackText;
        }
        catch { }
    }

    private void OnTTSProviderChanged(object? sender, EventArgs e)
    { UpdateTTSVoices(TTSProviderPicker.SelectedItem?.ToString() ?? "System"); }

    private void UpdateTTSVoices(string provider)
    {
        TTSVoicePicker.Items.Clear();
        if (ConfigManager.TTSProviders.TryGetValue(provider, out var info) && info.Voices.Length > 0)
        { foreach (var v in info.Voices) TTSVoicePicker.Items.Add(v); TTSVoicePicker.SelectedIndex = 0; }
    }

    private async void OnTestTts(object? sender, EventArgs e)
    {
        var provider = TTSProviderPicker.SelectedItem?.ToString() ?? "System";
        if (provider == "System")
        { var tts = new Services.TtsService(_apiTts); await tts.SpeakAsync("你好, 我是 Fairy, 很高兴为你服务。"); return; }
        if (string.IsNullOrWhiteSpace(TTSApiKeyEntry.Text?.Trim())) { TtsStatus.Text = "请先输入 API Key"; return; }
        TtsStatus.Text = "合成中...";
        var voice = TTSVoicePicker.SelectedItem?.ToString();
        var audio = await _apiTts.SynthesizeAsync("你好, 我是 Fairy, 很高兴为你服务。", provider, voice);
        TtsStatus.Text = audio.Length > 0 ? $"合成成功 ({audio.Length} bytes)" : "合成失败";
        if (audio.Length > 0) { var tts = new Services.TtsService(_apiTts); await tts.SpeakAsync("你好, 我是 Fairy, 很高兴为你服务。"); }
    }

    private async void OnOpenDevices(object? sender, EventArgs e)
    {
        // Connections live on their own page; this is only a doorway.
        if (await AppNavigation.PushAsync<DevicesPage>(Navigation)) return;

        await DisplayAlertAsync("设备连接", "打不开设备连接页，请重启应用后再试。", "确定");
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LLMModelNameEntry.Text))
        {
            await DisplayAlertAsync("模型名称必填", "请填写文本模型名称", "确定");
            return;
        }

        var config = ConfigManager.Load();
        config.Personality.Name = PersonalityNameEntry.Text?.Trim() ?? "Fairy";
        config.Personality.SystemPrompt = PersonalityPromptEditor.Text?.Trim() ?? "";
        config.Personality.Language = LanguagePicker.SelectedItem?.ToString() switch { "en-US" => "en-US", "ja-JP" => "ja-JP", _ => "zh-CN" };
        config.Personality.EnableEmotion = EnableEmotionCheckBox.IsChecked;
        var llmP = LLMProviderPicker.SelectedItem?.ToString() ?? "Kimi";
        config.LLM.Provider = llmP; config.LLM.ApiKey = LLMApiKeyEntry.Text?.Trim() ?? "";
        config.LLM.Model = LLMModelNameEntry.Text?.Trim() ?? "";
        if (ConfigManager.LLMProviders.TryGetValue(llmP, out var lp)) { config.LLM.BaseUrl = lp.BaseUrl; }
        config.FallbackLLM.ApiKey = FallbackLLMApiKeyEntry.Text?.Trim() ?? "";

        // Vision: the 识屏 flow needs these three, and before this section existed the only way to
        // set them was to run the whole setup wizard again.
        var visionUrl = VisionBaseUrlEntry.Text?.Trim() ?? "";
        config.Vision.BaseUrl = visionUrl;
        config.Vision.Model = VisionModelNameEntry.Text?.Trim() ?? "";
        config.Vision.ApiKey = VisionApiKeyEntry.Text?.Trim() ?? "";
        config.Vision.Provider = string.IsNullOrWhiteSpace(visionUrl) ? "OpenAI" : "自定义";
        var ttsP = TTSProviderPicker.SelectedItem?.ToString() ?? "System";
        config.TTS.Provider = ttsP; config.TTS.ApiKey = TTSApiKeyEntry.Text?.Trim() ?? "";
        config.TTS.Model = TTSModelNameEntry.Text?.Trim() ?? "";
        config.TTS.Voice = TTSVoicePicker.SelectedItem?.ToString() ?? "";
        config.TTS.VoiceCloneAudioPath = VoiceClonePathEntry.Text?.Trim() ?? "";
        config.TTS.VoiceClonePromptText = VoiceClonePromptEntry.Text?.Trim() ?? "";
        config.TTS.VoiceCloneLang = VoiceCloneLangPicker.SelectedItem?.ToString() ?? "zh";
        if (ConfigManager.TTSProviders.TryGetValue(ttsP, out var tp)) { config.TTS.BaseUrl = tp.BaseUrl; }
        config.Context.Enabled = ContextCompressionCheckBox.IsChecked;
        config.Knowledge.VectorSearchEnabled = VectorSearchCheckBox.IsChecked;
        config.Knowledge.EmbeddingsModel = EmbeddingsModelEntry.Text?.Trim() ?? "";
        config.Knowledge.EmbeddingsBaseUrl = EmbeddingsUrlEntry.Text?.Trim() ?? "";
        config.Performance.Mode = PerformanceModePicker.SelectedIndex switch { 0 => "high", 1 => "balanced", 2 => "low", _ => "balanced" };

        // Wake word
        config.WakeWord = new WakeWordConfig
        {
            Enabled = WakeWordEnabledCheckBox.IsChecked,
            WakeWord = string.IsNullOrWhiteSpace(WakeWordEntry.Text) ? "小精灵" : WakeWordEntry.Text!.Trim(),
            AlternateWords = string.IsNullOrWhiteSpace(WakeWordAltEntry.Text)
                ? Array.Empty<string>()
                : WakeWordAltEntry.Text!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            UseTtsFeedback = WakeWordTtsCheckBox.IsChecked,
            FeedbackText = string.IsNullOrWhiteSpace(WakeWordFeedbackEntry.Text) ? "我在" : WakeWordFeedbackEntry.Text!.Trim()
        };

        ConfigManager.Save(config);
        await DisplayAlertAsync("设置", "已保存", "确定");
    }

    /// <summary>
    /// Shows the endpoint a red square and asks what colour it is. This is the only honest answer to
    /// "can this model see images" — no model name says it, and the answer decides whether 识屏 may
    /// fall back to the main model when no vision block is configured.
    /// </summary>
    private async void OnProbeVisionClicked(object? sender, EventArgs e)
    {
        var config = ConfigManager.Load();

        var visionSettings = new VisionSettings(config.Vision.Provider, config.Vision.BaseUrl,
            config.Vision.Model, config.Vision.ApiKey) { SeesImages = config.Vision.SeesImages == true };
        var multimodal = new MultimodalSettings(config.CustomPlatform.MultimodalEnabled,
            config.CustomPlatform.MultimodalBaseUrl, config.CustomPlatform.MultimodalModel,
            config.CustomPlatform.MultimodalApiKey);
        var mainModel = new VisionSettings(config.LLM.Provider, config.LLM.BaseUrl, config.LLM.Model,
            config.LLM.ApiKey) { SeesImages = true };

        var (configured, _) = VisionTargetSelector.Select(visionSettings, multimodal, null);
        var target = configured ?? VisionTargetSelector.Select(null, null, mainModel).Target;

        if (target is null)
        {
            VisionProbeStatus.Text = "没有可测的接口：先填视觉模型的接口地址和模型名称，或者先把主模型配好。";
            return;
        }

        VisionProbeStatus.Text = $"正在让「{target.Name}」看一张图…";
        VisionProbeStatus.TextColor = Color.FromArgb("#9CA3AF");

        VisionProbeResult result;
        using (var client = new VisionClient(() => target))
            result = await client.ProbeAsync();

        if (configured is not null)
        {
            // Only a conclusive answer is written down: a timeout must not be remembered as "this
            // model cannot see images", or one bad network moment would disable the feature for good.
            if (result.Conclusive)
            {
                config.Vision.SeesImages = result.CanSeeImages;
                config.Vision.ProbedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            }
        }
        else if (result.Conclusive)
        {
            config.LLM.SeesImages = result.CanSeeImages;
        }

        ConfigManager.Save(config);

        VisionProbeStatus.Text = (configured is not null ? "视觉模型：" : "主模型：") +
                                 (result.Conclusive ? result.Message : "这次没测出结果 — " + result.Message);
        VisionProbeStatus.TextColor = Color.FromArgb(
            result.Conclusive ? result.CanSeeImages ? "#6EE7B7" : "#FCA5A5" : "#FBBF24");
        LoadConfig();
    }

    private async void OnTestClicked(object? sender, EventArgs e)    {
        var config = ConfigManager.Load();
        if (string.IsNullOrWhiteSpace(config.LLM.ApiKey)) { await DisplayAlertAsync("错误", "请先配置 LLM API Key", "确定"); return; }
        try
        {
            var llm = new LlmService();
            var r = await llm.ChatAsync(new List<Models.ChatMessage> { new() { Role = "user", Content = "Say hello" } }, "hello");
            await DisplayAlertAsync("Test", r, "OK");
        }
        catch (Exception ex) { await DisplayAlertAsync("Error", ex.Message, "OK"); }
    }

    private async void OnImportDocument(object? sender, EventArgs e)
    {
        try
        {
            // Open file manager to select document
            var result = await FilePicker.PickAsync(new PickOptions
            {
                PickerTitle = "选择文档 (PPT/Word/PDF)",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.Android, new[] { "application/pdf", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/vnd.openxmlformats-officedocument.presentationml.presentation" } },
                    { DevicePlatform.iOS, new[] { "com.adobe.pdf", "org.openxmlformats.wordprocessingml.document" } },
                    { DevicePlatform.MacCatalyst, new[] { "com.adobe.pdf", "org.openxmlformats.wordprocessingml.document" } },
                })
            });

            if (result != null)
            {
                var kb = new KnowledgeBaseService();
                var text = await kb.AnalyzeDocumentAsync(result.FullPath);
                KnowledgeStatus.Text = $"已导入: {result.FileName}";
                KnowledgeStatus.TextColor = Colors.LimeGreen;
            }
        }
        catch (Exception ex)
        {
            KnowledgeStatus.Text = $"错误: {ex.Message}";
            KnowledgeStatus.TextColor = Colors.OrangeRed;
        }
    }
}
