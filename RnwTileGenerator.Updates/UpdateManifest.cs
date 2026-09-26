using System.Security.Cryptography;
using System.Text.Json;

namespace RnwTileGenerator.Updates;

/// <summary>Warum ein Update nicht vorbereitet werden konnte (<see cref="None"/> = alles in Ordnung).</summary>
public enum UpdateProblem
{
    None,
    NoSignedPackage,
    BadSignature,
    InvalidManifest,
    VersionMismatch,
    NotNewer,
    ProtocolTooNew,
    AssetMissing,
    SizeMismatch,
    HashMismatch,
    ExtractFailed,
    Network,
}

/// <summary>
/// Signierte Paketbeschreibung eines Releases (<c>update-manifest.json</c>). Enthält die Version, damit ein altes, gültig signiertes
/// Paket nicht unter einem neuen Tag angeboten werden kann.
/// </summary>
public sealed record UpdateManifest(AppVersion Version, string Asset, string Sha256, long Size, int MinApplyProtocol)
{
    /// <summary>Höchstes Protokoll des Update-Modus, das diese Version beherrscht (siehe <see cref="ApplyUpdateArguments"/>).</summary>
    public const int SupportedApplyProtocol = 1;

    /// <summary><see langword="null"/> bei ungültigem JSON, fehlenden Feldern oder unsicheren Werten.</summary>
    public static UpdateManifest? Parse(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryString(root, "version", out var versionText) || !AppVersion.TryParse(versionText, out var version)
                || !TryString(root, "asset", out var asset) || !IsSafeZipName(asset)
                || !TryString(root, "sha256", out var sha) || !IsSha256(sha)
                || !root.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var bytes) || bytes <= 0
                || !root.TryGetProperty("minApplyProtocol", out var protocol) || protocol.ValueKind != JsonValueKind.Number
                || !protocol.TryGetInt32(out var minProtocol) || minProtocol < 1)
            {
                return null;
            }

            return new UpdateManifest(version, asset, sha.ToLowerInvariant(), bytes, minProtocol);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static UpdateManifest Create(string zipPath, AppVersion version)
    {
        using var stream = File.OpenRead(zipPath);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new UpdateManifest(version, Path.GetFileName(zipPath), hash, stream.Length, SupportedApplyProtocol);
    }

    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(
        new { version = Version.ToString(), asset = Asset, sha256 = Sha256, size = Size, minApplyProtocol = MinApplyProtocol },
        new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Prüfregeln 2-4 der Spec plus Größe laut Release. Die Signatur prüft <see cref="ManifestSignature"/>, den Hash der Download.</summary>
    public UpdateProblem Check(ReleaseInfo release, AppVersion installed)
    {
        if (Version != release.Version)
        {
            return UpdateProblem.VersionMismatch;
        }

        if (Version <= installed)
        {
            return UpdateProblem.NotNewer;
        }

        if (MinApplyProtocol > SupportedApplyProtocol)
        {
            return UpdateProblem.ProtocolTooNew;
        }

        if (release.FindAsset(Asset) is not { } zip)
        {
            return UpdateProblem.AssetMissing;
        }

        return zip.Size == Size ? UpdateProblem.None : UpdateProblem.SizeMismatch;
    }

    private static bool TryString(JsonElement root, string name, out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString()!;
        return true;
    }

    /// <summary>Nur ein reiner Dateiname auf ".zip": kein Pfad, keine unzulässigen Zeichen.</summary>
    private static bool IsSafeZipName(string name) =>
        name.Length > 4
        && name == Path.GetFileName(name)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string text) => text.Length == 64 && text.All(Uri.IsHexDigit);
}
