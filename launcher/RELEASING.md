# Releasing Sporeholm

How to cut a release the launcher can install + keep up to date. Two artifacts come
out of this:

1. **The game release** — per‑OS zips + `manifest.json` + `changelog.md`, published to
   GitHub Releases. The launcher reads these to install/update the game.
2. **The launcher binaries** — one self‑contained `.exe` per OS that players download
   and double‑click. These don't change every release; rebuild them when the launcher
   itself changes.

> Everything below runs from the repo root `C:\Claude\Cloud\Sporeholm`. The launcher
> lives in `launcher/` and is its own .NET solution.

---

## How version parity works (why this matters)

- The game's version lives in **one place**: `project.godot` → `config/version` (e.g. `v0.8.9`).
- `package-release.ps1` reads that, writes it into `manifest.json` (**latest** version), and
  **stamps a `version.txt` into each build zip** (the **installed** version).
- On install the launcher extracts `version.txt` into `…/game/`, so the installed build
  *declares its own version*. The launcher compares **installed `version.txt`** vs
  **latest release tag** → correct "Up to date" / "Update available", even if its own
  `installed.json` record is missing. **Always bump `config/version` before packaging** —
  that single edit drives both sides of the comparison.

---

## One‑time setup

- **Export templates** — in Godot: *Editor ▸ Manage Export Templates ▸ Download and Install*
  (matching the editor version). Needed once per machine, and for each OS you want to ship.
- **Publishing** — either:
  - `gh auth login` (so `package-release.ps1 -Publish` can create the release), **or**
  - nothing — publish manually through GitHub Desktop / the web UI (steps below).
  `gh` is installed here but **not logged in**, so the manual path is the default for now.

---

## Step by step

### 1. Bump the version
Edit `project.godot` → `config/version="vX.Y.Z"`, update `changelog.md` (top `## [X.Y.Z] — date — title`
section feeds the launcher's news feed), and the in‑game menu text. Commit.

### 2. Export the game from Godot
*Project ▸ Export*. For each platform, export into its **own empty folder**:

| Preset (already in `export_presets.cfg`) | Export to |
|---|---|
| **Windows Desktop** | `C:\exports\win\Sporeholm.exe` (folder `C:\exports\win`) |
| **Linux** | `C:\exports\linux\Sporeholm.x86_64` |
| **macOS** | `C:\exports\mac\Sporeholm.app` |

Windows‑only is a fine first release; the launcher just reports "no build for this OS" on
platforms you didn't ship. (Linux/macOS need their export templates installed first.)

### 3. Package the game release
```powershell
# version + notes are read from project.godot + changelog.md automatically
.\launcher\scripts\package-release.ps1 -WindowsBuild C:\exports\win
#   add -LinuxBuild C:\exports\linux -MacBuild C:\exports\mac for those platforms
```
Produces `launcher\release\`:
```
Sporeholm-windows.zip     # the build + a stamped version.txt
manifest.json             # version, per-OS sha256 + size
changelog.md              # copied from the game (news feed source)
```

### 4. Build the launcher binaries (only when the launcher changed)
```powershell
.\launcher\scripts\build-launcher.ps1 -Rids win-x64
#   omit -Rids to build win-x64, linux-x64, osx-x64, osx-arm64
```
Output: `launcher\dist\<rid>\SporeholmLauncher[.exe]` — the self‑contained file players
download (no .NET needed). If uploading more than one OS, rename them so they don't
collide as release assets, e.g. `SporeholmLauncher-windows.exe`, `SporeholmLauncher-linux`,
`SporeholmLauncher-macos`.

### 5. Publish to GitHub Releases

**Option A — automated (after `gh auth login`):**
```powershell
.\launcher\scripts\package-release.ps1 -WindowsBuild C:\exports\win -Publish
```
Creates/updates release **`vX.Y.Z`** and uploads the game zip(s) + `manifest.json` +
`changelog.md`. Then attach the launcher binary from step 4:
```powershell
gh release upload vX.Y.Z --repo samdotson61/Sporeholm `
    .\launcher\dist\win-x64\SporeholmLauncher.exe --clobber
```

**Option B — manual (GitHub Desktop / web):**
1. On github.com/samdotson61/Sporeholm → **Releases ▸ Draft a new release**.
2. **Tag**: `vX.Y.Z` (must match `config/version`). Title: same. Paste the changelog section as notes.
3. **Attach** every file from `launcher\release\` (the zip(s), `manifest.json`, `changelog.md`)
   **and** the launcher `.exe` from `launcher\dist\win-x64\`.
4. Publish.

> The tag **is** the latest version the launcher detects (`releases/latest` → `tag_name`),
> so the tag must equal `config/version`.

### 6. Verify
```powershell
# point a throwaway data dir at the live release and check
$env:SPOREHOLM_LAUNCHER_DATA = "$env:TEMP\sporeholm-verify"
.\launcher\src\SporeholmLauncher.Cli\bin\Debug\net8.0\sporeholm-launcher-cli.exe status
#   Latest : vX.Y.Z  (NotInstalled)   ← detected from the release tag
```
Then double‑click the launcher: **news → Install → Play**. Re‑open it after installing — it
should read **Installed: vX.Y.Z** (from the stamped `version.txt`) and show **Up to date**.

---

## Players: how they get it

1. Download `SporeholmLauncher.exe` (from the release assets, or wherever you host the launcher).
2. Double‑click → it opens to the news screen, **Install**s the latest game build, then **Play**s.
3. On later launches it checks the release tag and offers/installs updates (auto‑update is on
   by default; toggle in ⚙ Settings, which also lets them pin a specific release).

Game **saves** (Godot `user://`) live outside the launcher's folders and are never touched by
updates or rollback.
