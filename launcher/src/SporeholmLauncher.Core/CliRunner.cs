namespace SporeholmLauncher.Core;

/// <summary>Headless command dispatch shared by the console exe and the GUI's
/// CLI mode (the GUI runs this when started with arguments). Every launcher
/// capability is reachable here, which also makes the engine fully testable
/// without a display.</summary>
public static class CliRunner
{
    public static async Task<int> RunAsync(string[] args, TextWriter @out, CancellationToken ct = default)
    {
        var verb = (args.Length > 0 ? args[0] : "help").ToLowerInvariant();
        var cfg = LauncherConfig.Load();
        try
        {
            return verb switch
            {
                "status"   => await Status(cfg, @out, ct),
                "check"    => await Check(cfg, @out, ct),
                "update"   => await Update(cfg, @out, ct),
                "self-update" => await SelfUpdate(cfg, @out, ct),
                "play"     => await Play(cfg, @out, ct),
                "news"     => await News(cfg, @out, ct),
                "releases" => await Releases(cfg, @out, ct),
                "rollback" => Rollback(@out),
                "mods"     => Mods(args, @out),
                "config"   => ConfigCmd(args, cfg, @out),
                "sha256"   => await Sha256(args, @out, ct),
                "help" or "--help" or "-h" => Help(@out),
                _ => Unknown(verb, @out),
            };
        }
        catch (Exception ex)
        {
            @out.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> Status(LauncherConfig cfg, TextWriter o, CancellationToken ct)
    {
        var installed = InstalledState.Load();
        var detected = GameLauncher.IsInstalled;
        var installedLabel = (GameLauncher.InstalledVersion() ?? installed.Version)
                             ?? (detected ? "(detected build — no version record)" : "(none)");
        o.WriteLine($"Installed : {installedLabel}");
        o.WriteLine($"Launcher  : v{LauncherInfo.Version}");
        o.WriteLine($"Game dir  : {LauncherPaths.InstallDir}{(detected ? "  [build present]" : "  [no build]")}");
        o.WriteLine($"Data dir  : {LauncherPaths.DataDir}{(LauncherPaths.IsPortable ? "  [portable]" : "")}");
        o.WriteLine($"OS        : {LauncherPaths.CurrentOs}");
        o.WriteLine($"Source    : {ReleaseSourceFactory.Create(cfg).Describe()}{(cfg.Offline ? "  [OFFLINE]" : "")}");
        o.WriteLine($"Rollback  : {(Directory.Exists(LauncherPaths.PreviousDir) ? $"available → {installed.PreviousVersion}" : "none")}");
        if (cfg.Offline) return 0;
        try
        {
            var check = await new Updater(ReleaseSourceFactory.Create(cfg), cfg.Channel).CheckAsync(ct);
            o.WriteLine($"Latest    : {check.LatestVersion}  ({check.State})");
            if (check.Manifest?.Launcher is { } lm && !string.IsNullOrEmpty(lm.Version)
                && SemVer.Parse(lm.Version) > SemVer.Parse(LauncherInfo.Version))
                o.WriteLine($"Launcher  : update available → {lm.Version}  (run 'self-update')");
        }
        catch (Exception ex) { o.WriteLine($"Latest    : (could not reach source: {ex.Message})"); }
        return 0;
    }

    private static async Task<int> Check(LauncherConfig cfg, TextWriter o, CancellationToken ct)
    {
        if (cfg.Offline) { o.WriteLine("Offline mode — not checking for updates."); return 0; }
        var check = await new Updater(ReleaseSourceFactory.Create(cfg), cfg.Channel).CheckAsync(ct);
        o.WriteLine($"Installed : {(check.IsInstalled ? check.InstalledVersion ?? "(detected build)" : "(none)")}");
        o.WriteLine($"Latest    : {check.LatestVersion}");
        o.WriteLine($"Status    : {check.State}");
        if (check.State == UpdateState.NoBuildForThisOs)
            o.WriteLine($"  (the latest release has no '{LauncherPaths.CurrentOs}' build)");
        return check.State == UpdateState.UpdateAvailable ? 10 : 0;   // exit 10 = update available (scriptable)
    }

    private static async Task<int> Update(LauncherConfig cfg, TextWriter o, CancellationToken ct)
    {
        if (cfg.Offline) { o.WriteLine("Offline mode — not updating."); return 0; }
        var updater = new Updater(ReleaseSourceFactory.Create(cfg), cfg.Channel);
        var check = await updater.CheckAsync(ct);
        if (check.State == UpdateState.NoBuildForThisOs)
        {
            o.WriteLine($"No '{LauncherPaths.CurrentOs}' build in the latest release ({check.LatestVersion}).");
            return 2;
        }
        if (check.State == UpdateState.UpToDate)
        {
            o.WriteLine($"Already up to date ({check.InstalledVersion ?? "detected build"}).");
            return 0;
        }
        o.WriteLine($"Updating {check.InstalledVersion ?? "(none)"} → {check.LatestVersion}…");
        var progress = new Progress<UpdateProgress>(p => o.WriteLine($"  [{p.Phase}] {p.Message}"));
        await updater.ApplyAsync(check.Manifest!, check.File!, progress, ct);
        o.WriteLine($"Done. Installed {check.LatestVersion}.");
        return 0;
    }

    private static async Task<int> SelfUpdate(LauncherConfig cfg, TextWriter o, CancellationToken ct)
    {
        if (cfg.Offline) { o.WriteLine("Offline mode — not checking for a launcher update."); return 0; }
        var source = ReleaseSourceFactory.Create(cfg);
        Manifest manifest;
        try { manifest = await source.GetManifestAsync(cfg.Channel, ct); }
        catch (Exception ex) { o.WriteLine($"Could not reach the source: {ex.Message}"); return 1; }

        var su = new LauncherSelfUpdater(source);
        if (!su.Available(manifest, out var latest, out var file))
        {
            o.WriteLine($"Launcher is up to date (v{LauncherInfo.Version}).");
            return 0;
        }
        o.WriteLine($"Updating launcher v{LauncherInfo.Version} → {latest}…");
        var progress = new Progress<UpdateProgress>(p => o.WriteLine($"  [{p.Phase}] {p.Message}"));
        await su.ApplyAsync(file!, progress, ct);
        o.WriteLine("Launcher updated — the new version has been started.");
        return 0;
    }

    private static async Task<int> Play(LauncherConfig cfg, TextWriter o, CancellationToken ct)
    {
        if (!cfg.Offline && cfg.AutoUpdate)
        {
            try { await Update(cfg, o, ct); }
            catch (Exception ex) { o.WriteLine($"(update skipped: {ex.Message})"); }
        }
        if (!GameLauncher.IsInstalled)
        {
            o.WriteLine("No game build is installed yet. Run 'update' (online) or install a build first.");
            return 2;
        }
        o.WriteLine($"Launching {GameLauncher.InstalledVersion() ?? InstalledState.Load().Version ?? "the installed build"}…");
        GameLauncher.Launch();
        return 0;
    }

    private static async Task<int> News(LauncherConfig cfg, TextWriter o, CancellationToken ct)
    {
        if (cfg.Offline) { o.WriteLine("Offline mode — news is unavailable."); return 0; }
        var entries = await NewsService.GetAsync(ReleaseSourceFactory.Create(cfg), ct);
        if (entries.Count == 0) { o.WriteLine("No news available (the source has no changelog.md)."); return 0; }
        foreach (var e in entries.Take(8))
        {
            var head = $"── {e.Version}" + (e.Date is { Length: > 0 } ? $"  ({e.Date})" : "") + (e.Title is { Length: > 0 } ? $"  {e.Title}" : "");
            o.WriteLine(head);
            if (!string.IsNullOrWhiteSpace(e.Body)) o.WriteLine(e.Body);
            o.WriteLine();
        }
        return 0;
    }

    private static async Task<int> Releases(LauncherConfig cfg, TextWriter o, CancellationToken ct)
    {
        if (cfg.Offline) { o.WriteLine("Offline mode — release list unavailable."); return 0; }
        var releases = await ReleaseSourceFactory.Create(cfg).ListReleasesAsync(ct);
        if (releases.Count == 0) { o.WriteLine("No releases listed (non-GitHub source, or none uploaded yet)."); return 0; }
        var selected = string.IsNullOrEmpty(cfg.SelectedRelease) ? "latest" : cfg.SelectedRelease;
        o.WriteLine($"Selected: {selected}   (set with: config set release <tag|latest>)");
        foreach (var r in releases)
            o.WriteLine($"  {(r.Tag == cfg.SelectedRelease ? "*" : " ")} {r.Tag,-14}{(r.PreRelease ? "[pre] " : "      ")}{r.Name}");
        return 0;
    }

    private static int Rollback(TextWriter o)
    {
        var updater = new Updater(ReleaseSourceFactory.Create(LauncherConfig.Load()));
        if (!updater.CanRollback()) { o.WriteLine("Nothing to roll back to."); return 2; }
        var before = InstalledState.Load().Version;
        updater.Rollback();
        o.WriteLine($"Rolled back {before} → {InstalledState.Load().Version}.");
        return 0;
    }

    private static int Mods(string[] args, TextWriter o)
    {
        var sub = (args.Length > 1 ? args[1] : "list").ToLowerInvariant();
        switch (sub)
        {
            case "list":
                var mods = ModManager.List();
                if (mods.Count == 0) { o.WriteLine($"No mods. Drop a mod folder into {LauncherPaths.ModsDir}"); return 0; }
                foreach (var m in mods)
                    o.WriteLine($"  [{(m.Enabled ? "x" : " ")}] {m.Id,-24} {m.Version,-8} {m.Name}");
                return 0;
            case "enable":  ModManager.SetEnabled(Arg(args, 2), true);  o.WriteLine($"Enabled {Arg(args, 2)}.");  return 0;
            case "disable": ModManager.SetEnabled(Arg(args, 2), false); o.WriteLine($"Disabled {Arg(args, 2)}."); return 0;
            case "up":      ModManager.Move(Arg(args, 2), -1); o.WriteLine($"Moved {Arg(args, 2)} up.");   return 0;
            case "down":    ModManager.Move(Arg(args, 2), +1); o.WriteLine($"Moved {Arg(args, 2)} down."); return 0;
            default: o.WriteLine("usage: mods [list|enable <id>|disable <id>|up <id>|down <id>]"); return 1;
        }
    }

    private static int ConfigCmd(string[] args, LauncherConfig cfg, TextWriter o)
    {
        var sub = (args.Length > 1 ? args[1] : "show").ToLowerInvariant();
        if (sub == "show") { o.WriteLine(LauncherJson.Serialize(cfg)); o.WriteLine($"(file: {LauncherPaths.ConfigFile})"); return 0; }
        if (sub == "set")
        {
            var key = Arg(args, 2).ToLowerInvariant();
            var val = Arg(args, 3);
            switch (key)
            {
                case "source":     cfg.SourceKind = Enum.Parse<ReleaseSourceKind>(val, ignoreCase: true); break;
                case "owner":      cfg.GitHubOwner = val; break;
                case "repo":       cfg.GitHubRepo = val; break;
                case "baseurl":    cfg.BaseUrl = val; break;
                case "folderpath": cfg.FolderPath = val; break;
                case "channel":    cfg.Channel = val; break;
                case "release":    cfg.SelectedRelease = val.Equals("latest", StringComparison.OrdinalIgnoreCase) ? null : val; break;
                case "autoupdate": cfg.AutoUpdate = bool.Parse(val); break;
                case "offline":    cfg.Offline = bool.Parse(val); break;
                default: o.WriteLine($"unknown config key '{key}'"); return 1;
            }
            cfg.Save();
            o.WriteLine($"Set {key} = {val}.");
            return 0;
        }
        o.WriteLine("usage: config [show|set <key> <value>]");
        return 1;
    }

    private static async Task<int> Sha256(string[] args, TextWriter o, CancellationToken ct)
    {
        var path = Arg(args, 1);
        if (!File.Exists(path)) { o.WriteLine($"no such file: {path}"); return 1; }
        o.WriteLine(await Updater.ComputeSha256Async(path, ct));
        return 0;
    }

    private static int Help(TextWriter o)
    {
        o.WriteLine(
@"Sporeholm launcher (CLI)

  status              show installed + latest version and the update source
  check               check for an update (exit 10 if one is available)
  update              download + verify + install the latest build (keeps a rollback)
  self-update         update the launcher itself to the latest published version
  play                update (if auto-update on) then launch the game
  news                show the changelog / news feed from the update source
  releases            list the GitHub releases you can install (* = selected)
  rollback            revert to the previously-installed build
  mods list           list installed mods + load order
  mods enable <id>    enable a mod
  mods disable <id>   disable a mod
  mods up|down <id>   reorder a mod in the load order
  config show         print the launcher config
  config set <k> <v>  set a config key (source|owner|repo|baseUrl|folderPath|channel|autoUpdate|offline)
  sha256 <file>       print a file's SHA-256 (used by the packaging script)

  Run with no arguments to open the graphical launcher.");
        return 0;
    }

    private static int Unknown(string verb, TextWriter o) { o.WriteLine($"unknown command '{verb}'. Try 'help'."); return 1; }

    private static string Arg(string[] args, int i) =>
        i < args.Length ? args[i] : throw new ArgumentException("missing argument");
}
