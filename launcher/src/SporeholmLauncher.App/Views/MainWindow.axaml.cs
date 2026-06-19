using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using SporeholmLauncher.Core;

namespace SporeholmLauncher.App.Views;

public partial class MainWindow : Window
{
    private LauncherConfig _cfg = LauncherConfig.Load();
    private UpdateCheck? _latest;
    private bool _busy;
    private bool _selfUpdateChecked;

    private enum Mode { None, Install, Play }
    private Mode _mode = Mode.None;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    // ---- refresh + state machine -----------------------------------------

    private async Task RefreshAsync()
    {
        var installed = InstalledState.Load();
        var source = ReleaseSourceFactory.Create(_cfg);
        SetProgress(false);

        if (_cfg.Offline)
        {
            VersionLine.Text = $"Installed: {InstalledLabel(GameLauncher.InstalledVersion() ?? installed.Version)}    •    Offline";
            ShowNotes("Offline mode is on — open Settings to turn it off. You can still play the installed build.");
            SetMode(GameLauncher.IsInstalled ? Mode.Play : Mode.None);
            return;
        }

        var relLabel = string.IsNullOrEmpty(_cfg.SelectedRelease) ? "latest" : _cfg.SelectedRelease!;
        VersionLine.Text = $"Installed: {installed.Version ?? "(none)"}    •    checking {relLabel}…";

        try
        {
            _latest = await new Updater(source, _cfg.Channel).CheckAsync();

            // Keep the launcher itself current before it manages the game.
            if (!_selfUpdateChecked)
            {
                _selfUpdateChecked = true;
                if (await TrySelfUpdateAsync(source, _latest.Manifest)) return;   // updating → this instance exits
            }

            var shownInstalled = _latest.IsInstalled ? InstalledLabel(_latest.InstalledVersion) : "(none)";
            VersionLine.Text = $"Installed: {shownInstalled}    •    {relLabel}: {_latest.LatestVersion}";
            await LoadNewsAsync(source);

            switch (_latest.State)
            {
                case UpdateState.NotInstalled:
                    SetMode(Mode.Install);
                    break;
                case UpdateState.NoBuildForThisOs:
                    SetMode(Mode.None);
                    ShowNotes($"The selected release ({_latest.LatestVersion}) has no build for {LauncherPaths.CurrentOs}.");
                    break;
                case UpdateState.UpdateAvailable:
                    if (_cfg.AutoUpdate)
                    {
                        await RunUpdateAsync();
                        SetMode(Mode.Play);
                    }
                    else
                    {
                        SetMode(Mode.Play);                       // can play the current build…
                        await PromptUpdateAsync();                // …or be asked to update
                    }
                    break;
                default: // UpToDate
                    SetMode(Mode.Play);
                    break;
            }
        }
        catch (Exception ex)
        {
            VersionLine.Text = $"Installed: {InstalledLabel(GameLauncher.InstalledVersion() ?? installed.Version)}    •    update check failed";
            ShowNotes($"Couldn't reach the update source:\n{ex.Message}\n\nYou can still play the installed build, or open Settings.");
            SetMode(GameLauncher.IsInstalled ? Mode.Play : Mode.None);
        }
    }

    // A build present on disk with no version record (hand-copied, or a lost
    // installed.json) still counts as installed — label it so, don't say "(none)".
    private static string InstalledLabel(string? recordedVersion) =>
        recordedVersion ?? (GameLauncher.IsInstalled ? "(detected build)" : "(none)");

    private void SetMode(Mode m)
    {
        _mode = m;
        MainButton.Content = m switch { Mode.Install => "Install", Mode.Play => "▶  Play", _ => "—" };
        MainButton.IsEnabled = !_busy && m != Mode.None;
    }

    // ---- the one button + the update prompt -------------------------------

    private async void OnMainButton(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_mode == Mode.Install)
        {
            if (await RunUpdateAsync()) await RefreshAsync();   // re-sync version line + news + button
        }
        else if (_mode == Mode.Play)
        {
            try { GameLauncher.Launch(); }
            catch (Exception ex) { await Note($"Couldn't launch the game:\n{ex.Message}"); }
        }
    }

    private async Task PromptUpdateAsync()
    {
        if (_latest?.State != UpdateState.UpdateAvailable) return;
        var install = await Confirm(
            $"An update is available: {_latest.LatestVersion}\n(installed: {_latest.InstalledVersion}).\n\nInstall it now?",
            "Install", "Later");
        if (install)
        {
            if (await RunUpdateAsync()) await RefreshAsync();   // re-sync version line + news + button
        }
    }

    private async Task<bool> RunUpdateAsync()
    {
        SetBusy(true);
        try
        {
            var updater = new Updater(ReleaseSourceFactory.Create(_cfg), _cfg.Channel);
            var check = _latest ?? await updater.CheckAsync();
            if (check.State == UpdateState.UpToDate) return true;
            if (check.State == UpdateState.NoBuildForThisOs) { await Note($"No build for {LauncherPaths.CurrentOs} in this release."); return false; }

            SetProgress(true, 0, "Starting…");
            var progress = new Progress<UpdateProgress>(p => Dispatcher.UIThread.Post(() =>
            {
                StatusText.Text = p.Message;
                Progress.Value = p.Fraction;
            }));
            await updater.ApplyAsync(check.Manifest!, check.File!, progress);
            StatusText.Text = $"Installed {check.LatestVersion}.";
            return true;
        }
        catch (Exception ex)
        {
            await Note($"Install failed:\n{ex.Message}\n\nYour installed build is unchanged.");
            return false;
        }
        finally
        {
            SetProgress(false);
            SetBusy(false);
        }
    }

    // ---- launcher self-update --------------------------------------------

    /// <summary>If the release advertises a newer launcher, update the launcher itself
    /// (download → verify → swap → relaunch). Returns true if it restarted (this instance exits).</summary>
    private async Task<bool> TrySelfUpdateAsync(IReleaseSource source, Manifest? manifest)
    {
        if (manifest == null) return false;
        var su = new LauncherSelfUpdater(source);
        if (!su.Available(manifest, out var latest, out var file)) return false;

        if (!_cfg.AutoUpdate)
        {
            var go = await Confirm(
                $"A new launcher ({latest}) is available — you have {LauncherInfo.Version}.\n\nUpdate the launcher now?",
                "Update", "Later");
            if (!go) return false;
        }

        SetBusy(true);
        try
        {
            SetProgress(true, 0, "Updating launcher…");
            var progress = new Progress<UpdateProgress>(p => Dispatcher.UIThread.Post(() =>
            { StatusText.Text = p.Message; Progress.Value = p.Fraction; }));
            await su.ApplyAsync(file!, progress);
            Environment.Exit(0);   // the new launcher has started — quit this old instance
            return true;
        }
        catch (Exception ex)
        {
            SetProgress(false);
            SetBusy(false);
            await Note($"Couldn't update the launcher automatically:\n{ex.Message}\n\nYou can grab the latest launcher from the releases page.");
            return false;
        }
    }

    // ---- settings ---------------------------------------------------------

    private async void OnSettings(object? sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_cfg);
        await dlg.ShowDialog(this);
        _cfg = LauncherConfig.Load();   // settings may have changed source / release / auto-update
        await RefreshAsync();
    }

    // ---- news + helpers ---------------------------------------------------

    private async Task LoadNewsAsync(IReleaseSource source)
    {
        var fallback = string.IsNullOrWhiteSpace(_latest?.Manifest?.Notes) ? "" : _latest!.Manifest!.Notes!;
        try
        {
            var news = await NewsService.GetAsync(source);
            if (news.Count > 0) { NewsList.ItemsSource = news; NewsList.IsVisible = true; NotesText.IsVisible = false; }
            else ShowNotes(fallback);
        }
        catch { ShowNotes(fallback); }
    }

    private void ShowNotes(string text)
    {
        NewsList.IsVisible = false;
        NotesText.IsVisible = true;
        NotesText.Text = text;
    }

    private void SetProgress(bool visible, double frac = 0, string message = "")
    {
        Progress.IsVisible = visible;
        Progress.Value = frac;
        StatusText.Text = message;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        MainButton.IsEnabled = !busy && _mode != Mode.None;
    }

    private async Task Note(string message) => await Dialog(message, ("OK", true));

    private async Task<bool> Confirm(string message, string yes, string no)
    {
        var r = await Dialog(message, (no, false), (yes, true));
        return r;
    }

    private async Task<bool> Dialog(string message, params (string label, bool result)[] buttons)
    {
        var dlg = new Window
        {
            Title = "Sporeholm",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Background = Brush.Parse("#23262e"),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        bool result = false;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, value) in buttons)
        {
            var b = new Button { Content = label, MinWidth = 84 };
            b.Click += (_, _) => { result = value; dlg.Close(); };
            row.Children.Add(b);
        }
        dlg.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(18),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gainsboro },
                row,
            },
        };
        await dlg.ShowDialog(this);
        return result;
    }
}
