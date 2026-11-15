using FairyAI_Android.Services;

namespace FairyAI_Android.Pages;

public partial class SetupWizardPage : ContentPage
{
    private int _currentStep = 1;
    private const int TotalSteps = 13;

    public SetupWizardPage()
    {
        InitializeComponent();
        InitializeProviders();
        UpdateStepVisibility();
    }

    private void InitializeProviders()
    {
        // LLM Providers
        foreach (var provider in ConfigManager.LLMProviders.Keys)
            LLMProviderPicker.Items.Add(provider);
        LLMProviderPicker.SelectedIndex = 0;

        // Vision Providers
        VisionProviderPicker.Items.Add("OpenAI");
        VisionProviderPicker.Items.Add("Gemini");
        VisionProviderPicker.Items.Add("DeepSeek");
        VisionProviderPicker.Items.Add("Kimi");
        VisionProviderPicker.SelectedIndex = 0;

        // TTS Providers
        foreach (var provider in ConfigManager.TTSProviders.Keys)
            TTSProviderPicker.Items.Add(provider);
        TTSProviderPicker.SelectedIndex = 0;

        // Fallback LLM
        foreach (var provider in ConfigManager.LLMProviders.Keys)
            FallbackLLMProviderPicker.Items.Add(provider);

        // Default values
        WhisperModelPicker.SelectedIndex = 1; // base
        Live2DModelPicker.SelectedIndex = 0; // Fairy
        LanguagePicker.SelectedIndex = 0; // 简体中文
        PerformanceModePicker.SelectedIndex = 1; // 均衡
        ContextCompressionCheckBox.IsChecked = true;
        DeviceNameEntry.Text = "FairyAI-Android";
        WakeWordEntry.Text = "小精灵";
        PersonalityNameEntry.Text = "Fairy";
    }

    private void OnLLMProviderChanged(object? sender, EventArgs e)
    {
        var provider = LLMProviderPicker.SelectedItem?.ToString() ?? "Kimi";
        LLMModelNameEntry.Text = provider switch
        {
            "Kimi" => "kimi-k2.6",
            "DeepSeek" => "deepseek-chat",
            "MiMo" => "mimo-v2.5",
            "OpenAI" => "gpt-4o-mini",
            "OpenRouter" => "openai/gpt-4o-mini",
            _ => ""
        };
    }

    private void OnTTSProviderChanged(object? sender, EventArgs e)
    {
        var provider = TTSProviderPicker.SelectedItem?.ToString() ?? "System";
        TTSVoicePicker.Items.Clear();
        if (ConfigManager.TTSProviders.TryGetValue(provider, out var info) && info.Voices.Length > 0)
        {
            foreach (var v in info.Voices)
                TTSVoicePicker.Items.Add(v);
            TTSVoicePicker.SelectedIndex = 0;
        }
    }

    private void OnOpenShizuku(object? sender, EventArgs e)
    {
        // Optional by design: without Shizuku only the screen capabilities are missing, and the
        // status line says which state the device is in instead of asking for a permission that
        // does not exist in the manifest.
        var screen = new Services.Screen.ScreenControl();
        var status = screen.Status;
        var opened = screen.OpenShizukuApp(out var message);

        ScreenStatus.Text = opened ? message : message + " " + status.Detail;
        ScreenStatus.TextColor = status.CanInject ? Colors.LimeGreen : Colors.Yellow;
    }

    private async void OnNextClicked(object? sender, EventArgs e)
    {
        // Validate current step
        if (_currentStep == 1 && string.IsNullOrWhiteSpace(LLMProviderPicker.SelectedItem?.ToString()))
        {
            await DisplayAlertAsync("错误", "请选择 LLM 供应商", "确定");
            return;
        }

        if (_currentStep == 2 && string.IsNullOrWhiteSpace(LLMModelNameEntry.Text))
        {
            await DisplayAlertAsync("错误", "请输入模型名称", "确定");
            return;
        }

        if (_currentStep < TotalSteps)
        {
            _currentStep++;
            UpdateStepVisibility();
        }
        else
        {
            // Finish setup
            await SaveConfiguration();
            await DisplayAlertAsync("完成", "初始化完成！欢迎使用仙灵", "确定");
            
            // Navigate to main app. MainPage is deprecated in .NET 10; the supported way is to replace
            // the page of the window the wizard is already in.
            if (Application.Current?.Windows.FirstOrDefault() is { } window)
                window.Page = new AppShell();
        }
    }

    private void OnBackClicked(object? sender, EventArgs e)
    {
        if (_currentStep > 1)
        {
            _currentStep--;
            UpdateStepVisibility();
        }
    }

    private void UpdateStepVisibility()
    {
        // Update progress
        StepTitle.Text = $"第 {_currentStep} 步 / 共 {TotalSteps} 步";
        ProgressBar.Progress = (double)_currentStep / TotalSteps;

        // Hide all panels
        Step1Panel.IsVisible = false;
        Step2Panel.IsVisible = false;
        Step3Panel.IsVisible = false;
        Step4Panel.IsVisible = false;
        Step5Panel.IsVisible = false;
        Step6Panel.IsVisible = false;
        Step7Panel.IsVisible = false;
        Step8Panel.IsVisible = false;
        Step9Panel.IsVisible = false;
        Step10Panel.IsVisible = false;
        Step11Panel.IsVisible = false;
        Step12Panel.IsVisible = false;
        Step13Panel.IsVisible = false;

        // Show current step
        switch (_currentStep)
        {
            case 1: Step1Panel.IsVisible = true; break;
            case 2: Step2Panel.IsVisible = true; break;
            case 3: Step3Panel.IsVisible = true; break;
            case 4: Step4Panel.IsVisible = true; break;
            case 5: Step5Panel.IsVisible = true; break;
            case 6: Step6Panel.IsVisible = true; break;
            case 7: Step7Panel.IsVisible = true; break;
            case 8: Step8Panel.IsVisible = true; break;
            case 9: Step9Panel.IsVisible = true; break;
            case 10: Step10Panel.IsVisible = true; break;
            case 11: Step11Panel.IsVisible = true; break;
            case 12: Step12Panel.IsVisible = true; break;
            case 13: Step13Panel.IsVisible = true; break;
        }

        // Update button visibility
        BackBtn.IsVisible = _currentStep > 1;
        NextBtn.Text = _currentStep == TotalSteps ? "完成" : "下一步";
    }

    private async Task SaveConfiguration()
    {
        var config = ConfigManager.Load();

        // Step 1-2: LLM
        config.LLM.Provider = LLMProviderPicker.SelectedItem?.ToString() ?? "Kimi";
        config.LLM.ApiKey = LLMApiKeyEntry.Text?.Trim() ?? "";
        config.LLM.Model = LLMModelNameEntry.Text?.Trim() ?? "";
        if (ConfigManager.LLMProviders.TryGetValue(config.LLM.Provider, out var llmInfo))
        {
            config.LLM.BaseUrl = llmInfo.BaseUrl;
        }

        // Step 3: Vision
        var visionProvider = VisionProviderPicker.SelectedItem?.ToString() ?? "OpenAI";
        config.Vision.Provider = visionProvider;
        config.Vision.ApiKey = VisionApiKeyEntry.Text?.Trim() ?? "";
        config.Vision.Model = VisionModelNameEntry.Text?.Trim() ?? "";
        config.Vision.BaseUrl = visionProvider switch
        {
            "OpenAI" => "https://api.openai.com/v1/chat/completions",
            "Gemini" => "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
            "DeepSeek" => "https://api.deepseek.com/v1/chat/completions",
            "Kimi" => "https://api.moonshot.cn/v1/chat/completions",
            _ => ""
        };

        // Step 4: TTS
        config.TTS.Provider = TTSProviderPicker.SelectedItem?.ToString() ?? "System";
        config.TTS.ApiKey = TTSApiKeyEntry.Text?.Trim() ?? "";
        config.TTS.Voice = TTSVoicePicker.SelectedItem?.ToString() ?? "";
        if (ConfigManager.TTSProviders.TryGetValue(config.TTS.Provider, out var ttsInfo))
        {
            config.TTS.BaseUrl = ttsInfo.BaseUrl;
        }

        // Step 5: Offline ASR
        config.OfflineASR = new OfflineAsrConfig
        {
            Enabled = EnableOfflineAsrCheckBox.IsChecked,
            ModelName = WhisperModelPicker.SelectedIndex switch
            {
                0 => "tiny",
                1 => "base",
                2 => "small",
                _ => "base"
            }
        };

        // Step 6: Fallback LLM
        config.FallbackLLM.Provider = FallbackLLMProviderPicker.SelectedItem?.ToString() ?? "";
        config.FallbackLLM.ApiKey = FallbackLLMApiKeyEntry.Text?.Trim() ?? "";
        config.FallbackLLM.Model = FallbackLLMModelEntry.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(config.FallbackLLM.Provider) && 
            ConfigManager.LLMProviders.TryGetValue(config.FallbackLLM.Provider, out var fallbackInfo))
        {
            config.FallbackLLM.BaseUrl = fallbackInfo.BaseUrl;
        }

        // Step 7: Local Model
        config.LocalModel = new LocalModelConfig
        {
            Enabled = EnableLocalModelCheckBox.IsChecked,
            OllamaUrl = OllamaUrlEntry.Text?.Trim() ?? "http://localhost:11434",
            ModelName = LocalModelNameEntry.Text?.Trim() ?? "llama3"
        };

        // Step 8: Live2D
        config.Live2D = new Live2DConfig
        {
            Enabled = EnableLive2DCheckBox.IsChecked,
            ModelName = Live2DModelPicker.SelectedIndex switch
            {
                0 => "Fairy",
                1 => "Hiyori",
                2 => "Haru",
                _ => "Fairy"
            }
        };

        // Step 9: Personality
        config.Personality.Name = PersonalityNameEntry.Text?.Trim() ?? "Fairy";
        config.Personality.SystemPrompt = PersonalityPromptEditor.Text?.Trim() ?? "";
        config.Personality.Language = LanguagePicker.SelectedIndex switch
        {
            0 => "zh-CN",
            1 => "en-US",
            2 => "ja-JP",
            _ => "zh-CN"
        };

        // Step 10: Wake Word
        config.WakeWord = new WakeWordConfig
        {
            Enabled = WakeWordEnabledCheckBox.IsChecked,
            WakeWord = string.IsNullOrWhiteSpace(WakeWordEntry.Text) ? "小精灵" : WakeWordEntry.Text!.Trim(),
            AlternateWords = string.IsNullOrWhiteSpace(WakeWordAltEntry.Text)
                ? Array.Empty<string>()
                : WakeWordAltEntry.Text!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            UseTtsFeedback = WakeWordTtsCheckBox.IsChecked,
            FeedbackText = "我在"
        };

        // Step 11: Performance
        config.Performance.Mode = PerformanceModePicker.SelectedIndex switch
        {
            0 => "high",
            1 => "balanced",
            2 => "low",
            _ => "balanced"
        };
        config.Context.Enabled = ContextCompressionCheckBox.IsChecked;

        // Step 12: Accessibility (permission only, no config)


        // Step 13: Device Sync
        config.Sync.DeviceName = DeviceNameEntry.Text?.Trim() ?? "FairyAI-Android";
        config.Sync.SignalRUrl = RelayUrlEntry.Text?.Trim() ?? "";
        config.Sync.Enabled = !string.IsNullOrWhiteSpace(config.Sync.SignalRUrl);

        // Records that the wizard ran. Leaving the API key blank is allowed on step 1, so the key
        // cannot be what decides whether setup is done — that made every later launch start the
        // wizard again.
        config.SetupCompleted = true;

        ConfigManager.Save(config);
    }
}
