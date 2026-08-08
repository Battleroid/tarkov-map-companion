using Avalonia;

namespace TarkovMapCompanion;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless map render, for verifying projection and imagery without a window.
        //   TarkovMapCompanion --render-test <map> [out.png] [width] [height]
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

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Referenced by name by the Avalonia previewer and designer tooling.
    // No embedded font: the UI is monospace throughout and resolves Cascadia Mono / Consolas
    // from the system, so there is nothing to bundle.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
