using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FairyAI_Android.Models;
using FairyAI_Android.Services;

namespace FairyAI_Android.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ILlmService _llm;
    private readonly IIntentAnalyzer _intent;
    private readonly ISearchService _search;
    private readonly ITtsService _tts;
    private readonly IChatHistoryService _history;
    private readonly WakeWordService _wakeWord;
    private readonly FunctionCallingService _tools;
    private readonly Services.Usage.TokenUsageService _usage;
    private readonly ContextCompressionService _context;
    private readonly string _sessionId;

    [ObservableProperty] private string _userInput = "";
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private string _statusText = "准备就绪";
    [ObservableProperty] private bool _isListening;
    [ObservableProperty] private bool _isWakeWordActive;
    [ObservableProperty] private string _wakeWordText = "";

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public MainViewModel(ILlmService llm, IIntentAnalyzer intent,
        ISearchService search, ITtsService tts, IChatHistoryService history,
        WakeWordService wakeWord, FunctionCallingService tools,
        Services.Usage.TokenUsageService usage, ContextCompressionService context)
    {
        _llm = llm;
        _intent = intent;
        _search = search;
        _tts = tts;
        _history = history;
        _wakeWord = wakeWord;
        _tools = tools;
        _usage = usage;
        _context = context;
        _sessionId = Guid.NewGuid().ToString("N");

        // Wire wake word events
        _wakeWord.WakeWordDetected += OnWakeWordDetected;
        _wakeWord.PartialResult += OnWakeWordPartial;

        // Auto-start if enabled
        if (_wakeWord.GetConfig().Enabled)
        {
            StartWakeWordListening();
        }
    }

    // === Wake Word ===

    [RelayCommand]
    public void ToggleWakeWord()
    {
        if (_wakeWord.IsRunning)
        {
            _wakeWord.Stop();
            IsWakeWordActive = false;
            StatusText = "唤醒词已关闭";
        }
        else
        {
            StartWakeWordListening();
        }
    }

    private void StartWakeWordListening()
    {
        _wakeWord.LoadConfig();
        if (!_wakeWord.GetConfig().Enabled)
        {
            StatusText = "请在设置中启用唤醒词";
            return;
        }
        _wakeWord.Start();
        IsWakeWordActive = true;
        StatusText = $"聆听唤醒词: {_wakeWord.GetConfig().WakeWord}";
    }

    private async void OnWakeWordDetected(string recognizedText)
    {
        var config = _wakeWord.GetConfig();

        // TTS feedback
        if (config.UseTtsFeedback)
        {
            await _tts.SpeakAsync(config.FeedbackText);
        }

        // Extract command after wake word
        var command = _wakeWord.ExtractCommand(recognizedText);

        if (!string.IsNullOrWhiteSpace(command))
        {
            // Command detected, process directly
            UserInput = command;
            await SendMessageAsync();
        }
        else
        {
            // No command after wake word, listen for command
            StatusText = "请说出你的指令...";

            try
            {
                var asr = new AndroidAsrService();
                var result = await asr.RecognizeAsync();

                if (!string.IsNullOrWhiteSpace(result))
                {
                    UserInput = result;
                    await SendMessageAsync();
                }
                else
                {
                    StatusText = $"未听到指令, 继续监听唤醒词: {config.WakeWord}";
                }
            }
            catch
            {
                StatusText = $"语音识别出错, 继续监听唤醒词: {config.WakeWord}";
            }
        }
    }

    private void OnWakeWordPartial(string text)
    {
        WakeWordText = text;
    }

    // === Messaging ===

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(UserInput) || IsProcessing) return;

        var text = UserInput;
        UserInput = "";

        var userMsg = new ChatMessage { Role = "user", Content = text };
        Messages.Add(userMsg);
        await _history.SaveAsync(_sessionId, userMsg);

        IsProcessing = true;
        StatusText = "思考中...";

        try
        {
            var response = await RunAssistantAsync(text);

            var assistantMsg = new ChatMessage { Role = "assistant", Content = response };
            Messages.Add(assistantMsg);
            await _history.SaveAsync(_sessionId, assistantMsg);

            // TTS response
            await _tts.SpeakAsync(response);

            StatusText = IsWakeWordActive
                ? $"唤醒词监听中: {_wakeWord.GetConfig().WakeWord}"
                : "准备就绪";
        }
        catch (Exception ex)
        {
            StatusText = $"出错: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    /// <summary>
    /// Answers with tools when the endpoint supports them, and falls back to the older intent + search
    /// + streaming path when it does not. The fallback is not silent: the status line says which route
    /// was taken.
    /// </summary>
    private async Task<string> RunAssistantAsync(string text)
    {
        // What gets sent is decided once per turn: the whole conversation while it fits the budget,
        // otherwise a summary of the older part plus the recent turns. The visible transcript is not
        // touched either way.
        var requestHistory = await BuildRequestHistoryAsync();

        if (_tools.ToolCount > 0)
        {
            var personality = ConfigManager.Load().Personality.SystemPrompt;

            var outcome = await _tools.RunAsync(
                personality,
                requestHistory,
                text,
                onToolRun: result => MainThread.BeginInvokeOnMainThread(() =>
                    StatusText = result.Ok ? $"已完成 {result.Name}" : $"{result.Name} 未完成"),
                maxRounds: 5);

            if (outcome.PromptTokens > 0 || outcome.CompletionTokens > 0)
                _usage.Record(outcome.PromptTokens, outcome.CompletionTokens, exact: true);

            if (outcome.Error is null && !string.IsNullOrWhiteSpace(outcome.Text))
            {
                StatusText = outcome.Runs.Count == 0
                    ? "准备就绪"
                    : $"用过 {outcome.Runs.Count} 个工具";
                return outcome.Text;
            }

            if (!outcome.ToolsUnsupported && outcome.Error is not null)
            {
                // A real failure (network, bad key): say so instead of silently switching routes.
                return $"抱歉，这次没能完成：{outcome.Error}";
            }

            StatusText = "这个模型不支持工具调用，改用普通对话";
        }

        return await FallbackAsync(text, requestHistory);
    }

    /// <summary>
    /// The history to send. Compression is a request-level decision: it never edits <see cref="Messages"/>,
    /// so scrolling back in the chat still shows everything the user said.
    /// </summary>
    private async Task<List<ChatMessage>> BuildRequestHistoryAsync()
    {
        var all = Messages.ToList();
        try
        {
            var before = _context.Compressions;
            var request = await _context.BuildRequestHistoryAsync(all);

            if (_context.Compressions > before)
                MainThread.BeginInvokeOnMainThread(() =>
                    StatusText = $"较早的对话已折叠成摘要（发送 {request.Count} 条）");

            return request;
        }
        catch (Exception)
        {
            // Compression is an optimisation; a failure must not stop the answer.
            return all;
        }
    }

    /// <summary>The pre-tool path: intent analysis, an optional web search, then streaming.</summary>
    private async Task<string> FallbackAsync(string text, List<ChatMessage> requestHistory)
    {
        var historyText = string.Join("\n", requestHistory.TakeLast(10).Select(m => $"{m.Role}: {m.Content}"));
        var (intent, query, _) = await _intent.AnalyzeAsync(text, historyText);

        StatusText = intent switch
        {
            "search_web" => "联网搜索...",
            "analyze_screen" => "分析屏幕...",
            _ => "思考中...",
        };

        if (intent == "search_web")
        {
            var searchResult = await _search.SearchAsync(query);
            return await _llm.ChatAsync(
                requestHistory,
                $"[搜索结果]\n{searchResult}\n\n[用户问题] {text}");
        }

        return await StreamResponseAsync(text, requestHistory);
    }

    private async Task<string> StreamResponseAsync(string userMessage, List<ChatMessage> history)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            // The streaming path used to record nothing; the provider's usage chunk (or an estimate
            // when it sends none) now reaches the same ledger the tool loop writes to.
            await foreach (var chunk in _llm.StreamChatAsync(
                history, userMessage,
                usage => _usage.Record(usage.PromptTokens, usage.CompletionTokens, usage.Exact)))
            {
                sb.Append(chunk);

                var partial = sb.ToString();
                var existing = Messages.LastOrDefault(m => m.Role == "assistant");
                if (existing != null && existing == Messages.LastOrDefault())
                {
                    existing.Content = partial;
                }
                else
                {
                    Messages.Add(new ChatMessage { Role = "assistant", Content = partial });
                }
            }
        }
        catch (Exception ex)
        {
            return $"抱歉，出错了: {ex.Message}";
        }
        return sb.ToString();
    }

    [RelayCommand]
    private async Task StartListeningAsync()
    {
        try
        {
            IsListening = true;
            StatusText = "正在聆听...";

            var asr = new AndroidAsrService();
            var result = await asr.RecognizeAsync();

            if (!string.IsNullOrWhiteSpace(result))
            {
                UserInput = result;
                await SendMessageAsync();
            }
            else
            {
                StatusText = "未识别到语音";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"语音识别出错: {ex.Message}";
        }
        finally
        {
            IsListening = false;
        }
    }

    public void Dispose()
    {
        _wakeWord.WakeWordDetected -= OnWakeWordDetected;
        _wakeWord.PartialResult -= OnWakeWordPartial;
        _wakeWord.Stop();
    }
}
