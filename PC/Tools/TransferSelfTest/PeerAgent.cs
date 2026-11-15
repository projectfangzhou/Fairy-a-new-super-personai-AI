using System.IO;
using System.Net.Http;
using System.Windows;
using MyAiAssistant.Services;
using MyAiAssistant.Services.Transfer;

namespace TransferSelfTest;

/// <summary>
/// The joining device's side of the self-test: one signalling channel, demultiplexed by kind, over
/// which it first answers the host's connection and then opens its own.
///
/// This is what a future Android or Linux client will do. It routes an offer to the session that
/// will answer, and an answer or candidate to the session that offered — a real device has one
/// signalling path, so two independent pollers would steal each other's messages.
/// </summary>
internal sealed class PeerAgent : IDisposable
{
    private static readonly TimeSpan ChannelTimeout = TimeSpan.FromSeconds(60);

    private readonly SignalClient _signals;
    private readonly byte[] _groupKey;
    private readonly string _destination;
    private readonly string _hostDeviceId;
    private readonly TestLog _log;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<TransferSession> _answerers = [];

    private TransferSession? _offerer;
    private TransferSession? _connecting;
    private Task? _pump;
    private bool _disposed;

    public PeerAgent(SignalClient signals, byte[] groupKey, string destination, string hostDeviceId, TestLog log)
    {
        _signals = signals;
        _groupKey = groupKey;
        _destination = destination;
        _hostDeviceId = hostDeviceId;
        _log = log;
    }

    public void Start() => _pump = Task.Run(PumpAsync);

    public async Task<TransferCommandResult> SendAsync(IReadOnlyList<string> paths, string senderName)
    {
        var channel = await CreateChannelAsync("发送用");
        var session = new TransferSession(channel, _groupKey, _destination, offer =>
        {
            _log.Info($"对方想发送 {offer.Manifest.Files.Count} 个文件，自检自动同意");
            return Task.FromResult(true);
        });

        session.OutboundSignal += signal => _ = _signals.SendAsync(_hostDeviceId, signal);
        session.Refused += refusal => _log.Fail($"发送过程中被拒绝：{refusal.Message}（可重试={refusal.Retryable}）");
        session.FileReceived += progress => _log.Ok($"收到 {progress.File.Name}（{progress.File.Size} 字节）");

        _offerer = session;
        _connecting = session;

        _log.Step("发起连接（等对方接受）");
        if (!await session.StartChannelAsync(WebRtcRole.Offerer))
            return new TransferCommandResult(false, session.LastError ?? "通道无法启动");

        if (!await session.WaitForChannelAsync(ChannelTimeout))
            return new TransferCommandResult(false, "连接没有建立起来（信令或 ICE 失败，见日志）");

        _log.Ok("数据通道已建立");
        var bytes = await session.SendFilesAsync(paths, senderName);
        return new TransferCommandResult(true, $"已发送 {paths.Count} 个文件（{bytes} 字节上线）");
    }

    private async Task PumpAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                var poll = await _signals.PollAsync(_cancellation.Token);
                if (!poll.Ok)
                {
                    _log.Fail("轮询信令失败：" + poll.Error);
                    await Task.Delay(1000, _cancellation.Token);
                    continue;
                }

                foreach (var received in poll.Signals)
                {
                    switch (received.Signal.Kind)
                    {
                        case TransferSignalKind.Offer:
                            await AcceptOfferAsync(received.Signal);
                            break;
                        case TransferSignalKind.Answer:
                            _log.Info("收到对方的应答");
                            if (_offerer is not null) await _offerer.DeliverSignalAsync(received.Signal);
                            else _log.Fail("收到应答但没有正在发起的连接");
                            break;
                        case TransferSignalKind.Ice:
                            if (_connecting is not null) await _connecting.DeliverSignalAsync(received.Signal);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or HttpRequestException)
            {
                // Shutting down: the client is gone, which is not a test failure.
                return;
            }
            catch (Exception ex)
            {
                _log.Fail("信令轮询异常：" + ex.Message, ex);
                return;
            }

            try { await Task.Delay(60, _cancellation.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task AcceptOfferAsync(TransferSignal offer)
    {
        _log.Step("收到对方的连接请求，作为应答方接入");
        var channel = await CreateChannelAsync("接收用");
        var session = new TransferSession(channel, _groupKey, _destination, value =>
        {
            _log.Info($"对方想发送 {value.Manifest.Files.Count} 个文件（{value.TotalBytes} 字节），自检自动同意");
            return Task.FromResult(true);
        });

        session.OutboundSignal += signal => _ = _signals.SendAsync(_hostDeviceId, signal);
        session.Refused += refusal => _log.Fail($"接收过程中被拒绝：{refusal.Message}");
        session.FileReceived += progress => _log.Ok($"收到 {progress.File.Name}（{progress.File.Size} 字节）");

        _answerers.Add(session);
        _connecting = session;

        if (!await session.StartChannelAsync(WebRtcRole.Answerer))
        {
            _log.Fail("应答方通道无法启动：" + (session.LastError ?? "未知"));
            return;
        }

        await session.DeliverSignalAsync(offer);
    }

    private static async Task<ITransferChannel> CreateChannelAsync(string purpose)
    {
        var dispatcher = Application.Current?.Dispatcher
                         ?? throw new InvalidOperationException("没有 UI 线程，无法创建 WebRTC 面");
        var cache = Path.Combine(AppContext.BaseDirectory, "webview2-selftest-" + Environment.ProcessId);

        return await dispatcher.InvokeAsync(() => WebRtcPeer.CreateAsync(cache)).Task.Unwrap();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cancellation.Cancel();
        try { _pump?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { /* shutting down */ }
        _cancellation.Dispose();
    }
}
