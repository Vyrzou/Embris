"""Timeline view of a sequence: one track per key and mouse button (grouped into Keyboard and Mouse),
each press drawn as a bar. Drag a bar to move it, drag its ends to change when it starts or how long
it's held; the cut tool splits a hold in two; double-click an empty spot on a track to add a press.
"""
import copy
import math

from PySide6.QtCore import QRectF, Qt, Signal
from PySide6.QtGui import QColor, QFont, QPainter, QPen
from PySide6.QtWidgets import QWidget

import embris_core as core

GUTTER, RULER, ROW_H, SCROLL_H = 142, 30, 36, 12
KEY_BLUE, MOUSE_GOLD, GREEN = "#6080B2", "#D6AA52", "#30C48D"
MOUSE_TRACKS = ("Left click", "Right click", "Mouse moves")


class TlEvent:
    def __init__(self, kind, track, start, dur=0.0, key=None, button=None, x=None, y=None, text=None):
        self.kind, self.track, self.start, self.dur = kind, track, start, dur  # kind: key, left, right, move, text
        self.key, self.button, self.x, self.y, self.text = key, button, x, y, text

    @property
    def holdable(self):
        return self.kind in ("key", "left", "right")


class TimelineEditor(QWidget):
    changed = Signal()
    selection_changed = Signal()
    zoom_changed = Signal()
    crop_changed = Signal()

    def __init__(self, theme_getter):
        super().__init__()
        self.theme = theme_getter
        self.events, self.tracks, self.rows = [], [], []  # rows: ("header", group) or ("track", name)
        self.track_keys, self.extra_tracks, self.hidden = {}, [], set()
        self.collapsed = [False, False]
        self.total = self.trailing = 0.0
        self.px_per_sec, self.scroll_x, self.scroll_row = 200.0, 0.0, 0
        self.selected = self.dragging = None  # selected: the one selected bar (None when none or several)
        self.sel = []                          # every selected bar
        self.drag_mode, self.drag_offset = 0, 0.0
        self.banding, self.band_from, self.band_to = False, None, None  # selection rectangle
        self.group_drag = 0                    # several bars: 1 move, 2 stretch from the left end, 3 from the right
        self.group_orig, self.group_start0, self.group_end0, self.group_grab = {}, 0.0, 0.0, 0.0
        self.crop_a = self.crop_b = -1.0       # crop bars (seconds); -1 when crop is off
        self.crop_drag = 0
        self.crop_apply_rect = self.crop_cancel_rect = QRectF()
        self.snap_to_keys = True   # edges catch the starts and ends of other bars (and the playhead)
        self.snap_guide = -1.0     # where the last snap happened, drawn while dragging
        self.ruler_drag = self.thumb_drag = False
        self.thumb_grab = 0.0
        self.playhead = -1.0
        self.snap_ms = 10
        self.cut_tool = False
        self.setMouseTracking(True)
        self.setFocusPolicy(Qt.FocusPolicy.StrongFocus)
        self.setMinimumHeight(180)

    # ----- sequence <-> events

    def load(self, seq):
        sel = self.selected
        self.events = []
        open_keys, open_buttons = {}, {}
        t = last_input = 0.0
        for st in seq:
            t += st["d"] / 1000.0
            kind, e = st["t"], None
            if kind == "kd" and st["k"] not in open_keys:
                e = open_keys[st["k"]] = TlEvent("key", core.key_label(st["k"]), t, key=st["k"])
            elif kind == "ku" and st["k"] in open_keys:
                k = open_keys.pop(st["k"])
                k.dur = t - k.start
            elif kind == "md" and st.get("b", "left") not in open_buttons:
                b = st.get("b", "left")
                e = open_buttons[b] = TlEvent(b, b.title() + " click", t, button=b, x=st.get("x"), y=st.get("y"))
            elif kind == "mu" and st.get("b", "left") in open_buttons:
                b = open_buttons.pop(st.get("b", "left"))
                b.dur = t - b.start
            elif kind == "mv":
                e = TlEvent("move", "Mouse moves", t, x=st["x"], y=st["y"])
            elif kind == "tx":
                e = TlEvent("text", "Typing", t, text=st.get("text", ""))
            if kind != "w":
                last_input = t
            if e:
                self.events.append(e)
        for e in list(open_keys.values()) + list(open_buttons.values()):
            e.dur = max(0.0, t - e.start)  # never released: held to the end
        self.total, self.trailing = t, max(0.0, t - last_input)
        kept = []
        for old in self.sel:
            m = next((e for e in self.events if e.kind == old.kind and e.key == old.key
                      and e.button == old.button and abs(e.start - old.start) < 0.0005), None)
            if m is not None:
                kept.append(m)
        self.sel = kept
        self.selected = kept[0] if len(kept) == 1 else None
        self.build_rows()
        self.update()

    def build_rows(self):
        self.tracks = []
        for e in self.events:
            if e.track not in self.tracks:
                self.tracks.append(e.track)
                if e.kind == "key":
                    self.track_keys[e.track] = e.key
        self.tracks += [x for x in self.extra_tracks if x not in self.tracks]
        self.rows = []
        for g in (0, 1):
            mine = [tr for tr in self.tracks if (tr in MOUSE_TRACKS) == (g == 1)]
            if not mine:
                continue
            self.rows.append(("header", g))
            if not self.collapsed[g]:
                self.rows += [("track", tr) for tr in mine]
        self.scroll_row = max(0, min(self.scroll_row, max(0, len(self.rows) - 1)))

    def to_steps(self):
        """Back to steps; delays come from rounded absolute times so nothing drifts."""
        atoms = []
        for e in self.events:
            if e.kind == "key":
                atoms += [(e.start, {"t": "kd", "k": e.key}), (e.start + e.dur, {"t": "ku", "k": e.key})]
            elif e.kind in ("left", "right"):
                atoms += [(e.start, {"t": "md", "b": e.button, "x": e.x, "y": e.y}),
                          (e.start + e.dur, {"t": "mu", "b": e.button, "x": e.x, "y": e.y})]
            elif e.kind == "move":
                atoms.append((e.start, {"t": "mv", "x": e.x, "y": e.y}))
            else:
                atoms.append((e.start, {"t": "tx", "text": e.text}))
        order = sorted(range(len(atoms)), key=lambda i: (atoms[i][0], i))
        steps, last = [], 0
        for i in order:
            ms = int(round(atoms[i][0] * 1000))
            st = atoms[i][1]
            st["d"] = max(0, ms - last)
            last = max(last, ms)
            steps.append(st)
        if self.trailing > 0.0005:
            steps.append({"t": "w", "d": int(round(self.trailing * 1000))})
        return steps

    # ----- editing

    # ----- selection

    @property
    def selection_count(self):
        return len(self.sel)

    @property
    def group_start(self):
        return min((e.start for e in self.sel), default=0.0)

    @property
    def group_end(self):
        return max((e.start + e.dur for e in self.sel), default=0.0)

    @property
    def cropping(self):
        return self.crop_a >= 0

    def set_selection(self, events):
        self.sel = []
        for e in events or []:
            if e is not None and e not in self.sel:
                self.sel.append(e)
        self.selected = self.sel[0] if len(self.sel) == 1 else None
        self.selection_changed.emit()
        self.update()

    def select_only(self, e):
        self.set_selection([e] if e is not None else [])

    def select_all(self):
        self.set_selection([e for e in self.events if e.track not in self.hidden])

    def set_selected(self, start, dur):
        if self.selected is None:
            self.set_group(start, dur)
            return
        self.selected.start = max(0.0, start)
        if self.selected.holdable:
            self.selected.dur = max(0.0, dur)
        self.commit()

    def set_selected_position(self, x, y):
        if self.selected is not None:
            self.selected.x, self.selected.y = x, y
            self.commit()

    def set_selected_key(self, key):
        e = self.selected
        if e is not None and e.kind == "key":
            e.key, e.track = key, core.key_label(key)
            self.commit()

    def set_selected_text(self, text):
        if self.selected is not None and self.selected.kind == "text":
            self.selected.text = text
            self.commit()

    def set_selected_kind(self, kind, key):
        """Action type: key (hold), left, right, move or text."""
        e = self.selected
        if e is None or e.kind == kind:
            return
        e.kind = kind
        if kind == "key":
            e.key, e.track, e.dur = key, core.key_label(key), e.dur or 0.05
        elif kind in ("left", "right"):
            e.button, e.track, e.x, e.y, e.dur = kind, kind.title() + " click", None, None, e.dur or 0.04
        elif kind == "move":
            e.track, e.x, e.y, e.dur = "Mouse moves", 10, 0, 0.0
        else:
            e.track, e.text, e.dur = "Typing", "hello", 0.0
        self.commit()

    def delete_selected(self):
        if not self.sel:
            return
        for e in self.sel:
            self.events.remove(e)
        self.commit()
        self.set_selection([])

    def duplicate_selected(self):
        """Copies right after the originals (a group stays together)."""
        if not self.sel:
            return
        shift = max(self.group_end - self.group_start, 0.01) + self.snap(0.05)
        copies = []
        for e in self.sel:
            c = copy.copy(e)
            c.start = e.start + shift
            self.events.append(c)
            copies.append(c)
        self.commit()
        self.set_selection(copies)

    def set_group(self, start, span):
        """Several bars: move them to start at `start` and squish or stretch them to last `span` seconds."""
        if len(self.sel) < 2:
            return
        s0, length = self.group_start, self.group_end - self.group_start
        k = max(0.0, span) / length if length > 0.0005 else 1.0
        for e in self.sel:
            e.start = max(0.0, start) + (e.start - s0) * k
            e.dur *= k
        self.commit()

    # ----- cut & crop

    def split_all(self, at):
        """Cuts every hold that crosses this time, splitting the sequence into sections."""
        for e in list(self.events):
            if e.holdable and e.track not in self.hidden and e.start + 0.02 < at < e.start + e.dur - 0.02:
                self.split(e, at, commit=False)
        self.commit()
        self.set_playhead(at)

    def start_crop(self):
        """Shows the two crop bars (around the selection, or the whole sequence)."""
        if len(self.sel) > 1:
            self.crop_a, self.crop_b = self.group_start, self.group_end
        else:
            self.crop_a, self.crop_b = 0.0, max(0.1, self.total)
        self.crop_changed.emit()
        self.update()

    def cancel_crop(self):
        self.crop_a = self.crop_b = -1.0
        self.crop_changed.emit()
        self.update()

    def apply_crop(self):
        """Keeps only the part between the crop bars (holds crossing a bar are trimmed) and starts it at 0."""
        if self.crop_a < 0:
            return
        a, b = min(self.crop_a, self.crop_b), max(self.crop_a, self.crop_b)
        for e in list(self.events):
            end = e.start + e.dur
            if end < a or e.start > b or (e.dur == 0 and e.start < a):
                self.events.remove(e)
                continue
            s = max(e.start, a)
            e.dur = min(end, b) - s
            e.start = s - a
        last = max((e.start + e.dur for e in self.events), default=0.0)
        self.trailing = max(0.0, (b - a) - last)
        self.crop_a = self.crop_b = -1.0
        self.sel, self.selected = [], None
        self.commit()
        self.crop_changed.emit()
        self.selection_changed.emit()

    def add_track(self, key):
        """An empty track for a key, ready to add presses to (double-click it)."""
        name = core.key_label(key)
        self.track_keys[name] = key
        if name not in self.extra_tracks:
            self.extra_tracks.append(name)
        self.collapsed[0] = False
        self.build_rows()
        self.update()

    def set_zoom(self, px):
        self.px_per_sec = max(5.0, min(5000.0, px))
        self.update()

    def zoom(self, factor):
        self.set_zoom(self.px_per_sec * factor)
        self.zoom_changed.emit()

    def fit(self):
        self.px_per_sec = max(5.0, (self.width() - GUTTER - 24) / max(0.5, self.total))
        self.scroll_x = 0.0
        self.update()
        self.zoom_changed.emit()

    def set_playhead(self, sec):
        self.playhead = sec
        self.update()

    def commit(self):
        self.events.sort(key=lambda e: e.start)
        self.total = max((e.start + e.dur for e in self.events), default=0.0) + self.trailing
        self.build_rows()
        self.changed.emit()
        self.update()

    # ----- geometry

    def snap(self, sec):
        return sec if self.snap_ms <= 0 else round(sec * 1000 / self.snap_ms) * self.snap_ms / 1000.0

    def xpos(self, sec):
        return GUTTER + (sec - self.scroll_x) * self.px_per_sec

    def sec(self, x):
        return self.scroll_x + (x - GUTTER) / self.px_per_sec

    def row_y(self, row):
        return RULER + (row - self.scroll_row) * ROW_H

    def rows_bottom(self):
        return self.height() - SCROLL_H - 4

    def visible_rows(self):
        return max(1, int((self.rows_bottom() - RULER) // ROW_H))

    def span(self):
        return max(self.total, self.scroll_x + (self.width() - GUTTER) / self.px_per_sec) + 0.5

    def bar_rect(self, e):
        try:
            row = self.rows.index(("track", e.track))
        except ValueError:
            return None
        w = max(3 if e.kind in ("move", "text") else 10, e.dur * self.px_per_sec)
        return QRectF(self.xpos(e.start), self.row_y(row) + 5, w, ROW_H - 10)

    def scroll_track(self):
        return QRectF(GUTTER, self.height() - SCROLL_H - 2, self.width() - GUTTER - 6, SCROLL_H - 2)

    def thumb(self):
        tr = self.scroll_track()
        view = (self.width() - GUTTER) / self.px_per_sec
        w = max(30.0, tr.width() * min(1.0, view / self.span()))
        x = tr.x() + (tr.width() - w) * min(1.0, self.scroll_x / max(0.001, self.span() - view))
        return QRectF(x, tr.y() + 2, w, tr.height() - 4)

    def hit(self, pos):
        for e in reversed(self.events):
            if e.track in self.hidden:
                continue
            r = self.bar_rect(e)
            if r is None or r.top() < RULER - 2:
                continue
            r = r.adjusted(-3, 0, 3, 0)
            if r.contains(pos):
                if e.holdable and r.width() > 20:
                    return e, 2 if pos.x() <= r.left() + 7 else 3 if pos.x() >= r.right() - 7 else 1
                return e, 1
        return None, 0

    # ----- input

    def mousePressEvent(self, ev):
        self.setFocus()
        pos = ev.position()
        if pos.y() >= self.scroll_track().y() - 2 and pos.x() >= GUTTER:
            th = self.thumb()
            if th.left() <= pos.x() <= th.right():
                self.thumb_drag, self.thumb_grab = True, pos.x() - th.x()
            else:
                self.scroll_x = max(0.0, self.scroll_x + (-1 if pos.x() < th.x() else 1) * (self.width() - GUTTER) * 0.8 / self.px_per_sec)
                self.update()
            return
        if ev.button() == Qt.MouseButton.LeftButton and self.crop_a >= 0 and pos.x() >= GUTTER:
            if self.crop_apply_rect.contains(pos):
                self.apply_crop()
                return
            if self.crop_cancel_rect.contains(pos):
                self.cancel_crop()
                return
            bar = self.crop_bar_at(pos.x())
            if bar:
                self.crop_drag = bar
                return
        if pos.y() < RULER and pos.x() >= GUTTER and not self.cut_tool:
            self.ruler_drag = True
            self.set_playhead(max(0.0, self.snap(self.sec(pos.x()))))
            return
        if pos.x() < GUTTER:
            r = self.scroll_row + int((pos.y() - RULER) // ROW_H)
            if pos.y() < RULER or not 0 <= r < len(self.rows):
                return
            what, value = self.rows[r]
            if what == "header":
                self.collapsed[value] = not self.collapsed[value]
                self.build_rows()
            elif pos.x() >= GUTTER - 30:
                self.hidden ^= {value}
                if any(e.track in self.hidden for e in self.sel):
                    self.set_selection([e for e in self.sel if e.track not in self.hidden])
            self.update()
            return
        if ev.button() != Qt.MouseButton.LeftButton:
            return
        e, mode = self.hit(pos)
        if self.cut_tool:
            # Cut tool: a bar is split where clicked; an empty spot cuts every track there.
            at = self.snap(self.sec(pos.x()))
            if e is not None and e.holdable:
                self.split(e, at)
            elif e is None:
                self.split_all(at)
            return
        edge = self.group_edge_at(pos)
        if edge:  # several bars selected: drag the box ends to squish or stretch them
            self.begin_group_drag(edge, pos.x())
            return
        add = bool(ev.modifiers() & (Qt.KeyboardModifier.ShiftModifier | Qt.KeyboardModifier.ControlModifier | Qt.KeyboardModifier.MetaModifier))
        if e is None:
            # Empty spot: drag a rectangle to select several bars.
            if not add:
                self.set_selection([])
            self.banding, self.band_from, self.band_to = True, pos, pos
            return
        if add:
            self.set_selection([x for x in self.sel if x is not e] if e in self.sel else self.sel + [e])
            return
        if len(self.sel) > 1 and e in self.sel:
            self.begin_group_drag(1, pos.x())  # move them all
            return
        self.select_only(e)
        self.dragging, self.drag_mode = e, mode
        self.drag_offset = self.sec(pos.x()) - e.start

    def crop_bar_at(self, x):
        if self.crop_a < 0:
            return 0
        if abs(x - self.xpos(self.crop_a)) <= 6:
            return 1
        if abs(x - self.xpos(self.crop_b)) <= 6:
            return 2
        return 0

    def group_rect(self):
        """The box around several selected bars (None when fewer than two)."""
        if len(self.sel) < 2:
            return None
        rects = [r for r in (self.bar_rect(e) for e in self.sel) if r is not None]
        if not rects:
            return None
        top, bottom = min(r.top() for r in rects) - 3, max(r.bottom() for r in rects) + 3
        x1 = self.xpos(self.group_start)
        x2 = max(self.xpos(self.group_end), x1 + 6)
        return QRectF(x1, top, x2 - x1, bottom - top)

    def group_edge_at(self, pos):
        g = self.group_rect()
        if g is None or not g.top() <= pos.y() <= g.bottom():
            return 0
        if abs(pos.x() - g.left()) <= 6:
            return 2
        if abs(pos.x() - g.right()) <= 6:
            return 3
        return 0

    def begin_group_drag(self, kind, x):
        self.group_drag = kind
        self.group_start0, self.group_end0 = self.group_start, self.group_end
        self.group_grab = self.sec(x)
        self.group_orig = {id(e): (e, e.start, e.dur) for e in self.sel}

    def group_drag_to(self, x):
        """Moves (1) or rescales (2 = left end, 3 = right end) the selected bars from where the drag began."""
        a, b = self.group_start0, self.group_end0
        length = max(0.0005, b - a)
        s = 0.0 if self.group_drag == 1 else self.snap_edge(self.snap(self.sec(x)), self.sel)
        move = self.snap(self.sec(x) - self.group_grab)
        if self.group_drag == 1:
            move = self.snap_move(a + move, b + move, self.sel) - a  # either end of the group may catch a key
        for e, st, du in self.group_orig.values():
            if self.group_drag == 1:
                e.start = max(0.0, st + max(move, -a))
            elif self.group_drag == 3:
                k = max(0.01, (s - a) / length)
                e.start, e.dur = a + (st - a) * k, du * k
            else:
                k = max(0.01, (b - max(0.0, s)) / length)
                e.start, e.dur = b - (b - st) * k, du * k
        self.total = max([self.total] + [e.start + e.dur for e in self.sel])
        self.selection_changed.emit()
        self.update()

    # ----- snapping to keys

    def nearest_key(self, t, exclude):
        """The nearest key edge within a few pixels of t: (key, distance), or (None, inf)."""
        if not self.snap_to_keys:
            return None, math.inf
        reach = 8 / self.px_per_sec
        keys = [0.0] + ([self.playhead] if self.playhead >= 0 else [])
        for e in self.events:
            if e.track in self.hidden or (exclude and e in exclude):
                continue
            keys.append(e.start)
            if e.dur > 0:
                keys.append(e.start + e.dur)
        best, dist = None, math.inf
        for k in keys:
            d = abs(k - t)
            if d <= reach and d < dist:
                best, dist = k, d
        return best, dist

    def snap_edge(self, t, exclude):
        """An edge being dragged: catches a nearby key."""
        k, _ = self.nearest_key(t, exclude)
        self.snap_guide = k if k is not None else -1.0
        return t if k is None else k

    def snap_move(self, start, end, exclude):
        """Something being moved: whichever end is closer to a key catches it. Returns the new start."""
        k1, d1 = self.nearest_key(start, exclude)
        k2, d2 = self.nearest_key(end, exclude)
        if k1 is None and k2 is None:
            self.snap_guide = -1.0
            return start
        if k1 is not None and d1 <= d2:
            self.snap_guide = k1
            return k1
        self.snap_guide = k2
        return start + (k2 - end)

    def band_rect(self):
        a, b = self.band_from, self.band_to
        return QRectF(min(a.x(), b.x()), min(a.y(), b.y()), abs(a.x() - b.x()) + 1, abs(a.y() - b.y()) + 1)

    def split(self, e, at, commit=True):
        """Cuts a hold in two at the given time, with a 10 ms gap so the key is really let go."""
        end = e.start + e.dur
        if at <= e.start + 0.02 or at >= end - 0.02:
            return
        second = copy.copy(e)
        e.dur = at - e.start - 0.01
        second.start, second.dur = at, end - at
        self.events.append(second)
        if commit:
            self.commit()
            self.select_only(second)

    def mouseDoubleClickEvent(self, ev):
        pos = ev.position()
        if pos.x() < GUTTER or pos.y() < RULER or pos.y() > self.rows_bottom() or self.hit(pos)[0] is not None:
            return
        r = self.scroll_row + int((pos.y() - RULER) // ROW_H)
        if not 0 <= r < len(self.rows) or self.rows[r][0] == "header":
            return
        track, start = self.rows[r][1], max(0.0, self.snap(self.sec(pos.x())))
        if track in ("Left click", "Right click"):
            b = "left" if track == "Left click" else "right"
            e = TlEvent(b, track, start, 0.1, button=b)
        elif track == "Mouse moves":
            e = TlEvent("move", track, start, x=10, y=0)
        elif track == "Typing":
            e = TlEvent("text", track, start, text="hello")
        elif track in self.track_keys:
            e = TlEvent("key", track, start, 0.1, key=self.track_keys[track])
        else:
            return
        self.events.append(e)
        self.commit()
        self.select_only(e)

    def mouseMoveEvent(self, ev):
        pos = ev.position()
        x = pos.x()
        if self.thumb_drag:
            tr = self.scroll_track()
            view = (self.width() - GUTTER) / self.px_per_sec
            room = tr.width() - self.thumb().width()
            if room > 0:
                self.scroll_x = max(0.0, (x - tr.x() - self.thumb_grab) * (self.span() - view) / room)
            self.update()
            return
        if self.ruler_drag:
            self.set_playhead(max(0.0, self.snap(self.sec(x))))
            return
        if self.crop_drag:
            s = max(0.0, self.snap_edge(self.snap(self.sec(x)), None))
            if self.crop_drag == 1:
                self.crop_a = s
            else:
                self.crop_b = s
            self.update()
            return
        if self.banding:
            self.band_to = pos
            self.update()
            return
        if self.group_drag:
            self.group_drag_to(x)
            return
        d = self.dragging
        if d is not None:
            s = 0.0 if self.drag_mode == 1 else self.snap_edge(self.snap(self.sec(x)), [d])
            if self.drag_mode == 1:
                start = max(0.0, self.snap(self.sec(x) - self.drag_offset))
                d.start = max(0.0, self.snap_move(start, start + d.dur, [d]))  # its start or end may catch a key
            elif self.drag_mode == 2:
                end = d.start + d.dur
                d.start = max(0.0, min(s, end))
                d.dur = end - d.start
            elif self.drag_mode == 3:
                d.dur = max(0.0, s - d.start)
            self.total = max(self.total, d.start + d.dur)
            self.selection_changed.emit()
            self.update()
            return
        if x < GUTTER:
            self.setCursor(Qt.CursorShape.PointingHandCursor)
            return
        if self.crop_bar_at(x) or self.group_edge_at(pos):
            self.setCursor(Qt.CursorShape.SizeHorCursor)
            return
        if self.crop_apply_rect.contains(pos) or self.crop_cancel_rect.contains(pos):
            self.setCursor(Qt.CursorShape.PointingHandCursor)
            return
        e, mode = self.hit(pos)
        self.setCursor((Qt.CursorShape.SplitHCursor if self.cut_tool else Qt.CursorShape.ArrowCursor) if e is None else
                       Qt.CursorShape.SplitHCursor if self.cut_tool and e.holdable else
                       Qt.CursorShape.SizeHorCursor if mode in (2, 3) else Qt.CursorShape.PointingHandCursor)

    def mouseReleaseEvent(self, ev):
        self.ruler_drag = self.thumb_drag = False
        self.crop_drag = 0
        if self.snap_guide >= 0:
            self.snap_guide = -1.0
            self.update()
        if self.banding:
            # Select every visible bar the rectangle touches.
            self.banding = False
            band = self.band_rect()
            add = bool(ev.modifiers() & (Qt.KeyboardModifier.ShiftModifier | Qt.KeyboardModifier.ControlModifier | Qt.KeyboardModifier.MetaModifier))
            picked = list(self.sel) if add else []
            for e in self.events:
                r = self.bar_rect(e)
                if e.track not in self.hidden and e not in picked and r is not None and r.intersects(band):
                    picked.append(e)
            self.set_selection(picked)
            return
        if self.group_drag:
            self.group_drag = 0
            self.commit()
            return
        if self.dragging is not None:
            self.dragging = None
            self.commit()

    def wheelEvent(self, ev):
        delta = ev.angleDelta().y() or ev.angleDelta().x()
        mods = ev.modifiers()
        if mods & (Qt.KeyboardModifier.ControlModifier | Qt.KeyboardModifier.MetaModifier):
            at = self.sec(ev.position().x())
            self.zoom(1.25 if delta > 0 else 0.8)
            self.scroll_x = max(0.0, at - (ev.position().x() - GUTTER) / self.px_per_sec)
        elif not mods & Qt.KeyboardModifier.ShiftModifier and len(self.rows) > self.visible_rows():
            self.scroll_row = max(0, min(len(self.rows) - self.visible_rows(), self.scroll_row - (1 if delta > 0 else -1)))
        else:
            self.scroll_x = max(0.0, min(max(0.0, self.total - 0.5), self.scroll_x - math.copysign(80, delta) / self.px_per_sec))
        self.update()

    def keyPressEvent(self, ev):
        ctrl = ev.modifiers() & (Qt.KeyboardModifier.ControlModifier | Qt.KeyboardModifier.MetaModifier)
        k = ev.key()
        if k in (Qt.Key.Key_Delete, Qt.Key.Key_Backspace):
            self.delete_selected()
        elif k == Qt.Key.Key_D and ctrl:
            self.duplicate_selected()
        elif k == Qt.Key.Key_A and ctrl:
            self.select_all()
        elif k in (Qt.Key.Key_Return, Qt.Key.Key_Enter) and self.crop_a >= 0:
            self.apply_crop()
        elif k == Qt.Key.Key_Escape:
            if self.crop_a >= 0:
                self.cancel_crop()
            else:
                self.set_selection([])
        elif self.sel and k in (Qt.Key.Key_Left, Qt.Key.Key_Right):
            # Nudge the selected bars by one snap step.
            step = max(1, self.snap_ms) / 1000.0 * (1 if k == Qt.Key.Key_Right else -1)
            step = max(step, -self.group_start)
            for e in self.sel:
                e.start += step
            self.commit()
            self.selection_changed.emit()
        else:
            super().keyPressEvent(ev)

    # ----- drawing

    def paintEvent(self, _):
        t = self.theme()
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.setPen(QPen(QColor(_blend(t.border, t.accent, 0.5) if self.hasFocus() else t.border), 1))
        p.setBrush(QColor(t.input))
        p.drawRoundedRect(QRectF(0.5, 0.5, self.width() - 1, self.height() - 1), 6, 6)
        small = QFont(self.font())
        small.setPixelSize(11)
        bold = QFont(small)
        bold.setBold(True)
        name_font = QFont(self.font())
        name_font.setPixelSize(12)
        p.setFont(small)
        mid = Qt.AlignmentFlag.AlignVCenter
        bottom = self.rows_bottom()
        if not self.events and not self.extra_tracks:
            p.setPen(QColor(t.sub))
            p.drawText(self.rect(), Qt.AlignmentFlag.AlignCenter,
                       "Nothing to show yet. Record a sequence or add steps, and each key gets its own track here.")
            return

        # Group headers and tracks.
        p.save()
        p.setClipRect(QRectF(0, RULER, self.width(), bottom - RULER))
        for r in range(self.scroll_row, len(self.rows)):
            y = self.row_y(r)
            if y > bottom:
                break
            what, value = self.rows[r]
            if what == "header":
                p.setFont(bold)
                p.setPen(QColor(t.text))
                p.drawText(QRectF(12, y, GUTTER - 16, ROW_H), mid, ("▸  " if self.collapsed[value] else "▾  ") + ("KEYBOARD" if value == 0 else "MOUSE"))
                continue
            p.fillRect(QRectF(GUTTER, y + 1, self.width() - GUTTER - 2, ROW_H - 2), QColor(_blend(t.input, t.text, 0.03 if r % 2 == 0 else 0.015)))
            off = value in self.hidden
            color = QColor(_blend(t.sub, t.input, 0.4) if off else t.text)
            icon = value in MOUSE_TRACKS or value == "Typing"
            chip = QRectF(12, y + (ROW_H - 24) / 2, 30 if icon else GUTTER - 48, 24)
            p.setPen(QPen(QColor(_blend(t.border, t.text, 0.15)), 1))
            p.setBrush(QColor(_blend(t.input, t.text, 0.06)))
            p.drawRoundedRect(chip, 4, 4)
            p.setPen(color)
            p.setFont(name_font)
            glyph = {"Mouse moves": "✥", "Typing": "T"}.get(value, "◉") if icon else value
            p.drawText(chip, Qt.AlignmentFlag.AlignCenter, p.fontMetrics().elidedText(glyph, Qt.TextElideMode.ElideRight, int(chip.width() - 6)))
            if icon:
                p.drawText(QRectF(48, y, GUTTER - 82, ROW_H), mid, p.fontMetrics().elidedText(value, Qt.TextElideMode.ElideRight, GUTTER - 82))
            p.setPen(QColor(t.sub if off else t.accent))
            p.drawText(QRectF(GUTTER - 28, y, 22, ROW_H), Qt.AlignmentFlag.AlignCenter, "◌" if off else "◉")
        p.restore()
        p.setPen(QColor(t.border))
        p.drawLine(GUTTER - 1, 4, GUTTER - 1, self.height() - 4)

        # Ruler.
        p.save()
        p.setClipRect(QRectF(GUTTER, 0, self.width() - GUTTER - 2, self.height()))
        p.setFont(small)
        step = next((s for s in (0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 30, 60, 120, 300, 600, 1800, 3600)
                     if s * self.px_per_sec >= 70), 3600)
        s = math.floor(self.scroll_x / step) * step
        while self.xpos(s) < self.width():
            x = self.xpos(s)
            p.setPen(QColor(_blend(t.border, t.input, 0.55)))
            for m in range(1, 5):
                mx = self.xpos(s + step * m / 5)
                if mx > GUTTER:
                    p.drawLine(int(mx), RULER - 5, int(mx), RULER - 1)
            if x >= GUTTER:
                p.setPen(QColor(_blend(t.border, t.input, 0.3)))
                p.drawLine(int(x), RULER - 10, int(x), int(bottom))
                p.setPen(QColor(t.sub))
                fmt = "%.2fs" if step < 0.1 else "%.1fs"
                p.drawText(QRectF(x + 3, 3, 60, RULER - 14), mid, fmt % s if s < 60 else "%gm" % round(s / 60, 1))
            s += step
        end_pen = QPen(QColor(t.accent))
        end_pen.setStyle(Qt.PenStyle.DashLine)
        p.setPen(end_pen)
        p.drawLine(int(self.xpos(self.total)), RULER, int(self.xpos(self.total)), int(bottom))

        # Bars.
        p.setClipRect(QRectF(GUTTER, RULER, self.width() - GUTTER - 2, bottom - RULER))
        for e in self.events:
            if e.track in self.hidden:
                continue
            r = self.bar_rect(e)
            if r is None or r.right() < GUTTER or r.left() > self.width() or r.bottom() < RULER or r.top() > bottom:
                continue
            tone = KEY_BLUE if e.kind == "key" else MOUSE_GOLD if e.kind in ("left", "right") else t.sub if e.kind == "move" else GREEN
            fill = _blend(t.input, tone, 0.45 if e.kind == "move" else 0.42)
            is_sel = e in self.sel
            p.setPen(QPen(QColor(t.accent if is_sel else _blend(fill, tone, 0.6)), 2 if is_sel else 1))
            p.setBrush(QColor(fill))
            p.drawRoundedRect(r, 5, 5)
            if e.kind != "move" and r.width() > 26:
                text = '"%s"' % e.text if e.kind == "text" else e.track
                if e.kind == "key" and e.dur >= 0.12:
                    text += " (hold)"
                p.setPen(QColor(t.text))
                p.drawText(r.adjusted(6, 0, -4, 0), Qt.AlignmentFlag.AlignCenter,
                           p.fontMetrics().elidedText(text, Qt.TextElideMode.ElideRight, int(r.width() - 10)))
            if e is self.selected and e.holdable:
                p.setPen(Qt.PenStyle.NoPen)
                p.setBrush(QColor(t.accent))
                p.drawRoundedRect(QRectF(r.left() - 2, r.top() + 5, 5, r.height() - 10), 2, 2)
                p.drawRoundedRect(QRectF(r.right() - 3, r.top() + 5, 5, r.height() - 10), 2, 2)

        # Several selected: a box around them with a handle on each end to squish or stretch them.
        g = self.group_rect()
        if g is not None:
            dash = QPen(QColor(t.accent))
            dash.setStyle(Qt.PenStyle.DashLine)
            p.setPen(dash)
            p.setBrush(Qt.BrushStyle.NoBrush)
            p.drawRect(g)
            p.setPen(QPen(QColor(t.text), 1))
            p.setBrush(QColor(t.accent))
            for hx in (g.left(), g.right()):
                p.drawRoundedRect(QRectF(hx - 3, g.center().y() - 10, 6, 20), 3, 3)
        if self.snap_guide >= 0 and (self.dragging is not None or self.group_drag or self.crop_drag):
            guide = QPen(QColor(t.accent), 1.5)
            guide.setStyle(Qt.PenStyle.DotLine)
            p.setPen(guide)
            x = self.xpos(self.snap_guide)
            p.drawLine(int(x), RULER, int(x), int(bottom))
        if self.banding:
            band = self.band_rect()
            p.setPen(QPen(QColor(t.accent), 1))
            fill = QColor(t.accent)
            fill.setAlpha(40)
            p.setBrush(fill)
            p.drawRect(band)

        # Crop: everything outside the two bars is dimmed; drag the bars, then click Crop.
        if self.crop_a >= 0:
            p.setClipRect(QRectF(GUTTER, 0, self.width() - GUTTER - 2, bottom))
            xa, xb = self.xpos(min(self.crop_a, self.crop_b)), self.xpos(max(self.crop_a, self.crop_b))
            shade = QColor(t.input)
            shade.setAlpha(150)
            p.fillRect(QRectF(GUTTER, RULER, max(0.0, xa - GUTTER), bottom - RULER), shade)
            p.fillRect(QRectF(xb, RULER, max(0.0, self.width() - xb), bottom - RULER), shade)
            for x in (xa, xb):
                p.setPen(QPen(QColor(t.accent), 2))
                p.drawLine(int(x), 2, int(x), int(bottom))
                p.setPen(QPen(QColor(t.text), 1))
                p.setBrush(QColor(t.accent))
                p.drawRoundedRect(QRectF(x - 4, RULER + (bottom - RULER) / 2 - 14, 8, 28), 4, 4)
        p.restore()

        # Playhead with its time.
        x = self.xpos(self.playhead)
        if self.playhead >= 0 and GUTTER <= x < self.width():
            p.setPen(QPen(QColor(t.text), 1.5))
            p.drawLine(int(x), RULER - 4, int(x), int(bottom))
            label = "%.2fs" % self.playhead
            p.setFont(bold)
            w = p.fontMetrics().horizontalAdvance(label) + 10
            badge = QRectF(max(GUTTER, x - w / 2), 2, w, 18)
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(QColor(t.accent))
            p.drawRoundedRect(badge, 4, 4)
            p.setPen(QColor(t.on_accent))
            p.drawText(badge, Qt.AlignmentFlag.AlignCenter, label)

        # Crop and cancel buttons, on top of everything, just under the ruler between the bars.
        self.crop_apply_rect = self.crop_cancel_rect = QRectF()
        if self.crop_a >= 0:
            xa, xb = self.xpos(min(self.crop_a, self.crop_b)), self.xpos(max(self.crop_a, self.crop_b))
            p.setFont(bold)
            bw, bh = p.fontMetrics().horizontalAdvance("Crop") + 18, 22
            mid = max(GUTTER + 4, min(self.width() - bw - bh - 12, (xa + xb) / 2 - (bw + bh + 4) / 2))
            self.crop_apply_rect = QRectF(mid, RULER + 4, bw, bh)
            self.crop_cancel_rect = QRectF(mid + bw + 4, RULER + 4, bh, bh)
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(QColor(t.accent))
            p.drawRoundedRect(self.crop_apply_rect, 5, 5)
            p.setPen(QColor(t.on_accent))
            p.drawText(self.crop_apply_rect, Qt.AlignmentFlag.AlignCenter, "Crop")
            p.setPen(QPen(QColor(t.border), 1))
            p.setBrush(QColor(_blend(t.input, t.text, 0.12)))
            p.drawRoundedRect(self.crop_cancel_rect, 5, 5)
            p.setPen(QColor(t.text))
            p.drawText(self.crop_cancel_rect, Qt.AlignmentFlag.AlignCenter, "×")

        # Ruler line, more-tracks hint and the time scrollbar.
        p.setPen(QColor(t.border))
        p.drawLine(4, RULER - 1, self.width() - 4, RULER - 1)
        if len(self.rows) > self.visible_rows():
            p.setFont(small)
            p.setPen(QColor(t.sub))
            p.drawText(QRectF(12, 4, GUTTER - 16, RULER - 10), mid, "Wheel: more tracks")
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(QColor(_blend(t.input, t.text, 0.04)))
        p.drawRoundedRect(self.scroll_track(), 4, 4)
        p.setBrush(QColor(_blend(t.border, t.text, 0.2)))
        p.drawRoundedRect(self.thumb(), 3, 3)


def _blend(a, b, t):
    a, b = a.lstrip("#"), b.lstrip("#")
    ca = [int(a[i:i + 2], 16) for i in (0, 2, 4)]
    cb = [int(b[i:i + 2], 16) for i in (0, 2, 4)]
    return "#" + "".join("%02X" % int(x + (y - x) * t) for x, y in zip(ca, cb))
