using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MyAiAssistant.Services.Transfer;

/// <summary>
/// A WebRTC data channel, hosted in a WebView2 page.
///
/// The engine is the Chromium one that WebView2 already ships, so a direct device-to-device
/// connection costs no extra runtime and no extra licence: the app only supplies the page, relays
/// signalling and moves bytes across the bridge. That bridge is JSON, so bytes are base64 on the
/// way in and out — the cost is on this side of the socket, not on the wire between devices.
///
/// Nothing here decides who may connect. Authorisation happens before a peer is ever created: only
/// a device that already holds the group key is allowed to exchange signalling with us.
/// </summary>
public sealed class WebRtcPeer : ITransferChannel
{
    /// <summary>Host candidates are hidden behind mDNS names by default, which same-LAN peers cannot resolve.</summary>
    private const string BrowserArguments = "--disable-features=WebRtcHideLocalIpsWithMdns";

    /// <summary>How much outbound data to collect before crossing the bridge once.</summary>
    private const int OutboundBatchBytes = 512 * 1024;

    private static readonly TimeSpan OutboundBatchDelay = TimeSpan.FromMilliseconds(60);

    private readonly Window _window;
    private readonly WebView2 _view;
    private readonly TaskCompletionSource<bool> _pageReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _channelOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _channelClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<byte[]> _outbound = [];
    private readonly Timer _flushTimer;
    private readonly object _outboundGate = new();
    private DateTimeOffset _lastFlush = DateTimeOffset.MinValue;

    private WebRtcPeer(Window window, WebView2 view)
    {
        _window = window;
        _view = view;

        // A batch is only completed by the next chunk arriving; without a timer the last partial
        // batch would wait until the connection closes, and the receiver would never see it.
        _flushTimer = new Timer(_ => FlushOutbound(), null, TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(50));
    }

    /// <summary>Signalling that has to be handed to the other device. Null after the channel opens.</summary>
    public event Action<TransferSignal>? SignalOutbound;

    public event Action? ChannelOpened;

    public event Action? ChannelClosed;

    public event Action<byte[]>? BytesReceived;

    /// <summary>A page-level failure, already turned into something readable.</summary>
    public event Action<string>? Failed;

    public bool IsOpen { get; private set; }

    public string State { get; private set; } = "new";

    public bool IsReady => _pageReady.Task.IsCompletedSuccessfully;

    public string? LastError { get; private set; }

    /// <summary>
    /// Creates the surface. Must run on the UI thread: WebView2 needs an STA thread with a
    /// message pump, and the hidden window that hosts the control is what supplies it.
    /// </summary>
    public static async Task<WebRtcPeer> CreateAsync(string userDataFolder, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userDataFolder)) throw new ArgumentException("缓存目录不能为空", nameof(userDataFolder));

        var app = Application.Current ?? throw new InvalidOperationException("WebRTC 面需要 WPF 应用上下文");
        if (!app.Dispatcher.CheckAccess())
            throw new InvalidOperationException("WebRTC 面必须在 UI 线程创建");

        Directory.CreateDirectory(userDataFolder);

        var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder,
            new CoreWebView2EnvironmentOptions(BrowserArguments));

        var window = new Window
        {
            Width = 320,
            Height = 240,
            Left = -4000,
            Top = -4000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Title = "仙灵传输面"
        };

        var view = new WebView2();
        window.Content = view;

        var peer = new WebRtcPeer(window, view);

        try
        {
            // The control needs a live window before it will initialise: without a surface the
            // navigation never starts and the page never reports ready.
            window.Show();
            await view.EnsureCoreWebView2Async(environment);
            view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            view.CoreWebView2.Settings.IsStatusBarEnabled = false;
            view.CoreWebView2.WebMessageReceived += peer.OnWebMessage;
        }
        catch (Exception ex)
        {
            try { window.Close(); } catch (Exception) { /* nothing to keep */ }
            throw new InvalidOperationException($"WebRTC 面无法启动（WebView2 运行时缺失或不可用）：{ex.Message}", ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return peer;
    }

    /// <summary>Loads the page and starts the session in the given role.</summary>
    public async Task StartAsync(WebRtcRole role)
    {
        // Callers are not necessarily on the UI thread: the session opens the channel from whichever
        // thread is driving the transfer, and touching the control from there throws.
        await _view.Dispatcher.InvokeAsync(() => _view.NavigateToString(Page));

        var ready = await WaitAsync(() => _pageReady.Task.IsCompleted, TimeSpan.FromSeconds(20));
        if (!ready) throw new TimeoutException("WebRTC 页面没有就绪");

        await DeliverAsync(JsonSerializer.Serialize(new { type = "init", payload = role == WebRtcRole.Offerer ? "offerer" : "answerer" }));
    }

    /// <summary>Waits until the data channel is open. False means it never came up; see <see cref="LastError"/>.</summary>
    public async Task<bool> WaitForChannelAsync(TimeSpan timeout) =>
        await WaitAsync(() => _channelOpen.Task.IsCompleted, timeout) && IsOpen;

    public async Task<bool> WaitForCloseAsync(TimeSpan timeout) =>
        await WaitAsync(() => _channelClosed.Task.IsCompleted, timeout);

    public async Task ApplySignalAsync(TransferSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var type = signal.Kind switch
        {
            TransferSignalKind.Offer => "offer",
            TransferSignalKind.Answer => "answer",
            TransferSignalKind.Ice => "ice",
            _ => null
        };

        if (type is null) return;
        await DeliverAsync(JsonSerializer.Serialize(new { type, payload = signal.Payload }));
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data)
    {
        if (!IsOpen) throw new InvalidOperationException("数据通道尚未建立");

        await SendChunkAsync(data.ToArray());
    }

    /// <summary>Closes the channel from this side and tells the page to tear the connection down.</summary>
    public async Task CloseAsync()
    {
        try
        {
            FlushOutbound();
            await DeliverAsync("{\"type\":\"close\",\"payload\":\"\"}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The surface is already gone; nothing left to close.
        }
    }

    /// <summary>
    /// Hands one message to the page.
    ///
    /// This uses the control's native JSON channel rather than evaluating a script per message: the
    /// evaluation path costs tens of milliseconds each, which dominated a transfer of many chunks.
    /// </summary>
    private Task DeliverAsync(string message)
    {
        _view.Dispatcher.Invoke(() => _view.CoreWebView2.PostWebMessageAsJson(message));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Hands a chunk to the page, batched.
    ///
    /// Crossing the bridge costs far more than moving the bytes, so several frames are packed into
    /// one message: 512 KB at a time, or whatever has collected after 60 ms of quiet.
    /// </summary>
    private Task SendChunkAsync(byte[] payload)
    {
        bool flush;
        List<byte[]> batch;

        lock (_outboundGate)
        {
            _outbound.Add(payload);
            var total = _outbound.Sum(chunk => chunk.Length);
            flush = total >= OutboundBatchBytes || DateTimeOffset.UtcNow - _lastFlush > OutboundBatchDelay;

            batch = flush ? [.. _outbound] : [];
            if (flush)
            {
                _outbound.Clear();
                _lastFlush = DateTimeOffset.UtcNow;
            }
        }

        if (flush) PostBatch(batch);
        return Task.CompletedTask;
    }

    private void PostBatch(IReadOnlyList<byte[]> frames)
    {
        if (frames.Count == 0) return;

        var message = JsonSerializer.Serialize(new
        {
            type = "bytesBatch",
            payload = frames.Select(frame => Convert.ToBase64String(frame)).ToArray()
        });

        _view.Dispatcher.Invoke(() => _view.CoreWebView2.PostWebMessageAsJson(message));
    }

    /// <summary>Anything not yet handed to the page, sent before the connection is torn down.</summary>
    private void FlushOutbound()
    {
        if (_outbound.Count == 0) return;

        List<byte[]> batch;
        lock (_outboundGate)
        {
            batch = [.. _outbound];
            _outbound.Clear();
            _lastFlush = DateTimeOffset.UtcNow;
        }

        PostBatch(batch);
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        string? raw;
        try
        {
            // The page posts a JSON string, so the message arrives JSON-encoded inside JSON.
            using var envelope = JsonDocument.Parse(args.WebMessageAsJson);
            raw = envelope.RootElement.GetString();
        }
        catch (JsonException)
        {
            return;
        }

        if (raw is null) return;

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            var payload = root.TryGetProperty("payload", out var payloadElement) ? payloadElement.GetString() ?? "" : "";

            switch (type)
            {
                case "ready":
                    _pageReady.TrySetResult(true);
                    break;

                case "state":
                    State = payload;
                    break;

                case "open":
                    IsOpen = true;
                    _channelOpen.TrySetResult(true);
                    ChannelOpened?.Invoke();
                    break;

                case "closed":
                    IsOpen = false;
                    _channelClosed.TrySetResult(true);
                    ChannelClosed?.Invoke();
                    break;

                case "bytes":
                    var bytes = TryFromBase64(payload);
                    if (bytes is not null) BytesReceived?.Invoke(bytes);
                    break;

                case "error":
                    LastError = payload;
                    Failed?.Invoke(payload);
                    break;

                case "offer":
                    SignalOutbound?.Invoke(new TransferSignal(TransferSignalKind.Offer, payload));
                    break;

                case "answer":
                    SignalOutbound?.Invoke(new TransferSignal(TransferSignalKind.Answer, payload));
                    break;

                case "ice":
                    SignalOutbound?.Invoke(new TransferSignal(TransferSignalKind.Ice, payload));
                    break;
            }
        }
        catch (JsonException)
        {
            // A message we cannot parse is dropped rather than allowed to take the channel down.
        }
    }

    private static byte[]? TryFromBase64(string value)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }

        return condition();
    }

    public async ValueTask DisposeAsync()
    {
        IsOpen = false;
        _flushTimer.Dispose();
        await CloseAsync();
        try { _window.Close(); } catch (Exception) { /* already gone */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The page. It only offers, answers and moves bytes; it has no say in who connects, and it
    /// asks for no ICE servers of its own — the app supplies those later if a direct path fails.
    /// </summary>
    private const string Page = """
<!doctype html>
<html><head><meta charset="utf-8"><title>仙灵传输</title></head>
<body>
<script>
let pc = null, dc = null;
let remoteSet = false;
const pendingIce = [];

function post(type, payload) {
  chrome.webview.postMessage(JSON.stringify({ type: type, payload: payload }));
}

function toBase64(buffer) {
  const bytes = new Uint8Array(buffer);
  let binary = '';
  const step = 0x8000;
  for (let i = 0; i < bytes.length; i += step) {
    binary += String.fromCharCode.apply(null, bytes.subarray(i, i + step));
  }
  return btoa(binary);
}

function fromBase64(text) {
  const binary = atob(text);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

function wire(channel) {
  channel.binaryType = 'arraybuffer';
  channel.onopen = () => post('open', '');
  channel.onclose = () => post('closed', '');
  channel.onerror = e => post('error', 'data channel error');
  channel.onmessage = e => post('bytes', toBase64(e.data));
}

async function start(role) {
  pc = new RTCPeerConnection({ iceServers: [] });
  pc.onicecandidate = e => { if (e.candidate) post('ice', JSON.stringify(e.candidate)); };
  pc.onconnectionstatechange = () => post('state', pc.connectionState);
  pc.oniceconnectionstatechange = () => post('state', pc.iceConnectionState);

  if (role === 'offerer') {
    dc = pc.createDataChannel('transfer', { ordered: true });
    wire(dc);
    await pc.setLocalDescription(await pc.createOffer());
    post('offer', JSON.stringify(pc.localDescription));
  } else {
    pc.ondatachannel = e => { dc = e.channel; wire(dc); };
  }
}

async function acceptRemote(description) {
  await pc.setRemoteDescription(description);
  remoteSet = true;
  while (pendingIce.length) {
    const candidate = pendingIce.shift();
    try { await pc.addIceCandidate(candidate); } catch (e) { post('error', 'candidate failed: ' + e); }
  }
}

async function handle(m) {
  try {
    if (m.type === 'init') { await start(m.payload); return; }
    if (m.type === 'offer') {
      await acceptRemote(JSON.parse(m.payload));
      await pc.setLocalDescription(await pc.createAnswer());
      post('answer', JSON.stringify(pc.localDescription));
      return;
    }
    if (m.type === 'answer') { await acceptRemote(JSON.parse(m.payload)); return; }
    if (m.type === 'ice') {
      const candidate = JSON.parse(m.payload);
      // Candidates can arrive before the remote description; holding them is required, not optional.
      if (!remoteSet) { pendingIce.push(candidate); return; }
      await pc.addIceCandidate(candidate);
      return;
    }
    if (m.type === 'close') { try { if (dc) dc.close(); pc.close(); } catch (e) { } return; }
    if (m.type === 'bytes') {
      if (!dc || dc.readyState !== 'open') { post('error', '通道未打开，字节未发送'); return; }
      dc.send(fromBase64(m.payload));
      return;
    }
    if (m.type === 'bytesBatch') {
      if (!dc || dc.readyState !== 'open') { post('error', '通道未打开，字节未发送'); return; }
      for (const item of m.payload) dc.send(fromBase64(item));
      return;
    }
    post('error', 'unknown message ' + m.type);
  } catch (err) {
    post('error', String(err));
  }
}

// The host posts structured messages; this is the fast path a transfer actually uses.
chrome.webview.addEventListener('message', event => { handle(event.data); });

post('ready', '');
</script>
</body></html>
""";
}
