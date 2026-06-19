#!/usr/bin/env bash
#
# Sign + notarize + staple the macOS Sporeholm build, then refresh the release.
# RUN THIS ON YOUR MAC (it uses Apple's codesign / notarytool / stapler, which only
# exist on macOS). You need:
#   • Xcode Command Line Tools          ->  xcode-select --install
#   • A "Developer ID Application" cert  ->  developer.apple.com → Certificates (in your login keychain)
#   • A notarization credential profile, set up ONCE:
#
#       xcrun notarytool store-credentials sporeholm-notary \
#           --apple-id "you@example.com" --team-id "YOURTEAMID" \
#           --password "app-specific-password"
#
#     (app-specific password: appleid.apple.com → Sign-In and Security → App-Specific Passwords)
#     (team id: developer.apple.com → Membership)
#
# Usage:
#   ./sign-macos.sh                         # downloads Sporeholm-macos.zip from the latest release
#   ./sign-macos.sh /path/Sporeholm-macos.zip
#
# What it does: extract → sign every Mach-O inside-out with hardened runtime +
# .NET entitlements → notarize → staple → re-zip (keeping version.txt) → recompute
# the SHA-256 → patch manifest.json → re-upload the signed zip + manifest to the release.
#
set -euo pipefail

REPO="samdotson61/Sporeholm"
TAG="v0.8.9"
PROFILE="${NOTARY_PROFILE:-sporeholm-notary}"
WORK="$(mktemp -d)"
OUT="$PWD/Sporeholm-macos.zip"          # signed result (same asset name → ready to re-upload)
trap 'rm -rf "$WORK"' EXIT

say() { printf '\n\033[1;32m==>\033[0m %s\n' "$*"; }

# 1. Get the unsigned build -----------------------------------------------------
SRC="${1:-}"
if [ -z "$SRC" ]; then
  say "Downloading Sporeholm-macos.zip from the latest release…"
  curl -fL "https://github.com/$REPO/releases/latest/download/Sporeholm-macos.zip" -o "$WORK/in.zip"
  SRC="$WORK/in.zip"
fi
mkdir -p "$WORK/build"
ditto -x -k "$SRC" "$WORK/build"
APP="$(find "$WORK/build" -maxdepth 2 -name '*.app' -type d | head -1)"
[ -n "$APP" ] || { echo "ERROR: no .app found inside $SRC"; exit 1; }
say "App bundle: $APP"

# 2. Find the Developer ID Application identity ---------------------------------
IDENTITY="${SIGN_IDENTITY:-$(security find-identity -v -p codesigning | grep 'Developer ID Application' | head -1 | sed -E 's/.*"(.*)".*/\1/')}"
[ -n "$IDENTITY" ] || { echo "ERROR: no 'Developer ID Application' certificate in your keychain. Create one at developer.apple.com → Certificates."; exit 1; }
say "Signing identity: $IDENTITY"

# 3. Entitlements — a .NET/Mono app JITs under the hardened runtime -------------
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

# 4. Sign inside-out: every nested Mach-O first, then the main exe + bundle ------
say "Signing nested binaries (this takes a moment — there are many .NET dylibs)…"
while IFS= read -r f; do
  if file "$f" | grep -q 'Mach-O'; then
    codesign --force --timestamp --options runtime -s "$IDENTITY" "$f" >/dev/null
  fi
done < <(find "$APP" -type f)

MAIN="$APP/Contents/MacOS/$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$APP/Contents/Info.plist")"
codesign --force --timestamp --options runtime --entitlements "$ENT" -s "$IDENTITY" "$MAIN" >/dev/null
codesign --force --timestamp --options runtime --entitlements "$ENT" -s "$IDENTITY" "$APP"
say "Verifying signature…"
codesign --verify --deep --strict --verbose=2 "$APP"

# 5. Notarize (submit a zip of just the .app) + staple --------------------------
say "Submitting to Apple notary service (can take a few minutes)…"
ditto -c -k --keepParent "$APP" "$WORK/notarize.zip"
xcrun notarytool submit "$WORK/notarize.zip" --keychain-profile "$PROFILE" --wait
say "Stapling the notarization ticket…"
xcrun stapler staple "$APP"
spctl -a -vvv -t install "$APP" || true     # should print: accepted, source=Notarized Developer ID

# 6. Re-zip the build (Sporeholm.app + version.txt) with ditto (preserves perms) -
say "Re-zipping the signed build…"
rm -f "$OUT"
ditto -c -k "$WORK/build" "$OUT"             # zips the CONTENTS → Sporeholm.app + version.txt at root

SHA="$(shasum -a 256 "$OUT" | awk '{print $1}')"
SIZE="$(stat -f%z "$OUT")"
say "Signed zip: $OUT"
echo "    sha256 = $SHA"
echo "    size   = $SIZE bytes"

# 7. Patch manifest.json (macOS sha/size must match the new zip) + re-upload -----
if command -v gh >/dev/null 2>&1 && command -v python3 >/dev/null 2>&1; then
  say "Updating manifest.json and re-uploading to the release…"
  curl -fL "https://github.com/$REPO/releases/latest/download/manifest.json" -o "$WORK/manifest.json"
  python3 - "$WORK/manifest.json" "$SHA" "$SIZE" <<'PY'
import json, sys
path, sha, size = sys.argv[1], sys.argv[2], int(sys.argv[3])
m = json.load(open(path))
m["files"]["macos"]["sha256"] = sha
m["files"]["macos"]["size"]   = size
json.dump(m, open(path, "w"), indent=2)
PY
  gh release upload "$TAG" --repo "$REPO" --clobber "$OUT" "$WORK/manifest.json"
  say "Done. The signed, notarized macOS build is live and the launcher will verify + install it."
else
  cat <<EOF

==> Almost done — finish the upload manually (gh and/or python3 not found here):
    1. Re-upload the signed build:
         gh release upload $TAG --repo $REPO --clobber "$OUT"
       (or drag it into the release on github.com, replacing Sporeholm-macos.zip)
    2. Update manifest.json so files.macos = { "sha256": "$SHA", "size": $SIZE }
       then re-upload manifest.json the same way.
       (Tell Claude the sha256 + size above and it can patch + upload the manifest for you.)
EOF
fi
