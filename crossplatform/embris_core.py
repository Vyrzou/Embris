"""Embris for macOS / Linux - everything that isn't UI.

Settings and presets, key names, the global input watcher (hotkey, idle detection,
recording) and sequence playback. Uses pynput for keyboard and mouse.
"""
import itertools
import json
import os
import subprocess
import sys
import threading
import time

from pynput import keyboard, mouse

VERSION = "1.6.2"
IS_MAC = sys.platform == "darwin"

# New versions are published as GitHub Releases.
REPO = "Vyrzou/Embris"
RELEASES_PAGE = "https://github.com/%s/releases" % REPO
LATEST_RELEASE_API = "https://api.github.com/repos/%s/releases/latest" % REPO


def parse_version(tag):
    """'v1.6.0' -> (1, 6, 0, 0); None when there's no number in it. Missing parts count as 0."""
    import re
    m = re.search(r"\d+(\.\d+){0,3}", tag or "")
    if not m:
        return None
    parts = [int(x) for x in m.group(0).split(".")]
    return tuple(parts + [0] * (4 - len(parts)))


def is_newer(tag, current=VERSION):
    v = parse_version(tag)
    return v is not None and v > parse_version(current)


# ---------------------------------------------------------------- settings & presets

def config_dir():
    if IS_MAC:
        path = os.path.expanduser("~/Library/Application Support/Embris")
    else:
        path = os.path.join(os.environ.get("XDG_CONFIG_HOME", os.path.expanduser("~/.config")), "embris")
    os.makedirs(path, exist_ok=True)
    return path


SETTINGS_FILE = os.path.join(config_dir(), "settings.json")
PRESETS_FILE = os.path.join(config_dir(), "presets.json")

DEFAULTS = {
    # macro (stored in presets)
    "key": "space", "press_key": True, "hold_ms": 50,
    "click": False, "right_click": False, "x": 0, "y": 0,
    "interval": 60.0,
    "focus_app": True, "app_name": "Roblox" if IS_MAC else "Sober",
    "wait_idle": True, "idle_sec": 1.5,
    "use_sequence": False, "speed": 1.0, "repeat": 1, "sequence": [],
    # app
    "hotkey": "f6", "record_hotkey": "f8", "pinned": False, "close_to_tray": True, "sidebar_collapsed": False,
    "bg_file": "", "bg_source": "", "bg_dim": 55,
    "theme_name": "Ember", "theme_colors": None,
    # auto clicker
    "click_hotkey": "f7", "click_interval_ms": 100.0, "click_button": 0, "click_double": False,
    "click_times": 0, "click_fixed": False, "click_x": 0, "click_y": 0,
    "snap_to_keys": True, "overlay_hotkey": "f10", "auto_save_recordings": False, "ghost_opacity": 25,
    "frame_sync": 0,  # playback lines inputs up with the game's frames: 0 off, -1 the display's refresh rate, else that frame rate
}
APP_ONLY = ("hotkey", "record_hotkey", "sidebar_collapsed", "bg_file", "bg_source", "bg_dim", "pinned", "close_to_tray", "theme_name", "theme_colors",
            "click_hotkey", "click_interval_ms", "click_button", "click_double", "click_times", "click_fixed", "click_x", "click_y",
            "snap_to_keys", "overlay_hotkey", "auto_save_recordings", "ghost_opacity", "frame_sync")


def load_json(path, fallback):
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return fallback


def save_json(path, data):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=1)
    os.replace(tmp, path)


def load_settings():
    s = dict(DEFAULTS)
    saved = load_json(SETTINGS_FILE, {})
    if isinstance(saved, dict):
        s.update(saved)
    return s


def macro_part(s):
    return {k: v for k, v in s.items() if k not in APP_ONLY}


def load_presets():
    p = load_json(PRESETS_FILE, None)
    if isinstance(p, list):
        return p
    afk = dict(DEFAULTS, key="space", interval=900.0, focus_app=True, wait_idle=True, idle_sec=3.0)
    jump = dict(DEFAULTS, key="space", interval=1.0, focus_app=True, wait_idle=False)
    return [{"name": "Anti-AFK (Space every 15 min)", "settings": macro_part(afk)},
            {"name": "Jump every second", "settings": macro_part(jump)}]


# ---------------------------------------------------------------- key names
# Keys are stored as short names: "space", "f6", "page_up", or a single character like "e".

def key_from_pynput(k):
    if isinstance(k, keyboard.Key):
        return k.name
    ch = getattr(k, "char", None)
    if ch and ch.isprintable():
        return ch.lower()
    vk = getattr(k, "vk", None)
    return "vk%d" % vk if vk is not None else None


def to_pynput(name):
    if name.startswith("vk") and name[2:].isdigit():
        return keyboard.KeyCode.from_vk(int(name[2:]))
    if len(name) == 1:
        return keyboard.KeyCode.from_char(name)
    k = getattr(keyboard.Key, name, None)
    return k if k is not None else keyboard.KeyCode.from_char(name[0])


def key_label(name):
    if not name:
        return "None"
    if len(name) == 1:
        return name.upper()
    if name.startswith("vk"):
        return "Key " + name[2:]
    return name.replace("_", " ").title()


# ---------------------------------------------------------------- sequences
# A step is a dict: t = "kd"/"ku" (key down/up), "md"/"mu" (mouse down/up), "w" (wait) or
# "mv" (relative mouse move, x/y = distance; this is how camera turning is recorded),
# d = ms to wait before it, plus k (key) or b ("left"/"right"), x, y.

def describe_step(st):
    t = st["t"]
    if t == "w":
        return "Wait %d ms" % st["d"]
    if t == "mv":
        return "Move mouse %+d, %+d" % (st["x"], st["y"])
    if t == "tx":
        return 'Type "%s"' % st["text"]
    if t in ("kd", "ku"):
        return "Key %s %s" % (key_label(st["k"]), "down" if t == "kd" else "up")
    where = "" if st.get("x") is None else " at %d, %d" % (st["x"], st["y"])
    return "%s click %s%s" % ("Right" if st.get("b") == "right" else "Left", "down" if t == "md" else "up", where)


def format_steps(seq, max_lines=None, newest_last=False):
    lines, t = [], 0.0
    for st in seq:
        t += st["d"] / 1000.0
        lines.append(("+%.2fs" % t).ljust(10) + describe_step(st))
    if max_lines and len(lines) > max_lines:
        shown = max_lines - 1
        hidden = len(lines) - shown
        if newest_last:
            return "\n".join(["... %d earlier steps" % hidden] + lines[-shown:])
        return "\n".join(lines[:shown] + ["... and %d more steps" % hidden])
    return "\n".join(lines)


def summarize(seq):
    if not seq:
        return "Nothing recorded yet"
    actions = sum(1 for st in seq if st["t"] in ("kd", "md", "w", "tx"))
    total = sum(st["d"] for st in seq) / 1000.0
    return "%d action%s, %.1f seconds long" % (actions, "" if actions == 1 else "s", total)


def single_action_steps(s):
    """The plain key press / click as a tiny sequence, so it plays like a recording."""
    steps = []
    if s["click"]:
        b = "right" if s["right_click"] else "left"
        steps += [{"t": "md", "b": b, "x": s["x"], "y": s["y"], "d": 30},
                  {"t": "mu", "b": b, "x": s["x"], "y": s["y"], "d": 40}]
    if s["press_key"]:
        steps += [{"t": "kd", "k": s["key"], "d": 0}, {"t": "ku", "k": s["key"], "d": int(s["hold_ms"])}]
    return steps


def remove_last_step(seq):
    """Drops the last step, taking a press's matching release with it."""
    if not seq:
        return
    last = seq[-1]
    if len(seq) >= 2 and last["t"] in ("ku", "mu"):
        prev = seq[-2]
        same = prev.get("k") == last.get("k") and prev.get("b") == last.get("b")
        if prev["t"] == {"ku": "kd", "mu": "md"}[last["t"]] and same:
            del seq[-2:]
            return
    del seq[-1]


# ---------------------------------------------------------------- uninstalling

def install_paths():
    """Everything the installer created, plus settings and presets."""
    if IS_MAC:
        return [os.path.expanduser("~/Applications/Embris.app"), config_dir()]
    data = os.environ.get("XDG_DATA_HOME", os.path.expanduser("~/.local/share"))
    return [os.path.join(data, "embris"), config_dir(), os.path.expanduser("~/.local/bin/embris"),
            os.path.join(data, "applications", "embris.desktop"), os.path.join(data, "mime", "packages", "embris.xml")]


def register_file_type():
    """Linux: makes .emb files open with Embris (the macOS app declares it in its Info.plist)."""
    data = os.environ.get("XDG_DATA_HOME", os.path.expanduser("~/.local/share"))
    mime_dir = os.path.join(data, "mime", "packages")
    os.makedirs(mime_dir, exist_ok=True)
    with open(os.path.join(mime_dir, "embris.xml"), "w", encoding="utf-8") as f:
        f.write('<?xml version="1.0" encoding="UTF-8"?>\n'
                '<mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">\n'
                '  <mime-type type="application/x-embris">\n    <comment>Embris macro</comment>\n'
                '    <glob pattern="*.emb"/>\n  </mime-type>\n</mime-info>\n')
    run_cmd(["update-mime-database", os.path.join(data, "mime")])
    r = run_cmd(["xdg-mime", "default", "embris.desktop", "application/x-embris"])
    return r is not None and r.returncode == 0


def uninstall():
    import shutil
    for path in install_paths():
        if os.path.isdir(path) and not os.path.islink(path):
            shutil.rmtree(path, ignore_errors=True)
        elif os.path.lexists(path):
            try:
                os.remove(path)
            except OSError:
                pass


# ---------------------------------------------------------------- bringing the game to the front

def run_cmd(args):
    try:
        return subprocess.run(args, capture_output=True, text=True, timeout=4)
    except (OSError, subprocess.SubprocessError):
        return None


def osascript(*lines):
    args = ["osascript"]
    for line in lines:
        args += ["-e", line]
    return run_cmd(args)


def front_window():
    if IS_MAC:
        r = osascript('tell application "System Events" to get name of first application process whose frontmost is true')
    else:
        r = run_cmd(["xdotool", "getactivewindow"])
    return r.stdout.strip() if r is not None and r.returncode == 0 else None


def bring_to_front(name):
    name = name.replace('"', "")
    if IS_MAC:
        # Only activate a running app: "activate" on its own would launch it.
        r = osascript('if application "%s" is running then' % name,
                      'tell application "%s" to activate' % name,
                      'return "ok"', "end if", 'return "missing"')
        return r is not None and r.stdout.strip() == "ok"
    r = run_cmd(["xdotool", "search", "--onlyvisible", "--name", name])
    if r is None or r.returncode != 0 or not r.stdout.split():
        return False
    r = run_cmd(["xdotool", "windowactivate", "--sync", r.stdout.split()[0]])
    return r is not None and r.returncode == 0


def front_window_title():
    """Name of the app (Mac) or window title (Linux) in front, for hotstring window rules."""
    if IS_MAC:
        return front_window() or ""
    r = run_cmd(["xdotool", "getactivewindow", "getwindowname"])
    return r.stdout.strip() if r is not None and r.returncode == 0 else ""


def restore_front(prev):
    if not prev:
        return
    if IS_MAC:
        osascript('tell application "System Events" to set frontmost of process "%s" to true' % prev.replace('"', ""))
    else:
        run_cmd(["xdotool", "windowactivate", prev])


# ---------------------------------------------------------------- global input watcher

class InputWatcher:
    """Global keyboard/mouse listeners for the hotkey, idle detection and recording.

    Callbacks run on pynput's threads; on_hotkey / on_record_hotkey are called there too,
    so the UI must hand them over to its own thread.
    """

    def __init__(self, get_hotkey, on_hotkey, get_record_hotkey=lambda: None, on_record_hotkey=lambda: None,
                 get_suite_keys=lambda: (), on_suite_key=lambda key: None, on_hotstrings_toggled=lambda hs: None):
        self.get_hotkey, self.on_hotkey = get_hotkey, on_hotkey
        self.get_record_hotkey, self.on_record_hotkey = get_record_hotkey, on_record_hotkey
        self.get_suite_keys, self.on_suite_key = get_suite_keys, on_suite_key
        self.on_hotstrings_toggled = on_hotstrings_toggled
        self.suite_down = set()
        self.hotstring_sets = []  # dicts: name, window, toggle, on, items (longest first)
        self.typed = ""
        self.typer = keyboard.Controller()
        self.record_down = False
        self.last_pos = None
        self.last_input = time.time()
        self.held = set()
        self.sending = False      # True while Embris itself is pressing keys
        self.paused = False       # True while a key picker is waiting for a key
        self.own_rect = (0, 0, 0, 0)
        self.own_focused = False
        self.recording = False
        self.steps, self.rec_held, self.last_t = [], set(), 0.0
        self.hotkey_down = False
        self.get_click_hotkey, self.on_click_hotkey, self.click_down = (lambda: None), (lambda: None), False  # auto clicker
        self.get_overlay_hotkey, self.on_overlay_hotkey, self.overlay_down = (lambda: None), (lambda: None), False  # calibration overlay
        self.lock = threading.Lock()
        self.kb = keyboard.Listener(on_press=self.on_press, on_release=self.on_release)
        # On macOS the intercept sees each event's raw movement, which still arrives when a game
        # locks the cursor to turn the camera. Other platforms ignore darwin_* options.
        self.ms = mouse.Listener(on_click=self.on_click, on_move=self.on_move, on_scroll=self.on_scroll,
                                 darwin_intercept=self._darwin_intercept)

    def start(self):
        self.kb.start()
        self.ms.start()

    def stop(self):
        self.kb.stop()
        self.ms.stop()

    def trusted(self):
        return getattr(self.kb, "IS_TRUSTED", True) is not False

    def user_busy(self, idle_sec):
        return time.time() - self.last_input < idle_sec or bool(self.held)

    def start_recording(self):
        with self.lock:
            self.steps, self.rec_held, self.recording = [], set(), True

    def stop_recording(self):
        with self.lock:
            self.recording = False
            return list(self.steps)

    def _add(self, step):
        with self.lock:
            if not self.recording:
                return
            if step["t"] == "kd":
                if step["k"] in self.rec_held:
                    return  # auto-repeat
                self.rec_held.add(step["k"])
            elif step["t"] == "ku":
                if step["k"] not in self.rec_held:
                    return  # pressed before recording started
                self.rec_held.discard(step["k"])
            now = time.perf_counter()
            step["d"] = 0 if not self.steps else int(round((now - self.last_t) * 1000))
            self.last_t = now
            self.steps.append(step)

    def add_move(self, dx, dy):
        """Relative mouse movement, one step per mouse report. Not merging them matters: with mouse
        acceleration on, a big report turns more than the same distance in several small ones,
        so merged moves would replay as different camera turns."""
        with self.lock:
            if not self.recording or (dx == 0 and dy == 0):
                return
            now = time.perf_counter()
            self.steps.append({"t": "mv", "x": int(dx), "y": int(dy),
                               "d": 0 if not self.steps else int(round((now - self.last_t) * 1000))})
            self.last_t = now
    def _darwin_intercept(self, event_type, event):
        if self.recording and not self.sending and not self.own_focused and event_type in (5, 6, 7, 27):
            import Quartz  # comes with pynput on macOS
            dx = Quartz.CGEventGetIntegerValueField(event, Quartz.kCGMouseEventDeltaX)
            dy = Quartz.CGEventGetIntegerValueField(event, Quartz.kCGMouseEventDeltaY)
            self.add_move(int(dx), int(dy))
        return event

    def on_press(self, k):
        if self.sending:
            return
        name = key_from_pynput(k)
        self.last_input = time.time()
        self.held.add(name)
        if self.paused:
            return
        if name == self.get_hotkey():
            if not self.hotkey_down:
                self.hotkey_down = True
                self.on_hotkey()
            return
        if name == self.get_record_hotkey():
            if not self.record_down:
                self.record_down = True
                self.on_record_hotkey()
            return  # never part of the recording
        if name and name == self.get_click_hotkey():
            if not self.click_down:
                self.click_down = True
                self.on_click_hotkey()
            return
        if name and name == self.get_overlay_hotkey():
            if not self.overlay_down:
                self.overlay_down = True
                self.on_overlay_hotkey()
            return
        if name in self.get_suite_keys():
            if name not in self.suite_down:
                self.suite_down.add(name)
                self.on_suite_key(name)
            return
        for hs in self.hotstring_sets:
            if hs["toggle"] and name == hs["toggle"]:
                if name not in self.suite_down:
                    self.suite_down.add(name)
                    hs["on"] = not hs["on"]
                    self.typed = ""
                    self.on_hotstrings_toggled(hs)
                return
        if self.hotstring_sets and name:
            self._track_typing(name)
        if self.recording and name and not self.own_focused:
            self._add({"t": "kd", "k": name})

    def on_release(self, k):
        if self.sending:
            return
        name = key_from_pynput(k)
        self.last_input = time.time()
        self.held.discard(name)
        if name == self.get_hotkey():
            self.hotkey_down = False
            return
        if name == self.get_record_hotkey():
            self.record_down = False
            return
        if name and name == self.get_click_hotkey():
            self.click_down = False
            return
        if name and name == self.get_overlay_hotkey():
            self.overlay_down = False
            return
        if name in self.suite_down:
            self.suite_down.discard(name)
            return
        if self.recording and name and not self.own_focused and not self.paused:
            self._add({"t": "ku", "k": name})

    # ----- hotstrings: type a short text and it turns into the long one

    MODIFIERS = {"shift", "shift_r", "ctrl", "ctrl_r", "alt", "alt_r", "alt_gr", "cmd", "cmd_r", "caps_lock"}

    def _track_typing(self, name):
        if name == "backspace":
            self.typed = self.typed[:-1]
            return
        ch = name if len(name) == 1 else " " if name == "space" else None
        if ch is None:
            if name not in self.MODIFIERS:
                self.typed = ""
            return
        self.typed = (self.typed + ch)[-64:]
        for hs in self.hotstring_sets:
            if hs["toggle"] and not hs["on"]:
                continue
            for abbr, text in hs["items"]:
                if self.typed.lower().endswith(abbr.lower()):
                    self.typed = ""
                    threading.Thread(target=self._expand, args=(len(abbr), text, hs["window"]), daemon=True).start()
                    return

    def _expand(self, erase, text, window):
        time.sleep(0.03)  # let the last typed letter land first
        if window and window.lower() not in front_window_title().lower():
            return
        self.sending = True
        try:
            for _ in range(erase):
                self.typer.press(keyboard.Key.backspace)
                self.typer.release(keyboard.Key.backspace)
                time.sleep(0.004)
            self.typer.type(text)
        finally:
            time.sleep(0.03)
            self.sending = False

    def on_click(self, x, y, button, pressed):
        if self.sending:
            return
        self.last_input = time.time()
        btn = "right" if button == mouse.Button.right else "left"
        (self.held.add if pressed else self.held.discard)("mouse_" + btn)
        if self.recording and button in (mouse.Button.left, mouse.Button.right):
            wx, wy, ww, wh = self.own_rect
            if wx <= x < wx + ww and wy <= y < wy + wh:
                return  # clicks on Embris itself aren't recorded
            self._add({"t": "md" if pressed else "mu", "b": btn, "x": int(x), "y": int(y)})

    def on_move(self, x, y):
        if self.sending:
            return
        self.last_input = time.time()
        # Elsewhere, movement is the change in pointer position (macOS uses the raw intercept above).
        if self.recording and not IS_MAC and self.last_pos is not None and not self.own_focused:
            self.add_move(int(x - self.last_pos[0]), int(y - self.last_pos[1]))
        self.last_pos = (x, y)

    def on_scroll(self, x, y, dx, dy):
        if not self.sending:
            self.last_input = time.time()


# ---------------------------------------------------------------- playback

class Player:
    """Plays steps with pynput controllers. play() blocks, so run it on a worker thread."""

    def __init__(self, watcher):
        self.watcher = watcher
        self.kb = keyboard.Controller()
        self.ms = mouse.Controller()
        self.running = False

    def wait_until(self, start, due):
        """Waits for a point on the playback timeline. Timing against the start (not step to
        step) keeps small delays from adding up, so long recordings stay in sync."""
        while self.running:
            left = due - (time.perf_counter() - start)
            if left <= 0:
                return
            time.sleep(0.001 if left > 0.003 else 0)

    def move_by(self, dx, dy, held_buttons=()):
        """Relative mouse movement. On macOS the event carries the raw distance too, which is
        what games read to turn the camera while the cursor is locked."""
        if not IS_MAC:
            self.ms.move(dx, dy)
            return
        import Quartz
        x, y = self.ms.position
        if mouse.Button.left in held_buttons:
            kind, button = Quartz.kCGEventLeftMouseDragged, Quartz.kCGMouseButtonLeft
        elif mouse.Button.right in held_buttons:
            kind, button = Quartz.kCGEventRightMouseDragged, Quartz.kCGMouseButtonRight
        else:
            kind, button = Quartz.kCGEventMouseMoved, Quartz.kCGMouseButtonLeft
        ev = Quartz.CGEventCreateMouseEvent(None, kind, (x + dx, y + dy), button)
        Quartz.CGEventSetIntegerValueField(ev, Quartz.kCGMouseEventDeltaX, dx)
        Quartz.CGEventSetIntegerValueField(ev, Quartz.kCGMouseEventDeltaY, dy)
        Quartz.CGEventPost(Quartz.kCGHIDEventTap, ev)

    def position_ms(self):
        """How far into the sequence the current round has got, in the sequence's own milliseconds."""
        start = getattr(self, "play_start", None)
        if start is None:
            return 0
        return max(0, int((time.perf_counter() - start - self.round_start) * self.play_speed * 1000))

    def play(self, steps, speed, focus_app, app_name, repeat=1, frame_ms=0.0):
        """Plays the steps (repeat times). Returns None, "notfound" or an error message.

        frame_ms (frame sync): the game reads input once per frame, so an input sent near the edge
        of a frame lands in the frame before or after at random. With a frame length given, every
        input goes out in the middle of a frame, the same number of whole frames after the start as
        in the recording, so each replay hits the same frames."""
        prev = None
        if focus_app:
            prev = front_window()
            if not bring_to_front(app_name):
                return "notfound"
            time.sleep(0.25)
        old_pos = self.ms.position
        held_keys, held_buttons, used_mouse = set(), set(), False
        self.watcher.sending = True
        has_moves = any(st["t"] == "mv" for st in steps)
        start, at, due = time.perf_counter(), 0.0, 0.0  # at: the recording's own time; due: when it goes out
        frame = frame_ms / 1000.0
        first = frame / 2  # the middle of the first frame
        # Where playback is (for the time display and the playhead): see position_ms().
        self.play_start, self.play_speed, self.round_start = start, speed, 0.0
        try:
            for n, st in enumerate(itertools.chain.from_iterable(itertools.repeat(steps, max(1, int(repeat))))):
                if not self.running:
                    break
                if steps and n % len(steps) == 0:
                    self.round_start = first + at if frame > 0 else at
                at += st["d"] / 1000.0 / speed
                due = first + round(at / frame) * frame if frame > 0 else at
                self.wait_until(start, due)
                if not self.running:
                    break
                t = st["t"]
                if t == "kd":
                    self.kb.press(to_pynput(st["k"]))
                    held_keys.add(st["k"])
                elif t == "ku":
                    self.kb.release(to_pynput(st["k"]))
                    held_keys.discard(st["k"])
                elif t in ("md", "mu"):
                    used_mouse = True
                    btn = mouse.Button.right if st.get("b") == "right" else mouse.Button.left
                    if not has_moves and st.get("x") is not None:  # gameplay recordings click where the movement left the cursor
                        self.ms.position = (st["x"], st["y"])
                    if t == "md":
                        self.ms.press(btn)
                        held_buttons.add(btn)
                    else:
                        self.ms.release(btn)
                        held_buttons.discard(btn)
                elif t == "mv":
                    self.move_by(st["x"], st["y"], held_buttons)
                elif t == "tx":
                    self.kb.type(st.get("text", ""))
        finally:
            # Release anything still held so nothing gets stuck.
            for k in held_keys:
                self.kb.release(to_pynput(k))
            for b in held_buttons:
                self.ms.release(b)
            if used_mouse and focus_app:
                self.ms.position = old_pos
            if prev:
                time.sleep(0.05)
                restore_front(prev)
            time.sleep(0.05)
            self.watcher.sending = False
        return None


# ---------------------------------------------------------------- auto clicker

class AutoClicker:
    """Clicks a mouse button at a steady rate, where the pointer is or at a set spot, until stopped
    or a number of times. Runs on its own thread with exact timing. Clicks are skipped while the
    pointer is over Embris (own_rect), so its Stop button always works."""

    BUTTONS = (mouse.Button.left, mouse.Button.right, mouse.Button.middle)

    def __init__(self, own_rect_getter):
        self.ms = mouse.Controller()
        self.own_rect = own_rect_getter
        self.running = False
        self.clicks = 0
        self.on_finished = lambda: None  # called on the clicker thread when it stops by itself

    def start(self, interval_ms, button, double, times, fixed, x, y):
        if self.running:
            return
        self.running, self.clicks = True, 0
        threading.Thread(target=self._run, args=(max(0.0, float(interval_ms)), int(button), bool(double), int(times),
                                                 bool(fixed), int(x), int(y)), daemon=True).start()

    def stop(self):
        self.running = False

    def over_embris(self):
        try:
            px, py = self.ms.position
        except Exception:
            return False
        x, y, w, h = self.own_rect()
        return w > 0 and x <= px < x + w and y <= py < y + h

    def _run(self, interval_ms, button, double, times, fixed, x, y):
        btn = self.BUTTONS[max(0, min(2, button))]
        start, due = time.perf_counter(), 0.0
        try:
            while self.running and (times == 0 or self.clicks < times):
                if fixed:
                    self.ms.position = (x, y)
                if not self.over_embris():
                    self.ms.click(btn, 2 if double else 1)
                    self.clicks += 1
                due += max(interval_ms, 1.0) / 1000.0
                now = time.perf_counter() - start
                if now - due > 0.25:  # far behind (the computer was busy): carry on from now, no burst
                    due = now
                while self.running:
                    left = due - (time.perf_counter() - start)
                    if left <= 0:
                        break
                    time.sleep(min(0.05, left - 0.015) if left > 0.02 else 0.0005 if left > 0.002 else 0)
        finally:
            by_itself = self.running
            self.running = False
            if by_itself:
                self.on_finished()
