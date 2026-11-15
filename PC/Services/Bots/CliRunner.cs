using System.Diagnostics;

namespace MyAiAssistant.Services.Bots;

public sealed record CliResult(bool Ok, int ExitCode, string Output)
{
    public bool OutputContains(string value) =>
        Output.Contains(value, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Runs a CLI for read-only probing. Injected so detection logic can be tested.</summary>
public interface ICliRunner
{
    Task<CliResult> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class ProcessCliRunner : ICliRunner
{
    public async Task<CliResult> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null) return new CliResult(false, -1, "无法启动进程");

            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

            var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderr = process.StandardError.ReadToEndAsync(linked.Token);

            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return new CliResult(false, -1, "命令超时");
            }

            var output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
            return new CliResult(process.ExitCode == 0, process.ExitCode, output);
        }
        catch (Exception ex)
        {
            return new CliResult(false, -1, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}