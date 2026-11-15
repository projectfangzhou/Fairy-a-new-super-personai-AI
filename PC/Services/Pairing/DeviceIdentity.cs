using System.Security.Cryptography;

namespace MyAiAssistant.Services.Pairing;

/// <summary>
/// This machine's pairing identity: an ECDSA P-256 key for signatures and an ECDH P-256 key for
/// key agreement.
///
/// The device id is derived from the public signing key rather than chosen, so it cannot be
/// renamed into someone else's. A device name that the user types is only a label and is never
/// used to decide whether a request is trusted — the old LAN pairing keyed its trust on the
/// device name, which any device on the network can claim.
/// </summary>
public sealed class DeviceIdentity : IDisposable
{
    private const string DeviceIdPrefix = "DEV";

    private readonly ECDsa _signing;
    private readonly ECDiffieHellman _agreement;

    private DeviceIdentity(ECDsa signing, ECDiffieHellman agreement)
    {
        _signing = signing;
        _agreement = agreement;

        var publicKey = _signing.ExportSubjectPublicKeyInfo();
        try
        {
            DeviceId = DeviceIdPrefix + Base32.Encode(SHA256.HashData(publicKey).AsSpan(0, 10));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    public string DeviceId { get; }

    /// <summary>Device id in dash-separated groups, for showing and comparing by eye.</summary>
    public string ShortId => Base32.Group(DeviceId);

    public string SigningPublicKey => Convert.ToBase64String(_signing.ExportSubjectPublicKeyInfo());

    public string AgreementPublicKey => Convert.ToBase64String(_agreement.ExportSubjectPublicKeyInfo());

    public static DeviceIdentity Create()
    {
        var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var agreement = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return new DeviceIdentity(signing, agreement);
    }

    /// <summary>Rebuilds an identity from PKCS#8 material previously handed out by <see cref="ExportPrivateKeys"/>.</summary>
    public static DeviceIdentity FromPrivateKeys(string signingPrivateKey, string agreementPrivateKey)
    {
        var signing = ECDsa.Create();
        signing.ImportPkcs8PrivateKey(Convert.FromBase64String(signingPrivateKey), out _);

        var agreement = ECDiffieHellman.Create();
        agreement.ImportPkcs8PrivateKey(Convert.FromBase64String(agreementPrivateKey), out _);

        return new DeviceIdentity(signing, agreement);
    }

    /// <summary>
    /// The private keys in PKCS#8 form. Callers must protect them at rest — the store wraps them
    /// with the same protector used for API keys.
    /// </summary>
    public (string Signing, string Agreement) ExportPrivateKeys() => (
        Convert.ToBase64String(_signing.ExportPkcs8PrivateKey()),
        Convert.ToBase64String(_agreement.ExportPkcs8PrivateKey()));

    public byte[] Sign(ReadOnlySpan<byte> data) => _signing.SignData(data.ToArray(), HashAlgorithmName.SHA256);

    public static bool Verify(string signingPublicKey, ReadOnlySpan<byte> data, byte[] signature)
    {
        if (string.IsNullOrWhiteSpace(signingPublicKey) || signature.Length == 0) return false;

        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(signingPublicKey), out _);
            return verifier.VerifyData(data.ToArray(), signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Imports a counterpart's public agreement key so a shared secret can be derived with it.</summary>
    public static ECDiffieHellman ImportAgreementKey(string publicKey)
    {
        var key = ECDiffieHellman.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        return key;
    }

    /// <summary>A thumbprint of key material, shown on both screens so users can compare them.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> keyMaterial) =>
        Base32.Group(Base32.Encode(SHA256.HashData(keyMaterial).AsSpan(0, 10)));

    public void Dispose()
    {
        _signing.Dispose();
        _agreement.Dispose();
    }
}
