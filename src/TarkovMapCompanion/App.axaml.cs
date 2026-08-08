using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using TarkovMapCompanion.Settings;
using TarkovMapCompanion.Views;

namespace TarkovMapCompanion;

public partial class App : Application
{
    private SettingsStore? _settingsStore;
    private AppSettings? _settings;

    public AppSettings Settings => _settings ??= new AppSettings();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        _settingsStore = new SettingsStore();
        _settings = _settingsStore.Load();

        ApplyTheme(_settings.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(_settings);
            RestorePlacement(window, _settings);

            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => PersistPlacement(window);
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void ApplyTheme(AppTheme theme)
    {
        RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    public void SaveSettings()
    {
        if (_settingsStore is not null && _settings is not null)
            _settingsStore.Save(_settings);
    }

    private static void RestorePlacement(MainWindow window, AppSettings settings)
    {
        window.Topmost = settings.AlwaysOnTop;

        var placement = settings.Window;
        if (placement is null)
            return;

        window.Width = placement.Width;
        window.Height = placement.Height;

        // Only honor a saved position if some screen still covers it -- a monitor that has since
        // been unplugged would otherwise put the window somewhere the user cannot reach it.
        if (IsOnAnyScreen(window, placement))
        {
            window.WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.Manual;
            window.Position = new PixelPoint((int)placement.X, (int)placement.Y);
        }

        if (placement.Maximized)
            window.WindowState = Avalonia.Controls.WindowState.Maximized;
    }

    private static bool IsOnAnyScreen(MainWindow window, WindowPlacement placement)
    {
        var screens = window.Screens;
        if (screens is null || screens.ScreenCount == 0)
            return false;

        // Require a chunk of the title bar to land on a screen, not just a single corner pixel.
        var probe = new PixelPoint((int)(placement.X + placement.Width / 2), (int)(placement.Y + 16));

        foreach (var screen in screens.All)
        {
            if (screen.Bounds.Contains(probe))
                return true;
        }

        return false;
    }

    private void PersistPlacement(MainWindow window)
    {
        if (_settings is null)
            return;

        var maximized = window.WindowState == Avalonia.Controls.WindowState.Maximized;

        _settings.Window = new WindowPlacement
        {
            // A maximized window reports the maximized rect; keep the last normal size instead so
            // un-maximizing on the next launch restores something sensible.
            X = maximized ? _settings.Window?.X ?? window.Position.X : window.Position.X,
            Y = maximized ? _settings.Window?.Y ?? window.Position.Y : window.Position.Y,
            Width = maximized ? _settings.Window?.Width ?? window.Width : window.Width,
            Height = maximized ? _settings.Window?.Height ?? window.Height : window.Height,
            Maximized = maximized,
        };

        _settings.AlwaysOnTop = window.Topmost;
        SaveSettings();
    }
}
