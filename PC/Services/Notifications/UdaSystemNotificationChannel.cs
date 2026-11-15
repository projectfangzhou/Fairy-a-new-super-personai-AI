using System.IO;
using System.Runtime.InteropServices;

namespace MyAiAssistant.Services.Notifications;

/// <summary>
/// Ordinary system notifications through the UniDesktop API (UDA) C ABI — WinRT
/// <c>ToastNotificationManager</c> on Windows, <c>org.freedesktop.Notifications</c> on Linux.
///
/// UDA is what makes a real Action Center toast possible from an unpackaged process: it registers
/// the given app name as this process's AppUserModelID before the first toast, which is exactly the
/// step a plain WPF executable cannot do on its own. That is why this is preferred over the tray
/// balloon, which is only a fallback.
///
/// Contract taken from <c>Dependencies/uda/uda.h</c> (v0.2.1), not from memory.
/// </summary>
public sealed class UdaSystemNotificationChannel : ISystemNotificationChannel
{
    private const int UdaOk = 0;

    /// <summary>Name shown as the notification source, and used as the Windows toast identity.</summary>
    public const string DefaultAppName = "Xianling.FairyAI";

    private readonly NotifyDelegate _notify;
    private readonly LastErrorMessageDelegate _lastErrorMessage;
    private readonly string _appName;

    private UdaSystemNotificationChannel(NotifyDelegate notify, LastErrorMessageDelegate lastErrorMessage,
        string appName, string loadedFrom)
    {
        _notify = notify;
        _lastErrorMessage = lastErrorMessage;
        _appName = appName;
        LoadedFrom = loadedFrom;
    }

    // Every UDA export runs inside catch_unwind and returns an int32 status; no pointer is owned by
    // the caller except the ones released by uda_free_string, which notifications never return.
    private delegate int NotifyDelegate(IntPtr appName, IntPtr title, IntPtr body, IntPtr icon,
        IntPtr actions, out uint outId);

    /// <summary>Per-thread diagnosis for the most recent failure; the returned string is library-owned.</summary>
    private delegate IntPtr LastErrorMessageDelegate();

    public string Platform => OperatingSystem.IsWindows() ? "windows (uda)"
        : OperatingSystem.IsLinux() ? "linux (uda)"
        : "uda";

    public bool IsAvailable { get; private set; } = true;

    public string? LastError { get; private set; }

    /// <summary>Which file the library was loaded from. Empty when it could not be loaded.</summary>
    public string LoadedFrom { get; } = "";

    /// <summary>
    /// Loads the library. Returns null and an explanation when it is not there — the caller then
    /// falls back to the tray balloon instead of silently losing the notification.
    /// </summary>
    public static UdaSystemNotificationChannel? TryCreate(string? appName, string? libraryPath, out string? error)
    {
        error = null;

        foreach (var candidate in Candidates(libraryPath))
        {
            if (candidate is not null && !File.Exists(candidate)) continue;

            var name = candidate ?? "uda_ffi";
            if (!NativeLibrary.TryLoad(name, out var handle))
            {
                error ??= $"加载 {name} 失败";
                continue;
            }

            if (!NativeLibrary.TryGetExport(handle, "uda_notify", out var export))
            {
                error = $"{name} 里没有 uda_notify（版本不匹配？）";
                NativeLibrary.Free(handle);
                continue;
            }

            var notify = Marshal.GetDelegateForFunctionPointer<NotifyDelegate>(export);

            LastErrorMessageDelegate lastError = () => IntPtr.Zero;
            if (NativeLibrary.TryGetExport(handle, "uda_last_error_message", out var lastErrorExport))
                lastError = Marshal.GetDelegateForFunctionPointer<LastErrorMessageDelegate>(lastErrorExport);

            return new UdaSystemNotificationChannel(notify, lastError,
                string.IsNullOrWhiteSpace(appName) ? DefaultAppName : appName!, candidate ?? name);
        }

        error ??= "找不到 uda_ffi.dll（已查 UDA_LIBRARY、程序目录、Dependencies\\uda）";
        return null;
    }

    /// <summary>Search order matches the project's convention: explicit path, env var, beside the exe, then Dependencies.</summary>
    private static IEnumerable<string?> Candidates(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) yield return explicitPath;

        var fromEnvironment = Environment.GetEnvironmentVariable("UDA_LIBRARY");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) yield return fromEnvironment;

        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        yield return Path.Combine(baseDirectory, "uda_ffi.dll");
        yield return Path.Combine(baseDirectory, "Dependencies", "uda", "uda_ffi.dll");
        yield return Path.Combine(baseDirectory, "..", "..", "..", "Dependencies", "uda", "uda_ffi.dll");
        yield return null; // bare name: let the loader search the PATH
    }

    private static string DescribeStatus(int status) => status switch
    {
        -1 => "UDA 认为参数不合法",
        -2 => "UDA 报告本平台不支持通知",
        -3 => "UDA 探测通知后端失败",
        -4 => "UDA 通知 I/O 失败",
        -5 => "UDA 内部错误",
        -6 => "UDA 捕获到 panic",
        _ => $"UDA 返回未知状态 {status}"
    };

    /// <summary>Pulls the library's own explanation, which is per-thread and valid until the next call.</summary>
    private string DescribeNativeDetail()
    {
        try
        {
            var pointer = _lastErrorMessage();
            if (pointer == IntPtr.Zero) return "";
            var detail = Marshal.PtrToStringUTF8(pointer);
            return string.IsNullOrWhiteSpace(detail) ? "" : $"（{detail}）";
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or ArgumentException)
        {
            return "";
        }
    }

    public bool Show(string title, string body, NotificationSeverity severity)
    {
        if (!IsAvailable) return false;

        IntPtr appName = IntPtr.Zero, titlePointer = IntPtr.Zero, bodyPointer = IntPtr.Zero;
        try
        {
            // UDA takes UTF-8 char*, so Chinese text must not go through the ANSI code page.
            appName = Marshal.StringToCoTaskMemUTF8(_appName);
            titlePointer = Marshal.StringToCoTaskMemUTF8(title ?? string.Empty);
            bodyPointer = Marshal.StringToCoTaskMemUTF8(body ?? string.Empty);

            // icon and actions are optional; the C ABI has no urgency parameter, so severity only
            // reaches the OS on Linux (normal) and not at all on Windows. Reported, not faked.
            var status = _notify(appName, titlePointer, bodyPointer, IntPtr.Zero, IntPtr.Zero, out _);

            if (status == UdaOk)
            {
                LastError = null;
                return true;
            }

            LastError = DescribeStatus(status) + DescribeNativeDetail();

            if (status == -2) IsAvailable = false;
            return false;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            LastError = $"调用 UDA 失败：{ex.GetType().Name}";
            return false;
        }
        finally
        {
            if (appName != IntPtr.Zero) Marshal.FreeCoTaskMem(appName);
            if (titlePointer != IntPtr.Zero) Marshal.FreeCoTaskMem(titlePointer);
            if (bodyPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(bodyPointer);
        }
    }
}
