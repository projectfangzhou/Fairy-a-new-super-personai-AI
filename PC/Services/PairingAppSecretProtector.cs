namespace MyAiAssistant.Services.Pairing;

/// <summary>
/// The production protector: reuses <see cref="ApiKeyProtector"/> so pairing secrets are wrapped
/// exactly like API keys, with the same machine-local keys and the same integrity check.
/// </summary>
public sealed class AppPairingSecretProtector : IPairingSecretProtector
{
    public string Protect(string plaintext) => ApiKeyProtector.Protect(plaintext);

    public string Unprotect(string ciphertext) => ApiKeyProtector.Unprotect(ciphertext);
}
