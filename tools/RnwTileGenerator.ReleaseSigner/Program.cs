using System.Security.Cryptography;
using System.Text;
using RnwTileGenerator.Updates;

// Entwicklerwerkzeug für RNW-Releases (portiert aus PMT): keygen und Notfall-Befehle; Releases laufen über tools/RnwTileGenerator.Release. Texte bewusst nur deutsch.
var defaultKey = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RnwTileGenerator-Release", "release-key.p8");

if (args.Length == 0)
{
    return Usage();
}

var options = CommandLine.ReadOptions(args, 1);
try
{
    return args[0] switch
    {
        "keygen" => KeyGen(options.GetValueOrDefault("--key", defaultKey)),
        "package" => Package(options),
        "sign" when args.Length >= 2 => Sign(args[1], options.GetValueOrDefault("--key", defaultKey)),
        "verify" when args.Length >= 2 => Verify(args[1], options.GetValueOrDefault("--public-key", UpdateKeys.ProductionPublicKey)),
        "feed" => Feed(options),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("FEHLER: " + ex.Message);
    return 1;
}

static int KeyGen(string keyFile)
{
    if (File.Exists(keyFile))
    {
        Console.Error.WriteLine($"FEHLER: {keyFile} existiert schon. Ein neuer Schlüssel würde alle bestehenden Installationen vom Auto-Update abschneiden.");
        return 1;
    }

    var password = ReadPassword("Passwort für den privaten Schlüssel: ");
    if (password.Length < 12 || password != ReadPassword("Passwort wiederholen: "))
    {
        Console.Error.WriteLine("FEHLER: Passwort zu kurz (mindestens 12 Zeichen) oder Wiederholung stimmt nicht.");
        return 1;
    }

    var (publicKey, privateKey) = ManifestSignature.CreateKeyPair(password);
    Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
    File.WriteAllBytes(keyFile, privateKey);
    File.WriteAllText(Path.ChangeExtension(keyFile, ".pub.txt"), publicKey);
    Console.WriteLine($"Privater Schlüssel: {keyFile}");
    Console.WriteLine("JETZT SICHERN: Datei und Passwort offline aufbewahren (USB-Stick, Passwortmanager). Ohne sie gibt es keine Auto-Updates mehr.");
    Console.WriteLine();
    Console.WriteLine("Öffentlicher Schlüssel (in RnwTileGenerator.Updates/UpdateKeys.cs eintragen):");
    Console.WriteLine(publicKey);
    return 0;
}

static int Package(Dictionary<string, string> options)
{
    if (!options.TryGetValue("--folder", out var folder) || !Directory.Exists(folder)
        || !AppVersion.TryParse(options.GetValueOrDefault("--version"), out var version)
        || !options.TryGetValue("--out", out var output))
    {
        return Usage();
    }

    if (!File.Exists(Path.Combine(folder, UpdatePaths.ExecutableName)))
    {
        Console.Error.WriteLine($"FEHLER: {UpdatePaths.ExecutableName} fehlt in {folder}.");
        return 1;
    }

    Directory.CreateDirectory(output);
    var zip = Path.Combine(output, ReleasePackaging.ZipName(version));
    ReleasePackaging.CreateZip(folder, zip);
    var manifest = UpdateManifest.Create(zip, version);
    File.WriteAllBytes(Path.Combine(output, UpdatePreparer.ManifestName), manifest.ToJson());
    Console.WriteLine($"{zip} ({manifest.Size / 1024 / 1024} MB, SHA-256 {manifest.Sha256})");
    return 0;
}

static int Sign(string manifestFile, string keyFile)
{
    if (!File.Exists(keyFile))
    {
        Console.Error.WriteLine($"FEHLER: Schlüsseldatei {keyFile} fehlt. Erst 'keygen' ausführen oder --key angeben.");
        return 1;
    }

    try
    {
        var signature = ManifestSignature.Sign(File.ReadAllBytes(manifestFile), File.ReadAllBytes(keyFile), ReadPassword("Passwort des Release-Schlüssels: "));
        File.WriteAllText(manifestFile + ".sig", signature, Encoding.ASCII);
        Console.WriteLine("Signiert: " + manifestFile + ".sig");
        return 0;
    }
    catch (CryptographicException)
    {
        Console.Error.WriteLine("FEHLER: Falsches Passwort oder kaputte Schlüsseldatei.");
        return 1;
    }
}

static int Verify(string manifestFile, string publicKey)
{
    if (publicKey.Length == 0)
    {
        Console.Error.WriteLine("FEHLER: In UpdateKeys.ProductionPublicKey ist kein öffentlicher Schlüssel eingetragen.");
        return 1;
    }

    var ok = ManifestSignature.Verify(File.ReadAllBytes(manifestFile), File.ReadAllText(manifestFile + ".sig"), publicKey);
    Console.WriteLine(ok ? "Signatur gültig." : "SIGNATUR UNGÜLTIG.");
    return ok ? 0 : 1;
}

static int Feed(Dictionary<string, string> options)
{
    if (!options.TryGetValue("--folder", out var folder) || !Directory.Exists(folder) || !AppVersion.TryParse(options.GetValueOrDefault("--version"), out var version))
    {
        return Usage();
    }

    ReleasePackaging.WriteLocalFeed(folder, version);
    Console.WriteLine("Lokaler Feed: " + Path.Combine(folder, LocalFeedHandler.ReleaseFile));
    return 0;
}

static string ReadPassword(string prompt)
{
    if (Environment.GetEnvironmentVariable("RNW_RELEASE_KEY_PASSWORD") is { Length: > 0 } fromEnvironment)
    {
        return fromEnvironment;
    }

    Console.Write(prompt);
    var text = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return text.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (text.Length > 0)
            {
                text.Length--;
            }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            text.Append(key.KeyChar);
        }
    }
}

static int Usage()
{
    Console.Error.WriteLine("""
        Verwendung:
          keygen [--key <Datei>]
          package --folder <Publish-Ordner> --version <x.y.z> --out <Ausgabeordner>
          sign <update-manifest.json> [--key <Datei>]
          verify <update-manifest.json> [--public-key <Base64>]
          feed --folder <Feed-Ordner> --version <x.y.z>
        """);
    return 1;
}
