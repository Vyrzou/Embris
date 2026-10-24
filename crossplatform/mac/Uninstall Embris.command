#!/bin/bash
# Removes Embris, its private Python environment, settings and presets.
APP="$HOME/Applications/Embris.app"
SUPPORT="$HOME/Library/Application Support/Embris"

echo ""
read -r -p "  Remove Embris and its settings? [y/N] " answer
case "$answer" in
  [yY]*) ;;
  *) echo "  Nothing was removed."; exit 0 ;;
esac

pkill -f "Application Support/Embris/embris.py" 2>/dev/null || true
rm -rf "$APP" "$SUPPORT"
echo "  Embris has been removed."
echo "  You can also remove it from System Settings > Privacy & Security > Accessibility / Input Monitoring."
read -n 1 -s -r -p "  Press any key to close."
echo ""
