using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace MyAiAssistant.Services;

public sealed record AdbDevice(string Serial, string State, string TransportId)
{
    public override string ToString() => Serial;
}

public sealed record AdbProcessResult(int ExitCode, string Output, string Error);

public interface IAdbProcessRunner
{
    Task<AdbProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct);
}

public sealed class AdbProcessRunner : IAdbProcessRunner
{
    public async Task<AdbProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = new Process { StartInfo = start };
        timeout.Token.ThrowIfCancellationRequested();
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return new(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
            throw;
        }
    }
}

public sealed class AdbInputService
{
    private readonly string _executable;
    private readonly IAdbProcessRunner _runner;
    private static readonly IReadOnlyDictionary<string, string> Keys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["BACK"] = "4", ["HOME"] = "3", ["APP_SWITCH"] = "187",
        ["VOLUME_UP"] = "24", ["VOLUME_DOWN"] = "25"
    };
    public static IEnumerable<string> SupportedKeys => Keys.Keys;

    public AdbInputService(string executable, IAdbProcessRunner? runner = null)
    {
        if (!Path.IsPathFullyQualified(executable) || !string.Equals(Path.GetFileName(executable), "adb.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择 Android platform-tools 中 adb.exe 的完整路径。", nameof(executable));
        _executable = executable;
        _runner = runner ?? new AdbProcessRunner();
    }

    private async Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await _runner.RunAsync(_executable, args, ct);
        if (result.ExitCode != 0) throw new InvalidOperationException($"ADB 失败 ({result.ExitCode}): {result.Error.Trim()}");
        return result.Output;
    }

    public static IReadOnlyList<AdbDevice> ParseDevices(string output)
    {
        var devices = new List<AdbDevice>();
        foreach (var line in output.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || fields[0] == "List" || fields[0] == "*") continue;
            var transport = fields.FirstOrDefault(f => f.StartsWith("transport_id:", StringComparison.Ordinal))?[13..] ?? "";
            if (transport.Length == 0 || !transport.All(char.IsAsciiDigit)) continue;
            devices.Add(new(fields[0], fields[1], transport));
        }
        return devices;
    }

    private async Task<bool> IsUsbAsync(AdbDevice device, CancellationToken ct)
    {
        var path = (await RunAsync(new[] { "-t", device.TransportId, "get-devpath" }, ct)).Trim();
        return path.StartsWith("usb:", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<AdbDevice>> GetAuthorizedUsbDevicesAsync(CancellationToken ct = default)
    {
        var devices = ParseDevices(await RunAsync(new[] { "devices", "-l" }, ct));
        var authorized = new List<AdbDevice>();
        foreach (var device in devices.Where(d => d.State == "device"))
            if (await IsUsbAsync(device, ct)) authorized.Add(device);
        return authorized;
    }

    private async Task SendAsync(string serial, string[] input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serial)) throw new ArgumentException("必须显式选择目标手机 serial。", nameof(serial));
        var devices = ParseDevices(await RunAsync(new[] { "devices", "-l" }, ct));
        var matches = devices.Where(d => string.Equals(d.Serial, serial, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || matches[0].State != "device")
            throw new InvalidOperationException("目标手机未连接、未授权或 serial 不唯一，请刷新并重新选择。");
        var target = matches[0];
        if (!await IsUsbAsync(target, ct)) throw new InvalidOperationException("仅允许本机 USB ADB 设备。");
        // Pin the enumerated transport so reconnecting cannot redirect this operation to another device.
        await RunAsync(new[] { "-t", target.TransportId, "shell", "input" }.Concat(input).ToArray(), ct);
    }

    private static string Coordinate(int value)
    {
        if (value < 0 || value > 32767) throw new ArgumentOutOfRangeException(nameof(value), "坐标范围为 0–32767。");
        return value.ToString(CultureInfo.InvariantCulture);
    }

    public Task TapAsync(string serial, int x, int y, CancellationToken ct = default)
        => SendAsync(serial, new[] { "tap", Coordinate(x), Coordinate(y) }, ct);

    public Task SwipeAsync(string serial, int x, int y, int x2, int y2, int durationMs, CancellationToken ct = default)
    {
        if (durationMs < 50 || durationMs > 5000) throw new ArgumentOutOfRangeException(nameof(durationMs), "时长范围为 50–5000 毫秒。");
        return SendAsync(serial, new[] { "swipe", Coordinate(x), Coordinate(y), Coordinate(x2), Coordinate(y2), durationMs.ToString(CultureInfo.InvariantCulture) }, ct);
    }

    public Task KeyAsync(string serial, string key, CancellationToken ct = default)
    {
        if (!Keys.TryGetValue(key, out var code)) throw new ArgumentException("不支持的按键。", nameof(key));
        return SendAsync(serial, new[] { "keyevent", code }, ct);
    }
}
