"""Custom backgrounds for Embris on macOS / Linux: pictures, animated GIFs and videos, from a file or a
link. A link can be a direct image/video address or a web page (wallpaper sites, Pinterest...) that
contains one. Videos need Qt's multimedia module (the optional "video backgrounds" install).
"""
import html
import os
import re
import shutil
import time
import urllib.error
import urllib.parse
import urllib.request

from PySide6.QtCore import QSize, Qt, QUrl
from PySide6.QtGui import QImage, QImageReader, QMovie, QPainter, QPixmap
from PySide6.QtWidgets import QWidget

import embris_core as core

try:
    from PySide6.QtMultimedia import QMediaPlayer, QVideoSink
    HAS_VIDEO = True
except ImportError:  # the optional add-on isn't installed
    HAS_VIDEO = False

VIDEO_EXT = (".mp4", ".m4v", ".mov", ".webm", ".mkv", ".avi")
IMAGE_EXT = (".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".avif")
MAX_DOWNLOAD = 300 * 1024 * 1024
UA = "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36"


def folder():
    path = os.path.join(core.config_dir(), "backgrounds")
    os.makedirs(path, exist_ok=True)
    return path


def is_media(path):
    return path.lower().endswith(VIDEO_EXT + IMAGE_EXT)


# ---------------------------------------------------------------- links

class BlockedError(Exception):
    pass


def _get(url, referer=None):
    req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "*/*", **({"Referer": referer} if referer else {})})
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            ctype = (resp.headers.get("Content-Type") or "").lower()
            length = int(resp.headers.get("Content-Length") or 0)
            if length > MAX_DOWNLOAD:
                raise ValueError("That file is too big (over 300 MB).")
            data = resp.read(MAX_DOWNLOAD + 1)
            if len(data) > MAX_DOWNLOAD:
                raise ValueError("That file is too big (over 300 MB).")
            return data, ctype
    except urllib.error.HTTPError as e:
        if e.code in (403, 429):
            raise BlockedError("That site blocked the download. Right-click the picture or video and choose "
                               "\"Copy image/video address\", or save it and choose the file.")
        raise


def resolve(link):
    """Downloads a link (or copies a local file) into the backgrounds folder. Returns the file path."""
    link = link.strip().strip('"')
    if os.path.isfile(link):
        if not is_media(link):
            raise ValueError("That file isn't a picture, GIF or video.")
        dest = os.path.join(folder(), "local-%x%s" % (abs(hash(link)), os.path.splitext(link)[1].lower()))
        shutil.copyfile(link, dest)
        return dest
    if not link.lower().startswith("http"):
        link = "https://" + link
    data, ctype = _get(link)
    path = urllib.parse.urlparse(link).path
    if ctype.startswith(("image/", "video/")) or is_media(path):
        return _save(link, ctype, data)
    media = find_media(data.decode("utf-8", "replace"), link)
    if not media:
        raise ValueError("No picture or video found on that page. Right-click it and choose \"Copy image/video address\" instead.")
    data, ctype = _get(media, referer=link)
    return _save(media, ctype, data)


def _save(url, ctype, data):
    ext = os.path.splitext(urllib.parse.urlparse(url).path)[1].lower()
    if not is_media("x" + ext):
        types = {"image/png": ".png", "image/jpeg": ".jpg", "image/gif": ".gif", "image/webp": ".webp",
                 "image/avif": ".avif", "video/mp4": ".mp4", "video/webm": ".webm", "video/quicktime": ".mov"}
        ext = next((e for t, e in types.items() if ctype.startswith(t)), None)
        if not ext:
            raise ValueError("That link isn't a picture, GIF or video.")
    path = os.path.join(folder(), "bg-%s%s" % (time.strftime("%Y%m%d-%H%M%S"), ext))
    with open(path, "wb") as f:
        f.write(data)
    return path


META_A = re.compile(r"""<meta[^>]+(?:property|name)\s*=\s*["'](og:video(?::secure_url|:url)?|og:image(?::secure_url)?|twitter:image|twitter:player:stream)["'][^>]*?content\s*=\s*["']([^"']+)""", re.I)
META_B = re.compile(r"""<meta[^>]+content\s*=\s*["']([^"']+)["'][^>]*?(?:property|name)\s*=\s*["'](?:og:video(?::secure_url|:url)?|og:image(?::secure_url)?|twitter:image)""", re.I)
SRC_ATTR = re.compile(r"""<(?:video|source|img)[^>]+?(?:data-)?src\s*=\s*["']?([^"'\s>]+)""", re.I)
ANY_MEDIA = re.compile(r"""(?:https?:)?//[^"'\s<>()\\]+?\.(?:mp4|webm|mov|gif|png|jpe?g|webp)(?:\?[^"'\s<>\\]*)?""", re.I)


def find_media(page, base):
    """Best media link on a page: video first, then GIF, then picture; HD versions preferred."""
    page = html.unescape(page.replace("\\u002F", "/").replace("\\/", "/"))
    found = [m.group(2) for m in META_A.finditer(page)] + [m.group(1) for m in META_B.finditer(page)]
    found += [m.group(1) for m in SRC_ATTR.finditer(page)] + [m.group(0) for m in ANY_MEDIA.finditer(page)]
    best, best_score = None, 0
    for raw in found:
        url = urllib.parse.urljoin(base, raw.strip())
        if not url.startswith("http"):
            continue
        path = urllib.parse.urlparse(url).path.lower()
        score = (30 if path.endswith(".mp4") else 28 if path.endswith(".mov") else 26 if path.endswith(".webm")
                 else 20 if path.endswith(".gif") else 10 if path.endswith((".png", ".jpg", ".jpeg", ".webp")) else 0)
        if not score:
            continue
        low = url.lower()
        if any(s in low for s in ("1080", "1920", "originals", "/4k")):
            score += 5
        elif "720" in low or "preview" in low:
            score += 2
        if any(s in low for s in ("logo", "avatar", "icon", "sprite", "thumb")):
            score -= 8
        if score > best_score:
            best, best_score = url, score
    return best


# ---------------------------------------------------------------- showing it

class BackdropHost(QWidget):
    """Paints the background behind the whole window; the pages and cards on top are see-through tints."""

    def __init__(self):
        super().__init__()
        self.frame = None
        self.movie = None
        self.player = None
        self.sink = None

    @property
    def active(self):
        return self.frame is not None

    def stop(self):
        if self.movie:
            self.movie.stop()
            self.movie.deleteLater()
        if self.player:
            self.player.stop()
            self.player.deleteLater()
        self.movie = self.player = self.sink = None
        self.frame = None
        self.update()

    def load(self, path):
        """Shows a local picture, GIF or video. Returns an error message, or None."""
        self.stop()
        if not path or not os.path.isfile(path):
            return "The background file is missing."
        low = path.lower()
        if low.endswith(VIDEO_EXT):
            if not HAS_VIDEO:
                return ("Video backgrounds need the video add-on. Run the Embris installer again and answer yes "
                        "to \"video backgrounds\", or use a picture or GIF.")
            self.player = QMediaPlayer(self)
            self.sink = QVideoSink(self)
            self.player.setVideoSink(self.sink)
            self.player.setLoops(QMediaPlayer.Loops.Infinite)
            self.sink.videoFrameChanged.connect(self._video_frame)
            self.player.setSource(QUrl.fromLocalFile(path))
            self.player.play()
            return None
        reader = QImageReader(path)
        if reader.supportsAnimation() and reader.imageCount() > 1:
            self.movie = QMovie(path)
            self.movie.frameChanged.connect(lambda _: self._set(self.movie.currentPixmap()))
            self.movie.start()
            return None
        img = reader.read()
        if img.isNull():
            return "Couldn't show that file: " + (reader.errorString() or "unknown format")
        self._set(QPixmap.fromImage(img))
        return None

    def _video_frame(self, frame):
        img = frame.toImage()
        if not img.isNull():
            # Keep frames modest in size: the window scales them to fit anyway.
            if img.width() > 960:
                img = img.scaledToWidth(960, Qt.TransformationMode.FastTransformation)
            self._set(QPixmap.fromImage(img))

    def _set(self, pixmap):
        self.frame = pixmap if pixmap and not pixmap.isNull() else None
        self.update()

    def set_paused(self, paused):
        if self.movie:
            self.movie.setPaused(paused)
        if self.player:
            (self.player.pause if paused else self.player.play)()

    def paintEvent(self, _):
        if self.frame is None:
            return
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)
        size = self.frame.size().scaled(self.size(), Qt.AspectRatioMode.KeepAspectRatioByExpanding)
        x, y = (self.width() - size.width()) // 2, (self.height() - size.height()) // 2
        p.drawPixmap(x, y, size.width(), size.height(), self.frame)
