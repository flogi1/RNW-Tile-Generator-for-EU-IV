using System.Diagnostics;
using System.Text;

namespace RnwTileGenerator.Release;

/// <summary>Ergebnis eines Programmaufrufs: Exitcode und die Zeilen der Standardausgabe.</summary>
public sealed record CommandResult(int ExitCode, IReadOnlyList<string> Output);

/// <summary>Startet externe Programme (git, dotnet, gh). Tests setzen eine Attrappe ein.</summary>
public interface ICommandRunner
{
    /// <summary>
    /// <paramref name="log"/> bekommt Standardausgabe und Fehlerausgabe zeilenweise. <paramref name="environment"/> wird zusätzlich gesetzt. Abbruch
    /// beendet den Prozess samt Kindprozessen und wirft <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<CommandResult> RunAsync(
        string program,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        Action<string> log,
        CancellationToken cancellation);
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(
        string program,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        Action<string> log,
        CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(program)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }

        var output = new List<string>();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                lock (output)
                {
                    output.Add(line);
                }

                log(line);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                log(line);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                // Schon beendet.
            }

            throw;
        }

        // Nach dem Ende noch ausstehende Zeilen der asynchronen Leser abholen.
        process.WaitForExit();
        lock (output)
        {
            return new CommandResult(process.ExitCode, output.ToList());
        }
    }
}
