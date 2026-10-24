// Embris - a small configurable key/click macro for Roblox (or any app).
// Build: build.bat (uses the C# compiler that ships with Windows). UI controls live in Ui.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Embris
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public InputUnion u; }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO { public uint cbSize, dwTime; }

        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize, flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public RECT rcCaret;
        }

        public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_SCANCODE = 0x8;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4, MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10;
        public const uint WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_MOUSEMOVE = 0x200;
        public const uint WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205;

        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);
        [DllImport("user32.dll")] public static extern IntPtr ChildWindowFromPointEx(IntPtr hWnd, POINT pt, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
        [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
        [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);

        // Milliseconds since the last keyboard or mouse input anywhere on the system.
        public static uint IdleMs()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref info)) return 0;
            return unchecked((uint)Environment.TickCount - info.dwTime);
        }

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

        // True while any mouse button or keyboard key is physically held down (e.g. mid-drag).
        public static bool AnyInputHeld()
        {
            for (int vk = 0x01; vk <= 0xFE; vk++)
                if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
            return false;
        }
    }

    static class Input
    {
        static readonly HashSet<Keys> Extended = new HashSet<Keys> {
            Keys.Up, Keys.Down, Keys.Left, Keys.Right, Keys.Insert, Keys.Delete, Keys.Home, Keys.End,
            Keys.PageUp, Keys.PageDown, Keys.RControlKey, Keys.RMenu, Keys.Divide, Keys.NumLock
        };

        static void Send(Native.INPUT input)
        {
            Native.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        // Roblox reads scan codes, so send those rather than virtual-key codes.
        public static void Key(Keys key, bool up)
        {
            var inp = new Native.INPUT { type = Native.INPUT_KEYBOARD };
            inp.u.ki.wScan = (ushort)Native.MapVirtualKey((uint)key, 0);
            inp.u.ki.dwFlags = Native.KEYEVENTF_SCANCODE;
            if (Extended.Contains(key)) inp.u.ki.dwFlags |= Native.KEYEVENTF_EXTENDEDKEY;
            if (up) inp.u.ki.dwFlags |= Native.KEYEVENTF_KEYUP;
            Send(inp);
        }

        [DllImport("user32.dll")] static extern short VkKeyScan(char ch);

        // Types text with real key presses (Shift where needed), which games accept more readily than
        // pasted characters; characters with no key on the keyboard are sent as Unicode.
        public static void TypeText(string text)
        {
            foreach (char c in text)
            {
                short vk = VkKeyScan(c);
                if (vk == -1 || (vk & 0x600) != 0) // no key, or needs Ctrl/Alt
                {
                    var d = new Native.INPUT { type = Native.INPUT_KEYBOARD };
                    d.u.ki.wScan = c;
                    d.u.ki.dwFlags = 0x0004; // KEYEVENTF_UNICODE
                    var u = d;
                    u.u.ki.dwFlags = 0x0004 | Native.KEYEVENTF_KEYUP;
                    Native.SendInput(2, new[] { d, u }, Marshal.SizeOf(typeof(Native.INPUT)));
                }
                else
                {
                    bool shift = (vk & 0x100) != 0;
                    if (shift) Key(Keys.ShiftKey, false);
                    Key((Keys)(vk & 0xFF), false);
                    Key((Keys)(vk & 0xFF), true);
                    if (shift) Key(Keys.ShiftKey, true);
                }
                Thread.Sleep(6);
            }
        }

        public static void PostText(IntPtr hWnd, string text)
        {
            var gti = new Native.GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
            IntPtr dest = hWnd;
            if (Native.GetGUIThreadInfo(Native.GetWindowThreadProcessId(hWnd, IntPtr.Zero), ref gti) && gti.hwndFocus != IntPtr.Zero)
                dest = gti.hwndFocus;
            foreach (char c in text) Native.PostMessage(dest, 0x0102, (IntPtr)c, (IntPtr)1); // WM_CHAR
        }

        public static void MouseButton(bool right, bool up)
        {
            var inp = new Native.INPUT { type = Native.INPUT_MOUSE };
            inp.u.mi.dwFlags = right ? (up ? Native.MOUSEEVENTF_RIGHTUP : Native.MOUSEEVENTF_RIGHTDOWN)
                                     : (up ? Native.MOUSEEVENTF_LEFTUP : Native.MOUSEEVENTF_LEFTDOWN);
            Send(inp);
        }

        // Any mouse button: 0 left, 1 right, 2 middle.
        public static void Button(int button, bool up)
        {
            var inp = new Native.INPUT { type = Native.INPUT_MOUSE };
            inp.u.mi.dwFlags = button == 2 ? (up ? 0x40u : 0x20u) : button == 1 ? (up ? Native.MOUSEEVENTF_RIGHTUP : Native.MOUSEEVENTF_RIGHTDOWN)
                             : (up ? Native.MOUSEEVENTF_LEFTUP : Native.MOUSEEVENTF_LEFTDOWN);
            Send(inp);
        }

        // Relative movement: games that read raw mouse input (like Roblox's camera) see it as real movement.
        public static void MoveBy(int dx, int dy)
        {
            var inp = new Native.INPUT { type = Native.INPUT_MOUSE };
            inp.u.mi.dx = dx;
            inp.u.mi.dy = dy;
            inp.u.mi.dwFlags = 0x0001; // MOUSEEVENTF_MOVE
            Send(inp);
        }

        public static void Click(bool right, int holdMs)
        {
            MouseButton(right, false);
            Thread.Sleep(holdMs);
            MouseButton(right, true);
        }

        public static bool Focus(IntPtr hWnd)
        {
            if (Native.IsIconic(hWnd)) Native.ShowWindow(hWnd, 9); // SW_RESTORE
            // Tapping Alt lets a background app take the foreground.
            Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
            Native.keybd_event(0x12, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            return Native.SetForegroundWindow(hWnd);
        }

        // Background mode: post window messages straight to the target, no focus change.
        // Works for most normal apps; many games (including Roblox) ignore these.
        public static void PostKeyEvent(IntPtr hWnd, Keys key, bool up)
        {
            var gti = new Native.GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
            IntPtr dest = hWnd;
            if (Native.GetGUIThreadInfo(Native.GetWindowThreadProcessId(hWnd, IntPtr.Zero), ref gti) && gti.hwndFocus != IntPtr.Zero)
                dest = gti.hwndFocus;

            uint scan = Native.MapVirtualKey((uint)key, 0);
            long lParam = 1 | (scan << 16) | (Extended.Contains(key) ? 1L << 24 : 0);
            if (up) lParam |= 0xC0000000L;
            Native.PostMessage(dest, up ? Native.WM_KEYUP : Native.WM_KEYDOWN, (IntPtr)(int)key, (IntPtr)lParam);
        }

        public static void PostKey(IntPtr hWnd, Keys key, int holdMs)
        {
            PostKeyEvent(hWnd, key, false);
            Thread.Sleep(holdMs);
            PostKeyEvent(hWnd, key, true);
        }

        public static void PostMouseEvent(IntPtr hWnd, int screenX, int screenY, bool right, bool up)
        {
            var pt = new Native.POINT { X = screenX, Y = screenY };
            Native.ScreenToClient(hWnd, ref pt);
            IntPtr dest = hWnd;
            IntPtr child = Native.ChildWindowFromPointEx(hWnd, pt, 1 | 2); // skip invisible/disabled
            if (child != IntPtr.Zero && child != hWnd)
            {
                dest = child;
                pt = new Native.POINT { X = screenX, Y = screenY };
                Native.ScreenToClient(child, ref pt);
            }
            var lParam = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF));
            if (up)
            {
                Native.PostMessage(dest, right ? Native.WM_RBUTTONUP : Native.WM_LBUTTONUP, IntPtr.Zero, lParam);
                return;
            }
            Native.PostMessage(dest, Native.WM_MOUSEMOVE, IntPtr.Zero, lParam);
            Native.PostMessage(dest, right ? Native.WM_RBUTTONDOWN : Native.WM_LBUTTONDOWN, (IntPtr)(right ? 2 : 1), lParam); // MK_RBUTTON / MK_LBUTTON
        }

        public static void PostClick(IntPtr hWnd, int screenX, int screenY, bool right, int holdMs)
        {
            PostMouseEvent(hWnd, screenX, screenY, right, false);
            Thread.Sleep(holdMs);
            PostMouseEvent(hWnd, screenX, screenY, right, true);
        }
    }

    // One step of a recorded or built sequence. Kind: 0 key down, 1 key up, 2 mouse down, 3 mouse up, 4 wait,
    // 5 relative mouse move (X, Y hold the distance; used for turning the camera).
    // Code is the key, or 0 = left / 1 = right mouse button. Delay is ms to wait before the step.
    class RecStep
    {
        public const int NoPosition = int.MinValue; // click wherever the cursor is
        public int Delay, Kind, Code, X, Y;
        public string Text; // Kind 6: text to type

        public bool IsDown { get { return Kind == 0 || Kind == 2; } }

        public string Describe()
        {
            if (Kind == 4) return "Wait " + Delay + " ms";
            if (Kind == 5) return "Move mouse " + (X >= 0 ? "+" : "") + X + ", " + (Y >= 0 ? "+" : "") + Y;
            if (Kind == 6) return "Type \"" + Text + "\"";
            string updown = IsDown ? "down" : "up";
            if (Kind <= 1) return "Key " + Ui.KeyName((Keys)Code) + " " + updown;
            return (Code == 1 ? "Right" : "Left") + " click " + updown + (X == NoPosition ? "" : " at " + X + ", " + Y);
        }

        public static string Serialize(List<RecStep> steps)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in steps)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(s.Delay).Append(':').Append(s.Kind).Append(':').Append(s.Code).Append(':').Append(s.X).Append(':').Append(s.Y);
                if (s.Text != null) sb.Append(':').Append(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s.Text)));
            }
            return sb.ToString();
        }

        public static List<RecStep> Parse(string text)
        {
            var list = new List<RecStep>();
            try
            {
                foreach (var part in text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var f = part.Split(':');
                    list.Add(new RecStep { Delay = int.Parse(f[0]), Kind = int.Parse(f[1]), Code = int.Parse(f[2]), X = int.Parse(f[3]), Y = int.Parse(f[4]),
                        Text = f.Length > 5 ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(f[5])) : null });
                }
            }
            catch { list.Clear(); }
            return list;
        }
    }

    class Settings
    {
        // Macro settings (these are what presets store).
        public Keys ActionKey = Keys.Space;
        public bool PressKey = true;
        public int HoldMs = 50;
        public decimal IntervalSec = 60;
        public bool ClickEnabled = false;
        public bool RightClick = false;
        public int ClickX = 0, ClickY = 0;
        public string TargetProcess = "RobloxPlayerBeta";
        public int Mode = 0; // 0 = background (no switching), 1 = switch to it briefly
        public bool WaitIdle = true;
        public decimal IdleSec = 1.5m;
        public bool UseRecording = false;
        public double Speed = 1;  // playback speed of the sequence (2 = twice as fast)
        public int Repeat = 1;    // times the sequence plays each run
        public string Recording = "";

        // App settings.
        public Keys Hotkey = Keys.F6;
        public Keys RecordHotkey = Keys.F8;

        // Auto clicker.
        public Keys ClickHotkey = Keys.F7;
        public Keys OverlayHotkey = Keys.F10; // shows / hides the calibration overlay
        public int GhostOpacity = 25;         // % : how visible the start-view ghost is
        public bool CustomCursor = false;     // calibration overlay: show the uploaded cursor instead of the crosshair
        public int CursorSize = 32;           // its size in pixels
        public decimal ClickIntervalMs = 100;
        public int ClickButton = 0;          // 0 left, 1 right, 2 middle
        public bool ClickDouble = false;
        public int ClickTimes = 0;           // 0 = until stopped
        public bool ClickFixed = false;
        public int ClickAtX = 0, ClickAtY = 0;
        public bool Pinned = false;
        public bool CloseToTray = true;
        public bool SidebarCollapsed = false;
        public string BgFile = "", BgSource = "";
        public int BgDim = 55;
        public int BgDecoder = 0;   // 0 graphics card (Direct3D 11), 1 processor
        public int BgFps = 30;      // background video frame rate cap
        public int BgGlass = 1;     // see-through cards: 0 always, 1 only when the window is small, 2 never
        public bool SnapToKeys = true; // timeline: bars catch other bars' edges
        public bool AutoSaveRecordings = false; // every recording also goes into the macro suite
        public bool BindsOn = true;   // key binds active
        public string Binds = "";     // key binds (see KeyBind.SaveAll)
        public int FrameSync = 0;   // playback lines inputs up with the game's frames: 0 off, -1 the display's refresh rate, else that frame rate
        public string BgCard = "";  // graphics card for the video ("" = Windows' default)
        public int WindowOpacity = 100; // % (lower lets you see what's behind Embris)
        public string ThemeName = "Ember";
        public string ThemeColors = ""; // bg,surface,accent,text,border as hex

        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        public static string Dir
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string dir = Path.Combine(appData, "Embris");
                Directory.CreateDirectory(dir);
                // Carry over settings saved before the app was renamed.
                string old = Path.Combine(appData, "RobloxMacro", "settings.ini");
                string file = Path.Combine(dir, "settings.ini");
                if (!File.Exists(file) && File.Exists(old)) File.Copy(old, file);
                return dir;
            }
        }

        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public Theme GetTheme()
        {
            try
            {
                var c = ThemeColors.Split(',');
                if (ThemeName == "Midnight") return Theme.Default; // old default, now replaced by Ember
                if (c.Length == 5) return new Theme(ThemeName, c[0], c[1], c[2], c[3], c[4]);
            }
            catch { }
            return Theme.Default;
        }

        public void SetTheme(Theme t)
        {
            ThemeName = t.Name;
            ThemeColors = string.Join(",", new[] { Ui.Hex(t.Bg), Ui.Hex(t.Surface), Ui.Hex(t.Accent), Ui.Hex(t.Text), Ui.Hex(t.Border) });
        }

        public List<string> MacroLines()
        {
            return new List<string> {
                "ActionKey=" + (int)ActionKey, "PressKey=" + PressKey, "HoldMs=" + HoldMs,
                "IntervalSec=" + IntervalSec.ToString(Inv), "ClickEnabled=" + ClickEnabled, "RightClick=" + RightClick,
                "ClickX=" + ClickX, "ClickY=" + ClickY, "TargetProcess=" + TargetProcess, "Mode=" + Mode,
                "WaitIdle=" + WaitIdle, "IdleSec=" + IdleSec.ToString(Inv),
                "UseRecording=" + UseRecording, "Speed=" + Speed.ToString("R", Inv), "Repeat=" + Repeat, "Recording=" + Recording
            };
        }

        public void Save()
        {
            var lines = MacroLines();
            lines.AddRange(new[] { "Hotkey=" + (int)Hotkey, "RecordHotkey=" + (int)RecordHotkey, "Pinned=" + Pinned, "CloseToTray=" + CloseToTray, "SidebarCollapsed=" + SidebarCollapsed, "BgFile=" + BgFile, "BgSource=" + BgSource, "BgDim=" + BgDim, "BgDecoder=" + BgDecoder, "BgFps=" + BgFps, "BgGlass=" + BgGlass, "BgCard=" + BgCard, "SnapToKeys=" + SnapToKeys, "AutoSaveRecordings=" + AutoSaveRecordings, "FrameSync=" + FrameSync, "BindsOn=" + BindsOn, "Binds=" + Binds, "WindowOpacity=" + WindowOpacity, "ThemeName=" + ThemeName, "ThemeColors=" + ThemeColors,
                "ClickHotkey=" + (int)ClickHotkey, "OverlayHotkey=" + (int)OverlayHotkey, "GhostOpacity=" + GhostOpacity, "CustomCursor=" + CustomCursor, "CursorSize=" + CursorSize, "ClickIntervalMs=" + ClickIntervalMs.ToString(Inv), "ClickButton=" + ClickButton, "ClickDouble=" + ClickDouble,
                "ClickTimes=" + ClickTimes, "ClickFixed=" + ClickFixed, "ClickAtX=" + ClickAtX, "ClickAtY=" + ClickAtY });
            File.WriteAllLines(FilePath, lines);
        }

        public static Dictionary<string, string> ParseLines(IEnumerable<string> lines)
        {
            var d = new Dictionary<string, string>();
            foreach (var line in lines)
            {
                int i = line.IndexOf('=');
                if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1);
            }
            return d;
        }

        // Applies whichever keys are present; used for both the settings file and presets.
        public void Apply(Dictionary<string, string> d)
        {
            try
            {
                if (d.ContainsKey("ActionKey")) ActionKey = (Keys)int.Parse(d["ActionKey"]);
                if (d.ContainsKey("PressKey")) PressKey = bool.Parse(d["PressKey"]);
                if (d.ContainsKey("HoldMs")) HoldMs = int.Parse(d["HoldMs"]);
                if (d.ContainsKey("IntervalSec")) IntervalSec = decimal.Parse(d["IntervalSec"], Inv);
                if (d.ContainsKey("ClickEnabled")) ClickEnabled = bool.Parse(d["ClickEnabled"]);
                if (d.ContainsKey("RightClick")) RightClick = bool.Parse(d["RightClick"]);
                if (d.ContainsKey("ClickX")) ClickX = int.Parse(d["ClickX"]);
                if (d.ContainsKey("ClickY")) ClickY = int.Parse(d["ClickY"]);
                if (d.ContainsKey("TargetProcess") && d["TargetProcess"] != "") TargetProcess = d["TargetProcess"];
                if (d.ContainsKey("Mode")) Mode = Math.Max(0, Math.Min(1, int.Parse(d["Mode"])));
                if (d.ContainsKey("WaitIdle")) WaitIdle = bool.Parse(d["WaitIdle"]);
                if (d.ContainsKey("IdleSec")) IdleSec = decimal.Parse(d["IdleSec"], Inv);
                if (d.ContainsKey("UseRecording")) UseRecording = bool.Parse(d["UseRecording"]);
                if (d.ContainsKey("SpeedIndex")) Speed = d["SpeedIndex"] == "0" ? 0.5 : d["SpeedIndex"] == "2" ? 2 : 1; // older versions
                if (d.ContainsKey("Speed")) Speed = ClampSpeed(double.Parse(d["Speed"], Inv));
                if (d.ContainsKey("Repeat")) Repeat = Math.Max(1, int.Parse(d["Repeat"]));
                if (d.ContainsKey("Recording")) Recording = d["Recording"];
                if (d.ContainsKey("Hotkey")) Hotkey = (Keys)int.Parse(d["Hotkey"]);
                if (d.ContainsKey("RecordHotkey")) RecordHotkey = (Keys)int.Parse(d["RecordHotkey"]);
                if (d.ContainsKey("Pinned")) Pinned = bool.Parse(d["Pinned"]);
                if (d.ContainsKey("CloseToTray")) CloseToTray = bool.Parse(d["CloseToTray"]);
                if (d.ContainsKey("SidebarCollapsed")) SidebarCollapsed = bool.Parse(d["SidebarCollapsed"]);
                if (d.ContainsKey("BgFile")) BgFile = d["BgFile"];
                if (d.ContainsKey("BgSource")) BgSource = d["BgSource"];
                if (d.ContainsKey("BgDim")) BgDim = Math.Max(0, Math.Min(100, int.Parse(d["BgDim"])));
                if (d.ContainsKey("BgDecoder")) BgDecoder = Math.Max(0, Math.Min(1, int.Parse(d["BgDecoder"])));
                if (d.ContainsKey("BgFps")) BgFps = Math.Max(10, Math.Min(60, int.Parse(d["BgFps"])));
                if (d.ContainsKey("BgGlass")) BgGlass = Math.Max(0, Math.Min(2, int.Parse(d["BgGlass"])));
                if (d.ContainsKey("BgCard")) BgCard = d["BgCard"];
                if (d.ContainsKey("SnapToKeys")) SnapToKeys = bool.Parse(d["SnapToKeys"]);
                if (d.ContainsKey("AutoSaveRecordings")) AutoSaveRecordings = bool.Parse(d["AutoSaveRecordings"]);
                if (d.ContainsKey("BindsOn")) BindsOn = bool.Parse(d["BindsOn"]);
                if (d.ContainsKey("Binds")) Binds = d["Binds"];
                if (d.ContainsKey("FrameSync")) FrameSync = Math.Max(-1, Math.Min(1000, int.Parse(d["FrameSync"])));
                if (d.ContainsKey("WindowOpacity")) WindowOpacity = Math.Max(20, Math.Min(100, int.Parse(d["WindowOpacity"])));
                if (d.ContainsKey("ThemeName")) ThemeName = d["ThemeName"];
                if (d.ContainsKey("ThemeColors")) ThemeColors = d["ThemeColors"];
                if (d.ContainsKey("ClickHotkey")) ClickHotkey = (Keys)int.Parse(d["ClickHotkey"]);
                if (d.ContainsKey("OverlayHotkey")) OverlayHotkey = (Keys)int.Parse(d["OverlayHotkey"]);
                if (d.ContainsKey("GhostOpacity")) GhostOpacity = Math.Max(0, Math.Min(100, int.Parse(d["GhostOpacity"])));
                if (d.ContainsKey("CustomCursor")) CustomCursor = bool.Parse(d["CustomCursor"]);
                if (d.ContainsKey("CursorSize")) CursorSize = Math.Max(4, Math.Min(512, int.Parse(d["CursorSize"])));
                if (d.ContainsKey("ClickIntervalMs")) ClickIntervalMs = Math.Max(0, decimal.Parse(d["ClickIntervalMs"], Inv));
                if (d.ContainsKey("ClickButton")) ClickButton = Math.Max(0, Math.Min(2, int.Parse(d["ClickButton"])));
                if (d.ContainsKey("ClickDouble")) ClickDouble = bool.Parse(d["ClickDouble"]);
                if (d.ContainsKey("ClickTimes")) ClickTimes = Math.Max(0, int.Parse(d["ClickTimes"]));
                if (d.ContainsKey("ClickFixed")) ClickFixed = bool.Parse(d["ClickFixed"]);
                if (d.ContainsKey("ClickAtX")) ClickAtX = int.Parse(d["ClickAtX"]);
                if (d.ContainsKey("ClickAtY")) ClickAtY = int.Parse(d["ClickAtY"]);
            }
            catch { }
        }

        public static double ClampSpeed(double s) { return double.IsNaN(s) || s <= 0 ? 1 : Math.Min(s, 1e6); }

        public static Settings Load()
        {
            var s = new Settings();
            try { if (File.Exists(FilePath)) s.Apply(ParseLines(File.ReadAllLines(FilePath))); }
            catch { }
            return s;
        }
    }

    class Preset
    {
        public string Name;
        public List<string> Lines;

        // Besides macro settings, a preset can keep its own hotkey ("PresetHotkey=") and hotstrings
        // ("HotstringWindow=", "HotstringToggle=", "Hotstring=abbreviation<TAB>replacement").
        public static bool IsExtra(string line)
        {
            return line.StartsWith("PresetHotkey=") || line.StartsWith("Hotstring");
        }

        public string Get(string key)
        {
            foreach (var l in Lines) if (l.StartsWith(key + "=")) return l.Substring(key.Length + 1);
            return null;
        }

        public void Set(string key, string value)
        {
            Lines.RemoveAll(l => l.StartsWith(key + "="));
            if (value != null) Lines.Add(key + "=" + value);
        }

        public Keys Hotkey
        {
            get { int v; return int.TryParse(Get("PresetHotkey"), out v) ? (Keys)v : Keys.None; }
            set { Set("PresetHotkey", value == Keys.None ? null : ((int)value).ToString()); }
        }

        public List<KeyValuePair<string, string>> Hotstrings
        {
            get
            {
                var list = new List<KeyValuePair<string, string>>();
                foreach (var l in Lines)
                {
                    if (!l.StartsWith("Hotstring=")) continue;
                    int tab = l.IndexOf('\t');
                    if (tab > 10) list.Add(new KeyValuePair<string, string>(l.Substring(10, tab - 10), l.Substring(tab + 1)));
                }
                return list;
            }
        }
    }

    // Saved macros live in presets.ini as [Name] sections of settings lines.
    static class PresetStore
    {
        static string FilePath { get { return Path.Combine(Settings.Dir, "presets.ini"); } }

        public static List<Preset> Load()
        {
            if (!File.Exists(FilePath)) return Defaults();
            var list = new List<Preset>();
            Preset current = null;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    current = new Preset { Name = line.Substring(1, line.Length - 2), Lines = new List<string>() };
                    list.Add(current);
                }
                else if (current != null && line.Contains("=")) current.Lines.Add(line);
            }
            return list;
        }

        public static void Save(List<Preset> presets)
        {
            var lines = new List<string>();
            foreach (var p in presets)
            {
                lines.Add("[" + p.Name + "]");
                lines.AddRange(p.Lines);
            }
            File.WriteAllLines(FilePath, lines);
        }

        static List<Preset> Defaults()
        {
            var afk = new Settings { Mode = 1, WaitIdle = true, IdleSec = 3, PressKey = true, ActionKey = Keys.Space, IntervalSec = 900 };
            var jump = new Settings { Mode = 1, WaitIdle = false, PressKey = true, ActionKey = Keys.Space, IntervalSec = 1 };
            return new List<Preset> {
                new Preset { Name = "Anti-AFK (Space every 15 min)", Lines = afk.MacroLines() },
                new Preset { Name = "Jump every second", Lines = jump.MacroLines() }
            };
        }
    }

    class TargetItem
    {
        public string ProcessName, Title;
        public override string ToString()
        {
            return Title == null ? ProcessName + "  (not running)" : ProcessName + "  -  " + Title;
        }
    }

    // Low-level keyboard and mouse hooks on their own thread. They drive the global start/stop
    // hotkey (telling numpad keys apart from Home/End/arrows) and capture input while recording.
    // Running on a separate thread means the macro's own pauses never delay other apps' input.
    class HotkeyHook
    {
        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT { public Native.POINT pt; public uint mouseData, flags, time; public IntPtr extra; }

        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        public volatile int KeyCode, RecordKeyCode, ClickKeyCode, OverlayKeyCode;
        bool clickIsDown, overlayIsDown;
        public event Action OverlayPressed; // the calibration overlay's hotkey (hook thread)
        public event Action ClickPressed; // the auto clicker's hotkey (hook thread)
        public volatile bool Paused;
        public volatile BindEngine Binds; // key binds, run from this hook
        public event Action Pressed, RecordPressed; // raised on the hook thread
        public volatile int[] SuiteKeys = new int[0]; // hotkeys of macros in the suite
        public volatile HotstringSet[] HotstringSets = new HotstringSet[0];
        public event Action<HotstringSet> HotstringToggled;
        readonly System.Text.StringBuilder typed = new System.Text.StringBuilder();
        readonly HashSet<int> toggleDown = new HashSet<int>();
        public event Action<int> SuitePressed;
        readonly HashSet<int> suiteDown = new HashSet<int>();
        HookProc keyProc, mouseProc;
        IntPtr keyHook, mouseHook;
        uint threadId;
        bool isDown, recIsDown;

        volatile bool recording;
        IntPtr ownWindow;
        readonly List<RecStep> steps = new List<RecStep>();
        readonly HashSet<int> heldKeys = new HashSet<int>();
        // Recording timestamps come from one high-resolution clock. Each step's delay is the
        // difference of rounded absolute times, so rounding never adds up over a long recording.
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        double startMs;
        long lastMs;
        RawMouseWindow rawMouse;

        int DelayNow()
        {
            double now = Clock.Elapsed.TotalMilliseconds;
            if (steps.Count == 0) { startMs = now; lastMs = 0; return 0; }
            long abs = (long)Math.Round(now - startMs);
            int d = (int)Math.Max(0, abs - lastMs);
            lastMs = Math.Max(lastMs, abs);
            return d;
        }

        public void Start()
        {
            var t = new Thread(() =>
            {
                threadId = GetCurrentThreadId();
                keyProc = KeyCallback;
                mouseProc = MouseCallback;
                keyHook = SetWindowsHookEx(13, keyProc, GetModuleHandle(null), 0);     // WH_KEYBOARD_LL
                mouseHook = SetWindowsHookEx(14, mouseProc, GetModuleHandle(null), 0); // WH_MOUSE_LL
                rawMouse = new RawMouseWindow(this); // camera movement, on this thread so the UI never delays it
                Application.Run();
                UnhookWindowsHookEx(keyHook);
                UnhookWindowsHookEx(mouseHook);
            });
            t.IsBackground = true;
            t.Start();
        }

        public void Stop() { PostThreadMessage(threadId, 0x12, IntPtr.Zero, IntPtr.Zero); } // WM_QUIT

        public void StartRecording(IntPtr own)
        {
            lock (steps)
            {
                steps.Clear();
                heldKeys.Clear();
                ownWindow = own;
                recording = true;
            }
        }

        public List<RecStep> StopRecording()
        {
            lock (steps)
            {
                recording = false;
                return new List<RecStep>(steps);
            }
        }

        public bool IsRecording { get { return recording; } }

        // Relative mouse movement (camera turning), one step per raw mouse report. Not merging them
        // matters: with "Enhance pointer precision" on, Windows turns a big report more than the same
        // distance in several small ones, so merged moves would replay as different camera turns.
        public void AddMove(int dx, int dy)
        {
            lock (steps)
            {
                if (!recording || Native.GetForegroundWindow() == ownWindow) return;
                steps.Add(new RecStep { Kind = 5, X = dx, Y = dy, Delay = DelayNow() });
            }
        }

        void AddStep(RecStep s)
        {
            lock (steps)
            {
                if (!recording) return;
                if (s.Kind <= 1)
                {
                    // Skip auto-repeat key downs and ups whose down happened before recording started.
                    if (s.Kind == 0 && !heldKeys.Add(s.Code)) return;
                    if (s.Kind == 1 && !heldKeys.Remove(s.Code)) return;
                }
                s.Delay = DelayNow();
                steps.Add(s);
            }
        }

        IntPtr KeyCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && !Paused)
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                bool injected = (k.flags & 0x10) != 0; // ignore keys the macro itself sends
                int key = (int)Ui.NormalizeKey((Keys)k.vkCode, (k.flags & 1) != 0);
                int msg = wParam.ToInt32();
                bool down = msg == 0x100 || msg == 0x104; // WM_KEYDOWN / WM_SYSKEYDOWN
                if (!injected && key == KeyCode)
                {
                    if (down && !isDown && Pressed != null) Pressed();
                    isDown = down;
                    return (IntPtr)1; // swallow the hotkey so it doesn't also reach the game
                }
                if (!injected && key == ClickKeyCode && key != 0)
                {
                    if (down && !clickIsDown && ClickPressed != null) ClickPressed();
                    clickIsDown = down;
                    return (IntPtr)1;
                }
                if (!injected && key == OverlayKeyCode && key != 0)
                {
                    if (down && !overlayIsDown && OverlayPressed != null) OverlayPressed();
                    overlayIsDown = down;
                    return (IntPtr)1;
                }
                var suite = SuiteKeys;
                if (!injected && Array.IndexOf(suite, key) >= 0)
                {
                    if (down && suiteDown.Add(key) && SuitePressed != null) SuitePressed(key);
                    if (!down) suiteDown.Remove(key);
                    return (IntPtr)1;
                }
                foreach (var hs in HotstringSets)
                {
                    if (injected || hs.ToggleKey == 0 || key != hs.ToggleKey) continue;
                    if (down && toggleDown.Add(key))
                    {
                        hs.On = !hs.On;
                        typed.Length = 0;
                        if (HotstringToggled != null) HotstringToggled(hs);
                    }
                    if (!down) toggleDown.Remove(key);
                    return (IntPtr)1;
                }
                if (!injected && down && HotstringSets.Length > 0) TrackTyping(key);
                if (!injected && key == RecordKeyCode)
                {
                    if (down && !recIsDown && RecordPressed != null) RecordPressed();
                    recIsDown = down;
                    return (IntPtr)1; // never part of the recording
                }
                // Key binds (spam or hold a click or key...): a blocked trigger key doesn't reach the game.
                var binds = Binds;
                if (!injected && binds != null && binds.OnKey(key, down)) return (IntPtr)1;
                if (recording && !injected && Native.GetForegroundWindow() != ownWindow)
                    AddStep(new RecStep { Kind = down ? 0 : 1, Code = key });
            }
            return CallNextHookEx(keyHook, code, wParam, lParam);
        }

        // ---------- hotstrings: type a short text and it turns into the long one ----------

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);

        static char KeyChar(Keys k)
        {
            if (k >= Keys.A && k <= Keys.Z) return (char)('a' + (k - Keys.A));
            if (k >= Keys.D0 && k <= Keys.D9) return (char)('0' + (k - Keys.D0));
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return (char)('0' + (k - Keys.NumPad0));
            switch (k)
            {
                case Keys.Space: return ' ';
                case Keys.OemMinus: case Keys.Subtract: return '-';
                case Keys.OemPeriod: case Keys.Decimal: return '.';
                case Keys.Oemcomma: return ',';
                case Keys.OemQuestion: return '/';
                case Keys.OemSemicolon: return ';';
                case Keys.OemQuotes: return '\'';
                default: return '\0';
            }
        }

        static bool IsModifier(Keys k)
        {
            return k == Keys.ShiftKey || k == Keys.LShiftKey || k == Keys.RShiftKey || k == Keys.ControlKey ||
                   k == Keys.LControlKey || k == Keys.RControlKey || k == Keys.Menu || k == Keys.LMenu ||
                   k == Keys.RMenu || k == Keys.Capital;
        }

        static bool WindowMatches(string window)
        {
            if (string.IsNullOrEmpty(window)) return true;
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(Native.GetForegroundWindow(), sb, sb.Capacity);
            return sb.ToString().IndexOf(window, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void TrackTyping(int key)
        {
            if (key == (int)Keys.Back) { if (typed.Length > 0) typed.Length--; return; }
            char c = KeyChar((Keys)key);
            if (c == '\0') { if (!IsModifier((Keys)key)) typed.Length = 0; return; }
            typed.Append(c);
            if (typed.Length > 64) typed.Remove(0, typed.Length - 64);
            string buffer = typed.ToString();
            foreach (var hs in HotstringSets)
            {
                if (hs.ToggleKey != 0 && !hs.On) continue;
                foreach (var item in hs.Items) // longest first
                {
                    if (!buffer.EndsWith(item.Key, StringComparison.OrdinalIgnoreCase) || !WindowMatches(hs.Window)) continue;
                    typed.Length = 0;
                    int erase = item.Key.Length;
                    string text = item.Value;
                    // After the hook returns, so the last typed letter has landed before it's erased.
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        Thread.Sleep(30);
                        for (int i = 0; i < erase; i++) { Input.Key(Keys.Back, false); Input.Key(Keys.Back, true); Thread.Sleep(4); }
                        Input.TypeText(text);
                    });
                    return;
                }
            }
        }

        IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && recording)
            {
                int msg = wParam.ToInt32();
                if (msg == 0x201 || msg == 0x202 || msg == 0x204 || msg == 0x205)
                {
                    var m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    bool injected = (m.flags & 1) != 0;
                    // Clicks on Embris itself (like the Stop button) aren't part of the recording.
                    if (!injected && Native.GetAncestor(Native.WindowFromPoint(m.pt), 2) != ownWindow)
                        AddStep(new RecStep
                        {
                            Kind = msg == 0x201 || msg == 0x204 ? 2 : 3,
                            Code = msg >= 0x204 ? 1 : 0,
                            X = m.pt.X, Y = m.pt.Y
                        });
                }
            }
            return CallNextHookEx(mouseHook, code, wParam, lParam);
        }
    }

    class HotstringSet
    {
        public string Name, Window;
        public int ToggleKey;         // 0 = always on
        public volatile bool On;
        public List<KeyValuePair<string, string>> Items;
    }

    // Message-only window on the hook thread that receives raw mouse input even while a game is in
    // front. Games like Roblox turn the camera from raw movement, which the mouse hook can't see
    // once the cursor is locked.
    class RawMouseWindow : NativeWindow
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
        [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

        readonly HotkeyHook hook;

        public RawMouseWindow(HotkeyHook hook)
        {
            this.hook = hook;
            CreateHandle(new CreateParams { Parent = (IntPtr)(-3) }); // HWND_MESSAGE
            var dev = new[] { new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = 0x100, hwndTarget = Handle } }; // mouse, RIDEV_INPUTSINK
            RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x00FF && hook.IsRecording) Read(m.LParam); // WM_INPUT
            base.WndProc(ref m);
        }

        void Read(IntPtr hRaw)
        {
            int header = 8 + 2 * IntPtr.Size; // RAWINPUTHEADER
            uint size = 0;
            GetRawInputData(hRaw, 0x10000003, IntPtr.Zero, ref size, (uint)header); // RID_INPUT
            if (size == 0) return;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(hRaw, 0x10000003, buf, ref size, (uint)header) != size) return;
                if (Marshal.ReadInt32(buf, 0) != 0) return;            // not RIM_TYPEMOUSE
                if ((Marshal.ReadInt16(buf, header) & 1) != 0) return; // absolute (tablets, remote desktop)
                int dx = Marshal.ReadInt32(buf, header + 12), dy = Marshal.ReadInt32(buf, header + 16);
                if (dx != 0 || dy != 0) hook.AddMove(dx, dy);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    class MainForm : Form
    {
        const string VFill = "vfill", AtBottom = "bottom", Fixed = "fixed"; // layout tags: grow with the window / stick to the bottom / keep size and place
        static readonly string[] ColorNames = { "Background", "Surface", "Accent", "Text", "Border" };
        static readonly Font Mono = new Font("Consolas", 9.5f);
        // No practical limits: holds and waits run off the UI thread, so their length doesn't matter.
        const int MaxHoldMs = int.MaxValue;       // about 24 days
        const decimal MaxIntervalSec = 2147483;   // the longest timer Windows has (about 24 days)
        const decimal MaxPos = 1000000;

        HotkeyHook hotkeyHook = new HotkeyHook();
        Settings settings = Settings.Load();
        List<RecStep> sequence;
        List<Preset> presets = PresetStore.Load();
        System.Windows.Forms.Timer actionTimer = new System.Windows.Forms.Timer();
        System.Windows.Forms.Timer pickTimer = new System.Windows.Forms.Timer();
        System.Windows.Forms.Timer cursorTimer = new System.Windows.Forms.Timer();
        System.Windows.Forms.Timer idleTimer = new System.Windows.Forms.Timer();
        volatile bool running;
        bool rightClick, builderRight, recordingNow, playing;
        int actionCount, stepType;

        int pickCountdown;
        string pickText;
        RoundButton pickButton;
        NumberBox pickX, pickY;
        Action pickDone;

        Box[] pages = new Box[11];
        NavButton[] navs = new NavButton[11];
        // Sidebar order (page numbers): the auto clicker and key binds under Macro, calibration after the editors.
        static readonly int[] NavOrder = { 0, 8, 10, 1, 2, 3, 9, 4, 5, 6, 7 };
        Box cardTarget, cardTiming, cardKey, cardClick, cardHotkey, cardSaved, cardBuilder;
        Box[] stepPanels = new Box[3];
        Dropdown cboTarget, cboMode;
        Toggle chkIdle, chkKey, chkClick, chkUseRec;
        NumberBox numIdle, numHold, numInterval, numX, numY, bHold, bX, bY, bWait;
        KeyBox keyAction, keyHotkey, keyRecordHotkey, bKey;
        RoundButton btnLeft, btnRight, btnPick, btnRun, btnRecord, btnBuilderRecord, btnPin, bLeft, bRight, bPick;
        RoundButton[] stepChips = new RoundButton[3];
        TextLabel lblCursor, lblIntervalHint, lblHint, lblBuilderSteps;
        TextField txtPresetName;
        ItemList presetList;
        StatusView status;
        ThemeTile[] tiles;
        ColorBox[] colorBoxes = new ColorBox[5];
        NotifyIcon tray;
        bool trayTipShown, exiting, skipSave;
        Toggle pinToggle;
        ToolTip tips = new ToolTip();

        Dictionary<Control, Rectangle> baseBounds;
        Size baseClient;
        bool restoreFull;

        public MainForm()
        {
            sequence = RecStep.Parse(settings.Recording);

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Embris";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(960, 640);
            Font = Ui.F(9.75f);
            Theme.Current = settings.GetTheme();

            BuildSidebar();
            BuildBottomBar();
            BuildMacroPage();
            BuildPresetsPage();
            BuildSequencePage();
            BuildTimelinePage();
            BuildScriptsPage();
            BuildHotkeyPage();
            BuildSettingsPage();
            BuildThemePage();
            BuildAutoClickerPage();
            BuildCalibrationPage();
            BuildKeyBindsPage();
            BuildMini();

            actionTimer.Tick += (s, e) => DoAction();
            playTimer.Tick += (s, e) => PlayTick();
            startViewTimer.Tick += (s, e) => StartViewTick();
            idleTimer.Interval = 200;
            idleTimer.Tick += (s, e) => IdleTick();
            pickTimer.Interval = 1000;
            pickTimer.Tick += (s, e) => PickTick();
            cursorTimer.Interval = 100;
            cursorTimer.Tick += (s, e) =>
            {
                if (!pages[0].Visible) return;
                var p = Cursor.Position;
                lblCursor.Text = "Mouse is at " + p.X + ", " + p.Y;
            };
            cursorTimer.Start();

            BuildTray();
            Resize += (s, e) =>
            {
                // Minimizing keeps Embris on the taskbar. Minimized while squished into the compact view,
                // it comes back at full size.
                if (WindowState == FormWindowState.Minimized) { if (isCompact) restoreFull = true; return; }
                if (restoreFull && WindowState == FormWindowState.Normal)
                {
                    restoreFull = false;
                    BeginInvoke((Action)ResetWindowSize);
                }
                // Dragging the window edge: only stretch the page and move the bottom bar (cheap, so the
                // window keeps up with the mouse); everything inside is laid out once you pause or let go.
                if (Background.Sizing && WindowState == FormWindowState.Normal && baseBounds != null && !isCompact)
                {
                    StretchDuringDrag();
                    settleTimer.Stop();
                    settleTimer.Start();
                    return;
                }
                RelayoutInOnePaint();
            };
            settleTimer.Tick += (s, e) => { settleTimer.Stop(); RelayoutInOnePaint(); };

            ResumeLayout(false);

            hotkeyHook.Pressed += () => { if (IsHandleCreated) BeginInvoke((Action)Toggle); };
            hotkeyHook.RecordPressed += () => { if (IsHandleCreated) BeginInvoke((Action)ToggleRecording); };
            hotkeyHook.SuitePressed += key => { if (IsHandleCreated) BeginInvoke((Action)(() => OnSuiteKey(key))); };
            hotkeyHook.HotstringToggled += hs => { if (IsHandleCreated) BeginInvoke((Action)(() => OnHotstringToggled(hs))); };
            hotkeyHook.KeyCode = (int)settings.Hotkey;
            hotkeyHook.RecordKeyCode = (int)settings.RecordHotkey;
            hotkeyHook.ClickKeyCode = (int)settings.ClickHotkey;
            hotkeyHook.OverlayKeyCode = (int)settings.OverlayHotkey;
            hotkeyHook.OverlayPressed += () => { if (IsHandleCreated) BeginInvoke((Action)ToggleOverlay); };
            // Typing a number (say a 7 on the numpad) shouldn't trigger a hotkey.
            NumberBox.Editing += on => hotkeyHook.Paused = on;
            hotkeyHook.ClickPressed += () => { if (IsHandleCreated) BeginInvoke((Action)ToggleClicker); };
            // Key binds run from the keyboard hook.
            binds = KeyBind.ParseAll(settings.Binds);
            bindEngine.Enabled = settings.BindsOn;
            bindEngine.GameInFront = GameInFront;
            bindEngine.PlayMacro += (name, cmd) => { if (IsHandleCreated) BeginInvoke((Action)(() => BindMacro(name, cmd))); };
            HandleCreated += (s, e) => bindEngine.OwnWindow = Handle;
            PushBinds();
            hotkeyHook.Binds = bindEngine;
            hotkeyHook.Start();

            Theme.Changed += OnThemeChanged;
            ApplyChrome();
            ApplyPin();
            ShowPage(0);
            RefreshTargets();
            RefreshPresetList();
            UpdateIdleEnabled();
            UpdateSequenceEnabled();
            UpdateSequenceViews();
            UpdateStatus();
            ResetUndo();
        }

        // ---------- layout helpers (coordinates are at 96 DPI; the form scales them) ----------

        static T Add<T>(Control parent, T c, int x, int y, int w, int h) where T : Control
        {
            c.SetBounds(x, y, w, h);
            parent.Controls.Add(c);
            return c;
        }

        static TextLabel Label(Control parent, string text, int x, int y, int w)
        {
            return Add(parent, new TextLabel { Text = text, Sub = true, Font = Ui.F(9f) }, x, y, w, 20);
        }

        static Box Card(Control parent, string title, int x, int y, int w, int h)
        {
            return Add(parent, new Box(BoxRole.Card) { Title = title }, x, y, w, h);
        }

        static NumberBox Num(Control parent, decimal min, decimal max, int decimals, decimal step, decimal value, int x, int y, int w)
        {
            var n = Add(parent, new NumberBox { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = step }, x, y, w, 34);
            n.Value = value;
            return n;
        }

        static T Tagged<T>(T c, string tag) where T : Control
        {
            c.Tag = tag;
            return c;
        }

        Box Page(int i, string title, string subtitle)
        {
            var p = Add(this, new Box(BoxRole.Page), 220, 0, 740, 556);
            Add(p, new TextLabel { Text = title, Font = Ui.Semi(20f) }, 32, 20, 676, 42);
            Add(p, new TextLabel { Text = subtitle, Sub = true }, 32, 62, 676, 22);
            pages[i] = p;
            return p;
        }

        // ---------- resizing from the corners ----------

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Remember the designed layout (already DPI-scaled); every resize is computed from it.
            baseClient = ClientSize;
            baseBounds = new Dictionary<Control, Rectangle>();
            CaptureBounds(this);
            // Smaller than the designed size switches to the compact view.
            MinimumSize = SizeFromClientSize(new Size(Ui.S(280), Ui.S(54)));
            Relayout(); // applies a collapsed sidebar from last time
            Background.Attach(this);
            Backdrop.Retinted += () => Invalidate(true);
            Background.Failed += msg => { lblBg.TextColor = Ui.Danger; lblBg.Text = msg; SetStatus(StatusKind.Problem, "Background video won't play", msg); };
            Backdrop.Dim = settings.BgDim;
            Background.UseGpu = settings.BgDecoder == 0;
            Background.Card = settings.BgCard;
            Background.Fps = settings.BgFps;
            Opacity = settings.WindowOpacity / 100.0;
            ApplyGlass();
            if (settings.BgFile != "") SetBackground(settings.BgFile, settings.BgSource);
            // Look for a new version a few seconds after start-up, out of the way.
            var updateTimer = new System.Windows.Forms.Timer { Interval = 4000 };
            updateTimer.Tick += (s, ev) => { updateTimer.Dispose(); CheckForUpdates(false); };
            updateTimer.Start();
            sideFooter2.Click += (s, ev) => { if (newRelease != null) ShowPage(6); };
        }

        void CaptureBounds(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                baseBounds[c] = c.Bounds;
                if (c is Box) CaptureBounds(c);
            }
        }

        void Relayout()
        {
            if (baseBounds == null) return;
            SuspendLayout();
            int shift = Ui.S(220) - SidebarWidth; // room freed by a collapsed sidebar
            bool compact = ClientSize.Width + shift < baseClient.Width || ClientSize.Height < baseClient.Height;
            if (compact != isCompact)
            {
                isCompact = compact;
                foreach (Control c in Controls) c.Visible = c == mini ? compact : !compact;
                if (!compact) ShowPage(currentPage);
                else SyncMini();
            }
            if (compact)
            {
                mini.BringToFront();
                LayoutMini();
                ResumeLayout();
                return;
            }
            foreach (Control c in Controls)
            {
                var box = c as Box;
                if (box == null || box == mini || !baseBounds.ContainsKey(c)) continue;
                if (box.Role == BoxRole.Sidebar) { LayoutSidebar(); continue; }
                // Hidden pages are laid out when they're opened: resizing only moves what you can see.
                if (box.Role == BoxRole.Page && !box.Visible) { stalePages.Add(box); continue; }
                PlaceBox(box);
            }
            ResumeLayout();
            UpdateSequenceViews();
            ApplyGlass();
        }

        readonly HashSet<Box> stalePages = new HashSet<Box>(); // pages not yet laid out for the current window size
        readonly System.Windows.Forms.Timer settleTimer = new System.Windows.Forms.Timer { Interval = 120 };

        // While the edge is dragged: the open page and the bottom bar follow the window, nothing else moves.
        void StretchDuringDrag()
        {
            int shift = Ui.S(220) - SidebarWidth;
            int dW = ClientSize.Width - baseClient.Width, dH = ClientSize.Height - baseClient.Height;
            // Shrinking below the normal size switches to the compact view: do the full layout for that.
            if (ClientSize.Width + shift < baseClient.Width || ClientSize.Height < baseClient.Height) { RelayoutInOnePaint(); return; }
            SuspendLayout();
            foreach (Control c in Controls)
            {
                var box = c as Box;
                Rectangle b;
                if (box == null || !box.Visible || box == mini || !baseBounds.TryGetValue(c, out b)) continue;
                if (box.Role == BoxRole.Sidebar) LayoutSidebar();
                else if (box.Role == BoxRole.Bar) box.SetBounds(b.X - shift, b.Y + dH, b.Width + dW + shift, b.Height);
                else if (box.Role == BoxRole.Page) box.SetBounds(b.X - shift, b.Y, b.Width + dW + shift, b.Height + dH);
            }
            ResumeLayout(false);
        }

        // Fits a page or the bottom bar (and everything in it) to the window.
        void PlaceBox(Box box)
        {
            Rectangle b = baseBounds[box];
            int shift = Ui.S(220) - SidebarWidth;
            int dW = ClientSize.Width - baseClient.Width, dH = ClientSize.Height - baseClient.Height;
            if (box.Role == BoxRole.Bar) box.SetBounds(b.X - shift, b.Y + dH, b.Width + dW + shift, b.Height);
            else box.SetBounds(b.X - shift, b.Y, b.Width + dW + shift, b.Height + dH);
            LayoutChildren(box);
            stalePages.Remove(box);
        }

        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);

        // Resizing: move everything with drawing switched off, then paint the window once. Without
        // this each control repaints as it moves and the window flickers and lags behind the mouse.
        void RelayoutInOnePaint()
        {
            if (!IsHandleCreated || !Visible) { Relayout(); return; }
            SendMessage(Handle, 0x0B, IntPtr.Zero, IntPtr.Zero); // WM_SETREDRAW off
            try { Relayout(); }
            finally
            {
                SendMessage(Handle, 0x0B, (IntPtr)1, IntPtr.Zero);
                RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x1 | 0x4 | 0x80 | 0x100 | 0x400); // invalidate, erase, all children, now, frame
            }
        }

        // See-through cards look best but every control on them is redrawn with each video frame;
        // in a big window that gets heavy, so "only when small" switches to solid cards there.
        void ApplyGlass()
        {
            bool solid = settings.BgGlass == 2 || (settings.BgGlass == 1 && (long)ClientSize.Width * ClientSize.Height > (long)Ui.S(1400) * Ui.S(900));
            if (solid == Backdrop.SolidCards) return;
            Backdrop.SolidCards = solid;
            Backdrop.Retint();
            Invalidate(true);
        }

        // Rows stay put; horizontally children stretch in proportion so side-by-side columns share new space.
        void LayoutChildren(Box parent)
        {
            Rectangle pb = baseBounds[parent];
            int pad = parent.Role == BoxRole.Card ? Ui.S(18) : parent.Role == BoxRole.Page ? Ui.S(32) : parent.Role == BoxRole.Inner ? 0 : -1;
            int dW = parent.Width - pb.Width, dH = parent.Height - pb.Height;
            double k = pad < 0 ? 1 : (parent.Width - 2.0 * pad) / (pb.Width - 2.0 * pad);
            foreach (Control c in parent.Controls)
            {
                Rectangle b;
                if (!baseBounds.TryGetValue(c, out b)) continue;
                int x = b.X, y = b.Y, w = b.Width, h = b.Height;
                string tag = c.Tag as string;
                if (pad >= 0 && tag != Fixed)
                {
                    x = pad + (int)Math.Round((b.X - pad) * k);
                    w = pad + (int)Math.Round((b.Right - pad) * k) - x;
                }
                else if (parent.Role == BoxRole.Bar && (c is StatusView || c is TextLabel)) w += dW;
                if (tag == VFill) h += dH;
                else if (tag == AtBottom) y += dH;
                c.SetBounds(x, y, w, h);
                var child = c as Box;
                if (child != null) LayoutChildren(child);
            }
        }

        // ---------- sidebar & bottom bar ----------

        void BuildSidebar()
        {
            var side = sidebar = Add(this, new Box(BoxRole.Sidebar), 0, 0, 220, 640);
            sideLogo = Add(side, new Logo { Text = "Embris" }, 20, 20, 110, 40);
            btnCollapse = Add(side, new RoundButton { Style = BtnStyle.Chip, MenuIcon = true, Text = "" }, 134, 24, 34, 32);
            btnCollapse.Click += (s, e) => { settings.SidebarCollapsed = !settings.SidebarCollapsed; settings.Save(); Relayout(); };
            btnPin = Add(side, new RoundButton { Style = BtnStyle.Chip, PinIcon = true, Text = "" }, 172, 24, 34, 32);
            btnPin.Click += (s, e) => { settings.Pinned = !settings.Pinned; settings.Save(); ApplyPin(); };

            string[] names = { "Macro", "Presets", "Sequence editor", "Timeline editor", "Scripts", "Hotkeys", "Settings", "Themes & Colors", "Auto clicker", "Calibration", "Key binds" };
            string[] icons = { "\uE8FD", "\uE8F1", "\uE179", "\uE7C4", "\uE943", "\uE765", "\uE713", "\uE790", "\uE962", "\uE1D2", "\uE7FC" };
            for (int i = 0; i < names.Length; i++)
            {
                int n = i;
                navs[i] = Add(side, new NavButton { Text = names[i], Icon = icons[i] }, 12, 84 + Array.IndexOf(NavOrder, i) * 46, 196, 42);
                navs[i].Click += (s, e) => ShowPage(n);
                tips.SetToolTip(navs[i], names[i]);
            }
            sideFooter1 = Add(side, new TextLabel { Text = "Settings save automatically.", Sub = true, Font = Ui.F(8.5f) }, 20, 590, 190, 18);
            sideFooter2 = Add(side, new TextLabel { Text = "Embris v" + Ui.Version, Faint = true, Font = Ui.F(8f) }, 20, 610, 190, 18);
        }

        Box sidebar;
        Logo sideLogo;
        RoundButton btnCollapse;
        TextLabel sideFooter1, sideFooter2;

        int SidebarWidth { get { return Ui.S(settings.SidebarCollapsed ? 64 : 220); } }

        // The sidebar is laid out by hand: full (icons, names, footer) or collapsed to a strip of icons.
        void LayoutSidebar()
        {
            int h = ClientSize.Height;
            bool c = settings.SidebarCollapsed;
            sidebar.SetBounds(0, 0, SidebarWidth, h);
            if (c)
            {
                sideLogo.SetBounds(Ui.S(14), Ui.S(16), Ui.S(36), Ui.S(36));
                btnCollapse.SetBounds(Ui.S(15), Ui.S(60), Ui.S(34), Ui.S(32));
                btnPin.SetBounds(Ui.S(15), Ui.S(98), Ui.S(34), Ui.S(32));
            }
            else
            {
                sideLogo.SetBounds(Ui.S(20), Ui.S(20), Ui.S(110), Ui.S(40));
                btnCollapse.SetBounds(Ui.S(134), Ui.S(24), Ui.S(34), Ui.S(32));
                btnPin.SetBounds(Ui.S(172), Ui.S(24), Ui.S(34), Ui.S(32));
            }
            int top = c ? Ui.S(146) : Ui.S(84);
            for (int i = 0; i < navs.Length; i++)
                navs[i].SetBounds(c ? Ui.S(8) : Ui.S(12), top + Array.IndexOf(NavOrder, i) * Ui.S(c ? 44 : 46), c ? Ui.S(48) : Ui.S(196), Ui.S(c ? 40 : 42));
            sideFooter1.Visible = sideFooter2.Visible = !c;
            sideFooter1.SetBounds(Ui.S(20), h - Ui.S(50), Ui.S(190), Ui.S(18));
            sideFooter2.SetBounds(Ui.S(20), h - Ui.S(30), Ui.S(190), Ui.S(18));
            tips.SetToolTip(btnCollapse, c ? "Show the full sidebar" : "Collapse the sidebar");
        }

        // Back to the designed size, so every page and button fits again.
        void ResetWindowSize()
        {
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            ClientSize = new Size(baseClient.Width - Ui.S(220) + SidebarWidth, baseClient.Height);
            var area = Screen.FromControl(this).WorkingArea;
            Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
        }

        void ApplyPin()
        {
            TopMost = settings.Pinned;
            if (pinToggle != null) pinToggle.Checked = settings.Pinned;
            btnPin.Selected = miniPin.Selected = settings.Pinned;
            btnPin.Invalidate();
            miniPin.Invalidate();
            tips.SetToolTip(miniPin, settings.Pinned ? "Pinned on top - click to unpin" : "Pin on top of other windows");
            tips.SetToolTip(btnPin, settings.Pinned ? "Pinned on top - click to unpin" : "Pin on top of other windows");
        }

        void BuildBottomBar()
        {
            var bar = Add(this, new Box(BoxRole.Bar), 220, 556, 740, 84);
            btnRecord = Add(bar, new RoundButton { RecordDot = true, Text = "Record", Font = Ui.Semi(11f) }, 28, 18, 150, 48);
            btnRecord.Click += (s, e) => ToggleRecording();
            btnRun = Add(bar, new RoundButton { Style = BtnStyle.Primary, Icon = "", Text = "Run macro", Font = Ui.Semi(11f) }, 190, 18, 200, 48);
            btnRun.Click += (s, e) => Toggle();
            status = Add(bar, new StatusView { Text = "Ready" }, 412, 18, 300, 26);
            lblHint = Add(bar, new TextLabel { Sub = true, Font = Ui.F(9f) }, 440, 44, 272, 20);
        }

        // ---------- compact view (shown when the window is made smaller than its normal size) ----------
        // Header, status and the Run/Record buttons always show; the extra rows appear in order of
        // importance as the window gets taller.

        Box mini;
        Logo miniLogo;
        StatusView miniStatus;
        TextLabel miniHint, miniSummary, miniKeyLabel, miniEveryLabel, miniSecLabel;
        RoundButton miniRun, miniRecord, miniPin, miniLoad, miniExpand;
        KeyBox miniKey;
        NumberBox miniInterval;
        Toggle miniSeq;
        Dropdown miniMode, miniPresets;

        void BuildMini()
        {
            mini = Add(this, new Box(BoxRole.Page) { Visible = false }, 0, 0, 300, 160);
            miniLogo = Add(mini, new Logo { Text = "Embris" }, 0, 0, 10, 10);
            miniPin = Add(mini, new RoundButton { Style = BtnStyle.Chip, PinIcon = true, Text = "" }, 0, 0, 10, 10);
            miniPin.Click += (s, e) => { settings.Pinned = !settings.Pinned; settings.Save(); ApplyPin(); };
            miniExpand = Add(mini, new RoundButton { Style = BtnStyle.Chip, Icon = "", Text = "" }, 0, 0, 10, 10);
            miniExpand.Click += (s, e) => ResetWindowSize();
            tips.SetToolTip(miniExpand, "Back to full size");
            miniStatus = Add(mini, new StatusView { Text = "Ready", Small = true }, 0, 0, 10, 10);
            miniHint = Add(mini, new TextLabel { Sub = true, Font = Ui.F(8.5f) }, 0, 0, 10, 10);
            miniSummary = Add(mini, new TextLabel { Faint = true, Font = Ui.F(8.5f) }, 0, 0, 10, 10);

            miniKeyLabel = Add(mini, new TextLabel { Text = "Key", Sub = true, Font = Ui.F(9f) }, 0, 0, 10, 10);
            miniKey = Add(mini, new KeyBox { Key = settings.ActionKey }, 0, 0, 10, 10);
            miniKey.KeyChanged += (s, e) => keyAction.Key = miniKey.Key;
            miniKey.GotFocus += (s, e) => hotkeyHook.Paused = true;
            miniKey.LostFocus += (s, e) => hotkeyHook.Paused = false;
            miniEveryLabel = Add(mini, new TextLabel { Text = "every", Sub = true, Font = Ui.F(9f) }, 0, 0, 10, 10);
            miniInterval = Add(mini, new NumberBox { Minimum = 0.001m, Maximum = MaxIntervalSec, DecimalPlaces = 3, Increment = 1 }, 0, 0, 10, 10);
            miniInterval.Value = settings.IntervalSec;
            miniInterval.ValueChanged += (s, e) => numInterval.Value = miniInterval.Value;
            miniSecLabel = Add(mini, new TextLabel { Text = "sec", Sub = true, Font = Ui.F(9f) }, 0, 0, 10, 10);

            miniSeq = Add(mini, new Toggle { Text = "Play the recorded sequence", Font = Ui.F(9f) }, 0, 0, 10, 10);
            miniSeq.CheckedChanged += (s, e) => chkUseRec.Checked = miniSeq.Checked;

            miniMode = Add(mini, new Dropdown { Font = Ui.F(9f) }, 0, 0, 10, 10);
            miniMode.Items.Add("Background (no switching)");
            miniMode.Items.Add("Switch to the game briefly");
            miniMode.SelectedIndexChanged += (s, e) => cboMode.SelectedIndex = miniMode.SelectedIndex;

            miniPresets = Add(mini, new Dropdown { Font = Ui.F(9f) }, 0, 0, 10, 10);
            miniLoad = Add(mini, new RoundButton { Text = "Load", Font = Ui.F(9f) }, 0, 0, 10, 10);
            miniLoad.Click += (s, e) =>
            {
                if (miniPresets.SelectedIndex < 0) return;
                presetList.SelectedIndex = miniPresets.SelectedIndex;
                LoadPreset();
                SyncMini();
            };

            miniRecord = Add(mini, new RoundButton { RecordDot = true, Text = "Record", Font = Ui.Semi(9.75f) }, 0, 0, 10, 10);
            miniRecord.Click += (s, e) => ToggleRecording();
            miniRun = Add(mini, new RoundButton { Style = BtnStyle.Primary, Icon = "", Text = "Run macro", Font = Ui.Semi(9.75f) }, 0, 0, 10, 10);
            miniRun.Click += (s, e) => Toggle();
        }

        // Copies the main controls into the compact ones (the compact ones write straight back).
        void SyncMini()
        {
            if (mini == null) return;
            miniKey.Key = keyAction.Key;
            numInterval.Commit();
            miniInterval.Value = numInterval.Value;
            miniSeq.Checked = chkUseRec.Checked;
            miniSeq.Text = "Play the recorded sequence (" + sequence.Count + " steps)";
            miniMode.SelectedIndex = cboMode.SelectedIndex;
            int keep = miniPresets.SelectedIndex;
            miniPresets.Items.Clear();
            foreach (var p in presets) miniPresets.Items.Add(p.Name);
            miniPresets.SelectedIndex = -1;
            miniPresets.SelectedIndex = Math.Min(Math.Max(keep, 0), presets.Count - 1);
        }

        void LayoutMini()
        {
            int w = ClientSize.Width, h = ClientSize.Height, pad = Ui.S(14), gap = Ui.S(8), inner = w - 2 * pad;
            mini.SetBounds(0, 0, w, h);

            // Smallest size: one row with the logo, Record, Run and the pin. Nothing else.
            bool oneRow = h < Ui.S(118);
            foreach (Control c in mini.Controls)
                if (c != miniLogo && c != miniPin && c != miniExpand && c != miniRecord && c != miniRun) c.Visible = !oneRow;
            if (oneRow)
            {
                int rowH = Math.Min(Ui.S(38), h - Ui.S(12)), rowY = (h - rowH) / 2, logoW = w >= Ui.S(420) ? Ui.S(112) : rowH;
                miniLogo.SetBounds(pad, rowY, logoW, rowH);
                miniPin.SetBounds(w - pad - Ui.S(32), (h - Ui.S(28)) / 2, Ui.S(32), Ui.S(28));
                miniExpand.SetBounds(w - pad - Ui.S(68), (h - Ui.S(28)) / 2, Ui.S(32), Ui.S(28));
                int left = pad + logoW + gap, right = w - pad - Ui.S(68) - gap, recWidth = (right - left - gap) * 2 / 5;
                miniRecord.SetBounds(left, rowY, recWidth, rowH);
                miniRun.SetBounds(left + recWidth + gap, rowY, right - left - recWidth - gap, rowH);
                return;
            }
            miniLogo.SetBounds(pad, Ui.S(8), inner - Ui.S(76), Ui.S(30));
            miniPin.SetBounds(w - pad - Ui.S(32), Ui.S(9), Ui.S(32), Ui.S(28));
            miniExpand.SetBounds(w - pad - Ui.S(68), Ui.S(9), Ui.S(32), Ui.S(28));
            miniStatus.SetBounds(pad, Ui.S(44), inner, Ui.S(22));

            int bh = Ui.S(38), by = h - pad - bh, recW = (inner - gap) * 2 / 5;
            miniRecord.SetBounds(pad, by, recW, bh);
            miniRun.SetBounds(pad + recW + gap, by, inner - recW - gap, bh);

            // Optional rows, most important first; each shows only if it fits above the buttons.
            int y = Ui.S(72), room = by - gap;
            int row = Ui.S(32);
            bool quick = y + row <= room;
            int lw = Ui.S(28), ew = Ui.S(38), sw = Ui.S(26), kw = (inner - lw - ew - sw - 3 * Ui.S(6)) / 2;
            if (quick)
            {
                miniKeyLabel.SetBounds(pad, y, lw, row);
                miniKey.SetBounds(pad + lw + Ui.S(6), y, kw, row);
                miniEveryLabel.SetBounds(pad + lw + kw + Ui.S(12), y, ew, row);
                miniInterval.SetBounds(pad + lw + kw + ew + Ui.S(12), y, kw, row);
                miniSecLabel.SetBounds(pad + lw + 2 * kw + ew + Ui.S(18), y, sw, row);
                y += row + gap;
            }
            foreach (Control c in new Control[] { miniKeyLabel, miniKey, miniEveryLabel, miniInterval, miniSecLabel }) c.Visible = quick;

            bool seq = y + Ui.S(26) <= room;
            miniSeq.Visible = seq;
            if (seq) { miniSeq.SetBounds(pad, y, inner, Ui.S(26)); y += Ui.S(26) + gap; }

            bool mode = y + row <= room;
            miniMode.Visible = mode;
            if (mode) { miniMode.SetBounds(pad, y, inner, row); y += row + gap; }

            bool preset = y + row <= room;
            miniPresets.Visible = miniLoad.Visible = preset;
            if (preset)
            {
                miniPresets.SetBounds(pad, y, inner - Ui.S(70), row);
                miniLoad.SetBounds(w - pad - Ui.S(62), y, Ui.S(62), row);
                y += row + gap;
            }

            bool hint = y + Ui.S(18) <= room;
            miniHint.Visible = hint;
            if (hint) { miniHint.SetBounds(pad, y, inner, Ui.S(18)); y += Ui.S(18) + Ui.S(2); }

            bool summary = y + Ui.S(18) <= room;
            miniSummary.Visible = summary;
            if (summary) miniSummary.SetBounds(pad, y, inner, Ui.S(18));
        }
        string MacroSummary()
        {
            string what = settings.UseRecording ? "Plays a " + sequence.Count + "-step sequence"
                : settings.PressKey && settings.ClickEnabled ? "Clicks + presses " + Ui.KeyName(settings.ActionKey)
                : settings.PressKey ? "Presses " + Ui.KeyName(settings.ActionKey)
                : settings.ClickEnabled ? "Clicks" : "Nothing set up";
            decimal s = settings.IntervalSec;
            string every = s < 60 ? s.ToString("0.##") + "s" : s < 3600 ? (s / 60).ToString("0.#") + " min" : (s / 3600).ToString("0.##") + " h";
            return what + " every " + every + "   |   Run " + Ui.KeyName(settings.Hotkey) + "   Record " + Ui.KeyName(settings.RecordHotkey);
        }

        int currentPage;
        bool isCompact;

        void ShowPage(int index)
        {
            currentPage = index;
            if (isCompact) return;
            if (stalePages.Contains(pages[index])) PlaceBox(pages[index]);
            if (index == 3 && timelineNeedsFit && timeline != null) { timeline.Fit(); timelineNeedsFit = false; }
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i].Visible = i == index;
                navs[i].Selected = i == index;
                navs[i].Invalidate();
            }
        }

        // ---------- pages ----------

        void BuildMacroPage()
        {
            var p = Page(0, "Macro", "Choose the app, what to press or click, and how often.");

            cardTarget = Card(p, "Target app", 32, 100, 330, 236);
            Label(cardTarget, "App to send input to", 18, 48, 294);
            cboTarget = Add(cardTarget, new Dropdown(), 18, 70, 294, 34);
            cboTarget.Opening += (s, e) => RefreshTargets();
            Label(cardTarget, "Send mode", 18, 112, 294);
            cboMode = Add(cardTarget, new Dropdown(), 18, 134, 294, 34);
            cboMode.Items.Add("Background (no switching)");
            cboMode.Items.Add("Switch to it briefly (works in Roblox)");
            cboMode.SelectedIndex = settings.Mode;
            cboMode.SelectedIndexChanged += (s, e) => UpdateIdleEnabled();
            chkIdle = Add(cardTarget, new Toggle { Text = "Wait until I'm idle", Checked = settings.WaitIdle }, 18, 186, 176, 30);
            numIdle = Num(cardTarget, 0, 1000000, 1, 0.5m, settings.IdleSec, 196, 184, 80);
            Label(cardTarget, "sec", 284, 191, 30);

            cardTiming = Card(p, "Timing", 32, 352, 330, 180);
            Label(cardTiming, "Repeat every (seconds)", 18, 48, 294);
            numInterval = Num(cardTiming, 0.001m, MaxIntervalSec, 3, 1, settings.IntervalSec, 18, 70, 140);
            lblIntervalHint = Label(cardTiming, "", 172, 77, 140);
            numInterval.ValueChanged += (s, e) => UpdateIntervalHint();
            UpdateIntervalHint();
            // Calibration overlay: crosshair + ghost of the start view over the game.
            calibrationToggle = Add(cardTiming, new Toggle { Text = "Calibration (" + Ui.KeyName(settings.OverlayHotkey) + ")" }, 18, 122, 150, 30);
            // How see-through the ghost of the start view is.
            GhostSlider(cardTiming, 172, 122, 100);
            WireCalibrationToggle(calibrationToggle);

            cardKey = Card(p, "Key press", 378, 100, 330, 166);
            chkKey = Add(cardKey, new Toggle { Text = "Press a key", Checked = settings.PressKey }, 18, 46, 200, 30);
            Label(cardKey, "Key", 18, 88, 150);
            keyAction = Add(cardKey, new KeyBox { Key = settings.ActionKey }, 18, 110, 150, 34);
            Label(cardKey, "Hold for (ms)", 184, 88, 128);
            numHold = Num(cardKey, 0, MaxHoldMs, 0, 10, settings.HoldMs, 184, 110, 128);

            cardClick = Card(p, "Mouse click", 378, 282, 330, 250);
            chkClick = Add(cardClick, new Toggle { Text = "Click at a position", Checked = settings.ClickEnabled }, 18, 46, 176, 30);
            btnLeft = Add(cardClick, new RoundButton { Style = BtnStyle.Chip, Text = "Left", Font = Ui.F(9f) }, 196, 47, 56, 28);
            btnRight = Add(cardClick, new RoundButton { Style = BtnStyle.Chip, Text = "Right", Font = Ui.F(9f) }, 256, 47, 56, 28);
            btnLeft.Click += (s, e) => SetClickButton(false);
            btnRight.Click += (s, e) => SetClickButton(true);
            SetClickButton(settings.RightClick);
            Label(cardClick, "X", 18, 88, 88);
            Label(cardClick, "Y", 114, 88, 88);
            numX = Num(cardClick, -MaxPos, MaxPos, 0, 1, settings.ClickX, 18, 110, 88);
            numY = Num(cardClick, -MaxPos, MaxPos, 0, 1, settings.ClickY, 114, 110, 88);
            btnPick = Add(cardClick, new RoundButton { Text = "Pick (3s)" }, 210, 110, 102, 34);
            btnPick.Click += (s, e) => StartPick(btnPick, numX, numY, () => chkClick.Checked = true);
            lblCursor = Label(cardClick, "", 18, 154, 294);
        }

        void BuildPresetsPage()
        {
            var p = Page(1, "Presets", "Your macro suite: save macros, give each its own hotkey, or build one step by step.");

            cardSaved = Tagged(Card(p, "Macro suite", 32, 100, 330, 436), VFill);
            Label(cardSaved, "Macro name", 18, 48, 294);
            txtPresetName = Add(cardSaved, new TextField { Placeholder = "e.g. Anti-AFK" }, 18, 70, 196, 34);
            var save = Add(cardSaved, new RoundButton { Style = BtnStyle.Primary, Icon = "", Text = "Save" }, 222, 70, 90, 34);
            save.Click += (s, e) => SavePreset();
            presetList = Add(cardSaved, Tagged(new ItemList { EmptyText = "No macros yet. Name one above and click Save." }, VFill), 18, 116, 294, 212);
            presetList.SelectedIndexChanged += (s, e) =>
            {
                int i = presetList.SelectedIndex;
                if (i >= 0) txtPresetName.Text = presets[i].Name;
                presetHotkeyBox.Key = i >= 0 ? presets[i].Hotkey : Keys.None;
                presetHotkeyBox.Enabled = presetHotkeyClear.Enabled = i >= 0;
            };
            // Each macro can have its own hotkey: press it anywhere to load and run that macro.
            Add(cardSaved, Tagged(new TextLabel { Text = "Hotkey", Sub = true, Font = Ui.F(9f) }, AtBottom), 18, 347, 54, 20);
            presetHotkeyBox = Add(cardSaved, Tagged(new KeyBox { Key = Keys.None, Enabled = false }, AtBottom), 74, 340, 150, 34);
            presetHotkeyBox.KeyChanged += (s, e) => SetPresetHotkey(presetHotkeyBox.Key);
            presetHotkeyClear = Add(cardSaved, Tagged(new RoundButton { Text = "Clear", Enabled = false }, AtBottom), 230, 340, 82, 34);
            presetHotkeyClear.Click += (s, e) => SetPresetHotkey(Keys.None);
            tips.SetToolTip(presetHotkeyBox, "Press this key anywhere to load and run this macro (press again to stop)");
            presetList.ItemActivated += (s, e) => LoadPreset();
            var load = Add(cardSaved, Tagged(new RoundButton { Style = BtnStyle.Primary, Icon = "", Text = "Load" }, AtBottom), 18, 382, 140, 38);
            load.Click += (s, e) => LoadPreset();
            var del = Add(cardSaved, Tagged(new RoundButton { Icon = "", Text = "Delete" }, AtBottom), 172, 382, 140, 38);
            del.Click += (s, e) => DeletePreset();

            cardBuilder = Tagged(Card(p, "Macro builder", 378, 100, 330, 436), VFill);
            Label(cardBuilder, "Add a step", 18, 48, 294);
            string[] types = { "Key", "Click", "Wait" };
            for (int i = 0; i < 3; i++)
            {
                int n = i;
                stepChips[i] = Add(cardBuilder, new RoundButton { Style = BtnStyle.Chip, Text = types[i], Font = Ui.F(9f) }, 18 + i * 100, 70, 94, 30);
                stepChips[i].Click += (s, e) => SetStepType(n);
            }

            var keyPanel = stepPanels[0] = Add(cardBuilder, new Box(BoxRole.Inner), 18, 110, 294, 60);
            Label(keyPanel, "Key", 0, 0, 140);
            bKey = Add(keyPanel, new KeyBox { Key = Keys.E }, 0, 22, 140, 34);
            Label(keyPanel, "Hold for (ms)", 154, 0, 140);
            bHold = Num(keyPanel, 0, MaxHoldMs, 0, 10, 50, 154, 22, 140);

            var clickPanel = stepPanels[1] = Add(cardBuilder, new Box(BoxRole.Inner), 18, 110, 294, 60);
            Label(clickPanel, "X", 0, 0, 64);
            Label(clickPanel, "Y", 70, 0, 64);
            bX = Num(clickPanel, -MaxPos, MaxPos, 0, 1, 0, 0, 22, 64);
            bY = Num(clickPanel, -MaxPos, MaxPos, 0, 1, 0, 70, 22, 64);
            Label(clickPanel, "Button", 140, 0, 96);
            bLeft = Add(clickPanel, new RoundButton { Style = BtnStyle.Chip, Text = "L", Font = Ui.F(9f) }, 140, 24, 32, 30);
            bRight = Add(clickPanel, new RoundButton { Style = BtnStyle.Chip, Text = "R", Font = Ui.F(9f) }, 176, 24, 32, 30);
            bLeft.Click += (s, e) => SetBuilderButton(false);
            bRight.Click += (s, e) => SetBuilderButton(true);
            SetBuilderButton(false);
            bPick = Add(clickPanel, new RoundButton { Text = "Pick" }, 214, 22, 80, 34);
            bPick.Click += (s, e) => StartPick(bPick, bX, bY, null);

            var waitPanel = stepPanels[2] = Add(cardBuilder, new Box(BoxRole.Inner), 18, 110, 294, 60);
            Label(waitPanel, "Wait for (ms)", 0, 0, 140);
            bWait = Num(waitPanel, 0, MaxHoldMs, 0, 100, 500, 0, 22, 140);
            SetStepType(0);

            var add = Add(cardBuilder, new RoundButton { Style = BtnStyle.Primary, Icon = "", Text = "Add step" }, 18, 182, 176, 38);
            add.Click += (s, e) => AddBuilderStep();
            // Records keys, clicks and camera movement and adds them to the end of the sequence.
            btnBuilderRecord = Add(cardBuilder, new RoundButton { RecordDot = true, Text = "Record" }, 202, 182, 110, 38);
            btnBuilderRecord.Click += (s, e) => ToggleRecording(true);
            Label(cardBuilder, "Sequence (plays instead of the single key / click)", 18, 232, 294);
            lblBuilderSteps = Add(cardBuilder, Tagged(new TextLabel { Sub = true, Wrap = true, Font = Mono }, VFill), 18, 256, 294, 72);
            // Repeat and speed of the sequence (the same values as on the editor pages).
            Add(cardBuilder, Tagged(new TextLabel { Text = "Repeat", Sub = true, Font = Ui.F(9f) }, AtBottom), 18, 345, 46, 20);
            builderRepeat = Add(cardBuilder, Tagged(new NumberBox { Minimum = 1, Maximum = int.MaxValue, DecimalPlaces = 0, Increment = 1 }, AtBottom), 66, 338, 84, 34);
            builderRepeat.Value = settings.Repeat;
            builderRepeat.ValueChanged += (s, e) => { if (editors[0] != null) editors[0].Repeat.Value = builderRepeat.Value; };
            tips.SetToolTip(builderRepeat, "How many times the sequence plays each time the macro runs");
            Add(cardBuilder, Tagged(new TextLabel { Text = "Speed", Sub = true, Font = Ui.F(9f) }, AtBottom), 162, 345, 44, 20);
            builderSpeed = Add(cardBuilder, Tagged(new NumberBox { Minimum = 0.01m, Maximum = 1000000, DecimalPlaces = 2, Increment = 0.25m, Suffix = "x" }, AtBottom), 206, 338, 106, 34);
            builderSpeed.Value = (decimal)settings.Speed;
            builderSpeed.ValueChanged += (s, e) => { if (editors[0] != null) editors[0].Speed.Value = builderSpeed.Value; };
            tips.SetToolTip(builderSpeed, "Type any speed: 0.5 = half speed, 1 = as recorded, 3 = three times as fast");
            var undo = Add(cardBuilder, Tagged(new RoundButton { Icon = "", Text = "Remove last" }, AtBottom), 18, 382, 140, 38);
            undo.Click += (s, e) => RemoveLastStep();
            var clear = Add(cardBuilder, Tagged(new RoundButton { Icon = "", Text = "Clear" }, AtBottom), 172, 382, 140, 38);
            clear.Click += (s, e) => { sequence.Clear(); SequenceChanged(); };
        }

        // ---------- scripts page: macros as text (.emb), plus AutoHotkey hotstring files ----------

        TextArea scriptBox;
        Dropdown scriptMacros;
        TextLabel scriptStatus;
        System.Windows.Forms.Timer scriptCheck = new System.Windows.Forms.Timer { Interval = 400 };
        string scriptFolder;

        void BuildScriptsPage()
        {
            var p = Page(4, "Scripts", "Write macros as text, open .emb or .ahk files, and save .emb files to send to friends.");
            var c = Tagged(Card(p, "Script", 32, 100, 676, 436), VFill);
            scriptMacros = Add(c, new Dropdown { Font = Ui.F(9f) }, 18, 46, 170, 34);
            scriptMacros.Opening += (s, e) => FillScriptMacros();
            var edit = Add(c, new RoundButton { Text = "Edit" }, 194, 46, 60, 34);
            tips.SetToolTip(edit, "Show the chosen macro from your suite as a script");
            edit.Click += (s, e) =>
            {
                if (scriptMacros.SelectedIndex < 0 || scriptMacros.SelectedIndex >= presets.Count) return;
                scriptBox.Text = EmbScript.FromPreset(presets[scriptMacros.SelectedIndex]);
            };
            var newScript = Add(c, new RoundButton { Icon = "", Text = "New" }, 266, 46, 72, 34);
            newScript.Click += (s, e) => scriptBox.Text = EmbScript.Template;
            var open = Add(c, new RoundButton { Icon = "", Text = "Open" }, 344, 46, 76, 34);
            open.Click += (s, e) => OpenScriptDialog();
            var saveFile = Add(c, new RoundButton { Icon = "", Text = "Save file" }, 426, 46, 96, 34);
            saveFile.Click += (s, e) => SaveScriptDialog();
            var add = Add(c, new RoundButton { Style = BtnStyle.Primary, Icon = "", Text = "Add to suite" }, 528, 46, 130, 34);
            add.Click += (s, e) => AddScriptToSuite(true);

            scriptBox = Add(c, Tagged(new TextArea(), VFill), 18, 92, 640, 296);
            scriptBox.Box.Font = Mono;
            scriptBox.Text = EmbScript.Template;
            scriptBox.Box.TextChanged += (s, e) => { scriptCheck.Stop(); scriptCheck.Start(); };
            scriptCheck.Tick += (s, e) => { scriptCheck.Stop(); CheckScript(); };
            scriptStatus = Add(c, Tagged(new TextLabel { Sub = true, Font = Ui.F(9f) }, AtBottom), 18, 398, 640, 22);
            CheckScript();
        }

        void FillScriptMacros()
        {
            int keep = scriptMacros.SelectedIndex;
            scriptMacros.Items.Clear();
            foreach (var p in presets) scriptMacros.Items.Add(p.Name);
            scriptMacros.SelectedIndex = -1;
            scriptMacros.SelectedIndex = Math.Min(Math.Max(keep, 0), presets.Count - 1);
        }

        ScriptResult CheckScript()
        {
            var r = EmbScript.Parse(scriptBox.Text, "Script");
            int steps = r.Steps.Count;
            scriptStatus.TextColor = r.Errors.Count > 0 ? (Color?)Ui.Danger : null;
            scriptStatus.Text = r.Errors.Count > 0
                ? r.Errors[0] + (r.Errors.Count > 1 ? "   (+" + (r.Errors.Count - 1) + " more)" : "")
                : "Looks good: \"" + r.Name + "\", " + steps + " step" + (steps == 1 ? "" : "s") +
                  (r.Hotstrings.Count > 0 ? ", " + r.Hotstrings.Count + " hotstrings" : "") +
                  (r.Hotkey != Keys.None ? ", hotkey " + Ui.KeyName(r.Hotkey) : "") + ". Click Add to suite to use it.";
            scriptStatus.Invalidate();
            return r;
        }

        bool AddScriptToSuite(bool select)
        {
            var r = CheckScript();
            if (r.Errors.Count > 0)
            {
                SetStatus(StatusKind.Problem, "The script has a mistake", r.Errors[0]);
                return false;
            }
            if (r.Hotkey == settings.Hotkey || r.Hotkey == settings.RecordHotkey) r.Hotkey = Keys.None;
            var preset = r.ToPreset();
            foreach (var other in presets) if (preset.Hotkey != Keys.None && other.Hotkey == preset.Hotkey) other.Hotkey = Keys.None;
            int i = presets.FindIndex(x => x.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) presets[i] = preset;
            else { presets.Add(preset); i = presets.Count - 1; }
            PresetStore.Save(presets);
            RefreshPresetList();
            if (select) presetList.SelectedIndex = i;
            SetStatus(StatusKind.Ready, "Added \"" + preset.Name + "\" to your macro suite",
                preset.Hotkey != Keys.None ? "Press " + Ui.KeyName(preset.Hotkey) + " anywhere to run it." : "Load it from the Presets page.");
            return true;
        }

        void OpenScriptDialog()
        {
            using (var dlg = new OpenFileDialog { Filter = "Embris or AutoHotkey scripts (*.emb;*.ahk)|*.emb;*.ahk|All files (*.*)|*.*", InitialDirectory = scriptFolder })
                if (dlg.ShowDialog(this) == DialogResult.OK) OpenScriptFile(dlg.FileName, false);
        }

        // Shows a .emb file in the editor (an .ahk file is converted to a script first).
        public void OpenScriptFile(string path, bool addToSuite)
        {
            try
            {
                scriptFolder = Path.GetDirectoryName(path);
                string text = File.ReadAllText(path);
                string name = Path.GetFileNameWithoutExtension(path);
                if (path.EndsWith(".ahk", StringComparison.OrdinalIgnoreCase) || !text.Contains("steps:") && !text.Contains("hotstrings:") && text.Contains("::"))
                {
                    var r = EmbScript.Parse(text, name);
                    r.Settings.TargetProcess = "RobloxPlayerBeta";
                    text = EmbScript.FromPreset(r.ToPreset());
                }
                else if (!text.Contains("name:")) text = "name: " + name + "\r\n" + text;
                scriptBox.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
                ShowPage(4);
                CheckScript();
                if (addToSuite) AddScriptToSuite(true);
                else SetStatus(StatusKind.Ready, "Opened " + Path.GetFileName(path), "Check it, then click Add to suite to use it.");
            }
            catch (Exception ex)
            {
                SetStatus(StatusKind.Problem, "Couldn't open that file", ex.Message);
            }
        }

        void SaveScriptDialog()
        {
            var r = CheckScript();
            string safe = string.Join("_", r.Name.Split(Path.GetInvalidFileNameChars()));
            using (var dlg = new SaveFileDialog { Filter = "Embris macro (*.emb)|*.emb", FileName = safe + EmbScript.Extension, InitialDirectory = scriptFolder })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(dlg.FileName, scriptBox.Text);
                scriptFolder = Path.GetDirectoryName(dlg.FileName);
                SetStatus(StatusKind.Ready, "Saved " + Path.GetFileName(dlg.FileName), "Send it to friends - they can open it in Embris.");
            }
        }

        // ---------- sequence editor & timeline editor ----------
        // Two pages that edit the same sequence: a table of steps, and a timeline with a track per key.
        // Both share a header (Record, Save sequence) and a footer (Test, Stop, time, Repeat, Speed).

        class EditorParts
        {
            public TextLabel Summary, Time;
            public Toggle Use;
            public RoundButton Record, Save, Test, StopTest, Undo, Redo;
            public NumberBox Repeat, Speed;
        }

        readonly EditorParts[] editors = new EditorParts[2];
        readonly Dictionary<RoundButton, string> recordTexts = new Dictionary<RoundButton, string>();
        bool syncingEditor, timelineEditing, testing, timelineNeedsFit = true;
        string lastPresetName;
        readonly System.Windows.Forms.Timer playTimer = new System.Windows.Forms.Timer { Interval = 40 };
        volatile int playMs; // where playback is in the sequence (ms), for the time display and the playhead
        Stopwatch playClock;
        double playRoundStart, playSpeed = 1;

        // While playing: moves the playhead and the time display along.
        void PlayTick()
        {
            var c = playClock;
            if (!playing || c == null) return;
            double ms = (c.Elapsed.TotalMilliseconds - playRoundStart) * playSpeed;
            playMs = (int)Math.Max(0, Math.Min(SequenceMs(), ms));
            UpdateTimeLabels();
            if (currentPage == 3) timeline.Playhead = playMs / 1000.0;
        }
        const decimal MaxSec = 2147483;

        EditorParts BuildEditorChrome(Box p, string recordText)
        {
            var e = new EditorParts();
            // The title and summary keep to the left, so they never cover the icon buttons beside them.
            p.Controls[0].Width = Ui.S(220);
            p.Controls[0].Tag = Fixed;
            e.Summary = (TextLabel)p.Controls[1];
            e.Summary.Width = Ui.S(216);
            e.Summary.Tag = Fixed;
            // Icon buttons beside the title keep their size and place, so a wide window can't
            // stretch them into the Record button.
            e.Undo = Add(p, Tagged(new RoundButton { Style = BtnStyle.Chip, Icon = "\uE7A7", Text = "" }, Fixed), 256, 56, 36, 30);
            e.Redo = Add(p, Tagged(new RoundButton { Style = BtnStyle.Chip, Icon = "\uE7A6", Text = "" }, Fixed), 296, 56, 36, 30);
            var calibrate = Add(p, Tagged(new RoundButton { Style = BtnStyle.Chip, Icon = "\uE1D2", Text = "" }, Fixed), 336, 56, 36, 30);
            calibrate.Click += (s, a) => ShowCalibrationMenu(calibrate);
            foreach (var chip in new Control[] { e.Undo, e.Redo, calibrate }) chip.BringToFront();
            tips.SetToolTip(calibrate, "Calibration: a crosshair and a ghost of your start view over the game, to line every run up the same way");
            e.Undo.Click += (s, a) => Undo();
            e.Redo.Click += (s, a) => Redo();
            tips.SetToolTip(e.Undo, "Undo (Ctrl+Z)");
            tips.SetToolTip(e.Redo, "Redo (Ctrl+Shift+Z)");
            e.Undo.Enabled = e.Redo.Enabled = false;
            e.Record = Add(p, new RoundButton { RecordDot = true, Text = recordText }, 388, 24, 150, 40);
            recordTexts[e.Record] = recordText;
            e.Record.Click += (s, a) => ToggleRecording();
            e.Save = Add(p, new RoundButton { Style = BtnStyle.Primary, Icon = "", Text = "Save sequence" }, 548, 24, 160, 40);
            e.Save.Click += (s, a) => SaveSequenceToSuite();
            tips.SetToolTip(e.Save, "Save this sequence as a macro in your suite (see Presets)");
            e.Use = Add(p, new Toggle { Text = "Play this sequence when the macro runs", Checked = settings.UseRecording, Font = Ui.F(9f) }, 388, 70, 320, 26);
            e.Use.CheckedChanged += (s, a) => SetUseRecording(e.Use.Checked);

            e.Test = Add(p, Tagged(new RoundButton { Style = BtnStyle.Primary, Icon = "", Text = "Test sequence", Font = Ui.Semi(10.5f) }, AtBottom), 32, 488, 160, 44);
            e.Test.Click += (s, a) => TestSequence();
            tips.SetToolTip(e.Test, "Play the sequence now in the target app (Repeat times), then stop");
            e.StopTest = Add(p, Tagged(new RoundButton { Icon = "", Text = "", Enabled = false }, AtBottom), 200, 488, 44, 44);
            e.StopTest.Click += (s, a) => { if (running) Stop(); };
            tips.SetToolTip(e.StopTest, "Stop");
            e.Time = Add(p, Tagged(new TextLabel { Font = Ui.Semi(11f) }, AtBottom), 258, 488, 168, 44);
            Add(p, Tagged(new TextLabel { Text = "Repeat", Sub = true, Font = Ui.F(9f) }, AtBottom), 430, 500, 48, 20);
            e.Repeat = Add(p, Tagged(new NumberBox { Minimum = 1, Maximum = int.MaxValue, DecimalPlaces = 0, Increment = 1 }, AtBottom), 480, 493, 76, 34);
            e.Repeat.Value = settings.Repeat;
            e.Repeat.ValueChanged += (s, a) =>
            {
                settings.Repeat = (int)e.Repeat.Value;
                foreach (var o in editors) if (o != null) o.Repeat.Value = e.Repeat.Value;
                builderRepeat.Value = e.Repeat.Value;
            };
            tips.SetToolTip(e.Repeat, "How many times the sequence plays each time the macro runs");
            Add(p, Tagged(new TextLabel { Text = "Speed", Sub = true, Font = Ui.F(9f) }, AtBottom), 566, 500, 44, 20);
            e.Speed = Add(p, Tagged(new NumberBox { Minimum = 0.01m, Maximum = 1000000, DecimalPlaces = 2, Increment = 0.25m, Suffix = "x" }, AtBottom), 612, 493, 96, 34);
            e.Speed.Value = (decimal)settings.Speed;
            e.Speed.ValueChanged += (s, a) =>
            {
                settings.Speed = (double)e.Speed.Value;
                foreach (var o in editors) if (o != null) o.Speed.Value = e.Speed.Value;
                builderSpeed.Value = e.Speed.Value;
            };
            tips.SetToolTip(e.Speed, "Type any speed: 0.5 = half speed, 1 = as recorded, 3 = three times as fast");
            return e;
        }

        void SetUseRecording(bool on)
        {
            settings.UseRecording = on;
            foreach (var e in editors) if (e != null) e.Use.Checked = on;
            UpdateSequenceEnabled();
        }

        // ---------- sequence editor ----------

        StepTable stepTable;
        List<SeqRow> seqRows = new List<SeqRow>();
        readonly List<int> tableRows = new List<int>(); // table row -> row in seqRows (mouse moves can be hidden)
        Toggle chkShowMoves;
        Dropdown edAction;
        KeyBox edKey;
        RoundButton edAllKeys, edLeft, edRight;
        TextField edText;
        NumberBox edHold, edAfter, edX, edY, edMoveX, edMoveY;
        TextLabel edKeysLabel, edHoldLabel, edPosLabel;
        KeyboardPreview keyboard;
        Box stepsCard, propsCard, tlCard, tlPropsCard;

        void BuildSequencePage()
        {
            var p = Page(2, "Sequence editor", "");
            editors[0] = BuildEditorChrome(p, "Record keys");
            chkUseRec = editors[0].Use;

            stepsCard = Tagged(Card(p, "Steps", 32, 100, 400, 376), VFill);
            chkShowMoves = Add(stepsCard, new Toggle { Text = "Mouse moves", Checked = true, Font = Ui.F(9f) }, 262, 10, 126, 28);
            chkShowMoves.CheckedChanged += (s, e) => RefreshStepTable();
            tips.SetToolTip(chkShowMoves, "Show camera / mouse movement in the list");
            stepTable = Add(stepsCard, Tagged(new StepTable(), VFill), 12, 44, 376, 256);
            stepTable.SelectionChanged += (s, e) => LoadStepProps();
            stepTable.RowsMoved += MoveStepRows;
            stepTable.MenuRequested += ShowStepMenu;
            stepTable.DeleteRequested += (s, e) => DeleteStepRows(false);
            tips.SetToolTip(stepTable, "Drag the dots to reorder. Shift+click selects several. Right-click or ... for more.");

            var addStep = Add(stepsCard, Tagged(new RoundButton { Icon = "", Text = "Add step" }, AtBottom), 12, 310, 108, 52);
            addStep.Click += (s, e) => ShowAddMenu(addStep);
            RowType[] quick = { RowType.KeyPress, RowType.KeyDown, RowType.KeyUp, RowType.Delay };
            for (int i = 0; i < quick.Length; i++)
            {
                var type = quick[i];
                var tile = Add(stepsCard, Tagged(new ToolTile { Icon = SeqModel.Icons[(int)type], Text = SeqModel.Names[(int)type] }, AtBottom), 128 + i * 66, 310, 60, 52);
                tile.Click += (s, e) => AddStepRow(type);
            }

            propsCard = Tagged(Card(p, "Step properties", 444, 100, 264, 376), VFill);
            Label(propsCard, "Action", 18, 51, 66);
            edAction = Add(propsCard, new Dropdown(), 88, 44, 158, 34);
            foreach (var n in SeqModel.Names) edAction.Items.Add(n);
            edAction.SelectedIndexChanged += (s, e) =>
            {
                if (edAction.SelectedIndex >= 0) EditRow(r => SeqModel.ChangeType(sequence, r, (RowType)edAction.SelectedIndex));
            };

            edKeysLabel = Label(propsCard, "Keys", 18, 93, 66);
            edKey = Add(propsCard, new KeyBox { Key = Keys.E }, 88, 86, 104, 34);
            edKey.KeyChanged += (s, e) => ChangeRowKey(edKey.Key);
            edAllKeys = Add(propsCard, new RoundButton { Style = BtnStyle.Chip, Text = "All", Font = Ui.F(9f) }, 198, 86, 48, 34);
            edAllKeys.Click += (s, e) => { edAllKeys.Selected = !edAllKeys.Selected; edAllKeys.Invalidate(); };
            edLeft = Add(propsCard, new RoundButton { Style = BtnStyle.Chip, Text = "Left", Font = Ui.F(9f) }, 88, 86, 76, 34);
            edRight = Add(propsCard, new RoundButton { Style = BtnStyle.Chip, Text = "Right", Font = Ui.F(9f) }, 170, 86, 76, 34);
            edLeft.Click += (s, e) => EditRow(r => SeqModel.SetButton(sequence, r, false));
            edRight.Click += (s, e) => EditRow(r => SeqModel.SetButton(sequence, r, true));
            edText = Add(propsCard, new TextField { Placeholder = "Text to type" }, 88, 86, 158, 34);
            edText.Box.TextChanged += (s, e) => EditRow(r => sequence[r.First].Text = edText.Text);
            edMoveX = Num(propsCard, -MaxPos, MaxPos, 0, 1, 0, 88, 86, 76);
            edMoveY = Num(propsCard, -MaxPos, MaxPos, 0, 1, 0, 170, 86, 76);
            edMoveX.ValueChanged += (s, e) => EditRow(r => SeqModel.SetMove(sequence, r, (int)edMoveX.Value, (int)edMoveY.Value));
            edMoveY.ValueChanged += (s, e) => EditRow(r => SeqModel.SetMove(sequence, r, (int)edMoveX.Value, (int)edMoveY.Value));

            edHoldLabel = Label(propsCard, "Hold", 18, 135, 66);
            edHold = Num(propsCard, 0, MaxHoldMs, 0, 10, 50, 88, 128, 120);
            Label(propsCard, "ms", 214, 135, 32);
            edHold.ValueChanged += (s, e) => EditRow(r => SeqModel.SetHold(sequence, r, (int)edHold.Value));
            Label(propsCard, "Delay after", 18, 177, 70);
            edAfter = Num(propsCard, 0, MaxHoldMs, 0, 10, 0, 88, 170, 120);
            Label(propsCard, "ms", 214, 177, 32);
            edAfter.ValueChanged += (s, e) => EditRow(r => SeqModel.SetDelayAfter(sequence, seqRows, seqRows.IndexOf(r), (int)edAfter.Value));
            edPosLabel = Label(propsCard, "Position", 18, 219, 66);
            edX = Num(propsCard, -MaxPos, MaxPos, 0, 1, 0, 88, 212, 76);
            edY = Num(propsCard, -MaxPos, MaxPos, 0, 1, 0, 170, 212, 76);
            edX.ValueChanged += (s, e) => EditRow(r => SeqModel.SetPosition(sequence, r, (int)edX.Value, (int)edY.Value));
            edY.ValueChanged += (s, e) => EditRow(r => SeqModel.SetPosition(sequence, r, (int)edX.Value, (int)edY.Value));

            Add(propsCard, new TextLabel { Text = "Keyboard", Font = Ui.Semi(10f) }, 18, 256, 80, 22);
            Add(propsCard, new TextLabel { Text = "click a key to add it", Sub = true, Font = Ui.F(8.5f) }, 96, 258, 150, 20);
            keyboard = Add(propsCard, Tagged(new KeyboardPreview(), VFill), 18, 282, 228, 80);
            keyboard.KeyClicked += k => AddStepRow(RowType.KeyPress, k);
            tips.SetToolTip(keyboard, "Click a key to add a press of it after the selected step");
        }

        // The one selected row (null when none or several are selected).
        SeqRow CurrentRow
        {
            get
            {
                int a = stepTable.SelectionStart;
                return a >= 0 && a == stepTable.SelectionEnd && a < tableRows.Count ? seqRows[tableRows[a]] : null;
            }
        }

        // Rebuilds the rows from the sequence, keeping the selection where it was.
        void RefreshStepTable()
        {
            SeqModel.Normalize(sequence);
            seqRows = SeqModel.Rows(sequence);
            tableRows.Clear();
            var rows = new List<TableRow>();
            bool moves = chkShowMoves.Checked;
            for (int i = 0; i < seqRows.Count; i++)
            {
                var r = seqRows[i];
                if (r.Type == RowType.Move && !moves) continue;
                tableRows.Add(i);
                rows.Add(new TableRow
                {
                    Icon = SeqModel.Icons[(int)r.Type], Action = SeqModel.Names[(int)r.Type],
                    Keys = SeqModel.KeyText(sequence, r), Duration = SeqModel.DurationText(sequence, r), Chip = r.IsKey || r.IsMouse
                });
            }
            stepTable.EmptyText = sequence.Count == 0
                ? "No steps yet. Click Record keys (or press " + Ui.KeyName(settings.RecordHotkey) + ") and play, or add steps with the buttons below."
                : "Only mouse movement here. Turn on \"Mouse moves\" to see it.";
            stepTable.SetRows(rows);
            LoadStepProps();
        }

        void LoadStepProps()
        {
            if (stepTable == null) return;
            syncingEditor = true;
            var r = CurrentRow;
            var t = r == null ? RowType.KeyPress : r.Type;
            bool key = r != null && r.IsKey, mouse = r != null && r.IsMouse, move = r != null && t == RowType.Move, text = r != null && t == RowType.Type;
            edAction.Enabled = r != null;
            edAction.SelectedIndex = r == null ? -1 : (int)t;
            edKey.Visible = edAllKeys.Visible = !mouse && !move && !text;
            edKey.Enabled = edAllKeys.Enabled = key;
            edLeft.Visible = edRight.Visible = mouse;
            edMoveX.Visible = edMoveY.Visible = move;
            edText.Visible = text;
            edKeysLabel.Text = mouse ? "Button" : move ? "Distance" : text ? "Text" : "Keys";
            edHoldLabel.Text = t == RowType.Delay ? "Wait" : move ? "Takes" : "Hold";
            edHold.Enabled = r != null && (t == RowType.KeyPress || t == RowType.Click || t == RowType.Delay || (move && r.Count > 1));
            edAfter.Enabled = r != null && t != RowType.Delay;
            bool fixedPos = mouse && sequence[r.First].X != RecStep.NoPosition;
            edX.Enabled = edY.Enabled = fixedPos;
            edPosLabel.Text = mouse && !fixedPos ? "At cursor" : "Position";
            keyboard.Lit.Clear();
            if (r != null)
            {
                var st = sequence[r.First];
                if (key) { edKey.Key = (Keys)st.Code; keyboard.Lit.Add((Keys)st.Code); }
                if (mouse)
                {
                    edLeft.Selected = st.Code == 0;
                    edRight.Selected = st.Code == 1;
                    edLeft.Invalidate();
                    edRight.Invalidate();
                    if (fixedPos) { edX.Value = st.X; edY.Value = st.Y; }
                }
                if (move) { var m = SeqModel.MoveTotal(sequence, r); edMoveX.Value = m.X; edMoveY.Value = m.Y; }
                if (text) edText.Text = st.Text ?? "";
                edHold.Value = SeqModel.Hold(sequence, r);
                edAfter.Value = SeqModel.DelayAfter(sequence, seqRows, seqRows.IndexOf(r));
                tips.SetToolTip(edAllKeys, key ? "When on, changing or deleting this key changes every " + Ui.KeyName((Keys)st.Code) + " in the sequence" : "");
            }
            keyboard.Invalidate();
            syncingEditor = false;
        }

        // Selects the table rows covering steps first..last.
        void SelectSteps(int first, int last)
        {
            int a = -1, b = -1;
            for (int i = 0; i < tableRows.Count; i++)
            {
                var r = seqRows[tableRows[i]];
                if (a < 0 && r.Last >= first) a = i;
                if (r.First <= last) b = i;
            }
            if (a < 0) a = b = tableRows.Count - 1;
            stepTable.SelectRows(a, Math.Max(a, b));
        }

        // Applies an edit to the selected row, then refreshes and keeps it selected.
        void EditRow(Action<SeqRow> change)
        {
            var r = CurrentRow;
            if (syncingEditor || r == null) return;
            int first = r.First;
            change(r);
            SequenceChanged();
            SelectSteps(first, first);
        }

        void ChangeRowKey(Keys key)
        {
            var r = CurrentRow;
            if (syncingEditor || r == null || !r.IsKey) return;
            if (edAllKeys.Selected) { ReplaceKey((Keys)sequence[r.First].Code, key, false); SelectSteps(r.First, r.First); }
            else EditRow(x => SeqModel.SetKey(sequence, x, key));
        }

        void AddStepRow(RowType type, Keys key = Keys.None)
        {
            int at = stepTable.SelectionEnd >= 0 && stepTable.SelectionEnd < tableRows.Count ? seqRows[tableRows[stepTable.SelectionEnd]].Last + 1 : sequence.Count;
            if (key == Keys.None) key = edKey.Key == Keys.None ? Keys.E : edKey.Key;
            int hold = type == RowType.Delay ? 250 : type == RowType.Click ? 40 : 50;
            var steps = SeqModel.Make(type, key, false, RecStep.NoPosition, RecStep.NoPosition, hold);
            SeqModel.Insert(sequence, at, steps);
            if (!chkUseRec.Checked) chkUseRec.Checked = true;
            SequenceChanged();
            SelectSteps(at, at);
            stepTable.Focus();
        }

        void DeleteStepRows(bool keepOnly)
        {
            int a = stepTable.SelectionStart, b = stepTable.SelectionEnd;
            if (a < 0 || b >= tableRows.Count) return;
            var r = CurrentRow;
            if (!keepOnly && r != null && r.IsKey && edAllKeys.Selected) { ReplaceKey((Keys)sequence[r.First].Code, Keys.None, true); return; }
            int sa = tableRows[a], sb = tableRows[b], first = seqRows[sa].First;
            if (keepOnly) SeqModel.Crop(sequence, seqRows, sa, sb);
            else SeqModel.Delete(sequence, seqRows, sa, sb);
            SequenceChanged();
            if (keepOnly || tableRows.Count == 0) stepTable.SelectRows(-1, -1);
            else SelectSteps(first, first);
            SetStatus(StatusKind.Ready, keepOnly ? "Cropped to " + (b - a + 1) + " step(s)" : "Deleted " + (b - a + 1) + " step(s)",
                sequence.Count == 0 ? "The sequence is empty now." : seqRows.Count + " steps left.");
        }

        void MoveStepRows(int a, int b, int to)
        {
            if (a < 0 || b >= tableRows.Count) return;
            int sa = tableRows[a], sb = tableRows[b], sto = to < tableRows.Count ? tableRows[to] : seqRows.Count;
            int len = seqRows[sb].Last - seqRows[sa].First + 1;
            int at = sto < seqRows.Count ? seqRows[sto].First : sequence.Count;
            if (at > seqRows[sa].First) at -= len;
            SeqModel.MoveRows(sequence, seqRows, sa, sb, sto);
            SequenceChanged();
            SelectSteps(at, at + len - 1);
        }

        void ShowAddMenu(Control button)
        {
            var menu = NewMenu();
            for (int i = 0; i < SeqModel.Names.Length; i++)
            {
                var type = (RowType)i;
                AddMenuItem(menu, SeqModel.Names[i], () => AddStepRow(type), true);
            }
            menu.Show(button, new Point(0, button.Height + Ui.S(4)));
        }

        void ShowStepMenu(int row, Point screen)
        {
            var menu = NewMenu();
            int a = stepTable.SelectionStart, b = stepTable.SelectionEnd;
            var r = CurrentRow;
            AddMenuItem(menu, "Duplicate", () => { if (r != null) { int f = r.Last + 1; SeqModel.Duplicate(sequence, r); SequenceChanged(); SelectSteps(f, f); } }, r != null);
            AddMenuItem(menu, "Move up", () => MoveStepRows(a, b, a - 1), a > 0);
            AddMenuItem(menu, "Move down", () => MoveStepRows(a, b, b + 2), b < tableRows.Count - 1);
            if (r != null && r.IsMouse)
            {
                bool atCursor = sequence[r.First].X == RecStep.NoPosition;
                AddMenuItem(menu, atCursor ? "Click at a set position" : "Click where the cursor is", () =>
                {
                    var c = Cursor.Position;
                    EditRow(x => SeqModel.SetPosition(sequence, x, atCursor ? c.X : RecStep.NoPosition, atCursor ? c.Y : RecStep.NoPosition));
                }, true);
            }
            if (r != null && r.IsKey)
            {
                var k = (Keys)sequence[r.First].Code;
                AddMenuItem(menu, "Remove every " + Ui.KeyName(k), () => ReplaceKey(k, Keys.None, true), true);
            }
            AddMenuItem(menu, "Crop to the selected steps", () => DeleteStepRows(true), a >= 0 && b > a);
            AddMenuItem(menu, b > a ? "Delete " + (b - a + 1) + " steps" : "Delete", () => DeleteStepRows(false), a >= 0);
            menu.Show(screen);
        }

        static ContextMenuStrip NewMenu()
        {
            var menu = new ContextMenuStrip { Renderer = new UiMenuRenderer(), ShowImageMargin = false, Font = Ui.F(9.75f), Padding = new Padding(Ui.S(3)) };
            menu.Closed += (s, e) => menu.BeginInvoke((Action)menu.Dispose);
            return menu;
        }

        static void AddMenuItem(ContextMenuStrip menu, string text, Action act, bool enabled)
        {
            var item = new ToolStripMenuItem(text) { AutoSize = false, Enabled = enabled };
            item.Size = new Size(Ui.S(230), Ui.S(32));
            item.Click += (s, e) => act();
            menu.Items.Add(item);
        }

        // Changes (or removes) every press of one key.
        void ReplaceKey(Keys from, Keys to, bool remove)
        {
            int n = 0;
            for (int i = sequence.Count - 1; i >= 0; i--)
            {
                var st = sequence[i];
                if (st.Kind > 1 || st.Code != (int)from) continue;
                n++;
                if (remove)
                {
                    // Keep the timing: the removed step's wait moves to the next step.
                    if (i + 1 < sequence.Count) sequence[i + 1].Delay += st.Delay;
                    sequence.RemoveAt(i);
                }
                else st.Code = (int)to;
            }
            SequenceChanged();
            SetStatus(StatusKind.Ready, n == 0 ? "No " + Ui.KeyName(from) + " steps found" :
                (remove ? "Removed " : "Changed ") + n + " " + Ui.KeyName(from) + " step(s)" + (remove ? "" : " to " + Ui.KeyName(to)), "");
        }

        // ---------- timeline editor ----------

        TimelineEditor timeline;
        RoundButton tlSelectTool, tlCutTool, tlCropTool, tlSnapKeys, tlDuplicate, tlDelete;
        Dropdown tlSnap, tlType;
        KeyBox tlAddTrack, tlKey;
        Slider tlZoom;
        NumberBox tlStart, tlDur, tlEnd, tlX, tlY;
        TextField tlText;
        TextLabel tlInfo, tlKeyLabel, tlPosLabel;
        static readonly int[] SnapChoices = { 0, 1, 5, 10, 50, 100 };

        NumberBox SmallNum(Control parent, decimal min, decimal max, int decimals, decimal step, int x, int y, int w, string suffix)
        {
            return Add(parent, new NumberBox { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = step, Suffix = suffix }, x, y, w, 32);
        }

        void BuildTimelinePage()
        {
            var p = Page(3, "Timeline editor", "");
            editors[1] = BuildEditorChrome(p, "Record inputs");

            tlCard = Tagged(Card(p, null, 32, 100, 456, 376), VFill);
            tlSelectTool = Add(tlCard, new RoundButton { Style = BtnStyle.Chip, Icon = "", Text = "", Selected = true }, 14, 12, 36, 32);
            tlCutTool = Add(tlCard, new RoundButton { Style = BtnStyle.Chip, Icon = "\uE8C6", Text = "" }, 52, 12, 36, 32);
            tlCropTool = Add(tlCard, new RoundButton { Style = BtnStyle.Chip, Icon = "\uE7A8", Text = "" }, 90, 12, 36, 32);
            tips.SetToolTip(tlCropTool, "Crop: drag the two bars to the part you want to keep, then click Crop (or Enter). Esc cancels.");
            tlCropTool.Click += (s, e) => { if (timeline.Cropping) timeline.CancelCrop(); else timeline.StartCrop(); };
            tips.SetToolTip(tlSelectTool, "Select: drag bars to move them, drag their ends to change the hold. Drag on an empty spot to select several, then drag the box ends to squish or stretch them.");
            tips.SetToolTip(tlCutTool, "Cut: click a hold to split it, or an empty spot to cut every track there");
            tlSelectTool.Click += (s, e) => SetCutTool(false);
            tlCutTool.Click += (s, e) => SetCutTool(true);
            tlSnap = Add(tlCard, new Dropdown { Font = Ui.F(9f) }, 130, 12, 76, 32);
            tlSnapKeys = Add(tlCard, new RoundButton { Style = BtnStyle.Chip, MagnetIcon = true, Text = "", Selected = settings.SnapToKeys }, 210, 12, 32, 32);
            tips.SetToolTip(tlSnapKeys, "Snap to keys: bars you move or stretch catch the starts and ends of other bars and the playhead");
            tlSnapKeys.Click += (s, e) =>
            {
                settings.SnapToKeys = !settings.SnapToKeys;
                tlSnapKeys.Selected = timeline.SnapToKeys = settings.SnapToKeys;
                tlSnapKeys.Invalidate();
                settings.Save();
            };
            foreach (int ms in SnapChoices) tlSnap.Items.Add(ms == 0 ? "No snap" : ms + " ms");
            tips.SetToolTip(tlSnap, "Snap: bars line up to this step when you drag them");
            tlSnap.SelectedIndex = 3;
            tlSnap.SelectedIndexChanged += (s, e) => timeline.SnapMs = SnapChoices[Math.Max(0, tlSnap.SelectedIndex)];
            tlAddTrack = Add(tlCard, new KeyBox { Key = Keys.None, Caption = "Track", Font = Ui.F(9f) }, 246, 12, 66, 32);
            tips.SetToolTip(tlAddTrack, "Click, then press a key to give it a track. Double-click a track to add a press.");
            tlAddTrack.KeyChanged += (s, e) =>
            {
                var k = tlAddTrack.Key;
                tlAddTrack.Key = Keys.None;
                if (k == Keys.None) return;
                timeline.AddTrack(k);
                SetStatus(StatusKind.Ready, "Added a " + Ui.KeyName(k) + " track", "Double-click the track where you want a press.");
            };
            var zoomOut = Add(tlCard, new RoundButton { Text = "−" }, 316, 12, 26, 32);
            tlZoom = Add(tlCard, new Slider(), 344, 12, 64, 32);
            var zoomIn = Add(tlCard, new RoundButton { Text = "+" }, 410, 12, 28, 32);
            tips.SetToolTip(tlZoom, "Zoom (Ctrl+wheel on the timeline). Double-click to fit.");
            zoomOut.Click += (s, e) => timeline.Zoom(0.7);
            zoomIn.Click += (s, e) => timeline.Zoom(1.4);
            tlZoom.ValueChanged += (s, e) => timeline.PxPerSec = 5 * Math.Pow(1000, tlZoom.Value);
            tlZoom.DoubleClick += (s, e) => timeline.Fit();

            timeline = Add(tlCard, Tagged(new TimelineEditor { SnapToKeys = settings.SnapToKeys }, VFill), 14, 54, 428, 308);
            timeline.Changed += (s, e) =>
            {
                sequence = timeline.ToSteps();
                timelineEditing = true;
                SequenceChanged();
                timelineEditing = false;
                LoadTimelineProps();
            };
            timeline.SelectionChanged += (s, e) => LoadTimelineProps();
            timeline.CropChanged += (s, e) => { tlCropTool.Selected = timeline.Cropping; tlCropTool.Invalidate(); };
            timeline.ZoomChanged += (s, e) => tlZoom.Value = Math.Log(timeline.PxPerSec / 5) / Math.Log(1000);

            tlPropsCard = Tagged(Card(p, "Selected input", 500, 100, 208, 376), VFill);
            tlInfo = Add(tlPropsCard, new TextLabel { Font = Ui.Semi(10f) }, 18, 42, 172, 24);
            Label(tlPropsCard, "Start time", 18, 76, 62);
            tlStart = SmallNum(tlPropsCard, 0, MaxSec, 3, 0.01m, 82, 70, 108, " s");
            Label(tlPropsCard, "Duration", 18, 112, 62);
            tlDur = SmallNum(tlPropsCard, 0, MaxSec, 3, 0.01m, 82, 106, 108, " s");
            Label(tlPropsCard, "End time", 18, 148, 62);
            tlEnd = SmallNum(tlPropsCard, 0, MaxSec, 3, 0.01m, 82, 142, 108, " s");
            Label(tlPropsCard, "Action", 18, 184, 62);
            tlType = Add(tlPropsCard, new Dropdown { Font = Ui.F(9f) }, 82, 178, 108, 32);
            foreach (var n in new[] { "Key hold", "Left click", "Right click", "Mouse move", "Type text" }) tlType.Items.Add(n);
            tlKeyLabel = Label(tlPropsCard, "Key", 18, 220, 62);
            tlKey = Add(tlPropsCard, new KeyBox { Key = Keys.E }, 82, 214, 108, 32);
            tlText = Add(tlPropsCard, new TextField { Placeholder = "Text to type" }, 82, 214, 108, 32);
            tlDuplicate = Add(tlPropsCard, new RoundButton { Icon = "", Text = "Duplicate", Font = Ui.F(9f) }, 18, 254, 84, 32);
            tlDelete = Add(tlPropsCard, new RoundButton { Icon = "", Text = "Delete", Font = Ui.F(9f) }, 106, 254, 84, 32);
            tlPosLabel = Add(tlPropsCard, new TextLabel { Text = "Mouse position", Font = Ui.Semi(10f) }, 18, 296, 172, 22);
            tlX = SmallNum(tlPropsCard, -MaxPos, MaxPos, 0, 1, 18, 322, 84, null);
            tlY = SmallNum(tlPropsCard, -MaxPos, MaxPos, 0, 1, 106, 322, 84, null);

            tlStart.ValueChanged += (s, e) => { if (!syncingEditor) timeline.SetSelected((double)tlStart.Value, (double)tlDur.Value); };
            tlDur.ValueChanged += (s, e) => { if (!syncingEditor) timeline.SetSelected((double)tlStart.Value, (double)tlDur.Value); };
            tlEnd.ValueChanged += (s, e) => { if (!syncingEditor) timeline.SetSelected((double)tlStart.Value, Math.Max(0, (double)(tlEnd.Value - tlStart.Value))); };
            tlType.SelectedIndexChanged += (s, e) => { if (!syncingEditor && tlType.SelectedIndex >= 0) timeline.SetSelectedKind(tlType.SelectedIndex, tlKey.Key == Keys.None ? Keys.E : tlKey.Key); };
            tlKey.KeyChanged += (s, e) => { if (!syncingEditor) timeline.SetSelectedKey(tlKey.Key); };
            tlText.Box.TextChanged += (s, e) => { if (!syncingEditor) timeline.SetSelectedText(tlText.Text); };
            tlX.ValueChanged += (s, e) => { if (!syncingEditor) timeline.SetSelectedPosition((int)tlX.Value, (int)tlY.Value); };
            tlY.ValueChanged += (s, e) => { if (!syncingEditor) timeline.SetSelectedPosition((int)tlX.Value, (int)tlY.Value); };
            tlDuplicate.Click += (s, e) => timeline.DuplicateSelected();
            tlDelete.Click += (s, e) => timeline.DeleteSelected();
        }

        void SetCutTool(bool cut)
        {
            timeline.CutTool = cut;
            tlCutTool.Selected = cut;
            tlSelectTool.Selected = !cut;
            tlCutTool.Invalidate();
            tlSelectTool.Invalidate();
        }

        void LoadTimelineProps()
        {
            if (timeline == null) return;
            var e = timeline.Selected;
            syncingEditor = true;
            foreach (Control c in new Control[] { tlStart, tlType, tlKey, tlText, tlDuplicate, tlDelete }) c.Enabled = e != null;
            tlDur.Enabled = tlEnd.Enabled = e != null && e.Kind <= 2;
            tlText.Visible = e != null && e.Kind == 4;
            tlKey.Visible = !tlText.Visible;
            tlKey.Enabled = e != null && e.Kind == 0;
            tlKeyLabel.Text = tlText.Visible ? "Text" : "Key";
            bool move = e != null && e.Kind == 3, click = e != null && (e.Kind == 1 || e.Kind == 2);
            bool fixedPos = click && e.X != RecStep.NoPosition;
            tlX.Enabled = tlY.Enabled = move || fixedPos;
            tlPosLabel.Text = move ? "Movement (x, y)" : click && !fixedPos ? "Clicks at the cursor" : "Mouse position";
            if (e != null)
            {
                tlStart.Value = (decimal)e.Start;
                tlDur.Value = (decimal)e.Dur;
                tlEnd.Value = (decimal)(e.Start + e.Dur);
                tlType.SelectedIndex = e.Kind;
                if (e.Kind == 0) tlKey.Key = (Keys)e.Code;
                if (e.Kind == 4) tlText.Text = e.Text ?? "";
                if (move || fixedPos) { tlX.Value = e.X; tlY.Value = e.Y; }
                string what = e.Kind <= 2 ? (e.Dur >= 0.12 ? "Hold" : "Tap") : move ? "Move" : "Type";
                tlInfo.Text = (e.Kind == 0 ? Ui.KeyName((Keys)e.Code) : e.Track) + " — " + what;
            }
            else if (timeline.SelectionCount > 1)
            {
                // Several bars: Start moves them, Duration / End squish or stretch them together.
                double a = timeline.GroupStart, b = timeline.GroupEnd;
                tlStart.Enabled = tlDur.Enabled = tlEnd.Enabled = tlDuplicate.Enabled = tlDelete.Enabled = true;
                tlStart.Value = (decimal)a;
                tlDur.Value = (decimal)(b - a);
                tlEnd.Value = (decimal)b;
                tlType.SelectedIndex = -1;
                tlInfo.Text = timeline.SelectionCount + " inputs selected";
            }
            else
            {
                tlType.SelectedIndex = -1;
                tlInfo.Text = timeline.Items.Count == 0 ? "Nothing here yet" : "Click a bar, or drag a box around several";
            }
            syncingEditor = false;
        }

        // ---------- shared: summary, time, test, save ----------

        string SequenceSummary()
        {
            if (sequence.Count == 0) return "Nothing recorded yet";
            int actions = 0;
            foreach (var r in seqRows) if (r.Type != RowType.Delay) actions++;
            return (lastPresetName != null ? lastPresetName + "  ·  " : "") + actions + " action" + (actions == 1 ? "" : "s") +
                   "  ·  " + (SequenceMs() / 1000.0).ToString("0.00") + " s";
        }

        long SequenceMs()
        {
            long ms = 0;
            foreach (var s in sequence) ms += s.Delay;
            return ms;
        }

        static string Clock(long ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            return (t.TotalHours >= 1 ? (int)t.TotalHours + ":" : "") + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00") + "." + t.Milliseconds.ToString("000");
        }

        void UpdateTimeLabels()
        {
            string text = Clock(playing ? playMs : 0) + " / " + Clock(SequenceMs());
            foreach (var e in editors) if (e != null) e.Time.Text = text;
        }

        // Plays the sequence once now (Repeat times), then stops.
        void TestSequence()
        {
            if (running) { Stop(); return; }
            if (recordingNow) return;
            ReadControls();
            if (sequence.Count == 0)
            {
                SetStatus(StatusKind.Problem, "Nothing to test", "Record or add some steps first.");
                return;
            }
            IntPtr target = FindTarget();
            if (target == IntPtr.Zero)
            {
                SetStatus(StatusKind.Problem, settings.TargetProcess + " not found", "Open it first, or choose another app on the Macro page.");
                return;
            }
            running = true;
            testing = true;
            SetEditable(false);
            StartPlayback(target, new List<RecStep>(sequence), settings.Speed, settings.Repeat);
        }

        // Saves the sequence (with the current settings) as a macro in the suite.
        void SaveSequenceToSuite()
        {
            ReadControls();
            string name = lastPresetName ?? "My sequence";
            bool use = settings.UseRecording;
            settings.UseRecording = true;
            var lines = settings.MacroLines();
            settings.UseRecording = use;
            SavePresetLines(name, lines);
            SetStatus(StatusKind.Ready, "Saved \"" + name + "\" to your macro suite", "Rename it or give it a hotkey on the Presets page.");
        }

        // ---------- auto clicker: a simple click-at-a-rate tool, separate from the macro ----------

        readonly AutoClicker clicker = new AutoClicker();
        NumberBox acHours, acMins, acSecs, acMs, acTimes, acX, acY;
        Dropdown acButton, acType;
        RoundButton acForever, acCount, acHere, acFixed, acPick, acStart;
        KeyBox acHotkey;
        TextLabel acRate, acStatus;
        readonly System.Windows.Forms.Timer acTimer = new System.Windows.Forms.Timer { Interval = 100 };

        void BuildAutoClickerPage()
        {
            var p = Page(8, "Auto clicker", "Clicks for you at a steady rate. Press the hotkey anywhere to start or stop.");

            var interval = Card(p, "Click interval", 32, 100, 676, 112);
            acRate = Add(interval, new TextLabel { Sub = true, Font = Ui.F(9f) }, 300, 16, 358, 20);
            decimal ms = settings.ClickIntervalMs;
            string[] units = { "hours", "mins", "secs", "ms" };
            var boxes = new NumberBox[4];
            decimal[] parts = { Math.Floor(ms / 3600000), Math.Floor(ms % 3600000 / 60000), Math.Floor(ms % 60000 / 1000), ms % 1000 };
            for (int i = 0; i < 4; i++)
            {
                boxes[i] = Num(interval, 0, 1000000, 0, 1, parts[i], 18 + i * 164, 56, 100);
                Label(interval, units[i], 124 + i * 164, 63, 56);
                boxes[i].ValueChanged += (s, e) => SaveClicker();
            }
            acHours = boxes[0]; acMins = boxes[1]; acSecs = boxes[2]; acMs = boxes[3];

            var options = Card(p, "Click options", 32, 226, 330, 148);
            Label(options, "Mouse button", 18, 48, 140);
            acButton = Add(options, new Dropdown(), 18, 70, 140, 34);
            foreach (var n in new[] { "Left", "Right", "Middle" }) acButton.Items.Add(n);
            acButton.SelectedIndex = settings.ClickButton;
            acButton.SelectedIndexChanged += (s, e) => SaveClicker();
            Label(options, "Click type", 172, 48, 140);
            acType = Add(options, new Dropdown(), 172, 70, 140, 34);
            acType.Items.Add("Single");
            acType.Items.Add("Double");
            acType.SelectedIndex = settings.ClickDouble ? 1 : 0;
            acType.SelectedIndexChanged += (s, e) => SaveClicker();

            var repeat = Card(p, "Click repeat", 378, 226, 330, 148);
            acForever = Add(repeat, new RoundButton { Style = BtnStyle.Chip, Text = "Until stopped" }, 18, 50, 140, 32);
            acCount = Add(repeat, new RoundButton { Style = BtnStyle.Chip, Text = "Repeat" }, 18, 92, 90, 32);
            acTimes = Num(repeat, 1, int.MaxValue, 0, 1, Math.Max(1, settings.ClickTimes), 116, 91, 110);
            Label(repeat, "times", 234, 98, 60);
            acForever.Click += (s, e) => { settings.ClickTimes = 0; SyncClicker(); SaveClicker(); };
            acCount.Click += (s, e) => { settings.ClickTimes = (int)acTimes.Value; SyncClicker(); SaveClicker(); };
            acTimes.ValueChanged += (s, e) => { if (settings.ClickTimes > 0) settings.ClickTimes = (int)acTimes.Value; SaveClicker(); };

            var where = Card(p, "Cursor position", 32, 388, 676, 96);
            acHere = Add(where, new RoundButton { Style = BtnStyle.Chip, Text = "Where the pointer is" }, 18, 48, 180, 34);
            acFixed = Add(where, new RoundButton { Style = BtnStyle.Chip, Text = "At a set spot" }, 206, 48, 130, 34);
            Label(where, "X", 352, 55, 14);
            acX = Num(where, -MaxPos, MaxPos, 0, 1, settings.ClickAtX, 368, 48, 86);
            Label(where, "Y", 462, 55, 14);
            acY = Num(where, -MaxPos, MaxPos, 0, 1, settings.ClickAtY, 478, 48, 86);
            acPick = Add(where, new RoundButton { Text = "Pick (3s)" }, 572, 48, 86, 34);
            acHere.Click += (s, e) => { settings.ClickFixed = false; SyncClicker(); SaveClicker(); };
            acFixed.Click += (s, e) => { settings.ClickFixed = true; SyncClicker(); SaveClicker(); };
            acX.ValueChanged += (s, e) => SaveClicker();
            acY.ValueChanged += (s, e) => SaveClicker();
            acPick.Click += (s, e) => StartPick(acPick, acX, acY, () => { settings.ClickFixed = true; SyncClicker(); SaveClicker(); });

            acStart = Add(p, Tagged(new RoundButton { Style = BtnStyle.Primary, Icon = "", Text = "Start", Font = Ui.Semi(11f) }, AtBottom), 32, 494, 180, 44);
            acStart.Click += (s, e) => ToggleClicker();
            tips.SetToolTip(acStart, "Clicks are skipped while the pointer is over Embris, so you can always click Stop.");
            Add(p, Tagged(new TextLabel { Text = "Hotkey", Sub = true, Font = Ui.F(9f) }, AtBottom), 230, 506, 50, 20);
            acHotkey = Add(p, Tagged(new KeyBox { Key = settings.ClickHotkey }, AtBottom), 282, 499, 130, 34);
            acHotkey.KeyChanged += (s, e) => SetClickerHotkey(acHotkey.Key);
            acHotkey.GotFocus += (s, e) => hotkeyHook.Paused = true;
            acHotkey.LostFocus += (s, e) => hotkeyHook.Paused = false;
            acStatus = Add(p, Tagged(new TextLabel { Sub = true, Font = Ui.F(9f) }, AtBottom), 428, 506, 280, 20);

            clicker.Finished += () => { if (IsHandleCreated) BeginInvoke((Action)ClickerStopped); };
            acTimer.Tick += (s, e) => UpdateClickerStatus();
            SyncClicker();
        }

        decimal ClickIntervalFromBoxes()
        {
            return acHours.Value * 3600000 + acMins.Value * 60000 + acSecs.Value * 1000 + acMs.Value;
        }

        void SaveClicker()
        {
            if (acStatus == null) return;
            settings.ClickIntervalMs = ClickIntervalFromBoxes();
            settings.ClickButton = Math.Max(0, acButton.SelectedIndex);
            settings.ClickDouble = acType.SelectedIndex == 1;
            settings.ClickAtX = (int)acX.Value;
            settings.ClickAtY = (int)acY.Value;
            settings.Save();
            UpdateClickerStatus();
        }

        void SyncClicker()
        {
            acForever.Selected = settings.ClickTimes == 0;
            acCount.Selected = settings.ClickTimes > 0;
            acHere.Selected = !settings.ClickFixed;
            acFixed.Selected = settings.ClickFixed;
            acTimes.Enabled = settings.ClickTimes > 0;
            acX.Enabled = acY.Enabled = settings.ClickFixed;
            foreach (var b in new[] { acForever, acCount, acHere, acFixed }) b.Invalidate();
            UpdateClickerStatus();
        }

        void UpdateClickerStatus()
        {
            if (acStatus == null) return;
            double ms = Math.Max(1, (double)ClickIntervalFromBoxes());
            acRate.Text = ms < 1000 ? "= " + (1000 / ms).ToString("0.#") + " clicks a second"
                        : ms < 60000 ? "= one click every " + (ms / 1000).ToString("0.##") + " s"
                        : "= one click every " + (ms / 60000).ToString("0.##") + " min";
            acStatus.Text = clicker.Running
                ? "Clicking... " + clicker.Clicks + " click" + (clicker.Clicks == 1 ? "" : "s") + ". Press " + Ui.KeyName(settings.ClickHotkey) + " to stop."
                : "Press " + Ui.KeyName(settings.ClickHotkey) + " anywhere to start.";
        }

        void ToggleClicker()
        {
            if (clicker.Running) { clicker.Stop(); ClickerStopped(); return; }
            foreach (var n in new[] { acHours, acMins, acSecs, acMs, acTimes, acX, acY }) n.Commit();
            SaveClicker();
            clicker.OwnWindow = Handle;
            clicker.Start((double)settings.ClickIntervalMs, settings.ClickButton, settings.ClickDouble, settings.ClickTimes,
                settings.ClickFixed, settings.ClickAtX, settings.ClickAtY);
            acStart.Style = BtnStyle.Danger;
            acStart.Icon = "";
            acStart.Text = "Stop";
            acStart.Invalidate();
            acTimer.Start();
            SetStatus(StatusKind.Running, "Auto clicker on", "Press " + Ui.KeyName(settings.ClickHotkey) + " anywhere to stop it.");
        }

        void ClickerStopped()
        {
            acTimer.Stop();
            acStart.Style = BtnStyle.Primary;
            acStart.Icon = "";
            acStart.Text = "Start";
            acStart.Invalidate();
            UpdateClickerStatus();
            SetStatus(StatusKind.Ready, "Auto clicker stopped", clicker.Clicks + " click" + (clicker.Clicks == 1 ? "" : "s") + " done.");
        }

        void SetClickerHotkey(Keys key)
        {
            if (key == settings.Hotkey || key == settings.RecordHotkey || presets.Exists(x => x.Hotkey == key))
            {
                acHotkey.Key = settings.ClickHotkey;
                SetStatus(StatusKind.Problem, "That key is already used", Ui.KeyName(key) + " is used by another hotkey.");
                return;
            }
            settings.ClickHotkey = key;
            hotkeyHook.ClickKeyCode = (int)key;
            settings.Save();
            UpdateClickerStatus();
        }

        // ---------- calibration overlay: crosshair + ghost of the start view ----------

        CalibrationOverlay overlay;
        readonly System.Windows.Forms.Timer startViewTimer = new System.Windows.Forms.Timer { Interval = 150 };
        bool startViewTaken;
        static string StartViewFile { get { return Path.Combine(Settings.Dir, "start-view.png"); } }

        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out Rect r);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref Native.POINT p);

        // The game's view on screen (its client area), or the screen under the mouse.
        Rectangle ViewArea(IntPtr window)
        {
            Rect r;
            if (window != IntPtr.Zero && GetClientRect(window, out r) && r.Right > 0 && r.Bottom > 0)
            {
                var p = new Native.POINT();
                ClientToScreen(window, ref p);
                return new Rectangle(p.X, p.Y, r.Right, r.Bottom);
            }
            return Screen.FromPoint(Cursor.Position).Bounds;
        }

        // While recording: as soon as something other than Embris is in front (the game), save the start view once.
        void StartViewTick()
        {
            if (!recordingNow || startViewTaken) { startViewTimer.Stop(); return; }
            IntPtr front = Native.GetForegroundWindow();
            if (front == IntPtr.Zero || front == Handle) return;
            startViewTaken = CalibrationOverlay.CaptureStartView(ViewArea(front), StartViewFile);
            if (startViewTaken) startViewTimer.Stop();
        }

        Toggle calibrationToggle;
        // The calibration controls appear on the Macro page and the Calibration page; these keep them in step.
        readonly List<Toggle> calibrationToggles = new List<Toggle>();
        readonly List<KeyValuePair<Slider, TextLabel>> ghostSliders = new List<KeyValuePair<Slider, TextLabel>>();

        void WireCalibrationToggle(Toggle toggle)
        {
            calibrationToggles.Add(toggle);
            toggle.CheckedChanged += (s, e) =>
            {
                bool shown = overlay != null && overlay.Visible;
                if (toggle.Checked != shown) ToggleOverlay();
            };
            tips.SetToolTip(toggle, "Shows a crosshair and a faded ghost of your start view over the game, to line every run up the same way");
        }

        // How see-through the ghost of the start view is: a slider and its % label.
        void GhostSlider(Control parent, int x, int y, int w)
        {
            var ghost = Add(parent, new Slider { Value = settings.GhostOpacity / 100.0 }, x, y, w, 30);
            var text = Add(parent, new TextLabel { Text = settings.GhostOpacity + "%", Sub = true, Font = Ui.F(9f) }, x + w + 2, y, 40, 30);
            ghostSliders.Add(new KeyValuePair<Slider, TextLabel>(ghost, text));
            ghost.ValueChanged += (s, e) =>
            {
                int v = (int)Math.Round(ghost.Value * 100);
                if (v == settings.GhostOpacity) return;
                settings.GhostOpacity = v;
                foreach (var pair in ghostSliders)
                {
                    if (pair.Key != ghost) pair.Key.Value = v / 100.0;
                    pair.Value.Text = v + "%";
                }
                if (overlay != null) overlay.GhostOpacity = v / 100f;
            };
            ghost.MouseUp += (s, e) => settings.Save();
            tips.SetToolTip(ghost, "How visible the ghost of your start view is (lower = more see-through)");
        }

        void ToggleOverlay()
        {
            ToggleOverlayCore();
            bool shown = overlay != null && overlay.Visible;
            foreach (var toggle in calibrationToggles) if (toggle.Checked != shown) toggle.Checked = shown;
        }

        static string CustomCursorFile { get { return Path.Combine(Settings.Dir, "custom-cursor.png"); } }
        Bitmap customCursor; // loaded from CustomCursorFile

        Bitmap CustomCursorImage()
        {
            if (customCursor == null && File.Exists(CustomCursorFile))
                try { customCursor = CalibrationOverlay.LoadCursorImage(CustomCursorFile); } catch (Exception) { }
            return customCursor;
        }

        // Shows the current crosshair settings on the overlay, if it's up.
        void ApplyCursor()
        {
            if (cursorPreview != null) cursorPreview.Invalidate();
            if (overlay == null) return;
            overlay.CustomCursor = settings.CustomCursor ? CustomCursorImage() : null;
            overlay.CursorSize = settings.CursorSize;
            if (overlay.Visible) overlay.Redraw();
        }

        void ToggleOverlayCore()
        {
            if (overlay != null && overlay.Visible) { overlay.Hide(); return; }
            if (overlay == null) overlay = new CalibrationOverlay();
            overlay.GhostOpacity = settings.GhostOpacity / 100f;
            overlay.CustomCursor = settings.CustomCursor ? CustomCursorImage() : null;
            overlay.CursorSize = settings.CursorSize;
            IntPtr game = FindTarget();
            bool ghost = File.Exists(StartViewFile);
            overlay.ShowOver(ViewArea(game), ghost ? StartViewFile : null,
                (ghost ? "Line the view up with the ghost, then press " : "Aim at your start spot, then press ") + Ui.KeyName(settings.Hotkey) +
                " to run.   " + Ui.KeyName(settings.OverlayHotkey) + " hides this.");
        }

        // Takes a new start view in 3 seconds (time to switch to the game).
        void RetakeStartView()
        {
            int left = 3;
            var t = new System.Windows.Forms.Timer { Interval = 1000 };
            SetStatus(StatusKind.Waiting, "Taking the start view in 3...", "Switch to the game and aim where your run starts.");
            t.Tick += (s, e) =>
            {
                if (--left > 0) { SetStatus(StatusKind.Waiting, "Taking the start view in " + left + "...", "Switch to the game and aim where your run starts."); return; }
                t.Stop();
                t.Dispose();
                bool ok = CalibrationOverlay.CaptureStartView(ViewArea(Native.GetForegroundWindow() == Handle ? FindTarget() : Native.GetForegroundWindow()), StartViewFile);
                SetStatus(ok ? StatusKind.Ready : StatusKind.Problem, ok ? "Start view saved" : "Couldn't take the start view",
                    ok ? "Press " + Ui.KeyName(settings.OverlayHotkey) + " to show the calibration overlay." : "");
                if (ok && overlay != null && overlay.Visible) { overlay.Hide(); ToggleOverlay(); }
            };
            t.Start();
        }

        CursorPreview cursorPreview;
        Toggle overlayPageToggle;

        void BuildCalibrationPage()
        {
            var p = Page(9, "Calibration", "Start every run from the same spot: a crosshair and a ghost of your start view over the game.");

            var ov = Card(p, "Overlay", 32, 100, 676, 158);
            overlayPageToggle = Add(ov, new Toggle { Text = "Show the overlay (" + Ui.KeyName(settings.OverlayHotkey) + ")" }, 18, 44, 270, 30);
            WireCalibrationToggle(overlayPageToggle);
            Label(ov, "Ghost of the start view", 300, 50, 150);
            GhostSlider(ov, 452, 44, 160);
            var retake = Add(ov, new RoundButton { Icon = "", Text = "Retake start view in 3 s" }, 18, 84, 240, 34);
            retake.Click += (s, e) => RetakeStartView();
            tips.SetToolTip(retake, "Switch to the game and aim where your run starts; the view is captured after 3 seconds");
            Label(ov, "It's also saved each time you start recording.", 270, 93, 390);
            Label(ov, "Before playing, line the game up with the ghost, then press " + Ui.KeyName(settings.Hotkey) + " to run.", 18, 128, 640);

            var ch = Card(p, "Crosshair", 32, 272, 676, 264);
            cursorPreview = Add(ch, new CursorPreview { GetImage = () => settings.CustomCursor ? CustomCursorImage() : null, GetSize = () => settings.CursorSize }, 18, 44, 200, 200);
            Label(ch, "Show", 240, 51, 56);
            var style = Add(ch, new Dropdown(), 300, 44, 240, 34);
            style.Items.Add("Built-in crosshair");
            style.Items.Add("My cursor image");
            var upload = Add(ch, new RoundButton { Style = BtnStyle.Primary, Icon = "", Text = "Upload cursor" }, 240, 90, 180, 34);
            var remove = Add(ch, new RoundButton { Text = "Remove" }, 428, 90, 112, 34);
            Label(ch, "Size", 240, 141, 56);
            var size = Num(ch, 4, 512, 0, 2, settings.CursorSize, 300, 134, 110);
            Label(ch, "px", 416, 141, 30);
            var note = Add(ch, new TextLabel { Sub = true, Wrap = true, Font = Ui.F(9f) }, 240, 182, 418, 62);
            Action sync = () =>
            {
                bool have = File.Exists(CustomCursorFile);
                if (!have) settings.CustomCursor = false;
                style.SelectedIndex = settings.CustomCursor ? 1 : 0;
                remove.Enabled = have;
                note.TextColor = null;
                note.Text = have ? "Your cursor is drawn at the centre of the game view, on top of the ghost."
                                 : "Upload a .png, .cur or .ico (a PNG with a see-through background works best). It's drawn at the centre of the game view.";
                ApplyCursor();
            };
            style.SelectedIndexChanged += (s, e) =>
            {
                bool custom = style.SelectedIndex == 1;
                if (custom && !File.Exists(CustomCursorFile)) { style.SelectedIndex = 0; UploadCursor(sync, note); return; }
                if (custom == settings.CustomCursor) return;
                settings.CustomCursor = custom;
                settings.Save();
                ApplyCursor();
            };
            upload.Click += (s, e) => UploadCursor(sync, note);
            remove.Click += (s, e) =>
            {
                if (customCursor != null) { customCursor.Dispose(); customCursor = null; }
                try { File.Delete(CustomCursorFile); } catch (IOException) { }
                settings.CustomCursor = false;
                settings.Save();
                sync();
            };
            size.ValueChanged += (s, e) => { settings.CursorSize = (int)size.Value; settings.Save(); ApplyCursor(); };
            tips.SetToolTip(size, "How big your cursor image is drawn, in pixels");
            sync();
        }

        // Copies a cursor or picture into Embris's folder (as PNG) and switches the overlay to it.
        void UploadCursor(Action sync, TextLabel note)
        {
            using (var dlg = new OpenFileDialog { Title = "Choose a cursor", Filter = "Cursors and pictures|*.png;*.cur;*.ico;*.gif;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (var img = CalibrationOverlay.LoadCursorImage(dlg.FileName)) img.Save(CustomCursorFile, System.Drawing.Imaging.ImageFormat.Png);
                    if (customCursor != null) { customCursor.Dispose(); customCursor = null; }
                    settings.CustomCursor = true;
                    settings.Save();
                    sync();
                }
                catch (Exception ex)
                {
                    note.TextColor = Ui.Danger;
                    note.Text = "Couldn't read that file: " + ex.Message;
                }
            }
        }

        // ---------- key binds: keys that spam or hold a click or a key, or play a macro ----------

        List<KeyBind> binds = new List<KeyBind>();
        readonly BindEngine bindEngine = new BindEngine();
        ItemList bindList;
        Toggle bindOn, bindBlock, bindOnlyGame;
        KeyBox bindTrigger, bindKey;
        Dropdown bindMode, bindAction, bindMacro;
        NumberBox bindEvery;
        TextLabel bindKeyLabel, bindMacroLabel, bindEveryLabel, bindEveryUnit, bindRate, bindSummary;
        Box bindEditor;
        bool loadingBind;

        // Hands the binds to the hook (a fresh copy, so editing never races with a key press) and saves them.
        void PushBinds()
        {
            bindEngine.StopAll();
            bindEngine.Binds = binds.ConvertAll(b => b.Copy()).ToArray();
            settings.Binds = KeyBind.SaveAll(binds);
        }

        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        IntPtr lastFront;
        bool lastFrontIsGame;

        // Whether the target app (the game) is the window in front. Called on the hook thread.
        bool GameInFront()
        {
            IntPtr front = Native.GetForegroundWindow();
            if (front == lastFront) return lastFrontIsGame;
            bool game = false;
            try
            {
                uint pid;
                GetWindowThreadProcessId(front, out pid);
                using (var p = Process.GetProcessById((int)pid))
                    game = p.ProcessName.Equals(settings.TargetProcess, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { }
            lastFront = front;
            lastFrontIsGame = game;
            return game;
        }

        KeyBind SelectedBind { get { int i = bindList.SelectedIndex; return i >= 0 && i < binds.Count ? binds[i] : null; } }

        void BuildKeyBindsPage()
        {
            var p = Page(10, "Key binds", "Make keys do things: hold R to spam clicks, press F to hold the mouse down, or play a preset.");
            var listCard = Tagged(Card(p, "Your binds", 32, 100, 300, 436), VFill);
            // All binds on/off, in the list's title row.
            var master = Add(listCard, new Toggle { Text = "All on", Checked = settings.BindsOn, Font = Ui.F(9f) }, 182, 12, 100, 26);
            master.CheckedChanged += (s, e) =>
            {
                settings.BindsOn = bindEngine.Enabled = master.Checked;
                if (!master.Checked) bindEngine.StopAll();
                settings.Save();
            };
            tips.SetToolTip(master, "Turn all key binds on or off");

            bindList = Add(listCard, Tagged(new ItemList { EmptyText = "No key binds yet. Click Add bind." }, VFill), 18, 46, 264, 318);
            bindList.SelectedIndexChanged += (s, e) => LoadBind();
            var add = Add(listCard, Tagged(new RoundButton { Style = BtnStyle.Primary, Icon = "", Text = "Add bind" }, AtBottom), 18, 380, 128, 38);
            add.Click += (s, e) =>
            {
                // A new bind starts on a key no other bind uses.
                Keys[] tryKeys = { Keys.R, Keys.F, Keys.G, Keys.V, Keys.X, Keys.C, Keys.Z, Keys.T, Keys.B };
                var key = Array.Find(tryKeys, k => !binds.Exists(b => b.Trigger == k) && !IsHotkey(k));
                binds.Add(new KeyBind { Trigger = key == Keys.None ? Keys.R : key });
                BindsChanged();
                bindList.SelectedIndex = binds.Count - 1;
            };
            var remove = Add(listCard, Tagged(new RoundButton { Icon = "", Text = "Remove" }, AtBottom), 154, 380, 128, 38);
            remove.Click += (s, e) =>
            {
                int i = bindList.SelectedIndex;
                if (i < 0 || i >= binds.Count) return;
                binds.RemoveAt(i);
                BindsChanged();
                bindList.SelectedIndex = Math.Min(i, binds.Count - 1);
            };

            var ed = bindEditor = Tagged(Card(p, "Bind", 344, 100, 364, 436), VFill);
            bindOn = Add(ed, new Toggle { Text = "On" }, 18, 44, 100, 30);
            bindOn.CheckedChanged += (s, e) => EditBind(b => b.On = bindOn.Checked);
            Label(ed, "When I press", 18, 91, 110);
            bindTrigger = Add(ed, new KeyBox { Key = Keys.R }, 130, 84, 216, 34);
            bindTrigger.KeyChanged += (s, e) =>
            {
                if (IsHotkey(bindTrigger.Key))
                {
                    var b = SelectedBind;
                    bindTrigger.Key = b != null ? b.Trigger : Keys.R;
                    SetStatus(StatusKind.Problem, "That key is one of Embris's hotkeys", "Pick a different key for the bind.");
                    return;
                }
                EditBind(b => b.Trigger = bindTrigger.Key);
            };
            Label(ed, "Mode", 18, 133, 110);
            bindMode = Add(ed, new Dropdown(), 130, 126, 216, 34);
            foreach (var n in KeyBind.ModeNames) bindMode.Items.Add(n);
            bindMode.SelectedIndexChanged += (s, e) => { if (bindMode.SelectedIndex >= 0) EditBind(b => b.Mode = (BindMode)bindMode.SelectedIndex); };
            Label(ed, "Do", 18, 175, 110);
            bindAction = Add(ed, new Dropdown(), 130, 168, 216, 34);
            foreach (var n in KeyBind.ActionNames) bindAction.Items.Add(n);
            bindAction.SelectedIndexChanged += (s, e) =>
            {
                if (bindAction.SelectedIndex < 0) return;
                EditBind(b =>
                {
                    b.Action = KeyBind.ActionOrder[bindAction.SelectedIndex];
                    // Playing a preset: start with the first one in the suite.
                    if (b.Action == BindAction.PlayMacro && b.Macro == "" && presets.Count > 0) b.Macro = presets[0].Name;
                });
            };
            bindKeyLabel = Label(ed, "Key", 18, 217, 110);
            bindKey = Add(ed, new KeyBox { Key = Keys.E }, 130, 210, 216, 34);
            bindKey.KeyChanged += (s, e) => EditBind(b => b.Key = bindKey.Key);
            bindMacroLabel = Label(ed, "Macro", 18, 217, 110);
            bindMacro = Add(ed, new Dropdown(), 130, 210, 216, 34);
            bindMacro.Opening += (s, e) => FillBindMacros();
            bindMacro.SelectedIndexChanged += (s, e) => { if (bindMacro.SelectedItem != null) EditBind(b => b.Macro = bindMacro.SelectedItem.ToString()); };
            bindEveryLabel = Label(ed, "Every", 18, 259, 110);
            bindEvery = Num(ed, 1, 3600000, 0, 5, 50, 130, 252, 110);
            bindEvery.ValueChanged += (s, e) => EditBind(b => b.EveryMs = (double)bindEvery.Value);
            bindEveryUnit = Label(ed, "ms", 246, 259, 30);
            bindRate = Add(ed, new TextLabel { Sub = true, Font = Ui.F(9f) }, 272, 259, 74, 22);
            bindBlock = Add(ed, new Toggle { Text = "Keep the key from the game" }, 18, 298, 328, 30);
            bindBlock.CheckedChanged += (s, e) => EditBind(b => b.Block = bindBlock.Checked);
            tips.SetToolTip(bindBlock, "On: the game never sees the key (R won't reload). Off: the game gets it too.");
            bindOnlyGame = Add(ed, new Toggle { Text = "Only while " + settings.TargetProcess + " is in front" }, 18, 332, 328, 30);
            bindOnlyGame.CheckedChanged += (s, e) => EditBind(b => b.OnlyInGame = bindOnlyGame.Checked);
            tips.SetToolTip(bindOnlyGame, "The app is the one chosen on the Macro page");
            bindSummary = Add(ed, new TextLabel { Sub = true, Wrap = true, Font = Ui.F(9f) }, 18, 372, 328, 48);
            tips.SetToolTip(bindEvery, "Time between clicks or key taps (50 ms = 20 a second)");
            // Picking a key shouldn't set off hotkeys or other binds.
            foreach (var box in new[] { bindTrigger, bindKey })
            {
                box.GotFocus += (s, e) => hotkeyHook.Paused = true;
                box.LostFocus += (s, e) => hotkeyHook.Paused = false;
            }

            RefreshBindList();
            if (binds.Count > 0) bindList.SelectedIndex = 0;
            LoadBind();
        }

        bool IsHotkey(Keys k)
        {
            return k == settings.Hotkey || k == settings.RecordHotkey || k == settings.ClickHotkey || k == settings.OverlayHotkey || presets.Exists(x => x.Hotkey == k);
        }

        void FillBindMacros()
        {
            var b = SelectedBind;
            bindMacro.Items.Clear();
            foreach (var pr in presets) bindMacro.Items.Add(pr.Name);
            bindMacro.SelectedIndex = b == null ? -1 : presets.FindIndex(x => x.Name.Equals(b.Macro, StringComparison.OrdinalIgnoreCase));
        }

        void RefreshBindList()
        {
            int keep = bindList.SelectedIndex;
            bindList.Items.Clear();
            foreach (var b in binds) bindList.Items.Add(b.Title);
            bindList.SelectedIndex = keep < binds.Count ? keep : binds.Count - 1;
            bindList.Invalidate();
        }

        void BindsChanged()
        {
            PushBinds();
            settings.Save();
            RefreshBindList();
            LoadBind();
        }

        void EditBind(Action<KeyBind> change)
        {
            var b = SelectedBind;
            if (loadingBind || b == null) return;
            change(b);
            PushBinds();
            settings.Save();
            if (bindList.SelectedIndex >= 0) bindList.Items[bindList.SelectedIndex] = b.Title;
            bindList.Invalidate();
            LoadBind();
        }

        // Shows the selected bind in the editor; fields that don't apply to its action are hidden.
        void LoadBind()
        {
            var b = SelectedBind;
            foreach (Control c in bindEditor.Controls) c.Enabled = b != null;
            if (b == null) { bindSummary.Text = "Add a bind, or pick one from the list."; return; }
            loadingBind = true;
            bindOn.Checked = b.On;
            bindTrigger.Key = b.Trigger;
            bindMode.SelectedIndex = (int)b.Mode;
            bindAction.SelectedIndex = Array.IndexOf(KeyBind.ActionOrder, b.Action);
            bindKey.Key = b.Key;
            bindEvery.Value = (decimal)Math.Max(1, Math.Min(3600000, b.EveryMs));
            bindBlock.Checked = b.Block;
            bindOnlyGame.Checked = b.OnlyInGame;
            bindOnlyGame.Text = "Only while " + settings.TargetProcess + " is in front";
            bool macro = b.Action == BindAction.PlayMacro;
            bindKeyLabel.Visible = bindKey.Visible = b.UsesKey;
            bindMacroLabel.Visible = bindMacro.Visible = macro;
            if (macro) FillBindMacros();
            bindEveryLabel.Visible = bindEvery.Visible = bindEveryUnit.Visible = bindRate.Visible = b.IsSpam && b.Mode != BindMode.Once;
            bindRate.Text = b.EveryMs > 0 ? (1000 / b.EveryMs).ToString(b.EveryMs >= 100 ? "0.#" : "0") + "/s" : "";
            bindSummary.Text = b.Describe();
            loadingBind = false;
        }

        void ShowCalibrationMenu(Control button)
        {
            var menu = NewMenu();
            AddMenuItem(menu, (overlay != null && overlay.Visible ? "Hide" : "Show") + " the calibration overlay (" + Ui.KeyName(settings.OverlayHotkey) + ")", ToggleOverlay, true);
            AddMenuItem(menu, "Retake the start view in 3 seconds", RetakeStartView, true);
            menu.Show(button, new Point(0, button.Height + Ui.S(4)));
        }
        void BuildHotkeyPage()
        {
            var p = Page(5, "Hotkeys", "Start and stop the macro from anywhere, even while Roblox is focused.");

            cardHotkey = Card(p, "Hotkeys", 32, 100, 676, 164);
            Label(cardHotkey, "Click a box, then press the key you want. Numpad keys work too. Hotkeys work in any app and are never recorded.", 18, 48, 640);
            Label(cardHotkey, "Run / stop the macro", 18, 82, 200);
            keyHotkey = Add(cardHotkey, new KeyBox { Key = settings.Hotkey }, 18, 104, 200, 34);
            keyHotkey.KeyChanged += (s, e) => SetHotkey(keyHotkey, false);
            Label(cardHotkey, "Start / stop recording", 236, 82, 200);
            keyRecordHotkey = Add(cardHotkey, new KeyBox { Key = settings.RecordHotkey }, 236, 104, 200, 34);
            keyRecordHotkey.KeyChanged += (s, e) => SetHotkey(keyRecordHotkey, true);
            Label(cardHotkey, "Calibration overlay", 454, 82, 204);
            var keyOverlay = Add(cardHotkey, new KeyBox { Key = settings.OverlayHotkey }, 454, 104, 204, 34);
            keyOverlay.KeyChanged += (s, e) =>
            {
                var k = keyOverlay.Key;
                if (k == settings.Hotkey || k == settings.RecordHotkey || k == settings.ClickHotkey || presets.Exists(x => x.Hotkey == k))
                {
                    keyOverlay.Key = settings.OverlayHotkey;
                    SetStatus(StatusKind.Problem, "That key is already used", "Pick a different key for each hotkey.");
                    return;
                }
                settings.OverlayHotkey = k;
                hotkeyHook.OverlayKeyCode = (int)k;
                settings.Save();
                calibrationToggle.Text = "Calibration (" + Ui.KeyName(k) + ")";
                overlayPageToggle.Text = "Show the overlay (" + Ui.KeyName(k) + ")";
            };
            tips.SetToolTip(keyOverlay, "Shows or hides the crosshair and the ghost of your start view over the game");
            // Let key pickers receive the hotkey itself instead of it toggling the macro.
            foreach (var box in new[] { keyHotkey, keyRecordHotkey, keyOverlay, keyAction, bKey, edKey, tlKey, tlAddTrack })
            {
                box.GotFocus += (s, e) => hotkeyHook.Paused = true;
                box.LostFocus += (s, e) => hotkeyHook.Paused = false;
            }

            // Recording: optionally keep every take.
            var recCard = Card(p, "Recording", 32, 278, 676, 104);
            var autoSave = Add(recCard, new Toggle { Text = "Save each recording to my macro suite", Checked = settings.AutoSaveRecordings }, 18, 44, 330, 30);
            autoSave.CheckedChanged += (s, e) => { settings.AutoSaveRecordings = autoSave.Checked; settings.Save(); };
            tips.SetToolTip(autoSave, "Each take is saved as \"Recording\" with the date and time, on the Presets page");
            // Frame sync: match Roblox's frame rate so every input lands on the same frame each replay.
            Label(recCard, "Frame sync", 362, 47, 90);
            var frameSync = Add(recCard, new Dropdown(), 452, 40, 206, 32);
            double displayMs, displayHz;
            long vblank;
            bool haveDisplay = DisplayTiming(out displayMs, out vblank, out displayHz);
            int[] rates = { 0, -1, 30, 60, 75, 120, 144, 165, 240, 360 };
            foreach (int r in rates)
                frameSync.Items.Add(r == 0 ? "Off" : r < 0 ? "Match my display" + (haveDisplay ? " (" + Math.Round(displayHz) + " fps)" : "") : r + " fps");
            frameSync.SelectedIndex = Math.Max(0, Array.IndexOf(rates, settings.FrameSync));
            if (Array.IndexOf(rates, settings.FrameSync) < 0) { frameSync.Items.Add(settings.FrameSync + " fps"); frameSync.SelectedIndex = frameSync.Items.Count - 1; }
            frameSync.SelectedIndexChanged += (s, e) =>
            {
                if (frameSync.SelectedIndex < rates.Length) settings.FrameSync = rates[frameSync.SelectedIndex];
                settings.Save();
            };
            tips.SetToolTip(frameSync, "Set this to Roblox's frame rate (Roblox menu > Settings > Maximum Frame Rate). Playback then sends every input in the middle of a frame, so jumps land on the same frame each replay.");
            Label(recCard, "Frame sync: set it to Roblox's Maximum Frame Rate so jumps land on the same frame every replay.", 18, 76, 640);

            var tipsCard = Card(p, "Tips", 32, 396, 676, 140);
            Add(tipsCard, new TextLabel
            {
                Sub = true, Wrap = true,
                Text = "•  To record gameplay: click into Roblox, press the record key, play, then press it again. Keys, clicks and camera movement are all recorded.\n" +
                       "•  Play it back by pressing the run key while you're in the game. Use the calibration overlay to start each run from the same spot.\n" +
                       "•  Background mode can't reach Roblox when it's behind other windows. Use \"Switch to it briefly\" for that."
            }, 18, 46, 640, 84);
        }

        void SetHotkey(KeyBox box, bool record)
        {
            Keys other = record ? settings.Hotkey : settings.RecordHotkey;
            if (box.Key == other || box.Key == settings.ClickHotkey || box.Key == settings.OverlayHotkey)
            {
                box.Key = record ? settings.RecordHotkey : settings.Hotkey;
                SetStatus(StatusKind.Problem, "That key is already used", "Pick a different key for each hotkey.");
                return;
            }
            if (record) settings.RecordHotkey = box.Key;
            else settings.Hotkey = box.Key;
            settings.Save();
            RegisterToggleHotkey();
        }

        void BuildSettingsPage()
        {
            var p = Page(6, "Settings", "How Embris behaves on your PC.");

            var window = Card(p, "Window & files", 32, 100, 676, 186);
            var closeToTray = Add(window, new Toggle { Text = "Keep running in the tray when I close the window", Checked = settings.CloseToTray }, 18, 44, 640, 30);
            closeToTray.CheckedChanged += (s, e) => { settings.CloseToTray = closeToTray.Checked; settings.Save(); };
            Label(window, "The hotkey keeps working from the tray. Click the tray icon to open Embris, or right-click it to exit.", 18, 72, 640);
            pinToggle = Add(window, new Toggle { Text = "Keep the window on top (same as the pin)", Checked = settings.Pinned }, 18, 100, 330, 30);
            // Window opacity: see what's behind Embris (handy pinned over a game).
            Label(window, "Window opacity", 360, 106, 104);
            var opacity = Add(window, new Slider { Value = (settings.WindowOpacity - 20) / 80.0 }, 466, 100, 140, 30);
            var opacityText = Add(window, new TextLabel { Text = settings.WindowOpacity + "%" }, 612, 100, 46, 30);
            opacity.ValueChanged += (s, e) =>
            {
                settings.WindowOpacity = 20 + (int)Math.Round(opacity.Value * 80);
                opacityText.Text = settings.WindowOpacity + "%";
                Opacity = settings.WindowOpacity / 100.0;
            };
            opacity.MouseUp += (s, e) => settings.Save();
            tips.SetToolTip(opacity, "Lower it to see what's behind Embris (20% to 100%)");
            pinToggle.CheckedChanged += (s, e) =>
            {
                if (settings.Pinned == pinToggle.Checked) return;
                settings.Pinned = pinToggle.Checked;
                settings.Save();
                ApplyPin();
            };

            var resetSize = Add(window, new RoundButton { Icon = "", Text = "Back to full size" }, 18, 138, 180, 34);
            resetSize.Click += (s, e) => ResetWindowSize();
            var link = Add(window, new RoundButton { Icon = "", Text = "Open .emb files with Embris" }, 206, 138, 250, 34);
            link.Click += (s, e) =>
            {
                Program.RegisterFileType(Application.ExecutablePath);
                SetStatus(StatusKind.Ready, ".emb files now open with Embris", "Double-click a macro file to add it to your suite.");
            };

            // Visuals: how the custom background is drawn.
            var visuals = Card(p, "Visuals", 32, 298, 676, 134);
            // Decoding: automatic graphics card, a specific one, or the processor.
            Label(visuals, "Video decoding", 18, 44, 250);
            var decoder = Add(visuals, new Dropdown(), 18, 66, 252, 34);
            var cards = Mf.GraphicsCards();
            decoder.Items.Add("Graphics card: automatic");
            foreach (var c in cards) decoder.Items.Add(c);
            decoder.Items.Add("Processor (no graphics card)");
            decoder.SelectedIndex = settings.BgDecoder == 1 ? decoder.Items.Count - 1 : Math.Max(0, cards.IndexOf(settings.BgCard) + 1);
            decoder.SelectedIndexChanged += (s, e) =>
            {
                int i = decoder.SelectedIndex;
                settings.BgDecoder = i == decoder.Items.Count - 1 ? 1 : 0;
                settings.BgCard = i >= 1 && i <= cards.Count ? cards[i - 1] : "";
                settings.Save();
                Background.UseGpu = settings.BgDecoder == 0;
                Background.Card = settings.BgCard;
                if (settings.BgFile != "") SetBackground(settings.BgFile, settings.BgSource); // restart the video with it
            };
            Label(visuals, "Smoothness", 284, 44, 150);
            var fps = Add(visuals, new Dropdown(), 284, 66, 152, 34);
            int[] rates = { 15, 24, 30, 60 };
            foreach (int r in rates) fps.Items.Add(r + " fps" + (r == 60 ? " (smoothest)" : r == 15 ? " (lightest)" : ""));
            fps.SelectedIndex = Math.Max(0, Array.IndexOf(rates, settings.BgFps));
            if (Array.IndexOf(rates, settings.BgFps) < 0) fps.SelectedIndex = 2;
            fps.SelectedIndexChanged += (s, e) => { settings.BgFps = rates[Math.Max(0, fps.SelectedIndex)]; Background.Fps = settings.BgFps; settings.Save(); };
            Label(visuals, "See-through cards", 450, 44, 208);
            var glass = Add(visuals, new Dropdown(), 450, 66, 208, 34);
            glass.Items.Add("Always");
            glass.Items.Add("Only when small");
            glass.Items.Add("Never (smoothest)");
            glass.SelectedIndex = settings.BgGlass;
            glass.SelectedIndexChanged += (s, e) => { settings.BgGlass = glass.SelectedIndex; settings.Save(); ApplyGlass(); };
            Label(visuals, "A big window redraws more on every video frame. Solid cards keep the video moving around them and stay smooth.", 18, 106, 640);
            tips.SetToolTip(decoder, "Which graphics card decodes and resizes the background video (the processor stays free)");
            tips.SetToolTip(fps, "60 is smoothest; lower uses less of your PC (while the game is in front it's 15 anyway)");

            var about = Card(p, "About", 32, 444, 676, 96);
            // All three buttons share the title row; the version and update news sit underneath.
            Add(about, new TextLabel { Text = "Embris v" + Ui.Version }, 18, 44, 120, 22);
            btnUpdate = Add(about, new RoundButton { Icon = "", Text = "Check for updates" }, 112, 10, 194, 32);
            btnUpdate.Click += (s, e) => UpdateClicked();
            lblUpdate = Add(about, new TextLabel { Sub = true, Font = Ui.F(9f) }, 146, 44, 512, 22);
            tips.SetToolTip(btnUpdate, "Gets the newest Embris from " + Updater.ReleasesPage);
            Label(about, "Settings and presets are saved in " + Settings.Dir, 18, 68, 640);
            var exit = Add(about, new RoundButton { Icon = "", Text = "Exit Embris" }, 316, 10, 150, 32);
            exit.Click += (s, e) => ExitApp();
            var uninstall = Add(about, new RoundButton { Icon = "", Text = "Uninstall Embris" }, 476, 10, 182, 32);
            uninstall.Click += (s, e) => Uninstall();
        }

        // ---------- updates (GitHub Releases) ----------

        RoundButton btnUpdate;
        TextLabel lblUpdate;
        ReleaseInfo newRelease; // a newer version found by the last check
        bool updateBusy;

        void UpdateClicked()
        {
            if (newRelease != null) InstallUpdate();
            else CheckForUpdates(true);
        }

        // Asks GitHub for the newest release. On start-up it's quiet unless there is a new version.
        async void CheckForUpdates(bool manual)
        {
            if (updateBusy) return;
            updateBusy = true;
            if (manual) { lblUpdate.TextColor = null; lblUpdate.Text = "Checking..."; }
            try
            {
                var r = await Updater.LatestAsync();
                if (IsDisposed) return;
                if (Updater.IsNewer(r))
                {
                    newRelease = r;
                    lblUpdate.TextColor = Theme.Current.Accent;
                    lblUpdate.Text = "Version " + r.Version.ToString(3) + " is out!";
                    btnUpdate.Text = Updater.CanInstall(r) ? "Update to v" + r.Version.ToString(3) : "Open download page";
                    sideFooter2.TextColor = Theme.Current.Accent;
                    sideFooter2.Text = "Update available: v" + r.Version.ToString(3);
                    sideFooter2.Cursor = Cursors.Hand;
                    if (!manual && tray != null && !Visible)
                        tray.ShowBalloonTip(4000, "Embris " + r.Version.ToString(3) + " is out", "Open Embris and go to Settings to update.", ToolTipIcon.Info);
                }
                else if (manual) lblUpdate.Text = r == null ? "No releases published yet." : "You have the newest version.";
            }
            catch (Exception ex)
            {
                if (manual && !IsDisposed) { lblUpdate.TextColor = Ui.Danger; lblUpdate.Text = "Couldn't reach GitHub: " + ex.GetBaseException().Message; }
            }
            finally { updateBusy = false; }
        }

        // Downloads the new installer and runs it; it closes Embris, updates it and opens it again.
        async void InstallUpdate()
        {
            var r = newRelease;
            if (updateBusy || r == null) return;
            if (!Updater.CanInstall(r)) { Process.Start(r.PageUrl); return; }
            string notes = r.Notes.Trim();
            if (notes.Length > 600) notes = notes.Substring(0, 600) + "...";
            var answer = MessageBox.Show(this,
                "Update Embris from v" + Ui.Version + " to v" + r.Version.ToString(3) + "?\n\n" +
                (notes != "" ? notes + "\n\n" : "") + "Embris will close, update and open again. Your settings and macros are kept.",
                "Update Embris", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
            if (running) Toggle();
            if (recordingNow) ToggleRecording();
            updateBusy = true;
            btnUpdate.Enabled = false;
            lblUpdate.TextColor = null;
            lblUpdate.Text = "Downloading...";
            try
            {
                string setup = await Updater.DownloadSetupAsync(r);
                settings.Save();
                Updater.RunSetup(setup);
                ExitApp();
            }
            catch (Exception ex)
            {
                lblUpdate.TextColor = Ui.Danger;
                lblUpdate.Text = "Download failed: " + ex.GetBaseException().Message;
                btnUpdate.Enabled = true;
            }
            finally { updateBusy = false; }
        }

        // Installed copies have the installer next to them as "Uninstall Embris.exe"; it asks, closes
        // Embris and removes everything. A copy that was never installed just gets its settings removed.
        void Uninstall()
        {
            string uninstaller = Path.Combine(Application.StartupPath, "Uninstall Embris.exe");
            if (File.Exists(uninstaller))
            {
                Process.Start(uninstaller, "/uninstall");
                return;
            }
            var answer = MessageBox.Show(this,
                "This copy of Embris isn't installed - it runs straight from:\n" + Application.StartupPath +
                "\n\nRemove its saved settings and presets and close Embris? You can then delete that folder yourself.",
                "Uninstall Embris", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
            skipSave = true;
            try { Directory.Delete(Settings.Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            ExitApp();
        }

        void BuildThemePage()
        {
            var p = Page(7, "Themes & Colors", "Pick a preset or make your own. Changes apply instantly.");

            var presetCard = Card(p, "Preset themes", 32, 100, 676, 136);
            tiles = new ThemeTile[Theme.Presets.Length];
            for (int i = 0; i < tiles.Length; i++)
            {
                var preset = Theme.Presets[i];
                tiles[i] = Add(presetCard, new ThemeTile { Preset = preset }, 18 + i * 108, 46, 98, 72);
                tiles[i].Click += (s, e) => Theme.Set(preset.Copy(preset.Name));
            }

            var custom = Card(p, "Custom colors", 32, 252, 400, 284);
            for (int i = 0; i < 5; i++)
            {
                int n = i;
                int y = 46 + i * 46;
                Add(custom, new TextLabel { Text = ColorNames[i] }, 18, y + 8, 110, 20);
                colorBoxes[i] = Add(custom, new ColorBox { Title = ColorNames[i] + " color" }, 130, y, 252, 36);
                colorBoxes[i].ValueChanged += (s, e) => SetCustomColor(n, colorBoxes[n].Value);
            }

            var reset = Add(presetCard, new RoundButton { Icon = "", Text = "Reset to " + Theme.Presets[0].Name, Font = Ui.F(9f) }, 498, 10, 160, 30);
            reset.Click += (s, e) => Theme.Set(Theme.Default);

            // Custom background: a picture, GIF or video from a file or a link.
            var bg = Card(p, "Background", 448, 252, 260, 284);
            // Good places for animated (video) wallpapers: download one there, then Choose file.
            Label(bg, "Animated:", 18, 46, 64);
            foreach (var site in new[] { new { Name = "MoeWalls", Url = "https://moewalls.com/", X = 84 }, new { Name = "MotionBGs", Url = "https://motionbgs.com", X = 164 } })
            {
                var url = site.Url;
                var link = Add(bg, new RoundButton { Style = BtnStyle.Chip, Text = site.Name, Font = Ui.F(8.5f) }, site.X, 40, 78, 24);
                link.Click += (s, e) => { try { Process.Start(url); } catch (Exception) { } };
                tips.SetToolTip(link, "Animated wallpapers: download a video from " + url + ", then click Choose file");
            }
            var choose = Add(bg, new RoundButton { Icon = "", Text = "Choose file" }, 18, 68, 224, 34);
            choose.Click += (s, e) => ChooseBackgroundFile();
            choose.Top = Ui.S(70);
            tips.SetToolTip(choose, "A picture, GIF or video from your PC");
            txtBgLink = Add(bg, new TextField { Placeholder = "Paste an image, video or page link" }, 18, 110, 224, 34);
            txtBgLink.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; UseBackgroundLink(); } };
            var use = Add(bg, new RoundButton { Style = BtnStyle.Primary, Text = "Use link" }, 18, 150, 108, 34);
            use.Click += (s, e) => UseBackgroundLink();
            var remove = Add(bg, new RoundButton { Text = "Remove" }, 134, 150, 108, 34);
            remove.Click += (s, e) => SetBackground(null, null);
            Label(bg, "Darken", 18, 199, 56);
            numBgDim = Num(bg, 0, 100, 0, 5, settings.BgDim, 76, 192, 84);
            Label(bg, "%", 166, 199, 20);
            numBgDim.ValueChanged += (s, e) =>
            {
                settings.BgDim = (int)numBgDim.Value;
                Backdrop.Dim = settings.BgDim;
                settings.Save();
                Backdrop.Retint();
                Invalidate(true);
            };
            lblBg = Add(bg, new TextLabel { Sub = true, Wrap = true, Font = Ui.F(8.5f) }, 18, 234, 224, 40);
            SyncThemeControls();
        }

        // ---------- custom background ----------

        TextField txtBgLink;
        NumberBox numBgDim;
        TextLabel lblBg;

        void ChooseBackgroundFile()
        {
            using (var dlg = new OpenFileDialog { Filter = "Pictures, GIFs and videos|*.png;*.jpg;*.jpeg;*.jfif;*.bmp;*.gif;*.webp;*.avif;*.mp4;*.m4v;*.mov;*.webm;*.wmv;*.avi;*.mkv|All files|*.*" })
                if (dlg.ShowDialog(this) == DialogResult.OK) FetchBackground(dlg.FileName);
        }

        void UseBackgroundLink()
        {
            string link = txtBgLink.Text.Trim();
            if (link.Length == 0) { lblBg.Text = "Paste a link first (right-click a picture or video > Copy address, or a page link)."; return; }
            FetchBackground(link);
        }

        void FetchBackground(string source)
        {
            lblBg.TextColor = null;
            lblBg.Text = File.Exists(source) ? "Loading..." : "Downloading...";
            Background.Fetch(source, path => SetBackground(path, source), error =>
            {
                lblBg.TextColor = Ui.Danger;
                lblBg.Text = error;
            });
        }

        void SetBackground(string path, string source)
        {
            string old = settings.BgFile;
            string error = path == null ? null : Background.Load(path);
            if (path == null) Background.Stop();
            if (error != null) { lblBg.TextColor = Ui.Danger; lblBg.Text = error; return; }
            settings.BgFile = path ?? "";
            settings.BgSource = source ?? "";
            settings.Save();
            // Old downloads aren't needed any more.
            if (!string.IsNullOrEmpty(old) && old != path && old.StartsWith(Background.Folder, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            lblBg.TextColor = null;
            lblBg.Text = path == null ? "No background. Pick a file or paste a link." : "Showing " + Path.GetFileName(source ?? path) + ".";
            Invalidate(true);
        }

        // ---------- theme ----------

        void SetCustomColor(int index, Color c)
        {
            var t = Theme.Current.Copy("Custom");
            switch (index)
            {
                case 0: t.Bg = c; break;
                case 1: t.Surface = c; break;
                case 2: t.Accent = c; break;
                case 3: t.Text = c; break;
                default: t.Border = c; break;
            }
            Theme.Set(t);
        }

        void SyncThemeControls()
        {
            var t = Theme.Current;
            Color[] values = { t.Bg, t.Surface, t.Accent, t.Text, t.Border };
            for (int i = 0; i < 5; i++) colorBoxes[i].Value = values[i];
            foreach (var tile in tiles) tile.Selected = tile.Preset.Name == t.Name;
        }

        void OnThemeChanged()
        {
            settings.SetTheme(Theme.Current);
            settings.Save();
            SyncThemeControls();
            ApplyChrome();
            Backdrop.Retint();
            Invalidate(true);
        }

        static int ColorRef(Color c) { return c.R | (c.G << 8) | (c.B << 16); }

        // Colors the Windows title bar to match (Windows 11).
        void ApplyChrome()
        {
            var t = Theme.Current;
            BackColor = t.Bg;
            if (IsHandleCreated)
            {
                int dark = t.IsLight ? 0 : 1, caption = ColorRef(t.Sidebar), text = ColorRef(t.Text), border = ColorRef(t.Border);
                Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
                Native.DwmSetWindowAttribute(Handle, 35, ref caption, 4);
                Native.DwmSetWindowAttribute(Handle, 36, ref text, 4);
                Native.DwmSetWindowAttribute(Handle, 34, ref border, 4);
            }
            Icon = Ui.AppIcon;
            tray.Icon = Ui.AppIcon;
        }

        // ---------- macro page behaviour ----------

        void SetClickButton(bool right)
        {
            rightClick = right;
            btnLeft.Selected = !right;
            btnRight.Selected = right;
            btnLeft.Invalidate();
            btnRight.Invalidate();
        }

        void UpdateIntervalHint()
        {
            decimal s = numInterval.Value;
            lblIntervalHint.Text = s < 60 ? "= " + s.ToString("0.###") + " sec"
                : s < 3600 ? "= " + (s / 60).ToString("0.#") + " min"
                : "= " + (s / 3600).ToString("0.##") + " hours";
        }

        // Waiting for idle only matters when the macro switches windows.
        void UpdateIdleEnabled()
        {
            bool on = cboMode.SelectedIndex == 1;
            chkIdle.Enabled = on;
            numIdle.Enabled = on;
        }

        // The single key / click settings don't apply while a sequence plays instead.
        void UpdateSequenceEnabled()
        {
            if (running) return;
            cardKey.Enabled = cardClick.Enabled = !chkUseRec.Checked;
        }

        // Lists every app with a visible window; keeps the saved target even if it isn't running.
        void RefreshTargets()
        {
            var selected = cboTarget.SelectedItem as TargetItem;
            string current = selected != null ? selected.ProcessName : settings.TargetProcess;
            cboTarget.Items.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int self = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == self || p.MainWindowHandle == IntPtr.Zero || p.MainWindowTitle == "") continue;
                    if (!seen.Add(p.ProcessName)) continue;
                    cboTarget.Items.Add(new TargetItem { ProcessName = p.ProcessName, Title = p.MainWindowTitle });
                }
                catch { }
            }
            if (!seen.Contains(current)) cboTarget.Items.Add(new TargetItem { ProcessName = current });
            int index = 0;
            for (int i = 0; i < cboTarget.Items.Count; i++)
                if (string.Equals(((TargetItem)cboTarget.Items[i]).ProcessName, current, StringComparison.OrdinalIgnoreCase)) index = i;
            cboTarget.SelectedIndex = -1;
            cboTarget.SelectedIndex = index;
        }

        void StartPick(RoundButton button, NumberBox x, NumberBox y, Action done)
        {
            if (pickTimer.Enabled) return;
            pickButton = button;
            pickX = x;
            pickY = y;
            pickDone = done;
            pickText = button.Text;
            pickCountdown = 3;
            button.Enabled = false;
            button.Text = "Move... 3";
            pickTimer.Start();
        }

        void PickTick()
        {
            pickCountdown--;
            if (pickCountdown > 0) { pickButton.Text = "Move... " + pickCountdown; return; }
            pickTimer.Stop();
            var p = Cursor.Position;
            pickX.Value = p.X;
            pickY.Value = p.Y;
            if (pickDone != null) pickDone();
            pickButton.Text = pickText;
            pickButton.Enabled = true;
        }

        // Puts every control back in line with the settings (after loading a preset).
        void SyncControls()
        {
            chkKey.Checked = settings.PressKey;
            keyAction.Key = settings.ActionKey;
            numHold.Value = settings.HoldMs;
            chkClick.Checked = settings.ClickEnabled;
            SetClickButton(settings.RightClick);
            numX.Value = settings.ClickX;
            numY.Value = settings.ClickY;
            numInterval.Value = settings.IntervalSec;
            cboMode.SelectedIndex = settings.Mode;
            chkIdle.Checked = settings.WaitIdle;
            numIdle.Value = settings.IdleSec;
            chkUseRec.Checked = settings.UseRecording;
            editors[0].Speed.Value = (decimal)settings.Speed;
            editors[0].Repeat.Value = settings.Repeat;
            cboTarget.SelectedIndex = -1;
            RefreshTargets();
            UpdateIdleEnabled();
            UpdateSequenceEnabled();
            UpdateSequenceViews();
        }

        // ---------- presets ----------

        KeyBox presetHotkeyBox;
        RoundButton presetHotkeyClear;
        int suitePreset = -1;

        void RefreshPresetList()
        {
            int keep = presetList.SelectedIndex;
            presetList.Items.Clear();
            foreach (var p in presets)
            {
                string extra = (p.Hotkey != Keys.None ? "   [" + Ui.KeyName(p.Hotkey) + "]" : "") +
                               (p.Hotstrings.Count > 0 ? "   (" + p.Hotstrings.Count + " hotstrings)" : "");
                presetList.Items.Add(p.Name + extra);
            }
            presetList.SelectedIndex = keep < presets.Count ? keep : -1;
            UpdateSuiteKeys();
            if (mini != null) SyncMini();
        }

        // Hotstrings come from every macro in the suite that has them.
        void UpdateHotstrings()
        {
            var old = hotkeyHook.HotstringSets;
            var sets = new List<HotstringSet>();
            foreach (var p in presets)
            {
                var items = p.Hotstrings;
                if (items.Count == 0) continue;
                items.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
                int toggle;
                int.TryParse(p.Get("HotstringToggle"), out toggle);
                var prev = Array.Find(old, h => h.Name == p.Name);
                sets.Add(new HotstringSet { Name = p.Name, Window = p.Get("HotstringWindow") ?? "", ToggleKey = toggle, Items = items,
                                            On = prev != null ? prev.On : toggle == 0 });
            }
            hotkeyHook.HotstringSets = sets.ToArray();
        }

        void OnHotstringToggled(HotstringSet hs)
        {
            SetStatus(hs.On ? StatusKind.Running : StatusKind.Ready, "Hotstrings " + (hs.On ? "on" : "off") + ": " + hs.Name,
                hs.On ? "Type the short text" + (hs.Window != "" ? " in " + hs.Window : "") + ". Press " + Ui.KeyName((Keys)hs.ToggleKey) + " to turn them off."
                      : "Press " + Ui.KeyName((Keys)hs.ToggleKey) + " to turn them back on.");
        }

        void UpdateSuiteKeys()
        {
            var keys = new List<int>();
            foreach (var p in presets) if (p.Hotkey != Keys.None) keys.Add((int)p.Hotkey);
            hotkeyHook.SuiteKeys = keys.ToArray();
            UpdateHotstrings();
        }

        void SetPresetHotkey(Keys key)
        {
            int i = presetList.SelectedIndex;
            if (i < 0) return;
            if (key != Keys.None && (key == settings.Hotkey || key == settings.RecordHotkey))
            {
                presetHotkeyBox.Key = presets[i].Hotkey;
                SetStatus(StatusKind.Problem, "That key is already used", Ui.KeyName(key) + " is the run or record hotkey.");
                return;
            }
            foreach (var p in presets) if (key != Keys.None && p.Hotkey == key) p.Hotkey = Keys.None; // one macro per key
            presets[i].Hotkey = key;
            PresetStore.Save(presets);
            RefreshPresetList();
            SetStatus(StatusKind.Ready, key == Keys.None ? "Hotkey removed" : Ui.KeyName(key) + " runs \"" + presets[i].Name + "\"",
                key == Keys.None ? "" : "Press it anywhere to start or stop this macro.");
        }

        // A macro's own hotkey: load and start it, or stop it if it's the one running.
        void OnSuiteKey(int key) { RunSuiteIndex(presets.FindIndex(p => (int)p.Hotkey == key)); }

        // A key bind that plays a preset or recording from the suite: start it, stop it, or either.
        void BindMacro(string name, MacroCommand cmd)
        {
            int i = presets.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) { SetStatus(StatusKind.Problem, "\"" + name + "\" isn't in your suite", "Pick another preset for that key bind."); return; }
            bool runningThis = running && suitePreset == i;
            if (cmd == MacroCommand.Stop) { if (runningThis) Stop(); return; }
            if (cmd == MacroCommand.Start && runningThis) return;
            RunSuiteIndex(i); // starts it (or stops it, for a toggle press while it runs)
        }

        void RunSuiteIndex(int i)
        {
            if (i < 0 || recordingNow) return;
            if (running)
            {
                bool same = suitePreset == i;
                Stop();
                if (same) return;
            }
            presetList.SelectedIndex = i;
            LoadPreset();
            Toggle();
            suitePreset = running ? i : -1;
        }

        void SavePreset()
        {
            string name = txtPresetName.Text.Replace("[", "(").Replace("]", ")").Trim();
            if (name == "")
            {
                SetStatus(StatusKind.Problem, "Name your preset", "Type a name in the Preset name box first.");
                return;
            }
            ReadControls();
            SavePresetLines(name, settings.MacroLines());
            SetStatus(StatusKind.Ready, "Saved \"" + name + "\"", "Load it any time from the Presets page.");
        }

        void SavePresetLines(string name, List<string> lines)
        {
            int index = presets.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var preset = new Preset { Name = name, Lines = lines };
            if (index >= 0)
            {
                preset.Lines.AddRange(presets[index].Lines.FindAll(Preset.IsExtra)); // keep its hotkey and hotstrings
                presets[index] = preset;
            }
            else { presets.Add(preset); index = presets.Count - 1; }
            PresetStore.Save(presets);
            lastPresetName = name;
            RefreshPresetList();
            presetList.SelectedIndex = index;
            UpdateSequenceViews();
        }

        void LoadPreset()
        {
            int i = presetList.SelectedIndex;
            if (i < 0 || running) return;
            settings.Apply(Settings.ParseLines(presets[i].Lines));
            sequence = RecStep.Parse(settings.Recording);
            lastPresetName = presets[i].Name;
            timelineNeedsFit = true;
            settings.Save();
            SyncControls();
            ResetUndo();
            SetStatus(StatusKind.Ready, "Loaded \"" + presets[i].Name + "\"", "Press " + Ui.KeyName(settings.Hotkey) + " or click Run macro to start it.");
        }

        void DeletePreset()
        {
            int i = presetList.SelectedIndex;
            if (i < 0) return;
            if (MessageBox.Show(this, "Delete the preset \"" + presets[i].Name + "\"?", "Embris", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            presets.RemoveAt(i);
            PresetStore.Save(presets);
            RefreshPresetList();
            txtPresetName.Text = "";
        }

        // ---------- macro builder & sequence ----------

        void SetStepType(int type)
        {
            stepType = type;
            for (int i = 0; i < 3; i++)
            {
                stepChips[i].Selected = i == type;
                stepChips[i].Invalidate();
                stepPanels[i].Visible = i == type;
            }
        }

        void SetBuilderButton(bool right)
        {
            builderRight = right;
            bLeft.Selected = !right;
            bRight.Selected = right;
            bLeft.Invalidate();
            bRight.Invalidate();
        }

        void AddBuilderStep()
        {
            foreach (var n in new[] { bHold, bX, bY, bWait }) n.Commit();
            if (stepType == 0)
            {
                int k = (int)bKey.Key;
                sequence.Add(new RecStep { Kind = 0, Code = k });
                sequence.Add(new RecStep { Kind = 1, Code = k, Delay = (int)bHold.Value });
            }
            else if (stepType == 1)
            {
                int b = builderRight ? 1 : 0, x = (int)bX.Value, y = (int)bY.Value;
                sequence.Add(new RecStep { Kind = 2, Code = b, X = x, Y = y });
                sequence.Add(new RecStep { Kind = 3, Code = b, X = x, Y = y, Delay = 40 });
            }
            else sequence.Add(new RecStep { Kind = 4, Delay = (int)bWait.Value });
            chkUseRec.Checked = true;
            SequenceChanged();
        }

        // Removes the last step, taking a press's matching release with it.
        void RemoveLastStep()
        {
            int n = sequence.Count;
            if (n == 0) return;
            var last = sequence[n - 1];
            bool pair = n >= 2 && (last.Kind == 1 || last.Kind == 3) && sequence[n - 2].Kind == last.Kind - 1 && sequence[n - 2].Code == last.Code;
            sequence.RemoveRange(pair ? n - 2 : n - 1, pair ? 2 : 1);
            SequenceChanged();
        }

        void SequenceChanged()
        {
            settings.Recording = RecStep.Serialize(sequence);
            settings.UseRecording = chkUseRec.Checked;
            settings.Save();
            UpdateSequenceViews();
            Remember();
        }

        // ---------- undo / redo of sequence edits ----------

        readonly List<string> undoStack = new List<string>(), redoStack = new List<string>();
        string undoCurrent;           // the sequence as it is now (serialized)
        const int UndoLimit = 200;

        // Called after every change: the state before it goes on the undo stack.
        void Remember()
        {
            string now = RecStep.Serialize(sequence);
            if (undoCurrent == null) { undoCurrent = now; UpdateUndoButtons(); return; }
            if (now == undoCurrent) return;
            undoStack.Add(undoCurrent);
            // Keep at most UndoLimit steps and about 100 MB (long gameplay recordings are big).
            long size = 0;
            foreach (var s in undoStack) size += s.Length;
            while (undoStack.Count > 1 && (undoStack.Count > UndoLimit || size > 50000000)) { size -= undoStack[0].Length; undoStack.RemoveAt(0); }
            redoStack.Clear();
            undoCurrent = now;
            UpdateUndoButtons();
        }

        // A fresh start (loading a macro): nothing to undo.
        void ResetUndo()
        {
            undoStack.Clear();
            redoStack.Clear();
            undoCurrent = RecStep.Serialize(sequence);
            UpdateUndoButtons();
        }

        void Undo() { StepHistory(undoStack, redoStack, "Undone"); }
        void Redo() { StepHistory(redoStack, undoStack, "Redone"); }

        void StepHistory(List<string> from, List<string> to, string what)
        {
            if (from.Count == 0 || running || recordingNow) return;
            to.Add(undoCurrent);
            undoCurrent = from[from.Count - 1];
            from.RemoveAt(from.Count - 1);
            sequence = RecStep.Parse(undoCurrent);
            settings.Recording = undoCurrent;
            settings.Save();
            UpdateSequenceViews();
            undoCurrent = RecStep.Serialize(sequence);
            UpdateUndoButtons();
            SetStatus(StatusKind.Ready, what, undoStack.Count + " more to undo, " + redoStack.Count + " to redo.");
        }

        void UpdateUndoButtons()
        {
            foreach (var e in editors)
            {
                if (e == null) continue;
                e.Undo.Enabled = undoStack.Count > 0;
                e.Redo.Enabled = redoStack.Count > 0;
            }
        }

        // Ctrl+Z undoes, Ctrl+Shift+Z (or Ctrl+Y) redoes, except in text boxes, which keep their own.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool inText = ActiveControl is TextBoxBase || (ActiveControl != null && ActiveControl.ContainsFocus && FindFocusedTextBox(ActiveControl));
            if (!inText)
            {
                if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
                if (keyData == (Keys.Control | Keys.Shift | Keys.Z) || keyData == (Keys.Control | Keys.Y)) { Redo(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        static bool FindFocusedTextBox(Control c)
        {
            if (c is TextBoxBase && c.Focused) return true;
            foreach (Control child in c.Controls) if (child.ContainsFocus && FindFocusedTextBox(child)) return true;
            return false;
        }

        static int LinesThatFit(Control label)
        {
            return Math.Max(2, label.Height / TextRenderer.MeasureText("X", Mono).Height);
        }

        string FormatSteps(int maxLines, bool newestLast)
        {
            var lines = new List<string>();
            double t = 0;
            foreach (var s in sequence)
            {
                t += s.Delay / 1000.0;
                lines.Add(("+" + t.ToString("0.00") + "s").PadRight(10) + s.Describe());
            }
            if (lines.Count <= maxLines) return string.Join("\n", lines.ToArray());
            int shown = maxLines - 1, hidden = lines.Count - shown;
            if (newestLast) return "... " + hidden + " earlier steps\n" + string.Join("\n", lines.GetRange(hidden, shown).ToArray());
            return string.Join("\n", lines.GetRange(0, shown).ToArray()) + "\n... and " + hidden + " more steps";
        }

        void UpdateSequenceViews()
        {
            if (stepTable == null) return;
            RefreshStepTable();
            if (!timelineEditing)
            {
                timeline.Load(sequence);
                if (timelineNeedsFit && currentPage == 3 && !isCompact) { timeline.Fit(); timelineNeedsFit = false; }
                LoadTimelineProps();
            }
            string summary = SequenceSummary();
            foreach (var e in editors) e.Summary.Text = summary;
            UpdateTimeLabels();
            lblBuilderSteps.Text = sequence.Count == 0 ? "No steps yet. Pick Key, Click or Wait above and click Add step."
                : FormatSteps(LinesThatFit(lblBuilderSteps), true);
        }

        bool recordAppend;
        string savedTake; // the name a recording was auto-saved under, for the status line
        NumberBox builderRepeat, builderSpeed;

        [StructLayout(LayoutKind.Sequential)]
        struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags, Count, Timeout; }
        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FlashInfo info);

        [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITaskbarList3
        {
            void HrInit(); void AddTab(IntPtr w); void DeleteTab(IntPtr w); void ActivateTab(IntPtr w); void SetActiveAlt(IntPtr w);
            void MarkFullscreenWindow(IntPtr w, [MarshalAs(UnmanagedType.Bool)] bool full);
            void SetProgressValue(IntPtr w, ulong done, ulong total);
            void SetProgressState(IntPtr w, int state); // 0 none, 4 error (red), 8 paused (yellow)
        }
        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")] class TaskbarListClass { }

        ITaskbarList3 taskbar;
        readonly System.Windows.Forms.Timer blinkTimer = new System.Windows.Forms.Timer { Interval = 550 };
        bool blinkOn;

        // While recording, Embris's taskbar button blinks red so you can see it's on; stopping clears it
        // completely (Windows' own flashing could leave the button highlighted).
        void FlashTaskbar(bool on)
        {
            try
            {
                if (taskbar == null)
                {
                    taskbar = (ITaskbarList3)new TaskbarListClass();
                    taskbar.HrInit();
                    blinkTimer.Tick += (s, e) => { blinkOn = !blinkOn; SetTaskbarRed(blinkOn); };
                }
            }
            catch (Exception) { taskbar = null; }
            if (on) { blinkOn = true; SetTaskbarRed(true); blinkTimer.Start(); }
            else
            {
                blinkTimer.Stop();
                SetTaskbarRed(false);
                var info = new FlashInfo { Size = (uint)Marshal.SizeOf(typeof(FlashInfo)), Window = Handle, Flags = 0 }; // FLASHW_STOP, just in case
                FlashWindowEx(ref info);
            }
        }

        void SetTaskbarRed(bool red)
        {
            if (taskbar == null) return;
            try
            {
                if (red) { taskbar.SetProgressState(Handle, 4); taskbar.SetProgressValue(Handle, 100, 100); }
                else taskbar.SetProgressState(Handle, 0);
            }
            catch (Exception) { }
        }

        RoundButton[] RecordButtons { get { return new[] { btnRecord, editors[0].Record, editors[1].Record, miniRecord, btnBuilderRecord }; } }

        void ToggleRecording() { ToggleRecording(false); }

        // append = true (the builder's Record button) adds the recording to the end of the sequence.
        void ToggleRecording(bool append)
        {
            if (!recordingNow)
            {
                // Recording stops anything that's playing: the macro, a test, the auto clicker.
                if (running) Stop();
                if (clicker.Running) { clicker.Stop(); ClickerStopped(); }
                recordingNow = true;
                recordAppend = append;
                hotkeyHook.StartRecording(Handle);
                FlashTaskbar(true);
                startViewTaken = false;
                startViewTimer.Start();
                foreach (var b in RecordButtons) { b.Style = BtnStyle.Danger; b.Text = "Stop"; b.Invalidate(); }
                btnRun.Enabled = miniRun.Enabled = false;
                SetStatus(StatusKind.Problem, "Recording...", "Play in the game, then press " + Ui.KeyName(settings.RecordHotkey) + " or click Stop.");
                return;
            }
            recordingNow = false;
            FlashTaskbar(false);
            startViewTimer.Stop();
            var steps = hotkeyHook.StopRecording();
            foreach (var b in RecordButtons)
            {
                string text;
                b.Style = BtnStyle.Secondary;
                b.Text = recordTexts.TryGetValue(b, out text) ? text : "Record";
                b.Invalidate();
            }
            btnRun.Enabled = miniRun.Enabled = true;
            if (steps.Count > 0)
            {
                if (recordAppend && sequence.Count > 0)
                {
                    steps[0].Delay = Math.Max(steps[0].Delay, 100);
                    sequence.AddRange(steps);
                }
                else sequence = steps;
                chkUseRec.Checked = true;
                timelineNeedsFit = true;
                SequenceChanged();
                if (settings.AutoSaveRecordings)
                {
                    // Keep this take in the suite (with the current settings, playing the sequence).
                    string name = "Recording " + DateTime.Now.ToString("MMM d, HH:mm:ss");
                    ReadControls();
                    bool use = settings.UseRecording;
                    settings.UseRecording = true;
                    var lines = settings.MacroLines();
                    settings.UseRecording = use;
                    SavePresetLines(name, lines);
                    savedTake = name;
                }
                if (!recordAppend && currentPage != 3) ShowPage(2);
                else if (currentPage == 3) { timeline.Fit(); timelineNeedsFit = false; }
            }
            UpdateStatus();
            if (savedTake != null)
            {
                SetStatus(StatusKind.Ready, "Saved \"" + savedTake + "\"", "It's in your macro suite on the Presets page.");
                savedTake = null;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == (int)Program.OpenMessage)
            {
                ShowFromTray();
                try
                {
                    string file = File.Exists(Program.OpenRequestFile) ? File.ReadAllText(Program.OpenRequestFile).Trim() : "";
                    File.Delete(Program.OpenRequestFile);
                    if (file.Length > 0 && File.Exists(file)) OpenScriptFile(file, true);
                }
                catch (IOException) { }
            }
            // While the window edge is being dragged the background isn't rescaled on every step
            // (the current frame stretches); it's redone once when you let go.
            if (m.Msg == 0x231) Background.Sizing = true;         // WM_ENTERSIZEMOVE
            else if (m.Msg == 0x232)                              // WM_EXITSIZEMOVE
            {
                Background.SizingDone();
                if (settleTimer.Enabled) { settleTimer.Stop(); RelayoutInOnePaint(); }
            }
            base.WndProc(ref m);
        }

        // ---------- hotkey ----------

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyChrome();
            RegisterToggleHotkey();
        }

        void RegisterToggleHotkey()
        {
            hotkeyHook.KeyCode = (int)settings.Hotkey;
            hotkeyHook.RecordKeyCode = (int)settings.RecordHotkey;
            UpdateStatus();
            UpdateSequenceViews();
        }

        // ---------- running the macro ----------

        void ReadControls()
        {
            foreach (var n in new[] { numIdle, numHold, numInterval, numX, numY, builderRepeat, builderSpeed, editors[0].Speed, editors[0].Repeat, editors[1].Speed, editors[1].Repeat }) n.Commit();
            settings.PressKey = chkKey.Checked;
            settings.ActionKey = keyAction.Key;
            settings.HoldMs = (int)numHold.Value;
            settings.ClickEnabled = chkClick.Checked;
            settings.RightClick = rightClick;
            settings.ClickX = (int)numX.Value;
            settings.ClickY = (int)numY.Value;
            settings.IntervalSec = numInterval.Value;
            settings.Mode = cboMode.SelectedIndex;
            settings.WaitIdle = chkIdle.Checked;
            settings.IdleSec = numIdle.Value;
            settings.UseRecording = chkUseRec.Checked;
            settings.Speed = (double)editors[0].Speed.Value;
            settings.Repeat = (int)editors[0].Repeat.Value;
            settings.Recording = RecStep.Serialize(sequence);
            var target = cboTarget.SelectedItem as TargetItem;
            if (target != null) settings.TargetProcess = target.ProcessName;
        }

        void Toggle()
        {
            if (recordingNow) { ToggleRecording(); return; }
            if (running) { Stop(); return; }
            ReadControls();
            if (settings.UseRecording && sequence.Count == 0)
            {
                SetStatus(StatusKind.Problem, "Nothing to play", "Record or build a sequence first, or turn off \"Play the sequence\".");
                return;
            }
            if (!settings.UseRecording && !settings.PressKey && !settings.ClickEnabled)
            {
                SetStatus(StatusKind.Problem, "Nothing to do", "Turn on \"Press a key\" or \"Click at a position\" first.");
                return;
            }
            settings.Save();
            running = true;
            actionCount = 0;
            actionTimer.Interval = (int)(settings.IntervalSec * 1000);
            actionTimer.Start();
            SetEditable(false);
            DoAction(); // run once right away
        }

        void Stop()
        {
            running = false;
            testing = false;
            suitePreset = -1;
            actionTimer.Stop();
            idleTimer.Stop();
            SetEditable(true);
            UpdateStatus();
        }

        void SetEditable(bool on)
        {
            foreach (var card in new[] { cardTarget, cardTiming, cardKey, cardClick, cardHotkey, cardSaved, cardBuilder, stepsCard, propsCard, tlCard, tlPropsCard })
                card.Enabled = on;
            btnRecord.Enabled = miniRecord.Enabled = on;
            foreach (var e in editors)
            {
                e.Record.Enabled = e.Save.Enabled = e.Use.Enabled = on;
                e.Test.Enabled = on;
                e.StopTest.Enabled = !on;
            }
            foreach (Control c in new Control[] { miniKey, miniInterval, miniSeq, miniMode, miniPresets, miniLoad }) c.Enabled = on;
            foreach (var b in new[] { btnRun, miniRun })
            {
                b.Style = on ? BtnStyle.Primary : BtnStyle.Danger;
                b.Icon = on ? "" : "";
                b.Text = on ? "Run macro" : "Stop macro";
                b.Invalidate();
            }
            if (on)
            {
                UpdateIdleEnabled();
                UpdateSequenceEnabled();
            }
        }

        IntPtr FindTarget()
        {
            foreach (var p in Process.GetProcessesByName(settings.TargetProcess))
                if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
            return IntPtr.Zero;
        }

        void DoAction()
        {
            if (idleTimer.Enabled || playing) return; // already waiting, or a sequence is still playing
            if (settings.Mode == 1 && settings.WaitIdle && UserBusy())
            {
                SetStatus(StatusKind.Waiting, "Waiting for you to pause",
                    settings.IdleSec + "s with no input and nothing held down, then it runs.");
                idleTimer.Start();
                return;
            }
            RunAction();
        }

        void IdleTick()
        {
            if (!running) { idleTimer.Stop(); return; }
            if (UserBusy()) return;
            idleTimer.Stop();
            RunAction();
        }

        bool UserBusy()
        {
            return Native.IdleMs() < settings.IdleSec * 1000 || Native.AnyInputHeld();
        }

        void RunAction()
        {
            IntPtr target = FindTarget();
            if (target == IntPtr.Zero)
            {
                SetStatus(StatusKind.Problem, settings.TargetProcess + " not found",
                    "Waiting for it to open... (checked " + DateTime.Now.ToString("T") + ")");
                return;
            }

            if (settings.UseRecording)
            {
                StartPlayback(target, new List<RecStep>(sequence), settings.Speed, settings.Repeat);
                return;
            }

            // The single click and key press play as a tiny sequence, so long holds never freeze the window.
            var steps = new List<RecStep>();
            if (settings.ClickEnabled)
            {
                int b = settings.RightClick ? 1 : 0;
                steps.Add(new RecStep { Kind = 2, Code = b, X = settings.ClickX, Y = settings.ClickY, Delay = 30 });
                steps.Add(new RecStep { Kind = 3, Code = b, X = settings.ClickX, Y = settings.ClickY, Delay = 40 });
            }
            if (settings.PressKey)
            {
                steps.Add(new RecStep { Kind = 0, Code = (int)settings.ActionKey });
                steps.Add(new RecStep { Kind = 1, Code = (int)settings.ActionKey, Delay = settings.HoldMs });
            }
            StartPlayback(target, steps, 1, 1);
        }

        void StartPlayback(IntPtr target, List<RecStep> steps, double speed, int repeat)
        {
            playing = true;
            playMs = 0;
            playTimer.Start();
            bool switchMode = settings.Mode == 1;
            if (!switchMode && target != Native.GetForegroundWindow() && settings.TargetProcess.StartsWith("Roblox", StringComparison.OrdinalIgnoreCase))
                SetStatus(StatusKind.Waiting, "Roblox isn't in front",
                    "Roblox ignores input sent in the background. Choose \"Switch to it briefly\" on the Macro page to play while you use other apps.");
            else if (testing)
                SetStatus(StatusKind.Running, "Testing the sequence on " + settings.TargetProcess, (repeat > 1 ? repeat + " times, " : "") + "at " + speed.ToString("0.##") + "x. Click Stop to end it.");
            else if (settings.UseRecording)
                SetStatus(StatusKind.Running, "Playing sequence on " + settings.TargetProcess, steps.Count + " steps" + (repeat > 1 ? ", " + repeat + " times" : "") + "...");
            var t = new Thread(() =>
            {
                Play(target, steps, speed, switchMode, repeat);
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        playing = false;
                        playTimer.Stop();
                        timeline.Playhead = -1;
                        UpdateTimeLabels();
                        actionCount++;
                        if (testing) { Stop(); SetStatus(StatusKind.Ready, "Test finished", "Change anything you like and test again."); }
                        else if (running) UpdateStatus();
                    }));
                }
                catch (InvalidOperationException) { } // window already closed
            });
            t.IsBackground = true;
            t.Start();
        }

        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

        // Waits until a point on the playback timeline: sleeps while far away, then spins for the last
        // couple of milliseconds. Timing against the start (not step to step) means small delays
        // never add up, so long recordings stay in sync. Pressing Stop ends the wait right away.
        void WaitUntil(Stopwatch clock, double dueMs)
        {
            while (running)
            {
                double left = dueMs - clock.Elapsed.TotalMilliseconds;
                if (left <= 0) return;
                // Sleep only while far away: if Windows keeps the timer coarse (16 ms), a sleep
                // could overshoot, so the last stretch yields and spins instead.
                if (left > 20) Thread.Sleep(1);
                else if (left > 2.5) Thread.Sleep(0);
                else Thread.SpinWait(200);
            }
        }

        [DllImport("dwmapi.dll")] static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, byte[] info);

        // The display's exact refresh period and its last vertical blank, from the desktop compositor
        // (DWM_TIMING_INFO: rateRefresh at 4, qpcRefreshPeriod at 12, qpcVBlank at 28; 292 bytes).
        public static bool DisplayTiming(out double periodMs, out long vblankTicks, out double hz)
        {
            periodMs = 0; vblankTicks = 0; hz = 0;
            var info = new byte[292];
            BitConverter.GetBytes(292).CopyTo(info, 0);
            try { if (DwmGetCompositionTimingInfo(IntPtr.Zero, info) != 0) return false; }
            catch (Exception) { return false; }
            uint num = BitConverter.ToUInt32(info, 4), den = BitConverter.ToUInt32(info, 8);
            long period = BitConverter.ToInt64(info, 12);
            vblankTicks = BitConverter.ToInt64(info, 28);
            if (period <= 0 || num == 0 || den == 0) return false;
            periodMs = period * 1000.0 / Stopwatch.Frequency;
            hz = (double)num / den;
            return true;
        }

        // Frame sync: the game reads input once per frame, so an input sent near the edge of a frame
        // lands in the frame before or after at random, and a jump a frame early or late lands
        // somewhere else. With frame sync every input goes out in the middle of a frame, the same
        // number of whole frames after the start as in the recording, so each replay hits the same frames.
        // Returns the frame length (0 = off) and where the first frame's middle is on the play clock.
        static double FrameGrid(int frameSync, long clockStartTicks, out double firstMs)
        {
            firstMs = 0;
            if (frameSync == 0) return 0;
            double periodMs, hz;
            long vblank;
            bool display = DisplayTiming(out periodMs, out vblank, out hz);
            // A frame rate other than the display's: its own grid, with no display timing to follow.
            if (frameSync > 0 && (!display || Math.Abs(hz - frameSync) > 1)) return 1000.0 / frameSync;
            if (!display) return 1000.0 / 60;
            // The game's frames follow the display's: start in the middle of one, a little after now.
            double vblankMs = (vblank - clockStartTicks) * 1000.0 / Stopwatch.Frequency;
            double k = Math.Ceiling((5 - vblankMs) / periodMs);
            firstMs = vblankMs + k * periodMs + periodMs / 2;
            return periodMs;
        }

        // Runs on a worker thread so long sequences don't freeze the window.
        void Play(IntPtr target, List<RecStep> steps, double speed, bool switchMode, int repeat)
        {
            IntPtr previous = Native.GetForegroundWindow();
            Point oldCursor = Cursor.Position;
            bool switched = switchMode && target != previous;
            if (switched)
            {
                Input.Focus(target);
                Thread.Sleep(150);
            }
            // Real input whenever the game is in front (Roblox ignores background messages),
            // so pressing the hotkey while playing always works, whatever the send mode.
            bool direct = switchMode || previous == target;
            var heldKeys = new HashSet<int>();
            var heldButtons = new Dictionary<int, Point>();
            // Gameplay recordings move the mouse step by step; clicks then happen wherever that
            // movement put the cursor, instead of jumping (which would jolt a locked camera).
            bool hasMoves = steps.Exists(s => s.Kind == 5);
            timeBeginPeriod(1); // 1 ms sleep resolution while playing
            var priority = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            // The game in front gets Windows' attention; raise Embris while it plays so inputs land on time.
            var proc = Process.GetCurrentProcess();
            var oldClass = proc.PriorityClass;
            try { proc.PriorityClass = ProcessPriorityClass.High; } catch (Exception) { }
            long clockStart = Stopwatch.GetTimestamp();
            var clock = Stopwatch.StartNew();
            double firstFrame;
            double frame = FrameGrid(settings.FrameSync, clockStart, out firstFrame);
            double at = 0, due = 0; // at: the recording's own time; due: when it goes out
            playClock = clock;
            playSpeed = speed;
            for (int round = 0; round < repeat && running; round++)
            {
                playRoundStart = frame > 0 ? firstFrame + at : at; // the time display and playhead follow this round
                foreach (var s in steps)
                {
                    if (!running) break;
                    at += s.Delay / speed;
                    // Frame sync: the nearest whole frame after the start, sent mid-frame.
                    due = frame > 0 ? firstFrame + Math.Round(at / frame) * frame : at;
                    WaitUntil(clock, due);
                    if (!running) break;
                    if (s.Kind <= 1)
                    {
                        if (direct) Input.Key((Keys)s.Code, !s.IsDown);
                        else Input.PostKeyEvent(target, (Keys)s.Code, !s.IsDown);
                        if (s.IsDown) heldKeys.Add(s.Code); else heldKeys.Remove(s.Code);
                    }
                    else if (s.Kind <= 3)
                    {
                        if (direct)
                        {
                            if (!hasMoves && s.X != RecStep.NoPosition) Native.SetCursorPos(s.X, s.Y);
                            Input.MouseButton(s.Code == 1, !s.IsDown);
                        }
                        else if (s.X == RecStep.NoPosition) Input.PostMouseEvent(target, Cursor.Position.X, Cursor.Position.Y, s.Code == 1, !s.IsDown);
                        else Input.PostMouseEvent(target, s.X, s.Y, s.Code == 1, !s.IsDown);
                        if (s.IsDown) heldButtons[s.Code] = new Point(s.X, s.Y); else heldButtons.Remove(s.Code);
                    }
                    else if (s.Kind == 5 && direct) Input.MoveBy(s.X, s.Y);
                    else if (s.Kind == 6 && s.Text != null)
                    {
                        if (direct) Input.TypeText(s.Text);
                        else Input.PostText(target, s.Text);
                    }
                }
            }
            Thread.CurrentThread.Priority = priority;
            try { proc.PriorityClass = oldClass; } catch (Exception) { }
            timeEndPeriod(1);
            // Release anything still held so no key or button gets stuck.
            foreach (int k in heldKeys)
            {
                if (direct) Input.Key((Keys)k, true);
                else Input.PostKeyEvent(target, (Keys)k, true);
            }
            foreach (var b in heldButtons)
            {
                if (direct) Input.MouseButton(b.Key == 1, true);
                else Input.PostMouseEvent(target, b.Value.X, b.Value.Y, b.Key == 1, true);
            }
            if (switched)
            {
                Thread.Sleep(50);
                Native.SetCursorPos(oldCursor.X, oldCursor.Y);
                Input.Focus(previous);
            }
        }

        void SetStatus(StatusKind kind, string text, string hint)
        {
            status.Kind = miniStatus.Kind = kind;
            status.Text = miniStatus.Text = text;
            lblHint.Text = miniHint.Text = hint;
            miniSummary.Text = MacroSummary();
            if (tray != null) tray.Text = "Embris - " + (running ? "running" : "stopped");
        }

        void UpdateStatus()
        {
            if (status == null || recordingNow) return;
            string key = Ui.KeyName(settings.Hotkey);
            if (running)
                SetStatus(StatusKind.Running, (settings.UseRecording ? "Playing sequence on " : "Running on ") + settings.TargetProcess,
                    actionCount + " run(s), last at " + DateTime.Now.ToString("T") + ". Press " + key + " to stop.");
            else
                SetStatus(StatusKind.Ready, "Ready", "Press " + key + " anywhere, or click Run macro.");
        }

        // ---------- tray ----------

        void BuildTray()
        {
            tray = new NotifyIcon { Text = "Embris", Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
            var menu = new ContextMenuStrip { Renderer = new UiMenuRenderer(), ShowImageMargin = false, Font = Ui.F(9.75f), Padding = new Padding(Ui.S(3)) };
            menu.Items.Add("Open Embris", null, (s, e) => ShowFromTray());
            var toggleItem = menu.Items.Add("Start macro", null, (s, e) => Toggle());
            menu.Items.Add("Exit Embris", null, (s, e) => ExitApp());
            foreach (ToolStripItem item in menu.Items)
            {
                item.AutoSize = false;
                item.Size = new Size(Ui.S(190), Ui.S(32));
            }
            menu.Opening += (s, e) => toggleItem.Text = recordingNow ? "Stop recording" : running ? "Stop macro" : "Start macro";
            tray.ContextMenuStrip = menu;
        }

        void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            if (restoreFull) { restoreFull = false; ResetWindowSize(); }
            Activate();
        }

        void HideToTray()
        {
            if (isCompact) restoreFull = true;
            Hide();
            if (trayTipShown) return;
            trayTipShown = true;
            tray.ShowBalloonTip(2000, "Embris is still running", "Click the Embris icon in the tray to open it. Right-click it to exit.", ToolTipIcon.Info);
        }

        void ExitApp()
        {
            exiting = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Closing the window keeps Embris (and its hotkey) running in the tray, unless turned off in Settings.
            if (e.CloseReason == CloseReason.UserClosing && settings.CloseToTray && !exiting)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            if (recordingNow) ToggleRecording();
            clicker.Stop();
            bindEngine.StopAll(); // let go of anything a key bind is holding
            if (overlay != null) overlay.Close();
            running = false;
            if (!skipSave)
            {
                ReadControls();
                settings.Save();
            }
            hotkeyHook.Stop();
            tray.Visible = false;
            base.OnFormClosing(e);
        }
    }

    static class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        public static readonly uint OpenMessage = RegisterWindowMessage("Embris.OpenRequest");
        public static string OpenRequestFile { get { return Path.Combine(Settings.Dir, "open-request.txt"); } }

        [STAThread]
        static void Main(string[] args)
        {
            Native.SetProcessDPIAware();
            KeepFullSpeedWhenMinimized();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string file = args.Length > 0 && File.Exists(args[0]) ? args[0] : null;

            // Only one Embris at a time: a second launch (like double-clicking a .emb file) hands its
            // file to the running one and brings it to the front.
            bool first;
            using (var mutex = new Mutex(true, "Embris.SingleInstance", out first))
            {
                if (!first)
                {
                    try { File.WriteAllText(OpenRequestFile, file ?? ""); } catch (IOException) { }
                    Native.PostMessage((IntPtr)0xFFFF, OpenMessage, IntPtr.Zero, IntPtr.Zero); // HWND_BROADCAST
                    return;
                }
                var form = new MainForm();
                if (file != null) form.Shown += (s, e) => form.OpenScriptFile(file, true);
                Application.Run(form);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }
        [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

        // Windows slows down apps whose window is minimized or hidden ("power throttling"): its timers
        // get coarse (about 16 ms) and its threads may run on slower cores. That would make a macro
        // played while Embris is minimized or in the tray drift from the recording, so opt out.
        static void KeepFullSpeedWhenMinimized()
        {
            var state = new PowerThrottlingState { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 }; // execution speed, timer resolution: off
            try { SetProcessInformation(GetCurrentProcess(), 4, ref state, Marshal.SizeOf(typeof(PowerThrottlingState))); } // ProcessPowerThrottling
            catch (EntryPointNotFoundException) { } // older Windows doesn't throttle anyway
        }

        // Makes .emb files open with this Embris (current user only, no admin needed).
        public static void RegisterFileType(string exePath)
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\.emb")) k.SetValue("", "Embris.Script");
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\Embris.Script"))
            {
                k.SetValue("", "Embris macro");
                using (var icon = k.CreateSubKey("DefaultIcon")) icon.SetValue("", "\"" + exePath + "\",0");
                using (var cmd = k.CreateSubKey(@"shell\open\command")) cmd.SetValue("", "\"" + exePath + "\" \"%1\"");
            }
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
        }

        [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
