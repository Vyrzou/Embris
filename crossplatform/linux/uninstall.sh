#!/usr/bin/env bash
# Removes Embris, its private Python environment, settings and presets.
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
CONFIG="${XDG_CONFIG_HOME:-$HOME/.config}"

read -r -p "  Remove Embris and its settings? [y/N] " answer
case "$answer" in
  [yY]*) ;;
  *) echo "  Nothing was removed."; exit 0 ;;
esac

pkill -f "embris/embris.py" 2>/dev/null || true
rm -rf "$DATA/embris" "$CONFIG/embris"
rm -f "$HOME/.local/bin/embris" "$DATA/applications/embris.desktop" "$DATA/mime/packages/embris.xml"
echo "  Embris has been removed."
