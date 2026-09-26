using System.ComponentModel;
using System.Diagnostics;

namespace RnwTileGenerator.Updates;

/// <summary>Echte Prozesse unter Windows.</summary>
public sealed class WindowsProcessControl : IProcessControl
{
    /// <summary>Prozesse namens RnwTileGenerator mit Programmdatei im Ordner. Ist der Pfad nicht lesbar, zählt der Prozess vorsichtshalber mit.</summary>
    public IReadOnlyList<int> FindInFolder(string folder, int ownPid)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        var result = new List<int>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(UpdatePaths.ExecutableName)))
        {
            using (process)
            {
                if (process.Id == ownPid)
                {
                    continue;
                }

                string? path;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    path = null;
                }

                if (path is null || Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(process.Id);
                }
            }
        }

        return result;
    }

    public bool HasExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return true;
        }
    }

    public void Kill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
        }
    }

    public int Start(string exe, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Prozess wurde nicht gestartet.");
        return process.Id;
    }
}
