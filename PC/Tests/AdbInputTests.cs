using MyAiAssistant.Services;

internal static class AdbInputTests
{
    private const string Listing = "List of devices attached\r\nPHONE_A device product:p model:m transport_id:1\r\nPHONE_B device transport_id:2\r\nLOCKED unauthorized transport_id:3\r\nOFF offline transport_id:4\r\n192.168.1.2:5555 device transport_id:5\r\n";
    private static int _checks;

    public static async Task Main()
    {
        var runner = new FakeRunner { Listing = Listing };
        var service = new AdbInputService(@"C:\platform-tools\adb.exe", runner);
        var devices = await service.GetAuthorizedUsbDevicesAsync();
        Check(devices.Select(d => d.Serial).SequenceEqual(new[] { "PHONE_A", "PHONE_B" }), "Only authorized USB devices listed");
        Check(!runner.Commands.Any(IsInput), "Discovery never injects input");
        await Reject(() => service.TapAsync("", 1, 2), runner, "Multiple devices require explicit selection");
        await Reject(() => service.TapAsync("LOCKED", 1, 2), runner, "Unauthorized device rejected");
        await Reject(() => service.TapAsync("OFF", 1, 2), runner, "Offline device rejected");
        await Reject(() => service.TapAsync("MISSING", 1, 2), runner, "Missing serial rejected");
        await Reject(() => service.TapAsync("192.168.1.2:5555", 1, 2), runner, "TCP device rejected");
        await Reject(() => service.TapAsync("PHONE_A;reboot", 1, 2), runner, "Injected serial cannot select target");
        await Reject(() => service.TapAsync("PHONE_A", -1, 2), runner, "Negative coordinate rejected");
        await Reject(() => service.TapAsync("PHONE_A", 32768, 2), runner, "Coordinate bound enforced");
        await Reject(() => service.SwipeAsync("PHONE_A", 1, 2, 3, 4, 5001), runner, "Duration bound enforced");
        await Reject(() => service.KeyAsync("PHONE_A", "BACK; reboot"), runner, "Key injection rejected");
        await Reject(() => service.KeyAsync("PHONE_A", "POWER"), runner, "Non-whitelisted key rejected");
        await service.TapAsync("PHONE_B", 10, 20);
        Check(runner.Commands.Last().SequenceEqual(new[] { "-t", "2", "shell", "input", "tap", "10", "20" }), "Tap pinned to chosen device transport");
        await service.SwipeAsync("PHONE_A", 0, 1, 200, 400, 300);
        Check(runner.Commands.Last().SequenceEqual(new[] { "-t", "1", "shell", "input", "swipe", "0", "1", "200", "400", "300" }), "Swipe sends bounded numeric arguments");
        await service.KeyAsync("PHONE_B", "BACK");
        Check(runner.Commands.Last().SequenceEqual(new[] { "-t", "2", "shell", "input", "keyevent", "4" }), "Back mapped to fixed key code");
        runner.Listing = "PHONE_A unauthorized transport_id:1";
        await Reject(() => service.TapAsync("PHONE_A", 1, 2), runner, "Authorization rechecked before every action");
        runner.Listing = "PHONE_A device transport_id:1\nPHONE_A device transport_id:2";
        await Reject(() => service.TapAsync("PHONE_A", 1, 2), runner, "Duplicate serial rejected");
        runner.Listing = "PHONE_A device transport_id:1";
        await Reject(() => service.TapAsync("", 1, 2), runner, "Single device still requires explicit selection");
        runner.FailInput = true;
        try { await service.TapAsync("PHONE_A", 1, 2); throw new Exception("Expected process error"); }
        catch (InvalidOperationException) { _checks++; }
        Check(runner.Commands.Last().Contains("input"), "ADB error does not trigger another backend");
        runner.FailInput = false;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await service.TapAsync("PHONE_A", 1, 2, cancelled.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { _checks++; }
        Console.WriteLine($"PASS: {_checks} checks; fake ADB only, no physical device input.");
    }

    private static bool IsInput(string[] args) => args.Contains("input");
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
    }
    private static async Task Reject(Func<Task> action, FakeRunner runner, string name)
    {
        var before = runner.Commands.Count(IsInput);
        var rejected = false;
        try { await action(); }
        catch (ArgumentException) { rejected = true; }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && before == runner.Commands.Count(IsInput), name);
    }

    private sealed class FakeRunner : IAdbProcessRunner
    {
        public string Listing { get; set; } = "";
        public bool FailInput { get; set; }
        public List<string[]> Commands { get; } = new();
        public Task<AdbProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var args = arguments.ToArray();
            Commands.Add(args);
            if (args.SequenceEqual(new[] { "devices", "-l" })) return Task.FromResult(new AdbProcessResult(0, Listing, ""));
            if (args.Length == 3 && args[0] == "-t" && args[2] == "get-devpath")
                return Task.FromResult(new AdbProcessResult(0, args[1] == "5" ? "unknown" : "usb:1-2", ""));
            if (IsInput(args)) return Task.FromResult(new AdbProcessResult(FailInput ? 1 : 0, "", FailInput ? "transport disconnected" : ""));
            throw new Exception("Unexpected ADB command: " + string.Join(" ", args));
        }
    }
}
