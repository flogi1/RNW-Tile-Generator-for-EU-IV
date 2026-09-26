using System.Net;
using System.Runtime.InteropServices;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

/// <summary>Splits a command line exactly like Windows does at process start (shared by the command-line checks).</summary>
public static class WindowsCommandLine
{
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int count);

    /// <summary>The first element (program name) is dropped.</summary>
    public static string[] Split(string arguments)
    {
        var pointer = CommandLineToArgvW("x.exe " + arguments, out var count);
        try
        {
            return Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!).ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }
}

public class UpdatesPart1Checks
{
    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{\"message\":\"API rate limit exceeded\"}") });
    }

    [Fact]
    public void Join_survives_windows_splitting()
    {
        string[] args = [@"C:\Program Files\RNW Ä\", "", "a\"b", @"C:\ohne", @"ende\\", "x y\\\"z"];
        Assert.Equal(args, WindowsCommandLine.Split(CommandLine.Join(args)));
    }

    [Fact]
    public async Task Checker_never_throws_on_403_rate_limit()
    {
        var result = await new UpdateChecker(new HttpClient(new StatusHandler(HttpStatusCode.Forbidden)), "flogi1/RNW-Tile-Generator-for-EU-IV")
            .CheckAsync(new AppVersion(1, 1, 0));
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Zip_name_uses_rnw_asset_name() =>
        Assert.Equal("RnwTileGenerator-1.1.0-win-x64.zip", ReleasePackaging.ZipName(new AppVersion(1, 1, 0)));

    [Fact]
    public void Rnw_names_for_exe_install_list_and_update_folder()
    {
        Assert.Equal("RnwTileGenerator.exe", UpdatePaths.ExecutableName);
        Assert.Equal("rnw-install-files.txt", UpdatePaths.InstallListName);
        Assert.EndsWith(Path.Combine("RnwTileGenerator", "updates"), UpdatePaths.Default.Root);
    }
}
