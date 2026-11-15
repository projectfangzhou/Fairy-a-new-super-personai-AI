using System.IO;
using System.Text;
using System.Windows;
using MyAiAssistant.Services.Transfer;

namespace WebRtc.Tests;

/// <summary>
/// Exercises the real transfer surface: two WebRTC peers hosted in WebView2, signalling relayed by
/// the host exactly as the paired channel will relay it between machines, then bytes both ways.
/// </summary>
internal static class Program
{
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        // WebView2 requires an STA thread; without it the COM layer fails with a puzzling error.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 0;

        app.Startup += async (_, _) =>
        {
            try
            {
                await RunAsync();
                Console.WriteLine($"All {_checks} WebRTC transfer checks passed.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAILED: " + ex);
                exitCode = 1;
            }
            finally
            {
                app.Shutdown();
            }
        };

        app.Run();
        return exitCode;
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
    }

    private static void Step(string message) => Console.WriteLine("[step] " + message);

    private static async Task RunAsync()
    {
        var cache = Path.Combine(AppContext.BaseDirectory, "webview2-transfer");

        Step("creating the two surfaces");
        var peerA = await WebRtcPeer.CreateAsync(cache);
        var peerB = await WebRtcPeer.CreateAsync(cache);
        await using var holderA = peerA;
        await using var holderB = peerB;

        var failures = new List<string>();
        peerA.Failed += message => failures.Add("A: " + message);
        peerB.Failed += message => failures.Add("B: " + message);

        // The host is the signalling path: whatever one side emits is handed to the other.
        peerA.SignalOutbound += signal => { _ = peerB.ApplySignalAsync(signal); };
        peerB.SignalOutbound += signal => { _ = peerA.ApplySignalAsync(signal); };

        Step("starting the answering side first, so the offer has somewhere to land");
        await peerB.StartAsync(WebRtcRole.Answerer);
        await peerA.StartAsync(WebRtcRole.Offerer);
        Assert(peerA.IsReady && peerB.IsReady, "both pages reported ready");

        Step("waiting for the data channel");
        var openedA = peerA.WaitForChannelAsync(TimeSpan.FromSeconds(40));
        var openedB = peerB.WaitForChannelAsync(TimeSpan.FromSeconds(40));
        await Task.WhenAll(openedA, openedB);

        Assert(peerA.IsOpen, $"the offering side gets an open channel (state {peerA.State}, error {peerA.LastError})");
        Assert(peerB.IsOpen, $"the answering side gets an open channel (state {peerB.State}, error {peerB.LastError})");
        Assert(failures.Count == 0, "no page-level errors while connecting: " + string.Join("; ", failures));

        Step("sending a text payload");
        var textBytes = Encoding.UTF8.GetBytes("仙灵 WebRTC 通道测试");
        var textReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        peerB.BytesReceived += bytes => textReceived.TrySetResult(bytes);
        await peerA.SendAsync(textBytes);

        Assert(await Task.WhenAny(textReceived.Task, Task.Delay(TimeSpan.FromSeconds(20))) == textReceived.Task,
            "the payload crosses the channel");
        Assert(textReceived.Task.Result.SequenceEqual(textBytes), "the payload arrives byte-for-byte, non-ASCII included");

        Step("sending 256 KB of random bytes");
        var bulk = new byte[256 * 1024];
        Random.Shared.NextBytes(bulk);
        var bulkStarted = DateTime.UtcNow;
        var bulkReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        peerB.BytesReceived += bytes => bulkReceived.TrySetResult(bytes);
        await peerA.SendAsync(bulk);

        Assert(await Task.WhenAny(bulkReceived.Task, Task.Delay(TimeSpan.FromSeconds(30))) == bulkReceived.Task,
            "a 256 KB payload crosses the channel");
        Assert(bulkReceived.Task.Result.SequenceEqual(bulk), "the bulk payload arrives unchanged");

        // The bridge is the bottleneck, not the crypto, so the rate is worth stating out loud.
        var bulkSeconds = (DateTime.UtcNow - bulkStarted).TotalSeconds;
        Step($"256 KB took {bulkSeconds:0.00} s (~{bulk.Length / 1024.0 / Math.Max(0.001, bulkSeconds):0} KB/s)");
        Assert(bulkSeconds < 10, "a quarter megabyte crosses in well under ten seconds");

        Step("sending back the other way");
        var reverse = Encoding.UTF8.GetBytes("pong");
        var reverseReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        peerA.BytesReceived += bytes => reverseReceived.TrySetResult(bytes);
        await peerB.SendAsync(reverse);

        Assert(await Task.WhenAny(reverseReceived.Task, Task.Delay(TimeSpan.FromSeconds(20))) == reverseReceived.Task &&
               reverseReceived.Task.Result.SequenceEqual(reverse),
            "the channel carries data in both directions");

        Step("closing");
        await peerA.CloseAsync();
        var closed = await peerB.WaitForCloseAsync(TimeSpan.FromSeconds(20));
        Assert(closed || !peerB.IsOpen, "closing one side is observed by the other");

        // Signals round-trip through the wire format the paired channel will carry.
        var signal = new TransferSignal(TransferSignalKind.Offer, "{\"type\":\"offer\",\"sdp\":\"v=0\"}");
        var parsed = TransferSignal.Parse(signal.ToJson());
        Assert(parsed is not null && parsed.Kind == TransferSignalKind.Offer && parsed.Payload == signal.Payload,
            "a signal survives being serialised and parsed");
        Assert(TransferSignal.Parse("not json") is null, "a malformed signal is refused, not thrown");
        Assert(TransferSignal.Parse("{\"kind\":\"Nonsense\",\"payload\":\"x\"}") is null, "an unknown signal kind is refused");
    }
}
