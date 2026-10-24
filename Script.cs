// Embris scripts (.emb): a plain-text form of a macro, easy to read, edit and send to friends.
// Also understands AutoHotkey hotstring lines (":*:d1::desert 1") so .ahk hotstring files import too.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Embris
{
    class ScriptResult
    {
        public string Name = "";
        public Keys Hotkey = Keys.None;
        public Settings Settings = new Settings { UseRecording = true, Mode = 1, WaitIdle = false };
        public List<RecStep> Steps = new List<RecStep>();
        public string HotstringWindow = "";
        public Keys HotstringToggle = Keys.None;
        public List<KeyValuePair<string, string>> Hotstrings = new List<KeyValuePair<string, string>>();
        public List<string> Errors = new List<string>();

        public Preset ToPreset()
        {
            Settings.UseRecording = true;
            Settings.Recording = RecStep.Serialize(Steps);
            var p = new Preset { Name = Name, Lines = Settings.MacroLines() };
            p.Hotkey = Hotkey;
            if (Hotstrings.Count > 0)
            {
                p.Set("HotstringWindow", HotstringWindow);
                p.Set("HotstringToggle", HotstringToggle == Keys.None ? null : ((int)HotstringToggle).ToString());
                foreach (var h in Hotstrings) p.Lines.Add("Hotstring=" + h.Key + "\t" + h.Value);
            }
            return p;
        }
    }

    static class EmbScript
    {
        public const string Extension = ".emb";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Regex AhkHotstring = new Regex(@"^:([^:]*):(.+?)::(.*)$");
        static readonly Regex AhkToggle = new Regex(@"^(\w+)::\s*\w+\s*:=\s*!\s*\w+", RegexOptions.IgnoreCase);
        static readonly Regex AhkWindow = new Regex(@"WinActive\(\s*""([^""]+)""", RegexOptions.IgnoreCase);

        public const string Template =
@"# Embris macro script (.emb). Lines starting with # are notes.
name: My macro
hotkey: F9          # optional: press it anywhere to run this macro
every: 60           # seconds between runs
target: RobloxPlayerBeta
mode: switch        # switch = bring the game to the front, background = don't

steps:
  tap Space         # press and release (or: hold W 500)
  wait 250          # milliseconds
  type hello        # types text
  click left 640 360
  move 10 -5        # turn the camera

hotstrings:         # type the short text and it turns into the long one
  window: Roblox    # only in this window
  toggle: F5        # this key turns the hotstrings on and off
  d1 = desert 1
";

        // ---------------------------------------------------------------- writing

        public static string FromPreset(Preset p)
        {
            var s = new Settings();
            s.Apply(Settings.ParseLines(p.Lines));
            var steps = s.UseRecording ? RecStep.Parse(s.Recording) : SingleActionSteps(s);
            var sb = new StringBuilder();
            sb.AppendLine("# Embris macro script (.emb) - open it in Embris, or double-click it once Embris is installed.");
            sb.AppendLine("name: " + p.Name);
            if (p.Hotkey != Keys.None) sb.AppendLine("hotkey: " + Ui.KeyName(p.Hotkey));
            sb.AppendLine("every: " + s.IntervalSec.ToString("0.###", Inv));
            sb.AppendLine("target: " + s.TargetProcess);
            sb.AppendLine("mode: " + (s.Mode == 1 ? "switch" : "background"));
            if (s.Mode == 1) sb.AppendLine("wait idle: " + (s.WaitIdle ? s.IdleSec.ToString("0.##", Inv) : "off"));
            sb.AppendLine("speed: " + s.Speed.ToString("0.####", Inv));
            if (s.Repeat != 1) sb.AppendLine("repeat: " + s.Repeat);
            sb.AppendLine();
            sb.AppendLine("steps:");
            for (int i = 0; i < steps.Count; i++)
            {
                var st = steps[i];
                if (st.Kind == 4) { sb.AppendLine("  wait " + st.Delay); continue; }
                if (st.Delay > 0) sb.AppendLine("  wait " + st.Delay);
                var next = i + 1 < steps.Count ? steps[i + 1] : null;
                if (st.Kind == 0 && next != null && next.Kind == 1 && next.Code == st.Code)
                {
                    sb.AppendLine(next.Delay == 30 ? "  tap " + Ui.KeyName((Keys)st.Code) : "  hold " + Ui.KeyName((Keys)st.Code) + " " + next.Delay);
                    i++;
                }
                else if (st.Kind == 2 && next != null && next.Kind == 3 && next.Code == st.Code && next.X == st.X && next.Y == st.Y)
                {
                    sb.AppendLine("  click " + Button(st) + Position(st) + (next.Delay != 40 ? " " + next.Delay : ""));
                    i++;
                }
                else if (st.Kind == 0) sb.AppendLine("  down " + Ui.KeyName((Keys)st.Code));
                else if (st.Kind == 1) sb.AppendLine("  up " + Ui.KeyName((Keys)st.Code));
                else if (st.Kind == 2) sb.AppendLine("  mousedown " + Button(st) + Position(st));
                else if (st.Kind == 3) sb.AppendLine("  mouseup " + Button(st) + Position(st));
                else if (st.Kind == 5) sb.AppendLine("  move " + st.X + " " + st.Y);
                else if (st.Kind == 6) sb.AppendLine("  type " + st.Text);
            }
            var hs = p.Hotstrings;
            if (hs.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("hotstrings:");
                string window = p.Get("HotstringWindow");
                if (!string.IsNullOrEmpty(window)) sb.AppendLine("  window: " + window);
                int toggle;
                if (int.TryParse(p.Get("HotstringToggle"), out toggle)) sb.AppendLine("  toggle: " + Ui.KeyName((Keys)toggle));
                foreach (var h in hs) sb.AppendLine("  " + h.Key + " = " + h.Value);
            }
            return sb.ToString();
        }

        static string Button(RecStep st) { return st.Code == 1 ? "right" : "left"; }
        static string Position(RecStep st) { return st.X == RecStep.NoPosition ? "" : " " + st.X + " " + st.Y; }

        static List<RecStep> SingleActionSteps(Settings s)
        {
            var list = new List<RecStep>();
            if (s.ClickEnabled)
            {
                int b = s.RightClick ? 1 : 0;
                list.Add(new RecStep { Kind = 2, Code = b, X = s.ClickX, Y = s.ClickY });
                list.Add(new RecStep { Kind = 3, Code = b, X = s.ClickX, Y = s.ClickY, Delay = 40 });
            }
            if (s.PressKey)
            {
                list.Add(new RecStep { Kind = 0, Code = (int)s.ActionKey });
                list.Add(new RecStep { Kind = 1, Code = (int)s.ActionKey, Delay = s.HoldMs });
            }
            return list;
        }

        // ---------------------------------------------------------------- reading

        public static ScriptResult Parse(string text, string fallbackName)
        {
            var r = new ScriptResult { Name = fallbackName };
            string section = "";
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int n = 0; n < lines.Length; n++)
            {
                string raw = lines[n];
                var win = AhkWindow.Match(raw); // AutoHotkey "#If WinActive(...)" (a comment line otherwise)
                if (win.Success) { r.HotstringWindow = win.Groups[1].Value; continue; }
                string line = StripComment(raw).Trim();
                if (line.Length == 0) continue;
                try
                {
                    // AutoHotkey bits, so hotstring scripts like ":*:d1::desert 1" import as they are.
                    var m = AhkHotstring.Match(line);
                    if (m.Success) { r.Hotstrings.Add(new KeyValuePair<string, string>(m.Groups[2].Value, m.Groups[3].Value)); continue; }
                    m = AhkToggle.Match(line);
                    if (m.Success) { r.HotstringToggle = Key(m.Groups[1].Value); continue; }

                    if (line.StartsWith("#") || line == "{" || line == "}" || line.StartsWith("SendMode") || line.StartsWith("SetWorkingDir")) continue;

                    string lower = line.ToLowerInvariant();
                    if (lower == "steps:" || lower == "hotstrings:") { section = lower; continue; }
                    if (section == "steps:") { ParseStep(line, r.Steps); continue; }
                    if (section == "hotstrings:")
                    {
                        string k, v;
                        if (Setting(line, out k, out v) && (k == "window" || k == "toggle"))
                        {
                            if (k == "window") r.HotstringWindow = v;
                            else r.HotstringToggle = Key(v);
                            continue;
                        }
                        int eq = line.IndexOf('=');
                        if (eq <= 0) throw new FormatException("expected  short = long text");
                        r.Hotstrings.Add(new KeyValuePair<string, string>(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim()));
                        continue;
                    }
                    string key, value;
                    if (!Setting(line, out key, out value)) throw new FormatException("unknown line");
                    ApplySetting(r, key, value);
                }
                catch (Exception ex)
                {
                    r.Errors.Add("Line " + (n + 1) + ": " + ex.Message + "  (" + raw.Trim() + ")");
                }
            }
            return r;
        }

        static string StripComment(string line)
        {
            // "#" starts a note only at the start or after a space, so keys like "#" in text survive.
            int i = line.IndexOf(" #");
            string t = line.TrimStart();
            if (t.StartsWith("#")) return "";
            if (t.StartsWith(";")) return "";
            return i >= 0 ? line.Substring(0, i) : line;
        }

        static bool Setting(string line, out string key, out string value)
        {
            int c = line.IndexOf(':');
            key = value = null;
            if (c <= 0) return false;
            key = line.Substring(0, c).Trim().ToLowerInvariant();
            value = line.Substring(c + 1).Trim();
            return true;
        }

        static void ApplySetting(ScriptResult r, string key, string value)
        {
            var s = r.Settings;
            switch (key)
            {
                case "name": r.Name = value.Replace("[", "(").Replace("]", ")"); break;
                case "hotkey": r.Hotkey = value.Length == 0 || value.Equals("none", StringComparison.OrdinalIgnoreCase) ? Keys.None : Key(value); break;
                case "every": s.IntervalSec = Math.Max(0.001m, Number(value.TrimEnd('s', 'S'))); break;
                case "target":
                    // Mac/Linux scripts name the app ("Roblox") or window ("Sober"); here it's the process.
                    s.TargetProcess = value.Equals("Roblox", StringComparison.OrdinalIgnoreCase) || value.Equals("Sober", StringComparison.OrdinalIgnoreCase) ? "RobloxPlayerBeta" : value;
                    break;
                case "mode": s.Mode = value.StartsWith("b", StringComparison.OrdinalIgnoreCase) ? 0 : 1; break;
                case "wait idle":
                    s.WaitIdle = !value.Equals("off", StringComparison.OrdinalIgnoreCase);
                    if (s.WaitIdle) s.IdleSec = Number(value);
                    break;
                case "speed":
                    decimal sp = Number(value.TrimEnd('x', 'X'));
                    if (sp <= 0) throw new FormatException("speed must be more than 0");
                    s.Speed = Settings.ClampSpeed((double)sp);
                    break;
                case "repeat": s.Repeat = (int)Math.Max(1, Math.Min(int.MaxValue, Number(value))); break;
                default: throw new FormatException("unknown setting \"" + key + "\"");
            }
        }

        static void ParseStep(string line, List<RecStep> steps)
        {
            int sp = line.IndexOf(' ');
            string cmd = (sp < 0 ? line : line.Substring(0, sp)).ToLowerInvariant();
            string rest = sp < 0 ? "" : line.Substring(sp + 1).Trim();
            var a = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            switch (cmd)
            {
                case "tap":
                case "press":
                    {
                        Keys k = Key(rest);
                        steps.Add(new RecStep { Kind = 0, Code = (int)k });
                        steps.Add(new RecStep { Kind = 1, Code = (int)k, Delay = 30 });
                        break;
                    }
                case "hold":
                    {
                        if (a.Length < 2) throw new FormatException("use: hold KEY milliseconds");
                        Keys k = Key(string.Join(" ", a, 0, a.Length - 1));
                        steps.Add(new RecStep { Kind = 0, Code = (int)k });
                        steps.Add(new RecStep { Kind = 1, Code = (int)k, Delay = (int)Number(a[a.Length - 1]) });
                        break;
                    }
                case "down": steps.Add(new RecStep { Kind = 0, Code = (int)Key(rest) }); break;
                case "up": steps.Add(new RecStep { Kind = 1, Code = (int)Key(rest) }); break;
                case "wait":
                case "sleep": steps.Add(new RecStep { Kind = 4, Delay = (int)Number(rest.TrimEnd('m', 's')) }); break;
                case "type":
                case "send": steps.Add(new RecStep { Kind = 6, Text = rest }); break;
                case "move":
                    if (a.Length != 2) throw new FormatException("use: move dx dy");
                    steps.Add(new RecStep { Kind = 5, X = (int)Number(a[0]), Y = (int)Number(a[1]) });
                    break;
                case "click":
                case "mousedown":
                case "mouseup":
                    {
                        int b = a.Length > 0 && a[0].Equals("right", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                        int first = a.Length > 0 && (a[0].Equals("left", StringComparison.OrdinalIgnoreCase) || b == 1) ? 1 : 0;
                        int x = RecStep.NoPosition, y = RecStep.NoPosition, hold = 40;
                        if (a.Length >= first + 2) { x = (int)Number(a[first]); y = (int)Number(a[first + 1]); }
                        if (a.Length >= first + 3) hold = (int)Number(a[first + 2]);
                        if (cmd != "mouseup") steps.Add(new RecStep { Kind = 2, Code = b, X = x, Y = y });
                        if (cmd != "mousedown") steps.Add(new RecStep { Kind = 3, Code = b, X = x, Y = y, Delay = cmd == "click" ? hold : 0 });
                        break;
                    }
                default: throw new FormatException("unknown step \"" + cmd + "\"");
            }
        }

        static decimal Number(string s)
        {
            decimal v;
            if (!decimal.TryParse(s.Trim(), NumberStyles.Number, Inv, out v)) throw new FormatException("\"" + s + "\" isn't a number");
            return v;
        }

        static Keys Key(string name)
        {
            Keys k;
            if (!Ui.TryParseKey(name, out k)) throw new FormatException("unknown key \"" + name + "\"");
            return k;
        }
    }
}
