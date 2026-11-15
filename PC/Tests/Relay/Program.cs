using MyAiAssistant.Services.Relay;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

const long GB = 1024L * 1024 * 1024;
const long MB = 1024L * 1024;

// ---- 1. The defaults are the free-tier figures, and they say so ----
{
    var policy = new RelayQuotaPolicy();
    Assert(policy.Quota.SingleFileLimitBytes == 2 * GB, "the default single-file limit is 2 GB");
    Assert(policy.Quota.ShareTotalLimitBytes == 5 * GB, "the default per-share limit is 5 GB");
    Assert(policy.Quota.Source == QuotaSource.FreeTierDefault, "the defaults are marked as free-tier defaults");
    Assert(policy.Quota.Describe().Contains("免费", StringComparison.Ordinal),
        "the limit describes itself as based on the free tier, not as a promise");
    Assert(policy.Quota.SingleFileLimitBytes < policy.Quota.ShareTotalLimitBytes,
        "one file can never use the whole share allowance");
}

// ---- 2. Blocks happen before the upload, with the reason ----
{
    var policy = new RelayQuotaPolicy();

    var tooBig = policy.Evaluate(new[] { 3 * GB });
    Assert(!tooBig.Allowed, "a file above the single-file limit is refused");
    Assert(tooBig.Reason.Contains("单文件上限", StringComparison.Ordinal), "the refusal names the single-file limit");
    Assert(tooBig.Reason.Contains("最大的一个", StringComparison.Ordinal), "the refusal names the largest file");

    var tooMuch = policy.Evaluate(new[] { 2 * GB, 2 * GB, 2 * GB });
    Assert(!tooMuch.Allowed, "a share above the total limit is refused");
    Assert(tooMuch.Reason.Contains("单次分享上限", StringComparison.Ordinal), "the refusal names the share limit");

    Assert(policy.Evaluate(Array.Empty<long>()).Allowed == false, "an empty share is refused");
    Assert(!policy.Evaluate(new[] { -1L }).Allowed, "a negative size is refused instead of summed");
}

// ---- 3. The boundary is allowed, exactly ----
{
    var policy = new RelayQuotaPolicy();
    var exactly = policy.Evaluate(new[] { 2 * GB });
    Assert(exactly.Allowed, "a file exactly at the single-file limit is allowed");

    var exactlyTotal = policy.Evaluate(new[] { 2 * GB, 2 * GB, GB });
    Assert(exactlyTotal.Allowed, "a share exactly at the total limit is allowed");

    var overByOne = policy.Evaluate(new[] { 5 * GB + 1 });
    Assert(!overByOne.Allowed, "one byte over the share limit is refused");
}

// ---- 4. A full account is refused before uploading more ----
{
    var policy = new RelayQuotaPolicy();
    var noRoom = policy.Evaluate(new[] { 2 * GB }, usedBytes: 9 * GB, storageLimitBytes: 10 * GB);
    Assert(!noRoom.Allowed, "uploading into a full allowance is refused");
    Assert(noRoom.Reason.Contains("剩余的", StringComparison.Ordinal), "the refusal says how much room is left");

    var exactlyFull = policy.Evaluate(new[] { 1 * GB }, usedBytes: 9 * GB, storageLimitBytes: 10 * GB);
    Assert(exactlyFull.Allowed, "exactly filling the allowance is allowed, one byte more is not");

    var someRoom = policy.Evaluate(new[] { 512 * MB }, usedBytes: 8 * GB, storageLimitBytes: 10 * GB);
    Assert(someRoom.Allowed, "an upload that still fits is allowed");

    var noBudgetConfigured = policy.Evaluate(new[] { 1 * GB }, usedBytes: 9 * GB, storageLimitBytes: 0);
    Assert(noBudgetConfigured.Allowed, "without a known storage budget, only the share limits apply");
}

// ---- 5. A user who checked their account can raise the limits, and the source changes ----
{
    var custom = new RelayQuota(10 * GB, 30 * GB, QuotaSource.UserConfigured);
    var policy = new RelayQuotaPolicy(custom);
    Assert(policy.Quota.Describe().Contains("你填写", StringComparison.Ordinal),
        "user-configured limits are labelled as the user's, not as the free tier");
    Assert(policy.Evaluate(new[] { 9 * GB }).Allowed, "a 9 GB file passes a 10 GB single-file limit");

    var big = policy.Evaluate(new[] { 20 * GB });
    Assert(!big.Allowed && big.Reason.Contains("10 GB", StringComparison.Ordinal),
        "the raised single-file limit is the one enforced");
}

// ---- 6. Human-readable sizes are used in every message ----
{
    Assert(RelayQuotaPolicy.Human(512) == "512 B", "bytes are shown as bytes");
    Assert(RelayQuotaPolicy.Human(2 * MB).EndsWith("MB", StringComparison.Ordinal), "megabytes are shown as MB");
    Assert(RelayQuotaPolicy.Human(2 * GB).Contains("GB", StringComparison.Ordinal), "gigabytes are shown as GB");
    Assert(RelayQuotaPolicy.Human(5 * GB).StartsWith("5", StringComparison.Ordinal), "the number is not rounded away");
}

Console.WriteLine($"All {checks} relay quota checks passed.");
return 0;
