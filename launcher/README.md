# Sporeholm Launcher

A small, cross-platform launcher that installs **Sporeholm**, keeps it up to date
on its own, and starts it — no game engine, no setup. It also owns the `mods/`
layout so player-made mods have a stable home. This is the "Phase 8.5 — Launcher"
milestone from the game roadmap.

> Lives in the game repo at `Sporeholm/launcher/` but is its own .NET 8 solution —
> it's kept out of the Godot build by a `.gdignore` (editor) and a `<Compile Remove>`
> in `Sporeholm.csproj` (`dotnet build`). The update/install engine is a pure-BCL
> library with no third-party dependencies; only the GUI uses Avalonia. Runs on
> Windows, Linux, and macOS.

---

## What the player gets

- **Download → double-click → play.** The player downloads just the launcher (from
  GitHub now, Steam later) and double-clicks it. It opens to a screen with the news
  feed; a progress bar runs along the bottom and one button sits bottom-right: it
  reads **Install** the first time (downloads + installs the game), then becomes
  **Play**. After install, if a newer release is found it asks whether to install it
  (unless auto-update is on, in which case it just does).
- **News / changelog feed.** The launcher fetches the game's `changelog.md` from the
  release and shows a scrollable, version-by-version news feed as the main screen.
- **Settings.** A settings page (⚙) with an **auto-update** checkbox, a dropdown to
  **pick any uploaded GitHub release** (Latest, or pin an older version), offline
  mode, rollback, and mod management.
- **Safe by construction.** Every update keeps the *previous* build for one-click
  **Rollback**. Updates only ever replace the launcher's own install folder — your
  **save files are never touched**. A bad/corrupt download fails the checksum and
  is discarded, leaving the working build in place. An **Offline** toggle plays the
  installed build with no network.
- **Mod-ready.** A `mods/` folder with per-mod `mod.json` + a launcher-managed load
  order. (The in-game modding API is a later phase; the launcher owns the layout now
  so it never has to be reorganised once mods arrive.)

---

## Layout

```
SporeholmLauncher/
├── SporeholmLauncher.sln
├── src/
│   ├── SporeholmLauncher.Core/   # the engine — pure .NET BCL, no NuGet deps
│   ├── SporeholmLauncher.Cli/    # headless: status/check/update/play/rollback/mods/config
│   └── SporeholmLauncher.App/    # Avalonia GUI (the double-click launcher)
├── scripts/
│   └── package-release.ps1       # producer side: zip + manifest + GitHub publish
└── samples/manifest.json         # reference manifest
```

---

## Build & run (development)

```bash
# the graphical launcher
dotnet run --project src/SporeholmLauncher.App

# the headless launcher (same engine)
dotnet run --project src/SporeholmLauncher.Cli -- status
dotnet run --project src/SporeholmLauncher.Cli -- help
```

## Build the launcher players download (one self-contained file per OS)

No .NET install needed on the player's machine — the runtime is bundled. One command
builds all desktop OSes (win-x64, linux-x64, osx-x64, osx-arm64) into `dist/<rid>/`:

```bash
./scripts/build-launcher.ps1
# or a subset:  ./scripts/build-launcher.ps1 -Rids win-x64,linux-x64
```

That single self-contained file **is** the download — the player runs it, it opens to
the news screen, installs the game on first run, and keeps it updated. (Under the hood
the publish is `dotnet publish src/SporeholmLauncher.App -c Release -r <rid>
--self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`.)

> The launcher installs the game into a per-user data folder (below). It also supports
> a **co-located** mode for a future Steam/itch depot: drop a `portable.txt` next to
> the launcher and it keeps the game + updates in its own folder instead.

---

## Configuration (`launcher.json`)

Lives in the per-user data folder (below). All three update sources are supported;
just edit the file or use `config set`:

| Key          | Meaning                                                              |
|--------------|---------------------------------------------------------------------|
| `sourceKind` | `github` (default) · `url` · `folder`                               |
| `gitHubOwner`/`gitHubRepo` | for `github` — defaults to `samdotson61` / `Sporeholm` |
| `baseUrl`    | for `url` — static host holding `manifest.json` + zips              |
| `folderPath` | for `folder` — a local/network folder (great for testing + LAN)    |
| `channel`    | `stable` (→ `manifest.json`) or another channel (→ `manifest-<c>.json`) |
| `selectedRelease` | which GitHub release to install — `null`/`latest` tracks newest, or a tag (e.g. `v0.8.9`) pins it. The Settings dropdown sets this. |
| `autoUpdate` | install a newer release on open without asking (default `true`); when off, the launcher asks |
| `offline`    | never touch the network (default `false`)                          |

**Portable mode:** drop a `portable.txt` file next to the launcher executable and it
keeps the game, updates, mods, and config in its own folder instead of the per-user
data dir. (The bundle ships this marker; `SPOREHOLM_LAUNCHER_DATA` still overrides everything.)

```bash
dotnet run --project src/SporeholmLauncher.Cli -- config set source folder
dotnet run --project src/SporeholmLauncher.Cli -- config set folderPath /path/to/release
```

**Portable / relocatable mode:** set the env var `SPOREHOLM_LAUNCHER_DATA` to put
all launcher state (install, mods, config, downloads) under a folder you choose.

### Where files live

| Path | What |
|------|------|
| `<data>/game/` | the installed build |
| `<data>/game.previous/` | retained for one-click rollback |
| `<data>/mods/` | mods + `load-order.json` |
| `<data>/downloads/` | verified download cache |
| `<data>/launcher.json`, `installed.json` | config + state |

`<data>` = `%APPDATA%\Sporeholm` (Windows) · `~/Library/Application Support/Sporeholm`
(macOS) · `$XDG_DATA_HOME|~/.local/share/Sporeholm` (Linux). The game's own **saves**
(Godot's `user://`) live elsewhere and are never touched by the launcher.

---

## Publishing a game release (producer side)

1. **Export the game from Godot** (`Project ▸ Export`) into a folder per OS.
   > The game has **Windows / Linux / macOS** export presets (in `export_presets.cfg`).
   > The Linux + macOS ones were added pre-wired — open Godot's Export dialog once to
   > install the matching export templates and let Godot finalise them (set the macOS
   > bundle id, etc.). Any OS you don't export simply reports "no build for this OS"
   > in the launcher until its zip is published.
2. **Package + publish** with the script (uses `gh`):

   ```powershell
   # version + "what's new" are read from the game's project.godot + changelog.md
   ./scripts/package-release.ps1 -WindowsBuild C:\exports\sporeholm-win -Publish
   ```

   Pass `-LinuxBuild` / `-MacBuild` too for those platforms. It zips each build to
   `Sporeholm-<os>.zip`, computes SHA-256, writes `manifest.json`, **copies the game's
   `changelog.md`** (this feeds the launcher's news feed), and (with `-Publish`)
   creates/updates the GitHub release `v<version>` and uploads the zips + manifest +
   changelog. The launcher's default `github` source detects the **latest version from
   the repo's latest release tag** (via the public GitHub API), then pins its download to
   that tag's `manifest.json` + zips — so the new build reaches players on their next
   check, and "latest" always reflects what GitHub actually published (not a self-reported
   manifest field). If the API can't be reached it falls back to the
   `releases/latest/download/` redirect and the manifest's own `version`.

   Local/no-hosting test instead of publishing: drop `-Publish` and point a
   `folder` source at the output `release/` directory.

### Manifest format (`samples/manifest.json`)

```jsonc
{
  "version": "v0.8.9",
  "channel": "stable",
  "notes": "What's new in this build…",
  "releasedUtc": "2026-06-16T00:00:00Z",
  "files": {
    "windows": { "name": "Sporeholm-windows.zip", "sha256": "<hex>", "size": 123456 },
    "linux":   { "name": "Sporeholm-linux.zip",   "sha256": "<hex>", "size": 123456 },
    "macos":   { "name": "Sporeholm-macos.zip",   "sha256": "<hex>", "size": 123456 }
  }
}
```

Each file may also carry an absolute `"url"` to override where that asset is fetched.

---

## Mods

A mod is a self-describing folder under `mods/`:

```
mods/
├── load-order.json          # written by the launcher (order + enabled flags)
└── my-cool-mod/
    └── mod.json             # { "name": "...", "version": "...", "author": "...", "description": "..." }
```

The launcher lists, enables/disables, and reorders mods; the game reads
`load-order.json` at startup (game-side loading is a later phase). Manage from the
GUI's **Mods** panel or the CLI: `mods list | enable <id> | disable <id> | up <id> | down <id>`.

---

## CLI reference

```
status              installed + latest version and the update source
check               check for an update (exit code 10 = update available)
news                show the changelog / news feed from the update source
releases            list the GitHub releases you can install (* = selected)
update              download + verify + install the selected release (keeps a rollback)
play                update (if auto-update) then launch the game
rollback            revert to the previously-installed build
mods list|enable|disable|up|down [<id>]
config show|set <key> <value>
sha256 <file>       print a file's SHA-256
```

---

## Status

**Working + tested:** the full update engine (check → download → SHA-256 verify →
backup → install → rollback, with a corrupt-download safe-fail), the changelog/news
feed, game-launch discovery per OS, the mod layout, all three release sources, the
CLI, and the Avalonia GUI. Self-contained single-file builds verified for all three
desktop OSes (Windows/Linux/macOS). Covered by an end-to-end run (local-folder
release) and a producer→consumer run (`package-release.ps1` → install → news).

**Before public distribution:**
- **Install the Linux/macOS export templates** in Godot and finalise the (pre-wired)
  Linux + macOS export presets — set the macOS bundle id, then export those builds.
- **Code-signing / notarization** of the published launcher binaries (so OSes don't
  warn on first run).
- A launcher **app icon**.
- First real GitHub release published by `package-release.ps1 -Publish`.
