#!/usr/bin/env bash
# Embris installer for Linux. Run from a terminal:  bash install.sh
set -e
cd "$(dirname "$0")"

VERSION="1.6.2"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
DIR="$DATA/embris"
BIN="$HOME/.local/bin"

echo ""
echo "  Installing Embris $VERSION"
echo "  -------------------------"

# System packages: Python with venv (required), the Qt X11 cursor library, and xdotool
# (to bring the game to the front).
missing=0
command -v python3 >/dev/null 2>&1 || missing=1
if [ $missing -eq 0 ]; then
  python3 -c 'import venv, ensurepip' >/dev/null 2>&1 || missing=1
fi
command -v xdotool >/dev/null 2>&1 || missing=1
if command -v ldconfig >/dev/null 2>&1 && ! ldconfig -p 2>/dev/null | grep -q libxcb-cursor; then missing=1; fi

if [ $missing -eq 1 ]; then
  if command -v apt-get >/dev/null 2>&1; then CMD="sudo apt-get install -y python3 python3-venv xdotool libxcb-cursor0"
  elif command -v dnf >/dev/null 2>&1; then CMD="sudo dnf install -y python3 xdotool xcb-util-cursor"
  elif command -v pacman >/dev/null 2>&1; then CMD="sudo pacman -S --needed --noconfirm python xdotool xcb-util-cursor"
  elif command -v zypper >/dev/null 2>&1; then CMD="sudo zypper install -y python3 xdotool libxcb-cursor0"
  else CMD=""
  fi
  echo "  Some system packages are missing (Python, venv, xdotool or libxcb-cursor)."
  if [ -n "$CMD" ]; then
    echo "  This will install them:  $CMD"
    read -r -p "  Run it now? [Y/n] " answer
    case "$answer" in
      [nN]*) echo "  Install them yourself, then run this installer again."; exit 1 ;;
      *) $CMD ;;
    esac
  else
    echo "  Install python3 (with venv), xdotool and libxcb-cursor with your package manager, then run this again."
    exit 1
  fi
fi

python3 -c 'import sys; sys.exit(0 if sys.version_info >= (3, 9) else 1)' ||
  { echo "  Embris needs Python 3.9 or newer."; exit 1; }

echo "  Setting up a private Python environment..."
mkdir -p "$DIR" "$BIN" "$DATA/applications"
python3 -m venv "$DIR/venv"
"$DIR/venv/bin/python" -m pip install --quiet --upgrade pip
echo "  Downloading Qt and pynput (about 80 MB, this can take a minute)..."
"$DIR/venv/bin/python" -m pip install --quiet "PySide6-Essentials>=6.5"
read -r -p "  Add video backgrounds (MP4/WebM wallpapers, about 150 MB more)? [Y/n] " video
case "$video" in
  [nN]*) echo "  Skipped. Pictures and GIFs still work as backgrounds." ;;
  *) QT=$("$DIR/venv/bin/python" -c 'import PySide6; print(PySide6.__version__)')
     "$DIR/venv/bin/python" -m pip install --quiet "PySide6-Addons==$QT" ;;
esac
# --no-deps skips evdev (needs a compiler); the X11 backend only needs python-xlib and six.
"$DIR/venv/bin/python" -m pip install --quiet --no-deps "pynput>=1.7.6" python-xlib six

cp embris.py embris_core.py emb_script.py backgrounds.py timeline.py editors.py overlay.py embris.png "$DIR/"

cat > "$BIN/embris" <<LAUNCHER
#!/usr/bin/env bash
exec "$DIR/venv/bin/python" "$DIR/embris.py" "\$@"
LAUNCHER
chmod +x "$BIN/embris"

cat > "$DATA/applications/embris.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Embris
Comment=Key & click macro
Exec="$BIN/embris" %f
Icon=$DIR/embris.png
Terminal=false
Categories=Utility;
StartupWMClass=embris
MimeType=application/x-embris;
DESKTOP
# .emb macro files open with Embris.
mkdir -p "$DATA/mime/packages"
cat > "$DATA/mime/packages/embris.xml" <<MIME
<?xml version="1.0" encoding="UTF-8"?>
<mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
  <mime-type type="application/x-embris">
    <comment>Embris macro</comment>
    <glob pattern="*.emb"/>
  </mime-type>
</mime-info>
MIME
command -v update-mime-database >/dev/null 2>&1 && update-mime-database "$DATA/mime" >/dev/null 2>&1 || true
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$DATA/applications" >/dev/null 2>&1 || true
command -v xdg-mime >/dev/null 2>&1 && xdg-mime default embris.desktop application/x-embris >/dev/null 2>&1 || true

echo ""
echo "  Done! Open Embris from your app menu, or run: embris"
if [ "${XDG_SESSION_TYPE:-}" = "wayland" ]; then
  echo ""
  echo "  Note: you're on Wayland. If keys or the hotkey don't work, log in with an X11 / Xorg session."
fi
echo ""
nohup "$BIN/embris" >/dev/null 2>&1 &
