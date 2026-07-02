#!/usr/bin/env bash
#
# Finalize the macOS release artifacts — RUN THIS ON A MAC. It is the step that makes
# them actually runnable AND trusted; macOS zips straight off the Windows box ship
# broken in three independent ways (all found the hard way on v0.8.9):
#
#   1. Zips made on Windows carry no Unix exec bits → the .app fails to spawn at all.
#   2. Unsigned binaries → Apple Silicon SIGKILLs them even with exec bits restored.
#   3. `dotnet publish` single-file on macOS NEVER embeds native dylibs (by design) —
#      libSkiaSharp / libHarfBuzzSharp / libAvaloniaNative must be shipped INSIDE the
#      .app next to the binaries, or the GUI crashes at render init on every Mac.
#
# What it does, in order (one pass, one upload — since v0.8.10 notarization happens
# HERE rather than in a second download→re-sign→re-upload round-trip):
#
#   • GAME     — take Sporeholm-macos.zip (from the release by default, or --game-zip),
#                restore exec bits, sync Info.plist's version to the zip's version.txt,
#                sign, NOTARIZE + staple, re-zip with perms.
#   • LAUNCHER — skipped when LauncherInfo.cs matches the manifest's launcher version
#                (the binaries wouldn't change); otherwise publish all four RIDs,
#                assemble the universal .app (arch trampoline + BOTH binaries + the
#                dylibs + icon), sign, NOTARIZE + staple, zip. Win/Linux binaries are
#                rebuilt too so a version bump never strands their self-update.
#   • SMOKE    — actually LAUNCH the freshly-zipped launcher .app once (the v0.8.9
#                lesson: "it built" ≠ "it opens"). Skippable with --no-smoke.
#   • UPLOAD   — with --upload: patch manifest.json (game macOS entry + the launcher
#                section when rebuilt) and `gh release upload --clobber` everything.
#
# Signing tiers: with a "Developer ID Application" identity in the keychain AND the
# `sporeholm-notary` notarytool profile stored, artifacts are signed + notarized +
# stapled (zero Gatekeeper prompt — the standard since v0.8.10). With no cert the
# script falls back to ad-hoc signing and skips notarization (runnable; Gatekeeper
# prompts once). SIGN_IDENTITY / NOTARY_PROFILE override the defaults.
#
# Usage:
#   ./package-macos.sh                     # build + repair + notarize + smoke → out-macos/
#   ./package-macos.sh --upload            # …then patch manifest + upload to the release
#   ./package-macos.sh --game-zip <path>   # use a local game zip instead of downloading
#   ./package-macos.sh --tag v0.9.0        # target a specific release (default: latest)
#   ./package-macos.sh --force-launcher    # rebuild the launcher even if version unchanged
#   ./package-macos.sh --no-smoke          # skip the launch test (headless CI only)
#   ./package-macos.sh --no-notarize       # ad-hoc tier even if a cert exists
#
set -euo pipefail

REPO="samdotson61/Sporeholm"
HERE="$(cd "$(dirname "$0")" && pwd)"
LAUNCHER_DIR="$(dirname "$HERE")"                 # launcher/
GAME_REPO="$(dirname "$LAUNCHER_DIR")"            # repo root
OUT="$LAUNCHER_DIR/out-macos"
PROFILE="${NOTARY_PROFILE:-sporeholm-notary}"
TAG="" GAME_ZIP="" UPLOAD=0 SMOKE=1 NOTARIZE=1 FORCE_LAUNCHER=0

while [ $# -gt 0 ]; do
  case "$1" in
    --upload)         UPLOAD=1 ;;
    --tag)            TAG="$2"; shift ;;
    --game-zip)       GAME_ZIP="$2"; shift ;;
    --no-smoke)       SMOKE=0 ;;
    --no-notarize)    NOTARIZE=0 ;;
    --force-launcher) FORCE_LAUNCHER=1 ;;
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

# Identity: Developer ID if available, else ad-hoc ("-") without notarization.
IDENTITY="${SIGN_IDENTITY:-$(security find-identity -v -p codesigning 2>/dev/null \
            | grep 'Developer ID Application' | head -1 | sed -E 's/.*"(.*)".*/\1/' || true)}"
if [ -z "$IDENTITY" ]; then
  IDENTITY="-"; NOTARIZE=0
  say "No Developer ID cert — AD-HOC tier (runnable; Gatekeeper prompts once; no notarization)"
else
  say "Signing identity: $IDENTITY"
  if [ "$NOTARIZE" = 1 ] && ! xcrun notarytool history --keychain-profile "$PROFILE" >/dev/null 2>&1; then
    echo "ERROR: notary profile '$PROFILE' not found. Store it once:" >&2
    echo "  xcrun notarytool store-credentials $PROFILE --apple-id <id> --team-id <team>" >&2
    echo "or pass --no-notarize for the ad-hoc tier." >&2
    exit 1
  fi
fi

rm -rf "$OUT"; mkdir -p "$OUT"
WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT

# Hardened-runtime entitlements for the .NET/Godot JIT (required when notarizing).
ENT="$WORK/entitlements.plist"
cat > "$ENT" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>com.apple.security.cs.allow-jit</key><true/>
  <key>com.apple.security.cs.allow-unsigned-executable-memory</key><true/>
  <key>com.apple.security.cs.disable-library-validation</key><true/>
  <key>com.apple.security.cs.allow-dyld-environment-variables</key><true/>
</dict></plist>
EOF

SIGN_FLAGS=()
[ "$NOTARIZE" = 1 ] && SIGN_FLAGS=(--timestamp --options runtime)

sign_app() {   # $1 = .app path — every Mach-O inside-out, main executables last
  local app="$1"
  while IFS= read -r f; do
    if file "$f" | grep -q 'Mach-O'; then
      codesign --force "${SIGN_FLAGS[@]}" -s "$IDENTITY" "$f" >/dev/null 2>&1
    fi
  done < <(find "$app/Contents" -type f)
  local mainflags=("${SIGN_FLAGS[@]}")
  [ "$NOTARIZE" = 1 ] && mainflags+=(--entitlements "$ENT")
  for f in "$app/Contents/MacOS/"*; do
    if [ -f "$f" ] && file "$f" | grep -q 'Mach-O'; then
      codesign --force "${mainflags[@]}" -s "$IDENTITY" "$f" >/dev/null
    fi
  done
  if [ "$NOTARIZE" = 1 ]; then
    codesign --force "${mainflags[@]}" -s "$IDENTITY" "$app"
  else
    codesign --force -s "$IDENTITY" "$app"
  fi
  codesign --verify --strict "$app"
}

notarize_app() {   # $1 = .app path, $2 = label
  [ "$NOTARIZE" = 1 ] || return 0
  say "Notarizing $2 (a few minutes)…"
  ditto -c -k --keepParent "$1" "$WORK/notarize-$2.zip"
  xcrun notarytool submit "$WORK/notarize-$2.zip" --keychain-profile "$PROFILE" --wait
  xcrun stapler staple "$1"
}

# ---- 1. GAME: repair + sign + notarize Sporeholm-macos.zip ---------------------------
say "Game: repairing Sporeholm-macos.zip"
if [ -z "$GAME_ZIP" ]; then
  GAME_ZIP="$WORK/game-in.zip"
  gh release download "$TAG" --repo "$REPO" --pattern 'Sporeholm-macos.zip' --output "$GAME_ZIP"
fi
mkdir -p "$WORK/game"; ditto -x -k "$GAME_ZIP" "$WORK/game"
GAPP="$WORK/game/Sporeholm.app"
[ -d "$GAPP" ] || { echo "ERROR: no Sporeholm.app inside the game zip."; exit 1; }

# The Godot export preset stamps its own bundle version — sync the plist to
# version.txt so the .app always declares the real version even if the preset lags.
GVER="$(tr -d 'v[:space:]' < "$WORK/game/version.txt" 2>/dev/null || true)"
if [ -n "$GVER" ]; then
  plutil -replace CFBundleShortVersionString -string "$GVER" "$GAPP/Contents/Info.plist"
  plutil -replace CFBundleVersion            -string "$GVER" "$GAPP/Contents/Info.plist"
fi
chmod -R +x "$GAPP/Contents/MacOS"
# iCloud/FileProvider folders re-apply FinderInfo xattrs that codesign rejects as
# "detritus" — we sign in a temp dir so this is belt-and-suspenders.
xattr -cr "$GAPP" 2>/dev/null || true
sign_app "$GAPP"
notarize_app "$GAPP" game
( cd "$WORK/game" && zip -qrX "$OUT/Sporeholm-macos.zip" Sporeholm.app version.txt )
say "Game zip ready ($(du -h "$OUT/Sporeholm-macos.zip" | cut -f1 | tr -d ' ')) — version $GVER"

# ---- 2. LAUNCHER: rebuild only when its version changed ------------------------------
LVER="$(grep -Eo 'Version *= *"[^"]+"' "$LAUNCHER_DIR/src/SporeholmLauncher.Core/LauncherInfo.cs" | sed -E 's/.*"(.*)"/\1/')"
MANIFEST_LVER="$(gh release download "$TAG" --repo "$REPO" --pattern manifest.json --output - 2>/dev/null \
                  | python3 -c 'import json,sys; print(json.load(sys.stdin).get("launcher",{}).get("version",""))' 2>/dev/null || true)"
LAUNCHER_BUILT=0
if [ "$FORCE_LAUNCHER" = 1 ] || [ "$LVER" != "$MANIFEST_LVER" ]; then
  say "Launcher: publishing v$LVER (manifest has '${MANIFEST_LVER:-none}') — win-x64, linux-x64, osx-x64, osx-arm64"
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
  sign_app "$APP"
  notarize_app "$APP" launcher
  ( cd "$WORK" && zip -qrX "$OUT/SporeholmLauncher-macos.zip" SporeholmLauncher.app )
  cp "$LAUNCHER_DIR/dist/win-x64/SporeholmLauncher.exe" "$OUT/SporeholmLauncher.exe"
  cp "$LAUNCHER_DIR/dist/linux-x64/SporeholmLauncher"   "$OUT/SporeholmLauncher-linux"
  LAUNCHER_BUILT=1
else
  say "Launcher: v$LVER already published — skipping rebuild (--force-launcher overrides)"
fi

# ---- 3. SMOKE: the zip must actually open on this Mac --------------------------------
if [ "$SMOKE" = 1 ] && [ "$LAUNCHER_BUILT" = 1 ]; then
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
  python3 - "$OUT" "$LVER" "$LAUNCHER_BUILT" <<'PY'
import json, hashlib, os, sys
out, lver, built = sys.argv[1], sys.argv[2], sys.argv[3] == "1"
def entry(name):
    p = os.path.join(out, name); h = hashlib.sha256()
    with open(p, 'rb') as f:
        for c in iter(lambda: f.read(1 << 20), b''): h.update(c)
    return {'name': name, 'sha256': h.hexdigest(), 'size': os.path.getsize(p)}
mp = os.path.join(out, 'manifest.json')
m = json.load(open(mp))
m['files']['macos'] = entry('Sporeholm-macos.zip')
if built:
    m.setdefault('launcher', {})['version'] = lver
    m['launcher']['files'] = {
        'windows': entry('SporeholmLauncher.exe'),
        'linux':   entry('SporeholmLauncher-linux'),
        'macos':   entry('SporeholmLauncher-macos.zip'),
    }
json.dump(m, open(mp, 'w'), indent=2)
print('manifest patched: game macOS' + (' + launcher ' + lver if built else ' (launcher unchanged)'))
PY
  ASSETS=(Sporeholm-macos.zip manifest.json)
  [ "$LAUNCHER_BUILT" = 1 ] && ASSETS+=(SporeholmLauncher-macos.zip SporeholmLauncher.exe SporeholmLauncher-linux)
  ( cd "$OUT" && gh release upload "$TAG" --repo "$REPO" --clobber "${ASSETS[@]}" )
  say "Uploaded. Heads-up: releases/latest/download/ (CDN) can lag a few minutes; verify via 'gh release download'."
else
  say "Artifacts ready in $OUT (re-run with --upload to publish):"
  ls -la "$OUT"
fi
