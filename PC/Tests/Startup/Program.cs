using System.Reflection;
using MyAiAssistant;
using MyAiAssistant.Services.Messaging;

namespace Startup.Tests;

/// <summary>
/// Checks the startup wiring of the WebChat server against the compiled app, without starting it.
///
/// The pieces were tested before this: the launch rule (WebChatLaunch) and the server itself. What
/// was never tested is whether the app actually calls them — a wiring bug there looks exactly like
/// "the feature is off", and only a human noticing the port would catch it. These checks read the
/// IL of the shipped assembly instead.
/// </summary>
internal static class Program
{
    private const BindingFlags AnyInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static int _checks;

    private static int Main()
    {
        try
        {
            LaunchStepCallsTheRule();
            StartupCallsTheLaunchStep();
            ShutdownDisposesTheServer();

            Console.WriteLine($"All {_checks} startup checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAILED: " + ex.Message);
            return 1;
        }
    }

    private static void LaunchStepCallsTheRule()
    {
        var step = typeof(App).GetMethod("StartWebChatIfEnabled", AnyInstance);
        Assert(step is not null, "App still has a WebChat launch step");

        var called = IlScanner.CalledMethods(step!).Select(Describe).ToArray();
        Assert(called.Contains("WebChatLaunch.StartIfEnabled"),
            "the launch step calls the tested rule, not a hand-rolled copy: " + string.Join(", ", called));
    }

    private static void StartupCallsTheLaunchStep()
    {
        var startup = typeof(App).GetMethod("OnStartup", AnyInstance);
        Assert(startup is not null, "App still overrides OnStartup");

        // OnStartup is async, so the calls live in the state machine's MoveNext.
        var body = IlScanner.AsyncBody(startup!) ?? startup!;
        var called = IlScanner.CalledMethods(body).Select(Describe).ToArray();

        Assert(called.Contains("WebChatLaunchStep.StartWebChatIfEnabled"),
            "startup actually runs the launch step: " + string.Join(", ", called));
    }

    private static void ShutdownDisposesTheServer()
    {
        var exit = typeof(App).GetMethod("OnExit", AnyInstance);
        Assert(exit is not null, "App still overrides OnExit");

        var called = IlScanner.CalledMethods(exit!).Select(Describe).ToArray();
        Assert(called.Contains("WebChatEndpoint.Dispose"),
            "shutdown releases the listening socket: " + string.Join(", ", called));

        // Control: the scanner has to be able to report absence too, otherwise the checks above
        // would pass even if it reported everything.
        Assert(!called.Contains("WebChatLaunch.StartIfEnabled"),
            "the scanner distinguishes called from not called");
    }

    private static string Describe(MethodBase method)
    {
        var type = method.DeclaringType;
        var name = type is null ? method.Name : type.Name + "." + method.Name;

        // The launch step is a private App method; call it by its own name so the assertions read
        // the same way as the source.
        return name == "App.StartWebChatIfEnabled" ? "WebChatLaunchStep.StartWebChatIfEnabled" : name;
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
    }
}
