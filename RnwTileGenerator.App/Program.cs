using System;
using System.IO;
using System.Linq;
using System.Windows;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.App;

/// <summary>
/// Entry point. This project has no App.xaml (see the "code-only WPF" note
/// in the .csproj) - the whole UI, including the Application object
/// itself, is built in plain C#, so there is no XAML compiler step to
/// depend on.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Update mode first: the new version runs from its staging folder and only swaps the
        // program folder (no splash, no main window). See UpdateApplyMode / RnwTileGenerator.Updates.
        if (args.Contains(ApplyUpdateArguments.Switch))
            return UpdateApplyMode.Run(args);

        var start = UpdateStartArguments.Parse(args);

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        CrashHandling.Install(app);

        // Splash first: everything below runs on the UI thread, so each step repaints the
        // bar explicitly (SplashWindow.Step). Steps are the real start-up work, in order.
        var splash = new SplashWindow();
        splash.Show();

        splash.Step(0.05, "Loading language...");
        _ = Loc.Current;
        splash.Step(0.20, Loc.T("splash.settings"));
        GenerationPrefs.GetDouble("startup.warmup", 0);
        PresetStore.LoadUser();
        splash.Step(0.40, Loc.T("splash.icons"));
        PreloadIcons();
        splash.Step(0.65, Loc.T("splash.window"));
        var window = new MainWindow(start);
        // The splash was the first window, so WPF made it Application.MainWindow; with
        // ShutdownMode.OnMainWindowClose closing it would end the program.
        app.MainWindow = window;
        // Start signal for the waiting update mode: right after the main window exists, before
        // anything that could show a dialog (the update mode gives up after 90 s).
        if (start.Applied is { } applied)
            MainWindow.WriteStartMarker(applied);
        splash.Step(1.0, Loc.T("splash.ready"));

        // One-shot: WPF raises ContentRendered again whenever Content is replaced (landing -> editor).
        EventHandler? firstRendered = null;
        firstRendered = (_, _) =>
        {
            window.ContentRendered -= firstRendered;
            splash.Close();
            window.OnFirstRenderedAfterStart();
        };
        window.ContentRendered += firstRendered;
        return app.Run(window);
    }

    private static void PreloadIcons()
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "Icons");
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*.dds"))
                IconCatalog.TryGet(Path.GetFileNameWithoutExtension(file));
        }
        catch
        {
            // Pre-loading is only a head start; IconCatalog loads lazily anyway.
        }
    }
}
