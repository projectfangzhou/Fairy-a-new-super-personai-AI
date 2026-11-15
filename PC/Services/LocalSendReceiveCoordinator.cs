using System.IO;
using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services.LocalSend;
using MyAiAssistant.Services.Permissions;
using MyAiAssistant.Services.Relay;

namespace MyAiAssistant.Services;

/// <summary>
/// Owns the LAN receive server and the question it has to ask before writing anything.
///
/// Receiving a file from the network is an inbound transfer, so it goes through the same two
/// doors as everything else: the user answers through the shared pending queue (the one the
/// control centre renders), and the answer is written to the operation audit. A transfer that
/// nobody answers is refused rather than accepted by silence.
/// </summary>
public sealed class LocalSendReceiveCoordinator : IReceiveConsent, IDisposable
{
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(2);

    private readonly AgentControlCenter _center;
    private readonly OperationAudit _audit;
    private LocalSendReceiveOptions _options;
    private readonly Action<string>? _log;
    private LocalSendReceiver? _receiver;

    public LocalSendReceiveCoordinator(
        AgentControlCenter center,
        OperationAudit audit,
        LocalSendReceiveOptions options,
        Action<string>? log = null)
    {
        _center = center ?? throw new ArgumentNullException(nameof(center));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;
    }

    public bool IsRunning => _receiver?.IsRunning == true;

    public int Port => _receiver?.Port ?? 0;

    public string? LastError { get; private set; }

    public string SaveDirectory => _options.SaveDirectory;

    public string Alias => _options.Alias;

    public bool Start()
    {
        if (_receiver is not null) return IsRunning;

        Directory.CreateDirectory(_options.SaveDirectory);
        var receiver = new LocalSendReceiver(_options, this);
        receiver.FilesReceived += OnFilesReceived;

        if (!receiver.Start(System.Net.IPAddress.Any, _options.Port))
        {
            LastError = receiver.LastError ?? "端口被占用或权限不足";
            receiver.Dispose();
            _log?.Invoke($"LocalSend 接收端未能启动：{LastError}");
            return false;
        }

        _receiver = receiver;
        LastError = null;
        _log?.Invoke($"LocalSend 接收端已监听 0.0.0.0:{receiver.Port}（设备名 {_options.Alias}）");
        return true;
    }

    public void Stop()
    {
        var receiver = _receiver;
        _receiver = null;
        if (receiver is null) return;
        receiver.FilesReceived -= OnFilesReceived;
        receiver.Dispose();
    }

    /// <summary>
    /// Asks the user, through the shared pending queue, whether to accept these files. Denial,
    /// timeout and any failure all answer "no".
    /// </summary>
    public async Task<bool> ConfirmAsync(LocalSendIncoming incoming, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        var task = _center.Enqueue($"接收 {incoming.Files.Count} 个文件 · {incoming.SenderAlias}", "LocalSend", null);
        task.Start();

        var prompt = BuildPrompt(incoming);
        bool accepted;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AnswerTimeout);
            accepted = await task.AskApprovalAsync(prompt, RiskCategory.ExternalTransfer, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            accepted = false;
            _log?.Invoke("LocalSend 接收确认超时，按拒绝处理");
        }
        catch (Exception ex)
        {
            accepted = false;
            _log?.Invoke($"LocalSend 接收确认失败：{ex.GetType().Name}");
        }

        if (accepted) task.Complete();
        else task.Fail("用户拒绝接收或确认超时");

        _audit.Record(new OperationRecord(
            DateTimeOffset.UtcNow,
            "localsend.receive",
            RiskCategory.ExternalTransfer,
            $"接收来自 {incoming.SenderAlias} 的文件",
            _options.SaveDirectory,
            accepted ? "allowed" : "denied",
            accepted ? $"用户同意接收 {incoming.Files.Count} 个文件" : "用户拒绝或未在时限内确认",
            "user",
            0,
            incoming.Files.Select(file => file.FileName).Take(8).ToArray()));

        return accepted;
    }

    private void OnFilesReceived(IReadOnlyList<string> paths)
    {
        _log?.Invoke($"LocalSend 已接收 {paths.Count} 个文件到 {_options.SaveDirectory}");
        _audit.Record(new OperationRecord(
            DateTimeOffset.UtcNow,
            "localsend.saved",
            RiskCategory.ExternalTransfer,
            $"保存 {paths.Count} 个接收文件",
            _options.SaveDirectory,
            "allowed",
            "已写入接收目录",
            "user",
            0,
            paths.Select(Path.GetFileName).OfType<string>().Take(8).ToArray()));
    }

    private static string BuildPrompt(LocalSendIncoming incoming)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"{incoming.SenderAlias} 想发来 {incoming.Files.Count} 个文件，共 {RelayQuotaPolicy.Human(incoming.TotalBytes)}");
        builder.AppendLine($"保存到：{incoming.Files.Count} 个文件将写入接收目录");
        builder.AppendLine("文件：");
        foreach (var file in incoming.Files.Take(10))
            builder.AppendLine($"· {file.FileName}（{RelayQuotaPolicy.Human(file.Size)}）");
        if (incoming.Files.Count > 10)
            builder.AppendLine($"… 还有 {incoming.Files.Count - 10} 个");
        return builder.ToString().TrimEnd();
    }

    /// <summary>Applies changed settings. The listener is stopped so the next Start uses them.</summary>
    public void UpdateOptions(LocalSendReceiveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Stop();
        _options = options;
    }

    public void Dispose() => Stop();
}
