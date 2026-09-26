namespace RnwTileGenerator.Updates;

/// <summary>
/// Öffentlicher Schlüssel, mit dem RNW Update-Pakete prüft. Wird einmal mit <c>RnwTileGenerator.ReleaseSigner keygen</c> erzeugt und hier
/// eingetragen. Solange leer, bietet RNW kein Selbst-Update an, nur die Release-Seite.
/// </summary>
public static class UpdateKeys
{
    public const string ProductionPublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE/k2KR4N2bDnLZr/VidCDikDS/M5aoIoVDo+69si2zu0KPUQqbiKqoJZfAad1s60pMmBXzsO3YZ06wosQmco54Q==";

    public static bool HasProductionKey => ProductionPublicKey.Length > 0;
}
