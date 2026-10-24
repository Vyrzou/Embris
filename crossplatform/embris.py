#!/usr/bin/env python3
"""Embris for macOS / Linux - key & click macro with the same design as the Windows app.

Qt (PySide6) for the window, pynput (via embris_core) for global input.
"""
import json
import math
import os
import sys
import threading
import time

from PySide6.QtCore import QEvent, QObject, QPoint, QPointF, QRect, QRectF, QSize, Qt, QTimer, QUrl, Signal
from PySide6.QtNetwork import QLocalServer, QLocalSocket, QNetworkAccessManager, QNetworkReply, QNetworkRequest
from PySide6.QtGui import QColor, QDesktopServices, QFont, QFontDatabase, QIcon, QLinearGradient, QKeySequence, QPainter, QPainterPath, QPen, QPixmap, QRadialGradient, QShortcut
from PySide6.QtWidgets import (QAbstractButton, QApplication, QButtonGroup, QColorDialog, QComboBox, QDialog,
                               QDoubleSpinBox, QFrame, QGridLayout, QHBoxLayout, QLabel, QLineEdit,
                               QAbstractItemView, QFileDialog, QListWidget, QListWidgetItem, QMainWindow, QMenu, QMessageBox, QPlainTextEdit, QPushButton, QSizePolicy, QSlider,
                               QStackedWidget, QSystemTrayIcon, QVBoxLayout, QWidget)

import backgrounds as bgs
import editors as ed
import overlay as ov
import emb_script
from editors import KeyboardPreview, StepTable
from timeline import TimelineEditor
import embris_core as core
from embris_core import IS_MAC, VERSION, key_label

APP_DIR = os.path.dirname(os.path.abspath(__file__))
DANGER, SUCCESS, WARNING = "#E5484D", "#30C48D", "#F5A623"
SNAP_CHOICES = (0, 1, 5, 10, 50, 100)
TL_KINDS = ("key", "left", "right", "move", "text")  # timeline action types, in the Action box's order


def clock_text(ms):
    """00:01.700 (with hours when needed)."""
    s, ms = divmod(int(ms), 1000)
    m, s = divmod(s, 60)
    h, m = divmod(m, 60)
    return ("%d:" % h if h else "") + "%02d:%02d.%03d" % (m, s, ms)


def asset(name):
    for path in (os.path.join(APP_DIR, name), os.path.join(APP_DIR, "assets", name)):
        if os.path.exists(path):
            return path
    return None


# ---------------------------------------------------------------- theme

def blend(a, b, t):
    a, b = a.lstrip("#"), b.lstrip("#")
    ca = [int(a[i:i + 2], 16) for i in (0, 2, 4)]
    cb = [int(b[i:i + 2], 16) for i in (0, 2, 4)]
    return "#" + "".join("%02X" % int(x + (y - x) * t) for x, y in zip(ca, cb))


def luma(c):
    c = c.lstrip("#")
    r, g, b = (int(c[i:i + 2], 16) for i in (0, 2, 4))
    return (0.299 * r + 0.587 * g + 0.114 * b) / 255


class Theme:
    FIELDS = ("bg", "surface", "accent", "text", "border")

    def __init__(self, name, bg, surface, accent, text, border):
        self.name, self.bg, self.surface, self.accent, self.text, self.border = name, bg, surface, accent, text, border

    def copy(self, name=None):
        return Theme(name or self.name, *self.colors())

    def colors(self):
        return [getattr(self, f) for f in self.FIELDS]

    @property
    def light(self):
        return luma(self.bg) > 0.5

    @property
    def sidebar(self):
        return blend(self.bg, self.surface, 0.5)

    @property
    def input(self):
        return blend(self.surface, self.bg, 0.55) if self.light else blend(self.bg, self.surface, 0.3)

    @property
    def sub(self):
        return blend(self.text, self.surface, 0.4)

    @property
    def on_accent(self):
        return "#141619" if luma(self.accent) > 0.62 else "#FFFFFF"


PRESETS = [
    Theme("Ember", "#151619", "#202226", "#E89545", "#ECEDEF", "#36383D"),
    Theme("Ocean", "#0A1A24", "#11293A", "#1EC8E8", "#EEF8FB", "#24495F"),
    Theme("Forest", "#0F1712", "#18251C", "#2FBF71", "#EEF6F0", "#2F4A38"),
    Theme("Sunset", "#1A1114", "#271A1F", "#FF7A45", "#FBF1EE", "#4A2F36"),
    Theme("Lavender", "#15121F", "#211C30", "#A970FF", "#F4F0FB", "#3D3456"),
    Theme("Light", "#F3F5F8", "#FFFFFF", "#1677FF", "#1B2430", "#D5DBE3"),
]
T = PRESETS[0].copy()  # current theme
BG = {"active": False, "dim": 55}  # custom background: pages, sidebar and cards become see-through tints


def rgba(color, alpha):
    c = color.lstrip("#")
    return "rgba(%d, %d, %d, %d)" % (int(c[0:2], 16), int(c[2:4], 16), int(c[4:6], 16), alpha)


def chevron_file(color):
    """Combo box arrows in Qt style sheets must be image files, so draw one."""
    path = os.path.join(core.config_dir(), "chevron.png")
    pm = QPixmap(20, 20)
    pm.fill(Qt.GlobalColor.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    p.setPen(QPen(QColor(color), 2, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap, Qt.PenJoinStyle.RoundJoin))
    arrow = QPainterPath()
    arrow.moveTo(5, 8)
    arrow.lineTo(10, 13)
    arrow.lineTo(15, 8)
    p.drawPath(arrow)
    p.end()
    pm.save(path)
    return path.replace("\\", "/")


def stylesheet():
    t = T
    see, dim = BG["active"], BG["dim"]
    page = rgba(t.bg, dim * 255 // 100) if see else t.bg
    side = rgba(t.sidebar, max(dim, 72) * 255 // 100) if see else t.sidebar
    card = rgba(t.surface, 210) if see else t.surface
    hover = blend(t.input, t.text, 0.07)
    chip_on = blend(t.input, t.accent, 0.28)
    dim = blend(t.text, t.surface, 0.6)
    fs = 13
    return f"""
    QWidget {{ color: {t.text}; font-size: {fs}px; }}
    QMainWindow {{ background: {t.bg}; }}
    QStackedWidget {{ background: {"transparent" if see else t.bg}; }}
    QWidget#page {{ background: {page}; }}
    QFrame#sidebar {{ background: {side}; border-right: 1px solid {t.border}; }}
    QFrame#bar {{ background: {side}; border-top: 1px solid {t.border}; }}
    QFrame#card {{ background: {card}; border: 1px solid {t.border}; border-radius: 10px; }}
    QLabel {{ background: transparent; border: none; }}
    QLabel:disabled {{ color: {dim}; }}
    QLabel#title {{ font-size: 26px; font-weight: 600; }}
    QLabel#appname {{ font-size: 17px; font-weight: 600; }}
    QLabel#cardTitle {{ font-size: 15px; font-weight: 600; }}
    QLabel#strong {{ font-size: 14px; font-weight: 600; }}
    QLabel#sub {{ color: {t.sub}; font-size: 12px; }}
    QLabel#faint {{ color: {blend(t.sub, t.sidebar, 0.4)}; font-size: 11px; }}
    QPushButton {{ background: {t.input}; border: 1px solid {t.border}; border-radius: 8px; padding: 8px 14px; color: {t.text}; }}
    QPushButton:hover {{ background: {hover}; }}
    QPushButton:disabled {{ color: {dim}; }}
    QPushButton[kind="primary"] {{ background: {t.accent}; border-color: {t.accent}; color: {t.on_accent}; font-weight: 600; }}
    QPushButton[kind="primary"]:hover {{ background: {blend(t.accent, '#FFFFFF', 0.1)}; }}
    QPushButton[kind="primary"]:disabled {{ background: {blend(t.accent, t.bg, 0.5)}; }}
    QPushButton[kind="danger"] {{ background: {DANGER}; border-color: {DANGER}; color: #FFFFFF; font-weight: 600; }}
    QPushButton[kind="big"] {{ font-size: 15px; font-weight: 600; padding: 12px 22px; }}
    QPushButton[kind="chip"] {{ color: {t.sub}; padding: 6px 12px; }}
    QPushButton[kind="chip"]:checked {{ background: {chip_on}; border-color: {t.accent}; color: {t.text}; }}
    QPushButton#nav {{ background: transparent; border: none; border-radius: 8px; text-align: left;
                       padding: 11px 16px; color: {t.sub}; font-size: 14px; }}
    QPushButton#nav:hover {{ background: {blend(t.sidebar, t.text, 0.04)}; }}
    QPushButton#nav:checked {{ background: {blend(t.sidebar, t.text, 0.08)}; color: {t.text}; font-weight: 600;
                               border-left: 3px solid {t.accent}; border-top-left-radius: 2px; border-bottom-left-radius: 2px; }}
    QPushButton#pin {{ padding: 4px; }}
    QPushButton#pin:checked {{ background: {chip_on}; border-color: {t.accent}; }}
    QLineEdit, QDoubleSpinBox, QComboBox, QPlainTextEdit, QListWidget {{
        background: {t.input}; border: 1px solid {t.border}; border-radius: 6px; padding: 6px 8px;
        selection-background-color: {t.accent}; selection-color: {t.on_accent}; }}
    QDoubleSpinBox {{ padding: 0px 8px; }}
    QAbstractSpinBox::up-button, QAbstractSpinBox::down-button {{ width: 0px; border: none; }}  /* NoButtons + style sheets hides the text (Qt bug) */
    QLineEdit:focus, QDoubleSpinBox:focus, QComboBox:focus, QListWidget:focus {{ border-color: {t.accent}; }}
    QLineEdit:disabled, QDoubleSpinBox:disabled, QComboBox:disabled {{ color: {dim}; }}
    QComboBox::drop-down {{ border: none; width: 28px; }}
    QComboBox::down-arrow {{ image: url({chevron_file(t.sub)}); width: 14px; height: 14px; }}
    QComboBox QAbstractItemView {{ background: {t.surface}; border: 1px solid {t.border}; padding: 4px; outline: none;
                                   selection-background-color: {chip_on}; selection-color: {t.text}; }}
    QListWidget {{ padding: 4px; outline: none; }}
    QListWidget::item {{ padding: 9px 8px; border-radius: 6px; }}
    QListWidget::item:hover {{ background: {blend(t.input, t.text, 0.05)}; }}
    QListWidget::item:selected {{ background: {chip_on}; color: {t.text}; }}
    QPlainTextEdit {{ color: {t.sub}; }}
    QScrollBar:vertical {{ background: transparent; width: 8px; margin: 2px; }}
    QScrollBar::handle:vertical {{ background: {t.border}; border-radius: 3px; min-height: 24px; }}
    QScrollBar::add-line, QScrollBar::sub-line, QScrollBar::add-page, QScrollBar::sub-page {{ height: 0; background: none; }}
    QToolTip {{ background: {t.surface}; color: {t.text}; border: 1px solid {t.border}; padding: 4px; }}
    QMenu {{ background: {t.surface}; border: 1px solid {t.border}; padding: 4px; }}
    QMenu::item {{ padding: 8px 22px; border-radius: 5px; }}
    QMenu::item:selected {{ background: {chip_on}; }}
    QMessageBox, QColorDialog {{ background: {t.surface}; }}
    """


# ---------------------------------------------------------------- widgets

def set_kind(button, kind):
    button.setProperty("kind", kind)
    button.style().unpolish(button)
    button.style().polish(button)


def push(text, kind=None, slot=None, checkable=False):
    b = QPushButton(text)
    b.setCursor(Qt.CursorShape.PointingHandCursor)
    if kind:
        b.setProperty("kind", kind)
    b.setCheckable(checkable)
    if slot:
        b.clicked.connect(slot)
    return b


def label(text, name=None, wrap=False):
    l = QLabel(text)
    if name:
        l.setObjectName(name)
    l.setWordWrap(wrap)
    return l


def spin(value, low, high, decimals=0, step=1.0):
    s = QDoubleSpinBox()
    s.setRange(low, high)
    s.setDecimals(decimals)
    s.setSingleStep(step)
    s.setValue(float(value))
    s.setMinimumHeight(34)
    return s


class Toggle(QAbstractButton):
    """Pill switch with its label."""

    def __init__(self, text, checked=False):
        super().__init__()
        self.setText(text)
        self.setCheckable(True)
        self.setChecked(bool(checked))
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setMinimumHeight(28)

    def sizeHint(self):
        return QSize(52 + self.fontMetrics().horizontalAdvance(self.text()), 28)

    def paintEvent(self, _):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        on, back = self.isChecked(), T.surface
        dim = (lambda c: c) if self.isEnabled() else (lambda c: blend(c, back, 0.55))
        y = (self.height() - 22) / 2
        p.setPen(QPen(QColor(dim(T.accent if on else T.border)), 1))
        p.setBrush(QColor(dim(T.accent if on else T.input)))
        p.drawRoundedRect(QRectF(0.5, y + 0.5, 40, 21), 10.5, 10.5)
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(QColor(dim(T.on_accent if on else T.sub)))
        p.drawEllipse(QRectF(23 if on else 5, y + 5, 12, 12))
        p.setPen(QColor(dim(T.text)))
        p.drawText(QRect(52, 0, self.width() - 52, self.height()), Qt.AlignmentFlag.AlignVCenter | Qt.AlignmentFlag.AlignLeft, self.text())


QT_KEYS = {
    Qt.Key.Key_Space: "space", Qt.Key.Key_Return: "enter", Qt.Key.Key_Enter: "enter", Qt.Key.Key_Escape: "esc",
    Qt.Key.Key_Backspace: "backspace", Qt.Key.Key_Tab: "tab", Qt.Key.Key_Up: "up", Qt.Key.Key_Down: "down",
    Qt.Key.Key_Left: "left", Qt.Key.Key_Right: "right", Qt.Key.Key_Home: "home", Qt.Key.Key_End: "end",
    Qt.Key.Key_PageUp: "page_up", Qt.Key.Key_PageDown: "page_down", Qt.Key.Key_Delete: "delete",
    Qt.Key.Key_Insert: "insert", Qt.Key.Key_CapsLock: "caps_lock", Qt.Key.Key_Shift: "shift",
    Qt.Key.Key_Alt: "alt",
    # On macOS Qt swaps these: Key_Control is Command and Key_Meta is Control.
    Qt.Key.Key_Control: "cmd" if IS_MAC else "ctrl", Qt.Key.Key_Meta: "ctrl" if IS_MAC else "cmd",
}
QT_KEYS = {int(k.value) if hasattr(k, "value") else int(k): v for k, v in QT_KEYS.items()}
F1 = int(Qt.Key.Key_F1.value) if hasattr(Qt.Key.Key_F1, "value") else int(Qt.Key.Key_F1)


def key_from_qt(ev):
    k = ev.key()
    k = int(k.value) if hasattr(k, "value") else int(k)
    if k in QT_KEYS:
        return QT_KEYS[k]
    if F1 <= k < F1 + 24:
        return "f%d" % (k - F1 + 1)
    text = ev.text()
    if text and text.isprintable() and text != " ":
        return text.lower()
    if 0x20 < k < 0x7F:
        return chr(k).lower()
    return None


class KeyBox(QPushButton):
    """Click it, then press a key."""
    changed = Signal(str)

    def __init__(self, key, on_capture):
        super().__init__(key_label(key))
        self.key, self.on_capture, self.capturing = key, on_capture, False
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setMinimumHeight(34)
        self.clicked.connect(self.start)
        f = self.font()
        f.setBold(True)
        self.setFont(f)

    def set_key(self, key):
        self.key = key
        self.setText(key_label(key))

    def start(self):
        self.capturing = True
        self.setText("Press a key...")
        self.setFocus()
        self.on_capture(True)

    def finish(self):
        self.capturing = False
        self.setText(key_label(self.key))
        # Let the key's own release pass before the global hotkey listens again.
        QTimer.singleShot(400, lambda: self.on_capture(False))

    def keyPressEvent(self, ev):
        if not self.capturing:
            return super().keyPressEvent(ev)
        name = key_from_qt(ev)
        if name:
            self.key = name
            self.finish()
            self.clearFocus()
            self.changed.emit(name)

    def focusOutEvent(self, ev):
        if self.capturing:
            self.finish()
        super().focusOutEvent(ev)


class AddTrackBox(KeyBox):
    """Timeline toolbar: click, press a key, and that key gets a track."""
    CAPTION = "＋ Track"

    def __init__(self, on_capture):
        super().__init__(None, on_capture)
        self.setText(self.CAPTION)

    def finish(self):
        super().finish()
        self.setText(self.CAPTION)


class ColorBox(QPushButton):
    changed = Signal(str)

    def __init__(self, title=None):
        super().__init__()
        self.value = "#000000"
        self.title = title
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setMinimumHeight(36)
        self.setStyleSheet("text-align: left; padding-left: 10px;")
        self.setIconSize(QSize(20, 20))
        self.clicked.connect(self.pick)

    def set_value(self, value):
        self.value = value
        pm = QPixmap(40, 40)
        pm.fill(Qt.GlobalColor.transparent)
        p = QPainter(pm)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.setPen(QPen(QColor(T.border), 2))
        p.setBrush(QColor(value))
        p.drawRoundedRect(QRectF(1, 1, 38, 38), 8, 8)
        p.end()
        self.setIcon(QIcon(pm))
        self.setText("  " + value.upper())

    def pick(self):
        dlg = ColorPicker(self.value, self.title, self.window())
        at = self.mapToGlobal(QPoint(0, self.height() + 6))
        screen = self.screen().availableGeometry()
        if at.y() + dlg.height() > screen.bottom():
            at.setY(self.mapToGlobal(QPoint(0, 0)).y() - dlg.height() - 6)
        dlg.move(max(screen.left(), min(at.x(), screen.right() - dlg.width())), max(screen.top(), at.y()))
        if dlg.exec() == QDialog.DialogCode.Accepted:
            self.set_value(dlg.value().upper())
            self.changed.emit(self.value)


class ColorPicker(QDialog):
    """Embris's own color picker: a saturation / brightness square for the chosen hue, a rainbow hue
    bar, a preview with an editable hex code, and Cancel / Apply."""

    def __init__(self, value, title, parent=None):
        super().__init__(parent, Qt.WindowType.Popup | Qt.WindowType.FramelessWindowHint)
        c = QColor(value)
        h, s, v, _ = c.getHsvF()
        self.h, self.s, self.v = max(0.0, h), s, v
        self.setObjectName("picker")
        self.setStyleSheet("QDialog#picker { background: %s; border: 1px solid %s; border-radius: 10px; }" % (T.surface, T.border))
        self.setFixedSize(300, 392)
        lay = QVBoxLayout(self)
        lay.setContentsMargins(20, 14, 20, 16)
        lay.setSpacing(10)
        head = label(title or "Custom color")
        head.setStyleSheet("font-size: 19px; font-weight: 600;")
        lay.addWidget(head)
        self.square = _SvSquare(self)
        lay.addWidget(self.square, 1)
        self.bar = _HueBar(self)
        lay.addWidget(self.bar)
        row = QHBoxLayout()
        self.swatch = _Swatch(self)
        row.addWidget(self.swatch)
        # The code reads as plain bold text; click it to type one.
        self.hex = QLineEdit()
        self.hex.setStyleSheet("QLineEdit { font-size: 18px; font-weight: 600; background: transparent; border: 1px solid transparent; }"
                               "QLineEdit:focus { background: %s; border-color: %s; }" % (T.input, T.accent))
        self.hex.setToolTip("Click to type a color code")
        self.hex.setMinimumHeight(36)
        self.hex.setMaximumWidth(160)
        self.hex.textEdited.connect(self.typed_hex)
        row.addWidget(self.hex)
        row.addStretch()
        lay.addLayout(row)
        btns = QHBoxLayout()
        btns.addWidget(push("Cancel", slot=self.reject), 1)
        btns.addWidget(push("Apply", "primary", self.accept), 1)
        lay.addLayout(btns)
        self.changed(True)

    def value(self):
        return QColor.fromHsvF(min(self.h, 0.9999), self.s, self.v).name()

    def set_hue(self, h):
        self.h = max(0.0, min(0.9999, h))
        self.changed(True)

    def set_sat_val(self, s, v):
        self.s, self.v = max(0.0, min(1.0, s)), max(0.0, min(1.0, v))
        self.changed(True)

    def changed(self, update_hex):
        for w in (self.square, self.bar, self.swatch):
            w.update()
        if update_hex:
            self.hex.setText(self.value().upper())

    def typed_hex(self, text):
        t = text.strip().lstrip("#")
        if len(t) == 3:
            t = "".join(ch * 2 for ch in t)
        if len(t) != 6 or any(ch not in "0123456789abcdefABCDEF" for ch in t):
            return
        h, s, v, _ = QColor("#" + t).getHsvF()
        self.h, self.s, self.v = max(0.0, h), s, v
        self.changed(False)

    def keyPressEvent(self, ev):
        if ev.key() in (Qt.Key.Key_Return, Qt.Key.Key_Enter):
            self.accept()
        else:
            super().keyPressEvent(ev)


class _SvSquare(QWidget):
    def __init__(self, picker):
        super().__init__()
        self.picker = picker
        self.setMinimumHeight(180)
        self.setCursor(Qt.CursorShape.CrossCursor)

    def pick(self, pos):
        self.picker.set_sat_val(pos.x() / max(1, self.width() - 1), 1 - pos.y() / max(1, self.height() - 1))

    def mousePressEvent(self, ev):
        self.pick(ev.position())

    def mouseMoveEvent(self, ev):
        if ev.buttons() & Qt.MouseButton.LeftButton:
            self.pick(ev.position())

    def paintEvent(self, _):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        r = QRectF(0, 0, self.width(), self.height())
        path = QPainterPath()
        path.addRoundedRect(r, 8, 8)
        p.setClipPath(path)
        # White -> hue across, then transparent -> black down.
        across = QLinearGradient(r.topLeft(), r.topRight())
        across.setColorAt(0, QColor("#FFFFFF"))
        across.setColorAt(1, QColor.fromHsvF(min(self.picker.h, 0.9999), 1, 1))
        p.fillRect(r, across)
        down = QLinearGradient(r.topLeft(), r.bottomLeft())
        down.setColorAt(0, QColor(0, 0, 0, 0))
        down.setColorAt(1, QColor(0, 0, 0, 255))
        p.fillRect(r, down)
        p.setClipping(False)
        c = QPointF(self.picker.s * (self.width() - 1), (1 - self.picker.v) * (self.height() - 1))
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.setPen(QPen(QColor(0, 0, 0, 120), 3))
        p.drawEllipse(c, 7, 7)
        p.setPen(QPen(QColor("#FFFFFF"), 2))
        p.drawEllipse(c, 7, 7)


class _HueBar(QWidget):
    PAD = 8

    def __init__(self, picker):
        super().__init__()
        self.picker = picker
        self.setFixedHeight(26)
        self.setCursor(Qt.CursorShape.PointingHandCursor)

    def pick(self, x):
        self.picker.set_hue((x - self.PAD) / max(1, self.width() - 2 * self.PAD))

    def mousePressEvent(self, ev):
        self.pick(ev.position().x())

    def mouseMoveEvent(self, ev):
        if ev.buttons() & Qt.MouseButton.LeftButton:
            self.pick(ev.position().x())

    def paintEvent(self, _):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        r = QRectF(self.PAD - 4, 5, self.width() - 2 * self.PAD + 8, self.height() - 10)
        grad = QLinearGradient(QPointF(self.PAD, 0), QPointF(self.width() - self.PAD, 0))
        for i in range(7):
            grad.setColorAt(i / 6, QColor.fromHsvF(min(i / 6, 0.9999) if i < 6 else 0.0, 1, 1))
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(grad)
        p.drawRoundedRect(r, r.height() / 2, r.height() / 2)
        x = self.PAD + self.picker.h * (self.width() - 2 * self.PAD)
        p.setPen(QPen(QColor("#FFFFFF"), 2))
        p.setBrush(QColor.fromHsvF(min(self.picker.h, 0.9999), 1, 1))
        p.drawRoundedRect(QRectF(x - 5, 1, 10, self.height() - 2), 5, 5)


class _Swatch(QWidget):
    def __init__(self, picker):
        super().__init__()
        self.picker = picker
        self.setFixedSize(38, 38)

    def paintEvent(self, _):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.setPen(QPen(QColor(T.border), 1.5))
        p.setBrush(QColor(self.picker.value()))
        p.drawEllipse(QRectF(1, 1, self.width() - 3, self.height() - 3))


class ThemeTile(QAbstractButton):
    def __init__(self, preset, is_default):
        super().__init__()
        self.preset, self.is_default, self.selected = preset, is_default, False
        self.setFixedSize(98, 72)
        self.setCursor(Qt.CursorShape.PointingHandCursor)

    def paintEvent(self, _):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        pr = self.preset
        border = T.accent if self.selected else (blend(T.border, T.text, 0.25) if self.underMouse() else T.border)
        p.setPen(QPen(QColor(border), 2 if self.selected else 1))
        p.setBrush(QColor(pr.surface))
        p.drawRoundedRect(QRectF(1, 1, self.width() - 2, self.height() - 2), 8, 8)
        if self.is_default:
            badge = QRectF((self.width() - 50) / 2, 5, 50, 16)
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(QColor(pr.accent))
            p.drawRoundedRect(badge, 8, 8)
            f = QFont(self.font())
            f.setPixelSize(10)
            f.setBold(True)
            p.setFont(f)
            p.setPen(QColor(pr.on_accent))
            p.drawText(badge, Qt.AlignmentFlag.AlignCenter, "Default")
        d, gap = 14, 5
        x = (self.width() - 3 * d - 2 * gap) / 2
        p.setPen(Qt.PenStyle.NoPen)
        for i, c in enumerate((blend(pr.accent, pr.surface, 0.5), pr.accent, blend(pr.accent, "#FFFFFF", 0.45))):
            p.setBrush(QColor(c))
            p.drawEllipse(QRectF(x + i * (d + gap), 26, d, d))
        f = QFont(self.font())
        f.setPixelSize(12)
        p.setFont(f)
        p.setPen(QColor(pr.text))
        p.drawText(QRect(0, 44, self.width(), 22), Qt.AlignmentFlag.AlignCenter, pr.name)

    def enterEvent(self, e):
        self.update()
        super().enterEvent(e)

    def leaveEvent(self, e):
        self.update()
        super().leaveEvent(e)


class StatusDot(QWidget):
    """Status light: a lit core that slowly breathes when ready, with sonar ripples while running,
    recording or waiting. One shared timer animates every light."""
    lights = []
    timer = None

    def __init__(self):
        super().__init__()
        self.kind = "ready"
        self.setFixedSize(22, 22)
        StatusDot.lights.append(self)
        if StatusDot.timer is None:
            StatusDot.timer = QTimer()
            StatusDot.timer.setInterval(40)
            StatusDot.timer.timeout.connect(lambda: [d.update() for d in StatusDot.lights if d.isVisible()])
            StatusDot.timer.start()

    def set_kind(self, kind):
        self.kind = kind
        self.update()

    def paintEvent(self, _):
        color = QColor({"running": T.accent, "waiting": WARNING, "problem": DANGER}.get(self.kind, SUCCESS))
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        c = QPointF(self.width() / 2, self.height() / 2)
        core = 9.0
        now = time.monotonic()
        if self.kind == "ready":
            k = 0.5 + 0.5 * math.sin(now * 2 * math.pi / 2.6)  # breathing glow
            r = core / 2 + 2.5 + k * 2.5
            glow = QColor(color)
            glow.setAlpha(int(40 + 45 * k))
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(glow)
            p.drawEllipse(c, r, r)
        else:
            period = 1.8 if self.kind == "waiting" else 1.1
            for i in range(2):  # two ripples spreading out from the light
                t = ((now / period) + i * 0.5) % 1.0
                r = core / 2 + t * (self.width() / 2 - core / 2 - 1)
                ring = QColor(color)
                ring.setAlpha(int(170 * (1 - t)))
                p.setPen(QPen(ring, 1.6))
                p.setBrush(Qt.BrushStyle.NoBrush)
                p.drawEllipse(c, r, r)
            soft = QColor(color)
            soft.setAlpha(60)
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(soft)
            p.drawEllipse(c, core / 2 + 2, core / 2 + 2)
        # The light itself: a lit centre fading to the colour, with a crisp rim.
        grad = QRadialGradient(QPointF(c.x() - core * 0.15, c.y() - core * 0.15), core * 0.6)
        grad.setColorAt(0, QColor(blend(color.name(), "#FFFFFF", 0.55)))
        grad.setColorAt(1, color)
        p.setBrush(grad)
        p.setPen(QPen(QColor(blend(color.name(), "#FFFFFF", 0.25)), 1))
        p.drawEllipse(c, core / 2, core / 2)

def pin_icon(color, filled):
    """Upright pushpin: head, tapered body, wide collar, needle."""
    pm = QPixmap(36, 36)
    pm.fill(Qt.GlobalColor.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    u, cx, top = 1.6, 18.0, 5.0
    path = QPainterPath()
    path.moveTo(cx - 3.5 * u, top)
    path.lineTo(cx + 3.5 * u, top)
    path.lineTo(cx + 3 * u, top + 6 * u)
    path.lineTo(cx + 6 * u, top + 9.5 * u)
    path.lineTo(cx - 6 * u, top + 9.5 * u)
    path.lineTo(cx - 3 * u, top + 6 * u)
    path.closeSubpath()
    pen = QPen(QColor(color), 1.4 * u, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap, Qt.PenJoinStyle.RoundJoin)
    p.setPen(pen)
    p.setBrush(QColor(color) if filled else Qt.BrushStyle.NoBrush)
    p.drawPath(path)
    p.drawLine(int(cx), int(top + 9.5 * u), int(cx), int(top + 16 * u))
    p.end()
    return QIcon(pm)


class Bridge(QObject):
    """Hands callables from pynput / worker threads to the Qt thread."""
    call = Signal(object)


# ---------------------------------------------------------------- main window

class MainWindow(QMainWindow):
    PAGES = ("Macro", "Presets", "Sequence editor", "Timeline editor", "Scripts", "Hotkeys", "Settings", "Themes && Colors", "Auto clicker")
    NAV_ICONS = ("▤", "☰", "☷", "▥", "{ }", "⌨", "⚙", "◐", "◉")
    NAV_ORDER = (0, 8, 1, 2, 3, 4, 5, 6, 7)  # sidebar order (page numbers): the auto clicker sits under Macro

    def __init__(self):
        super().__init__()
        global T
        self.s = core.load_settings()
        self.presets = core.load_presets()
        colors = self.s.get("theme_colors")
        T = Theme(self.s["theme_name"], *colors) if isinstance(colors, list) and len(colors) == 5 else PRESETS[0].copy()

        self.bridge = Bridge()
        self.bridge.call.connect(lambda fn: fn())
        self.watcher = core.InputWatcher(lambda: self.s["hotkey"], lambda: self.bridge.call.emit(self.toggle),
                                         lambda: self.s["record_hotkey"], lambda: self.bridge.call.emit(self.toggle_record),
                                         lambda: self.suite_keys, lambda key: self.bridge.call.emit(lambda: self.on_suite_key(key)),
                                         lambda hs: self.bridge.call.emit(lambda: self.on_hotstrings_toggled(hs)))
        self.suite_keys = set()
        self.suite_preset = -1
        self.seq_rows, self.table_rows, self.editors, self.record_texts = [], [], [], {}
        self.syncing_editor = self.timeline_editing = self.testing = False
        self.timeline_needs_fit = True
        self.last_preset_name = None
        self.player = core.Player(self.watcher)
        self.running = self.playing = self.waiting = self.recording = self.exiting = False
        self.tip_shown = self.skip_save = self.record_append = False
        self.runs = 0
        self.pick = None

        self.timer = QTimer(self)
        self.timer.timeout.connect(self.try_run)
        self.idle_timer = QTimer(self)
        self.idle_timer.setInterval(200)
        self.idle_timer.timeout.connect(self.idle_poll)
        self.track_timer = QTimer(self)
        self.track_timer.setInterval(200)
        self.track_timer.timeout.connect(self.track)
        self.play_timer = QTimer(self)
        self.play_timer.setInterval(40)
        self.play_timer.timeout.connect(self.play_tick)

        self.setWindowTitle("Embris")
        logo = asset("embris.png")
        self.app_icon = QIcon(logo) if logo else QIcon()
        self.setWindowIcon(self.app_icon)
        self.setMinimumSize(320, 230)  # smaller than the normal size shows the compact view
        self.resize(960, 640)
        self.build()
        self.build_tray()
        BG["dim"] = int(self.s.get("bg_dim", 55))
        if self.s.get("bg_file"):
            self.set_background(self.s["bg_file"], self.s.get("bg_source"))
        self.apply_theme()
        self.apply_pin()
        self.apply_sidebar()
        self.show_page(0)
        self.refresh_presets()
        self.new_release = None  # a newer version found by the last check
        self.update_reply = None
        QTimer.singleShot(4000, lambda: self.check_updates(False))  # out of the way of start-up
        self.refresh_sequence()
        self.update_enabled()
        self.update_status()
        self.reset_undo()
        # Ctrl+Z undoes, Ctrl+Shift+Z (or Ctrl+Y) redoes; text boxes keep their own (Cmd on a Mac).
        for keys, slot in (("Ctrl+Z", self.undo), ("Ctrl+Shift+Z", self.redo), ("Ctrl+Y", self.redo)):
            sc = QShortcut(QKeySequence(keys), self)
            sc.setContext(Qt.ShortcutContext.WindowShortcut)
            sc.activated.connect(lambda fn=slot: None if isinstance(QApplication.focusWidget(), (QLineEdit, QPlainTextEdit)) else fn())
        self.clicker = core.AutoClicker(lambda: self.watcher.own_rect)
        self.clicker.on_finished = lambda: self.bridge.call.emit(self.clicker_stopped)
        self.watcher.get_click_hotkey = lambda: self.s.get("click_hotkey")
        self.watcher.get_overlay_hotkey = lambda: self.s.get("overlay_hotkey")
        self.watcher.on_overlay_hotkey = lambda: self.bridge.call.emit(self.toggle_overlay)
        self.overlay = None
        self.start_view_timer = QTimer(self)
        self.start_view_timer.setInterval(150)
        self.start_view_timer.timeout.connect(self.start_view_tick)
        # Short attention pulses while recording (one long alert would keep going after it stops).
        self.attention_timer = QTimer(self)
        self.attention_timer.setInterval(1200)
        self.attention_timer.timeout.connect(lambda: QApplication.alert(self, 600))
        self.watcher.on_click_hotkey = lambda: self.bridge.call.emit(self.toggle_clicker)
        self.watcher.start()
        self.track_timer.start()
        QTimer.singleShot(1500, self.check_permissions)

    # ----- layout helpers

    def card(self, title):
        f = QFrame()
        f.setObjectName("card")
        g = QGridLayout(f)
        g.setContentsMargins(18, 14, 18, 16)
        g.setHorizontalSpacing(10)
        g.setVerticalSpacing(8)
        g.addWidget(label(title, "cardTitle"), 0, 0, 1, 4)
        return f, g

    def page(self, title, subtitle):
        w = QWidget()
        w.setObjectName("page")
        v = QVBoxLayout(w)
        v.setContentsMargins(32, 22, 32, 22)
        v.setSpacing(14)
        v.addWidget(label(title, "title"))
        v.addWidget(label(subtitle, "sub"))
        v.addSpacing(4)
        self.stack.addWidget(w)
        return v

    # ----- build

    def logo_label(self, size):
        logo = QLabel()
        pm = QPixmap(asset("embris.png"))
        dpr = self.devicePixelRatioF()
        pm = pm.scaled(int(size * dpr), int(size * dpr), Qt.AspectRatioMode.KeepAspectRatio, Qt.TransformationMode.SmoothTransformation)
        pm.setDevicePixelRatio(dpr)
        logo.setPixmap(pm)
        return logo

    def build_mini(self):
        """Compact view, shown when the window is made smaller than its normal size."""
        w = QWidget()
        w.setObjectName("page")
        v = QVBoxLayout(w)
        v.setContentsMargins(18, 14, 18, 16)
        v.setSpacing(6)
        top = QHBoxLayout()
        if asset("embris.png"):
            top.addWidget(self.logo_label(32))
        top.addWidget(label("Embris", "appname"))
        top.addStretch()
        self.mini_expand = push("⤢")
        self.mini_expand.setObjectName("pin")
        self.mini_expand.setFixedSize(34, 32)
        self.mini_expand.setToolTip("Back to full size")
        self.mini_expand.clicked.connect(self.reset_window_size)
        top.addWidget(self.mini_expand)
        self.mini_pin = QPushButton()
        self.mini_pin.setObjectName("pin")
        self.mini_pin.setCheckable(True)
        self.mini_pin.setFixedSize(34, 32)
        self.mini_pin.setIconSize(QSize(20, 20))
        self.mini_pin.setCursor(Qt.CursorShape.PointingHandCursor)
        self.mini_pin.clicked.connect(lambda: self.set_pinned(self.mini_pin.isChecked()))
        top.addWidget(self.mini_pin)
        v.addLayout(top)
        v.addSpacing(6)
        row = QHBoxLayout()
        self.mini_dot = StatusDot()
        row.addWidget(self.mini_dot)
        self.mini_status = label("Ready", "strong")
        row.addWidget(self.mini_status, 1)
        v.addLayout(row)
        self.mini_hint = label("", "sub", wrap=True)
        self.mini_summary = label("", "sub", wrap=True)
        v.addWidget(self.mini_hint)
        v.addWidget(self.mini_summary)
        v.addStretch()
        btns = QHBoxLayout()
        btns.setSpacing(10)
        self.mini_record = push("●  Record", "big", self.toggle_record)
        self.mini_run = push("▶  Run macro", "primary", self.toggle)
        self.mini_run.setStyleSheet("font-size: 15px; padding: 12px 22px;")
        btns.addWidget(self.mini_record, 2)
        btns.addWidget(self.mini_run, 3)
        v.addLayout(btns)
        return w

    def resizeEvent(self, ev):
        super().resizeEvent(ev)
        if hasattr(self, "views"):
            collapsed = bool(self.s.get("sidebar_collapsed"))
            self.views.setCurrentIndex(1 if self.width() < (724 if collapsed else 880) or self.height() < 580 else 0)

    def build(self):
        central = QWidget()
        h = QHBoxLayout(central)
        h.setContentsMargins(0, 0, 0, 0)
        h.setSpacing(0)
        self.views = QStackedWidget()
        self.views.addWidget(central)
        self.views.addWidget(self.build_mini())
        for view in (central, self.views.widget(1)):
            view.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Ignored)
        self.backdrop = bgs.BackdropHost()
        host_layout = QVBoxLayout(self.backdrop)
        host_layout.setContentsMargins(0, 0, 0, 0)
        host_layout.addWidget(self.views)
        self.setCentralWidget(self.backdrop)

        side = QFrame()
        side.setObjectName("sidebar")
        side.setFixedWidth(220)
        sv = QVBoxLayout(side)
        sv.setContentsMargins(12, 18, 12, 14)
        sv.setSpacing(6)
        self.side = side
        top = QHBoxLayout()
        top.setContentsMargins(8, 0, 0, 0)
        self.side_logo = self.logo_label(36) if asset("embris.png") else QLabel()
        top.addWidget(self.side_logo)
        self.side_name = label("Embris", "appname")
        top.addWidget(self.side_name)
        top.addStretch()
        self.collapse_btn = push("≡")
        self.collapse_btn.setObjectName("pin")
        self.collapse_btn.setFixedSize(34, 32)
        self.collapse_btn.clicked.connect(self.toggle_sidebar)
        top.addWidget(self.collapse_btn)
        self.pin_btn = QPushButton()
        self.pin_btn.setObjectName("pin")
        self.pin_btn.setCheckable(True)
        self.pin_btn.setFixedSize(34, 32)
        self.pin_btn.setIconSize(QSize(20, 20))
        self.pin_btn.setCursor(Qt.CursorShape.PointingHandCursor)
        self.pin_btn.clicked.connect(lambda: self.set_pinned(self.pin_btn.isChecked()))
        top.addWidget(self.pin_btn)
        sv.addLayout(top)
        sv.addSpacing(18)

        self.stack = QStackedWidget()
        self.nav = QButtonGroup(self)
        self.nav.setExclusive(True)
        for i in self.NAV_ORDER:
            name = self.PAGES[i]
            b = QPushButton(self.NAV_ICONS[i] + "   " + name.replace("&&", "&&"))
            b.setToolTip(name.replace("&&", "&"))
            b.setObjectName("nav")
            b.setCheckable(True)
            b.setCursor(Qt.CursorShape.PointingHandCursor)
            b.clicked.connect(lambda _=False, n=i: self.show_page(n))
            self.nav.addButton(b, i)
            sv.addWidget(b)
        sv.addStretch()
        self.side_footer = [label("Settings save automatically.", "sub"), label("Embris v" + VERSION, "faint")]
        for w in self.side_footer:
            sv.addWidget(w)
        h.addWidget(side)

        right = QVBoxLayout()
        right.setContentsMargins(0, 0, 0, 0)
        right.setSpacing(0)
        h.addLayout(right, 1)
        self.banner = label("", wrap=True)
        self.banner.setStyleSheet("padding: 10px 32px; background: %s;" % blend(T.bg, WARNING, 0.18))
        self.banner.hide()
        right.addWidget(self.banner)
        right.addWidget(self.stack, 1)

        bar = QFrame()
        bar.setObjectName("bar")
        bh = QHBoxLayout(bar)
        bh.setContentsMargins(28, 16, 28, 16)
        bh.setSpacing(12)
        self.record_btn = push("●  Record", "big", self.toggle_record)
        self.record_btn.setMinimumWidth(150)
        self.run_btn = push("▶  Run macro", "primary", self.toggle)
        self.run_btn.setStyleSheet("font-size: 15px; padding: 12px 22px;")
        self.run_btn.setMinimumWidth(200)
        bh.addWidget(self.record_btn)
        bh.addWidget(self.run_btn)
        bh.addSpacing(8)
        self.dot = StatusDot()
        bh.addWidget(self.dot)
        info = QVBoxLayout()
        info.setSpacing(2)
        self.status_label = label("Ready", "strong")
        self.hint_label = label("", "sub")
        info.addWidget(self.status_label)
        info.addWidget(self.hint_label)
        bh.addLayout(info, 1)
        right.addWidget(bar)

        self.build_macro()
        self.build_presets()
        self.build_sequence_editor()
        self.build_timeline_editor()
        self.build_scripts()
        self.build_hotkeys()
        self.build_settings()
        self.build_themes()
        self.build_auto_clicker()

    def build_macro(self):
        v = self.page("Macro", "Choose the game, what to press or click, and how often.")
        s = self.s
        grid = QGridLayout()
        grid.setSpacing(16)
        grid.setColumnStretch(0, 1)
        grid.setColumnStretch(1, 1)
        v.addLayout(grid)
        v.addStretch()

        c, g = self.card("Target app")
        self.t_focus = Toggle("Bring the game to the front first", s["focus_app"])
        self.t_focus.toggled.connect(self.update_enabled)
        g.addWidget(self.t_focus, 1, 0, 1, 4)
        self.e_app = QLineEdit(s["app_name"])
        self.e_app.setMinimumHeight(34)
        g.addWidget(self.e_app, 2, 0, 1, 4)
        g.addWidget(label("App name, e.g. Roblox" if IS_MAC else "Part of the window title, e.g. Sober", "sub"), 3, 0, 1, 4)
        self.t_idle = Toggle("Wait until I'm idle", s["wait_idle"])
        g.addWidget(self.t_idle, 4, 0, 1, 2)
        self.sp_idle = spin(s["idle_sec"], 0, 1000000, 1, 0.5)
        self.sp_idle.setMaximumWidth(80)
        g.addWidget(self.sp_idle, 4, 2)
        g.addWidget(label("sec", "sub"), 4, 3)
        grid.addWidget(c, 0, 0)
        self.card_target = c

        c, g = self.card("Key press")
        self.t_key = Toggle("Press a key", s["press_key"])
        g.addWidget(self.t_key, 1, 0, 1, 2)
        g.addWidget(label("Key", "sub"), 2, 0)
        g.addWidget(label("Hold for (ms)", "sub"), 2, 1)
        self.key_box = KeyBox(s["key"], self.pause_hotkey)
        g.addWidget(self.key_box, 3, 0)
        self.sp_hold = spin(s["hold_ms"], 0, 2147483647)
        g.addWidget(self.sp_hold, 3, 1)
        g.setColumnStretch(0, 1)
        g.setColumnStretch(1, 1)
        g.setRowStretch(4, 1)
        grid.addWidget(c, 0, 1)
        self.card_key = c

        c, g = self.card("Timing")
        g.addWidget(label("Repeat every (seconds)", "sub"), 1, 0, 1, 2)
        self.sp_interval = spin(s["interval"], 0.001, 2147483, 3, 1)
        self.sp_interval.setMaximumWidth(160)
        self.sp_interval.valueChanged.connect(self.update_interval_hint)
        g.addWidget(self.sp_interval, 2, 0)
        self.interval_hint = label("", "sub")
        g.addWidget(self.interval_hint, 2, 1)
        self.t_calibration = Toggle("Calibration (%s)" % key_label(self.s.get("overlay_hotkey") or "f10"), False)
        self.t_calibration.setToolTip("Shows a crosshair and a faded ghost of your start view over the game, to line every run up the same way")
        self.t_calibration.toggled.connect(lambda on: None if on == (self.overlay is not None and self.overlay.isVisible()) else self.toggle_overlay())
        g.addWidget(self.t_calibration, 3, 0)
        # How see-through the ghost of the start view is.
        ghost_row = QHBoxLayout()
        self.ghost_slider = QSlider(Qt.Orientation.Horizontal)
        self.ghost_slider.setRange(0, 100)
        self.ghost_slider.setValue(int(s.get("ghost_opacity", 25)))
        self.ghost_slider.setToolTip("How visible the ghost of your start view is (lower = more see-through)")
        self.ghost_text = label("%d%%" % int(s.get("ghost_opacity", 25)), "sub")
        self.ghost_slider.valueChanged.connect(self.set_ghost_opacity)
        ghost_row.addWidget(self.ghost_slider, 1)
        ghost_row.addWidget(self.ghost_text)
        g.addLayout(ghost_row, 3, 1)
        g.setColumnStretch(1, 1)
        g.setRowStretch(4, 1)
        grid.addWidget(c, 1, 0)
        self.card_timing = c
        self.update_interval_hint()

        c, g = self.card("Mouse click")
        self.t_click = Toggle("Click at a position", s["click"])
        g.addWidget(self.t_click, 1, 0, 1, 2)
        chips = QHBoxLayout()
        chips.setSpacing(4)
        self.b_left = push("Left", "chip", checkable=True)
        self.b_right = push("Right", "chip", checkable=True)
        grp = QButtonGroup(self)
        grp.addButton(self.b_left)
        grp.addButton(self.b_right)
        (self.b_right if s["right_click"] else self.b_left).setChecked(True)
        chips.addStretch()
        chips.addWidget(self.b_left)
        chips.addWidget(self.b_right)
        g.addLayout(chips, 1, 2, 1, 2)
        g.addWidget(label("X", "sub"), 2, 0)
        g.addWidget(label("Y", "sub"), 2, 1)
        self.sp_x = spin(s["x"], -1000000, 1000000)
        self.sp_y = spin(s["y"], -1000000, 1000000)
        g.addWidget(self.sp_x, 3, 0)
        g.addWidget(self.sp_y, 3, 1)
        self.pick_btn = push("Pick (3s)", slot=lambda: self.start_pick(self.pick_btn, self.sp_x, self.sp_y, lambda: self.t_click.setChecked(True)))
        g.addWidget(self.pick_btn, 3, 2, 1, 2)
        self.cursor_label = label("", "sub")
        g.addWidget(self.cursor_label, 4, 0, 1, 4)
        for i in range(4):
            g.setColumnStretch(i, 1)
        grid.addWidget(c, 1, 1)
        self.card_click = c

    def build_presets(self):
        v = self.page("Presets", "Your macro suite: save macros, give each its own hotkey, or build one step by step.")
        row = QHBoxLayout()
        row.setSpacing(16)
        v.addLayout(row, 1)

        c, g = self.card("Macro suite")
        g.addWidget(label("Macro name", "sub"), 1, 0, 1, 2)
        self.e_preset = QLineEdit()
        self.e_preset.setPlaceholderText("e.g. Anti-AFK")
        self.e_preset.setMinimumHeight(34)
        g.addWidget(self.e_preset, 2, 0)
        g.addWidget(push("Save", "primary", self.save_preset), 2, 1)
        self.preset_list = QListWidget()
        self.preset_list.setTextElideMode(Qt.TextElideMode.ElideRight)
        self.preset_list.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.preset_list.currentRowChanged.connect(self.on_preset_selected)
        self.preset_list.itemDoubleClicked.connect(lambda _: self.load_preset())
        g.addWidget(self.preset_list, 3, 0, 1, 2)
        hk = QHBoxLayout()
        hk.setSpacing(8)
        hk.addWidget(label("Hotkey", "sub"))
        self.preset_hotkey = KeyBox(None, self.pause_hotkey)
        self.preset_hotkey.setToolTip("Press this key anywhere to load and run this macro (press again to stop)")
        self.preset_hotkey.changed.connect(self.set_preset_hotkey)
        hk.addWidget(self.preset_hotkey, 1)
        hk.addWidget(push("Clear", slot=lambda: self.set_preset_hotkey(None)))
        g.addLayout(hk, 5, 0, 1, 2)
        btns = QHBoxLayout()
        btns.setSpacing(8)
        btns.addWidget(push("Load", "primary", self.load_preset))
        btns.addWidget(push("Delete", slot=self.delete_preset))
        g.addLayout(btns, 6, 0, 1, 2)
        g.setColumnStretch(0, 1)
        g.setRowStretch(3, 1)
        row.addWidget(c, 1)
        self.card_saved = c

        c, g = self.card("Macro builder")
        g.addWidget(label("Add a step", "sub"), 1, 0, 1, 2)
        chips = QHBoxLayout()
        chips.setSpacing(6)
        self.step_group = QButtonGroup(self)
        for i, name in enumerate(("Key", "Click", "Wait")):
            b = push(name, "chip", checkable=True)
            b.clicked.connect(lambda _=False, n=i: self.step_stack.setCurrentIndex(n))
            self.step_group.addButton(b, i)
            chips.addWidget(b)
        g.addLayout(chips, 2, 0, 1, 2)

        self.step_stack = QStackedWidget()
        self.step_stack.setStyleSheet("QStackedWidget { background: transparent; }")
        kp = QWidget()
        kg = QGridLayout(kp)
        kg.setContentsMargins(0, 0, 0, 0)
        kg.addWidget(label("Key", "sub"), 0, 0)
        kg.addWidget(label("Hold for (ms)", "sub"), 0, 1)
        self.b_key = KeyBox("e", self.pause_hotkey)
        self.b_hold = spin(50, 0, 2147483647)
        kg.addWidget(self.b_key, 1, 0)
        kg.addWidget(self.b_hold, 1, 1)
        cp = QWidget()
        cg = QGridLayout(cp)
        cg.setContentsMargins(0, 0, 0, 0)
        for i, t in enumerate(("X", "Y", "Button")):
            cg.addWidget(label(t, "sub"), 0, i)
        self.b_x = spin(0, -1000000, 1000000)
        self.b_y = spin(0, -1000000, 1000000)
        cg.addWidget(self.b_x, 1, 0)
        cg.addWidget(self.b_y, 1, 1)
        self.b_btn = QComboBox()
        self.b_btn.addItems(["Left", "Right"])
        self.b_btn.setMinimumHeight(34)
        cg.addWidget(self.b_btn, 1, 2)
        self.b_pick = push("Pick", slot=lambda: self.start_pick(self.b_pick, self.b_x, self.b_y, None))
        cg.addWidget(self.b_pick, 1, 3)
        wp = QWidget()
        wg = QGridLayout(wp)
        wg.setContentsMargins(0, 0, 0, 0)
        wg.addWidget(label("Wait for (ms)", "sub"), 0, 0)
        self.b_wait = spin(500, 0, 2147483647, 0, 100)
        wg.addWidget(self.b_wait, 1, 0)
        wg.setColumnStretch(1, 1)
        for grid_ in (kg, cg):
            for col in range(4):
                grid_.setColumnStretch(col, 1)
        for w in (kp, cp, wp):
            w.setStyleSheet("background: transparent;")
            self.step_stack.addWidget(w)
        self.step_group.button(0).setChecked(True)
        g.addWidget(self.step_stack, 3, 0, 1, 2)
        add_row = QHBoxLayout()
        add_row.setSpacing(8)
        add_row.addWidget(push("+  Add step", "primary", self.add_step), 3)
        self.b_record = push("●  Record steps", slot=lambda: self.toggle_record(append=True))
        self.b_record.setToolTip("Record keys, clicks and camera movement and add them to the end of the sequence")
        add_row.addWidget(self.b_record, 2)
        g.addLayout(add_row, 4, 0, 1, 2)
        g.addWidget(label("Sequence (plays instead of the single key / click)", "sub"), 5, 0, 1, 2)
        self.builder_text = QPlainTextEdit()
        self.builder_text.setReadOnly(True)
        self.builder_text.setFont(QFontDatabase.systemFont(QFontDatabase.SystemFont.FixedFont))
        g.addWidget(self.builder_text, 6, 0, 1, 2)
        # Repeat and speed of the sequence (the same values as on the editor pages).
        rs = QHBoxLayout()
        rs.setSpacing(8)
        rs.addWidget(label("Repeat", "sub"))
        self.builder_repeat = spin(self.s.get("repeat", 1), 1, 2147483647)
        self.builder_repeat.setToolTip("How many times the sequence plays each time the macro runs")
        self.builder_repeat.valueChanged.connect(self.set_repeat)
        rs.addWidget(self.builder_repeat, 1)
        rs.addWidget(label("Speed", "sub"))
        self.builder_speed = QComboBox()
        self.builder_speed.setEditable(True)
        self.builder_speed.addItems(["0.5x", "1x", "1.5x", "2x", "4x"])
        self.builder_speed.setEditText("%gx" % float(self.s["speed"]))
        self.builder_speed.setToolTip("Type any speed: 0.5 = half speed, 1 = as recorded, 3 = three times as fast")
        self.builder_speed.setMinimumHeight(34)
        self.builder_speed.currentTextChanged.connect(self.set_speed_text)
        rs.addWidget(self.builder_speed, 1)
        g.addLayout(rs, 7, 0, 1, 2)
        btns = QHBoxLayout()
        btns.setSpacing(8)
        btns.addWidget(push("Remove last", slot=self.remove_last))
        btns.addWidget(push("Clear", slot=self.clear_sequence))
        g.addLayout(btns, 8, 0, 1, 2)
        g.setRowStretch(6, 1)
        g.setColumnStretch(0, 1)
        g.setColumnStretch(1, 1)
        row.addWidget(c, 1)
        self.card_builder = c

    # ----- sequence editor & timeline editor
    # Two pages that edit the same sequence: a table of steps, and a timeline with a track per key.
    # Both share a header (Record, Save sequence) and a footer (Test, Stop, time, Repeat, Speed).

    def editor_page(self, title, record_text):
        w = QWidget()
        w.setObjectName("page")
        v = QVBoxLayout(w)
        v.setContentsMargins(32, 22, 32, 18)
        v.setSpacing(14)
        head = QGridLayout()
        head.setHorizontalSpacing(10)
        head.addWidget(label(title, "title"), 0, 0)
        summary = label("", "sub")
        sub_row = QHBoxLayout()
        sub_row.setSpacing(6)
        sub_row.addWidget(summary)
        undo_btn = push("↶", "chip", self.undo)
        redo_btn = push("↷", "chip", self.redo)
        undo_btn.setToolTip("Undo (Ctrl+Z)")
        redo_btn.setToolTip("Redo (Ctrl+Shift+Z)")
        for b in (undo_btn, redo_btn):
            b.setFixedWidth(36)
            b.setEnabled(False)
            sub_row.addWidget(b)
        calibrate = push("⌖", "chip")
        calibrate.setFixedWidth(36)
        calibrate.setToolTip("Calibration: a crosshair and a ghost of your start view over the game, to line every run up the same way")
        calibrate.clicked.connect(lambda: self.show_calibration_menu(calibrate))
        sub_row.addWidget(calibrate)
        sub_row.addStretch()
        head.addLayout(sub_row, 1, 0)
        rec = push("●  " + record_text, slot=self.toggle_record)
        rec.setMinimumWidth(150)
        self.record_texts[rec] = "●  " + record_text
        save = push("Save sequence", "primary", self.save_sequence_to_suite)
        save.setToolTip("Save this sequence as a macro in your suite (see Presets)")
        head.addWidget(rec, 0, 1)
        head.addWidget(save, 0, 2)
        use = Toggle("Play this sequence when the macro runs", self.s["use_sequence"])
        use.toggled.connect(self.set_use_sequence)
        head.addWidget(use, 1, 1, 1, 2)
        head.setColumnStretch(0, 1)
        v.addLayout(head)
        body = QHBoxLayout()
        body.setSpacing(12)
        v.addLayout(body, 1)

        foot = QHBoxLayout()
        foot.setSpacing(10)
        test = push("▶  Test sequence", "primary", self.test_sequence)
        test.setStyleSheet("font-size: 14px; padding: 10px 20px;")
        test.setToolTip("Play the sequence now in the game (Repeat times), then stop")
        stop = push("■", slot=lambda: self.stop() if self.running else None)
        stop.setToolTip("Stop")
        stop.setEnabled(False)
        time_label = label("", "strong")
        foot.addWidget(test)
        foot.addWidget(stop)
        foot.addSpacing(8)
        foot.addWidget(time_label)
        foot.addStretch()
        foot.addWidget(label("Repeat", "sub"))
        repeat = spin(self.s.get("repeat", 1), 1, 2147483647)
        repeat.setToolTip("How many times the sequence plays each time the macro runs")
        repeat.setMaximumWidth(90)
        repeat.valueChanged.connect(self.set_repeat)
        foot.addWidget(repeat)
        foot.addSpacing(6)
        foot.addWidget(label("Speed", "sub"))
        # Pick a speed or type any (0.5 = half speed, 3 = three times as fast).
        speed = QComboBox()
        speed.setEditable(True)
        speed.addItems(["0.5x", "1x", "1.5x", "2x", "4x"])
        speed.setEditText("%gx" % float(self.s["speed"]))
        speed.setToolTip("Type any speed: 0.5 = half speed, 1 = as recorded, 3 = three times as fast")
        speed.setMinimumHeight(34)
        speed.setMaximumWidth(100)
        speed.currentTextChanged.connect(self.set_speed_text)
        foot.addWidget(speed)
        v.addLayout(foot)
        self.stack.addWidget(w)
        self.editors.append({"summary": summary, "undo": undo_btn, "redo": redo_btn, "record": rec, "save": save, "use": use, "test": test, "stop": stop,
                             "time": time_label, "repeat": repeat, "speed": speed})
        return body

    def build_sequence_editor(self):
        body = self.editor_page("Sequence editor", "Record keys")
        self.t_seq = self.editors[0]["use"]
        self.speed_box = self.editors[0]["speed"]

        c, g = self.card("Steps")
        self.t_moves = Toggle("Mouse moves", True)
        self.t_moves.setToolTip("Show camera / mouse movement in the list")
        self.t_moves.toggled.connect(lambda _: self.refresh_sequence())
        g.addWidget(self.t_moves, 0, 3, Qt.AlignmentFlag.AlignRight)
        self.step_table = StepTable(lambda: T)
        self.step_table.setToolTip("Drag the dots to reorder. Shift+click selects several. Right-click or ⋮ for more.")
        self.step_table.selection_changed.connect(self.load_step_props)
        self.step_table.rows_moved.connect(self.move_step_rows)
        self.step_table.menu_requested.connect(self.show_step_menu)
        self.step_table.delete_requested.connect(lambda: self.delete_step_rows(False))
        g.addWidget(self.step_table, 1, 0, 1, 4)
        g.setRowStretch(1, 1)
        tiles = QHBoxLayout()
        tiles.setSpacing(6)
        add = push("＋\nAdd step")
        add.setMinimumHeight(52)
        add.setStyleSheet("font-size: 12px; font-weight: 600; padding: 4px 6px;")
        add.clicked.connect(lambda: self.show_add_menu(add))
        tiles.addWidget(add, 2)
        for kind in (ed.KEY_PRESS, ed.KEY_DOWN, ed.KEY_UP, ed.DELAY):
            b = push(ed.ICONS[kind] + "\n" + ed.NAMES[kind], slot=lambda _=False, k=kind: self.add_step_row(k))
            b.setStyleSheet("font-size: 11px; padding: 4px 6px;")
            b.setMinimumHeight(52)
            tiles.addWidget(b, 1)
        g.addLayout(tiles, 2, 0, 1, 4)
        g.setColumnStretch(1, 1)
        body.addWidget(c, 3)
        self.steps_card = c

        c, g = self.card("Step properties")
        g.addWidget(label("Action", "sub"), 1, 0)
        self.ed_action = QComboBox()
        self.ed_action.addItems(ed.NAMES)
        self.ed_action.setMinimumHeight(34)
        self.ed_action.currentIndexChanged.connect(
            lambda i: self.edit_row(lambda r: ed.change_type(self.seq, r, i)) if i >= 0 else None)
        g.addWidget(self.ed_action, 1, 1, 1, 3)

        self.ed_keys_label = label("Keys", "sub")
        g.addWidget(self.ed_keys_label, 2, 0)
        slot = QHBoxLayout()
        slot.setSpacing(6)
        self.ed_key = KeyBox("e", self.pause_hotkey)
        self.ed_key.changed.connect(self.change_row_key)
        self.ed_all = push("All", "chip", checkable=True)
        self.ed_all.setToolTip("When on, changing or deleting this key changes every press of it in the sequence")
        self.ed_left = push("Left", "chip", checkable=True)
        self.ed_right = push("Right", "chip", checkable=True)
        self.ed_left.clicked.connect(lambda: self.edit_row(lambda r: ed.set_button(self.seq, r, False)))
        self.ed_right.clicked.connect(lambda: self.edit_row(lambda r: ed.set_button(self.seq, r, True)))
        self.ed_text = QLineEdit()
        self.ed_text.setPlaceholderText("Text to type")
        self.ed_text.textEdited.connect(lambda text: self.edit_row(lambda r: self.seq[r.first].__setitem__("text", text)))
        self.ed_move_x = spin(0, -1000000, 1000000)
        self.ed_move_y = spin(0, -1000000, 1000000)
        for box in (self.ed_move_x, self.ed_move_y):
            box.valueChanged.connect(lambda _: self.edit_row(
                lambda r: ed.set_move(self.seq, r, int(self.ed_move_x.value()), int(self.ed_move_y.value()))))
        for w in (self.ed_key, self.ed_all, self.ed_left, self.ed_right, self.ed_text, self.ed_move_x, self.ed_move_y):
            slot.addWidget(w, 0 if w is self.ed_all else 1)
        g.addLayout(slot, 2, 1, 1, 3)

        self.ed_hold_label = label("Hold", "sub")
        g.addWidget(self.ed_hold_label, 3, 0)
        self.ed_hold = spin(50, 0, 2147483647, 0, 10)
        self.ed_hold.setSuffix(" ms")
        self.ed_hold.valueChanged.connect(lambda v: self.edit_row(lambda r: ed.set_hold(self.seq, r, int(v))))
        g.addWidget(self.ed_hold, 3, 1, 1, 3)
        g.addWidget(label("Delay after", "sub"), 4, 0)
        self.ed_after = spin(0, 0, 2147483647, 0, 10)
        self.ed_after.setSuffix(" ms")
        self.ed_after.valueChanged.connect(lambda v: self.edit_row(
            lambda r: ed.set_delay_after(self.seq, self.seq_rows, self.seq_rows.index(r), int(v))))
        g.addWidget(self.ed_after, 4, 1, 1, 3)
        self.ed_pos_label = label("Position", "sub")
        g.addWidget(self.ed_pos_label, 5, 0)
        self.ed_x = spin(0, -1000000, 1000000)
        self.ed_y = spin(0, -1000000, 1000000)
        for box in (self.ed_x, self.ed_y):
            box.valueChanged.connect(lambda _: self.edit_row(
                lambda r: ed.set_position(self.seq, r, int(self.ed_x.value()), int(self.ed_y.value()))))
        g.addWidget(self.ed_x, 5, 1)
        g.addWidget(self.ed_y, 5, 2, 1, 2)
        g.addWidget(label("Keyboard preview", "cardTitle"), 6, 0, 1, 4)
        self.keyboard = KeyboardPreview(lambda: T)
        g.addWidget(self.keyboard, 7, 0, 1, 4)
        g.setRowStretch(7, 1)
        g.setColumnStretch(1, 1)
        g.setColumnStretch(2, 1)
        c.setMaximumWidth(320)
        body.addWidget(c, 2)
        self.props_card = c

    @staticmethod
    def small_button(text, slot):
        b = push(text, slot=slot)
        b.setStyleSheet("padding: 4px 8px;")
        b.setMinimumWidth(32)
        return b

    def build_timeline_editor(self):
        body = self.editor_page("Timeline editor", "Record inputs")
        c, g = self.card("Timeline")
        bar = QHBoxLayout()
        bar.setSpacing(6)
        self.tool_group = QButtonGroup(self)
        for i, (text, tip) in enumerate((("↖", "Select: drag bars to move them, drag their ends to change the hold. "
                                                "Drag on an empty spot to select several, then drag the box ends to squish or stretch them."),
                                         ("✂", "Cut: click a hold to split it, or an empty spot to cut every track there"))):
            b = push(text, "chip", checkable=True)
            b.setToolTip(tip)
            b.setFixedWidth(40)
            self.tool_group.addButton(b, i)
            bar.addWidget(b)
        self.tool_group.button(0).setChecked(True)
        self.tool_group.idClicked.connect(lambda i: setattr(self.timeline, "cut_tool", i == 1))
        self.tl_crop = push("⛶", "chip", checkable=True)
        self.tl_crop.setFixedWidth(40)
        self.tl_crop.setToolTip("Crop: drag the two bars to the part you want to keep, then click Crop (or Enter). Esc cancels.")
        self.tl_crop.clicked.connect(lambda: self.timeline.cancel_crop() if self.timeline.cropping else self.timeline.start_crop())
        bar.addWidget(self.tl_crop)
        self.tl_snap = QComboBox()
        self.tl_snap.addItems(["No snap" if ms == 0 else "%d ms" % ms for ms in SNAP_CHOICES])
        self.tl_snap.setCurrentIndex(3)
        self.tl_snap.setToolTip("Snap: bars line up to this step when you drag them")
        self.tl_snap.setMinimumHeight(34)
        self.tl_snap.setMinimumWidth(84)
        self.tl_snap.currentIndexChanged.connect(lambda i: setattr(self.timeline, "snap_ms", SNAP_CHOICES[max(0, i)]))
        bar.addWidget(self.tl_snap)
        self.tl_snap_keys = push("∪", "chip", checkable=True)
        self.tl_snap_keys.setFixedWidth(40)
        self.tl_snap_keys.setChecked(bool(self.s.get("snap_to_keys", True)))
        self.tl_snap_keys.setToolTip("Snap to keys: bars you move or stretch catch the starts and ends of other bars and the playhead")
        self.tl_snap_keys.toggled.connect(self.set_snap_to_keys)
        bar.addWidget(self.tl_snap_keys)
        self.tl_add_track = AddTrackBox(self.pause_hotkey)
        self.tl_add_track.setToolTip("Click, then press a key to give it a track. Double-click a track to add a press.")
        self.tl_add_track.changed.connect(self.add_timeline_track)
        self.tl_add_track.setMinimumWidth(84)
        bar.addWidget(self.tl_add_track)
        bar.addStretch()
        # Zoom sits on the title row so the tools fit a narrow window.
        zoom = QHBoxLayout()
        zoom.setSpacing(4)
        zoom.addStretch()
        small = lambda text, fn: self.small_button(text, fn)
        zoom.addWidget(small("−", lambda: self.timeline.zoom(0.7)))
        self.tl_zoom = QSlider(Qt.Orientation.Horizontal)
        self.tl_zoom.setRange(0, 1000)
        self.tl_zoom.setMaximumWidth(110)
        self.tl_zoom.setMinimumWidth(60)
        self.tl_zoom.setToolTip("Zoom (Ctrl+wheel on the timeline)")
        self.tl_zoom.valueChanged.connect(lambda v: None if self.syncing_editor else self.timeline.set_zoom(5 * 1000 ** (v / 1000.0)))
        zoom.addWidget(self.tl_zoom)
        zoom.addWidget(small("+", lambda: self.timeline.zoom(1.4)))
        zoom.addWidget(small("Fit", lambda: self.timeline.fit()))
        g.addLayout(zoom, 0, 1, 1, 3)
        g.addLayout(bar, 1, 0, 1, 4)
        self.timeline = TimelineEditor(lambda: T)
        self.timeline.snap_to_keys = bool(self.s.get("snap_to_keys", True))
        self.timeline.changed.connect(self.on_timeline_changed)
        self.timeline.selection_changed.connect(self.load_timeline_props)
        self.timeline.zoom_changed.connect(self.sync_zoom_slider)
        self.timeline.crop_changed.connect(lambda: self.tl_crop.setChecked(self.timeline.cropping))
        g.addWidget(self.timeline, 2, 0, 1, 4)
        g.setRowStretch(2, 1)
        g.setColumnStretch(1, 1)
        body.addWidget(c, 3)
        self.tl_card = c

        c, g = self.card("Selected input")
        self.tl_info = label("", "strong")
        g.addWidget(self.tl_info, 1, 0, 1, 4)
        self.tl_start, self.tl_dur, self.tl_end = (spin(0, 0, 2147483, 3, 0.01) for _ in range(3))
        for row, (name, box) in enumerate((("Start time", self.tl_start), ("Duration", self.tl_dur), ("End time", self.tl_end)), 2):
            box.setSuffix(" s")
            g.addWidget(label(name, "sub"), row, 0)
            g.addWidget(box, row, 1, 1, 3)
        self.tl_start.valueChanged.connect(lambda _: None if self.syncing_editor else self.timeline.set_selected(self.tl_start.value(), self.tl_dur.value()))
        self.tl_dur.valueChanged.connect(lambda _: None if self.syncing_editor else self.timeline.set_selected(self.tl_start.value(), self.tl_dur.value()))
        self.tl_end.valueChanged.connect(lambda _: None if self.syncing_editor else self.timeline.set_selected(
            self.tl_start.value(), max(0.0, self.tl_end.value() - self.tl_start.value())))
        g.addWidget(label("Action", "sub"), 5, 0)
        self.tl_type = QComboBox()
        self.tl_type.addItems(["Key hold", "Left click", "Right click", "Mouse move", "Type text"])
        self.tl_type.setMinimumHeight(34)
        self.tl_type.currentIndexChanged.connect(lambda i: None if self.syncing_editor or i < 0 else self.timeline.set_selected_kind(
            TL_KINDS[i], self.tl_key.key or "e"))
        g.addWidget(self.tl_type, 5, 1, 1, 3)
        self.tl_key_label = label("Key", "sub")
        g.addWidget(self.tl_key_label, 6, 0)
        self.tl_key = KeyBox("e", self.pause_hotkey)
        self.tl_key.changed.connect(lambda k: None if self.syncing_editor else self.timeline.set_selected_key(k))
        self.tl_text = QLineEdit()
        self.tl_text.setPlaceholderText("Text to type")
        self.tl_text.textEdited.connect(lambda text: self.timeline.set_selected_text(text))
        g.addWidget(self.tl_key, 6, 1, 1, 3)
        g.addWidget(self.tl_text, 6, 1, 1, 3)
        self.tl_duplicate = push("Duplicate", slot=lambda: self.timeline.duplicate_selected())
        self.tl_delete = push("Delete", slot=lambda: self.timeline.delete_selected())
        g.addWidget(self.tl_duplicate, 7, 0, 1, 2)
        g.addWidget(self.tl_delete, 7, 2, 1, 2)
        self.tl_pos_label = label("Mouse position", "cardTitle")
        g.addWidget(self.tl_pos_label, 8, 0, 1, 4)
        self.tl_x, self.tl_y = spin(0, -1000000, 1000000), spin(0, -1000000, 1000000)
        for box in (self.tl_x, self.tl_y):
            box.valueChanged.connect(lambda _: None if self.syncing_editor else self.timeline.set_selected_position(
                int(self.tl_x.value()), int(self.tl_y.value())))
        g.addWidget(self.tl_x, 9, 0, 1, 2)
        g.addWidget(self.tl_y, 9, 2, 1, 2)
        g.setRowStretch(10, 1)
        g.setColumnStretch(1, 1)
        c.setMaximumWidth(300)
        body.addWidget(c, 2)
        self.tl_props_card = c

    def build_scripts(self):
        v = self.page("Scripts", "Write macros as text, open .emb or .ahk files, and save .emb files to send to friends.")
        c, g = self.card("Script")
        bar = QHBoxLayout()
        bar.setSpacing(8)
        self.script_macros = QComboBox()
        self.script_macros.setMinimumHeight(34)
        self.script_macros.setMinimumWidth(160)
        bar.addWidget(self.script_macros, 2)
        edit = push("Edit", slot=self.script_from_macro)
        edit.setToolTip("Show the chosen macro from your suite as a script")
        bar.addWidget(edit)
        bar.addWidget(push("New", slot=lambda: self.script_box.setPlainText(emb_script.TEMPLATE)))
        bar.addWidget(push("Open", slot=self.open_script_dialog))
        bar.addWidget(push("Save file", slot=self.save_script_dialog))
        bar.addWidget(push("+  Add to suite", "primary", lambda: self.add_script_to_suite()))
        g.addLayout(bar, 1, 0)
        self.script_box = QPlainTextEdit()
        self.script_box.setFont(QFontDatabase.systemFont(QFontDatabase.SystemFont.FixedFont))
        self.script_box.setLineWrapMode(QPlainTextEdit.LineWrapMode.NoWrap)
        self.script_box.setStyleSheet("QPlainTextEdit { color: %s; }" % T.text)
        self.script_box.setPlainText(emb_script.TEMPLATE)
        g.addWidget(self.script_box, 2, 0)
        self.script_status = label("", "sub", wrap=True)
        g.addWidget(self.script_status, 3, 0)
        g.setRowStretch(2, 1)
        v.addWidget(c, 1)
        self.script_timer = QTimer(self)
        self.script_timer.setSingleShot(True)
        self.script_timer.setInterval(400)
        self.script_timer.timeout.connect(self.check_script)
        self.script_box.textChanged.connect(self.script_timer.start)
        self.script_folder = os.path.expanduser("~")
        self.check_script()
    def build_hotkeys(self):
        v = self.page("Hotkeys", "Start and stop the macro from anywhere, even while your game is focused.")
        c, g = self.card("Hotkeys")
        g.addWidget(label("Click a box, then press the key you want. Hotkeys work in any app and are never recorded.", "sub", wrap=True), 1, 0, 1, 3)
        g.addWidget(label("Run / stop the macro", "sub"), 2, 0)
        g.addWidget(label("Start / stop recording", "sub"), 2, 1)
        self.hotkey_box = KeyBox(self.s["hotkey"], self.pause_hotkey)
        self.hotkey_box.setMinimumWidth(200)
        self.hotkey_box.changed.connect(lambda name: self.on_hotkey_changed("hotkey", self.hotkey_box))
        g.addWidget(self.hotkey_box, 3, 0)
        self.record_hotkey_box = KeyBox(self.s["record_hotkey"], self.pause_hotkey)
        self.record_hotkey_box.setMinimumWidth(200)
        self.record_hotkey_box.changed.connect(lambda name: self.on_hotkey_changed("record_hotkey", self.record_hotkey_box))
        g.addWidget(self.record_hotkey_box, 3, 1)
        g.addWidget(label("Calibration overlay", "sub"), 2, 2)
        self.overlay_hotkey_box = KeyBox(self.s.get("overlay_hotkey") or "f10", self.pause_hotkey)
        self.overlay_hotkey_box.setMinimumWidth(200)
        self.overlay_hotkey_box.setToolTip("Shows or hides the crosshair and the ghost of your start view over the game")
        self.overlay_hotkey_box.changed.connect(self.set_overlay_hotkey)
        g.addWidget(self.overlay_hotkey_box, 3, 2)
        g.setColumnStretch(3, 1)
        v.addWidget(c)
        self.card_hotkey = c

        c, g = self.card("Recording")
        self.t_autosave = Toggle("Save each recording to my macro suite when I stop", self.s.get("auto_save_recordings", False))
        self.t_autosave.toggled.connect(lambda on: self.set_setting("auto_save_recordings", bool(on)))
        g.addWidget(self.t_autosave, 1, 0, 1, 3)
        g.addWidget(label("Each take is saved as \"Recording\" with the date and time. Find them on the Presets page.", "sub", wrap=True), 2, 0, 1, 3)
        # Frame sync: match Roblox's frame rate so every input lands on the same frame each replay.
        g.addWidget(label("Frame sync"), 3, 0)
        self.frame_sync_box = QComboBox()
        self.frame_sync_rates = [0, -1, 30, 60, 75, 120, 144, 165, 240, 360]
        hz = round(QApplication.primaryScreen().refreshRate()) if QApplication.primaryScreen() else 0
        for r in self.frame_sync_rates:
            self.frame_sync_box.addItem("Off" if r == 0 else ("Match my display" + (" (%d fps)" % hz if hz else "")) if r < 0 else "%d fps" % r)
        cur = int(self.s.get("frame_sync", 0))
        if cur not in self.frame_sync_rates:
            self.frame_sync_rates.append(cur)
            self.frame_sync_box.addItem("%d fps" % cur)
        self.frame_sync_box.setCurrentIndex(self.frame_sync_rates.index(cur))
        self.frame_sync_box.currentIndexChanged.connect(lambda i: self.set_setting("frame_sync", self.frame_sync_rates[i]))
        self.frame_sync_box.setToolTip("Set this to Roblox's frame rate (Roblox menu > Settings > Maximum Frame Rate). "
                                       "Playback then sends every input in the middle of a frame, so jumps land on the same frame each replay.")
        g.addWidget(self.frame_sync_box, 3, 1)
        g.addWidget(label("Pick the same frame rate as Roblox's Maximum Frame Rate, so each jump lands on the same frame every replay.", "sub", wrap=True), 4, 0, 1, 3)
        v.addWidget(c)

        c, g = self.card("Tips")
        tips = [
            "To record gameplay: click into the game, press the record key, play, then press it again. Keys, clicks and camera movement are all recorded. Press the run key to play it back.",
            "Keep \"Bring the game to the front first\" on so the key reaches the game even while you use other apps.",
            "Roblox kicks idle players after 20 minutes, so repeating every 900 seconds (15 min) is enough.",
            "Closing this window keeps Embris running from the %s. Make the window small for a compact view, or pin it on top." % ("menu bar" if IS_MAC else "tray"),
            "Pick a key that doesn't do anything harmful in your game. The hotkey also reaches the game, so choose one it ignores.",
        ]
        if IS_MAC:
            tips.append("On many Macs you need to hold fn to press F-keys like F6.")
        for i, tip in enumerate(tips):
            g.addWidget(label("•  " + tip, "sub", wrap=True), i + 1, 0)
        v.addWidget(c)
        v.addStretch()

    def build_settings(self):
        v = self.page("Settings", "How Embris behaves on your computer.")
        c, g = self.card("Window")
        where = "menu bar" if IS_MAC else "tray"
        self.t_close = Toggle("Keep running in the %s when I close the window" % where, self.s["close_to_tray"])
        self.t_close.toggled.connect(lambda on: self.set_setting("close_to_tray", on))
        g.addWidget(self.t_close, 1, 0)
        g.addWidget(label("The hotkey keeps working. Click the Embris icon in the %s to open it again, or use its menu to quit." % where, "sub", wrap=True), 2, 0)
        self.t_pin = Toggle("Keep the window on top of other windows (same as the pin button)", self.s["pinned"])
        self.t_pin.toggled.connect(self.set_pinned)
        g.addWidget(self.t_pin, 3, 0)
        v.addWidget(c)

        c, g = self.card("About")
        g.addWidget(label("Embris v%s for %s" % (VERSION, "macOS" if IS_MAC else "Linux"), "strong"), 1, 0)
        g.addWidget(label("Settings and presets are saved in " + core.config_dir(), "sub", wrap=True), 2, 0)
        perm = ("Embris needs permission to watch and send input: System Settings > Privacy & Security > "
                "Accessibility and Input Monitoring." if IS_MAC else
                "Works on X11 desktops. On Wayland, sending keys and the hotkey may not work - pick an \"X11\" / "
                "\"Xorg\" session at login. Bringing the game to the front needs xdotool.")
        g.addWidget(label(perm, "sub", wrap=True), 3, 0)
        up = QHBoxLayout()
        self.btn_update = push("Check for updates", slot=self.update_clicked)
        self.btn_update.setToolTip("Gets the newest Embris from " + core.RELEASES_PAGE)
        self.lbl_update = label("", "sub")
        up.addWidget(self.btn_update)
        up.addWidget(self.lbl_update, 1)
        g.addLayout(up, 5, 0)
        ex = QHBoxLayout()
        ex.addWidget(push("Quit Embris", slot=self.exit_app))
        ex.addWidget(push("Uninstall Embris", slot=self.uninstall))
        ex.addWidget(push("Back to full size", slot=self.reset_window_size))
        if not IS_MAC:
            ex.addWidget(push("Open .emb files with Embris", slot=self.link_file_type))
        ex.addStretch()
        g.addLayout(ex, 4, 0)
        v.addWidget(c)
        v.addStretch()

    def build_themes(self):
        v = self.page("Themes & Colors", "Pick a preset or make your own. Changes apply instantly.")
        c, g = self.card("Preset themes")
        tiles = QHBoxLayout()
        tiles.setSpacing(10)
        self.tiles = []
        for i, pr in enumerate(PRESETS):
            tile = ThemeTile(pr, i == 0)
            tile.clicked.connect(lambda _=False, p=pr: self.set_theme(p.copy()))
            tiles.addWidget(tile)
            self.tiles.append(tile)
        tiles.addStretch()
        g.addLayout(tiles, 1, 0)
        g.addWidget(push("Reset to " + PRESETS[0].name, slot=lambda: self.set_theme(PRESETS[0].copy())), 0, 1)
        v.addWidget(c)

        row = QHBoxLayout()
        row.setSpacing(16)
        c, g = self.card("Custom colors")
        self.color_boxes = []
        for i, name in enumerate(("Background", "Surface", "Accent", "Text", "Border")):
            g.addWidget(label(name), i + 1, 0)
            box = ColorBox(name + " color")
            box.changed.connect(lambda value, n=i: self.set_custom_color(n, value))
            g.addWidget(box, i + 1, 1)
            self.color_boxes.append(box)
        g.setColumnStretch(1, 1)
        row.addWidget(c, 3)

        # Custom background: a picture, GIF or video from a file or a link.
        c, g = self.card("Background")
        g.addWidget(label("Picture, GIF or video - a file or a link", "sub", wrap=True), 1, 0, 1, 3)
        g.addWidget(push("Choose file", slot=self.choose_background_file), 2, 0, 1, 3)
        self.bg_link = QLineEdit()
        self.bg_link.setPlaceholderText("Paste an image, video or page link")
        self.bg_link.setMinimumHeight(34)
        self.bg_link.returnPressed.connect(self.use_background_link)
        g.addWidget(self.bg_link, 3, 0, 1, 3)
        btns = QHBoxLayout()
        btns.setSpacing(8)
        btns.addWidget(push("Use link", "primary", self.use_background_link))
        btns.addWidget(push("Remove", slot=lambda: self.set_background(None, None)))
        g.addLayout(btns, 4, 0, 1, 3)
        g.addWidget(label("Darken", "sub"), 5, 0)
        self.bg_dim = spin(self.s.get("bg_dim", 55), 0, 100, 0, 5)
        self.bg_dim.setMaximumWidth(90)
        self.bg_dim.valueChanged.connect(self.set_background_dim)
        g.addWidget(self.bg_dim, 5, 1)
        g.addWidget(label("%", "sub"), 5, 2)
        self.bg_status = label("", "sub", wrap=True)
        g.addWidget(self.bg_status, 6, 0, 1, 3)
        g.setRowStretch(7, 1)
        g.setColumnStretch(2, 1)
        row.addWidget(c, 2)
        v.addLayout(row)
        v.addStretch()

    # ----- auto clicker: a simple click-at-a-rate tool, separate from the macro

    def build_auto_clicker(self):
        v = self.page("Auto clicker", "Clicks for you at a steady rate. Press the hotkey anywhere to start or stop.")
        s = self.s
        c, g = self.card("Click interval")
        self.ac_rate = label("", "sub")
        g.addWidget(self.ac_rate, 0, 2, 1, 2, Qt.AlignmentFlag.AlignRight)
        ms = float(s.get("click_interval_ms", 100))
        parts = (ms // 3600000, ms % 3600000 // 60000, ms % 60000 // 1000, ms % 1000)
        row = QHBoxLayout()
        row.setSpacing(8)
        self.ac_parts = []
        for value, unit in zip(parts, ("hours", "mins", "secs", "ms")):
            box = spin(value, 0, 1000000)
            box.valueChanged.connect(lambda _: self.save_clicker())
            self.ac_parts.append(box)
            row.addWidget(box, 1)
            row.addWidget(label(unit, "sub"))
            row.addSpacing(8)
        g.addLayout(row, 1, 0, 1, 4)
        v.addWidget(c)

        mid = QHBoxLayout()
        mid.setSpacing(16)
        c, g = self.card("Click options")
        g.addWidget(label("Mouse button", "sub"), 1, 0)
        g.addWidget(label("Click type", "sub"), 1, 1)
        self.ac_button = QComboBox()
        self.ac_button.addItems(["Left", "Right", "Middle"])
        self.ac_button.setCurrentIndex(int(s.get("click_button", 0)))
        self.ac_type = QComboBox()
        self.ac_type.addItems(["Single", "Double"])
        self.ac_type.setCurrentIndex(1 if s.get("click_double") else 0)
        for box in (self.ac_button, self.ac_type):
            box.setMinimumHeight(34)
            box.currentIndexChanged.connect(lambda _: self.save_clicker())
        g.addWidget(self.ac_button, 2, 0)
        g.addWidget(self.ac_type, 2, 1)
        mid.addWidget(c, 1)
        c, g = self.card("Click repeat")
        self.ac_forever = push("Until stopped", "chip", checkable=True)
        self.ac_count = push("Repeat", "chip", checkable=True)
        group = QButtonGroup(self)
        group.addButton(self.ac_forever, 0)
        group.addButton(self.ac_count, 1)
        self.ac_times = spin(max(1, int(s.get("click_times", 0))), 1, 2147483647)
        self.ac_times.valueChanged.connect(lambda _: self.save_clicker())
        group.idClicked.connect(lambda _: self.save_clicker())
        g.addWidget(self.ac_forever, 1, 0, 1, 3)
        g.addWidget(self.ac_count, 2, 0)
        g.addWidget(self.ac_times, 2, 1)
        g.addWidget(label("times", "sub"), 2, 2)
        (self.ac_count if int(s.get("click_times", 0)) > 0 else self.ac_forever).setChecked(True)
        mid.addWidget(c, 1)
        v.addLayout(mid)

        c, g = self.card("Cursor position")
        row = QHBoxLayout()
        row.setSpacing(8)
        self.ac_here = push("Where the pointer is", "chip", checkable=True)
        self.ac_fixed = push("At a set spot", "chip", checkable=True)
        spot = QButtonGroup(self)
        spot.addButton(self.ac_here, 0)
        spot.addButton(self.ac_fixed, 1)
        spot.idClicked.connect(lambda _: self.save_clicker())
        (self.ac_fixed if s.get("click_fixed") else self.ac_here).setChecked(True)
        self.ac_x = spin(s.get("click_x", 0), -1000000, 1000000)
        self.ac_y = spin(s.get("click_y", 0), -1000000, 1000000)
        for box in (self.ac_x, self.ac_y):
            box.setMaximumWidth(110)
            box.valueChanged.connect(lambda _: self.save_clicker())
        self.ac_pick = push("Pick (3s)")
        self.ac_pick.clicked.connect(lambda: self.start_pick(self.ac_pick, self.ac_x, self.ac_y,
                                                             lambda: (self.ac_fixed.setChecked(True), self.save_clicker())))
        for w in (self.ac_here, self.ac_fixed, label("X", "sub"), self.ac_x, label("Y", "sub"), self.ac_y, self.ac_pick):
            row.addWidget(w)
        g.addLayout(row, 1, 0, 1, 4)
        v.addWidget(c)
        v.addStretch()

        foot = QHBoxLayout()
        foot.setSpacing(10)
        self.ac_start = push("▶  Start", "primary", self.toggle_clicker)
        self.ac_start.setStyleSheet("font-size: 15px; padding: 12px 28px;")
        self.ac_start.setToolTip("Clicks are skipped while the pointer is over Embris, so you can always click Stop.")
        foot.addWidget(self.ac_start)
        foot.addSpacing(10)
        foot.addWidget(label("Hotkey", "sub"))
        self.ac_hotkey = KeyBox(s.get("click_hotkey") or "f7", self.pause_hotkey)
        self.ac_hotkey.setMinimumWidth(120)
        self.ac_hotkey.changed.connect(self.set_clicker_hotkey)
        foot.addWidget(self.ac_hotkey)
        self.ac_status = label("", "sub")
        foot.addWidget(self.ac_status, 1)
        v.addLayout(foot)
        self.ac_timer = QTimer(self)
        self.ac_timer.setInterval(100)
        self.ac_timer.timeout.connect(self.update_clicker_status)
        self.sync_clicker()

    def clicker_interval_ms(self):
        h, m, sec, ms = (b.value() for b in self.ac_parts)
        return h * 3600000 + m * 60000 + sec * 1000 + ms

    def save_clicker(self):
        if not hasattr(self, "ac_status"):
            return
        s = self.s
        s["click_interval_ms"] = self.clicker_interval_ms()
        s["click_button"] = max(0, self.ac_button.currentIndex())
        s["click_double"] = self.ac_type.currentIndex() == 1
        s["click_times"] = int(self.ac_times.value()) if self.ac_count.isChecked() else 0
        s["click_fixed"] = self.ac_fixed.isChecked()
        s["click_x"], s["click_y"] = int(self.ac_x.value()), int(self.ac_y.value())
        self.save()
        self.sync_clicker()

    def sync_clicker(self):
        self.ac_times.setEnabled(self.ac_count.isChecked())
        self.ac_x.setEnabled(self.ac_fixed.isChecked())
        self.ac_y.setEnabled(self.ac_fixed.isChecked())
        self.update_clicker_status()

    def update_clicker_status(self):
        ms = max(1.0, self.clicker_interval_ms())
        self.ac_rate.setText("= %g clicks a second" % round(1000 / ms, 1) if ms < 1000 else
                             "= one click every %g s" % round(ms / 1000, 2) if ms < 60000 else
                             "= one click every %g min" % round(ms / 60000, 2))
        key = key_label(self.s.get("click_hotkey"))
        n = self.clicker.clicks if hasattr(self, "clicker") else 0
        running = hasattr(self, "clicker") and self.clicker.running
        self.ac_status.setText("Clicking... %d click%s. Press %s to stop." % (n, "" if n == 1 else "s", key) if running
                               else "Press %s anywhere to start." % key)

    def toggle_clicker(self):
        if self.clicker.running:
            self.clicker.stop()
            self.clicker_stopped()
            return
        self.save_clicker()
        s = self.s
        self.clicker.start(s["click_interval_ms"], s["click_button"], s["click_double"], s["click_times"],
                           s["click_fixed"], s["click_x"], s["click_y"])
        self.ac_start.setText("■  Stop")
        set_kind(self.ac_start, "danger")
        self.ac_timer.start()
        self.set_status("running", "Auto clicker on", "Press %s anywhere to stop it." % key_label(s.get("click_hotkey")))

    def clicker_stopped(self):
        self.ac_timer.stop()
        self.ac_start.setText("▶  Start")
        set_kind(self.ac_start, "primary")
        self.update_clicker_status()
        n = self.clicker.clicks
        self.set_status("ready", "Auto clicker stopped", "%d click%s done." % (n, "" if n == 1 else "s"))

    def set_clicker_hotkey(self, key):
        if key in (self.s["hotkey"], self.s["record_hotkey"]) or any(p.get("hotkey") == key for p in self.presets):
            self.ac_hotkey.set_key(self.s.get("click_hotkey"))
            self.set_status("problem", "That key is already used", "%s is used by another hotkey." % key_label(key))
            return
        self.s["click_hotkey"] = key
        self.save()
        self.update_clicker_status()
    # ----- calibration overlay: crosshair + ghost of the start view

    def pulse_attention(self):
        QApplication.alert(self, 600)
        self.attention_timer.start()

    def start_view_tick(self):
        """While recording: as soon as Embris isn't the active window (the game is), save the start view once."""
        if not self.recording or getattr(self, "start_view_taken", True):
            self.start_view_timer.stop()
            return
        if self.isActiveWindow():
            return
        self.start_view_taken = ov.capture_start_view()
        if self.start_view_taken:
            self.start_view_timer.stop()

    def toggle_overlay(self):
        self.toggle_overlay_core()
        shown = self.overlay is not None and self.overlay.isVisible()
        if self.t_calibration.isChecked() != shown:
            self.t_calibration.blockSignals(True)
            self.t_calibration.setChecked(shown)
            self.t_calibration.blockSignals(False)
            self.t_calibration.update()

    def toggle_overlay_core(self):
        if self.overlay is not None and self.overlay.isVisible():
            self.overlay.hide()
            return
        if self.overlay is None:
            self.overlay = ov.CalibrationOverlay(lambda: T)
        self.overlay.ghost_opacity = int(self.s.get("ghost_opacity", 25)) / 100
        path = ov.start_view_file()
        ghost = os.path.exists(path)
        _, area = ov.screen_area()
        self.overlay.show_over(area, path if ghost else None,
                               "%s, then press %s to run.   %s hides this." % (
                                   "Line the view up with the ghost" if ghost else "Aim at your start spot",
                                   key_label(self.s["hotkey"]), key_label(self.s.get("overlay_hotkey"))))

    def set_ghost_opacity(self, value):
        self.s["ghost_opacity"] = int(value)
        self.ghost_text.setText("%d%%" % int(value))
        if self.overlay is not None:
            self.overlay.ghost_opacity = int(value) / 100
            self.overlay.update()
        self.save()

    def retake_start_view(self):
        """Takes a new start view in 3 seconds (time to switch to the game)."""
        self.retake_left = 3

        def tick():
            if self.retake_left > 0:
                self.set_status("waiting", "Taking the start view in %d..." % self.retake_left, "Switch to the game and aim where your run starts.")
                self.retake_left -= 1
                QTimer.singleShot(1000, tick)
                return
            ok = ov.capture_start_view()
            self.set_status("ready" if ok else "problem", "Start view saved" if ok else "Couldn't take the start view",
                            ("Press %s to show the calibration overlay." % key_label(self.s.get("overlay_hotkey"))) if ok else
                            ("Allow Embris under Screen Recording in System Settings." if IS_MAC else ""))
            if ok and self.overlay is not None and self.overlay.isVisible():
                self.overlay.hide()
                self.toggle_overlay()
        tick()

    def show_calibration_menu(self, button):
        menu = QMenu(self)
        shown = self.overlay is not None and self.overlay.isVisible()
        menu.addAction("%s the calibration overlay (%s)" % ("Hide" if shown else "Show", key_label(self.s.get("overlay_hotkey"))), self.toggle_overlay)
        menu.addAction("Retake the start view in 3 seconds", self.retake_start_view)
        menu.exec(button.mapToGlobal(button.rect().bottomLeft()))

    def set_overlay_hotkey(self, key):
        if key in (self.s["hotkey"], self.s["record_hotkey"], self.s.get("click_hotkey")) or any(p.get("hotkey") == key for p in self.presets):
            self.overlay_hotkey_box.set_key(self.s.get("overlay_hotkey"))
            self.set_status("problem", "That key is already used", "Pick a different key for each hotkey.")
            return
        self.s["overlay_hotkey"] = key
        self.save()
        self.t_calibration.setText("Calibration (%s)" % key_label(key))
    def build_tray(self):
        self.tray = None
        if not QSystemTrayIcon.isSystemTrayAvailable():
            return
        self.tray = QSystemTrayIcon(self.app_icon, self)
        self.tray.setToolTip("Embris")
        menu = QMenu()
        menu.addAction("Open Embris", self.show_from_tray)
        self.tray_toggle = menu.addAction("Start macro", self.toggle)
        menu.addSeparator()
        menu.addAction("Quit Embris", self.exit_app)
        menu.aboutToShow.connect(lambda: self.tray_toggle.setText(
            "Stop recording" if self.recording else "Stop macro" if self.running else "Start macro"))
        self.tray_menu = menu
        self.tray.setContextMenu(menu)
        self.tray.activated.connect(lambda reason: self.show_from_tray()
                                    if reason == QSystemTrayIcon.ActivationReason.Trigger and not IS_MAC else None)
        self.tray.show()

    # ----- pages, theme, pin

    def show_page(self, index):
        self.stack.setCurrentIndex(index)
        self.nav.button(index).setChecked(True)
        if index == 3 and self.timeline_needs_fit:
            QTimer.singleShot(0, self.timeline.fit)  # once it has its size
            self.timeline_needs_fit = False

    def set_theme(self, theme):
        global T
        T = theme
        self.s["theme_name"], self.s["theme_colors"] = theme.name, theme.colors()
        self.save()
        self.apply_theme()

    def set_custom_color(self, index, value):
        t = T.copy("Custom")
        setattr(t, Theme.FIELDS[index], value)
        self.set_theme(t)

    def apply_theme(self):
        QApplication.instance().setStyleSheet(stylesheet())
        for box, value in zip(self.color_boxes, T.colors()):
            box.set_value(value)
        for tile in self.tiles:
            tile.selected = tile.preset.name == T.name
            tile.update()
        self.banner.setStyleSheet("padding: 10px 32px; background: %s;" % blend(T.bg, WARNING, 0.18))
        self.update_pin_icon()
        for w in self.findChildren(QWidget):
            w.update()

    def update_pin_icon(self):
        on = bool(self.s["pinned"])
        for b in (self.pin_btn, self.mini_pin):
            b.setIcon(pin_icon(T.accent if on else T.sub, on))
            b.setToolTip("Pinned on top - click to unpin" if on else "Pin on top of other windows")
        self.pin_btn.setToolTip("Pinned on top - click to unpin" if on else "Pin on top of other windows")

    def set_pinned(self, on):
        on = bool(on)
        if on == bool(self.s["pinned"]):
            return
        self.s["pinned"] = on
        self.save()
        self.apply_pin()

    def apply_pin(self):
        on = bool(self.s["pinned"])
        for w in (self.pin_btn, self.mini_pin, self.t_pin):
            w.blockSignals(True)
            w.setChecked(on)
            w.blockSignals(False)
        self.t_pin.update()
        visible = self.isVisible()
        self.setWindowFlag(Qt.WindowType.WindowStaysOnTopHint, on)
        if visible:
            self.show()
        self.update_pin_icon()

    def set_setting(self, key, value):
        self.s[key] = value
        self.save()

    def save(self):
        try:
            core.save_json(core.SETTINGS_FILE, self.s)
        except OSError:
            pass

    def check_permissions(self):
        notes = []
        if IS_MAC and not self.watcher.trusted():
            notes.append("macOS is blocking Embris. Open System Settings > Privacy & Security, turn on Embris under "
                         "Accessibility and Input Monitoring, then quit and reopen Embris.")
        if sys.platform.startswith("linux"):
            if os.environ.get("XDG_SESSION_TYPE") == "wayland":
                notes.append("You're on Wayland: sending keys and the hotkey may not work. If nothing happens, log in with an X11 / Xorg session.")
            import shutil
            if shutil.which("xdotool") is None:
                notes.append("Install xdotool so Embris can bring the game to the front (for example: sudo apt install xdotool).")
        if notes:
            self.banner.setText("\n".join(notes))
            self.banner.show()

    # ----- tray & closing

    def show_from_tray(self):
        self.showNormal()
        self.raise_()
        self.activateWindow()

    def closeEvent(self, ev):
        if self.s["close_to_tray"] and self.tray is not None and not self.exiting:
            ev.ignore()
            self.hide()
            if not self.tip_shown:
                self.tip_shown = True
                self.tray.showMessage("Embris is still running",
                                      "Use the Embris icon in the %s to open it or quit." % ("menu bar" if IS_MAC else "tray"),
                                      self.app_icon, 4000)
            return
        self.running = self.player.running = False
        self.clicker.stop()
        if self.overlay is not None:
            self.overlay.close()
        if self.recording:
            self.toggle_record()
        if not self.skip_save:
            self.read_controls()
            self.save()
        self.watcher.stop()
        if self.tray is not None:
            self.tray.hide()
        ev.accept()
        QApplication.instance().quit()

    # ------------------------------------------------------------ updates (GitHub Releases)

    def update_clicked(self):
        if self.new_release:
            # The Mac/Linux installers run from a download, so take them to it.
            QDesktopServices.openUrl(QUrl(self.new_release.get("html_url") or core.RELEASES_PAGE))
        else:
            self.check_updates(True)

    def check_updates(self, manual):
        """Asks GitHub for the newest release (Qt's networking uses the system's certificates). On
        start-up it's quiet unless there's a new version."""
        if self.update_reply is not None:
            return
        if manual:
            self.lbl_update.setStyleSheet("")
            self.lbl_update.setText("Checking...")
        if not hasattr(self, "net"):
            self.net = QNetworkAccessManager(self)
        req = QNetworkRequest(QUrl(core.LATEST_RELEASE_API))
        req.setRawHeader(b"User-Agent", ("Embris/" + VERSION).encode())  # GitHub's API wants one
        req.setRawHeader(b"Accept", b"application/vnd.github+json")
        self.update_reply = reply = self.net.get(req)
        reply.finished.connect(lambda: self.update_checked(reply, manual))

    def update_checked(self, reply, manual):
        self.update_reply = None
        reply.deleteLater()
        status = reply.attribute(QNetworkRequest.Attribute.HttpStatusCodeAttribute)
        if status == 404:  # nothing published yet
            if manual:
                self.lbl_update.setText("No releases published yet.")
            return
        if reply.error() != QNetworkReply.NetworkError.NoError:
            if manual:
                self.lbl_update.setStyleSheet("color: %s;" % DANGER)
                self.lbl_update.setText("Couldn't reach GitHub: " + reply.errorString())
            return
        try:
            release = json.loads(bytes(reply.readAll()).decode("utf-8"))
        except ValueError:
            return
        tag = release.get("tag_name", "")
        if not core.is_newer(tag):
            if manual:
                self.lbl_update.setText("You have the newest version.")
            return
        self.new_release = release
        version = ".".join(str(x) for x in core.parse_version(tag)[:3])
        accent = "color: %s;" % T.accent
        self.lbl_update.setStyleSheet(accent)
        self.lbl_update.setText("Version %s is out! Download it and run the installer again." % version)
        self.btn_update.setText("Get v" + version)
        self.side_footer[1].setStyleSheet(accent)
        self.side_footer[1].setText("Update available: v" + version)
        if not manual and self.tray is not None and not self.isVisible():
            self.tray.showMessage("Embris %s is out" % version, "Open Embris and go to Settings to get it.")

    def uninstall(self):
        what = "Embris.app" if IS_MAC else "Embris and its app-menu entry"
        answer = QMessageBox.question(self, "Uninstall Embris", "Remove %s, its settings and presets from this computer?" % what)
        if answer != QMessageBox.StandardButton.Yes:
            return
        self.skip_save = True
        core.uninstall()
        QMessageBox.information(self, "Uninstall Embris", "Embris has been removed. It will close now.")
        self.exit_app()

    def exit_app(self):
        self.exiting = True
        self.close()

    # ----- small behaviours

    def pause_hotkey(self, on):
        self.watcher.paused = on

    def on_hotkey_changed(self, which, box):
        other = self.s["record_hotkey" if which == "hotkey" else "hotkey"]
        if box.key == other or box.key == self.s.get("click_hotkey"):
            box.set_key(self.s[which])
            self.set_status("problem", "That key is already used", "Pick a different key for each hotkey.")
            return
        self.s[which] = box.key
        self.save()
        self.update_status()
        self.refresh_sequence()

    def update_interval_hint(self):
        sec = self.sp_interval.value()
        if sec < 60:
            text = "= %g sec" % round(sec, 2)
        elif sec < 3600:
            text = "= %g min" % round(sec / 60, 1)
        else:
            text = "= %g hours" % round(sec / 3600, 2)
        self.interval_hint.setText(text)

    def update_enabled(self):
        if self.running:
            return
        focus = self.t_focus.isChecked()
        self.t_idle.setEnabled(focus)
        self.sp_idle.setEnabled(focus)
        use_seq = self.t_seq.isChecked() if hasattr(self, "t_seq") else False
        self.card_key.setEnabled(not use_seq)
        self.card_click.setEnabled(not use_seq)

    def track(self):
        """Tells the watcher where Embris is (so recording skips it) and shows the mouse position."""
        geo = self.frameGeometry()
        scale = 1.0 if IS_MAC else self.devicePixelRatioF()
        self.watcher.own_rect = (geo.x() * scale, geo.y() * scale, geo.width() * scale, geo.height() * scale)
        self.watcher.own_focused = self.isActiveWindow()
        if self.stack.currentIndex() == 0 and self.isVisible():
            try:
                x, y = self.player.ms.position
                self.cursor_label.setText("Mouse is at %d, %d" % (x, y))
            except Exception:
                pass

    def start_pick(self, button, sx, sy, done):
        if self.pick is not None:
            return
        self.pick = [3, button, button.text(), sx, sy, done]
        button.setEnabled(False)
        self.pick_tick()

    def pick_tick(self):
        left, button, text, sx, sy, done = self.pick
        if left > 0:
            button.setText("Move... %d" % left)
            self.pick[0] -= 1
            QTimer.singleShot(1000, self.pick_tick)
            return
        x, y = self.player.ms.position
        sx.setValue(int(x))
        sy.setValue(int(y))
        if done:
            done()
        button.setText(text)
        button.setEnabled(True)
        self.pick = None

    # ----- settings <-> controls

    def typed_speed(self):
        """The speed typed or picked in the speed box ("1.5x", "2"); 1 if it isn't a number."""
        try:
            sp = float(self.speed_box.currentText().strip().rstrip("xX").strip())
        except ValueError:
            return 1.0
        return min(sp, 1e6) if sp > 0 else 1.0

    def read_controls(self):
        s = self.s
        s["press_key"] = self.t_key.isChecked()
        s["key"] = self.key_box.key
        s["hold_ms"] = int(self.sp_hold.value())
        s["click"] = self.t_click.isChecked()
        s["right_click"] = self.b_right.isChecked()
        s["x"], s["y"] = int(self.sp_x.value()), int(self.sp_y.value())
        s["interval"] = self.sp_interval.value()
        s["focus_app"] = self.t_focus.isChecked()
        s["app_name"] = self.e_app.text().strip() or core.DEFAULTS["app_name"]
        s["wait_idle"] = self.t_idle.isChecked()
        s["idle_sec"] = self.sp_idle.value()
        s["use_sequence"] = self.t_seq.isChecked()
        s["speed"] = self.typed_speed()
        s["repeat"] = int(self.editors[0]["repeat"].value())

    def write_controls(self):
        s = self.s
        self.t_key.setChecked(s["press_key"])
        self.key_box.set_key(s["key"])
        self.sp_hold.setValue(s["hold_ms"])
        self.t_click.setChecked(s["click"])
        (self.b_right if s["right_click"] else self.b_left).setChecked(True)
        self.sp_x.setValue(s["x"])
        self.sp_y.setValue(s["y"])
        self.sp_interval.setValue(float(s["interval"]))
        self.t_focus.setChecked(s["focus_app"])
        self.e_app.setText(s["app_name"])
        self.t_idle.setChecked(s["wait_idle"])
        self.sp_idle.setValue(float(s["idle_sec"]))
        self.set_use_sequence(s["use_sequence"])
        self.set_speed_text("%gx" % float(s["speed"]))
        self.set_repeat(int(s.get("repeat", 1)))
        self.update_enabled()
        self.refresh_sequence()

    # ----- presets

    def refresh_presets(self):
        keep = self.preset_list.currentRow()
        self.preset_list.blockSignals(True)
        self.preset_list.clear()
        for p in self.presets:
            extra = ("   [%s]" % key_label(p["hotkey"]) if p.get("hotkey") else "")
            hs = (p.get("hotstrings") or {}).get("items") or []
            extra += ("   (%d hotstrings)" % len(hs) if hs else "")
            self.preset_list.addItem(p["name"] + extra)
        self.preset_list.blockSignals(False)
        if 0 <= keep < len(self.presets):
            self.preset_list.setCurrentRow(keep)
        self.suite_keys = {p["hotkey"] for p in self.presets if p.get("hotkey")}
        self.update_hotstrings()
        self.script_macros.clear()
        self.script_macros.addItems([p["name"] for p in self.presets])

    def on_preset_selected(self, row):
        if 0 <= row < len(self.presets):
            self.e_preset.setText(self.presets[row]["name"])
            self.preset_hotkey.set_key(self.presets[row].get("hotkey"))

    def save_presets(self):
        core.save_json(core.PRESETS_FILE, self.presets)
        self.refresh_presets()

    def save_preset(self):
        name = self.e_preset.text().strip()
        if not name:
            self.set_status("problem", "Name your macro", "Type a name in the Macro name box first.")
            return
        self.read_controls()
        data = {"name": name, "settings": core.macro_part(self.s)}
        for i, p in enumerate(self.presets):
            if p["name"].lower() == name.lower():
                for extra in ("hotkey", "hotstrings"):  # keep its hotkey and hotstrings
                    if p.get(extra):
                        data[extra] = p[extra]
                self.presets[i] = data
                index = i
                break
        else:
            self.presets.append(data)
            index = len(self.presets) - 1
        self.save_presets()
        self.preset_list.setCurrentRow(index)
        self.set_status("ready", 'Saved "%s"' % name, "Load it any time from the Presets page.")

    def load_preset(self):
        row = self.preset_list.currentRow()
        if row < 0 or self.running:
            return
        p = self.presets[row]
        self.s.update(p["settings"])
        self.last_preset_name = p["name"]
        self.timeline_needs_fit = True
        QTimer.singleShot(0, self.reset_undo)  # a loaded macro starts a fresh history
        self.save()
        self.write_controls()
        self.set_status("ready", 'Loaded "%s"' % p["name"], "Press %s or click Run macro to start it." % key_label(self.s["hotkey"]))

    def delete_preset(self):
        row = self.preset_list.currentRow()
        if row < 0:
            return
        name = self.presets[row]["name"]
        if QMessageBox.question(self, "Embris", 'Delete the macro "%s"?' % name) == QMessageBox.StandardButton.Yes:
            del self.presets[row]
            self.save_presets()
            self.e_preset.clear()

    # ----- macro suite: each macro can have its own hotkey

    def set_preset_hotkey(self, key):
        row = self.preset_list.currentRow()
        if row < 0:
            return
        if key and key in (self.s["hotkey"], self.s["record_hotkey"]):
            self.preset_hotkey.set_key(self.presets[row].get("hotkey"))
            self.set_status("problem", "That key is already used", "%s is the run or record hotkey." % key_label(key))
            return
        for p in self.presets:  # one macro per key
            if key and p.get("hotkey") == key:
                p.pop("hotkey")
        if key:
            self.presets[row]["hotkey"] = key
        else:
            self.presets[row].pop("hotkey", None)
        self.preset_hotkey.set_key(key)
        self.save_presets()
        name = self.presets[row]["name"]
        self.set_status("ready", ('%s runs "%s"' % (key_label(key), name)) if key else "Hotkey removed",
                        "Press it anywhere to start or stop this macro." if key else "")

    def on_suite_key(self, key):
        i = next((n for n, p in enumerate(self.presets) if p.get("hotkey") == key), -1)
        if i < 0 or self.recording:
            return
        if self.running:
            same = self.suite_preset == i
            self.stop()
            if same:
                return
        self.preset_list.setCurrentRow(i)
        self.load_preset()
        self.toggle()
        self.suite_preset = i if self.running else -1

    # ----- hotstrings (from every macro in the suite that has them)

    def update_hotstrings(self):
        old = {hs["name"]: hs["on"] for hs in self.watcher.hotstring_sets}
        sets = []
        for p in self.presets:
            hs = p.get("hotstrings") or {}
            items = sorted(((a, b) for a, b in hs.get("items") or []), key=lambda ab: -len(ab[0]))
            if not items:
                continue
            toggle = hs.get("toggle")
            sets.append({"name": p["name"], "window": hs.get("window") or "", "toggle": toggle,
                         "on": old.get(p["name"], not toggle), "items": items})
        self.watcher.hotstring_sets = sets

    def on_hotstrings_toggled(self, hs):
        key = key_label(hs["toggle"])
        where = (" in " + hs["window"]) if hs["window"] else ""
        self.set_status("running" if hs["on"] else "ready", "Hotstrings %s: %s" % ("on" if hs["on"] else "off", hs["name"]),
                        ("Type the short text%s. Press %s to turn them off." % (where, key)) if hs["on"]
                        else "Press %s to turn them back on." % key)

    # ----- sequence, builder, recording

    @property
    def seq(self):
        return self.s["sequence"]

    def sequence_changed(self):
        self.s["use_sequence"] = self.t_seq.isChecked()
        self.save()
        self.refresh_sequence()
        self.remember()

    # ----- undo / redo of sequence edits

    UNDO_LIMIT = 200

    def remember(self):
        """Called after every change: the state before it goes on the undo stack."""
        now = json.dumps(self.seq)
        if getattr(self, "undo_current", None) is None:
            self.undo_current = now
        elif now != self.undo_current:
            self.undo_stack.append(self.undo_current)
            del self.undo_stack[:-self.UNDO_LIMIT]
            self.redo_stack.clear()
            self.undo_current = now
        self.update_undo_buttons()

    def reset_undo(self):
        self.undo_stack, self.redo_stack = [], []
        self.undo_current = json.dumps(self.seq)
        self.update_undo_buttons()

    def undo(self):
        self.step_history(self.undo_stack, self.redo_stack, "Undone")

    def redo(self):
        self.step_history(self.redo_stack, self.undo_stack, "Redone")

    def step_history(self, source, target, what):
        if not source or self.running or self.recording:
            return
        target.append(self.undo_current)
        self.undo_current = source.pop()
        self.s["sequence"] = json.loads(self.undo_current)
        self.save()
        self.refresh_sequence()
        self.undo_current = json.dumps(self.seq)
        self.update_undo_buttons()
        self.set_status("ready", what, "%d more to undo, %d to redo." % (len(self.undo_stack), len(self.redo_stack)))

    def update_undo_buttons(self):
        for e in self.editors:
            e["undo"].setEnabled(bool(getattr(self, "undo_stack", None)))
            e["redo"].setEnabled(bool(getattr(self, "redo_stack", None)))

    def set_use_sequence(self, on):
        for e in self.editors:
            e["use"].blockSignals(True)
            e["use"].setChecked(on)
            e["use"].blockSignals(False)
            e["use"].update()
        self.s["use_sequence"] = bool(on)
        self.update_enabled()

    def set_repeat(self, value):
        self.s["repeat"] = max(1, int(value))
        for box in [e["repeat"] for e in self.editors] + [self.builder_repeat]:
            if int(box.value()) != self.s["repeat"]:
                box.blockSignals(True)
                box.setValue(self.s["repeat"])
                box.blockSignals(False)

    def set_speed_text(self, text):
        for box in [e["speed"] for e in self.editors] + [self.builder_speed]:
            if box.currentText() != text:
                box.blockSignals(True)
                box.setEditText(text)
                box.blockSignals(False)

    def refresh_sequence(self):
        seq = self.seq
        if seq:
            self.builder_text.setPlainText(core.format_steps(seq, 400, newest_last=True))
            self.builder_text.verticalScrollBar().setValue(self.builder_text.verticalScrollBar().maximum())
        else:
            self.builder_text.setPlainText("No steps yet. Pick Key, Click or Wait above and click Add step.")
        # The step table: one row per action, pauses as Delay rows.
        ed.normalize(seq)
        self.seq_rows = ed.rows(seq)
        moves = self.t_moves.isChecked()
        self.table_rows = [i for i, r in enumerate(self.seq_rows) if moves or r.kind != ed.MOVE]
        items = []
        for i in self.table_rows:
            r = self.seq_rows[i]
            items.append((ed.ICONS[r.kind], ed.NAMES[r.kind], ed.key_text(seq, r), ed.duration_text(seq, r), r.is_key or r.is_mouse))
        self.step_table.empty_text = (
            "No steps yet. Click Record keys (or press %s) and play, or add steps with the buttons below." % key_label(self.s["record_hotkey"])
            if not seq else 'Only mouse movement here. Turn on "Mouse moves" to see it.')
        self.step_table.set_items(items)
        self.load_step_props()
        if not self.timeline_editing:
            self.timeline.load(seq)
            if self.timeline_needs_fit and self.stack.currentIndex() == 3:
                self.timeline.fit()
                self.timeline_needs_fit = False
            self.load_timeline_props()
        actions = sum(1 for r in self.seq_rows if r.kind != ed.DELAY)
        summary = ("Nothing recorded yet" if not seq else "%s%d action%s  ·  %.2f s" % (
            (self.last_preset_name + "  ·  ") if self.last_preset_name else "", actions, "" if actions == 1 else "s",
            sum(st["d"] for st in seq) / 1000.0))
        for e in self.editors:
            e["summary"].setText(summary)
        self.update_time_labels()

    # ----- sequence editor

    def current_row(self):
        """The one selected row (None when none or several are selected)."""
        a, b = self.step_table.sel_start, self.step_table.sel_end
        return self.seq_rows[self.table_rows[a]] if 0 <= a == b < len(self.table_rows) else None

    def load_step_props(self):
        self.syncing_editor = True
        r = self.current_row()
        seq = self.seq
        kind = r.kind if r else ed.KEY_PRESS
        key, mouse_row = bool(r and r.is_key), bool(r and r.is_mouse)
        move, text = bool(r and kind == ed.MOVE), bool(r and kind == ed.TYPE)
        self.ed_action.setEnabled(r is not None)
        self.ed_action.setCurrentIndex(kind if r else -1)
        for w in (self.ed_key, self.ed_all):
            w.setVisible(not (mouse_row or move or text))
            w.setEnabled(key)
        for w in (self.ed_left, self.ed_right):
            w.setVisible(mouse_row)
        for w in (self.ed_move_x, self.ed_move_y):
            w.setVisible(move)
        self.ed_text.setVisible(text)
        self.ed_keys_label.setText("Button" if mouse_row else "Distance" if move else "Text" if text else "Keys")
        self.ed_hold_label.setText("Wait" if kind == ed.DELAY else "Takes" if move else "Hold")
        self.ed_hold.setEnabled(bool(r) and (kind in (ed.KEY_PRESS, ed.CLICK, ed.DELAY) or (move and r.count > 1)))
        self.ed_after.setEnabled(bool(r) and kind != ed.DELAY)
        fixed = mouse_row and seq[r.first].get("x") is not None
        self.ed_x.setEnabled(fixed)
        self.ed_y.setEnabled(fixed)
        self.ed_pos_label.setText("At cursor" if mouse_row and not fixed else "Position")
        self.keyboard.lit = set()
        if r:
            st = seq[r.first]
            if key:
                self.ed_key.set_key(st["k"])
                self.keyboard.lit = {st["k"]}
            if mouse_row:
                self.ed_left.setChecked(st.get("b") != "right")
                self.ed_right.setChecked(st.get("b") == "right")
                if fixed:
                    self.ed_x.setValue(st["x"])
                    self.ed_y.setValue(st["y"])
            if move:
                mx, my = ed.move_total(seq, r)
                self.ed_move_x.setValue(mx)
                self.ed_move_y.setValue(my)
            if text and self.ed_text.text() != st.get("text", ""):
                self.ed_text.setText(st.get("text", ""))
            self.ed_hold.setValue(ed.hold(seq, r))
            self.ed_after.setValue(ed.delay_after(seq, self.seq_rows, self.seq_rows.index(r)))
        self.keyboard.update()
        self.syncing_editor = False

    def select_steps(self, first, last):
        """Selects the table rows covering steps first..last."""
        a = b = -1
        for i, n in enumerate(self.table_rows):
            r = self.seq_rows[n]
            if a < 0 and r.last >= first:
                a = i
            if r.first <= last:
                b = i
        if a < 0:
            a = b = len(self.table_rows) - 1
        self.step_table.select_rows(a, max(a, b))

    def edit_row(self, change):
        """Applies an edit to the selected row, then refreshes and keeps it selected."""
        r = self.current_row()
        if self.syncing_editor or r is None:
            return
        first = r.first
        change(r)
        self.sequence_changed()
        self.select_steps(first, first)

    def change_row_key(self, key):
        r = self.current_row()
        if self.syncing_editor or r is None or not r.is_key:
            return
        if self.ed_all.isChecked():
            self.replace_key(self.seq[r.first]["k"], key, False)
            self.select_steps(r.first, r.first)
        else:
            self.edit_row(lambda x: ed.set_key(self.seq, x, key))

    def add_step_row(self, kind):
        end = self.step_table.sel_end
        at = self.seq_rows[self.table_rows[end]].last + 1 if 0 <= end < len(self.table_rows) else len(self.seq)
        h = 250 if kind == ed.DELAY else 40 if kind == ed.CLICK else 50
        self.seq[at:at] = ed.make(kind, self.ed_key.key or "e", hold_ms=h)
        if not self.t_seq.isChecked():
            self.t_seq.setChecked(True)
        self.sequence_changed()
        self.select_steps(at, at)
        self.step_table.setFocus()

    def delete_step_rows(self, keep_only):
        a, b = self.step_table.sel_start, self.step_table.sel_end
        if a < 0 or b >= len(self.table_rows):
            return
        r = self.current_row()
        if not keep_only and r is not None and r.is_key and self.ed_all.isChecked():
            self.replace_key(self.seq[r.first]["k"], None, True)
            return
        sa, sb = self.table_rows[a], self.table_rows[b]
        first = self.seq_rows[sa].first
        (ed.crop if keep_only else ed.delete)(self.seq, self.seq_rows, sa, sb)
        self.sequence_changed()
        if keep_only or not self.table_rows:
            self.step_table.select_rows(-1, -1)
        else:
            self.select_steps(first, first)
        self.set_status("ready", ("Cropped to %d step(s)" if keep_only else "Deleted %d step(s)") % (b - a + 1),
                        "%d steps left." % len(self.seq_rows) if self.seq else "The sequence is empty now.")

    def move_step_rows(self, a, b, to):
        if a < 0 or b >= len(self.table_rows):
            return
        sa, sb = self.table_rows[a], self.table_rows[b]
        sto = self.table_rows[to] if to < len(self.table_rows) else len(self.seq_rows)
        n = self.seq_rows[sb].last - self.seq_rows[sa].first + 1
        at = self.seq_rows[sto].first if sto < len(self.seq_rows) else len(self.seq)
        if at > self.seq_rows[sa].first:
            at -= n
        ed.move_rows(self.seq, self.seq_rows, sa, sb, sto)
        self.sequence_changed()
        self.select_steps(at, at + n - 1)

    def show_add_menu(self, button):
        menu = QMenu(self)
        for kind, name in enumerate(ed.NAMES):
            menu.addAction(name, lambda k=kind: self.add_step_row(k))
        menu.exec(button.mapToGlobal(button.rect().bottomLeft()))

    def show_step_menu(self, row, point):
        menu = QMenu(self)
        a, b = self.step_table.sel_start, self.step_table.sel_end
        r = self.current_row()

        def act(text, fn, enabled=True):
            menu.addAction(text, fn).setEnabled(enabled)

        def duplicate():
            first = r.last + 1
            ed.duplicate(self.seq, r)
            self.sequence_changed()
            self.select_steps(first, first)
        act("Duplicate", duplicate, r is not None)
        act("Move up", lambda: self.move_step_rows(a, b, a - 1), a > 0)
        act("Move down", lambda: self.move_step_rows(a, b, b + 2), b < len(self.table_rows) - 1)
        if r is not None and r.is_mouse:
            at_cursor = self.seq[r.first].get("x") is None

            def toggle_position():
                x, y = self.player.ms.position if at_cursor else (None, None)
                self.edit_row(lambda row_: ed.set_position(self.seq, row_, int(x) if at_cursor else None, int(y) if at_cursor else None))
            act("Click at a set position" if at_cursor else "Click where the cursor is", toggle_position)
        if r is not None and r.is_key:
            k = self.seq[r.first]["k"]
            act("Remove every " + key_label(k), lambda: self.replace_key(k, None, True))
        act("Crop to the selected steps", lambda: self.delete_step_rows(True), a >= 0 and b > a)
        act("Delete %d steps" % (b - a + 1) if b > a else "Delete", lambda: self.delete_step_rows(False), a >= 0)
        menu.exec(point)

    def replace_key(self, frm, to, remove):
        n = ed.replace_key(self.seq, frm, to, remove)
        self.sequence_changed()
        self.set_status("ready", ("No %s steps found" % key_label(frm)) if n == 0 else
                        ("Removed %d %s step(s)" % (n, key_label(frm))) if remove else
                        ("Changed %d %s step(s) to %s" % (n, key_label(frm), key_label(to))), "")

    # ----- timeline editor

    def on_timeline_changed(self):
        self.s["sequence"] = self.timeline.to_steps()
        self.timeline_editing = True
        self.sequence_changed()
        self.timeline_editing = False
        self.load_timeline_props()

    def set_snap_to_keys(self, on):
        self.s["snap_to_keys"] = bool(on)
        self.timeline.snap_to_keys = bool(on)
        self.save()

    def add_timeline_track(self, key):
        self.timeline.add_track(key)
        self.set_status("ready", "Added a %s track" % key_label(key), "Double-click the track where you want a press.")

    def sync_zoom_slider(self):
        self.syncing_editor = True
        self.tl_zoom.setValue(int(round(1000 * math.log(self.timeline.px_per_sec / 5) / math.log(1000))))
        self.syncing_editor = False

    def load_timeline_props(self):
        e = self.timeline.selected
        self.syncing_editor = True
        for w in (self.tl_start, self.tl_type, self.tl_key, self.tl_text, self.tl_duplicate, self.tl_delete):
            w.setEnabled(e is not None)
        self.tl_dur.setEnabled(e is not None and e.holdable)
        self.tl_end.setEnabled(e is not None and e.holdable)
        text = e is not None and e.kind == "text"
        self.tl_text.setVisible(text)
        self.tl_key.setVisible(not text)
        self.tl_key.setEnabled(e is not None and e.kind == "key")
        self.tl_key_label.setText("Text" if text else "Key")
        move = e is not None and e.kind == "move"
        click = e is not None and e.kind in ("left", "right")
        fixed = click and e.x is not None
        self.tl_x.setEnabled(move or fixed)
        self.tl_y.setEnabled(move or fixed)
        self.tl_pos_label.setText("Movement (x, y)" if move else "Clicks at the cursor" if click and not fixed else "Mouse position")
        if e is not None:
            self.tl_start.setValue(e.start)
            self.tl_dur.setValue(e.dur)
            self.tl_end.setValue(e.start + e.dur)
            self.tl_type.setCurrentIndex(TL_KINDS.index(e.kind))
            if e.kind == "key":
                self.tl_key.set_key(e.key)
            if text and self.tl_text.text() != (e.text or ""):
                self.tl_text.setText(e.text or "")
            if move or fixed:
                self.tl_x.setValue(e.x or 0)
                self.tl_y.setValue(e.y or 0)
            what = ("Hold" if e.dur >= 0.12 else "Tap") if e.holdable else "Move" if move else "Type"
            self.tl_info.setText("%s — %s" % (e.track, what))
        elif self.timeline.selection_count > 1:
            # Several bars: Start moves them, Duration / End squish or stretch them together.
            a, b = self.timeline.group_start, self.timeline.group_end
            for w in (self.tl_start, self.tl_dur, self.tl_end, self.tl_duplicate, self.tl_delete):
                w.setEnabled(True)
            self.tl_start.setValue(a)
            self.tl_dur.setValue(b - a)
            self.tl_end.setValue(b)
            self.tl_type.setCurrentIndex(-1)
            self.tl_info.setText("%d inputs selected" % self.timeline.selection_count)
        else:
            self.tl_type.setCurrentIndex(-1)
            self.tl_info.setText("Click a bar, or drag a box around several" if self.timeline.events else "Nothing here yet")
        self.syncing_editor = False

    # ----- shared: time, test, save

    def update_time_labels(self):
        total = sum(st["d"] for st in self.seq)
        now = min(total, self.player.position_ms()) if self.playing else 0
        text = "%s / %s" % (clock_text(now), clock_text(total))
        for e in self.editors:
            e["time"].setText(text)

    def play_tick(self):
        if not self.playing:
            return
        self.update_time_labels()
        if self.stack.currentIndex() == 3:
            total = sum(st["d"] for st in self.seq)
            self.timeline.set_playhead(min(total, self.player.position_ms()) / 1000.0)

    def test_sequence(self):
        """Plays the sequence once now (Repeat times), then stops."""
        if self.running:
            self.stop()
            return
        if self.recording:
            return
        self.read_controls()
        if not self.seq:
            self.set_status("problem", "Nothing to test", "Record or add some steps first.")
            return
        self.save()
        self.running = self.player.running = True
        self.testing = True
        self.set_editable(False)
        self.start_play(force_sequence=True)

    def save_sequence_to_suite(self):
        """Saves the sequence (with the current settings) as a macro in the suite."""
        self.read_controls()
        name = self.last_preset_name or "My sequence"
        data = {"name": name, "settings": core.macro_part(dict(self.s, use_sequence=True))}
        for i, p in enumerate(self.presets):
            if p["name"].lower() == name.lower():
                for extra in ("hotkey", "hotstrings"):
                    if p.get(extra):
                        data[extra] = p[extra]
                self.presets[i] = data
                break
        else:
            self.presets.append(data)
        self.last_preset_name = name
        self.save_presets()
        self.refresh_sequence()
        self.set_status("ready", 'Saved "%s" to your macro suite' % name, "Rename it or give it a hotkey on the Presets page.")

    # ----- scripts (.emb)

    def check_script(self):
        r = emb_script.parse(self.script_box.toPlainText(), "Script")
        if r.errors:
            more = "   (+%d more)" % (len(r.errors) - 1) if len(r.errors) > 1 else ""
            self.script_status.setText(r.errors[0] + more)
            self.script_status.setStyleSheet("color: %s;" % DANGER)
        else:
            parts = ['Looks good: "%s", %d step%s' % (r.name, len(r.steps), "" if len(r.steps) == 1 else "s")]
            if r.hotstrings:
                parts.append("%d hotstrings" % len(r.hotstrings))
            if r.hotkey:
                parts.append("hotkey " + key_label(r.hotkey))
            self.script_status.setText(", ".join(parts) + ". Click Add to suite to use it.")
            self.script_status.setStyleSheet("")
        return r

    def script_from_macro(self):
        i = self.script_macros.currentIndex()
        if 0 <= i < len(self.presets):
            self.script_box.setPlainText(emb_script.from_preset(self.presets[i]))

    def add_script_to_suite(self, select=True):
        r = self.check_script()
        if r.errors:
            self.set_status("problem", "The script has a mistake", r.errors[0])
            return False
        if r.hotkey in (self.s["hotkey"], self.s["record_hotkey"]):
            r.hotkey = None
        preset = r.to_preset()
        for p in self.presets:
            if preset.get("hotkey") and p.get("hotkey") == preset["hotkey"]:
                p.pop("hotkey")
        i = next((n for n, p in enumerate(self.presets) if p["name"].lower() == preset["name"].lower()), -1)
        if i >= 0:
            self.presets[i] = preset
        else:
            self.presets.append(preset)
            i = len(self.presets) - 1
        self.save_presets()
        if select:
            self.preset_list.setCurrentRow(i)
        self.set_status("ready", 'Added "%s" to your macro suite' % preset["name"],
                        ("Press %s anywhere to run it." % key_label(preset["hotkey"])) if preset.get("hotkey")
                        else "Load it from the Presets page.")
        return True

    def open_script_dialog(self):
        path, _ = QFileDialog.getOpenFileName(self, "Open a script", self.script_folder,
                                              "Embris or AutoHotkey scripts (*.emb *.ahk);;All files (*)")
        if path:
            self.open_script_file(path, False)

    def open_script_file(self, path, add_to_suite):
        """Shows a .emb file in the editor (an .ahk file is converted to a script first)."""
        try:
            with open(path, "r", encoding="utf-8-sig", errors="replace") as f:
                text = f.read()
            self.script_folder = os.path.dirname(path)
            name = os.path.splitext(os.path.basename(path))[0]
            is_ahk = path.lower().endswith(".ahk") or ("steps:" not in text and "hotstrings:" not in text and "::" in text)
            if is_ahk:
                text = emb_script.from_preset(emb_script.parse(text, name).to_preset())
            elif "name:" not in text:
                text = "name: %s\n%s" % (name, text)
            self.script_box.setPlainText(text)
            self.show_page(4)
            self.check_script()
            if add_to_suite:
                self.add_script_to_suite()
            else:
                self.set_status("ready", "Opened " + os.path.basename(path), "Check it, then click Add to suite to use it.")
        except OSError as e:
            self.set_status("problem", "Couldn't open that file", str(e))

    def save_script_dialog(self):
        r = self.check_script()
        safe = "".join(ch for ch in r.name if ch not in '\\/:*?"<>|') or "macro"
        path, _ = QFileDialog.getSaveFileName(self, "Save script", os.path.join(self.script_folder, safe + emb_script.EXTENSION),
                                              "Embris macro (*.emb)")
        if not path:
            return
        if not path.lower().endswith(emb_script.EXTENSION):
            path += emb_script.EXTENSION
        with open(path, "w", encoding="utf-8") as f:
            f.write(self.script_box.toPlainText())
        self.script_folder = os.path.dirname(path)
        self.set_status("ready", "Saved " + os.path.basename(path), "Send it to friends - they can open it in Embris.")

    # ----- custom background

    def choose_background_file(self):
        path, _ = QFileDialog.getOpenFileName(self, "Choose a background", os.path.expanduser("~"),
                                              "Pictures, GIFs and videos (*.png *.jpg *.jpeg *.jfif *.bmp *.gif *.webp *.avif "
                                              "*.mp4 *.m4v *.mov *.webm *.mkv);;All files (*)")
        if path:
            self.fetch_background(path)

    def use_background_link(self):
        link = self.bg_link.text().strip()
        if not link:
            self.bg_status.setText("Paste a link first (right-click a picture or video > Copy address, or a page link).")
            return
        self.fetch_background(link)

    def fetch_background(self, source):
        self.bg_status.setStyleSheet("")
        self.bg_status.setText("Loading..." if os.path.isfile(source) else "Downloading...")

        def work():
            try:
                path = bgs.resolve(source)
                self.bridge.call.emit(lambda: self.set_background(path, source))
            except Exception as e:  # network errors, blocked sites, unsupported files
                msg = str(e) or e.__class__.__name__
                self.bridge.call.emit(lambda: self.background_failed(msg))

        threading.Thread(target=work, daemon=True).start()

    def background_failed(self, msg):
        self.bg_status.setStyleSheet("color: %s;" % DANGER)
        self.bg_status.setText(msg)

    def set_background(self, path, source):
        old = self.s.get("bg_file") or ""
        error = self.backdrop.load(path) if path else None
        if not path:
            self.backdrop.stop()
        if error:
            self.background_failed(error)
            return
        self.s["bg_file"], self.s["bg_source"] = path or "", source or ""
        self.save()
        if old and old != path and old.startswith(bgs.folder()):
            try:
                os.remove(old)  # old downloads aren't needed any more
            except OSError:
                pass
        BG["active"] = self.backdrop.active or bool(path)
        self.apply_theme()
        self.bg_status.setStyleSheet("")
        self.bg_status.setText("Showing %s." % os.path.basename(source or path) if path else "No background. Pick a file or paste a link.")

    def set_background_dim(self, value):
        self.s["bg_dim"] = BG["dim"] = int(value)
        self.save()
        if BG["active"]:
            self.apply_theme()

    def changeEvent(self, ev):
        if ev.type() == QEvent.Type.WindowStateChange:
            self.backdrop.set_paused(self.isMinimized())
        super().changeEvent(ev)

    def hideEvent(self, ev):
        self.backdrop.set_paused(True)
        super().hideEvent(ev)

    def showEvent(self, ev):
        self.backdrop.set_paused(False)
        super().showEvent(ev)

    # ----- window: collapsible sidebar, back to full size, .emb file type

    def toggle_sidebar(self):
        self.s["sidebar_collapsed"] = not self.s.get("sidebar_collapsed")
        self.save()
        self.apply_sidebar()

    def apply_sidebar(self):
        c = bool(self.s.get("sidebar_collapsed"))
        self.side.setFixedWidth(64 if c else 220)
        for w in [self.side_logo, self.side_name, self.pin_btn] + self.side_footer:
            w.setVisible(not c)
        for i, b in enumerate(self.nav.buttons()):
            idx = self.nav.id(b)
            b.setText(self.NAV_ICONS[idx] if c else self.NAV_ICONS[idx] + "   " + self.PAGES[idx].replace("&&", "&&"))
        self.collapse_btn.setToolTip("Show the full sidebar" if c else "Collapse the sidebar")
        self.views.setCurrentIndex(1 if self.width() < (724 if c else 880) or self.height() < 580 else 0)

    def reset_window_size(self):
        self.showNormal()
        self.resize(960 - (156 if self.s.get("sidebar_collapsed") else 0), 640)

    def link_file_type(self):
        ok = core.register_file_type()
        self.set_status("ready" if ok else "problem", ".emb files now open with Embris" if ok else "Couldn't link .emb files",
                        "Double-click a macro file to add it to your suite." if ok else "xdg-mime is needed (part of xdg-utils).")
    def add_step(self):
        seq, kind = self.s["sequence"], self.step_group.checkedId()
        if kind == 0:
            k = self.b_key.key
            seq += [{"t": "kd", "k": k, "d": 0}, {"t": "ku", "k": k, "d": int(self.b_hold.value())}]
        elif kind == 1:
            b, x, y = ("right" if self.b_btn.currentIndex() == 1 else "left"), int(self.b_x.value()), int(self.b_y.value())
            seq += [{"t": "md", "b": b, "x": x, "y": y, "d": 0}, {"t": "mu", "b": b, "x": x, "y": y, "d": 40}]
        else:
            seq.append({"t": "w", "d": int(self.b_wait.value())})
        self.t_seq.setChecked(True)
        self.sequence_changed()

    def remove_last(self):
        core.remove_last_step(self.s["sequence"])
        self.sequence_changed()

    def clear_sequence(self):
        self.s["sequence"] = []
        self.sequence_changed()

    def set_record_buttons(self, recording):
        for b in [self.record_btn, self.mini_record, self.b_record] + [e["record"] for e in self.editors]:
            if b is self.b_record:
                b.setText("■  Stop" if recording else "●  Record steps")
            else:
                b.setText("■  Stop" if recording else self.record_texts.get(b, "●  Record"))
            set_kind(b, "danger" if recording else ("big" if b in (self.record_btn, self.mini_record) else None))

    def toggle_record(self, append=False):
        """Records keys, clicks and mouse movement. append=True (the builder's button) adds the
        recording to the end of the sequence instead of replacing it."""
        if not self.recording:
            # Recording stops anything that's playing: the macro, a test, the auto clicker.
            if self.running:
                self.stop()
            if self.clicker.running:
                self.clicker.stop()
                self.clicker_stopped()
            self.recording = True
            self.record_append = append
            self.watcher.start_recording()
            self.set_record_buttons(True)
            self.pulse_attention()  # taskbar flashes (dock icon bounces on a Mac) while recording
            self.start_view_taken = False
            self.start_view_timer.start()
            self.run_btn.setEnabled(False)
            self.mini_run.setEnabled(False)
            self.set_status("problem", "Recording...", "Play in the game, then press %s or click Stop." % key_label(self.s["record_hotkey"]))
            return
        self.recording = False
        self.start_view_timer.stop()
        self.attention_timer.stop()
        steps = self.watcher.stop_recording()
        self.set_record_buttons(False)
        self.run_btn.setEnabled(True)
        self.mini_run.setEnabled(True)
        if steps:
            if self.record_append and self.s["sequence"]:
                steps[0]["d"] = max(steps[0]["d"], 100)
                self.s["sequence"] = self.s["sequence"] + steps
            else:
                self.s["sequence"] = steps
            self.t_seq.setChecked(True)
            self.timeline_needs_fit = True
            self.sequence_changed()
            if not self.record_append and self.stack.currentIndex() != 3:
                self.show_page(2)
            elif self.stack.currentIndex() == 3:
                self.timeline.fit()
                self.timeline_needs_fit = False
        saved = None
        if steps and self.s.get("auto_save_recordings"):
            # Keep this take in the suite (with the current settings, playing the sequence).
            saved = "Recording " + time.strftime("%b %d, %H:%M:%S")
            self.read_controls()
            self.presets.append({"name": saved, "settings": core.macro_part(dict(self.s, use_sequence=True))})
            self.last_preset_name = saved
            self.save_presets()
            self.refresh_sequence()
        self.update_status()
        if saved:
            self.set_status("ready", 'Saved "%s"' % saved, "It's in your macro suite on the Presets page.")

    # ----- running

    def toggle(self):
        if self.recording:
            self.toggle_record()
            return
        if self.running:
            self.stop()
            return
        self.read_controls()
        s = self.s
        if s["use_sequence"] and not s["sequence"]:
            self.set_status("problem", "Nothing to play", "Record or build a sequence first, or turn off \"Play the sequence\".")
            return
        if not s["use_sequence"] and not s["press_key"] and not s["click"]:
            self.set_status("problem", "Nothing to do", "Turn on \"Press a key\" or \"Click at a position\" first.")
            return
        self.save()
        self.running = self.player.running = True
        self.runs = 0
        self.set_editable(False)
        self.update_status()
        self.timer.start(max(10, int(s["interval"] * 1000)))
        self.try_run()

    def stop(self):
        self.running = self.player.running = self.waiting = self.testing = False
        self.suite_preset = -1
        self.timer.stop()
        self.idle_timer.stop()
        self.set_editable(True)
        self.update_status()

    def set_editable(self, on):
        for c in (self.card_target, self.card_timing, self.card_key, self.card_click, self.card_hotkey,
                  self.card_saved, self.card_builder, self.steps_card, self.props_card, self.tl_card, self.tl_props_card):
            c.setEnabled(on)
        for e in self.editors:
            for k in ("record", "save", "use", "test"):
                e[k].setEnabled(on)
            e["stop"].setEnabled(not on)
        self.record_btn.setEnabled(on)
        self.mini_record.setEnabled(on)
        for b in (self.run_btn, self.mini_run):
            b.setText("▶  Run macro" if on else "■  Stop macro")
            set_kind(b, "primary" if on else "danger")
        if on:
            self.update_enabled()

    def try_run(self):
        if self.playing or self.waiting or not self.running:
            return
        s = self.s
        if s["focus_app"] and s["wait_idle"] and self.watcher.user_busy(s["idle_sec"]):
            self.waiting = True
            self.set_status("waiting", "Waiting for you to pause", "%gs with no input and nothing held down, then it runs." % s["idle_sec"])
            self.idle_timer.start()
            return
        self.start_play()

    def idle_poll(self):
        if not self.running:
            self.idle_timer.stop()
            self.waiting = False
            return
        if self.watcher.user_busy(self.s["idle_sec"]):
            return
        self.idle_timer.stop()
        self.waiting = False
        self.start_play()

    def start_play(self, force_sequence=False):
        s = self.s
        use = s["use_sequence"] or force_sequence
        steps = list(s["sequence"]) if use else core.single_action_steps(s)
        speed = float(s["speed"]) if use else 1.0
        repeat = int(s.get("repeat", 1)) if use else 1
        self.playing = True
        self.play_timer.start()
        if self.testing:
            self.set_status("running", "Testing the sequence", "%sat %gx. Click Stop to end it." % ("%d times, " % repeat if repeat > 1 else "", speed))
        elif use:
            self.set_status("running", "Playing sequence", "%d steps%s..." % (len(steps), ", %d times" % repeat if repeat > 1 else ""))
        args = (steps, speed, s["focus_app"], s["app_name"], repeat, self.frame_ms())
        threading.Thread(target=self.play_thread, args=args, daemon=True).start()

    def frame_ms(self):
        """Frame sync's frame length in ms (0 = off)."""
        fps = int(self.s.get("frame_sync", 0))
        if fps < 0:
            screen = QApplication.primaryScreen()
            fps = screen.refreshRate() if screen and screen.refreshRate() > 1 else 60
        return 1000.0 / fps if fps > 0 else 0.0

    def play_thread(self, steps, speed, focus_app, app_name, repeat=1, frame_ms=0.0):
        try:
            error = self.player.play(steps, speed, focus_app, app_name, repeat, frame_ms)
        except Exception as e:  # keep running if the OS refuses input
            error = str(e)
        self.bridge.call.emit(lambda: self.play_done(error))

    def play_done(self, error):
        self.playing = False
        self.play_timer.stop()
        self.timeline.set_playhead(-1)
        self.update_time_labels()
        if self.testing:
            self.stop()
            if error == "notfound":
                self.set_status("problem", '"%s" not found' % self.s["app_name"], "Open the game first, or change the app on the Macro page.")
            elif error:
                self.set_status("problem", "Couldn't send input", error)
            else:
                self.set_status("ready", "Test finished", "Change anything you like and test again.")
            return
        if not self.running:
            return
        if error == "notfound":
            self.set_status("problem", '"%s" not found' % self.s["app_name"], "Is the game open? Waiting... (checked %s)" % time.strftime("%H:%M:%S"))
        elif error:
            self.set_status("problem", "Couldn't send input", error)
        else:
            self.runs += 1
            self.update_status()

    # ----- status

    def macro_summary(self):
        s = self.s
        if s["use_sequence"]:
            what = "Plays a %d-step sequence" % len(s["sequence"])
        elif s["press_key"] and s["click"]:
            what = "Clicks + presses " + key_label(s["key"])
        elif s["press_key"]:
            what = "Presses " + key_label(s["key"])
        else:
            what = "Clicks" if s["click"] else "Nothing set up"
        sec = float(s["interval"])
        every = ("%gs" % round(sec, 2) if sec < 60 else "%g min" % round(sec / 60, 1) if sec < 3600
                 else "%g h" % round(sec / 3600, 2))
        return "%s every %s   |   Run %s   Record %s" % (what, every, key_label(s["hotkey"]), key_label(s["record_hotkey"]))

    def set_status(self, kind, text, hint):
        for dot in (self.dot, self.mini_dot):
            dot.set_kind(kind)
        self.status_label.setText(text)
        self.mini_status.setText(text)
        self.hint_label.setText(hint)
        self.mini_hint.setText(hint)
        self.mini_summary.setText(self.macro_summary())
        if self.tray is not None:
            self.tray.setToolTip("Embris - " + ("running" if self.running else "stopped"))

    def update_status(self):
        if self.recording:
            return
        key = key_label(self.s["hotkey"])
        if self.running:
            what = "Playing sequence" if self.s["use_sequence"] else "Running"
            self.set_status("running", what, "%d run(s), last at %s. Press %s to stop." % (self.runs, time.strftime("%H:%M:%S"), key))
        else:
            self.set_status("ready", "Ready", "Press %s anywhere, or click Run macro." % key)


class EmbrisApp(QApplication):
    """Receives macOS "open this file" events (double-clicking a .emb file)."""
    file_opened = Signal(str)

    def event(self, ev):
        if ev.type() == QEvent.Type.FileOpen:
            self.file_opened.emit(ev.file())
            return True
        return super().event(ev)


SERVER_NAME = "embris-" + str(os.getuid() if hasattr(os, "getuid") else "user")


def main():
    files = [a for a in sys.argv[1:] if os.path.isfile(a)]
    # Only one Embris at a time: a second launch hands its file to the running one.
    sock = QLocalSocket()
    sock.connectToServer(SERVER_NAME)
    if sock.waitForConnected(300):
        sock.write(("\n".join(files) or "show").encode("utf-8"))
        sock.waitForBytesWritten(1000)
        sock.disconnectFromServer()
        return 0

    app = EmbrisApp(sys.argv)
    app.setApplicationName("Embris")
    app.setDesktopFileName("embris")  # matches the Linux .desktop file so the right icon shows
    app.setQuitOnLastWindowClosed(False)
    win = MainWindow()
    win.show()

    def handle(text):
        win.show_from_tray()
        for path in text.splitlines():
            if os.path.isfile(path):
                win.open_script_file(path, True)

    app.file_opened.connect(handle)
    QLocalServer.removeServer(SERVER_NAME)
    server = QLocalServer()
    server.listen(SERVER_NAME)

    def incoming():
        conn = server.nextPendingConnection()
        conn.waitForReadyRead(1000)
        handle(bytes(conn.readAll()).decode("utf-8", "replace"))
        conn.close()

    server.newConnection.connect(incoming)
    if files:
        QTimer.singleShot(300, lambda: handle("\n".join(files)))
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
