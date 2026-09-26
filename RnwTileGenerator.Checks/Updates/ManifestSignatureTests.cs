using System.Security.Cryptography;
using System.Text;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

public class ManifestSignatureTests
{
    private static readonly byte[] Data = Encoding.UTF8.GetBytes("""{"version":"1.2.0"}""");

    [Fact]
    public void Signierte_Daten_werden_mit_dem_passenden_Schluessel_bestaetigt()
    {
        var (publicKey, privateKey) = ManifestSignature.CreateKeyPair("geheim");
        var signature = ManifestSignature.Sign(Data, privateKey, "geheim");

        Assert.True(ManifestSignature.Verify(Data, signature, publicKey));
        Assert.True(ManifestSignature.Verify(Data, signature + "\r\n", publicKey));
    }

    [Fact]
    public void Ein_veraendertes_Byte_bricht_die_Pruefung()
    {
        var (publicKey, privateKey) = ManifestSignature.CreateKeyPair("geheim");
        var signature = ManifestSignature.Sign(Data, privateKey, "geheim");
        var changed = (byte[])Data.Clone();
        changed[^2] ^= 1;

        Assert.False(ManifestSignature.Verify(changed, signature, publicKey));
    }

    [Fact]
    public void Fremder_Schluessel_und_kaputte_Eingaben_ergeben_false()
    {
        var (_, privateKey) = ManifestSignature.CreateKeyPair("geheim");
        var (otherPublicKey, _) = ManifestSignature.CreateKeyPair("anders");
        var signature = ManifestSignature.Sign(Data, privateKey, "geheim");

        Assert.False(ManifestSignature.Verify(Data, signature, otherPublicKey));
        Assert.False(ManifestSignature.Verify(Data, "kein base64!", otherPublicKey));
        Assert.False(ManifestSignature.Verify(Data, signature, ""));
        Assert.False(ManifestSignature.Verify(Data, signature, "AAAA"));
    }

    [Fact]
    public void Falsches_Passwort_wirft()
    {
        var (_, privateKey) = ManifestSignature.CreateKeyPair("geheim");
        Assert.ThrowsAny<CryptographicException>(() => ManifestSignature.Sign(Data, privateKey, "falsch"));
    }
}
