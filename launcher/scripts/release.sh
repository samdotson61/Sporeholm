#!/usr/bin/env bash
#
# Cut a COMPLETE Sporeholm release from this Mac — export → package → sign →
# notarize → publish, one command. Replaces the old two-machine dance
# (Windows exports + package-release.ps1, then Mac finalize) for any release
# cut from a Mac; the Windows path still works and package-macos.sh still
# finalizes it (see RELEASING.md).
#
#   ./release.sh              # dry run: export + package everything into launcher/release-out/
#   ./release.sh --publish    # …and create/update the GitHub release + upload + finalize macOS
#
# What it does:
#   1. Reads the version from project.godot (config/version) — the single source
#      of truth. The tag, manifest, version.txt stamps, and macOS bundle version
#      all derive from it. Syncs export_presets.cfg's macOS version fields to it.
#   2. Headless-exports Windows / Linux / macOS with the pinned Godot .NET editor.
#   3. Zips each build with a version.txt at the zip root (zip -rX: Unix perms
#      preserved — the Linux binary keeps its exec bit — no AppleDouble junk).
#   4. Writes manifest.json (notes = top changelog entry) + a news-feed changelog
#      truncated to the newest entries (the full file stays in the repo).
#   5. With --publish: creates/updates release v<version>, uploads the game zips +
#      manifest + changelog; carries the previous release's launcher binaries
#      forward when the launcher didn't change (a release must be self-contained —
#      the README download links and self-update both point at ITS assets); then
#      hands macOS finalization to package-macos.sh (exec-bit repair, Developer ID
#      signing, notarization + stapling, smoke launch, manifest patch, upload).
#
# Requirements on this Mac (one-time; see RELEASING.md "One-time setup"):
#   • the pinned Godot .NET editor + matching mono export templates
#   • .NET 8 SDK, gh (authed), a Developer ID cert + `sporeholm-notary` profile
#
set -euo pipefail

REPO="samdotson61/Sporeholm"
HERE="$(cd "$(dirname "$0")" && pwd)"
LAUNCHER_DIR="$(dirname "$HERE")"
GAME_REPO="$(dirname "$LAUNCHER_DIR")"
OUT="$LAUNCHER_DIR/release-out"
GODOT="${GODOT:-/Applications/Godot_mono_4.6.3.app/Contents/MacOS/Godot}"
NEWS_ENTRIES="${NEWS_ENTRIES:-10}"
PUBLISH=0
[ "${1:-}" = "--publish" ] && PUBLISH=1

say() { printf '\n\033[1;32m==>\033[0m %s\n' "$*"; }
die() { echo "ERROR: $*" >&2; exit 1; }

# ---- 0. Preflight ---------------------------------------------------------------------
[ "$(uname -s)" = "Darwin" ] || die "run this on a Mac."
[ -x "$GODOT" ] || die "Godot .NET editor not found at $GODOT (set GODOT=… to override)."
command -v gh >/dev/null || die "gh CLI required."
DOTNET="$(command -v dotnet || true)"; [ -n "$DOTNET" ] || DOTNET="$HOME/.dotnet/dotnet"
[ -x "$DOTNET" ] || die "dotnet SDK 8+ required."
export PATH="$(dirname "$DOTNET"):$PATH"

VERSION="$(grep -Eo 'config/version="[^"]+"' "$GAME_REPO/project.godot" | sed -E 's/.*"(.*)"/\1/')"
[ -n "$VERSION" ] || die "config/version not found in project.godot."
TAG="$VERSION"
BARE="${VERSION#v}"
say "Releasing Sporeholm $VERSION"

GODOT_VER="$("$GODOT" --version | grep -Eo '^[0-9]+\.[0-9]+(\.[0-9]+)?')"
TPL_DIR="$HOME/Library/Application Support/Godot/export_templates"
ls "$TPL_DIR" 2>/dev/null | grep -q "^${GODOT_VER}.*mono$" \
  || die "no mono export templates for Godot $GODOT_VER in $TPL_DIR (download the matching _mono_export_templates.tpz)."

# The changelog's top entry feeds the release notes + news feed — warn if it lags.
TOP_ENTRY="$(grep -m1 -E '^## \[' "$GAME_REPO/changelog.md" | sed -E 's/^## \[([^]]+)\].*/\1/')"
[ "v$TOP_ENTRY" = "$VERSION" ] || [ "$TOP_ENTRY" = "$VERSION" ] \
  || echo "WARNING: changelog top entry is [$TOP_ENTRY] but releasing $VERSION — notes will look stale." >&2

# Keep the committed macOS preset's bundle version in lockstep with config/version.
sed -i '' -E "s|^application/short_version=\".*\"|application/short_version=\"$BARE\"|; s|^application/version=\".*\"|application/version=\"$BARE\"|" \
  "$GAME_REPO/export_presets.cfg"

# ---- 1. Export all three platforms ------------------------------------------------------
say "Exporting (Godot $GODOT_VER headless): Windows / Linux / macOS"
cd "$GAME_REPO"
rm -rf exports; mkdir -p exports/win exports/linux exports/mac
"$GODOT" --headless --path . --import >/dev/null 2>&1 || true
"$GODOT" --headless --path . --export-release "Windows Desktop" exports/win/Sporeholm.exe    2>&1 | grep -iE 'error' && die "Windows export failed." || true
"$GODOT" --headless --path . --export-release "Linux"           exports/linux/Sporeholm.x86_64 2>&1 | grep -iE 'error' && die "Linux export failed." || true
"$GODOT" --headless --path . --export-release "macOS"           exports/mac/Sporeholm.app     2>&1 | grep -iE 'error' && die "macOS export failed." || true
[ -f exports/win/Sporeholm.exe ] && [ -f exports/linux/Sporeholm.x86_64 ] && [ -d exports/mac/Sporeholm.app ] \
  || die "an export is missing its output."

# ---- 2. Zip each build (version.txt at the zip root; perms preserved) -------------------
say "Packaging zips"
rm -rf "$OUT"; mkdir -p "$OUT"
chmod +x exports/linux/Sporeholm.x86_64
for os in win linux mac; do printf '%s' "$VERSION" > "exports/$os/version.txt"; done
( cd exports/win   && zip -qrX "$OUT/Sporeholm-windows.zip" . -x '.*' )
( cd exports/linux && zip -qrX "$OUT/Sporeholm-linux.zip"   . -x '.*' )
( cd exports/mac   && zip -qrX "$OUT/Sporeholm-macos.zip"   . -x '.*' )

# ---- 3. Manifest + news-feed changelog ---------------------------------------------------
say "Writing manifest.json + news changelog (top $NEWS_ENTRIES entries)"
PREV_TAG="$(gh release view --repo "$REPO" --json tagName --jq .tagName 2>/dev/null || true)"
python3 - "$GAME_REPO" "$OUT" "$VERSION" "$NEWS_ENTRIES" <<'PY'
import hashlib, json, os, re, sys
from datetime import datetime, timezone
repo, out, version, news_n = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4])

def sha_size(name):
    p = os.path.join(out, name); h = hashlib.sha256()
    with open(p, 'rb') as f:
        for c in iter(lambda: f.read(1 << 20), b''): h.update(c)
    return {'name': name, 'sha256': h.hexdigest(), 'size': os.path.getsize(p)}

md = open(os.path.join(repo, 'changelog.md')).read()
sections = re.split(r'(?m)^(?=## )', md)
entries = [s for s in sections if s.startswith('## ')]
notes = entries[0].strip() if entries else ''
preamble = sections[0] if sections and not sections[0].startswith('## ') else ''
open(os.path.join(out, 'changelog.md'), 'w').write(preamble + ''.join(entries[:news_n]))

manifest = {
    'version': version,
    'channel': 'stable',
    'notes': notes,
    'releasedUtc': datetime.now(timezone.utc).isoformat(),
    'files': {
        'windows': sha_size('Sporeholm-windows.zip'),
        'linux':   sha_size('Sporeholm-linux.zip'),
        'macos':   sha_size('Sporeholm-macos.zip'),   # placeholder — package-macos.sh re-signs + re-patches
    },
}
json.dump(manifest, open(os.path.join(out, 'manifest.json'), 'w'), indent=2)
open(os.path.join(out, 'notes.md'), 'w').write(notes)
print('manifest written for', version)
PY

if [ "$PUBLISH" != 1 ]; then
  say "Dry run complete — release payload in $OUT (re-run with --publish to ship):"
  ls -la "$OUT"
  exit 0
fi

# ---- 4. Create/refresh the GitHub release ------------------------------------------------
say "Publishing GitHub release $TAG"
if gh release view "$TAG" --repo "$REPO" >/dev/null 2>&1; then
  say "Release $TAG exists — updating assets"
else
  gh release create "$TAG" --repo "$REPO" --title "$TAG" --notes-file "$OUT/notes.md"
fi
( cd "$OUT" && gh release upload "$TAG" --repo "$REPO" --clobber \
    Sporeholm-windows.zip Sporeholm-linux.zip Sporeholm-macos.zip manifest.json changelog.md )

# A release must be self-contained: the README's download links and launcher
# self-update read assets from THIS release. If the launcher didn't change,
# carry the previous release's binaries + manifest section forward.
LVER="$(grep -Eo 'Version *= *"[^"]+"' "$LAUNCHER_DIR/src/SporeholmLauncher.Core/LauncherInfo.cs" | sed -E 's/.*"(.*)"/\1/')"
PREV_LVER="" PREV_MANIFEST="$OUT/prev-manifest.json"
if [ -n "$PREV_TAG" ] && [ "$PREV_TAG" != "$TAG" ]; then
  gh release download "$PREV_TAG" --repo "$REPO" --pattern manifest.json --output "$PREV_MANIFEST" --clobber 2>/dev/null || true
  [ -f "$PREV_MANIFEST" ] && PREV_LVER="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("launcher",{}).get("version",""))' "$PREV_MANIFEST")"
fi
if [ -n "$PREV_LVER" ] && [ "$LVER" = "$PREV_LVER" ]; then
  say "Launcher v$LVER unchanged — carrying binaries forward from $PREV_TAG"
  mkdir -p "$OUT/carry"
  for a in SporeholmLauncher.exe SporeholmLauncher-linux SporeholmLauncher-macos.zip; do
    gh release download "$PREV_TAG" --repo "$REPO" --pattern "$a" --output "$OUT/carry/$a" --clobber
  done
  ( cd "$OUT/carry" && gh release upload "$TAG" --repo "$REPO" --clobber \
      SporeholmLauncher.exe SporeholmLauncher-linux SporeholmLauncher-macos.zip )
  python3 - "$OUT/manifest.json" "$PREV_MANIFEST" <<'PY'
import json, sys
m = json.load(open(sys.argv[1])); prev = json.load(open(sys.argv[2]))
if 'launcher' in prev: m['launcher'] = prev['launcher']
json.dump(m, open(sys.argv[1], 'w'), indent=2)
print('launcher manifest section carried forward')
PY
  ( cd "$OUT" && gh release upload "$TAG" --repo "$REPO" --clobber manifest.json )
fi

# ---- 5. Finalize macOS (exec bits, sign, notarize, staple, smoke, patch, upload) ---------
say "Finalizing macOS via package-macos.sh"
"$HERE/package-macos.sh" --tag "$TAG" --game-zip "$OUT/Sporeholm-macos.zip" --upload

say "Release $TAG is live. Verify as a player: download the launcher from the release,"
say "unzip, double-click → Install → Play. (releases/latest CDN can lag a few minutes.)"
