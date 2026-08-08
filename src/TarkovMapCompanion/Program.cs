using Avalonia;
using TarkovMapCompanion.Diagnostics;

namespace TarkovMapCompanion;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        InstallCrashHandlers();

        // Headless map render, for verifying projection and imagery without a window.
        //   TarkovMapCompanion --render-test <map> [out.png] [width] [height] [floors] [nobase] [bare]
        if (args.Length > 0 && args[0] is "--render-test")
            return Tools.RenderTest.RunAsync(args[1..]).GetAwaiter().GetResult();

        // Regenerates the embedded POI snapshot from json.tarkov.dev.
        //   TarkovMapCompanion --fetch-data [out.json.gz]
        if (args.Length > 0 && args[0] is "--fetch-data")
            return Tools.FetchData.RunAsync(args[1..]).GetAwaiter().GetResult();

        // Regenerates the bundled extract-conditions file from the Tarkov wiki.
        //   TarkovMapCompanion --fetch-wiki [out.json]
        if (args.Length > 0 && args[0] is "--fetch-wiki")
            return Tools.FetchWiki.RunAsync(args[1..]).GetAwaiter().GetResult();

        try
        {
            Log.Info($"starting {typeof(Program).Assembly.GetName().Version}");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("the app closed because of an unhandled exception", ex);
            return 1;
        }
    }

    /// <summary>
    /// Records why the app died, wherever it dies.
    /// </summary>
    /// <remarks>
    /// A windowed app has no console, so an exception on a background thread -- the folder
    /// watcher, the reconcile timer, a tile download -- closes the window instantly and leaves
    /// nothing behind to explain it. That is exactly what a crash on taking a screenshot looks
    /// like. These handlers cannot always keep the process alive, but they can always say why.
    /// </remarks>
    private static void InstallCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("unhandled exception", e.ExceptionObject as Exception);

        // An unobserved faulted Task does not crash the process by default, but it does mean
        // something failed silently and is worth knowing about.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    // Referenced by name by the Avalonia previewer and designer tooling.
    // No embedded font: the UI is monospace throughout and resolves Cascadia Mono / Consolas
    // from the system, so there is nothing to bundle.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
