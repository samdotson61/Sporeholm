using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SporeholmLauncher.Core;

namespace SporeholmLauncher.App.Views;

public partial class SettingsWindow : Window
{
    private readonly LauncherConfig _cfg;
    private List<ModInfo> _mods = new();
    private List<string?> _releaseTags = new();   // parallel to ReleaseBox items; null = "Latest"
    private bool _loading;

    // Parameterless ctor for Avalonia's XAML runtime loader; real use passes the config.
    public SettingsWindow() : this(LauncherConfig.Load()) { }

    public SettingsWindow(LauncherConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        AutoUpdateBox.IsChecked = _cfg.AutoUpdate;
        OfflineBox.IsChecked = _cfg.Offline;
        RollbackBtn.IsEnabled = new Updater(ReleaseSourceFactory.Create(_cfg)).CanRollback();
        SourceInfo.Text = $"{ReleaseSourceFactory.Create(_cfg).Describe()}\n{LauncherPaths.DataDir}";
        RefreshMods();
        Loaded += async (_, _) => await LoadReleasesAsync();
    }

    // ---- release picker ---------------------------------------------------

    private async Task LoadReleasesAsync()
    {
        _loading = true;
        var items = new List<string> { "Latest (newest release)" };
        _releaseTags = new List<string?> { null };

        if (!_cfg.Offline)
        {
            try
            {
                var releases = await ReleaseSourceFactory.Create(_cfg).ListReleasesAsync();
                foreach (var r in releases)
                {
                    items.Add(r.PreRelease ? $"{r.Display}  [pre-release]" : r.Display);
                    _releaseTags.Add(r.Tag);
                }
                ReleaseHint.Text = releases.Count == 0
                    ? "This source doesn't list releases (it serves a single manifest)."
                    : "Pick an older release to install a specific version.";
            }
            catch { ReleaseHint.Text = "Couldn't reach the release list."; }
        }
        else ReleaseHint.Text = "Offline — release list unavailable.";

        ReleaseBox.ItemsSource = items;
        var sel = _releaseTags.FindIndex(t => string.Equals(t, _cfg.SelectedRelease, StringComparison.OrdinalIgnoreCase));
        ReleaseBox.SelectedIndex = sel >= 0 ? sel : 0;
        _loading = false;
    }

    private void OnReleaseChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var idx = ReleaseBox.SelectedIndex;
        if (idx < 0 || idx >= _releaseTags.Count) return;
        _cfg.SelectedRelease = _releaseTags[idx];   // null for "Latest"
        _cfg.Save();
    }

    // ---- toggles ----------------------------------------------------------

    private void OnAutoUpdate(object? s, RoutedEventArgs e) { _cfg.AutoUpdate = AutoUpdateBox.IsChecked == true; _cfg.Save(); }

    private void OnOffline(object? s, RoutedEventArgs e)
    {
        _cfg.Offline = OfflineBox.IsChecked == true;
        _cfg.Save();
        SourceInfo.Text = $"{ReleaseSourceFactory.Create(_cfg).Describe()}\n{LauncherPaths.DataDir}";
    }

    private void OnRollback(object? s, RoutedEventArgs e)
    {
        try
        {
            new Updater(ReleaseSourceFactory.Create(_cfg)).Rollback();
            RollbackBtn.IsEnabled = new Updater(ReleaseSourceFactory.Create(_cfg)).CanRollback();
            RollbackBtn.Content = $"Rolled back to {InstalledState.Load().Version}";
        }
        catch { RollbackBtn.Content = "Nothing to roll back to"; RollbackBtn.IsEnabled = false; }
    }

    // ---- mods -------------------------------------------------------------

    private void RefreshMods()
    {
        _mods = ModManager.List();
        ModsList.ItemsSource = _mods.Select(m => $"{(m.Enabled ? "✓" : "•")}   {m.Name}   {m.Version}").ToList();
        ModsHint.Text = _mods.Count == 0
            ? $"No mods yet. Drop a mod folder into:\n{LauncherPaths.ModsDir}"
            : $"Load order is top → bottom.   Folder: {LauncherPaths.ModsDir}";
    }

    private ModInfo? Selected() =>
        ModsList.SelectedIndex >= 0 && ModsList.SelectedIndex < _mods.Count ? _mods[ModsList.SelectedIndex] : null;

    private void OnModEnable(object? s, RoutedEventArgs e)  => ModAction(m => ModManager.SetEnabled(m.Id, true));
    private void OnModDisable(object? s, RoutedEventArgs e) => ModAction(m => ModManager.SetEnabled(m.Id, false));
    private void OnModUp(object? s, RoutedEventArgs e)      => ModAction(m => ModManager.Move(m.Id, -1));
    private void OnModDown(object? s, RoutedEventArgs e)    => ModAction(m => ModManager.Move(m.Id, +1));

    private void ModAction(Action<ModInfo> act)
    {
        var m = Selected();
        if (m == null) return;
        var id = m.Id;
        try { act(m); } catch { /* surfaced by re-list */ }
        RefreshMods();
        var idx = _mods.FindIndex(x => x.Id == id);
        if (idx >= 0) ModsList.SelectedIndex = idx;
    }

    private void OnClose(object? s, RoutedEventArgs e) => Close();
}
