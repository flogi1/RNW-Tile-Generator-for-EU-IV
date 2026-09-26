using System.Security.Cryptography;

namespace RnwTileGenerator.Updates;

/// <summary>
/// Signatur des Update-Manifests: ECDsa P-256 mit SHA-256, Signatur im IEEE-P1363-Format als Base64. Dieselbe Klasse nutzen das
/// Release-Werkzeug (signieren) und RNW (prüfen), damit beide Seiten nicht auseinanderlaufen können.
/// </summary>
public static class ManifestSignature
{
    private const DSASignatureFormat Format = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;

    /// <summary>Öffentlicher Schlüssel als Base64 (SubjectPublicKeyInfo), privater als passwortgeschütztes PKCS#8.</summary>
    public static (string PublicKey, byte[] EncryptedPrivateKey) CreateKeyPair(string password)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var protection = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);
        return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), key.ExportEncryptedPkcs8PrivateKey(password, protection));
    }

    /// <summary>Wirft <see cref="CryptographicException"/> bei falschem Passwort oder kaputter Schlüsseldatei.</summary>
    public static string Sign(byte[] data, byte[] encryptedPrivateKey, string password)
    {
        using var key = ECDsa.Create();
        key.ImportEncryptedPkcs8PrivateKey(password, encryptedPrivateKey, out _);
        return Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, Format));
    }

    /// <summary>Wirft nie: leerer oder kaputter Schlüssel, kaputte Signatur und falsche Daten ergeben <see langword="false"/>.</summary>
    public static bool Verify(byte[] data, string signature, string publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey))
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(data, Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256, Format);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
