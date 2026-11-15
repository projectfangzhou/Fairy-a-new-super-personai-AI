namespace MyAiAssistant.Services.Permissions;

/// <summary>
/// The code-level rule set. It is deliberately not model-driven and not user-editable
/// at runtime: a reviewer model may only ever widen judgement inside the low-risk space,
/// so anything in a <see cref="RiskCategory"/> always reaches the user.
/// </summary>
public sealed class PermissionPolicy
{
    // Writes here are refused outright rather than offered for approval. The project must
    // never patch firmware, boot images or the secure-boot configuration, so there is no
    // legitimate "user said yes" path for them.
    private static readonly (string Fragment, string Reason)[] HardDeniedTargets =
    [
        ("\\efi\\", "禁止写入 EFI 启动分区"),
        ("\\boot\\", "禁止写入启动目录"),
        ("\\system32\\config", "禁止写入系统配置库"),
        ("\\system32\\drivers", "禁止写入驱动目录"),
        ("secureboot", "禁止修改 Secure Boot 配置"),
        ("\\\\.\\physicaldrive", "禁止直接写入物理磁盘"),
        ("\\\\.\\device\\harddisk", "禁止直接写入物理磁盘")
    ];

    public PolicyDecision Evaluate(ActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(request.Target))
        {
            var target = request.Target!.Replace('/', '\\').ToLowerInvariant();
            foreach (var (fragment, reason) in HardDeniedTargets)
                if (target.Contains(fragment, StringComparison.Ordinal))
                    return new(PermissionVerdict.Deny, reason);
        }

        if (request.IsRisky)
            return new(PermissionVerdict.RequireApproval, request.Category.Describe());

        return new(PermissionVerdict.Allow, "规则允许的低风险操作");
    }
}