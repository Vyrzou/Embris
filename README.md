# Embris

A key, click and gameplay macro app, made with Roblox in mind. Windows, plus a Mac/Linux version
with the same features.

## Features

- **Macro**: press a key and/or click at a spot every so often, sent to the game in the background or by switching to it briefly (waits until you're idle).
- **Recording**: record keys, clicks and camera movement and play them back 1:1. Mouse movement is kept per mouse report, so camera turns replay exactly. **Frame sync** (Hotkeys → Recording) sends every input in the middle of one of the game's frames, so jumps land on the same frame every replay.
- **Sequence editor**: a table of steps: edit, reorder, crop, change every press of a key.
- **Timeline editor**: a track per key; drag, stretch, select several and squish/stretch them, cut across tracks, crop between two bars, snap to keys.
- **Undo / redo** (Ctrl+Z / Ctrl+Shift+Z), repeat count and any playback speed.
- **Calibration overlay** for surf and other maps: a crosshair and a see-through ghost of the start view, to start every run from the same spot.
- **Auto clicker** with its own hotkey.
- **Macro suite**: save macros, each with its own hotkey; hotstrings.
- **Scripts**: macros as plain-text `.emb` files to share; reads AutoHotkey hotstring files (`.ahk`).
- **Updates**: checks this repo's Releases on start-up (and from Settings → About). On Windows it downloads and installs the new version for you; on Mac/Linux it takes you to the download.
- **Looks**: themes, custom colors with a color picker, video or picture backgrounds (decoded on the graphics card), compact view, pin on top, window opacity.

## Download

Get the latest installers from the **Releases** page:

- Windows: `EmbrisInstaller.zip` → run `EmbrisSetup.exe` (no admin needed).
- Mac: `Embris-Mac.zip` → double-click `Install Embris.command`.
- Linux: `Embris-Linux.zip` → run `install.sh`.

Windows may warn that the app isn't signed: click **More info → Run anyway**.

## Building

**Windows**: run `build.bat`. It uses the C# compiler that comes with Windows (.NET Framework 4,
no Visual Studio needed) and makes `Embris.exe`, the installer `EmbrisSetup.exe`,
`EmbrisInstaller.zip`, and the Mac and Linux zips.

**Mac / Linux**: the app is Python 3 + PySide6 + pynput, in `crossplatform/`. The installers set up a
private Python environment and install those for you. To run it from source:

```
pip install PySide6-Essentials pynput
python crossplatform/embris.py
```

## Project layout

| Path | What it is |
| --- | --- |
| `Embris.cs` | Windows app: main window, pages, recording, playback, hotkeys |
| `Ui.cs` | Windows controls, theme and background drawing |
| `Editors.cs`, `Timeline.cs` | Sequence and timeline editors |
| `Backgrounds.cs` | Custom backgrounds (Media Foundation / Direct3D 11 video) |
| `Script.cs` | `.emb` scripts and `.ahk` import |
| `AutoClicker.cs`, `Overlay.cs`, `ColorPicker.cs` | Auto clicker, calibration overlay, color picker |
| `Updater.cs` | Update check and download from GitHub Releases |
| `Setup.cs` | Windows installer / uninstaller (`/update` installs quietly) |
| `crossplatform/` | Mac/Linux app (Qt) and its installers |
