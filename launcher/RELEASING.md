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
*Project ▸ Export*, or headless (what the published v0.8.9 used):
```powershell
$g = "<…>\Godot_v4.6.2-stable_mono_win64.exe"
& $g --headless --path . --export-release "Windows Desktop" C:\exports\win\Sporeholm.exe
& $g --headless --path . --export-release "Linux"           C:\exports\linux\Sporeholm.x86_64
& $g --headless --path . --export-release "macOS"           C:\exports\mac\Sporeholm.app
```

| Preset | Output |
|---|---|
| **Windows Desktop** | folder `C:\exports\win` (Sporeholm.exe + .pck + `data_…\Sporeholm.dll`) |
| **Linux** | folder `C:\exports\linux` (Sporeholm.x86_64 + .pck + data) |
| **macOS** | folder `C:\exports\mac` containing `Sporeholm.app` |

**Gotchas (all learned the hard way):**
- **The C# game needs `Sporeholm.sln`** (committed). Without it the export "succeeds" but ships no game code.
- **`export_presets.cfg` is git‑ignored** — the Windows/Linux/macOS presets live only on the build machine. Keep them configured there.
- **macOS must be `universal`** (no x86_64‑only macOS template exists), and universal **requires `Import ETC2 ASTC`** (now on in `project.godot`; Windows/Linux presets keep `etc2_astc` off so their `.pck` stays lean). The preset uses `distribution_type=0` (Testing) so it exports **unsigned** — runnable, but macOS Gatekeeper warns until code‑signed (deferred). The launcher restores the exec bit + clears quarantine on launch.
- **Set the macOS preset's `application/short_version` + `application/version` to the game version** when bumping — v0.8.9 shipped stamped "1.0". (Safety net: `package-macos.sh` re-syncs the .app's Info.plist from the zip's `version.txt`, so a stale preset self-heals at finalize time.)

Any platform you skip just reports "no build for this OS" in the launcher.

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
download (no .NET needed). Release asset names (what the manifest + README links expect):
`SporeholmLauncher.exe` (win-x64), `SporeholmLauncher-linux` (linux-x64), and
`SporeholmLauncher-macos.zip` — the macOS one is **not** a renamed binary; it's the signed
`.app` zip that `package-macos.sh` builds in step 5.5 (which also rebuilds the other two
when the launcher version bumps, so self‑update stays coherent across OSes).

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

### 5.5 Finalize macOS **on a Mac** (required — every release that ships macOS)

Windows cannot produce runnable macOS artifacts. Three separate reasons, all shipped
broken in v0.8.9 and found by actually launching on a Mac:

1. **Zips made on Windows lose the Unix exec bits** (extractors ignore .NET's permission
   stamps because the entries read as MS-DOS-made) → the `.app` fails to spawn at all.
2. **Unsigned binaries are SIGKILLed on Apple Silicon** even with exec bits restored.
3. **`dotnet publish` single-file on macOS never embeds the native dylibs** — they must be
   copied into the `.app` beside the binaries or the launcher GUI crashes on every Mac.

One command on the Mac fixes all three, smoke-launches the result, and republishes:

```bash
launcher/scripts/package-macos.sh --upload          # latest release; --tag vX.Y.Z to pin
```

It repairs the game zip (exec bits + plist version + re-sign), rebuilds the launcher for
all four RIDs from `LauncherInfo.cs`'s version, assembles/signs the universal `.app` with
its dylibs, **launches the freshly-zipped launcher once** (aborts the upload if it doesn't
open — "it built" is not "it opens"), then patches `manifest.json` (game macOS entry + the
whole `launcher` section) and re-uploads. Uses a Developer ID cert if the keychain has
one, otherwise ad-hoc (runnable; see Code signing below for notarization).

### 6. Verify
```powershell
# point a throwaway data dir at the live release and check
$env:SPOREHOLM_LAUNCHER_DATA = "$env:TEMP\sporeholm-verify"
.\launcher\src\SporeholmLauncher.Cli\bin\Debug\net8.0\sporeholm-launcher-cli.exe status
#   Latest : vX.Y.Z  (NotInstalled)   ← detected from the release tag
```
Then double‑click the launcher: **news → Install → Play**. Re‑open it after installing — it
should read **Installed: vX.Y.Z** (from the stamped `version.txt`) and show **Up to date**.

**On the Mac** (non‑negotiable since v0.8.9 shipped a launcher that had never been launched
on one): after `package-macos.sh --upload`, download `SporeholmLauncher-macos.zip` from the
release **as a player would**, unzip, double‑click, and take it through **Install → Play**
once. `package-macos.sh` already smoke-launches, but this end‑to‑end pass also proves the
uploaded manifest checksums match what players actually download.

---

## Code signing (removes the OS "unknown developer" warnings)

Builds run unsigned, but each OS warns on first launch until signed.

- **macOS** — two tiers:
  - **Ad-hoc (current)** — `package-macos.sh` (step 5.5) ad-hoc signs when no Developer ID
    cert is in the keychain. The apps *run*, but Gatekeeper shows a one-time "unverified
    developer" prompt on browser-downloaded copies (right-click ▸ Open, or System Settings ▸
    Privacy & Security ▸ Open Anyway on newer macOS).
  - **Developer ID + notarization (removes the prompt)** — get an Apple Developer cert, then
    run [`scripts/sign-macos.sh`](scripts/sign-macos.sh) **on a Mac**. One run signs **both**
    the game (`Sporeholm.app`) and the launcher (`SporeholmLauncher.app`): hardened runtime +
    .NET JIT entitlements → notarize → staple → re‑zip (keeping `version.txt` / bundle perms)
    → patch **both** macOS checksums in `manifest.json` → re‑upload. After that Gatekeeper
    opens both silently. One‑time setup: `xcrun notarytool store-credentials sporeholm-notary …`
    (see the script header).
- **Windows** — needs a separate code‑signing certificate (Apple's program does **not** cover
  Windows). Options: **Azure Trusted Signing** (~$10/mo, cloud, no token), an **OV** cert
  (~$200–350/yr, now on a USB/HSM token), or **EV** (instant SmartScreen trust, ~$300–500/yr,
  token). Self‑signing does **not** clear SmartScreen. Once you have one, sign with
  `signtool sign /fd SHA256 /tr <rfc3161-url> /td SHA256 …` the launcher `.exe` + the game
  `Sporeholm.exe`, then re‑package/re‑upload. Until then the `.exe` still runs — SmartScreen
  shows "More info → Run anyway."

---

## Players: how they get it

1. Download `SporeholmLauncher.exe` (from the release assets, or wherever you host the launcher).
2. Double‑click → it opens to the news screen, **Install**s the latest game build, then **Play**s.
3. On later launches it checks the release tag and offers/installs updates (auto‑update is on
   by default; toggle in ⚙ Settings, which also lets them pin a specific release).

Game **saves** (Godot `user://`) live outside the launcher's folders and are never touched by
updates or rollback.
