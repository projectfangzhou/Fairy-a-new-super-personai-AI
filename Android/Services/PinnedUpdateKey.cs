namespace FairyAI_Android.Services;

/// <summary>
/// The update signing key trusted by this build. It is compiled into the client on
/// purpose: updates.json lives in a user-writable directory, so trusting the key it
/// names would let anyone who can edit that file substitute their own key and publish
/// a "correctly signed" malicious update.
///
/// ECDSA P-256, IEEE P1363 public key (0x04 ‖ X ‖ Y, 65 bytes). The matching private key is kept
/// offline by the release owner — never in this repository. Rotating it means re-pinning here and
/// re-signing every manifest, so the old key must be retired deliberately.
/// </summary>
public static class PinnedUpdateKey
{
    public const string Base64 = "BOC16NmPzF7hwqzYd/N3zuLxxZ73WgnXg9jRhXfNJaKJRgtoqbXnDn2Qsfd3UXB43UAXjrdra8fviS8VAV8LMG4=";
}
