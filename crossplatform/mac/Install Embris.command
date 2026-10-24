#!/bin/bash
# Embris installer for macOS. Double-click this file (right-click > Open the first time).
set -e
cd "$(dirname "$0")"

VERSION="1.7.0"
SUPPORT="$HOME/Library/Application Support/Embris"
APP="$HOME/Applications/Embris.app"

echo ""
echo "  Installing Embris $VERSION"
echo "  -------------------------"

# Find Python 3.9+ (needed by Qt). Prefer python.org and Homebrew builds; Apple's
# built-in python3 may ask to install the Command Line Tools first.
PY=""
for c in /Library/Frameworks/Python.framework/Versions/Current/bin/python3 \
         /opt/homebrew/bin/python3 /usr/local/bin/python3 python3; do
  if command -v "$c" >/dev/null 2>&1 &&
     "$c" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 9) else 1)' >/dev/null 2>&1; then
    PY="$(command -v "$c")"
    break
  fi
done

if [ -z "$PY" ]; then
  echo ""
  echo "  Python 3 is needed first. Opening the download page..."
  echo "  Install the macOS version from python.org, then run this installer again."
  open "https://www.python.org/downloads/macos/"
  read -n 1 -s -r -p "  Press any key to close."
  exit 1
fi
echo "  Using $("$PY" --version) at $PY"

echo "  Setting up a private Python environment..."
mkdir -p "$SUPPORT"
"$PY" -m venv "$SUPPORT/venv"
"$SUPPORT/venv/bin/python" -m pip install --quiet --upgrade pip
echo "  Downloading Qt and pynput (about 80 MB, this can take a minute)..."
"$SUPPORT/venv/bin/python" -m pip install --quiet "PySide6-Essentials>=6.5" "pynput>=1.7.6"
echo ""
read -r -p "  Add video backgrounds (MP4/WebM wallpapers, about 150 MB more)? [Y/n] " video
case "$video" in
  [nN]*) echo "  Skipped. Pictures and GIFs still work as backgrounds." ;;
  *) QT=$("$SUPPORT/venv/bin/python" -c 'import PySide6; print(PySide6.__version__)')
     "$SUPPORT/venv/bin/python" -m pip install --quiet "PySide6-Addons==$QT" ;;
esac

cp embris.py embris_core.py emb_script.py backgrounds.py timeline.py editors.py overlay.py embris.png "$SUPPORT/"

echo "  Creating $APP..."
mkdir -p "$HOME/Applications"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp embris.icns "$APP/Contents/Resources/embris.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Embris</string>
  <key>CFBundleDisplayName</key><string>Embris</string>
  <key>CFBundleIdentifier</key><string>com.embris.app</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleExecutable</key><string>embris</string>
  <key>CFBundleIconFile</key><string>embris</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSAppleEventsUsageDescription</key><string>Embris brings your game to the front before pressing keys.</string>
  <key>CFBundleDocumentTypes</key>
  <array>
    <dict>
      <key>CFBundleTypeName</key><string>Embris macro</string>
      <key>CFBundleTypeExtensions</key><array><string>emb</string></array>
      <key>CFBundleTypeIconFile</key><string>embris</string>
      <key>CFBundleTypeRole</key><string>Editor</string>
      <key>LSHandlerRank</key><string>Owner</string>
    </dict>
  </array>
</dict>
</plist>
PLIST

cat > "$APP/Contents/MacOS/embris" <<LAUNCHER
#!/bin/bash
exec "$SUPPORT/venv/bin/python" "$SUPPORT/embris.py"
LAUNCHER
chmod +x "$APP/Contents/MacOS/embris"
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true
touch "$APP"
# Tell macOS that .emb macro files open with Embris.
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$APP" 2>/dev/null || true

echo ""
echo "  Done! Embris is in the Applications folder inside your home folder."
echo ""
echo "  IMPORTANT - one-time permission:"
echo "  macOS will ask to let Embris control your computer. Allow it in"
echo "  System Settings > Privacy & Security > Accessibility AND Input Monitoring,"
echo "  then quit and reopen Embris."
echo ""
open "$APP"
read -n 1 -s -r -p "  Press any key to close this window."
echo ""
