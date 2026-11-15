#if ANDROID
using System.Globalization;
using System.Text;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Java.Util.Concurrent;
using Moe.Shizuku.Server;
using Rikka.Shizuku;
using Application = Android.App.Application;

namespace FairyAI_Android.Services.Screen;

/// <summary>
/// The real Android bridge, over the official Shizuku client library (dev.rikka.shizuku, Apache-2.0,
/// bound unmodified from the published AARs).
///
/// What it can read is reported as fact: whether the package is installed, whether the Shizuku service
/// answered, and whether this app has been granted access. Injection goes through
/// <c>IShizukuService.newProcess</c> with an argv array — deliberately no shell, so nothing in the
/// requested action can be reinterpreted as shell syntax.
///
/// Every call is still gated by <see cref="ScreenGate"/>: Shizuku's grant says "this app may use
/// Shizuku", not "do this now".
/// </summary>
public sealed class AndroidShizukuBridge : IShizukuBridge
{
    public const string PackageName = "moe.shizuku.privileged.api";

    private const int PermissionRequestCode = 4211;
    private const int ShellTimeoutSeconds = 20;

    /// <summary>A screenshot is a few megabytes; 16 is far above any phone screen and still bounded.</summary>
    private const int MaxCaptureBytes = 16 * 1024 * 1024;

    private const int CaptureReadSeconds = 15;

    private readonly Context _context;

    public AndroidShizukuBridge(Context? context = null)
    {
        _context = context ?? Application.Context;
        EnsurePermissionListener();
    }

    /// <summary>Raised when Shizuku answers a permission request, so the UI can refresh its status.</summary>
    public event EventHandler? StatusChanged;

    private static bool _permissionListenerRegistered;

    private void EnsurePermissionListener()
    {
        if (_permissionListenerRegistered) return;
        _permissionListenerRegistered = true;

        try
        {
            // Shizuku's own dialog answers asynchronously; without this the screen would keep showing
            // "not authorized" after the user has already allowed it.
            Shizuku.AddRequestPermissionResultListener(new PermissionListener(() =>
                StatusChanged?.Invoke(this, EventArgs.Empty)));
        }
        catch
        {
            // If the listener cannot be registered the status is simply refreshed on the next visit.
        }
    }

    public ShizukuProbeResult Probe()
    {
        var installed = IsInstalled();

        var running = false;
        try
        {
            running = Shizuku.PingBinder();
        }
        catch
        {
            // A dead binder or a missing provider is "not running" as far as this app is concerned.
        }

        if (!running)
        {
            return installed
                ? new ShizukuProbeResult(
                    ShizukuAvailability.NotRunning,
                    null,
                    "检测到已安装 Shizuku，但服务没有在运行：打开一次 Shizuku，或用电脑上的 ADB 启动它的服务。")
                : ShizukuProbeResult.NotInstalled();
        }

        var version = 0;
        try
        {
            version = Shizuku.Version;
        }
        catch
        {
            // Version is informational only.
        }

        var granted = false;
        try
        {
            granted = Shizuku.CheckSelfPermission() == (int)Permission.Granted;
        }
        catch
        {
            granted = false;
        }

        var versionText = version > 0 ? version.ToString(CultureInfo.InvariantCulture) : null;

        return granted
            ? new ShizukuProbeResult(
                ShizukuAvailability.Authorized,
                versionText,
                "Shizuku 已授权" + (versionText is null ? "" : $"（API {versionText}）") + "，可以执行屏幕操作。")
            : new ShizukuProbeResult(
                ShizukuAvailability.NotAuthorized,
                versionText,
                "Shizuku 在运行，但还没有授权给仙灵：点「打开 Shizuku」向它申请，允许后回到这里。");
    }

    public bool OpenShizukuApp(out string message)
    {
        if (!IsInstalled())
        {
            message = "这台设备上没有 Shizuku。需要的话先安装它；不安装不影响其他功能。";
            return false;
        }

        // Asking Shizuku directly is the flow the user is meant to see: Shizuku shows its own dialog
        // and the answer is recorded on its side. Falling back to opening the app never lies about
        // which of the two happened.
        try
        {
            if (Shizuku.PingBinder() && Shizuku.CheckSelfPermission() != (int)Permission.Granted)
            {
                Shizuku.RequestPermission(PermissionRequestCode);
                message = "已向 Shizuku 发出授权请求，请在它的弹窗里选「允许」。";
                return true;
            }
        }
        catch
        {
            // Fall through to opening the app.
        }

        try
        {
            using var intent = _context.PackageManager?.GetLaunchIntentForPackage(PackageName);
            if (intent is null)
            {
                message = "打不开 Shizuku 的界面，请手动打开它。";
                return false;
            }

            intent.AddFlags(ActivityFlags.NewTask);
            _context.StartActivity(intent);
            message = "已打开 Shizuku，请在里面启动服务并授权仙灵。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"打开 Shizuku 失败：{ex.GetType().Name}";
            return false;
        }
    }

    public Task<ScreenExecutionResult> InjectAsync(
        ScreenAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        var arguments = BuildArguments(action, out var refusal);
        if (arguments is null)
            return Task.FromResult(ScreenExecutionResult.Failed(refusal));

        // WaitForTimeout blocks, so this never runs on the caller's thread.
        return Task.Run(() => Execute(arguments, action, cancellationToken), cancellationToken);
    }

    private static string[]? BuildArguments(ScreenAction action, out string refusal)
    {
        refusal = "";

        switch (action.Capability)
        {
            case ScreenCapability.Tap:
                return new[] { "input", "tap", Int(action.X), Int(action.Y) };

            case ScreenCapability.Swipe:
                return new[] { "input", "swipe", Int(action.X), Int(action.Y), Int(action.X2), Int(action.Y2), "300" };

            case ScreenCapability.TypeText:
            {
                var text = action.Text;
                if (string.IsNullOrEmpty(text))
                {
                    refusal = "没有要输入的文字，未执行。";
                    return null;
                }

                // `input text` splits on spaces; %s is its own escape for one.
                return new[] { "input", "text", text.Replace(" ", "%s") };
            }

            case ScreenCapability.PressKey:
            {
                var key = action.Key?.Trim();
                if (string.IsNullOrEmpty(key) || !IsAllowedKey(key))
                {
                    refusal = "按键名只接受数字或 KEYCODE_ 开头的名称，未执行。";
                    return null;
                }

                return new[] { "input", "keyevent", key };
            }

            case ScreenCapability.ReadScreen:
                if (action.AsImage)
                {
                    // argv form, no shell: `screencap -p` writes a PNG to stdout when it is given no
                    // file name, and running as the shell user is what makes it allowed at all.
                    return new[] { "screencap", "-p" };
                }

                // One fixed pipeline, no user data in it. `dumpsys window` alone prints hundreds of
                // kilobytes: the remote pipe fills up, the child blocks on write and never exits, so
                // there would be nothing to wait for.
                return new[] { "sh", "-c", "dumpsys window | grep -m1 mCurrentFocus" };

            default:
                refusal = $"「{action.Capability}」没有对应的执行方式，未执行。";
                return null;
        }
    }

    private static ScreenExecutionResult Execute(
        string[] arguments,
        ScreenAction action,
        CancellationToken cancellationToken)
    {
        var capability = action.Capability;
        if (cancellationToken.IsCancellationRequested)
            return ScreenExecutionResult.Failed("操作被取消，未执行。");

        bool running;
        try
        {
            running = Shizuku.PingBinder();
        }
        catch
        {
            running = false;
        }

        if (!running) return ScreenExecutionResult.Failed("Shizuku 服务没有连接，未执行任何操作。");

        try
        {
            if (Shizuku.CheckSelfPermission() != (int)Permission.Granted)
                return ScreenExecutionResult.Failed("还没有被 Shizuku 授权，未执行任何操作。");
        }
        catch
        {
            return ScreenExecutionResult.Failed("读不到 Shizuku 的授权状态，按未授权处理。");
        }

        IRemoteProcess? process = null;
        try
        {
            // Shizuku hands this app the service binder through its provider. Reaching it through
            // SystemServiceHelper needs the hidden ServiceManager, which Android 14 refuses; the
            // binder Shizuku already gave us is the same object and needs no tricks.
            var binder = Shizuku.Binder;
            if (binder is null) return ScreenExecutionResult.Failed("拿不到 Shizuku 服务句柄，未执行。");

            var service = IShizukuService.Stub.AsInterface(binder);
            if (service is null) return ScreenExecutionResult.Failed("Shizuku 服务接口不可用，未执行。");

            process = service.NewProcess(arguments, null, null);
            if (process is null) return ScreenExecutionResult.Failed("Shizuku 没有创建进程，未执行。");

            var remote = process;
            var wantsImage = action.AsImage && capability == ScreenCapability.ReadScreen;

            // A screenshot is megabytes and the pipe holds 64 KB, so it has to be drained while the
            // child runs; reading it after the process exits deadlocks.
            var reader = wantsImage ? Task.Run(() => ReadImage(remote)) : null;

            // WaitFor blocks; racing it against a deadline keeps a stuck command from pinning the
            // worker thread. Destroying the process is what makes the blocked call return.
            var waiter = Task.Run(() => remote.WaitFor());
            if (!waiter.Wait(TimeSpan.FromSeconds(ShellTimeoutSeconds)))
            {
                try
                {
                    remote.Destroy();
                }
                catch
                {
                    // Already gone.
                }

                return ScreenExecutionResult.Failed($"命令 {ShellTimeoutSeconds} 秒没有结束，已终止。");
            }

            var exitCode = waiter.Result;

            if (reader is not null)
            {
                if (!reader.Wait(TimeSpan.FromSeconds(CaptureReadSeconds)))
                    return ScreenExecutionResult.Failed("截图数据没有读完，已丢弃这一张。");

                var (image, readError) = reader.Result;
                if (readError is not null) return ScreenExecutionResult.Failed(readError);

                if (exitCode != 0)
                    return ScreenExecutionResult.Failed($"screencap 以退出码 {exitCode} 结束，没有截到画面。");

                if (image is null || image.Length < 8)
                    return ScreenExecutionResult.Failed("没有截到画面：screencap 没有输出。");

                if (!IsPng(image))
                    return ScreenExecutionResult.Failed("截到的数据不是 PNG，已丢弃。");

                return ScreenExecutionResult.Ok(
                    $"已截取当前屏幕（{image.Length / 1024.0:0} KB，{arguments[0]}）", image);
            }

            if (exitCode != 0)
                return ScreenExecutionResult.Failed($"{arguments[0]} 以退出码 {exitCode} 结束，操作可能没有生效。");

            if (capability == ScreenCapability.ReadScreen)
            {
                var focus = ReadFocusedWindow(process);
                return ScreenExecutionResult.Ok(
                    string.IsNullOrWhiteSpace(focus) ? "已查询窗口状态，但没有拿到当前焦点窗口。" : focus);
            }

            return ScreenExecutionResult.Ok("已执行 " + string.Join(' ', arguments));
        }
        catch (Exception ex)
        {
            return ScreenExecutionResult.Failed($"通过 Shizuku 执行失败：{ex.GetType().Name}");
        }
        finally
        {
            try
            {
                process?.Destroy();
                process?.Dispose();
            }
            catch
            {
                // Nothing useful to do if cleanup fails.
            }
        }
    }

    private static string ReadFocusedWindow(IRemoteProcess process)
    {
        var descriptor = process.InputStream;
        if (descriptor is null) return "";

        Java.IO.FileInputStream? input = null;
        try
        {
            var fileDescriptor = descriptor.FileDescriptor;
            if (fileDescriptor is null) return "";

            // The remote pipe is a Java stream, not a managed one. dumpsys output is large, so this
            // scans line by line and stops at the first match instead of buffering everything.
            input = new Java.IO.FileInputStream(fileDescriptor);

            var buffer = new byte[8192];
            var line = new StringBuilder();
            int read;

            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var character = (char)buffer[i];
                    if (character == '\n')
                    {
                        if (line.ToString().Contains("mCurrentFocus", StringComparison.Ordinal))
                            return line.ToString().Trim();

                        line.Clear();
                    }
                    else if (character != '\r')
                    {
                        line.Append(character);
                    }
                }
            }

            return line.ToString().Contains("mCurrentFocus", StringComparison.Ordinal)
                ? line.ToString().Trim()
                : "";
        }
        catch
        {
            return "";
        }
        finally
        {
            try
            {
                input?.Dispose();
                descriptor.Dispose();
            }
            catch
            {
                // Nothing useful to do if cleanup fails.
            }
        }
    }

    /// <summary>Drains `screencap -p` stdout into memory, with a hard ceiling so nothing can run away.</summary>
    private static (byte[]? Bytes, string? Error) ReadImage(IRemoteProcess process)
    {
        var descriptor = process.InputStream;
        if (descriptor is null) return (null, "拿不到 screencap 的输出管道");

        Java.IO.FileInputStream? input = null;
        try
        {
            var fileDescriptor = descriptor.FileDescriptor;
            if (fileDescriptor is null) return (null, "拿不到 screencap 的输出句柄");

            input = new Java.IO.FileInputStream(fileDescriptor);

            using var captured = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;

            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                captured.Write(buffer, 0, read);

                if (captured.Length > MaxCaptureBytes)
                    return (null, $"截图超过 {MaxCaptureBytes / (1024 * 1024)} MB，已放弃");
            }

            return (captured.ToArray(), null);
        }
        catch (Exception ex)
        {
            // The process being destroyed mid-read is expected when the timeout wins the race.
            return (null, $"读取截图时出错：{ex.GetType().Name}");
        }
        finally
        {
            try
            {
                input?.Dispose();
                descriptor.Dispose();
            }
            catch
            {
                // Nothing useful to do if cleanup fails.
            }
        }
    }

    private static bool IsPng(byte[] data) =>
        data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;

    private static bool IsAllowedKey(string key) =>
        key.All(char.IsDigit) ||
        (key.StartsWith("KEYCODE_", StringComparison.Ordinal) &&
         key.All(c => char.IsAsciiLetterUpper(c) || char.IsDigit(c) || c == '_'));

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private bool IsInstalled()
    {
        try
        {
            // Shizuku is an ordinary app with a launcher screen, and the launcher intent needs no
            // API-33-only overloads.
            using var intent = _context.PackageManager?.GetLaunchIntentForPackage(PackageName);
            return intent is not null;
        }
        catch
        {
            return false;
        }
    }

    private sealed class PermissionListener : Java.Lang.Object, Shizuku.IOnRequestPermissionResultListener
    {
        private readonly Action _onResult;

        public PermissionListener(Action onResult) => _onResult = onResult;

        public void OnRequestPermissionResult(int requestCode, int grantResult) => _onResult();
    }
}
#endif
