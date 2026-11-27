using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MyAiAssistant.Models;

namespace MyAiAssistant.Services.Messaging;

/// <summary>Outcome of a loopback round trip against the web chat endpoint.</summary>
public sealed record WebChatSelfTest(bool Ok, string Message, double RoundTripMs);

/// <summary>
/// The built-in web chat surface: a loopback WebSocket server that answers chat turns with the
/// assistant, streaming the reply as it arrives.
///
/// Shape of a connection: handshake, a welcome frame, then one JSON object per frame in each
/// direction (see <see cref="WebChatProtocol"/>). Each connection keeps its own short history, so
/// two clients never see each other's conversation and a client that disconnects simply starts
/// fresh next time. Turns on one connection are answered in order.
///
/// Raw <see cref="TcpListener"/> rather than <see cref="HttpListener"/>, for the reason documented
/// on <c>LocalHttpServer</c>: HTTP.sys fails with "invalid handle" in some Windows sessions, and a
/// surface that cannot open is indistinguishable from a broken feature.
/// </summary>
public sealed class WebChatEndpoint : IDisposable
{
    public const int DefaultPort = 9881;

    /// <summary>Messages kept per connection. More than this and the model drowns in context.</summary>
    public const int DefaultMaxHistoryMessages = 20;

    private readonly IChatResponder _responder;
    private readonly int _maxHistoryMessages;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _clients;

    public WebChatEndpoint(IChatResponder responder, int maxHistoryMessages = DefaultMaxHistoryMessages)
    {
        _responder = responder ?? throw new ArgumentNullException(nameof(responder));
        _maxHistoryMessages = Math.Max(0, maxHistoryMessages);
    }

    public int Port { get; private set; } = DefaultPort;

    public bool IsRunning => _listener is not null;

    /// <summary>How many clients are connected right now.</summary>
    public int ClientCount => Volatile.Read(ref _clients);

    public string Url => $"ws://127.0.0.1:{Port}/";

    /// <summary>Binds loopback and starts accepting. Port 0 asks the OS for a free port.</summary>
    public void Start(int port = DefaultPort)
    {
        if (_listener is not null) return;
        if (port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "端口必须在 0-65535 之间。");

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Connects to this endpoint as an ordinary WebSocket client and completes a ping round trip.
    /// Readiness is only reported after a real handshake and a real framed answer, because a probe
    /// that merely opened a port would not prove a client can talk to it.
    /// </summary>
    public async Task<WebChatSelfTest> SelfTestAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning) return new(false, "服务未启动。", 0);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, Port, cancellationToken).ConfigureAwait(false);
            var stream = client.GetStream();

            var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            await WebSocketFraming.WriteTextAsync(stream,
                "GET / HTTP/1.1\r\n" +
                "Host: 127.0.0.1\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Key: {key}\r\n" +
                "Sec-WebSocket-Version: 13\r\n\r\n", cancellationToken).ConfigureAwait(false);

            var header = await WebSocketFraming.ReadHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
            if (header is null)
                return new(false, "握手没有回应。", stopwatch.Elapsed.TotalMilliseconds);

            var statusLine = header.Split("\r\n")[0];
            if (!statusLine.StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
                return new(false, "握手被拒绝：" + statusLine, stopwatch.Elapsed.TotalMilliseconds);

            if (!header.Contains(WebSocketFraming.Accept(key), StringComparison.Ordinal))
                return new(false, "Sec-WebSocket-Accept 校验失败。", stopwatch.Elapsed.TotalMilliseconds);

            var welcome = await WebSocketFraming.ReadTextFrameAsync(stream, cancellationToken).ConfigureAwait(false);
            if (welcome is null || !welcome.Contains("\"welcome\"", StringComparison.Ordinal))
                return new(false, "没有收到 welcome 帧。", stopwatch.Elapsed.TotalMilliseconds);

            await WebSocketFraming.WriteMaskedFrameAsync(stream, Json(type: "ping"), cancellationToken)
                .ConfigureAwait(false);
            var answer = await WebSocketFraming.ReadTextFrameAsync(stream, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            return answer is not null && answer.Contains("\"pong\"", StringComparison.Ordinal)
                ? new(true, $"本机 WebSocket 对话服务正常（{Url}）。", stopwatch.Elapsed.TotalMilliseconds)
                : new(false, "ping 没有得到 pong。", stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            stopwatch.Stop();
            return new(false, $"无法连接本机端口 {Port}（{ex.GetType().Name}）。",
                stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private static string Json(string type) => $"{{\"type\":\"{type}\"}}";

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client, token), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        Interlocked.Increment(ref _clients);
        try
        {
            using (client)
            {
                var stream = client.GetStream();

                var key = await WebSocketFraming.ReadHandshakeKeyAsync(stream, token).ConfigureAwait(false);
                if (key is null)
                {
                    await WebSocketFraming.WriteBadRequestAsync(stream).ConfigureAwait(false);
                    return;
                }

                await WebSocketFraming.WriteHandshakeAsync(stream, key, token).ConfigureAwait(false);
                await WebSocketFraming.WriteFrameAsync(stream, WebChatProtocol.Welcome(), token)
                    .ConfigureAwait(false);

                var history = new List<ChatMessage>();

                while (!token.IsCancellationRequested)
                {
                    var frame = await WebSocketFraming.ReadTextFrameAsync(stream, token).ConfigureAwait(false);
                    if (frame is null) return;

                    var incoming = WebChatProtocol.Parse(frame);
                    switch (incoming.Kind)
                    {
                        case WebChatMessageKind.Ping:
                            await WebSocketFraming.WriteFrameAsync(stream, WebChatProtocol.Pong(), token)
                                .ConfigureAwait(false);
                            break;

                        case WebChatMessageKind.Reset:
                            history.Clear();
                            await WebSocketFraming.WriteFrameAsync(stream, WebChatProtocol.ResetAck(), token)
                                .ConfigureAwait(false);
                            break;

                        case WebChatMessageKind.Invalid:
                            await WebSocketFraming.WriteFrameAsync(stream,
                                WebChatProtocol.Error(incoming.Error ?? "消息无效。"), token).ConfigureAwait(false);
                            break;

                        case WebChatMessageKind.Unknown:
                            await WebSocketFraming.WriteFrameAsync(stream,
                                WebChatProtocol.Error("未知的 type，支持 message / ping / reset。"), token)
                                .ConfigureAwait(false);
                            break;

                        case WebChatMessageKind.Message:
                            await AnswerAsync(stream, history, incoming.Text, token).ConfigureAwait(false);
                            break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // A client that walks away mid-answer is normal traffic, not a failure to report.
        }
        finally
        {
            Interlocked.Decrement(ref _clients);
        }
    }

    private async Task AnswerAsync(NetworkStream stream, List<ChatMessage> history, string prompt,
        CancellationToken token)
    {
        var reply = new StringBuilder();

        try
        {
            await foreach (var chunk in _responder.RespondAsync(history, prompt, token).ConfigureAwait(false))
            {
                if (string.IsNullOrEmpty(chunk)) continue;
                reply.Append(chunk);
                await WebSocketFraming.WriteFrameAsync(stream, WebChatProtocol.Delta(chunk), token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The exception type only: provider errors can carry the request URL and the key.
            await WebSocketFraming.WriteFrameAsync(stream,
                WebChatProtocol.Error($"生成回复失败（{ex.GetType().Name}）。"), token).ConfigureAwait(false);
            return;
        }

        if (reply.Length == 0)
        {
            await WebSocketFraming.WriteFrameAsync(stream,
                WebChatProtocol.Error("模型没有返回内容。"), token).ConfigureAwait(false);
            return;
        }

        history.Add(new ChatMessage { Role = "user", Content = prompt });
        history.Add(new ChatMessage { Role = "assistant", Content = reply.ToString() });
        TrimHistory(history);

        await WebSocketFraming.WriteFrameAsync(stream, WebChatProtocol.Done(reply.Length), token)
            .ConfigureAwait(false);
    }

    private void TrimHistory(List<ChatMessage> history)
    {
        var overflow = history.Count - Math.Max(2, _maxHistoryMessages);
        if (overflow > 0) history.RemoveRange(0, overflow);
    }
}
