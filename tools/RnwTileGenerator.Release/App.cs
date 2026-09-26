using System.Windows;

namespace RnwTileGenerator.Release;

/// <summary>Einstieg des Release-Werkzeugs (code-only, ohne App.xaml).</summary>
public static class App
{
    [STAThread]
    public static void Main()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new ReleaseWindow();

        // Ein unerwarteter Fehler darf keine geänderte csproj oder keinen halb freigegebenen Changelog zurücklassen.
        application.DispatcherUnhandledException += (_, e) =>
        {
            window.RevertAfterCrash();
            MessageBox.Show(e.Exception.ToString(), "RNW Release: unerwarteter Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        application.Run(window);
    }
}
