using Avalonia.Controls;
using Avalonia.Platform.Storage;
using TarkovMapCompanion.Settings;

namespace TarkovMapCompanion.Views;

/// <summary>
/// Live-editing preferences. There is no OK/Cancel: every change applies immediately and is
/// persisted, which matches how the rest of the window behaves and avoids a dialog that can be
/// dismissed into an inconsistent state.
/// </summary>
public partial class PreferencesWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MapSession? _session;
    private readonly Action _persist;

    private bool _loading = true;

    // Parameterless ctor exists only for the XAML previewer.
    public PreferencesWindow() : this(new AppSettings(), null, () => { })
    {
    }

    public PreferencesWindow(AppSettings settings, MapSession? session, Action persist)
    {
        _settings = settings;
        _session = session;
        _persist = persist;

        InitializeComponent();

        SettingsPathText.Text = SettingsStore.DefaultPath();
        SettingsPathText.SetValue(ToolTip.TipProperty, SettingsStore.DefaultPath());

        LoadValues();
        Wire();

        _loading = false;
    }

    // ---- Load ---------------------------------------------------------------

    private void LoadValues()
    {
        FolderBox.Text = _settings.ScreenshotFolder;
        UpdateFolderStatus();

        CullOff.IsChecked = _settings.CullMode == CullMode.Off;
        CullKeepLatest.IsChecked = _settings.CullMode == CullMode.KeepLatest;
        CullAfterRead.IsChecked = _settings.CullMode == CullMode.DeleteAfterRead;
        CullCount.Value = _settings.CullKeepCount;
        UpdateCullWarning();

        FollowPlayerBox.IsChecked = _settings.FollowPlayer;
        SmoothCameraBox.IsChecked = _settings.SmoothCameraMovement;
        SuggestMapBox.IsChecked = _settings.SuggestMapFromPosition;
        AutoSwitchBox.IsChecked = _settings.AutoSwitchMap;
        AutoSwitchBox.IsEnabled = _settings.SuggestMapFromPosition;

        PaddingSlider.Value = _settings.ExtractFocusPadding;
        UpdatePaddingLabel();

        TrailSlider.Value = _settings.HistoryTrailLength;
        UpdateTrailLabel();

        ArrivalRadiusSlider.Value = _settings.WaypointArrivalRadiusMeters;
        UpdateArrivalRadiusLabel();

        ArrivalMarkThenRemove.IsChecked = _settings.WaypointArrival == WaypointArrival.MarkThenRemove;
        ArrivalRemoveOnArrival.IsChecked = _settings.WaypointArrival == WaypointArrival.RemoveOnArrival;

        UpdateRaidLengthText();

        ThemeBox.ItemsSource = Enum.GetValues<AppTheme>();
        ThemeBox.SelectedItem = _settings.Theme;

        FontSlider.Value = _settings.FontSize;
        UpdateFontLabel();

        AlwaysOnTopBox.IsChecked = _settings.AlwaysOnTop;

        AllowNetworkBox.IsChecked = _settings.AllowNetwork;
        UpdateDataStatus();
    }

    // ---- Wire ---------------------------------------------------------------

    private void Wire()
    {
        CloseButton.Click += (_, _) => Close();

        BrowseButton.Click += async (_, _) => await BrowseForFolderAsync();
        DefaultFolderButton.Click += (_, _) =>
        {
            FolderBox.Text = AppSettings.DefaultScreenshotFolder();
            ApplyFolder();
        };
        FolderBox.LostFocus += (_, _) => ApplyFolder();

        CullOff.IsCheckedChanged += (_, _) => ApplyCullMode();
        CullKeepLatest.IsCheckedChanged += (_, _) => ApplyCullMode();
        CullAfterRead.IsCheckedChanged += (_, _) => ApplyCullMode();
        CullCount.ValueChanged += (_, _) => Apply(() =>
            _settings.CullKeepCount = (int)(CullCount.Value ?? _settings.CullKeepCount));

        FollowPlayerBox.IsCheckedChanged += (_, _) => Apply(() =>
            _settings.FollowPlayer = FollowPlayerBox.IsChecked ?? false);

        SmoothCameraBox.IsCheckedChanged += (_, _) => Apply(() =>
            _settings.SmoothCameraMovement = SmoothCameraBox.IsChecked ?? false);

        ArrivalMarkThenRemove.IsCheckedChanged += (_, _) => ApplyArrivalMode();
        ArrivalRemoveOnArrival.IsCheckedChanged += (_, _) => ApplyArrivalMode();

        ArrivalRadiusSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty)
                return;

            Apply(() =>
            {
                _settings.WaypointArrivalRadiusMeters = Math.Round(ArrivalRadiusSlider.Value);

                if (_session is not null)
                    _session.Waypoints.ArrivalRadiusMeters = _settings.WaypointArrivalRadiusMeters;

                UpdateArrivalRadiusLabel();
            });
        };

        SuggestMapBox.IsCheckedChanged += (_, _) => Apply(() =>
        {
            _settings.SuggestMapFromPosition = SuggestMapBox.IsChecked ?? false;
            AutoSwitchBox.IsEnabled = _settings.SuggestMapFromPosition;
        });

        AutoSwitchBox.IsCheckedChanged += (_, _) => Apply(() =>
            _settings.AutoSwitchMap = AutoSwitchBox.IsChecked ?? false);

        PaddingSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty)
                return;

            Apply(() =>
            {
                _settings.ExtractFocusPadding = PaddingSlider.Value;
                UpdatePaddingLabel();
            });
        };

        TrailSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty)
                return;

            Apply(() =>
            {
                _settings.HistoryTrailLength = (int)Math.Round(TrailSlider.Value);
                if (_session is not null)
                    _session.Player.TrailLength = _settings.HistoryTrailLength;

                UpdateTrailLabel();
            });
        };

        ThemeBox.SelectionChanged += (_, _) => Apply(() =>
        {
            if (ThemeBox.SelectedItem is not AppTheme theme)
                return;

            _settings.Theme = theme;
            (Avalonia.Application.Current as App)?.ApplyTheme(theme);
        });

        FontSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty)
                return;

            Apply(() =>
            {
                _settings.FontSize = Math.Round(FontSlider.Value);
                if (Owner is Window owner)
                    owner.FontSize = _settings.FontSize;

                FontSize = _settings.FontSize;
                UpdateFontLabel();
            });
        };

        AlwaysOnTopBox.IsCheckedChanged += (_, _) => Apply(() =>
        {
            _settings.AlwaysOnTop = AlwaysOnTopBox.IsChecked ?? false;
            if (Owner is Window owner)
                owner.Topmost = _settings.AlwaysOnTop;
        });

        AllowNetworkBox.IsCheckedChanged += (_, _) => Apply(() =>
        {
            _settings.AllowNetwork = AllowNetworkBox.IsChecked ?? false;
            UpdateDataStatus();
        });

        RefreshDataButton.Click += async (_, _) => await RefreshDataAsync();
        OpenCacheButton.Click += (_, _) => OpenFolder(AppPaths.CacheDirectory);
    }

    /// <summary>Runs an edit, then persists. Suppressed while the controls are being populated.</summary>
    private void Apply(Action change)
    {
        if (_loading)
            return;

        change();
        _persist();
    }

    // ---- Folder -------------------------------------------------------------

    private async Task BrowseForFolderAsync()
    {
        var start = Directory.Exists(_settings.ScreenshotFolder)
            ? await StorageProvider.TryGetFolderFromPathAsync(_settings.ScreenshotFolder)
            : null;

        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the Tarkov screenshot folder",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        if (picked.Count == 0)
            return;

        var path = picked[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        FolderBox.Text = path;
        ApplyFolder();
    }

    private void ApplyFolder() => Apply(() =>
    {
        var folder = FolderBox.Text ?? "";
        if (string.Equals(folder, _settings.ScreenshotFolder, StringComparison.Ordinal))
            return;

        _settings.ScreenshotFolder = folder;
        _settings.Normalize();
        FolderBox.Text = _settings.ScreenshotFolder;

        // Point the watcher at the new folder straight away rather than waiting for a restart.
        _session?.StartWatching();
        UpdateFolderStatus();
    });

    private void UpdateFolderStatus()
    {
        var folder = FolderBox.Text ?? "";

        if (Directory.Exists(folder))
        {
            var count = Screenshots.ScreenshotCuller.EnumerateScreenshots(folder).Count();
            FolderStatus.Text = $"Found {count} Tarkov screenshot{(count == 1 ? "" : "s")}.";
            FolderStatus.Foreground = this.FindResource("TextSecondaryBrush") as Avalonia.Media.IBrush;
        }
        else
        {
            FolderStatus.Text = "That folder does not exist yet.";
            FolderStatus.Foreground = this.FindResource("WarningBrush") as Avalonia.Media.IBrush;
        }

        UpdateCullWarning();
    }

    // ---- Culling ------------------------------------------------------------

    private void ApplyCullMode() => Apply(() =>
    {
        _settings.CullMode =
            CullAfterRead.IsChecked == true ? CullMode.DeleteAfterRead :
            CullKeepLatest.IsChecked == true ? CullMode.KeepLatest :
            CullMode.Off;

        CullCount.IsEnabled = _settings.CullMode == CullMode.KeepLatest;
        UpdateCullWarning();
    });

    /// <summary>
    /// Spells out what will actually happen, with the real folder and count. A checkbox labeled
    /// "keep newest 20" is easy to tick without registering that it deletes the other 200.
    /// </summary>
    private void UpdateCullWarning()
    {
        CullCount.IsEnabled = _settings.CullMode == CullMode.KeepLatest;

        if (_settings.CullMode == CullMode.Off)
        {
            CullWarning.IsVisible = false;
            return;
        }

        var folder = FolderBox.Text ?? _settings.ScreenshotFolder;
        var present = Directory.Exists(folder)
            ? Screenshots.ScreenshotCuller.EnumerateScreenshots(folder).Count()
            : 0;

        CullWarning.IsVisible = true;

        if (_settings.CullMode == CullMode.DeleteAfterRead)
        {
            CullWarning.Text = "Every new screenshot will be sent to the Recycle Bin as soon as "
                             + "its position is read. Existing files are left alone.";
            return;
        }

        var doomed = Math.Max(0, present - _settings.CullKeepCount);
        CullWarning.Text = doomed == 0
            ? $"{present} screenshots present, under the limit of {_settings.CullKeepCount}. Nothing will be removed yet."
            : $"{present} screenshots present. The next one taken will send {doomed} older "
              + $"screenshot{(doomed == 1 ? "" : "s")} to the Recycle Bin.";
    }

    // ---- Labels -------------------------------------------------------------

    private void UpdatePaddingLabel() =>
        PaddingLabel.Text = $"Focus mode padding around the player and exit: {_settings.ExtractFocusPadding:P0}";

    private void UpdateTrailLabel() =>
        TrailLabel.Text = _settings.HistoryTrailLength == 0
            ? "Trail: off"
            : $"Trail: last {_settings.HistoryTrailLength} positions";

    private void UpdateFontLabel() => FontLabel.Text = $"Text size: {_settings.FontSize:F0} px";

    private void ApplyArrivalMode() => Apply(() =>
    {
        _settings.WaypointArrival = ArrivalRemoveOnArrival.IsChecked == true
            ? WaypointArrival.RemoveOnArrival
            : WaypointArrival.MarkThenRemove;

        if (_session is not null)
            _session.Waypoints.Arrival = _settings.WaypointArrival;
    });

    private void UpdateArrivalRadiusLabel() =>
        ArrivalRadiusLabel.Text =
            $"A marker counts as reached within {_settings.WaypointArrivalRadiusMeters:F0} m";

    private void UpdateRaidLengthText()
    {
        if (_session is null)
            return;

        var minutes = _session.Player.MaxRaidLength.TotalMinutes;
        RaidLengthText.Text =
            $"Trail is cleared between raids automatically, and never shows more than {minutes:F0} "
            + "minutes of history. That ceiling comes from the current map's raid length.";
    }

    // ---- Data ---------------------------------------------------------------

    private void UpdateDataStatus()
    {
        if (_session is null)
        {
            DataStatus.Text = "";
            return;
        }

        var store = _session.MapData;
        var age = store.FetchedAt is { } fetched
            ? $"last updated {Describe(DateTimeOffset.UtcNow - fetched)}"
            : "age unknown";

        DataStatus.Text = $"Exits, spawns and loot come from {store.Origin} ({age}). "
                        + (_settings.AllowNetwork
                            ? $"Refreshed automatically every {_settings.DataRefreshIntervalHours} hours."
                            : "Downloads are off, so the bundled data will be used.");

        RefreshDataButton.IsEnabled = _settings.AllowNetwork;
    }

    private static string Describe(TimeSpan age) => age.TotalDays switch
    {
        >= 2 => $"{age.TotalDays:F0} days ago",
        >= 1 => "yesterday",
        _ when age.TotalHours >= 1 => $"{age.TotalHours:F0} hours ago",
        _ => "just now",
    };

    private async Task RefreshDataAsync()
    {
        if (_session is null)
            return;

        RefreshDataButton.IsEnabled = false;
        DataStatus.Text = "Downloading map data...";

        var ok = await _session.MapData.RefreshAsync();

        DataStatus.Text = ok ? "Map data updated." : "Could not reach tarkov.dev. The existing data is still in use.";
        RefreshDataButton.IsEnabled = _settings.AllowNetwork;

        if (ok)
            UpdateDataStatus();
    }

    private static void OpenFolder(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not open {path}: {ex.Message}");
        }
    }
}
