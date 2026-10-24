"""Embris scripts (.emb): a plain-text form of a macro, easy to read, edit and send to friends.

The same format as the Windows app, so scripts move between Windows, Mac and Linux. Also reads
AutoHotkey hotstring lines (":*:d1::desert 1"), so .ahk hotstring files import as they are.
"""
import re

from pynput import keyboard

import embris_core as core

EXTENSION = ".emb"

TEMPLATE = """# Embris macro script (.emb). Lines starting with # are notes.
name: My macro
hotkey: F9          # optional: press it anywhere to run this macro
every: 60           # seconds between runs
target: Roblox
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
"""

AHK_HOTSTRING = re.compile(r"^:([^:]*):(.+?)::(.*)$")
AHK_TOGGLE = re.compile(r"^(\w+)::\s*\w+\s*:=\s*!\s*\w+", re.I)
AHK_WINDOW = re.compile(r'WinActive\(\s*"([^"]+)"', re.I)

# Other spellings (including the Windows app's names) for the same keys.
ALIASES = {
    "escape": "esc", "return": "enter", "control": "ctrl", "controlkey": "ctrl", "lcontrolkey": "ctrl",
    "rcontrolkey": "ctrl_r", "shiftkey": "shift", "lshiftkey": "shift", "rshiftkey": "shift_r",
    "menu": "alt", "lmenu": "alt", "rmenu": "alt_r", "win": "cmd", "lwin": "cmd", "rwin": "cmd_r",
    "del": "delete", "pgup": "page_up", "pgdn": "page_down", "next": "page_down", "prior": "page_up",
    "back": "backspace", "capital": "caps_lock", "capslock": "caps_lock", "oemcomma": ",",
    "oemperiod": ".", "oemminus": "-", "oemplus": "=", "oemquestion": "/", "oemsemicolon": ";",
    "oemquotes": "'", "oemopenbrackets": "[", "oemclosebrackets": "]", "oempipe": "\\", "oemtilde": "`",
    "rightctrl": "ctrl_r", "rightalt": "alt_r", "rightshift": "shift_r", "rightwin": "cmd_r", "apps": "menu",
}


def parse_key(name):
    """'Space', 'F6', 'Page Up', 'E', 'Num 7', '{Space}' -> internal key name, or ValueError."""
    raw = name.strip().strip("{}")
    if len(raw) == 1:
        return raw.lower()
    n = raw.lower().replace(" ", "_")
    if n.startswith("num_") and len(n) == 5:
        return n[4]  # numpad digits play back as the digit
    if len(n) == 2 and n[0] == "d" and n[1].isdigit():
        return n[1]  # Windows "D1"
    n = ALIASES.get(n.replace("_", ""), ALIASES.get(n, n))
    if len(n) == 1 or getattr(keyboard.Key, n, None) is not None:
        return n
    raise ValueError('unknown key "%s"' % name.strip())


class ScriptResult:
    def __init__(self, name):
        self.name = name
        self.hotkey = None
        self.settings = dict(core.DEFAULTS, use_sequence=True, focus_app=True, wait_idle=False)
        self.steps = []
        self.hotstring_window = ""
        self.hotstring_toggle = None
        self.hotstrings = []
        self.errors = []

    def to_preset(self):
        s = dict(self.settings, sequence=self.steps, use_sequence=True)
        preset = {"name": self.name, "settings": core.macro_part(s)}
        if self.hotkey:
            preset["hotkey"] = self.hotkey
        if self.hotstrings:
            preset["hotstrings"] = {"window": self.hotstring_window, "toggle": self.hotstring_toggle,
                                    "items": [list(h) for h in self.hotstrings]}
        return preset


# ---------------------------------------------------------------- writing

def from_preset(p):
    s = dict(core.DEFAULTS)
    s.update(p.get("settings", {}))
    steps = list(s["sequence"]) if s["use_sequence"] else core.single_action_steps(s)
    out = ["# Embris macro script (.emb) - open it in Embris, or double-click it once Embris is installed.",
           "name: " + p["name"]]
    if p.get("hotkey"):
        out.append("hotkey: " + core.key_label(p["hotkey"]))
    out.append("every: %g" % float(s["interval"]))
    out.append("target: " + s["app_name"])
    out.append("mode: " + ("switch" if s["focus_app"] else "background"))
    if s["focus_app"]:
        out.append("wait idle: " + ("%g" % float(s["idle_sec"]) if s["wait_idle"] else "off"))
    out.append("speed: %g" % float(s["speed"]))
    if int(s.get("repeat", 1)) != 1:
        out.append("repeat: %d" % int(s["repeat"]))
    out.append("")
    out.append("steps:")
    i = 0
    while i < len(steps):
        st = steps[i]
        nxt = steps[i + 1] if i + 1 < len(steps) else None
        t = st["t"]
        if t == "w":
            out.append("  wait %d" % st["d"])
            i += 1
            continue
        if st["d"] > 0:
            out.append("  wait %d" % st["d"])
        if t == "kd" and nxt and nxt["t"] == "ku" and nxt["k"] == st["k"]:
            name = core.key_label(st["k"])
            out.append("  tap " + name if nxt["d"] == 30 else "  hold %s %d" % (name, nxt["d"]))
            i += 2
            continue
        if (t == "md" and nxt and nxt["t"] == "mu" and nxt.get("b") == st.get("b")
                and nxt.get("x") == st.get("x") and nxt.get("y") == st.get("y")):
            out.append("  click " + _button(st) + _pos(st) + ("" if nxt["d"] == 40 else " %d" % nxt["d"]))
            i += 2
            continue
        if t == "kd":
            out.append("  down " + core.key_label(st["k"]))
        elif t == "ku":
            out.append("  up " + core.key_label(st["k"]))
        elif t == "md":
            out.append("  mousedown " + _button(st) + _pos(st))
        elif t == "mu":
            out.append("  mouseup " + _button(st) + _pos(st))
        elif t == "mv":
            out.append("  move %d %d" % (st["x"], st["y"]))
        elif t == "tx":
            out.append("  type " + st["text"])
        i += 1
    hs = p.get("hotstrings")
    if hs and hs.get("items"):
        out += ["", "hotstrings:"]
        if hs.get("window"):
            out.append("  window: " + hs["window"])
        if hs.get("toggle"):
            out.append("  toggle: " + core.key_label(hs["toggle"]))
        out += ["  %s = %s" % (a, b) for a, b in hs["items"]]
    return "\n".join(out) + "\n"


def _button(st):
    return "right" if st.get("b") == "right" else "left"


def _pos(st):
    return "" if st.get("x") is None else " %d %d" % (st["x"], st["y"])


# ---------------------------------------------------------------- reading

def parse(text, fallback_name):
    r = ScriptResult(fallback_name)
    section = ""
    for n, raw in enumerate(text.lstrip("\ufeff").replace("\r\n", "\n").split("\n"), 1):
        win = AHK_WINDOW.search(raw)  # AutoHotkey "#If WinActive(...)" (a comment line otherwise)
        if win:
            r.hotstring_window = win.group(1)
            continue
        line = _strip_comment(raw).strip()
        if not line:
            continue
        try:
            m = AHK_HOTSTRING.match(line)
            if m:
                r.hotstrings.append((m.group(2), m.group(3)))
                continue
            m = AHK_TOGGLE.match(line)
            if m:
                r.hotstring_toggle = parse_key(m.group(1))
                continue
            if line.startswith("#") or line in ("{", "}") or line.startswith(("SendMode", "SetWorkingDir")):
                continue
            low = line.lower()
            if low in ("steps:", "hotstrings:"):
                section = low
                continue
            if section == "steps:":
                _parse_step(line, r.steps)
                continue
            if section == "hotstrings:":
                key, _, value = line.partition(":")
                if key.strip().lower() in ("window", "toggle") and _:
                    if key.strip().lower() == "window":
                        r.hotstring_window = value.strip()
                    else:
                        r.hotstring_toggle = parse_key(value)
                    continue
                if "=" not in line:
                    raise ValueError("expected  short = long text")
                a, _, b = line.partition("=")
                r.hotstrings.append((a.strip(), b.strip()))
                continue
            key, colon, value = line.partition(":")
            if not colon:
                raise ValueError("unknown line")
            _apply_setting(r, key.strip().lower(), value.strip())
        except ValueError as e:
            r.errors.append("Line %d: %s  (%s)" % (n, e, raw.strip()))
    return r


def _strip_comment(line):
    t = line.lstrip()
    if t.startswith(("#", ";")):
        return ""
    i = line.find(" #")
    return line[:i] if i >= 0 else line


def _number(s):
    try:
        return float(s.strip())
    except ValueError:
        raise ValueError('"%s" isn\'t a number' % s.strip())


def _apply_setting(r, key, value):
    s = r.settings
    if key == "name":
        r.name = value
    elif key == "hotkey":
        r.hotkey = None if value.lower() in ("", "none") else parse_key(value)
    elif key == "every":
        s["interval"] = max(0.001, _number(value.rstrip("sS")))
    elif key == "target":
        # Windows scripts name the Roblox process; here it's the app (Mac) or window title (Linux).
        s["app_name"] = core.DEFAULTS["app_name"] if value.lower().startswith("robloxplayer") else value
    elif key == "mode":
        s["focus_app"] = not value.lower().startswith("b")
    elif key == "wait idle":
        s["wait_idle"] = value.lower() != "off"
        if s["wait_idle"]:
            s["idle_sec"] = _number(value)
    elif key == "speed":
        sp = _number(value.rstrip("xX"))
        if sp <= 0:
            raise ValueError("speed must be more than 0")
        s["speed"] = min(sp, 1e6)
    elif key == "repeat":
        s["repeat"] = max(1, int(_number(value)))
    else:
        raise ValueError('unknown setting "%s"' % key)


def _parse_step(line, steps):
    cmd, _, rest = line.partition(" ")
    cmd, rest = cmd.lower(), rest.strip()
    a = rest.split()
    if cmd in ("tap", "press"):
        k = parse_key(rest)
        steps += [{"t": "kd", "k": k, "d": 0}, {"t": "ku", "k": k, "d": 30}]
    elif cmd == "hold":
        if len(a) < 2:
            raise ValueError("use: hold KEY milliseconds")
        k = parse_key(" ".join(a[:-1]))
        steps += [{"t": "kd", "k": k, "d": 0}, {"t": "ku", "k": k, "d": int(_number(a[-1]))}]
    elif cmd == "down":
        steps.append({"t": "kd", "k": parse_key(rest), "d": 0})
    elif cmd == "up":
        steps.append({"t": "ku", "k": parse_key(rest), "d": 0})
    elif cmd in ("wait", "sleep"):
        steps.append({"t": "w", "d": int(_number(rest.rstrip("ms")))})
    elif cmd in ("type", "send"):
        steps.append({"t": "tx", "text": rest, "d": 0})
    elif cmd == "move":
        if len(a) != 2:
            raise ValueError("use: move dx dy")
        steps.append({"t": "mv", "x": int(_number(a[0])), "y": int(_number(a[1])), "d": 0})
    elif cmd in ("click", "mousedown", "mouseup"):
        b = "right" if a and a[0].lower() == "right" else "left"
        first = 1 if a and a[0].lower() in ("left", "right") else 0
        x = y = None
        hold = 40
        if len(a) >= first + 2:
            x, y = int(_number(a[first])), int(_number(a[first + 1]))
        if len(a) >= first + 3:
            hold = int(_number(a[first + 2]))
        if cmd != "mouseup":
            steps.append({"t": "md", "b": b, "x": x, "y": y, "d": 0})
        if cmd != "mousedown":
            steps.append({"t": "mu", "b": b, "x": x, "y": y, "d": hold if cmd == "click" else 0})
    else:
        raise ValueError('unknown step "%s"' % cmd)
