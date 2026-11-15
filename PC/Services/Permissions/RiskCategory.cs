namespace MyAiAssistant.Services.Permissions;

/// <summary>
/// The hard risk categories the assistant must never slip past silently.
/// Anything other than <see cref="None"/> requires explicit user confirmation;
/// no model, skill or setting can downgrade that.
/// </summary>
public enum RiskCategory
{
    None = 0,
    DeleteOrOverwriteData,
    ReadWriteCredentials,
    ExternalTransfer,
    PrivilegeEscalation,
    ModifySystemSecurityOrBoot,
    InstallOrRunUnverifiedCode,
    PaymentOrAccount,
    PublishOrCommitCode,

    /// <summary>
    /// Acting on a device or service outside this machine — a Home Assistant entity, a scene,
    /// an appliance. Appended rather than inserted so stored audit records keep their meaning.
    /// </summary>
    ControlExternalDevice,

    /// <summary>
    /// Moving the mouse and typing on this desktop, i.e. acting as the user physically would.
    /// Appended for the same reason as the category above.
    /// </summary>
    OperateScreenAndInput
}

public static class RiskCategoryText
{
    public static string Describe(this RiskCategory category) => category switch
    {
        RiskCategory.None => "无风险类别",
        RiskCategory.DeleteOrOverwriteData => "删除或覆盖数据",
        RiskCategory.ReadWriteCredentials => "读取或写入凭据",
        RiskCategory.ExternalTransfer => "对外传输数据",
        RiskCategory.PrivilegeEscalation => "提权",
        RiskCategory.ModifySystemSecurityOrBoot => "修改系统安全或启动配置",
        RiskCategory.InstallOrRunUnverifiedCode => "安装依赖或运行来源不明代码",
        RiskCategory.PaymentOrAccount => "支付或账号操作",
        RiskCategory.PublishOrCommitCode => "发布或提交代码",
        RiskCategory.ControlExternalDevice => "控制外部设备或服务",
        RiskCategory.OperateScreenAndInput => "操作桌面（鼠标键盘）",
        _ => category.ToString()
    };
}