#!/usr/bin/env bash
#
# Sign + notarize + staple BOTH macOS artifacts — the game (Sporeholm.app) and the launcher
# (SporeholmLauncher.app) — then refresh the release so Gatekeeper opens them without warnings.
#
# RUN THIS ON YOUR MAC (codesign / notarytool / stapler are macOS-only). You need:
#   • Xcode Command Line Tools           ->  xcode-select --install
#   • A "Developer ID Application" cert   ->  developer.apple.com → Certificates (in your login keychain)
#   • A notarization credential profile, set up ONCE:
#
#       xcrun notarytool store-credentials sporeholm-notary \
#           --apple-id "you@example.com" --team-id "YOURTEAMID" --password "app-specific-password"
#
#     (app-specific password: appleid.apple.com → Sign-In and Security → App-Specific Passwords)
#
# For each artifact it: download → sign every Mach-O inside-out with the hardened runtime +
# .NET JIT entitlements → notarize → staple → re-zip (preserving version.txt / bundle perms) →
# recompute the SHA-256. Finally it patches manifest.json (both the game and launcher macOS
# checksums) and re-uploads the two signed zips + manifest.
#
set -euo pipefail

REPO="samdotson61/Sporeholm"
TAG="${TAG:-$(gh release view --repo "$REPO" --json tagName --jq .tagName)}"
PROFILE="${NOTARY_PROFILE:-sporeholm-notary}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
say() { printf '\n\033[1;32m==>\033[0m %s\n' "$*"; }

# ---- identity + entitlements -------------------------------------------------
IDENTITY="${SIGN_IDENTITY:-$(security find-identity -v -p codesigning | grep 'Developer ID Application' | head -1 | sed -E 's/.*"(.*)".*/\1/')}"
[ -n "$IDENTITY" ] || { echo "ERROR: no 'Developer ID Application' certificate in your keychain."; exit 1; }
say "Signing identity: $IDENTITY"

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

# Sign a .app inside-out: every nested Mach-O, then the .NET executables in MacOS/ with
# entitlements (they JIT), then the bundle. Works for the game (Mach-O main) and the
# launcher (script trampoline main + arm64/x64 Mach-O binaries beside it).
sign_app() {
  local app="$1"
  while IFS= read -r f; do
    if file "$f" | grep -q 'Mach-O'; then
      codesign --force --timestamp --options runtime -s "$IDENTITY" "$f" >/dev/null
    fi
  done < <(find "$app/Contents" -type f)
  for f in "$app/Contents/MacOS/"*; do
    if [ -f "$f" ] && file "$f" | grep -q 'Mach-O'; then
      codesign --force --timestamp --options runtime --entitlements "$ENT" -s "$IDENTITY" "$f" >/dev/null
    fi
  done
  codesign --force --timestamp --options runtime --entitlements "$ENT" -s "$IDENTITY" "$app"
  codesign --verify --deep --strict --verbose=2 "$app"
}

# Download an asset, sign + notarize + staple its .app, re-zip; sets OUT_SHA / OUT_SIZE / OUT_ZIP.
process() {
  local asset="$1" sub="$2"
  local dir="$WORK/$sub"; mkdir -p "$dir/x"
  say "Artifact: $asset"
  curl -fL "https://github.com/$REPO/releases/latest/download/$asset" -o "$dir/in.zip"
  ditto -x -k "$dir/in.zip" "$dir/x"
  local app; app="$(find "$dir/x" -maxdepth 2 -name '*.app' -type d | head -1)"
  [ -n "$app" ] || { echo "ERROR: no .app inside $asset"; exit 1; }

  say "Signing $app …"
  sign_app "$app"
  say "Notarizing (a few minutes)…"
  ditto -c -k --keepParent "$app" "$dir/n.zip"
  xcrun notarytool submit "$dir/n.zip" --keychain-profile "$PROFILE" --wait
  xcrun stapler staple "$app"
  spctl -a -vvv -t install "$app" || true

  local out="$PWD/$asset"; rm -f "$out"
  # zip -rX (not ditto): preserves structure + unix perms WITHOUT AppleDouble ._ files
  # (com.apple.provenance is SIP-protected, so ditto would always emit them).
  ( cd "$dir/x" && zip -qrX "$out" * )   # game: app + version.txt at root; launcher: app
  OUT_ZIP="$out"
  OUT_SHA="$(shasum -a 256 "$out" | awk '{print $1}')"
  OUT_SIZE="$(stat -f%z "$out")"
  say "Signed → $out   sha=$OUT_SHA   size=$OUT_SIZE"
}

# ---- sign both artifacts -----------------------------------------------------
process "Sporeholm-macos.zip" game
GAME_SHA="$OUT_SHA"; GAME_SIZE="$OUT_SIZE"; GAME_ZIP="$OUT_ZIP"

process "SporeholmLauncher-macos.zip" launcher
LAUNCHER_SHA="$OUT_SHA"; LAUNCHER_SIZE="$OUT_SIZE"; LAUNCHER_ZIP="$OUT_ZIP"

# ---- patch manifest (game + launcher macOS checksums) + re-upload -------------
if command -v gh >/dev/null 2>&1 && command -v python3 >/dev/null 2>&1; then
  say "Updating manifest.json and re-uploading…"
  curl -fL "https://github.com/$REPO/releases/latest/download/manifest.json" -o "$WORK/manifest.json"
  python3 - "$WORK/manifest.json" "$GAME_SHA" "$GAME_SIZE" "$LAUNCHER_SHA" "$LAUNCHER_SIZE" <<'PY'
import json, sys
p, gsha, gsize, lsha, lsize = sys.argv[1], sys.argv[2], int(sys.argv[3]), sys.argv[4], int(sys.argv[5])
m = json.load(open(p))
m["files"]["macos"]["sha256"] = gsha; m["files"]["macos"]["size"] = gsize
if isinstance(m.get("launcher"), dict) and "macos" in m["launcher"].get("files", {}):
    m["launcher"]["files"]["macos"]["sha256"] = lsha
    m["launcher"]["files"]["macos"]["size"]   = lsize
json.dump(m, open(p, "w"), indent=2)
print("manifest patched (game + launcher macOS checksums)")
PY
  gh release upload "$TAG" --repo "$REPO" --clobber "$GAME_ZIP" "$LAUNCHER_ZIP" "$WORK/manifest.json"
  say "Done — signed + notarized game and launcher are live; macOS Gatekeeper will open them cleanly."
else
  cat <<EOF

==> Almost done — finish manually (gh and/or python3 not found):
    Re-upload (asset names must match exactly):
      gh release upload $TAG --repo $REPO --clobber "$GAME_ZIP" "$LAUNCHER_ZIP"
    Then in manifest.json set:
      files.macos            = { sha256: $GAME_SHA,     size: $GAME_SIZE }
      launcher.files.macos   = { sha256: $LAUNCHER_SHA, size: $LAUNCHER_SIZE }
    and re-upload manifest.json. (Or send Claude those four values and it'll patch + upload.)
EOF
fi

# Note: the launcher .app is universal (an arch-picking shell trampoline + arm64/x64 binaries).
# If Apple's notary service rejects it (some macOS setups dislike a script as the main
# executable), tell Claude — the fallback is a single-arch (arm64) launcher .app, which signs
# and notarizes with no caveats.
