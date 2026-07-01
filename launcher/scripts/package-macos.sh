#!/usr/bin/env bash
#
# Finalize the macOS release artifacts — RUN THIS ON A MAC. It is the step that makes
# them actually runnable; a release whose macOS zips came straight off the Windows box
# ships broken in three independent ways (all found the hard way on v0.8.9):
#
#   1. Zips made on Windows carry no Unix exec bits → the .app fails to spawn at all.
#   2. Unsigned binaries → Apple Silicon SIGKILLs them even with exec bits restored.
#   3. `dotnet publish` single-file on macOS NEVER embeds native dylibs (by design) —
#      libSkiaSharp / libHarfBuzzSharp / libAvaloniaNative must be shipped INSIDE the
#      .app next to the binaries, or the GUI crashes at render init on every Mac.
#
# What it does, in order:
#   • GAME    — take Sporeholm-macos.zip (from the release by default, or --game-zip),
#               restore exec bits, sync Info.plist's version to the zip's version.txt,
#               re-sign (Developer ID if present, else ad-hoc), re-zip with perms.
#   • LAUNCHER — dotnet publish all four RIDs with the version from LauncherInfo.cs,
#               assemble the universal .app (arch trampoline + BOTH binaries + the
#               dylibs + icon), sign, zip with perms. Win/Linux binaries are built too
#               so a launcher version bump never strands their self-update.
#   • SMOKE   — actually LAUNCH the freshly-zipped launcher .app once (the v0.8.9
#               lesson: "it built" ≠ "it opens"). Skippable with --no-smoke (headless).
#   • UPLOAD  — with --upload: patch manifest.json (game macOS entry + the whole
#               launcher section) and `gh release upload --clobber` everything.
#
# Signing: uses a "Developer ID Application" identity when one is in the keychain,
# otherwise ad-hoc (runnable; Gatekeeper still shows the one-time "unverified
# developer" prompt until the app is Developer-ID-signed AND notarized — for that,
# run sign-macos.sh after this). SIGN_IDENTITY overrides the identity.
#
# Usage:
#   ./package-macos.sh                     # build + repair + smoke, artifacts in out/
#   ./package-macos.sh --upload            # …then patch manifest + upload to the release
#   ./package-macos.sh --game-zip <path>   # use a local game zip instead of downloading
#   ./package-macos.sh --tag v0.9.0        # target a specific release (default: latest)
#   ./package-macos.sh --no-smoke          # skip the launch test (headless CI only)
#
set -euo pipefail

REPO="samdotson61/Sporeholm"
HERE="$(cd "$(dirname "$0")" && pwd)"
LAUNCHER_DIR="$(dirname "$HERE")"                 # launcher/
GAME_REPO="$(dirname "$LAUNCHER_DIR")"            # repo root
OUT="$LAUNCHER_DIR/out-macos"
TAG="" GAME_ZIP="" UPLOAD=0 SMOKE=1

while [ $# -gt 0 ]; do
  case "$1" in
    --upload)   UPLOAD=1 ;;
    --tag)      TAG="$2"; shift ;;
    --game-zip) GAME_ZIP="$2"; shift ;;
    --no-smoke) SMOKE=0 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
  shift
done

say() { printf '\n\033[1;32m==>\033[0m %s\n' "$*"; }
[ "$(uname -s)" = "Darwin" ] || { echo "ERROR: run this on a Mac."; exit 1; }
command -v gh >/dev/null || { echo "ERROR: gh CLI required."; exit 1; }
DOTNET="$(command -v dotnet || true)"; [ -n "$DOTNET" ] || DOTNET="$HOME/.dotnet/dotnet"
[ -x "$DOTNET" ] || { echo "ERROR: dotnet SDK 8+ required (https://dot.net)."; exit 1; }

[ -n "$TAG" ] || TAG="$(gh release view --repo "$REPO" --json tagName --jq .tagName)"
say "Target release: $TAG"

# Identity: Developer ID if available, else ad-hoc ("-").
IDENTITY="${SIGN_IDENTITY:-$(security find-identity -v -p codesigning 2>/dev/null \
            | grep 'Developer ID Application' | head -1 | sed -E 's/.*"(.*)".*/\1/' || true)}"
if [ -z "$IDENTITY" ]; then IDENTITY="-"; say "No Developer ID cert — signing AD-HOC (runnable; notarize later via sign-macos.sh)"; else say "Signing identity: $IDENTITY"; fi

rm -rf "$OUT"; mkdir -p "$OUT"
WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT

# ---- 1. GAME: repair Sporeholm-macos.zip ---------------------------------------------
say "Game: repairing Sporeholm-macos.zip"
if [ -z "$GAME_ZIP" ]; then
  GAME_ZIP="$WORK/game-in.zip"
  gh release download "$TAG" --repo "$REPO" --pattern 'Sporeholm-macos.zip' --output "$GAME_ZIP"
fi
mkdir -p "$WORK/game"; ditto -x -k "$GAME_ZIP" "$WORK/game"
GAPP="$WORK/game/Sporeholm.app"
[ -d "$GAPP" ] || { echo "ERROR: no Sporeholm.app inside the game zip."; exit 1; }

# The Godot export preset stamps its own bundle version (and lives only on the build
# box) — sync the plist to version.txt so the .app always declares the real version.
GVER="$(tr -d 'v[:space:]' < "$WORK/game/version.txt" 2>/dev/null || true)"
if [ -n "$GVER" ]; then
  plutil -replace CFBundleShortVersionString -string "$GVER" "$GAPP/Contents/Info.plist"
  plutil -replace CFBundleVersion            -string "$GVER" "$GAPP/Contents/Info.plist"
fi
chmod -R +x "$GAPP/Contents/MacOS"
codesign --force -s "$IDENTITY" "$GAPP/Contents/MacOS/"* >/dev/null 2>&1 || true
codesign --force -s "$IDENTITY" "$GAPP"
codesign --verify --strict "$GAPP"
( cd "$WORK/game" && zip -qrX "$OUT/Sporeholm-macos.zip" Sporeholm.app version.txt )
say "Game zip ready ($(du -h "$OUT/Sporeholm-macos.zip" | cut -f1 | tr -d ' ')) — version $GVER"

# ---- 2. LAUNCHER: publish all RIDs + assemble the universal .app ---------------------
LVER="$(grep -Eo 'Version *= *"[^"]+"' "$LAUNCHER_DIR/src/SporeholmLauncher.Core/LauncherInfo.cs" | sed -E 's/.*"(.*)"/\1/')"
say "Launcher: publishing v$LVER (win-x64, linux-x64, osx-x64, osx-arm64)"
for rid in win-x64 linux-x64 osx-x64 osx-arm64; do
  "$DOTNET" publish "$LAUNCHER_DIR/src/SporeholmLauncher.App/SporeholmLauncher.App.csproj" \
    -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$LAUNCHER_DIR/dist/$rid" -v quiet --nologo
done

say "Launcher: assembling universal SporeholmLauncher.app"
APP="$WORK/SporeholmLauncher.app"; MACOS="$APP/Contents/MacOS"; RES="$APP/Contents/Resources"
mkdir -p "$MACOS" "$RES"
cp "$LAUNCHER_DIR/dist/osx-arm64/SporeholmLauncher" "$MACOS/SporeholmLauncher-arm64"
cp "$LAUNCHER_DIR/dist/osx-x64/SporeholmLauncher"   "$MACOS/SporeholmLauncher-x64"
# The dylibs dotnet leaves beside the single file are universal — ship them in the .app.
cp "$LAUNCHER_DIR/dist/osx-arm64/"lib*.dylib "$MACOS/"
n_dylibs=$(ls "$MACOS/"lib*.dylib 2>/dev/null | wc -l | tr -d ' ')
[ "$n_dylibs" -ge 3 ] || { echo "ERROR: expected ≥3 native dylibs beside the publish output, found $n_dylibs — refusing to ship a GUI that cannot render."; exit 1; }
cp "$GAME_REPO/icon.icns" "$RES/icon.icns"
printf '#!/bin/sh\nDIR="$(cd "$(dirname "$0")" && pwd)"\nif [ "$(uname -m)" = "arm64" ]; then\n  exec "$DIR/SporeholmLauncher-arm64" "$@"\nelse\n  exec "$DIR/SporeholmLauncher-x64" "$@"\nfi\n' > "$MACOS/SporeholmLauncher"
cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Sporeholm Launcher</string>
  <key>CFBundleDisplayName</key><string>Sporeholm Launcher</string>
  <key>CFBundleIdentifier</key><string>com.samdotson.sporeholm.launcher</string>
  <key>CFBundleVersion</key><string>$LVER</string>
  <key>CFBundleShortVersionString</key><string>$LVER</string>
  <key>CFBundleExecutable</key><string>SporeholmLauncher</string>
  <key>CFBundleIconFile</key><string>icon.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
EOF
chmod 755 "$MACOS/SporeholmLauncher" "$MACOS/SporeholmLauncher-arm64" "$MACOS/SporeholmLauncher-x64"
chmod 644 "$MACOS/"lib*.dylib
codesign --force -s "$IDENTITY" "$MACOS/"lib*.dylib "$MACOS/SporeholmLauncher-arm64" "$MACOS/SporeholmLauncher-x64"
codesign --force -s "$IDENTITY" "$APP"
codesign --verify --strict "$APP"
( cd "$WORK" && zip -qrX "$OUT/SporeholmLauncher-macos.zip" SporeholmLauncher.app )
cp "$LAUNCHER_DIR/dist/win-x64/SporeholmLauncher.exe" "$OUT/SporeholmLauncher.exe"
cp "$LAUNCHER_DIR/dist/linux-x64/SporeholmLauncher"   "$OUT/SporeholmLauncher-linux"

# ---- 3. SMOKE: the zip must actually open on this Mac --------------------------------
if [ "$SMOKE" = 1 ]; then
  say "Smoke test: launching the launcher from the final zip"
  mkdir -p "$WORK/smoke"; ( cd "$WORK/smoke" && unzip -q "$OUT/SporeholmLauncher-macos.zip" )
  # `open` = the same LaunchServices spawn a player's double-click uses — the exact
  # code path that failed on v0.8.9's artifacts.
  open "$WORK/smoke/SporeholmLauncher.app"
  sleep 6
  if pgrep -f "smoke/SporeholmLauncher.app" >/dev/null; then
    pkill -f "smoke/SporeholmLauncher.app" || true
    say "Smoke test PASSED — the launcher opens."
  else
    echo "ERROR: smoke test FAILED — the freshly-built launcher did not stay running." >&2
    echo "       Do NOT upload this. (--no-smoke skips this check on headless machines.)" >&2
    exit 1
  fi
fi

# ---- 4. UPLOAD: patch manifest + replace assets ---------------------------------------
if [ "$UPLOAD" = 1 ]; then
  say "Patching manifest.json + uploading to $TAG"
  gh release download "$TAG" --repo "$REPO" --pattern manifest.json --output "$OUT/manifest.json" --clobber
  python3 - "$OUT" "$LVER" <<'PY'
import json, hashlib, os, sys
out, lver = sys.argv[1], sys.argv[2]
def entry(name):
    p = os.path.join(out, name); h = hashlib.sha256()
    with open(p, 'rb') as f:
        for c in iter(lambda: f.read(1 << 20), b''): h.update(c)
    return {'name': name, 'sha256': h.hexdigest(), 'size': os.path.getsize(p)}
mp = os.path.join(out, 'manifest.json')
m = json.load(open(mp))
m['files']['macos'] = entry('Sporeholm-macos.zip')
m.setdefault('launcher', {})['version'] = lver
m['launcher']['files'] = {
    'windows': entry('SporeholmLauncher.exe'),
    'linux':   entry('SporeholmLauncher-linux'),
    'macos':   entry('SporeholmLauncher-macos.zip'),
}
json.dump(m, open(mp, 'w'), indent=2)
print('manifest patched: game macOS + launcher', lver)
PY
  ( cd "$OUT" && gh release upload "$TAG" --repo "$REPO" --clobber \
      Sporeholm-macos.zip SporeholmLauncher-macos.zip SporeholmLauncher.exe SporeholmLauncher-linux manifest.json )
  say "Uploaded. Players get working macOS builds on their next check."
else
  say "Artifacts ready in $OUT (re-run with --upload to publish):"
  ls -la "$OUT"
fi
