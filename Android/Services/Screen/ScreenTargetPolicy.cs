namespace FairyAI_Android.Services.Screen;

/// <summary>
/// Target-level checks that apply no matter who authorized what. Shizuku being granted does not make
/// a bank app operable: permission to inject input is not permission to move money.
///
/// Pure functions on purpose — no Android types — so the policy can be asserted directly.
/// </summary>
public static class ScreenTargetPolicy
{
    /// <summary>
    /// Package names that must never receive injected input. WeChat (com.tencent.mm) is intentionally
    /// absent: the assistant is meant to help with ordinary messaging.
    /// </summary>
    private static readonly HashSet<string> BlockedPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        // 支付宝
        "com.eg.android.AlipayGphone",
        "com.eg.android.AlipayGphoneRC",

        // 银行
        "com.icbc",
        "com.icbc.credit",
        "com.chinamworld.main",
        "com.chinamworld.bocmbc",
        "cmb.pb",
        "cmb.pb.international",
        "com.android.bankabc",
        "com.bankcomm.BankComm",
        "com.spdbccc.app",
        "com.cebbank.mobile.cemb",
        "cmbc.cc.app",
        "com.cib.xyk",
        "com.pingan.paces.ccms",
        "com.ccb.start",
        "cn.com.chinapay.boc",
        "com.unionpay",
        "com.unionpay.mobilepay",

        // 证券 / 理财
        "com.hexin.android",
        "com.eastmoney.android",
        "com.xueqiu.android",
    };

    /// <summary>Words that turn a harmless-looking string into a payment instruction.</summary>
    private static readonly HashSet<string> SensitiveKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "转账", "支付", "付款", "收款", "充值", "提现", "密码",
        "交易", "理财", "贷款", "信用卡", "银行", "余额",
        "transfer", "payment", "withdraw", "deposit", "password",
    };

    public static bool IsBlockedPackage(string? packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName)) return false;
        return BlockedPackages.Contains(packageName.Trim());
    }

    public static bool ContainsSensitiveAction(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return SensitiveKeywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyCollection<string> BlockedPackageList => BlockedPackages;

    public static IReadOnlyCollection<string> SensitiveKeywordList => SensitiveKeywords;
}
