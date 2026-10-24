// Custom backgrounds: pictures, animated GIFs and videos (decoded by Windows' Media Foundation, on the
// graphics card when it can), loaded from a file or downloaded from a link. A link can be a direct
// image/video address or a web page (wallpaper sites, Pinterest...) that contains one.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Embris
{
    static class Background
    {
        static readonly string[] VideoExt = { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".webm", ".mkv" };
        static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".jfif", ".avif" };
        const long MaxDownload = 300L * 1024 * 1024;

        static Control host;
        static Image still;          // a picture
        static GifFrames gif;        // an animated GIF
        static VideoFrames video;
        static volatile bool paused;
        static int generation; // frames from a background that was replaced are ignored
        public static event Action<string> Failed; // a video turned out to be unplayable (raised on the window's thread)

        public static string Folder
        {
            get
            {
                string dir = Path.Combine(Settings.Dir, "backgrounds");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static bool IsMedia(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return Array.IndexOf(VideoExt, ext) >= 0 || Array.IndexOf(ImageExt, ext) >= 0;
        }

        public static void Attach(Control form)
        {
            host = form;
            Backdrop.Attach(form);
            form.Resize += (s, e) =>
            {
                SetPaused(!form.Visible || ((Form)form).WindowState == FormWindowState.Minimized);
                if (Sizing) { resizedWhileSizing = true; return; } // dragging the edge: done once at the end (SizingDone)
                if (video != null) video.SetHostSize(HostSize);
                Rescale();
            };
            form.VisibleChanged += (s, e) => SetPaused(!form.Visible);
            ((Form)form).Activated += (s, e) => inactive = false;
            ((Form)form).Deactivate += (s, e) => inactive = true;
        }

        // True while the window edge is being dragged. The background keeps its current frame (stretched
        // to fit) instead of rescaling on every step, so resizing stays smooth.
        public static volatile bool Sizing;
        static volatile bool resizedWhileSizing; // Sizing is also on while the window is only moved: frames keep coming then

        public static void SizingDone()
        {
            Sizing = false;
            if (!resizedWhileSizing) return;
            resizedWhileSizing = false;
            if (video != null) video.SetHostSize(HostSize);
            if (gif != null) gif.Restart();
            Rescale();
            Backdrop.Retint();
            if (host != null) host.Invalidate(true);
        }

        public static void Stop()
        {
            if (gif != null) gif.Dispose();
            gif = null;
            if (still != null) still.Dispose();
            still = null;
            if (video != null) video.Dispose();
            video = null;
            generation++;
            Backdrop.SetFrame(null);
            Backdrop.ClearSpares();
            if (host != null) host.Invalidate(true);
        }

        // Shows a local picture, GIF or video. Returns an error message, or null.
        public static string Load(string path)
        {
            Stop();
            int gen = generation;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "The background file is missing.";
            string ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                if (Array.IndexOf(VideoExt, ext) >= 0)
                {
                    video = new VideoFrames(path, HostSize, UseGpu, Card, raw => OnVideoFrame(raw, gen), () =>
                    {
                        var h = host;
                        if (h == null || gen != generation) return;
                        try
                        {
                            h.BeginInvoke((Action)(() =>
                            {
                                if (gen == generation && Failed != null)
                                    Failed("This PC can't play that video's format. Install \"AV1 Video Extension\" / \"VP9 Video Extensions\" from the Microsoft Store, or use an MP4 link.");
                            }));
                        }
                        catch (InvalidOperationException) { }
                    });
                    return null;
                }
                still = LoadImage(path);
                if (GifFrames.IsAnimated(still))
                {
                    // Animated GIF: decoded, scaled and tinted on its own thread, like a video.
                    var frames = still;
                    still = null;
                    gif = new GifFrames(frames, () => HostSize, raw => OnVideoFrame(raw, gen));
                    return null;
                }
                Rescale();
                return null;
            }
            catch (Exception ex)
            {
                Stop();
                return "Couldn't show that file: " + ex.Message;
            }
        }

        static Image LoadImage(string path)
        {
            try
            {
                // Copy into memory so the file isn't kept locked (GIFs keep their frames).
                var bytes = File.ReadAllBytes(path);
                return Image.FromStream(new MemoryStream(bytes));
            }
            catch (ArgumentException)
            {
                return WicImage(path); // WebP, AVIF and other formats GDI+ can't read
            }
        }

        // Reads any format Windows has a codec for, through WPF's imaging (Windows Imaging Component).
        static Bitmap WicImage(string path)
        {
            using (var fs = File.OpenRead(path))
            {
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(fs,
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                var src = new System.Windows.Media.Imaging.FormatConvertedBitmap(decoder.Frames[0], System.Windows.Media.PixelFormats.Pbgra32, null, 0);
                var bmp = new Bitmap(src.PixelWidth, src.PixelHeight, PixelFormat.Format32bppPArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                src.CopyPixels(System.Windows.Int32Rect.Empty, data.Scan0, data.Stride * bmp.Height, data.Stride);
                bmp.UnlockBits(data);
                return bmp;
            }
        }

        static Size HostSize { get { return host == null ? Size.Empty : host.ClientSize; } }

        static void Rescale()
        {
            if (still == null || host == null) return;
            lock (still) Backdrop.SetFrame(Backdrop.Cover(still, HostSize));
            host.Invalidate(true);
        }

        static volatile bool framePending; // a frame is waiting for the window: skip new ones until it's shown

        // Whether a new frame would be shown now; the decoders skip copying frames out when it wouldn't.
        public static bool WantsFrame { get { return host != null && !paused && !framePending && !(Sizing && resizedWhileSizing); } }
        static double paintMs = 10;        // how long a repaint takes (smoothed)
        static volatile bool inactive;     // another app is in front
        public static bool UseGpu = true;  // decode on the graphics card (Direct3D 11) when it can
        public static string Card = "";    // which graphics card ("" = Windows' default)
        public static int Fps = 30;        // frame rate cap
        [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);

        // Called on the video thread with a new frame; scaled and tinted there so the window only copies it.
        static void OnVideoFrame(Bitmap raw, int gen)
        {
            var h = host;
            if (!WantsFrame || gen != generation) { Backdrop.Give(raw); return; }
            // Usually already the window's size (videos are scaled by the graphics card): use it as is.
            var size = HostSize;
            Bitmap scaled;
            if (raw.Width == size.Width && raw.Height == size.Height) scaled = raw;
            else { scaled = Backdrop.Cover(raw, size); Backdrop.Give(raw); }
            if (scaled == null) return;
            var composed = Backdrop.Compose(scaled);
            framePending = true;
            try
            {
                h.BeginInvoke((Action)(() =>
                {
                    framePending = false;
                    if ((video == null && gif == null) || gen != generation) { Backdrop.Give(scaled); Backdrop.Give(composed); return; }
                    // Repaint now, timing it: when the window takes long to paint (big or busy), frames come
                    // less often so the window stays responsive.
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    Backdrop.SetFrame(scaled, composed);
                    if (Backdrop.SolidCards)
                    {
                        // Solid cards: repaint only what the video shows through, then paint it now.
                        InvalidateSeeThrough(h);
                        RedrawWindow(h.Handle, IntPtr.Zero, IntPtr.Zero, 0x80 | 0x100); // all children, now
                    }
                    else RedrawWindow(h.Handle, IntPtr.Zero, IntPtr.Zero, 0x1 | 0x80 | 0x100); // invalidate, all children, now
                    paintMs = paintMs * 0.8 + clock.Elapsed.TotalMilliseconds * 0.2;
                    var v = video;
                    // The chosen frame rate at most (15 while another app, like the game, is in front),
                    // and less if the window can't repaint that fast.
                    double cap = 1000.0 / Math.Max(10, Fps);
                    if (inactive) cap = Math.Max(cap, 1000.0 / 15);
                    double min = Math.Max(cap, Math.Min(125, paintMs * (Fps >= 60 ? 1.6 : 2.5)));
                    if (v != null) v.MinFrameMs = min;
                    var gf = gif;
                    if (gf != null) gf.MinFrameMs = (float)min;
                }));
            }
            catch (InvalidOperationException) { framePending = false; Backdrop.Give(scaled); Backdrop.Give(composed); }
        }

        // Marks everything the background shows through for repainting, skipping solid cards and what
        // they contain (only their rounded corners show the video).
        static void InvalidateSeeThrough(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (!c.Visible) continue;
                var box = c as Box;
                if (box != null && box.Role == BoxRole.Card)
                {
                    int r = Ui.S(10);
                    box.Invalidate(new Rectangle(0, 0, r, r), false);
                    box.Invalidate(new Rectangle(box.Width - r, 0, r, r), false);
                    box.Invalidate(new Rectangle(0, box.Height - r, r, r), false);
                    box.Invalidate(new Rectangle(box.Width - r, box.Height - r, r, r), false);
                    continue;
                }
                c.Invalidate(false);
                if (box != null) InvalidateSeeThrough(box);
            }
        }

        static System.Windows.Forms.Timer trimTimer;
        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        public static void SetPaused(bool pause)
        {
            paused = pause;
            if (video != null) video.SetPaused(pause);
            if (gif != null) gif.Paused = pause;
            // Minimized or in the tray (while you play): after a few seconds hand the memory Embris isn't
            // using back to Windows, so the game gets it. (Fully unloading the video gives nothing more
            // back: Windows' video components keep their memory either way.)
            if (trimTimer == null)
            {
                trimTimer = new System.Windows.Forms.Timer { Interval = 3000 };
                trimTimer.Tick += (s, e) => { trimTimer.Stop(); if (paused) TrimMemory(); };
            }
            trimTimer.Stop();
            if (pause) trimTimer.Start();
        }

        static void TrimMemory()
        {
            Backdrop.ClearSpares();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1)); } catch (Exception) { }
        }

        // ---------------------------------------------------------------- links

        // Downloads a link (or copies a local file) into the backgrounds folder on a worker thread,
        // then calls done(path) or fail(message) on the window's thread.
        public static void Fetch(string link, Action<string> done, Action<string> fail)
        {
            link = (link ?? "").Trim().Trim('"');
            var h = host;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string result = null, error = null;
                try { result = Resolve(link); }
                catch (WebException ex)
                {
                    var resp = ex.Response as HttpWebResponse;
                    error = resp != null && (resp.StatusCode == HttpStatusCode.Forbidden || (int)resp.StatusCode == 429)
                        ? "That site blocked the download. Right-click the picture or video and choose \"Copy image/video address\", or save it and choose the file."
                        : "Couldn't download it: " + ex.Message;
                }
                catch (Exception ex) { error = ex.Message; }
                h.BeginInvoke((Action)(() => { if (error != null) fail(error); else done(result); }));
            });
        }

        static string Resolve(string link)
        {
            if (File.Exists(link))
            {
                if (!IsMedia(link)) throw new Exception("That file isn't a picture, GIF or video.");
                string copy = Path.Combine(Folder, "local-" + Math.Abs(link.GetHashCode()).ToString("x") + Path.GetExtension(link).ToLowerInvariant());
                File.Copy(link, copy, true);
                return copy;
            }
            if (!link.StartsWith("http", StringComparison.OrdinalIgnoreCase)) link = "https://" + link;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // TLS 1.2 / 1.3

            string type;
            byte[] first = Download(link, null, out type);
            if (type.StartsWith("image/") || type.StartsWith("video/") || IsMedia(new Uri(link).AbsolutePath))
                return Save(link, type, first);

            // A web page: find the best picture or video in it.
            string html = System.Text.Encoding.UTF8.GetString(first);
            string media = FindMedia(html, new Uri(link));
            if (media == null) throw new Exception("No picture or video found on that page. Right-click it and choose \"Copy image/video address\" instead.");
            byte[] data = Download(media, link, out type);
            return Save(media, type, data);
        }

        static byte[] Download(string url, string referer, out string contentType)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";
            req.Accept = "*/*";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (referer != null) req.Referer = referer;
            req.Timeout = 30000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                contentType = (resp.ContentType ?? "").ToLowerInvariant();
                if (resp.ContentLength > MaxDownload) throw new Exception("That file is too big (over 300 MB).");
                var buf = new byte[81920];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > MaxDownload) throw new Exception("That file is too big (over 300 MB).");
                }
                return ms.ToArray();
            }
        }

        static string Save(string url, string contentType, byte[] data)
        {
            string ext = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
            if (!IsMedia("x" + ext))
            {
                var map = new Dictionary<string, string> {
                    { "image/png", ".png" }, { "image/jpeg", ".jpg" }, { "image/gif", ".gif" }, { "image/webp", ".webp" },
                    { "image/avif", ".avif" }, { "video/mp4", ".mp4" }, { "video/webm", ".webm" }, { "video/quicktime", ".mov" } };
                ext = ".bin";
                foreach (var kv in map) if (contentType.StartsWith(kv.Key)) ext = kv.Value;
                if (ext == ".bin") throw new Exception("That link isn't a picture, GIF or video.");
            }
            string file = Path.Combine(Folder, "bg-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ext);
            File.WriteAllBytes(file, data);
            return file;
        }

        static readonly Regex MetaA = new Regex(@"<meta[^>]+(?:property|name)\s*=\s*[""'](og:video(?::secure_url|:url)?|og:image(?::secure_url)?|twitter:image|twitter:player:stream)[""'][^>]*?content\s*=\s*[""']([^""']+)", RegexOptions.IgnoreCase);
        static readonly Regex MetaB = new Regex(@"<meta[^>]+content\s*=\s*[""']([^""']+)[""'][^>]*?(?:property|name)\s*=\s*[""'](og:video(?::secure_url|:url)?|og:image(?::secure_url)?|twitter:image)", RegexOptions.IgnoreCase);
        static readonly Regex SrcAttr = new Regex(@"<(?:video|source|img)[^>]+?(?:data-)?src\s*=\s*[""']?([^""'\s>]+)", RegexOptions.IgnoreCase); // quoted or not
        static readonly Regex AnyMedia = new Regex(@"(?:https?:)?//[^""'\s<>()\\]+?\.(?:mp4|webm|mov|gif|png|jpe?g|webp)(?:\?[^""'\s<>\\]*)?", RegexOptions.IgnoreCase);

        // Picks the best media link on a page: video first, then GIF, then picture; HD versions preferred.
        static string FindMedia(string html, Uri page)
        {
            html = WebUtility.HtmlDecode(html.Replace("\\u002F", "/").Replace("\\/", "/"));
            var found = new List<string>();
            foreach (Match m in MetaA.Matches(html)) found.Add(m.Groups[2].Value);
            foreach (Match m in MetaB.Matches(html)) found.Add(m.Groups[1].Value);
            foreach (Match m in SrcAttr.Matches(html)) found.Add(m.Groups[1].Value);
            foreach (Match m in AnyMedia.Matches(html)) found.Add(m.Value);
            string best = null;
            int bestScore = 0;
            foreach (var raw in found)
            {
                Uri u;
                if (!Uri.TryCreate(page, raw.Trim(), out u) || (u.Scheme != "http" && u.Scheme != "https")) continue;
                string path = u.AbsolutePath.ToLowerInvariant();
                int score = path.EndsWith(".mp4") ? 30 : path.EndsWith(".mov") ? 28 : path.EndsWith(".webm") ? 24
                          : path.EndsWith(".gif") ? 20
                          : path.EndsWith(".png") || path.EndsWith(".jpg") || path.EndsWith(".jpeg") || path.EndsWith(".webp") ? 10 : 0;
                if (score == 0) continue;
                string s = u.AbsoluteUri.ToLowerInvariant();
                if (s.Contains("1080") || s.Contains("1920") || s.Contains("originals") || s.Contains("/4k")) score += 5;
                else if (s.Contains("720") || s.Contains("preview")) score += 2;
                if (s.Contains("logo") || s.Contains("avatar") || s.Contains("icon") || s.Contains("sprite") || s.Contains("thumb")) score -= 8;
                if (score > bestScore) { bestScore = score; best = u.AbsoluteUri; }
            }
            return best;
        }
    }

    // Plays an animated GIF on its own thread: each frame is decoded and scaled to the window there and
    // handed out at the GIF's own pace (skipping frames when the window can't keep up, so it never
    // slows down), so the window only has to copy finished frames.
    sealed class GifFrames : IDisposable
    {
        readonly Image img;               // used only by the GIF thread
        readonly Func<Size> size;
        readonly Action<Bitmap> onFrame;
        readonly int[] delays;            // ms per frame
        readonly Thread thread;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile bool stop;
        public volatile bool Paused;
        public volatile float MinFrameMs = 16; // set from how fast the window repaints

        public static bool IsAnimated(Image i)
        {
            return Array.IndexOf(i.FrameDimensionsList, FrameDimension.Time.Guid) >= 0 && i.GetFrameCount(FrameDimension.Time) > 1;
        }

        public GifFrames(Image image, Func<Size> size, Action<Bitmap> onFrame)
        {
            img = image;
            this.size = size;
            this.onFrame = onFrame;
            int count = img.GetFrameCount(FrameDimension.Time);
            delays = new int[count];
            byte[] raw = null;
            try { raw = img.GetPropertyItem(0x5100).Value; } catch (ArgumentException) { } // PropertyTagFrameDelay, 1/100 s each
            for (int i = 0; i < count; i++)
            {
                int d = raw != null && raw.Length >= (i + 1) * 4 ? BitConverter.ToInt32(raw, i * 4) * 10 : 100;
                delays[i] = d < 20 ? 100 : d; // like browsers: "as fast as possible" GIFs play at 10 fps
            }
            thread = new Thread(Run) { IsBackground = true, Name = "Embris GIF" };
            thread.Start();
        }

        public void Restart() { wake.Set(); }

        void Run()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double due = 0, lastShown = -1000;
            int i = 0, count = delays.Length;
            while (!stop)
            {
                if (Paused) { wake.WaitOne(200); clock.Restart(); due = 0; lastShown = -1000; continue; }
                double now = clock.Elapsed.TotalMilliseconds;
                double next = Math.Max(due, lastShown + MinFrameMs);
                if (now < next) { wake.WaitOne((int)Math.Ceiling(next - now)); continue; }
                // Running late (a busy window): skip ahead so the animation keeps its real speed.
                for (int n = 0; n < count && due + delays[i] <= now; n++) { due += delays[i]; i = (i + 1) % count; }
                if (now - due > 1000) due = now; // fell far behind (the PC was asleep...): start fresh
                try
                {
                    img.SelectActiveFrame(FrameDimension.Time, i);
                    if (!Background.WantsFrame) { lastShown = clock.Elapsed.TotalMilliseconds; due += delays[i]; i = (i + 1) % count; continue; }
                    var frame = Backdrop.Cover(img, size());
                    if (frame != null) onFrame(frame);
                }
                catch (Exception) { } // a broken frame: skip it
                lastShown = clock.Elapsed.TotalMilliseconds;
                due += delays[i];
                i = (i + 1) % count;
            }
        }

        public void Dispose()
        {
            stop = true;
            wake.Set();
            if (Thread.CurrentThread != thread) thread.Join(1000);
            img.Dispose();
        }
    }

    // Decodes a video with Windows' Media Foundation (MP4, WebM/VP9, MOV...) on its own thread and hands
    // out frames at the video's own pace, looping at the end.
    sealed class VideoFrames : IDisposable
    {
        readonly Thread thread;
        readonly Action<Bitmap> onFrame;
        readonly Action onFail;
        volatile bool stop, paused;

        readonly bool gpu;
        readonly string card;

        public VideoFrames(string path, Size host, bool gpu, string card, Action<Bitmap> onFrame, Action onFail)
        {
            this.gpu = gpu;
            this.card = card;
            SetHostSize(host);
            this.onFrame = onFrame;
            this.onFail = onFail;
            thread = new Thread(() => Run(Path.GetFullPath(path))) { IsBackground = true, Name = "Embris video" };
            thread.Start();
        }

        volatile int wantW, wantH; // window size: frames come out just big enough to cover it

        public void SetPaused(bool pause) { paused = pause; }
        public void Dispose() { stop = true; }
        public void SetHostSize(Size s) { if (s.Width > 0 && s.Height > 0) { wantW = s.Width; wantH = s.Height; } }

        // Output size for a w x h video: covers the window (or 960 wide before the window size is known).
        Size OutputSize(int w, int h)
        {
            double k = wantW > 0 ? Math.Max(wantW / (double)w, wantH / (double)h) : Math.Min(1.0, 960.0 / Math.Max(1, w));
            k = Math.Min(k, 3840.0 / Math.Max(1, w)); // a sensible cap; bigger windows scale the rest
            return new Size(Math.Max(2, ((int)Math.Ceiling(w * k) + 1) & ~1), Math.Max(2, ((int)Math.Ceiling(h * k) + 1) & ~1));
        }

        public double MinFrameMs = 1000.0 / 30; // at most 30 frames a second; less when the window is slow to paint

        void Run(string path)
        {
            Mf.MFStartup(0x00020070, 0);
            try
            {
                // The graphics card decodes when it can (much lighter on the processor); otherwise software.
                bool played = false;
                if (gpu)
                {
                    try { played = Play(path, true); }
                    catch (Exception) { }
                }
                if (!played && !stop) Play(path, false);
            }
            catch (Exception) { if (!stop) onFail(); } // no decoder for this format on this PC
            finally { Mf.MFShutdown(); }
        }

        // Returns false if hardware decoding didn't work out before the first frame.
        bool Play(string path, bool hardware)
        {
            Mf.IMFSourceReader reader = null;
            object manager = null;
            IntPtr device = IntPtr.Zero;
            bool gotFrame = false;
            try
            {
                Mf.IMFAttributes attrs;
                Mf.MFCreateAttributes(out attrs, 3);
                attrs.SetUINT32(Mf.EnableAdvancedVideoProcessing, 1); // decode + convert + resize
                if (hardware)
                {
                    device = Mf.CreateVideoDevice(card);
                    if (device == IntPtr.Zero) return false;
                    uint token;
                    Mf.IMFDXGIDeviceManager mgr;
                    Marshal.ThrowExceptionForHR(Mf.MFCreateDXGIDeviceManager(out token, out mgr));
                    Marshal.ThrowExceptionForHR(mgr.ResetDevice(device, token));
                    manager = mgr;
                    attrs.SetUnknown(Mf.SourceReaderD3DManager, mgr);
                    attrs.SetUINT32(Mf.EnableHardwareTransforms, 1);
                }
                Mf.MFCreateSourceReaderFromURL(path, attrs, out reader);
                reader.SetStreamSelection(0xFFFFFFFE, false); // all streams off...
                reader.SetStreamSelection(Mf.FirstVideoStream, true); // ...except video

                Mf.IMFMediaType native;
                reader.GetNativeMediaType(Mf.FirstVideoStream, 0, out native);
                ulong size;
                native.GetUINT64(Mf.MT_FrameSize, out size);
                int w = (int)(size >> 32), h = (int)(size & 0xFFFFFFFF);
                var outSize = OutputSize(w, h);
                int ow = outSize.Width, oh = outSize.Height, forW = wantW, forH = wantH;

                Mf.IMFMediaType type;
                Mf.MFCreateMediaType(out type);
                type.SetGUID(Mf.MT_MajorType, Mf.MediaTypeVideo);
                type.SetGUID(Mf.MT_Subtype, Mf.FormatRGB32);
                type.SetUINT64(Mf.MT_FrameSize, ((ulong)ow << 32) | (uint)oh);
                if (reader.SetCurrentMediaType(Mf.FirstVideoStream, IntPtr.Zero, type) < 0)
                {
                    if (hardware) return false;
                    ow = w; oh = h; // the converter can't resize this one: take the full size
                    type.SetUINT64(Mf.MT_FrameSize, ((ulong)ow << 32) | (uint)oh);
                    Marshal.ThrowExceptionForHR(reader.SetCurrentMediaType(Mf.FirstVideoStream, IntPtr.Zero, type));
                }

                var clock = System.Diagnostics.Stopwatch.StartNew();
                long loopOffset = 0, lastTs = 0;
                double lastShown = double.MinValue;
                while (!stop)
                {
                    if (paused) { Thread.Sleep(50); clock.Stop(); continue; }
                    if (!clock.IsRunning) clock.Start();
                    if ((wantW != forW || wantH != forH) && ow != w)
                    {
                        // The window was resized: have the video processor scale to the new size.
                        forW = wantW;
                        forH = wantH;
                        var s = OutputSize(w, h);
                        type.SetUINT64(Mf.MT_FrameSize, ((ulong)s.Width << 32) | (uint)s.Height);
                        if (reader.SetCurrentMediaType(Mf.FirstVideoStream, IntPtr.Zero, type) >= 0) { ow = s.Width; oh = s.Height; }
                        else type.SetUINT64(Mf.MT_FrameSize, ((ulong)ow << 32) | (uint)oh);
                    }
                    uint actual, flags;
                    long ts;
                    Mf.IMFSample sample;
                    int hr = reader.ReadSample(Mf.FirstVideoStream, 0, out actual, out flags, out ts, out sample);
                    if (hr < 0)
                    {
                        if (hardware && !gotFrame) return false;
                        Marshal.ThrowExceptionForHR(hr);
                    }
                    if ((flags & 2) != 0) // end of stream: start over
                    {
                        if (sample != null) Marshal.ReleaseComObject(sample);
                        var pv = new Mf.PropVariant { vt = 20, hVal = 0 };
                        var none = Guid.Empty;
                        reader.SetCurrentPosition(ref none, ref pv);
                        loopOffset += lastTs + 333333;
                        continue;
                    }
                    if (sample == null) continue;
                    lastTs = ts;
                    // Wait until this frame is due; drop it if we're behind or it comes too soon after the last one.
                    double dueMs = (loopOffset + ts) / 10000.0, nowMs = clock.Elapsed.TotalMilliseconds;
                    if (nowMs - dueMs > 80 || dueMs - lastShown < MinFrameMs - 2) { Marshal.ReleaseComObject(sample); continue; }
                    while (!stop && clock.Elapsed.TotalMilliseconds < dueMs) Thread.Sleep(2);
                    if (!Background.WantsFrame) { Marshal.ReleaseComObject(sample); continue; } // the window is busy: don't copy it out
                    var bmp = Copy(sample, ow, oh);
                    Marshal.ReleaseComObject(sample);
                    if (bmp == null) { if (hardware && !gotFrame) return false; continue; }
                    gotFrame = true;
                    lastShown = dueMs;
                    if (!stop) onFrame(bmp);
                }
                return true;
            }
            catch (Exception)
            {
                if (hardware && !gotFrame) return false;
                throw;
            }
            finally
            {
                if (reader != null) Marshal.ReleaseComObject(reader);
                if (manager != null) Marshal.ReleaseComObject(manager);
                if (device != IntPtr.Zero) Marshal.Release(device);
            }
        }
        static Bitmap Copy(Mf.IMFSample sample, int w, int h)
        {
            Mf.IMFMediaBuffer buffer;
            sample.ConvertToContiguousBuffer(out buffer);
            try
            {
                IntPtr data;
                int max, len;
                buffer.Lock(out data, out max, out len);
                try
                {
                    if (len < w * h * 4) return null;
                    var bmp = Backdrop.Take(w, h, PixelFormat.Format32bppRgb);
                    var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                    if (bd.Stride == w * 4) Mf.CopyMemory(bd.Scan0, data, (IntPtr)(w * h * 4));
                    else for (int y = 0; y < h; y++) Mf.CopyMemory(bd.Scan0 + y * bd.Stride, data + y * w * 4, (IntPtr)(w * 4));
                    bmp.UnlockBits(bd);
                    return bmp;
                }
                finally { buffer.Unlock(); }
            }
            finally { Marshal.ReleaseComObject(buffer); }
        }
    }

    // The few Media Foundation calls Embris needs (Windows 7 and later).
    static class Mf
    {
        public const uint FirstVideoStream = 0xFFFFFFFC;
        public static readonly Guid EnableAdvancedVideoProcessing = new Guid("0f81da2c-b537-4672-a8b2-a681b17307a3");
        public static readonly Guid MT_MajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid MT_Subtype = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MT_FrameSize = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid MediaTypeVideo = new Guid("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid FormatRGB32 = new Guid("00000016-0000-0010-8000-00AA00389B71");
        public static readonly Guid SourceReaderD3DManager = new Guid("ec822da2-e1e9-4b29-a0d8-563c719f5269");
        public static readonly Guid EnableHardwareTransforms = new Guid("a634a91c-822b-41b9-a494-4de4643612b0");

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] public static extern void CopyMemory(IntPtr dest, IntPtr src, IntPtr count);
        [DllImport("mfplat.dll")] public static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IMFDXGIDeviceManager manager);
        [DllImport("d3d11.dll")]
        static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint levelCount,
                                            uint sdkVersion, out IntPtr device, out int level, out IntPtr context);

        // A Direct3D 11 device that can decode video, shared safely with Media Foundation's threads.
        // ----- graphics cards (DXGI)

        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1([MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr factory);
        static readonly Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");

        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIFactory1
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
            void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
            [PreserveSig] int EnumAdapters1(uint index, out IntPtr adapter);
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIAdapter1
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
            void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
            [PreserveSig] int GetDesc1(out AdapterDesc1 desc);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct AdapterDesc1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId, DeviceId, SubSysId, Revision;
            public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public uint LuidLow; public int LuidHigh;
            public uint Flags; // 2 = software (Microsoft Basic Render Driver)
        }

        // Calls back with each real graphics card (name and its COM pointer, released afterwards).
        static void EachAdapter(Func<string, IntPtr, bool> visit)
        {
            IntPtr fptr;
            try { if (CreateDXGIFactory1(IID_IDXGIFactory1, out fptr) < 0) return; }
            catch (DllNotFoundException) { return; }
            var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(fptr);
            Marshal.Release(fptr);
            try
            {
                for (uint i = 0; ; i++)
                {
                    IntPtr a;
                    if (factory.EnumAdapters1(i, out a) < 0) break; // DXGI_ERROR_NOT_FOUND: no more
                    try
                    {
                        var adapter = (IDXGIAdapter1)Marshal.GetObjectForIUnknown(a);
                        AdapterDesc1 d;
                        bool real = adapter.GetDesc1(out d) >= 0 && (d.Flags & 2) == 0;
                        Marshal.ReleaseComObject(adapter);
                        if (real && !visit(d.Description.Trim(), a)) return;
                    }
                    finally { Marshal.Release(a); }
                }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }

        // Names of the graphics cards in this PC.
        public static List<string> GraphicsCards()
        {
            var names = new List<string>();
            try { EachAdapter((name, a) => { if (!names.Contains(name)) names.Add(name); return true; }); }
            catch (Exception) { }
            return names;
        }

        // A Direct3D 11 device that can decode video on the named graphics card (or the default one),
        // shared safely with Media Foundation's threads.
        public static IntPtr CreateVideoDevice(string card)
        {
            IntPtr device = IntPtr.Zero, context = IntPtr.Zero;
            int level;
            try
            {
                if (!string.IsNullOrEmpty(card))
                    EachAdapter((name, a) =>
                    {
                        if (name != card) return true;
                        // A specific adapter needs the "unknown" driver type.
                        if (D3D11CreateDevice(a, 0, IntPtr.Zero, 0x20 | 0x800, IntPtr.Zero, 0, 7, out device, out level, out context) < 0) device = IntPtr.Zero;
                        return false;
                    });
                // Hardware driver, BGRA + video support, SDK version 7.
                if (device == IntPtr.Zero && D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20 | 0x800, IntPtr.Zero, 0, 7, out device, out level, out context) < 0) return IntPtr.Zero;
            }
            catch (DllNotFoundException) { return IntPtr.Zero; }
            if (context != IntPtr.Zero) Marshal.Release(context);
            var mt = Marshal.GetObjectForIUnknown(device) as ID3D10Multithread;
            if (mt != null) { mt.SetMultithreadProtected(1); Marshal.ReleaseComObject(mt); }
            return device;
        }

        [ComImport, Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ID3D10Multithread
        {
            [PreserveSig] void Enter();
            [PreserveSig] void Leave();
            [PreserveSig] int SetMultithreadProtected(int protect);
            [PreserveSig] int GetMultithreadProtected();
        }

        [ComImport, Guid("eb533d5d-2db6-40f8-97a9-494692014f07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFDXGIDeviceManager
        {
            void CloseDeviceHandle(); void GetVideoService(); void LockDevice(); void OpenDeviceHandle();
            [PreserveSig] int ResetDevice(IntPtr device, uint resetToken);
        }

        [DllImport("mfplat.dll")] public static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] public static extern int MFShutdown();
        [DllImport("mfplat.dll")] public static extern int MFCreateAttributes(out IMFAttributes attrs, int size);
        [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IMFMediaType type);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        public static extern void MFCreateSourceReaderFromURL(string url, IMFAttributes attrs, out IMFSourceReader reader);

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        public struct PropVariant { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public long hVal; }

        // IMFAttributes: only the methods used are typed; the rest keep their places in the vtable.
        [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFAttributes
        {
            void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32();
            [PreserveSig] int GetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid key, out ulong value);
            void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
            void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem();
            void DeleteAllItems();
            [PreserveSig] int SetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
            [PreserveSig] int SetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid key, ulong value);
            void SetDouble();
            [PreserveSig] int SetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.LPStruct)] Guid value);
            void SetString(); void SetBlob();
            [PreserveSig] int SetUnknown([MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
            void LockStore(); void UnlockStore(); void GetCount();
            void GetItemByIndex(); void CopyAllItems();
        }

        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFMediaType
        {
            void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32();
            [PreserveSig] int GetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid key, out ulong value);
            void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
            void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem();
            void DeleteAllItems();
            [PreserveSig] int SetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
            [PreserveSig] int SetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid key, ulong value);
            void SetDouble();
            [PreserveSig] int SetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.LPStruct)] Guid value);
            void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount();
            void GetItemByIndex(); void CopyAllItems();
            // IMFMediaType's own methods follow (unused).
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFSample
        {
            void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64();
            void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
            void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem();
            void DeleteAllItems(); void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID(); void SetString();
            void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex();
            void CopyAllItems();
            void GetSampleFlags(); void SetSampleFlags(); void GetSampleTime(); void SetSampleTime(); void GetSampleDuration();
            void SetSampleDuration(); void GetBufferCount(); void GetBufferByIndex();
            void ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        }

        [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFMediaBuffer
        {
            void Lock(out IntPtr buffer, out int maxLength, out int currentLength);
            void Unlock();
        }

        [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFSourceReader
        {
            void GetStreamSelection();
            [PreserveSig] int SetStreamSelection(uint stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
            void GetNativeMediaType(uint stream, uint index, out IMFMediaType type);
            void GetCurrentMediaType();
            [PreserveSig] int SetCurrentMediaType(uint stream, IntPtr reserved, IMFMediaType type);
            [PreserveSig] int SetCurrentPosition(ref Guid format, ref PropVariant position);
            [PreserveSig] int ReadSample(uint stream, uint control, out uint actualStream, out uint flags, out long timestamp, out IMFSample sample);
        }
    }}
