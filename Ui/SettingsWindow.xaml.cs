using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LaunchHere;

/// <summary>One row of the allow/block list.</summary>
public sealed class AppEntry
{
    public required string Entry { get; init; }    // as stored in config.json: "chrome.exe" or a full path
    public ImageSource? Icon { get; init; }
    public bool IsPath => Entry.Contains('\\') || Entry.Contains('/');
    public string Name => IsPath ? Path.GetFileName(Entry) : Entry;
    public string Matches => IsPath ? Entry : "Any copy";
}

/// <summary>The settings window (WPF, Windows 11 Fluent theme). Edits a copy of the config; OK/Apply
/// hand it to the tray app, which saves and applies it live.</summary>
public partial class SettingsWindow : Window
{
    readonly Config _cfg;
    readonly Logger _log;
    readonly Action<Config> _onApply;
    readonly Func<string, Task<string?>> _installUpdate;
    readonly Action _openReport;
    readonly ObservableCollection<AppEntry> _apps = new();
    bool _loading;
    UpdateInfo? _update;

    internal SettingsWindow(Config cfg, Logger log, Action<Config> onApply, Func<string, Task<string?>> installUpdate, Action openReport)
    {
        Wpf.EnsureInitialized();
        InitializeComponent();
        Wpf.EnableKeyboard(this);
        Icon = Wpf.ToImageSource(TrayContext.MakeIcon(true));

        _cfg = cfg.Clone();
        _log = log;
        _onApply = onApply;
        _installUpdate = installUpdate;
        _openReport = openReport;

        AppsList.ItemsSource = _apps;
        _apps.CollectionChanged += (_, _) =>
        {
            EmptyListText.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Dirty();
        };
        AppsList.SelectionChanged += (_, _) => RemoveButton.IsEnabled = AppsList.SelectedItems.Count > 0;
        AppsList.KeyDown += (_, e) => { if (e.Key == Key.Delete) RemoveSelected(); };
        AddRunningButton.Click += (_, _) => AddRunning();
        BrowseButton.Click += (_, _) => Browse();
        RemoveButton.Click += (_, _) => RemoveSelected();
        AddNameButton.Click += (_, _) => AddTyped();
        AddNameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddTyped(); e.Handled = true; } };

        CheckUpdatesButton.Click += async (_, _) => await CheckForUpdates();
        UpdateNowButton.Click += async (_, _) => await UpdateNow();
        ReleaseNotesButton.Click += (_, _) => { if (_update != null) OpenUrl(_update.PageUrl); };
        ConfigLink.Click += (_, _) => OpenUrl(Config.FilePath);
        OpenLogButton.Click += (_, _) =>
        {
            if (!File.Exists(Logger.FilePath)) File.WriteAllText(Logger.FilePath, "");
            OpenUrl(Logger.FilePath);
        };
        ReportButton.Click += (_, _) => _openReport();
        VersionText.Text = $"LaunchHere {Updater.CurrentVersion}";

        OkButton.Click += (_, _) => { if (Apply()) Close(); };
        CancelButton.Click += (_, _) => Close();
        ApplyButton.Click += (_, _) => Apply();

        LoadValues();
        foreach (var t in new[] { WatchTaskbarToggle, WatchDesktopToggle, MoveOpenToggle, StartupToggle, VerboseLogToggle })
        {
            t.Checked += (_, _) => Dirty();
            t.Unchecked += (_, _) => Dirty();
        }
        BlacklistRadio.Checked += (_, _) => Dirty();
        WhitelistRadio.Checked += (_, _) => Dirty();
        foreach (var b in new[] { ArmTimeoutBox, GraceBox, CancelBox, ShrinkBox })
            b.TextChanged += (_, _) => Dirty();
        ApplyButton.IsEnabled = false;
    }

    // ------------------------------------------------------------------ values

    void LoadValues()
    {
        _loading = true;
        BlacklistRadio.IsChecked = _cfg.Mode == ListMode.Blacklist;
        WhitelistRadio.IsChecked = _cfg.Mode == ListMode.Whitelist;
        _apps.Clear();
        foreach (var a in _cfg.Apps) _apps.Add(NewEntry(a));
        WatchTaskbarToggle.IsChecked = _cfg.WatchTaskbar;
        WatchDesktopToggle.IsChecked = _cfg.WatchDesktop;
        MoveOpenToggle.IsChecked = _cfg.MoveOpenWindows;
        StartupToggle.IsChecked = Startup.IsEnabled;
        ArmTimeoutBox.Text = _cfg.ArmTimeoutMs.ToString();
        GraceBox.Text = _cfg.SameAppGraceMs.ToString();
        CancelBox.Text = _cfg.ActivationCancelMs.ToString();
        ShrinkBox.Text = _cfg.VerticalShrinkShare.ToString("0.##");
        VerboseLogToggle.IsChecked = _cfg.VerboseLog;
        _loading = false;
    }

    /// <summary>Verbose logging was switched on elsewhere (Report a problem): show it without marking
    /// the window as changed.</summary>
    internal void ShowVerboseOn()
    {
        _loading = true;
        VerboseLogToggle.IsChecked = true;
        _loading = false;
    }

    void Dirty()
    {
        if (!_loading) ApplyButton.IsEnabled = true;
    }

    bool ReadInt(TextBox box, string what, int min, int max, out int value)
    {
        if (int.TryParse(box.Text.Replace(",", "").Trim(), out value) && value >= min && value <= max) return true;
        MessageBox.Show(this, $"{what} must be a whole number from {min} to {max}.", "LaunchHere", MessageBoxButton.OK, MessageBoxImage.Warning);
        box.Focus();
        box.SelectAll();
        return false;
    }

    bool Apply()
    {
        if (!ReadInt(ArmTimeoutBox, "Wait for the app's window", 500, 60000, out int arm)) return false;
        if (!ReadInt(GraceBox, "Also move the app's next windows", 0, 30000, out int grace)) return false;
        if (!ReadInt(CancelBox, "Switch detection window", 100, 10000, out int cancel)) return false;
        if (!double.TryParse(ShrinkBox.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double shrink)
            && !double.TryParse(ShrinkBox.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out shrink)
            || shrink < 0 || shrink > 1)
        {
            MessageBox.Show(this, "Height shrink must be a number from 0 to 1.", "LaunchHere", MessageBoxButton.OK, MessageBoxImage.Warning);
            ShrinkBox.Focus();
            return false;
        }

        if (WhitelistRadio.IsChecked == true && _apps.Count == 0 &&
            MessageBox.Show(this, "\"Move only the apps listed\" is on, but the list is empty, so no app will be moved.\n\nSave anyway?",
                "LaunchHere", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;

        bool startup = StartupToggle.IsChecked == true;
        if (startup != Startup.IsEnabled)
        {
            try
            {
                Startup.Set(startup);
                _log.Info(startup ? "start with Windows: on" : "start with Windows: off");
            }
            catch (Exception ex)
            {
                _log.Error($"could not change start with Windows: {ex.Message}");
                MessageBox.Show(this, $"Could not change the startup setting:\n{ex.Message}", "LaunchHere", MessageBoxButton.OK, MessageBoxImage.Warning);
                _loading = true;
                StartupToggle.IsChecked = Startup.IsEnabled;
                _loading = false;
            }
        }

        var c = _cfg.Clone();
        c.Mode = WhitelistRadio.IsChecked == true ? ListMode.Whitelist : ListMode.Blacklist;
        c.Apps = _apps.Select(a => a.Entry).ToList();
        c.WatchTaskbar = WatchTaskbarToggle.IsChecked == true;
        c.WatchDesktop = WatchDesktopToggle.IsChecked == true;
        c.MoveOpenWindows = MoveOpenToggle.IsChecked == true;
        c.ArmTimeoutMs = arm;
        c.SameAppGraceMs = grace;
        c.ActivationCancelMs = cancel;
        c.VerticalShrinkShare = shrink;
        c.VerboseLog = VerboseLogToggle.IsChecked == true;
        _onApply(c);
        ApplyButton.IsEnabled = false;
        return true;
    }

    // ------------------------------------------------------------------ app list

    static AppEntry NewEntry(string entry, string? exePathForIcon = null)
    {
        bool isPath = entry.Contains('\\') || entry.Contains('/');
        string iconPath = exePathForIcon ?? (isPath ? entry : RunningApps.FindPath(entry));
        return new AppEntry { Entry = entry, Icon = Wpf.IconOf(iconPath) };
    }

    bool Add(string entry, string? exePathForIcon = null)
    {
        entry = entry.Trim().Trim('"');
        if (entry.Length == 0) return false;
        bool isPath = entry.Contains('\\') || entry.Contains('/');
        if (!isPath && !entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) entry += ".exe";
        if (_apps.Any(a => string.Equals(a.Entry, entry, StringComparison.OrdinalIgnoreCase))) return false;
        _apps.Add(NewEntry(entry, exePathForIcon));
        return true;
    }

    void AddTyped()
    {
        if (Add(AddNameBox.Text)) AddNameBox.Clear();
        else if (AddNameBox.Text.Trim().Length > 0) System.Media.SystemSounds.Beep.Play();
    }

    void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an app",
            Filter = "Apps (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dlg.ShowDialog(this) != true) return;
        string name = Path.GetFileName(dlg.FileName);
        var answer = MessageBox.Show(this,
            $"Match every copy of {name} (Yes), or only this one (No)?\n\n{dlg.FileName}",
            "LaunchHere", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;
        Add(answer == MessageBoxResult.Yes ? name : dlg.FileName, dlg.FileName);
    }

    void AddRunning()
    {
        var dlg = new RunningAppsWindow(_apps.Select(a => a.Entry)) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        foreach (var app in dlg.Selected) Add(app.Name, app.Path);
    }

    void RemoveSelected()
    {
        foreach (var a in AppsList.SelectedItems.Cast<AppEntry>().ToList()) _apps.Remove(a);
    }

    // ------------------------------------------------------------------ updates

    async Task CheckForUpdates()
    {
        CheckUpdatesButton.IsEnabled = false;
        ReleaseNotesButton.Visibility = UpdateNowButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "Checking for updates...";
        try
        {
            _update = await Updater.CheckAsync();
            if (_update == null)
            {
                UpdateStatusText.Text = $"You're up to date. Last checked {DateTime.Now:t}.";
                _log.Info("update check: up to date");
            }
            else
            {
                UpdateStatusText.Text = $"Version {_update.Version} is available.";
                ReleaseNotesButton.Visibility = UpdateNowButton.Visibility = Visibility.Visible;
                _log.Info($"update check: {_update.Version} available");
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Couldn't check for updates: {ex.Message}";
            _log.Error($"update check failed: {ex.Message}");
        }
        finally { CheckUpdatesButton.IsEnabled = true; }
    }

    async Task UpdateNow()
    {
        if (_update == null) return;
        if (ApplyButton.IsEnabled)
        {
            var save = MessageBox.Show(this, "Save your settings changes before updating?", "LaunchHere",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (save == MessageBoxResult.Cancel) return;
            if (save == MessageBoxResult.Yes && !Apply()) return;
        }

        CheckUpdatesButton.IsEnabled = UpdateNowButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        UpdateStatusText.Text = $"Downloading version {_update.Version}...";
        string file;
        try
        {
            file = await Updater.DownloadAsync(_update, new Progress<double>(p => UpdateProgress.Value = p));
        }
        catch (Exception ex)
        {
            _log.Error($"update download failed: {ex.Message}");
            UpdateStatusText.Text = $"Update failed: {ex.Message}";
            UpdateProgress.Visibility = Visibility.Collapsed;
            CheckUpdatesButton.IsEnabled = UpdateNowButton.IsEnabled = true;
            return;
        }

        UpdateStatusText.Text = "Installing and restarting...";
        string? err = await _installUpdate(file);   // closes this window; on success the app restarts
        if (err != null)
            MessageBox.Show(err, "LaunchHere update", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    static void OpenUrl(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { }
    }
}
