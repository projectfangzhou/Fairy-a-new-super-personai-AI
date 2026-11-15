using MyAiAssistant.Services.Notifications;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

// The binary lives in the repository under Dependencies/uda; walk up from the test binary.
static string? FindLibrary()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        var candidate = Path.Combine(directory.FullName, "Dependencies", "uda", "uda_ffi.dll");
        if (File.Exists(candidate)) return candidate;
        directory = directory.Parent;
    }
    return null;
}

// ---------------------------------------------------------------- a missing library is a clean failure

{
    var missing = UdaSystemNotificationChannel.TryCreate(null, @"Z:\nope\uda_ffi.dll", out var missingError);
    Assert(missing is null, "a library that does not exist yields no channel");
    Assert(missingError is not null, "and says why");
    Assert(!string.IsNullOrWhiteSpace(missingError), "the reason is not blank");
}

// ---------------------------------------------------------------- real delivery

var library = FindLibrary();
if (library is null)
{
    // No binary in the tree: the honest outcome is "not delivered", and the failure must be
    // explained rather than swallowed.
    var absent = UdaSystemNotificationChannel.TryCreate(null, null, out var absentError);
    Assert(absent is null && absentError is not null, "without the binary the channel is unavailable, with a reason");
    Console.WriteLine($"未投递：仓库里没有 uda_ffi.dll（{absentError}）");
    Console.WriteLine($"All {checks} UDA notification checks passed (delivery not exercised).");
}
else
{
    var channel = UdaSystemNotificationChannel.TryCreate(null, library, out var loadError);
    Assert(channel is not null, $"the library in Dependencies/uda loads: {loadError}");
    Assert(channel!.LoadedFrom.EndsWith("uda_ffi.dll", StringComparison.OrdinalIgnoreCase),
        "the channel reports which file it loaded");
    Assert(channel.IsAvailable, "the channel starts available");

    var shown = channel.Show("仙灵自检", "这是一条来自仙灵的测试通知（UDA / WinRT toast）", NotificationSeverity.Info);
    if (shown)
    {
        Assert(channel.LastError is null, "a successful delivery leaves no error behind");
    }
    else
    {
        // Unpackaged Windows processes are refused by WinRT unless the AUMID is registered through a
        // packaged identity or a Start-menu shortcut. What matters here is that the channel reports
        // the platform's own words instead of a bare "false".
        var reason = channel.LastError ?? "";
        Assert(reason.Contains("程序包标识符", StringComparison.Ordinal) ||
               reason.Contains("0x80073D54", StringComparison.Ordinal),
            $"the failure carries the platform's own reason: {reason}");
        Console.WriteLine($"未投递（未打包进程无法发 WinRT toast）：{reason}");
    }

    // The remaining calls must behave the same way: no exception, and Chinese/empty/long text must not
    // be rejected locally as "invalid argument" — that would be our bug, not the platform's.
    foreach (var (title, body, label) in new[]
             {
                 ("中文标题", "正文里有中文、符号（括号）和数字 123", "UTF-8 文本"),
                 ("只有标题", "", "空正文"),
                 ("长文本", new string('测', 400), "超长正文"),
             })
    {
        var ok = channel.Show(title, body, NotificationSeverity.Warning);
        var reason = channel.LastError ?? "";
        Assert(ok || !reason.Contains("参数不合法", StringComparison.Ordinal),
            $"{label} is not rejected as an invalid argument");
    }

    Assert(channel.LoadedFrom.EndsWith("uda_ffi.dll", StringComparison.OrdinalIgnoreCase),
        "the channel reports which file it loaded");

    // Without an explicit path in this test layout the search finds nothing, and the failure has to
    // name the places it looked — a bare "failed" would leave the next person guessing.
    var implicitChannel = UdaSystemNotificationChannel.TryCreate(null, null, out var implicitError);
    if (implicitChannel is null)
        Assert(implicitError!.Contains("Dependencies", StringComparison.Ordinal),
            "the failure names the search locations");
    else
        Assert(implicitChannel.IsAvailable, "an implicitly located library is usable");

    Console.WriteLine(shown
        ? $"All {checks} UDA notification checks passed (toast delivered from {Path.GetFileName(library)})."
        : $"All {checks} UDA notification checks passed (library loaded, toast refused by Windows: unpackaged process).");
}
