"""The sequence editor's pieces: a model that groups raw steps into editable rows ("Key press",
"Delay"...), the step table and the keyboard preview. Same behaviour as the Windows app.
"""
import copy

from PySide6.QtCore import QPoint, QRectF, Qt, Signal
from PySide6.QtGui import QColor, QFont, QPainter, QPen
from PySide6.QtWidgets import QWidget

import embris_core as core

# ---------------------------------------------------------------- model

KEY_PRESS, KEY_DOWN, KEY_UP, CLICK, MOUSE_DOWN, MOUSE_UP, MOVE, TYPE, DELAY = range(9)
NAMES = ("Key press", "Key down", "Key up", "Click", "Mouse down", "Mouse up", "Mouse move", "Type text", "Delay")
ICONS = ("⌨", "↓", "↑", "◉", "↓", "↑", "✥", "T", "◷")
MOVE_GAP_MS = 250  # a pause longer than this splits camera movement into separate rows
DASH = "—"


class Row:
    """One row of the editor: steps first..last of the sequence."""

    def __init__(self, kind, first, last):
        self.kind, self.first, self.last = kind, first, last

    @property
    def count(self):
        return self.last - self.first + 1

    @property
    def is_key(self):
        return self.kind <= KEY_UP

    @property
    def is_mouse(self):
        return CLICK <= self.kind <= MOUSE_UP


def block_length(seq, i):
    """How many steps belong together from i: a press and its release, a click, or a run of camera movement."""
    st = seq[i]
    nx = seq[i + 1] if i + 1 < len(seq) else None
    if st["t"] == "kd" and nx and nx["t"] == "ku" and nx.get("k") == st.get("k"):
        return 2
    if st["t"] == "md" and nx and nx["t"] == "mu" and nx.get("b", "left") == st.get("b", "left"):
        return 2
    if st["t"] == "mv":
        j = i + 1
        while j < len(seq) and seq[j]["t"] == "mv" and seq[j]["d"] <= MOVE_GAP_MS:
            j += 1
        return j - i
    return 1


def normalize(seq):
    """Puts every pause in a Delay step of its own so each row is one action. Timing stays the same.
    Changed steps are copies, so a sequence that is playing right now keeps its own steps."""
    out, i = [], 0
    while i < len(seq):
        st = seq[i]
        if st["t"] == "w":
            out.append(st)
            i += 1
            continue
        n = block_length(seq, i)
        if st["d"] > 0:
            out.append({"t": "w", "d": st["d"]})
            st = dict(st, d=0)
        out.append(st)
        out.extend(seq[i + 1:i + n])
        i += n
    seq[:] = out


def rows(seq):
    result, i = [], 0
    while i < len(seq):
        t = seq[i]["t"]
        n = 1 if t == "w" else block_length(seq, i)
        kind = {"kd": KEY_PRESS if n == 2 else KEY_DOWN, "ku": KEY_UP, "md": CLICK if n == 2 else MOUSE_DOWN,
                "mu": MOUSE_UP, "mv": MOVE, "tx": TYPE}.get(t, DELAY)
        result.append(Row(kind, i, i + n - 1))
        i += n
    return result


def hold(seq, r):
    if r.kind in (KEY_PRESS, CLICK):
        return seq[r.last]["d"]
    if r.kind == DELAY:
        return seq[r.first]["d"]
    if r.kind == MOVE:
        return sum(seq[i]["d"] for i in range(r.first + 1, r.last + 1))
    return 0


def move_total(seq, r):
    return (sum(seq[i].get("x") or 0 for i in range(r.first, r.last + 1)),
            sum(seq[i].get("y") or 0 for i in range(r.first, r.last + 1)))


def key_text(seq, r):
    st = seq[r.first]
    if r.is_key:
        return core.key_label(st["k"])
    if r.is_mouse:
        return "Right" if st.get("b") == "right" else "Left"
    if r.kind == MOVE:
        return "%+d, %+d" % move_total(seq, r)
    if r.kind == TYPE:
        return st.get("text", "")
    return ""


def ms_text(ms):
    return ("%g s" % round(ms / 1000.0, 2)) if ms >= 10000 else "%d ms" % ms


def duration_text(seq, r):
    if r.kind in (KEY_PRESS, CLICK, DELAY) or (r.kind == MOVE and r.count > 1):
        return ms_text(hold(seq, r))
    return DASH


# ----- editing a row

def set_hold(seq, r, ms):
    if r.kind in (KEY_PRESS, CLICK):
        seq[r.last]["d"] = ms
    elif r.kind == DELAY:
        seq[r.first]["d"] = ms
    elif r.kind == MOVE and r.count > 1:
        # Spread the new length over the movement, keeping its rhythm.
        n, old, done = r.count - 1, hold(seq, r), 0
        for i in range(1, n + 1):
            d = ms - done if i == n else round(seq[r.first + i]["d"] * ms / old) if old else ms // n
            d = max(0, min(d, ms - done))
            seq[r.first + i]["d"] = d
            done += d


def _partner(seq, start, kind, match, step, apply):
    j = start + step
    while 0 <= j < len(seq):
        if seq[j]["t"] == kind and match(seq[j]):
            apply(seq[j])
            return
        j += step


def set_key(seq, r, key):
    old = seq[r.first]["k"]
    for i in range(r.first, r.last + 1):
        seq[i]["k"] = key
    if r.kind == KEY_DOWN:
        _partner(seq, r.first, "ku", lambda s: s.get("k") == old, 1, lambda s: s.__setitem__("k", key))
    elif r.kind == KEY_UP:
        _partner(seq, r.first, "kd", lambda s: s.get("k") == old, -1, lambda s: s.__setitem__("k", key))


def set_button(seq, r, right):
    old, b = seq[r.first].get("b", "left"), "right" if right else "left"
    for i in range(r.first, r.last + 1):
        seq[i]["b"] = b
    if r.kind == MOUSE_DOWN:
        _partner(seq, r.first, "mu", lambda s: s.get("b", "left") == old, 1, lambda s: s.__setitem__("b", b))
    elif r.kind == MOUSE_UP:
        _partner(seq, r.first, "md", lambda s: s.get("b", "left") == old, -1, lambda s: s.__setitem__("b", b))


def set_position(seq, r, x, y):
    for i in range(r.first, r.last + 1):
        seq[i]["x"], seq[i]["y"] = x, y


def set_move(seq, r, dx, dy):
    """Sets a camera movement's total distance, spread over its steps in the same proportions."""
    ox, oy = move_total(seq, r)
    done_x = done_y = 0
    for i in range(r.first, r.last + 1):
        last = i == r.last
        x = dx - done_x if last else round(seq[i]["x"] * dx / ox) if ox else 0
        y = dy - done_y if last else round(seq[i]["y"] * dy / oy) if oy else 0
        seq[i]["x"], seq[i]["y"] = x, y
        done_x += x
        done_y += y


def make(kind, key="e", right=False, x=None, y=None, hold_ms=50):
    b = "right" if right else "left"
    if kind == KEY_PRESS:
        return [{"t": "kd", "k": key, "d": 0}, {"t": "ku", "k": key, "d": hold_ms}]
    if kind == KEY_DOWN:
        return [{"t": "kd", "k": key, "d": 0}]
    if kind == KEY_UP:
        return [{"t": "ku", "k": key, "d": 0}]
    if kind == CLICK:
        return [{"t": "md", "b": b, "x": x, "y": y, "d": 0}, {"t": "mu", "b": b, "x": x, "y": y, "d": hold_ms}]
    if kind == MOUSE_DOWN:
        return [{"t": "md", "b": b, "x": x, "y": y, "d": 0}]
    if kind == MOUSE_UP:
        return [{"t": "mu", "b": b, "x": x, "y": y, "d": 0}]
    if kind == MOVE:
        return [{"t": "mv", "x": 10, "y": 0, "d": 0}]
    if kind == TYPE:
        return [{"t": "tx", "text": "hello", "d": 0}]
    return [{"t": "w", "d": hold_ms}]


def change_type(seq, r, kind):
    """Turns a row into another kind of action, keeping its key, button, position and length where they fit."""
    if r.kind == kind:
        return
    st = seq[r.first]
    key = st["k"] if r.is_key else "e"
    right = r.is_mouse and st.get("b") == "right"
    x, y = (st.get("x"), st.get("y")) if r.is_mouse else (None, None)
    h = hold(seq, r) or (250 if kind == DELAY else 40 if kind == CLICK else 50)
    seq[r.first:r.last + 1] = make(kind, key, right, x, y, h)


def duplicate(seq, r):
    seq[r.last + 1:r.last + 1] = copy.deepcopy(seq[r.first:r.last + 1])


def delete(seq, rs, a, b):
    del seq[rs[a].first:rs[b].last + 1]


def crop(seq, rs, a, b):
    seq[:] = seq[rs[a].first:rs[b].last + 1]


def move_rows(seq, rs, a, b, to):
    """Moves rows a..b so they sit before row `to` (len(rs) = the end)."""
    if a <= to <= b + 1:
        return
    first, n = rs[a].first, rs[b].last - rs[a].first + 1
    at = rs[to].first if to < len(rs) else len(seq)
    block = seq[first:first + n]
    del seq[first:first + n]
    if at > first:
        at -= n
    seq[at:at] = block


def delay_after(seq, rs, i):
    return seq[rs[i + 1].first]["d"] if i + 1 < len(rs) and rs[i + 1].kind == DELAY else 0


def set_delay_after(seq, rs, i, ms):
    if i + 1 < len(rs) and rs[i + 1].kind == DELAY:
        if ms > 0:
            seq[rs[i + 1].first]["d"] = ms
        else:
            del seq[rs[i + 1].first]
    elif ms > 0:
        seq.insert(rs[i].last + 1, {"t": "w", "d": ms})


def replace_key(seq, frm, to, remove):
    """Changes (or removes) every press of one key. Removed steps hand their wait to the next step."""
    n, i = 0, len(seq) - 1
    while i >= 0:
        st = seq[i]
        if st["t"] in ("kd", "ku") and st.get("k") == frm:
            n += 1
            if remove:
                if i + 1 < len(seq):
                    seq[i + 1]["d"] += st["d"]
                del seq[i]
            else:
                st["k"] = to
        i -= 1
    return n


# ---------------------------------------------------------------- step table

def _blend(a, b, t):
    a, b = a.lstrip("#"), b.lstrip("#")
    ca = [int(a[i:i + 2], 16) for i in (0, 2, 4)]
    cb = [int(b[i:i + 2], 16) for i in (0, 2, 4)]
    return "#" + "".join("%02X" % int(x + (y - x) * t) for x, y in zip(ca, cb))


class StepTable(QWidget):
    """Step / Action / Keys / Duration, with drag handles to reorder and a menu button per row."""
    selection_changed = Signal()
    rows_moved = Signal(int, int, int)     # first, last, before
    menu_requested = Signal(int, QPoint)   # row, global point
    delete_requested = Signal()

    HEAD, ROW = 34, 42

    def __init__(self, theme_getter):
        super().__init__()
        self.theme = theme_getter
        self.items = []          # (icon, action, keys, duration, chip)
        self.empty_text = ""
        self.anchor = self.caret = -1
        self.scroll = 0
        self.hover = -1
        self.drag_from = self.drop_at = -1
        self.setMouseTracking(True)
        self.setFocusPolicy(Qt.FocusPolicy.StrongFocus)
        self.setMinimumHeight(160)

    # ----- selection & rows

    @property
    def sel_start(self):
        return min(self.anchor, self.caret)

    @property
    def sel_end(self):
        return max(self.anchor, self.caret)

    def select_rows(self, a, b):
        a, b = min(a, len(self.items) - 1), min(b, len(self.items) - 1)
        self.anchor, self.caret = (a, b) if a >= 0 and b >= 0 else (-1, -1)
        self.ensure_visible(self.caret)
        self.update()
        self.selection_changed.emit()

    def set_items(self, items):
        self.items = items
        self.anchor, self.caret = min(self.anchor, len(items) - 1), min(self.caret, len(items) - 1)
        self.scroll = max(0, min(self.scroll, self.max_scroll()))
        self.update()

    def visible_rows(self):
        return max(1, (self.height() - self.HEAD) // self.ROW)

    def max_scroll(self):
        return max(0, len(self.items) - self.visible_rows())

    def row_at(self, y):
        return -1 if y < self.HEAD else self.scroll + int((y - self.HEAD) // self.ROW)

    def row_y(self, r):
        return self.HEAD + (r - self.scroll) * self.ROW

    def ensure_visible(self, r):
        if r < 0:
            return
        if r < self.scroll:
            self.scroll = r
        elif r >= self.scroll + self.visible_rows():
            self.scroll = r - self.visible_rows() + 1
        self.scroll = max(0, min(self.scroll, self.max_scroll()))

    def columns(self):
        w = self.width()
        num, menu = 30, w - 12 - (8 if self.max_scroll() else 0) - 22
        rest = menu - 80
        action = num + 40
        keys = action + int(rest * 0.40) + 10
        dur = keys + int(rest * 0.30)
        return 10, num, action, keys, dur, menu

    # ----- input

    def keyPressEvent(self, ev):
        if not self.items:
            return super().keyPressEvent(ev)
        k, c = ev.key(), max(self.caret, 0)
        if k in (Qt.Key.Key_Delete, Qt.Key.Key_Backspace):
            self.delete_requested.emit()
            return
        moves = {Qt.Key.Key_Up: c - 1, Qt.Key.Key_Down: c + 1, Qt.Key.Key_Home: 0, Qt.Key.Key_End: len(self.items) - 1}
        if k not in moves:
            return super().keyPressEvent(ev)
        c = max(0, min(len(self.items) - 1, moves[k]))
        mods = ev.modifiers()
        if mods & Qt.KeyboardModifier.AltModifier and self.caret >= 0:
            a, b = self.sel_start, self.sel_end  # Alt+Up/Down moves the selected rows
            if k == Qt.Key.Key_Up and a > 0:
                self.rows_moved.emit(a, b, a - 1)
            elif k == Qt.Key.Key_Down and b < len(self.items) - 1:
                self.rows_moved.emit(a, b, b + 2)
        else:
            self.select_rows(self.anchor if mods & Qt.KeyboardModifier.ShiftModifier and self.anchor >= 0 else c, c)

    def mousePressEvent(self, ev):
        self.setFocus()
        pos = ev.position()
        r = self.row_at(pos.y())
        if r < 0 or r >= len(self.items):
            return
        _, num, _, _, _, menu = self.columns()
        inside = self.sel_start <= r <= self.sel_end and self.caret >= 0
        if ev.button() == Qt.MouseButton.RightButton or pos.x() >= menu:
            if not inside:
                self.select_rows(r, r)
            at = QPoint(int(pos.x()), int(self.row_y(r) + self.ROW)) if pos.x() >= menu else pos.toPoint()
            self.menu_requested.emit(r, self.mapToGlobal(at))
            return
        if ev.modifiers() & Qt.KeyboardModifier.ShiftModifier and self.anchor >= 0:
            self.select_rows(self.anchor, r)
        elif not inside or pos.x() >= num + 4:
            self.select_rows(r, r)
        self.drag_from = r

    def mouseMoveEvent(self, ev):
        pos = ev.position()
        if self.drag_from >= 0 and ev.buttons() & Qt.MouseButton.LeftButton:
            at = max(0, min(len(self.items), self.scroll + int((pos.y() - self.HEAD + self.ROW / 2) // self.ROW)))
            if pos.y() < self.HEAD + 6 and self.scroll > 0:
                self.scroll -= 1
            if pos.y() > self.height() - 6 and self.scroll < self.max_scroll():
                self.scroll += 1
            if at != self.drop_at:
                self.drop_at = at
                self.update()
            self.setCursor(Qt.CursorShape.SizeVerCursor)
            return
        r = self.row_at(pos.y())
        _, num, *_ = self.columns()
        self.setCursor(Qt.CursorShape.SizeVerCursor if 0 <= r < len(self.items) and pos.x() < num + 4 else Qt.CursorShape.ArrowCursor)
        if r != self.hover:
            self.hover = r
            self.update()

    def mouseReleaseEvent(self, ev):
        if self.drag_from >= 0 and self.drop_at >= 0:
            a, b = self.sel_start, self.sel_end
            if not a <= self.drag_from <= b:
                a = b = self.drag_from
            if not a <= self.drop_at <= b + 1:
                self.rows_moved.emit(a, b, self.drop_at)
        self.drag_from = self.drop_at = -1
        self.setCursor(Qt.CursorShape.ArrowCursor)
        self.update()

    def leaveEvent(self, ev):
        self.hover = -1
        self.update()

    def wheelEvent(self, ev):
        delta = ev.angleDelta().y()
        self.scroll = max(0, min(self.max_scroll(), self.scroll - (3 if delta > 0 else -3)))
        self.update()

    # ----- drawing

    def paintEvent(self, _):
        t = self.theme()
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        handle, num, action, keys, dur, menu = self.columns()
        small = QFont(self.font())
        small.setPixelSize(12)
        p.setFont(small)
        p.setPen(QColor(t.sub))
        mid = Qt.AlignmentFlag.AlignVCenter
        for x, w, text in ((num - 4, 44, "Step"), (action, keys - action, "Action"), (keys, dur - keys, "Keys"), (dur, menu - dur, "Duration")):
            p.drawText(QRectF(x, 0, w, self.HEAD), mid, text)
        p.setPen(QColor(t.border))
        p.drawLine(0, self.HEAD - 1, self.width(), self.HEAD - 1)
        if not self.items:
            p.setPen(QColor(t.sub))
            p.drawText(QRectF(16, self.HEAD + 8, self.width() - 32, self.height() - self.HEAD - 16),
                       Qt.AlignmentFlag.AlignHCenter | Qt.AlignmentFlag.AlignTop | Qt.TextFlag.TextWordWrap, self.empty_text)
            return
        normal = QFont(self.font())
        normal.setPixelSize(13)
        icon_font = QFont(self.font())
        icon_font.setPixelSize(15)
        p.setClipRect(QRectF(0, self.HEAD, self.width(), self.height() - self.HEAD))
        right_pad = 10 if self.max_scroll() else 0
        for r in range(self.scroll, len(self.items)):
            y = self.row_y(r)
            if y > self.height():
                break
            icon, act, ktext, dtext, chip = self.items[r]
            sel = self.caret >= 0 and self.sel_start <= r <= self.sel_end
            rect = QRectF(2, y + 3, self.width() - 4 - right_pad, self.ROW - 6)
            if sel:
                p.setPen(QPen(QColor(t.accent), 1.4))
                p.setBrush(QColor(_blend(t.input, t.accent, 0.12)))
                p.drawRoundedRect(rect, 8, 8)
            elif r == self.hover:
                p.setPen(Qt.PenStyle.NoPen)
                p.setBrush(QColor(_blend(t.input, t.text, 0.04)))
                p.drawRoundedRect(rect, 8, 8)
            else:
                p.setPen(QColor(_blend(t.border, t.surface, 0.4)))
                p.drawLine(6, y + self.ROW - 1, self.width() - 6, y + self.ROW - 1)
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(QColor(_blend(t.sub, t.surface, 0.3)))
            for i in range(3):  # drag handle dots
                for j in range(2):
                    p.drawEllipse(QRectF(handle + j * 5, y + self.ROW / 2 - 6 + i * 5, 2.6, 2.6))
            p.setFont(normal)
            p.setPen(QColor(t.text))
            p.drawText(QRectF(num, y, 36, self.ROW), mid, "%02d" % (r + 1))
            p.setFont(icon_font)
            p.setPen(QColor(t.sub))
            p.drawText(QRectF(action, y, 26, self.ROW), mid | Qt.AlignmentFlag.AlignHCenter, icon)
            p.setFont(normal)
            p.setPen(QColor(t.text))
            p.drawText(QRectF(action + 30, y, keys - action - 34, self.ROW), mid,
                       p.fontMetrics().elidedText(act, Qt.TextElideMode.ElideRight, keys - action - 34))
            if ktext:
                p.setFont(small)
                avail = dur - keys - 8
                if chip:
                    w = max(30, min(avail, p.fontMetrics().horizontalAdvance(ktext) + 16))
                    cr = QRectF(keys, y + (self.ROW - 28) / 2, w, 28)
                    p.setPen(QPen(QColor(_blend(t.border, t.text, 0.12)), 1))
                    p.setBrush(QColor(_blend(t.input, t.text, 0.07)))
                    p.drawRoundedRect(cr, 5, 5)
                    p.setPen(QColor(t.text))
                    p.drawText(cr, Qt.AlignmentFlag.AlignCenter, p.fontMetrics().elidedText(ktext, Qt.TextElideMode.ElideRight, int(w - 8)))
                else:
                    p.setPen(QColor(t.sub))
                    p.drawText(QRectF(keys, y, avail, self.ROW), mid, p.fontMetrics().elidedText(ktext, Qt.TextElideMode.ElideRight, avail))
            p.setFont(normal)
            p.setPen(QColor(t.sub if dtext == DASH else t.text))
            p.drawText(QRectF(dur, y, menu - dur, self.ROW), mid, dtext)
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(QColor(t.text if (r == self.hover or sel) else t.sub))
            for i in range(3):  # menu button: three dots
                p.drawEllipse(QRectF(menu + 9, y + self.ROW / 2 - 7 + i * 6, 3, 3))
        if self.drop_at >= 0:
            p.setPen(QPen(QColor(t.accent), 2))
            y = self.row_y(self.drop_at)
            p.drawLine(4, y, self.width() - 14, y)
        p.setClipping(False)
        if self.max_scroll():
            track = self.height() - self.HEAD - 8
            h = max(24, track * self.visible_rows() / len(self.items))
            ty = self.HEAD + 4 + (track - h) * self.scroll / self.max_scroll()
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(QColor(_blend(t.border, t.text, 0.15)))
            p.drawRoundedRect(QRectF(self.width() - 10, ty, 5, h), 2.5, 2.5)


# ---------------------------------------------------------------- keyboard preview

def _layout():
    caps = []

    def add(row, x, w, label, keys):
        caps.append((row, x, w, label, keys))
    add(0, 0, 1, "Esc", ("esc",))
    for i in range(12):
        add(0, 2 + i + (i // 4) * 0.5, 1, "F%d" % (i + 1), ("f%d" % (i + 1),))
    for i, ch in enumerate("`1234567890-="):
        add(1, i, 1, ch, (ch,))
    add(1, 13, 2, "Backspace", ("backspace",))
    add(2, 0, 1.5, "Tab", ("tab",))
    for i, ch in enumerate("QWERTYUIOP"):
        add(2, 1.5 + i, 1, ch, (ch.lower(),))
    add(2, 11.5, 1, "[", ("[",))
    add(2, 12.5, 1, "]", ("]",))
    add(2, 13.5, 1.5, "\\", ("\\",))
    add(3, 0, 1.75, "Caps", ("caps_lock",))
    for i, ch in enumerate("ASDFGHJKL"):
        add(3, 1.75 + i, 1, ch, (ch.lower(),))
    add(3, 10.75, 1, ";", (";",))
    add(3, 11.75, 1, "'", ("'",))
    add(3, 12.75, 2.25, "Enter", ("enter",))
    add(4, 0, 2.25, "Shift", ("shift", "shift_l"))
    for i, ch in enumerate("ZXCVBNM"):
        add(4, 2.25 + i, 1, ch, (ch.lower(),))
    add(4, 9.25, 1, ",", (",",))
    add(4, 10.25, 1, ".", (".",))
    add(4, 11.25, 1, "/", ("/",))
    add(4, 12.25, 2.75, "Shift", ("shift_r",))
    add(5, 0, 1.25, "Ctrl", ("ctrl", "ctrl_l"))
    add(5, 1.25, 1.25, "Cmd" if core.IS_MAC else "Super", ("cmd", "cmd_l"))
    add(5, 2.5, 1.25, "Alt", ("alt", "alt_l"))
    add(5, 3.75, 6.25, "", ("space",))
    add(5, 10, 1.25, "Alt", ("alt_r", "alt_gr"))
    add(5, 11.25, 1.25, "Cmd" if core.IS_MAC else "Super", ("cmd_r",))
    add(5, 12.5, 1.25, "Menu", ("menu",))
    add(5, 13.75, 1.25, "Ctrl", ("ctrl_r",))
    return caps


class KeyboardPreview(QWidget):
    """A small keyboard that lights up the key of the selected step."""
    CAPS = _layout()

    def __init__(self, theme_getter):
        super().__init__()
        self.theme = theme_getter
        self.lit = set()
        self.setMinimumHeight(80)

    def paintEvent(self, _):
        t = self.theme()
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        unit = (self.width() - 1) / 15.0
        gap = max(2.0, unit * 0.12)
        row_h = (self.height() - 1 - gap * 5) / 6.0
        f = QFont(self.font())
        f.setPixelSize(max(7, min(12, int(row_h * 0.55))))
        small = QFont(f)
        small.setPixelSize(max(6, f.pixelSize() - 2))
        for row, x, w, label, keys in self.CAPS:
            lit = any(k in self.lit for k in keys)
            r = QRectF(x * unit, row * (row_h + gap), w * unit - gap, row_h)
            p.setPen(QPen(QColor(t.accent if lit else _blend(t.border, t.text, 0.08)), 1))
            p.setBrush(QColor(t.accent if lit else _blend(t.input, t.text, 0.05)))
            p.drawRoundedRect(r, max(2.0, row_h * 0.18), max(2.0, row_h * 0.18))
            p.setFont(small if len(label) > 3 else f)
            p.setPen(QColor(t.on_accent if lit else t.sub))
            p.drawText(r, Qt.AlignmentFlag.AlignCenter, label)
