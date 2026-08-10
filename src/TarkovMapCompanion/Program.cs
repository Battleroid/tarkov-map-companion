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

        // Regenerates the embedded quest snapshot from json.tarkov.dev.
        //   TarkovMapCompanion --fetch-tasks [out.json.gz]
        if (args.Length > 0 && args[0] is "--fetch-tasks")
            return Tools.FetchTasks.RunAsync(args[1..]).GetAwaiter().GetResult();

        // Regenerates the bundled extract-conditions file from the Tarkov wiki.
        //   TarkovMapCompanion --fetch-wiki [out.json]
        if (args.Length > 0 && args[0] is "--fetch-wiki")
            return Tools.FetchWiki.RunAsync(args[1..]).GetAwaiter().GetResult();

        // Reads the extraction panel out of one screenshot, showing every stage of the decision.
        //   TarkovMapCompanion --read-exits <screenshot.png> [map] [whole]
        if (args.Length > 0 && args[0] is "--read-exits")
            return Tools.ReadExits.RunAsync(args[1..]).GetAwaiter().GetResult();

        // Hosts or joins a position-sharing session from the console, to check whether this
        // network can host at all.
        //   TarkovMapCompanion --party-test [name]
        //   TarkovMapCompanion --party-test join <code> [name]
        // Prints every place Tarkov screenshots might be, and what is in each.
        //   TarkovMapCompanion --find-screenshots
        if (args.Length > 0 && args[0] is "--find-screenshots")
            return Tools.FindScreenshots.Run();

        // Prints where Tarkov's own logs are and what the parser makes of the newest one.
        //   TarkovMapCompanion --find-logs [logs folder]
        if (args.Length > 0 && args[0] is "--find-logs")
            return Tools.FindLogs.Run(args[1..]);

        if (args.Length > 0 && args[0] is "--party-test")
            return Tools.PartyTest.RunAsync(args[1..]).GetAwaiter().GetResult();

        try
        {
            Log.Info($"starting {typeof(Program).Assembly.GetName().Version}");

            WarnAboutASecondInstance();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("the app closed because of an unhandled exception", ex);
            return 1;
        }
    }

    /// <summary>Held for the lifetime of the process; releasing it early would defeat the check.</summary>
    private static Mutex? _instanceLock;

    /// <summary>True when another copy of the app was already running at startup.</summary>
    public static bool AnotherInstanceRunning { get; private set; }

    /// <summary>
    /// Notices a second copy of the app.
    /// </summary>
    /// <remarks>
    /// Two instances watching one folder is not merely redundant. Both read every screenshot, so
    /// the OCR runs twice and can disagree when the two have different maps selected; both cull,
    /// so they race to recycle the same files and each fails on the ones the other already took;
    /// and one can delete a screenshot while the other is still reading it. All of that appeared
    /// in a real log as a scattering of unrelated-looking warnings.
    /// </remarks>
    private static void WarnAboutASecondInstance()
    {
        try
        {
            // Local, not Global: two different users on one machine are genuinely separate.
            _instanceLock = new Mutex(initiallyOwned: true, @"Local\TarkovMapCompanion", out var first);
            AnotherInstanceRunning = !first;

            if (AnotherInstanceRunning)
            {
                Log.Warn(
                    "another copy of Tarkov Map Companion is already running. Two copies watching one "
                    + "folder will read every screenshot twice and fight over culling; close one.");
            }
        }
        catch (Exception ex)
        {
            // Not being able to tell is not a reason to refuse to start.
            Log.Warn($"could not check for another instance: {ex.Message}");
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
