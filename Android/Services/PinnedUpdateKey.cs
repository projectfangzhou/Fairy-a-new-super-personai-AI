namespace FairyAI_Android.Services;

/// <summary>
/// The update signing key trusted by this build. It is compiled into the client on
/// purpose: updates.json lives in a user-writable directory, so trusting the key it
/// names would let anyone who can edit that file substitute their own key and publish
/// a "correctly signed" malicious update.
/// </summary>
public static class PinnedUpdateKey
{
    public const string Base64 = "0snQR62Xm9aELYB5ohj9nmg8BeqFz3UO899sHZjzGI8=";
}