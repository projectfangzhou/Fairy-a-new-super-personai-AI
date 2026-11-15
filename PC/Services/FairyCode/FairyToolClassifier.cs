using System.IO;
using System.Text.RegularExpressions;
using MyAiAssistant.Services.Permissions;

namespace MyAiAssistant.Services.FairyCode;

/// <summary>
/// Turns an engine tool call into a permission request. This is a code-level rule set, not
/// a model judgement: it decides only whether an action is risky enough to need the user,
/// and every risky call goes through <see cref="ApprovalGate"/> afterwards.
///
/// Two deliberate conservatism choices:
/// * an unknown tool is treated as "runs unverified code" instead of being allowed;
/// * the read-only command allowlist below is an exact-shape allowlist. Anything with a
///   shell metacharacter, a redirect, or an unfamiliar shape needs approval. A false
///   approval prompt costs the user one click; a false allow costs them the machine.
/// </summary>
public sealed partial class FairyToolClassifier
{
    private readonly string _workspaceRoot;

    public FairyToolClassifier(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public ClassifiedToolCall Classify(EngineToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var tool = call.ToolName.Trim().ToLowerInvariant();
        return tool switch
        {
            "read" or "ls" or "grep" or "find" => ReadOnly(tool, call),
            "write" or "edit" => Mutation(tool, call),
            "bash" or "powershell" => Command(tool, call),
            _ => Risky(tool, call, RiskCategory.InstallOrRunUnverifiedCode,
                $"未登记的工具「{tool}」，按运行来源不明代码处理")
        };
    }

    private ClassifiedToolCall ReadOnly(string tool, EngineToolCall call)
    {
        var path = FirstPath(call);
        if (path is not null && LooksLikeCredential(path))
            return Risky(tool, call, RiskCategory.ReadWriteCredentials, "读取的路径包含凭据特征");

        return new ClassifiedToolCall(
            new ActionRequest($"fairycode.{tool}", RiskCategory.None, Describe(tool, call), path),
            IsReadOnly: true);
    }

    private ClassifiedToolCall Mutation(string tool, EngineToolCall call)
    {
        var path = FirstPath(call);
        if (path is null)
            return Risky(tool, call, RiskCategory.DeleteOrOverwriteData, "写操作未给出目标路径，按覆盖数据处理");

        if (LooksLikeCredential(path))
            return Risky(tool, call, RiskCategory.ReadWriteCredentials, "写入的路径包含凭据特征");

        var full = Resolve(path);
        var target = full ?? path;
        var inside = full is not null && IsInsideWorkspace(full);

        // Creating a new file inside the workspace is additive; overwriting an existing one,
        // or creating anything outside the workspace, is a data change the user must see.
        if (inside && !File.Exists(full) && !Directory.Exists(full))
            return new ClassifiedToolCall(
                new ActionRequest($"fairycode.{tool}", RiskCategory.None, Describe(tool, call), target),
                IsReadOnly: false);

        var reason = inside ? "覆盖已存在的文件" : "写入工作区之外的位置";
        return Risky(tool, call, RiskCategory.DeleteOrOverwriteData, reason, target);
    }

    private ClassifiedToolCall Command(string tool, EngineToolCall call)
    {
        var command = call.Argument("command") ?? call.Argument("cmd") ?? string.Empty;
        var trimmed = command.Trim();

        if (trimmed.Length == 0)
            return Risky(tool, call, RiskCategory.InstallOrRunUnverifiedCode, "空命令，按运行代码处理");

        if (ContainsCredentialPath(trimmed))
            return Risky(tool, call, RiskCategory.ReadWriteCredentials, "命令参数包含凭据路径");

        if (!IsReadOnlyCommand(trimmed))
            return Risky(tool, call, RiskCategory.InstallOrRunUnverifiedCode,
                "命令执行：需要用户确认后才运行");

        return new ClassifiedToolCall(
            new ActionRequest($"fairycode.{tool}", RiskCategory.None, $"只读命令：{trimmed}", trimmed),
            IsReadOnly: true);
    }

    /// <summary>
    /// True only for an exact, argument-safe read-only shape. Shell metacharacters are
    /// rejected outright, so nothing here can be turned into a second command.
    /// </summary>
    public static bool IsReadOnlyCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        // No chaining, no substitution, no redirection, no globs that a shell would expand
        // differently per platform. Newlines are rejected because they chain commands.
        if (command.Any(c => c is '>' or '<' or '|' or '&' or ';' or '$' or '`' or '\n' or '\r'))
            return false;
        if (command.Contains("$(") || command.Contains("&&") || command.Contains("||")) return false;

        var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return false;

        var program = tokens[0].ToLowerInvariant();
        if (program.Contains('\\') || program.Contains('/')) return false; // no absolute paths

        return program switch
        {
            "ls" or "dir" or "pwd" or "type" or "cat" or "head" or "tail" or "wc" or "where"
                => NoDangerousFlags(tokens),
            "echo" => true,
            "grep" or "rg" or "find" => NoDangerousFlags(tokens) && !HasExecFlag(tokens),
            "git" => IsReadOnlyGit(tokens),
            "node" or "dotnet" or "python" or "python3" or "npm" or "pip"
                => IsVersionProbe(tokens),
            _ => false
        };
    }

    private static bool NoDangerousFlags(string[] tokens) =>
        !tokens.Any(t => t is "-o" or "--output" or "--out" or "-delete" or "-fprint");

    private static bool HasExecFlag(string[] tokens) =>
        tokens.Any(t => t.StartsWith("-exec", StringComparison.Ordinal) || t is "-ok" or "-okdir");

    private static bool IsVersionProbe(string[] tokens) =>
        tokens.Length == 2 && tokens[1] is "--version" or "-v" or "version";

    private static bool IsReadOnlyGit(string[] tokens)
    {
        if (tokens.Length < 2) return false;
        // Only inspection subcommands, and only without their write-capable options.
        var sub = tokens[1].ToLowerInvariant();
        if (sub is not ("status" or "diff" or "log" or "show" or "branch" or "rev-parse" or "ls-files" or "remote"))
            return false;
        return !tokens.Any(t =>
            t is "--output" or "-o" or "--ext-diff" or "--no-index" ||
            t.StartsWith("--output=", StringComparison.Ordinal) ||
            t.StartsWith("-c", StringComparison.Ordinal) && t.Contains("pager", StringComparison.OrdinalIgnoreCase));
    }

    private ClassifiedToolCall Risky(
        string tool, EngineToolCall call, RiskCategory category, string reason, string? target = null)
    {
        target ??= FirstPath(call) ?? SummarizeCommand(call);
        return new ClassifiedToolCall(
            new ActionRequest($"fairycode.{tool}", category, $"{Describe(tool, call)}（{reason}）", target),
            IsReadOnly: false);
    }

    private static string Describe(string tool, EngineToolCall call) => tool switch
    {
        "bash" or "powershell" => "执行命令",
        "write" => "写入文件",
        "edit" => "修改文件",
        "read" => "读取文件",
        "ls" => "列目录",
        "grep" => "搜索内容",
        "find" => "查找文件",
        _ => $"调用工具 {tool}"
    };

    private static string? SummarizeCommand(EngineToolCall call)
    {
        var command = call.Argument("command") ?? call.Argument("cmd");
        if (string.IsNullOrWhiteSpace(command)) return null;
        var single = command.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return single.Length <= 160 ? single : single[..160] + "…";
    }

    private string? FirstPath(EngineToolCall call) =>
        call.Argument("path") ?? call.Argument("file_path") ?? call.Argument("filePath") ??
        call.Argument("pattern") ?? call.Argument("dir");

    private string? Resolve(string path)
    {
        try
        {
            return Path.GetFullPath(Path.IsPathFullyQualified(path)
                ? path
                : Path.Combine(_workspaceRoot, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private bool IsInsideWorkspace(string fullPath)
    {
        var root = _workspaceRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), _workspaceRoot,
                   StringComparison.OrdinalIgnoreCase);
    }

    internal static bool LooksLikeCredential(string path)
    {
        var normalized = path.Replace('/', '\\').ToLowerInvariant();
        return CredentialPattern().IsMatch(normalized);
    }

    private static bool ContainsCredentialPath(string command)
    {
        foreach (var token in command.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var cleaned = token.Trim('"', '\'', '=', ',');
            if (cleaned.Length > 0 && LooksLikeCredential(cleaned)) return true;
        }
        return false;
    }

    [GeneratedRegex(@"(\\.ssh\\|\\\.aws\\|\\\.azure\\|id_rsa|id_ed25519|\.pem\b|\.pfx\b|\.p12\b|\.jks\b|keystore|\.npmrc\b|\.netrc\b|(^|[\\/])\.env(\.|$|\\|/)|credential|secret|password)", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialPattern();
}
