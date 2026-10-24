// Key binds: keys that do something while held, on a toggle, or once per press: spam or hold a mouse
// button or a key, or play a macro from the suite. The keyboard hook feeds them; each running action
// has its own thread with exact timing.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Embris
{
    enum BindMode { Held, Toggle, Once }
    enum BindAction { SpamLeft, SpamRight, HoldLeft, HoldRight, SpamKey, HoldKey, PlayMacro }

    class KeyBind
    {
        public bool On = true;
        public Keys Trigger = Keys.R;
        public BindMode Mode = BindMode.Held;
        public BindAction Action = BindAction.SpamLeft;
        public Keys Key = Keys.E;       // for SpamKey / HoldKey
        public double EveryMs = 50;     // for spamming
        public bool Block = true;       // the trigger key doesn't reach the game
        public bool OnlyInGame;         // only while the target app is in front
        public string Macro = "";       // for PlayMacro: a macro in the suite

        public static readonly string[] ModeNames = { "While held", "Toggle: press to start, again to stop", "Once per press" };
        public static readonly string[] ActionNames = { "Spam left click", "Spam right click", "Hold left click", "Hold right click", "Spam a key", "Hold a key", "Play a macro from my suite" };

        public KeyBind Copy() { return (KeyBind)MemberwiseClone(); }

        public bool IsSpam { get { return Action == BindAction.SpamLeft || Action == BindAction.SpamRight || Action == BindAction.SpamKey; } }
        public bool UsesKey { get { return Action == BindAction.SpamKey || Action == BindAction.HoldKey; } }

        string Target
        {
            get
            {
                switch (Action)
                {
                    case BindAction.SpamLeft: case BindAction.HoldLeft: return "left click";
                    case BindAction.SpamRight: case BindAction.HoldRight: return "right click";
                    case BindAction.PlayMacro: return "\"" + Macro + "\"";
                    default: return Ui.KeyName(Key);
                }
            }
        }

        // "R: spam left click while held"
        public string Title
        {
            get
            {
                string what = Action == BindAction.PlayMacro ? "play " + Target
                    : (IsSpam ? "spam " : Mode == BindMode.Once ? "tap " : "hold ") + Target;
                string when = Action == BindAction.PlayMacro ? "" : Mode == BindMode.Held ? " while held" : Mode == BindMode.Toggle ? " (toggle)" : "";
                return (On ? "" : "(off) ") + Ui.KeyName(Trigger) + ":  " + what + when;
            }
        }

        // A sentence for the editor: what pressing the key does.
        public string Describe()
        {
            string key = Ui.KeyName(Trigger);
            if (Action == BindAction.PlayMacro)
                return Macro == "" ? "Pick a macro from your suite." : "Pressing " + key + " starts \"" + Macro + "\"; pressing it again stops it.";
            string rate = IsSpam ? " " + (EveryMs > 0 ? (1000 / EveryMs).ToString(EveryMs >= 100 ? "0.#" : "0", CultureInfo.InvariantCulture) : "1000+") + " times a second" : "";
            string act = IsSpam ? "spams " + Target + rate : Mode == BindMode.Once ? "taps " + Target : "holds " + Target + " down";
            switch (Mode)
            {
                case BindMode.Held: return "Holding " + key + " " + act + (IsSpam ? "" : " until you let go") + ".";
                case BindMode.Toggle: return "Pressing " + key + " " + (IsSpam ? "starts: it " + act : act) + ". Press it again to stop.";
                default: return "Each press of " + key + " " + (IsSpam ? "clicks once" : act) + ".";
            }
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public string Save()
        {
            return string.Join(",", new[] { On.ToString(), ((int)Trigger).ToString(Inv), ((int)Mode).ToString(Inv), ((int)Action).ToString(Inv),
                ((int)Key).ToString(Inv), EveryMs.ToString("R", Inv), Block.ToString(), OnlyInGame.ToString(), Uri.EscapeDataString(Macro ?? "") });
        }

        public static KeyBind Parse(string s)
        {
            try
            {
                var f = s.Split(',');
                if (f.Length < 9) return null;
                return new KeyBind
                {
                    On = bool.Parse(f[0]), Trigger = (Keys)int.Parse(f[1], Inv), Mode = (BindMode)int.Parse(f[2], Inv), Action = (BindAction)int.Parse(f[3], Inv),
                    Key = (Keys)int.Parse(f[4], Inv), EveryMs = Math.Max(0, double.Parse(f[5], Inv)), Block = bool.Parse(f[6]), OnlyInGame = bool.Parse(f[7]),
                    Macro = Uri.UnescapeDataString(f[8])
                };
            }
            catch (FormatException) { return null; }
            catch (OverflowException) { return null; }
        }

        public static string SaveAll(List<KeyBind> binds)
        {
            var parts = new List<string>();
            foreach (var b in binds) parts.Add(b.Save());
            return string.Join("|", parts.ToArray());
        }

        public static List<KeyBind> ParseAll(string s)
        {
            var list = new List<KeyBind>();
            if (string.IsNullOrEmpty(s)) return list;
            foreach (var part in s.Split('|'))
            {
                var b = Parse(part);
                if (b != null) list.Add(b);
            }
            return list;
        }
    }

    // Runs the binds. OnKey is called by the keyboard hook (its own thread) for every real key event.
    class BindEngine
    {
        public volatile KeyBind[] Binds = new KeyBind[0];
        public volatile bool Enabled = true;
        public IntPtr OwnWindow;              // binds don't fire while Embris itself is in front
        public Func<bool> GameInFront;        // for "only while the game is in front"
        public event Action<string> PlayMacro; // raised on the hook thread with the macro's name

        readonly Dictionary<KeyBind, Action> stops = new Dictionary<KeyBind, Action>(); // running actions
        readonly HashSet<int> keysDown = new HashSet<int>();
        readonly HashSet<int> swallowed = new HashSet<int>(); // presses kept from the game, so their releases are too

        // Returns true when the key should be kept from the game.
        public bool OnKey(int key, bool down)
        {
            var binds = Binds;
            bool any = false, block = false;
            foreach (var b in binds) if (b.On && (int)b.Trigger == key) { any = true; break; }
            if (!any) return false;

            if (!down)
            {
                keysDown.Remove(key);
                // Letting go always ends a held action, even if the game is no longer in front.
                foreach (var b in binds)
                    if ((int)b.Trigger == key && b.Mode == BindMode.Held && b.Action != BindAction.PlayMacro) Stop(b);
                // The release is kept from the game only if the press was (or the key would stick).
                return swallowed.Remove(key);
            }
            bool repeat = !keysDown.Add(key);

            foreach (var b in binds)
            {
                if (!b.On || (int)b.Trigger != key) continue;
                if (!Enabled || Native.GetForegroundWindow() == OwnWindow) continue;
                if (b.OnlyInGame && GameInFront != null && !GameInFront()) continue;
                block |= b.Block;
                if (repeat) continue; // Windows repeats a held key: only the first press counts
                if (b.Action == BindAction.PlayMacro) { if (PlayMacro != null && b.Macro != "") PlayMacro(b.Macro); continue; }
                switch (b.Mode)
                {
                    case BindMode.Held: Start(b); break;
                    case BindMode.Toggle: if (stopsContains(b)) Stop(b); else Start(b); break;
                    default: Once(b); break;
                }
            }
            if (block) swallowed.Add(key);
            return block;
        }

        bool stopsContains(KeyBind b) { lock (stops) return stops.ContainsKey(b); }

        public bool IsRunning(KeyBind b) { return stopsContains(b); }

        void Start(KeyBind b)
        {
            lock (stops) if (stops.ContainsKey(b)) return;
            Action stop;
            switch (b.Action)
            {
                case BindAction.HoldLeft: Input.Button(0, false); stop = () => Input.Button(0, true); break;
                case BindAction.HoldRight: Input.Button(1, false); stop = () => Input.Button(1, true); break;
                case BindAction.HoldKey: { var k = b.Key; Input.Key(k, false); stop = () => Input.Key(k, true); break; }
                default:
                    {
                        var run = new Spammer(b);
                        stop = run.Stop;
                        break;
                    }
            }
            lock (stops) stops[b] = stop;
        }

        void Stop(KeyBind b)
        {
            Action stop;
            lock (stops)
            {
                if (!stops.TryGetValue(b, out stop)) return;
                stops.Remove(b);
            }
            stop();
        }

        static void Once(KeyBind b)
        {
            switch (b.Action)
            {
                case BindAction.SpamLeft: case BindAction.HoldLeft: Input.Button(0, false); Input.Button(0, true); break;
                case BindAction.SpamRight: case BindAction.HoldRight: Input.Button(1, false); Input.Button(1, true); break;
                default: Input.Key(b.Key, false); Input.Key(b.Key, true); break;
            }
        }

        // Ends every running action (binds turned off, edited, or Embris closing).
        public void StopAll()
        {
            List<Action> all;
            lock (stops)
            {
                all = new List<Action>(stops.Values);
                stops.Clear();
            }
            foreach (var s in all) s();
        }

        // Clicks or taps a key at a steady rate until stopped.
        sealed class Spammer
        {
            [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
            [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
            volatile bool running = true;

            public Spammer(KeyBind b)
            {
                var action = b.Action;
                var key = b.Key;
                double every = Math.Max(1, b.EveryMs);
                new Thread(() => Run(action, key, every)) { IsBackground = true, Name = "Embris key bind" }.Start();
            }

            public void Stop() { running = false; }

            void Run(BindAction action, Keys key, double every)
            {
                timeBeginPeriod(1);
                var clock = Stopwatch.StartNew();
                double due = 0;
                try
                {
                    while (running)
                    {
                        switch (action)
                        {
                            case BindAction.SpamLeft: Input.Button(0, false); Input.Button(0, true); break;
                            case BindAction.SpamRight: Input.Button(1, false); Input.Button(1, true); break;
                            default:
                                // Games read keys once per frame: hold it a moment so every tap registers.
                                Input.Key(key, false);
                                Thread.Sleep((int)Math.Min(15, every / 2));
                                Input.Key(key, true);
                                break;
                        }
                        due += every;
                        if (clock.Elapsed.TotalMilliseconds - due > 250) due = clock.Elapsed.TotalMilliseconds; // fell behind: no burst
                        while (running)
                        {
                            double left = due - clock.Elapsed.TotalMilliseconds;
                            if (left <= 0) break;
                            if (left > 20) Thread.Sleep(Math.Min(50, (int)(left - 15)));
                            else if (left > 2.5) Thread.Sleep(0);
                            else Thread.SpinWait(200);
                        }
                    }
                }
                finally { timeEndPeriod(1); }
            }
        }
    }
}
