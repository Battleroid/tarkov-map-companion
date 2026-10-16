using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TarkovMapCompanion.Data;
using TarkovMapCompanion.Localization;
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

    // Which category tab was open last time. Static so it survives the window being closed and
    // reopened, which is the whole point of remembering it.
    private static int _lastCategory;

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
        InitializeCategories();

        _loading = false;
    }

    /// <summary>
    /// Wires the category rail and swaps the six pages. The last category is restored from a static
    /// field, because a settings window that forgets which tab you were on every time you reopen it
    /// is the reason the single scroller was worth replacing.
    /// </summary>
    private void InitializeCategories()
    {
        CategoryList.SelectedIndex = _lastCategory is >= 0 and < 6 ? _lastCategory : 0;

        CategoryList.SelectionChanged += (_, _) =>
        {
            _lastCategory = CategoryList.SelectedIndex;
            ShowCategory(_lastCategory);
        };

        ShowCategory(CategoryList.SelectedIndex);
    }

    private void ShowCategory(int index)
    {
        // -1 is what a list reports with no selection; showing every page as invisible would leave
        // the pane blank. Fall back to the first category rather than honoring the sentinel.
        if (index < 0)
            index = 0;

        GeneralPage.IsVisible = index == 0;
        CapturePage.IsVisible = index == 1;
        DisplayPage.IsVisible = index == 2;
        QuestsPage.IsVisible = index == 3;
        PartyPage.IsVisible = index == 4;
        LanSyncPage.IsVisible = index == 5;
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

        ReadGameLogBox.IsChecked = _settings.ReadGameLog;
        AutoSwitchFromLogBox.IsChecked = _settings.AutoSwitchMapFromGameLog;
        AutoSwitchFromLogBox.IsEnabled = _settings.ReadGameLog;
        GameLogFolderBox.Text = _settings.GameLogFolder;
        UpdateGameLogStatus();

        SmoothCameraBox.IsChecked = _settings.SmoothCameraMovement;

        PlayerColorPicker.Color = ToAvalonia(_settings.PlayerColor, Rendering.MarkerPalette.Player);
        GuideColorPicker.Color = ToAvalonia(_settings.GuideLineColor, Rendering.MarkerPalette.ExtractLine);

        // The palette still seeds the swatch grid inside the picker, so the colors chosen for
        // separation under color blindness stay one click away even though anything is now reachable.
        foreach (var picker in new[] { PlayerColorPicker, GuideColorPicker })
            picker.Palette = new MarkerColorPalette();

        PlayerSizeBox.Value = (decimal)_settings.PlayerMarkerSize;
        PeerTrailBox.Value = _settings.PeerTrailLength;
        UpdatePeerTrailLabel();

        MinimapRangeBox.Value = (decimal)_settings.MinimapRangeMeters;
        MinimapClickThroughBox.IsChecked = _settings.MinimapClickThrough;

        ShowConditionsBox.IsChecked = _settings.ShowExitConditions;

        ExitFilterBox.ItemsSource = Enum.GetValues<ExitFilter>().Select(f => f.Label()).ToArray();
        ExitFilterBox.SelectedIndex = (int)_settings.ExitFilter;

        LevelFromLogBox.IsChecked = _settings.PlayerLevelFromGameLog;
        PlayerLevelBox.Value = _settings.PlayerLevel;
        ShowOffScreenPeersBox.IsChecked = _settings.ShowOffScreenPeers;
        ShareRouteBox.IsChecked = _settings.ShareRouteWithParty;
        AnimateArrowsBox.IsChecked = _settings.AnimateRouteArrows;
        SuggestMapBox.IsChecked = _settings.SuggestMapFromPosition;
        AutoSwitchBox.IsChecked = _settings.AutoSwitchMap;
        AutoSwitchBox.IsEnabled = _settings.SuggestMapFromPosition;

        PaddingSlider.Value = _settings.ExtractFocusPadding;
        UpdatePaddingLabel();

        TrailSlider.Value = _settings.HistoryTrailLength;
        UpdateTrailLabel();

        PlayerNameBox.Text = _settings.PlayerName;
        UpdatePlayerNameHint();

        PartyPortBox.Value = _settings.PartyPort;
        UpdatePartyPortHint();

        WebSyncEnableBox.IsChecked = _settings.WebSyncEnabled;
        WebSyncPortBox.Value = _settings.WebSyncPort;
        WebSyncFollowFloorBox.IsChecked = _settings.WebSyncFollowPlayerFloor;
        WebSyncShowPoisBox.IsChecked = _settings.WebSyncShowPois;
        WebSyncShowQuestsBox.IsChecked = _settings.WebSyncShowQuests;
        WebSyncShowAnnotationsBox.IsChecked = _settings.WebSyncShowAnnotations;
        UpdateWebSyncPortHint();

        // Reads the saved state into the URL, QR and status. No session call: the session already
        // applied WebSync on startup, and re-applying here is not needed to draw the controls.
        RefreshWebSync();

        PingSoundBox.IsChecked = _settings.PingSound;

        ArrivalRadiusBox.Value = (decimal)_settings.WaypointArrivalRadiusMeters;

        ArrivalMarkThenRemove.IsChecked = _settings.WaypointArrival == WaypointArrival.MarkThenRemove;
        ArrivalRemoveOnArrival.IsChecked = _settings.WaypointArrival == WaypointArrival.RemoveOnArrival;

        UpdateRaidLengthText();

        ThemeBox.ItemsSource = Enum.GetValues<AppTheme>();

        // Items stay the enum (that is what settings.json persists); only the rendered label is
        // localized, so SelectionChanged keeps matching on AppTheme as it always did.
        ThemeBox.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<AppTheme>(
            (theme, _) => new TextBlock { Text = Localizer.ThemeLabel(theme) });

        ThemeBox.SelectedItem = _settings.Theme;

        LanguageLabel.Text = Localization.Localizer.Loc("prefs.language");
        LanguageBox.ItemsSource = new[]
        {
            Localization.Localizer.Loc("language.english"),
            Localization.Localizer.Loc("language.chineseSimplified"),
        };
        LanguageBox.SelectedIndex = _settings.Language == AppLanguage.ChineseSimplified ? 1 : 0;

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

        DetectFolderButton.Click += (_, _) => DetectFolder();
        FolderBox.LostFocus += (_, _) => ApplyFolder();

        ReadGameLogBox.IsCheckedChanged += (_, _) => Apply(() =>
        {
            _settings.ReadGameLog = ReadGameLogBox.IsChecked ?? false;
            AutoSwitchFromLogBox.IsEnabled = _settings.ReadGameLog;

            // Takes effect now, in both directions. Turning it off closes the file.
            _session?.StartWatchingGameLog();
            UpdateGameLogStatus();
        });

        AutoSwitchFromLogBox.IsCheckedChanged += (_, _) => Apply(() =>
            _settings.AutoSwitchMapFromGameLog = AutoSwitchFromLogBox.IsChecked ?? true);

        BrowseLogButton.Click += async (_, _) => await BrowseForLogFolderAsync();
        DetectLogButton.Click += (_, _) => DetectLogFolder();
        GameLogFolderBox.LostFocus += (_, _) => ApplyLogFolder();

        CullOff.IsCheckedChanged += (_, _) => ApplyCullMode();
        CullKeepLatest.IsCheckedChanged += (_, _) => ApplyCullMode();
        CullAfterRead.IsCheckedChanged += (_, _) => ApplyCullMode();
        CullCount.ValueChanged += (_, _) => Apply(() =>
            _settings.CullKeepCount = (int)(CullCount.Value ?? _settings.CullKeepCount));

        // ColorChanged rather than a property watch: the picker raises it once the user settles on a
        // color, not for every pixel of a drag across the spectrum, which would otherwise rewrite
        // the settings file a few hundred times per gesture.
        PlayerColorPicker.ColorChanged += (_, e) => Apply(() =>
        {
            var color = FromAvalonia(e.NewColor);
            _settings.PlayerColor = Rendering.ColorCodec.ToHex(color);

            // Straight onto the overlay as well as into the settings: this is the one preference
            // whose effect you want to see on the map while the dialog is still open.
            if (_session is null)
                return;

            _session.Player.Color = color;

            // And out to the squad, which is a no-op unless a session is running.
            _session.Party.SelfColor = _settings.PlayerColor;
        });

        GuideColorPicker.ColorChanged += (_, e) => Apply(() =>
        {
            var color = FromAvalonia(e.NewColor);
            _settings.GuideLineColor = Rendering.ColorCodec.ToHex(color);

            if (_session is not null)
                _session.ExtractLine.Color = color;
        });

        PlayerSizeBox.ValueChanged += (_, _) => Apply(() =>
        {
            _settings.PlayerMarkerSize = (double)(PlayerSizeBox.Value ?? (decimal)_settings.PlayerMarkerSize);

            if (_session is not null)
                _session.Player.MarkerSize = (float)_settings.PlayerMarkerSize;
        });

        PeerTrailBox.ValueChanged += (_, _) => Apply(() =>
        {
            _settings.PeerTrailLength = (int)(PeerTrailBox.Value ?? _settings.PeerTrailLength);
            UpdatePeerTrailLabel();

            if (_session is not null)
                _session.Peers.TrailLength = _settings.PeerTrailLength;
        });

        ShowConditionsBox.IsCheckedChanged += (_, _) => Apply(() =>
            _settings.ShowExitConditions = ShowConditionsBox.IsChecked ?? true);

        ExitFilterBox.SelectionChanged += (_, _) => Apply(() =>
        {
            if (ExitFilterBox.SelectedIndex >= 0)
                _settings.ExitFilter = (ExitFilter)ExitFilterBox.SelectedIndex;
        });

        LevelFromLogBox.IsCheckedChanged += (_, _) => Apply(() =>
            _settings.PlayerLevelFromGameLog = LevelFromLogBox.IsChecked ?? true);

        PlayerLevelBox.ValueChanged += (_, _) => Apply(() =>
            _settings.PlayerLevel = (int)(PlayerLevelBox.Value ?? _settings.PlayerLevel));

        MinimapRangeBox.ValueChanged += (_, _) => Apply(() =>
            _settings.MinimapRangeMeters =
                (double)(MinimapRangeBox.Value ?? (decimal)_settings.MinimapRangeMeters));

        MinimapClickThroughBox.IsCheckedChanged += (_, _) => Apply(() =>
            _settings.MinimapClickThrough = MinimapClickThroughBox.IsChecked ?? false);

        ShowOffScreenPeersBox.IsCheckedChanged += (_, _) => Apply(() =>
        {
            _settings.ShowOffScreenPeers = ShowOffScreenPeersBox.IsChecked ?? true;

            if (_session is not null)
                _session.Peers.ShowOffScreen = _settings.ShowOffScreenPeers;
        });

        ShareRouteBox.IsCheckedChanged += (_, _) => Apply(() =>
        {
            _settings.ShareRouteWithParty = ShareRouteBox.IsChecked ?? true;

            // Turning it off has to withdraw what is already out there, not merely stop sending.
            // Leaving your last route drawn on everyone's map after you opted out would be the
            // worst reading of the setting.
            _session?.RepublishRoute();
        });

        AnimateArrowsBox.IsCheckedChanged += (_, _) => Apply(() =>
        {
            _settings.AnimateRouteArrows = AnimateArrowsBox.IsChecked ?? true;

            if (_session is not null)
                _session.Waypoints.AnimateArrows = _settings.AnimateRouteArrows;
        });

        SmoothCameraBox.IsCheckedChanged += (_, _) => Apply(() =>
            _settings.SmoothCameraMovement = SmoothCameraBox.IsChecked ?? false);

        PlayerNameBox.TextChanged += (_, _) => Apply(() =>
        {
            _settings.PlayerName = PlayerNameBox.Text ?? "";
            UpdatePlayerNameHint();
        });

        PingSoundBox.IsCheckedChanged += (_, _) => Apply(() =>
        {
            _settings.PingSound = PingSoundBox.IsChecked ?? false;
            Audio.PingSound.Enabled = _settings.PingSound;

            // Play it on the way on, so "what does it sound like" is answered by ticking the box.
            if (_settings.PingSound)
                Audio.PingSound.Play();
        });

        PartyPortBox.ValueChanged += (_, _) => Apply(() =>
        {
            _settings.PartyPort = (int)(PartyPortBox.Value ?? _settings.PartyPort);
            UpdatePartyPortHint();
        });

        WebSyncEnableBox.IsCheckedChanged += (_, _) => Apply(() =>
        {
            _settings.WebSyncEnabled = WebSyncEnableBox.IsChecked ?? false;

            // Starts or stops the embedded server without a restart; mints the first token when
            // this is the moment sharing was switched on.
            _session?.ApplyWebSyncSettings();
            RefreshWebSync();
        });

        WebSyncPortBox.ValueChanged += (_, _) => Apply(() =>
        {
            var value = WebSyncPortBox.Value;
            var port = value is { } number ? (int)number : _settings.WebSyncPort;

            // Out of range is not persisted: a bad port would leave the server unable to bind. The
            // hint says which port the control refuses rather than silently snapping the value.
            if (port < 1024 || port > 65535)
            {
                UpdateWebSyncPortHint();
                return;
            }

            _settings.WebSyncPort = port;
            UpdateWebSyncPortHint();

            // Rebinds: the reconciler restarts the host on a port change.
            _session?.ApplyWebSyncSettings();
            RefreshWebSync();
        });

        WebSyncCopyButton.Click += async (_, _) =>
        {
            var url = WebSyncUrlBox.Text;
            if (string.IsNullOrEmpty(url) || Clipboard is not { } clipboard)
                return;

            await clipboard.SetTextAsync(url);
            ShowBriefly(WebSyncCopyNote, Localizer.Loc("WebSync.Copied"));
        };

        WebSyncRegenerateButton.Click += (_, _) => Apply(() =>
        {
            // Clearing the token is what makes ApplyWebSyncSettings mint a fresh one: it generates
            // only when sharing is on and the token is empty. Anything already opened with the old
            // key stops working, which is the point of the button.
            _settings.WebSyncAccessToken = "";
            _session?.ApplyWebSyncSettings();

            RefreshWebSync();
            ShowBriefly(WebSyncRegenerateNote, Localizer.Loc("WebSync.TokenRegenerated"));
        });

        WebSyncFollowFloorBox.IsCheckedChanged += (_, _) => Apply(() =>
            _settings.WebSyncFollowPlayerFloor = WebSyncFollowFloorBox.IsChecked ?? true);

        WebSyncShowPoisBox.IsCheckedChanged += (_, _) => Apply(() =>
            _settings.WebSyncShowPois = WebSyncShowPoisBox.IsChecked ?? true);

        WebSyncShowQuestsBox.IsCheckedChanged += (_, _) => Apply(() =>
            _settings.WebSyncShowQuests = WebSyncShowQuestsBox.IsChecked ?? true);

        WebSyncShowAnnotationsBox.IsCheckedChanged += (_, _) => Apply(() =>
            _settings.WebSyncShowAnnotations = WebSyncShowAnnotationsBox.IsChecked ?? true);

        ArrivalMarkThenRemove.IsCheckedChanged += (_, _) => ApplyArrivalMode();
        ArrivalRemoveOnArrival.IsCheckedChanged += (_, _) => ApplyArrivalMode();

        ArrivalRadiusBox.ValueChanged += (_, _) => Apply(() =>
        {
            _settings.WaypointArrivalRadiusMeters =
                (double)(ArrivalRadiusBox.Value ?? (decimal)_settings.WaypointArrivalRadiusMeters);

            if (_session is not null)
                _session.Waypoints.ArrivalRadiusMeters = _settings.WaypointArrivalRadiusMeters;
        });

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

        LanguageBox.SelectionChanged += (_, _) => Apply(() =>
        {
            var language = LanguageBox.SelectedIndex == 1
                ? AppLanguage.ChineseSimplified
                : AppLanguage.English;

            if (language == _settings.Language)
            {
                // Back to the running language: there is nothing left to restart for.
                LanguageRestartText.IsVisible = false;
                return;
            }

            // Applied on restart, not live: this only persists the choice and says so. Rebuilding
            // every already-open window in a new language is a later phase.
            _settings.Language = language;
            LanguageRestartText.Text = Localization.Localizer.Loc("prefs.language.restart");
            LanguageRestartText.IsVisible = true;
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
            Title = Localizer.Loc("Prefs.ChooseScreenshotFolder"),
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

    /// <summary>
    /// Searches every place Tarkov might be writing and takes the one with screenshots in it.
    /// </summary>
    private void DetectFolder()
    {
        var candidates = Screenshots.ScreenshotFolders.Candidates();
        var best = candidates.FirstOrDefault(c => c.Looks);

        if (best is null)
        {
            FolderStatus.Text =
                Localizer.Loc("Prefs.NoScreenshotsFound");

            FolderStatus.Foreground = this.FindResource("WarningBrush") as Avalonia.Media.IBrush;
            return;
        }

        FolderBox.Text = best.Path;
        ApplyFolder();

        FolderStatus.Text =
            Localizer.Loc("Prefs.ScreenshotsFound", best.ScreenshotCount, best.ScreenshotCount == 1 ? "" : "s", best.Source);
    }

    // ---- Game log folder ----------------------------------------------------

    private async Task BrowseForLogFolderAsync()
    {
        var current = GameLogFolderBox.Text ?? "";

        var start = Directory.Exists(current)
            ? await StorageProvider.TryGetFolderFromPathAsync(current)
            : null;

        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Localizer.Loc("Prefs.ChooseLogFolder"),
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        if (picked.Count == 0)
            return;

        var path = picked[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        GameLogFolderBox.Text = path;
        ApplyLogFolder();
    }

    private void ApplyLogFolder() => Apply(() =>
    {
        var folder = GameLogFolderBox.Text ?? "";
        if (string.Equals(folder, _settings.GameLogFolder, StringComparison.Ordinal))
            return;

        _settings.GameLogFolder = folder;
        _session?.StartWatchingGameLog();
        UpdateGameLogStatus();
    });

    private void DetectLogFolder()
    {
        var best = GameLog.GameLogFolders.Candidates().FirstOrDefault(c => c.Looks);

        if (best is null)
        {
            GameLogStatus.Text =
                Localizer.Loc("Prefs.NoTarkovInstall");

            GameLogStatus.Foreground = this.FindResource("WarningBrush") as Avalonia.Media.IBrush;
            return;
        }

        GameLogFolderBox.Text = best.Path;
        ApplyLogFolder();
    }

    private void UpdateGameLogStatus()
    {
        if (!_settings.ReadGameLog)
        {
            GameLogStatus.Text = Localizer.Loc("Prefs.NotReadingLog");
            GameLogStatus.Foreground = this.FindResource("TextSecondaryBrush") as Avalonia.Media.IBrush;
            return;
        }

        var folder = string.IsNullOrWhiteSpace(GameLogFolderBox.Text)
            ? GameLog.GameLogFolders.Detect()
            : GameLogFolderBox.Text;

        if (folder is null)
        {
            GameLogStatus.Text = Localizer.Loc("Prefs.LogInstallNotFound");
            GameLogStatus.Foreground = this.FindResource("WarningBrush") as Avalonia.Media.IBrush;
            return;
        }

        var launches = GameLog.GameLogFolders.CountLogFolders(folder);

        if (launches == 0)
        {
            GameLogStatus.Text = Localizer.Loc("Prefs.NoLogsInFolder", folder);
            GameLogStatus.Foreground = this.FindResource("WarningBrush") as Avalonia.Media.IBrush;
            return;
        }

        GameLogStatus.Text = Localizer.Loc("Prefs.FollowingLog", folder, launches);
        GameLogStatus.Foreground = this.FindResource("TextSecondaryBrush") as Avalonia.Media.IBrush;
    }

    private void UpdateFolderStatus()
    {
        var folder = FolderBox.Text ?? "";

        if (!Directory.Exists(folder))
        {
            FolderStatus.Text = Localizer.Loc("Prefs.FolderMissing");
            FolderStatus.Foreground = this.FindResource("WarningBrush") as Avalonia.Media.IBrush;
            UpdateCullWarning();
            return;
        }

        var count = Screenshots.ScreenshotCuller.EnumerateScreenshots(folder).Count;

        // An empty folder is the silent failure worth shouting about: everything looks fine and the
        // map just never moves. Usually it means OneDrive has relocated Documents.
        if (count == 0)
        {
            FolderStatus.Text =
                Localizer.Loc("Prefs.NoScreenshotsYet");

            FolderStatus.Foreground = this.FindResource("WarningBrush") as Avalonia.Media.IBrush;
            UpdateCullWarning();
            return;
        }

        FolderStatus.Text = Localizer.Loc("Prefs.ScreenshotsPresent", count, count == 1 ? "" : "s");
        FolderStatus.Foreground = this.FindResource("TextSecondaryBrush") as Avalonia.Media.IBrush;

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
            CullWarning.Text = Localizer.Loc("Prefs.CullDeleteAfterRead");
            return;
        }

        var doomed = Math.Max(0, present - _settings.CullKeepCount);
        CullWarning.Text = doomed == 0
            ? Localizer.Loc("Prefs.CullUnderLimit", present, _settings.CullKeepCount)
            : Localizer.Loc("Prefs.CullWillSend", present, doomed, doomed == 1 ? "" : "s");
    }

    // ---- Labels -------------------------------------------------------------

    private void UpdatePaddingLabel() =>
        PaddingLabel.Text = Localizer.Loc("Prefs.PaddingLabel", _settings.ExtractFocusPadding);

    private void UpdateTrailLabel() =>
        TrailLabel.Text = _settings.HistoryTrailLength == 0
            ? Localizer.Loc("Prefs.TrailOff")
            : Localizer.Loc("Prefs.TrailLast", _settings.HistoryTrailLength);

    private void UpdateFontLabel() => FontLabel.Text = Localizer.Loc("Prefs.FontLabel", _settings.FontSize);

    /// <summary>Says "off" rather than "0", which is the one value that changes what happens.</summary>
    private void UpdatePeerTrailLabel() =>
        PeerTrailLabel.Text = _settings.PeerTrailLength == 0 ? Localizer.Loc("Prefs.PeerTrailOff") : Localizer.Loc("Prefs.PeerTrailPositions");

    private static Avalonia.Media.Color ToAvalonia(string? stored, SkiaSharp.SKColor fallback)
    {
        var color = Rendering.ColorCodec.Parse(stored, fallback);
        return Avalonia.Media.Color.FromRgb(color.Red, color.Green, color.Blue);
    }

    /// <summary>
    /// Alpha is dropped on the way in, not just on the way out.
    /// </summary>
    /// <remarks>
    /// The picker has an alpha slider and no obvious way to hide it, so somebody can genuinely drag
    /// their own marker to invisible. The overlays already control transparency for staleness and
    /// off-floor dimming, and a stored alpha would fight them; this is the same rule ColorCodec
    /// applies to the settings file, enforced at the other end too.
    /// </remarks>
    private static SkiaSharp.SKColor FromAvalonia(Avalonia.Media.Color color) =>
        new(color.R, color.G, color.B);

    private void ApplyArrivalMode() => Apply(() =>
    {
        _settings.WaypointArrival = ArrivalRemoveOnArrival.IsChecked == true
            ? WaypointArrival.RemoveOnArrival
            : WaypointArrival.MarkThenRemove;

        if (_session is not null)
            _session.Waypoints.Arrival = _settings.WaypointArrival;
    });

    /// <summary>Says what will actually be shown, including the fallback when the box is empty.</summary>
    private void UpdatePlayerNameHint() =>
        PlayerNameHint.Text = string.IsNullOrWhiteSpace(_settings.PlayerName)
            ? Localizer.Loc("Prefs.PlayerNameHintDefault", Environment.UserName)
            : Localizer.Loc("Prefs.PlayerNameHint", Party.PartyProtocol.CleanName(_settings.PlayerName));

    /// <summary>
    /// Names the exact forward, so nobody has to work out which address is theirs.
    /// </summary>
    private void UpdatePartyPortHint()
    {
        var lan = Party.PortMapper.LocalAddress();

        PartyPortHint.Text = lan is null
            ? Localizer.Loc("Prefs.PortHintPc", _settings.PartyPort)
            : Localizer.Loc("Prefs.PortHintAddress", _settings.PartyPort, lan);
    }

    // ---- WebSync ------------------------------------------------------------

    /// <summary>
    /// Redraws the URL, the QR code and the status line together, and dims the controls that only
    /// make sense while sharing. One entry point, called from every change that can affect them
    /// (enable, port, token), so they can never disagree about what is being served.
    /// </summary>
    private void RefreshWebSync()
    {
        var enabled = _settings.WebSyncEnabled;

        WebSyncStatus.Text = enabled
            ? Localizer.Loc("WebSync.StatusRunning")
            : Localizer.Loc("WebSync.StatusStopped");

        // A link and a QR code that lead nowhere are worse than none, so they are dimmed rather than
        // left showing a stale URL. Regenerate also needs a live session to mint a token; without
        // one (the design-preview path) it is disabled instead of throwing on click.
        WebSyncUrlBox.IsEnabled = enabled;
        WebSyncCopyButton.IsEnabled = enabled;
        WebSyncQrImage.IsEnabled = enabled;
        WebSyncRegenerateButton.IsEnabled = enabled && _session is not null;

        // The broadcast toggles describe what a client sees, which only exists while sharing. Dimmed
        // to match how the Party section greys out its dependent controls.
        WebSyncFollowFloorBox.IsEnabled = enabled;
        WebSyncShowPoisBox.IsEnabled = enabled;
        WebSyncShowQuestsBox.IsEnabled = enabled;
        WebSyncShowAnnotationsBox.IsEnabled = enabled;

        var url = BuildWebSyncUrl();

        WebSyncUrlBox.Text = url ?? "";
        WebSyncQrImage.Source = url is null ? null : RenderQr(url);
    }

    /// <summary>
    /// The URL a tablet opens, or null while sharing is off -- there is nothing to reach then.
    /// </summary>
    /// <remarks>
    /// The host is the machine's best LAN address, because a tablet on the same Wi-Fi cannot reach
    /// loopback, and <see cref="WebSync.LanAddress.Best"/> already excludes loopback and link-local
    /// ranges. When no usable address is found the URL falls back to <c>127.0.0.1</c>, which only
    /// works on this machine; the loopback literal is shown as-is so the fallback is visible for what
    /// it is rather than passed off as a shareable link.
    /// </remarks>
    private string? BuildWebSyncUrl()
    {
        if (!_settings.WebSyncEnabled || string.IsNullOrEmpty(_settings.WebSyncAccessToken))
            return null;

        var host = WebSync.LanAddress.Best()?.ToString() ?? "127.0.0.1";
        return $"http://{host}:{_settings.WebSyncPort}/?key={_settings.WebSyncAccessToken}";
    }

    /// <summary>Renders the QR PNG into a bitmap, or null when there is nothing to encode.</summary>
    private static Avalonia.Media.Imaging.Bitmap? RenderQr(string url)
    {
        try
        {
            var bytes = WebSync.QrCode.RenderPng(url);
            if (bytes is null)
                return null;

            using var stream = new MemoryStream(bytes);
            return new Avalonia.Media.Imaging.Bitmap(stream);
        }
        catch (Exception)
        {
            // A QR code that will not render is a display problem, not a fatal one: the URL beside
            // it still works, so the section degrades to the plain link.
            return null;
        }
    }

    /// <summary>Says which port the control will accept, rather than snapping it silently.</summary>
    private void UpdateWebSyncPortHint()
    {
        var value = WebSyncPortBox.Value;
        var invalid = value is null || value < 1024 || value > 65535;

        WebSyncPortHint.Text = invalid ? Localizer.Loc("WebSync.PortInvalid") : "";
        WebSyncPortHint.IsVisible = invalid;
    }

    /// <summary>Shows a short confirmation next to the control that raised it, then clears it.</summary>
    private static void ShowBriefly(TextBlock note, string text)
    {
        note.Text = text;
        note.IsVisible = true;

        DispatcherTimer.RunOnce(() => note.IsVisible = false, TimeSpan.FromSeconds(2));
    }

    private void UpdateRaidLengthText()
    {
        if (_session is null)
            return;

        var minutes = _session.Player.MaxRaidLength.TotalMinutes;
        RaidLengthText.Text =
            Localizer.Loc("Prefs.RaidLengthHint", minutes);
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
            ? Localizer.Loc("Prefs.DataLastUpdated", Describe(DateTimeOffset.UtcNow - fetched))
            : Localizer.Loc("Prefs.DataAgeUnknown");

        DataStatus.Text = Localizer.Loc(
            "Prefs.DataStatus",
            store.Origin,
            age,
            _settings.AllowNetwork
                ? Localizer.Loc("Prefs.DataStatusAuto", _settings.DataRefreshIntervalHours)
                : Localizer.Loc("Prefs.DataStatusOffline"));

        RefreshDataButton.IsEnabled = _settings.AllowNetwork;
    }

    private static string Describe(TimeSpan age) => age.TotalDays switch
    {
        >= 2 => Localizer.Loc("Time.DaysAgo", age.TotalDays),
        >= 1 => Localizer.Loc("Time.Yesterday"),
        _ when age.TotalHours >= 1 => Localizer.Loc("Time.HoursAgo", age.TotalHours),
        _ => Localizer.Loc("Time.JustNow"),
    };

    private async Task RefreshDataAsync()
    {
        if (_session is null)
            return;

        RefreshDataButton.IsEnabled = false;
        DataStatus.Text = Localizer.Loc("Prefs.DownloadingData");

        var ok = await _session.MapData.RefreshAsync();

        DataStatus.Text = ok ? Localizer.Loc("Prefs.DataUpdated") : Localizer.Loc("Prefs.DataUnreachable");
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
