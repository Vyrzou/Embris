"""Calibration overlay: a click-through layer over the game with a crosshair at the centre and a faded
"ghost" of the view when the recording started. Line the live view up with the ghost before playing,
and every run starts from the same spot.
"""
import os

from PySide6.QtCore import QRect, QRectF, Qt
from PySide6.QtGui import QColor, QFont, QGuiApplication, QPainter, QPen, QPixmap
from PySide6.QtWidgets import QWidget

import embris_core as core


def start_view_file():
    return os.path.join(core.config_dir(), "start-view.png")


def screen_area(screen=None):
    """The screen the game is on (the one under the mouse), in Qt's coordinates."""
    from PySide6.QtGui import QCursor
    screen = screen or QGuiApplication.screenAt(QCursor.pos()) or QGuiApplication.primaryScreen()
    return screen, screen.geometry()


def capture_start_view(path=None):
    """Grabs the middle of the screen under the mouse (what the camera is looking at) and saves it.
    On a Mac this needs the Screen Recording permission; without it the grab comes back empty."""
    path = path or start_view_file()
    screen, area = screen_area()
    w, h = max(16, area.width() * 2 // 5), max(16, area.height() * 2 // 5)
    x, y = (area.width() - w) // 2, (area.height() - h) // 2
    pm = screen.grabWindow(0, x, y, w, h)
    if pm.isNull():
        return False
    os.makedirs(os.path.dirname(path), exist_ok=True)
    return pm.save(path, "PNG")


class CalibrationOverlay(QWidget):
    def __init__(self, theme_getter):
        super().__init__(None, Qt.WindowType.FramelessWindowHint | Qt.WindowType.WindowStaysOnTopHint | Qt.WindowType.Tool
                         | Qt.WindowType.WindowTransparentForInput | Qt.WindowType.WindowDoesNotAcceptFocus)
        self.theme = theme_getter
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents)
        self.setAttribute(Qt.WidgetAttribute.WA_ShowWithoutActivating)
        self.ghost = None
        self.hint = ""
        self.ghost_opacity = 0.25  # how visible the start-view ghost is (0..1)

    def show_over(self, area, ghost_file, hint):
        """Covers the area (the game's screen) and shows the ghost of the start view, if there is one."""
        self.ghost = QPixmap(ghost_file) if ghost_file and os.path.exists(ghost_file) else None
        if self.ghost is not None and self.ghost.isNull():
            self.ghost = None
        self.hint = hint
        self.setGeometry(area)
        self.show()
        self.update()

    def paintEvent(self, _):
        t = self.theme()
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        cx, cy = self.width() / 2, self.height() / 2
        if self.ghost is not None:
            # The start view, faded, centred where it was captured (grabs are in physical pixels).
            dpr = self.ghost.devicePixelRatio() or 1
            w, h = self.ghost.width() / dpr, self.ghost.height() / dpr
            r = QRectF(cx - w / 2, cy - h / 2, w, h)
            p.setOpacity(self.ghost_opacity)
            p.drawPixmap(r, self.ghost, QRectF(self.ghost.rect()))
            p.setOpacity(1.0)
            dash = QPen(QColor(t.accent), 2)
            dash.setStyle(Qt.PenStyle.DashLine)
            p.setPen(dash)
            p.drawRect(r)
        # Crosshair: dark outline so it shows on any background, accent lines and a centre dot.
        arm, gap = 18, 5
        for color, width in ((QColor(10, 10, 12, 200), 5), (QColor(t.accent), 2.2)):
            pen = QPen(color, width)
            pen.setCapStyle(Qt.PenCapStyle.RoundCap)
            p.setPen(pen)
            p.drawLine(int(cx - arm), int(cy), int(cx - gap), int(cy))
            p.drawLine(int(cx + gap), int(cy), int(cx + arm), int(cy))
            p.drawLine(int(cx), int(cy - arm), int(cx), int(cy - gap))
            p.drawLine(int(cx), int(cy + gap), int(cx), int(cy + arm))
            p.drawEllipse(QRectF(cx - 1.5, cy - 1.5, 3, 3))
        if self.hint:
            f = QFont(self.font())
            f.setPixelSize(15)
            f.setBold(True)
            p.setFont(f)
            m = p.fontMetrics()
            w, h = m.horizontalAdvance(self.hint) + 24, m.height() + 14
            box = QRect(int(cx - w / 2), self.height() - h - 60, w, h)
            p.fillRect(box, QColor(20, 22, 26, 230))
            p.setPen(QColor("#FFFFFF"))
            p.drawText(box, Qt.AlignmentFlag.AlignCenter, self.hint)
