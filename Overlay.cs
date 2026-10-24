// Calibration overlay: a click-through layer over the game with a crosshair at the centre and a faded
// "ghost" of the view when the recording started. Line the live view up with the ghost before
// playing, and every run starts from the same spot.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Embris
{
    // The Calibration page's preview: the crosshair or the uploaded cursor on a dark game-like square.
    class CursorPreview : UiControl
    {
        public Func<Image> GetImage;
        public Func<int> GetSize;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            var t = T;
            var r = new RectangleF(0.75f, 0.75f, Width - 1.5f, Height - 1.5f);
            Ui.FillRound(g, r, Ui.S(8), Ui.Blend(Color.FromArgb(18, 20, 24), t.Accent, 0.06f), t.Border, 1);
            using (var pen = new Pen(Color.FromArgb(28, 255, 255, 255)))
                for (int i = 1; i < 4; i++)
                {
                    g.DrawLine(pen, Width * i / 4f, Ui.S(6), Width * i / 4f, Height - Ui.S(6));
                    g.DrawLine(pen, Ui.S(6), Height * i / 4f, Width - Ui.S(6), Height * i / 4f);
                }
            CalibrationOverlay.DrawCrosshair(g, Width / 2f, Height / 2f, GetImage != null ? GetImage() : null, GetSize != null ? GetSize() : 32);
        }
    }

    class CalibrationOverlay : Form
    {
        Image ghost;
        string hint = "";
        float ghostOpacity = 0.25f;

        // How visible the start-view ghost is (0 = invisible, 1 = solid).
        public float GhostOpacity
        {
            get { return ghostOpacity; }
            set { ghostOpacity = Math.Max(0, Math.Min(1, value)); Render(); }
        }

        public CalibrationOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual; // topmost comes from CreateParams: the TopMost property would activate it
        }

        // Click-through, never takes focus from the game, not in Alt+Tab. Layered: the whole window is
        // one picture with real see-through pixels (see Render).
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8; // LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE | TOPMOST
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnPaintBackground(PaintEventArgs e) { } // Render() draws everything
        protected override void OnPaint(PaintEventArgs e) { }

        // Covers the game window (or the screen) and shows the ghost of the start view, if there is one.
        public void ShowOver(Rectangle area, string ghostFile, string message)
        {
            if (ghost != null) { ghost.Dispose(); ghost = null; }
            if (!string.IsNullOrEmpty(ghostFile) && File.Exists(ghostFile))
                using (var s = new MemoryStream(File.ReadAllBytes(ghostFile))) ghost = new Bitmap(Image.FromStream(s));
            hint = message;
            Bounds = area;
            if (!Visible) Show();
            Render();
        }

        [StructLayout(LayoutKind.Sequential)] struct Pt { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct Sz { public int W, H; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Blend { public byte Op, Flags, Alpha, Format; }
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr screenDc, ref Pt pos, ref Sz size, IntPtr srcDc, ref Pt srcPos, int key, ref Blend blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        // Draws the overlay into a picture with real transparency and hands it to Windows: a faint
        // ghost stays faint (with the game showing through) instead of turning dark.
        void Render()
        {
            if (!IsHandleCreated || !Visible || Width <= 0 || Height <= 0) return;
            using (var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    Draw(g);
                }
                IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
                IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
                try
                {
                    var pos = new Pt { X = Left, Y = Top };
                    var size = new Sz { W = Width, H = Height };
                    var src = new Pt();
                    var blend = new Blend { Op = 0, Flags = 0, Alpha = 255, Format = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
                    UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref blend, 2); // ULW_ALPHA
                }
                finally
                {
                    SelectObject(mem, old);
                    DeleteObject(hbmp);
                    DeleteDC(mem);
                    ReleaseDC(IntPtr.Zero, screen);
                }
            }
        }

        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Render(); }

        void Draw(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = Width / 2f, cy = Height / 2f;
            if (ghost != null && ghostOpacity > 0)
            {
                // The start view, faded, centred where it was captured.
                var r = new RectangleF(cx - ghost.Width / 2f, cy - ghost.Height / 2f, ghost.Width, ghost.Height);
                var fade = new System.Drawing.Imaging.ColorMatrix { Matrix33 = ghostOpacity };
                using (var attrs = new System.Drawing.Imaging.ImageAttributes())
                {
                    attrs.SetColorMatrix(fade);
                    g.DrawImage(ghost, Rectangle.Round(r), 0, 0, ghost.Width, ghost.Height, GraphicsUnit.Pixel, attrs);
                }
                using (var pen = new Pen(Color.FromArgb(200, Theme.Current.Accent), 2) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            }
            DrawCrosshair(g, cx, cy, CustomCursor, CursorSize);
            if (hint.Length > 0)
            {
                // GDI+ text (GDI text would punch holes in the transparency).
                var f = Ui.Semi(11f);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                var size = g.MeasureString(hint, f);
                var box = new RectangleF(cx - size.Width / 2 - Ui.S(12), Height - size.Height - Ui.S(60), size.Width + Ui.S(24), size.Height + Ui.S(12));
                using (var b = new SolidBrush(Color.FromArgb(230, 20, 22, 26))) g.FillRectangle(b, box);
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(hint, f, Brushes.White, box, sf);
            }
        }
        // The custom cursor drawn at the centre instead of the crosshair (null = the crosshair), and its size in pixels.
        public Image CustomCursor;
        public int CursorSize = 32;

        public void Redraw() { Render(); }

        // The crosshair, or a custom cursor image centred on (cx, cy). Also used by the preview on the Calibration page.
        public static void DrawCrosshair(Graphics g, float cx, float cy, Image custom, int size)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (custom != null)
            {
                float s = Ui.S(Math.Max(4, size)), w = s, h = s;
                if (custom.Width > custom.Height) h = s * custom.Height / custom.Width; else w = s * custom.Width / custom.Height;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(custom, cx - w / 2, cy - h / 2, w, h);
                return;
            }
            // Crosshair: dark outline so it shows on any background, accent lines and a centre dot.
            float arm = Ui.S(18), gap = Ui.S(5);
            foreach (var p in new[] { new { Color = Color.FromArgb(200, 10, 10, 12), Width = Ui.S(5f) }, new { Color = Theme.Current.Accent, Width = Ui.S(2.2f) } })
                using (var pen = new Pen(p.Color, p.Width) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, cx - arm, cy, cx - gap, cy);
                    g.DrawLine(pen, cx + gap, cy, cx + arm, cy);
                    g.DrawLine(pen, cx, cy - arm, cx, cy - gap);
                    g.DrawLine(pen, cx, cy + gap, cx, cy + arm);
                    g.DrawEllipse(pen, cx - Ui.S(1.5f), cy - Ui.S(1.5f), Ui.S(3), Ui.S(3));
                }
        }

        // Reads a cursor or picture (.cur, .ico, .png, .gif, .jpg, .bmp) into a bitmap with transparency.
        public static Bitmap LoadCursorImage(string file)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".ico")
                using (var icon = new Icon(file, 256, 256)) return icon.ToBitmap();
            if (ext == ".cur")
                using (var cur = new System.Windows.Forms.Cursor(file))
                {
                    var bmp = new Bitmap(cur.Size.Width, cur.Size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        cur.Draw(g, new Rectangle(Point.Empty, cur.Size));
                    }
                    return bmp;
                }
            using (var s = new MemoryStream(File.ReadAllBytes(file)))
            using (var img = Image.FromStream(s))
                return new Bitmap(img);
        }

        // Grabs the middle of an area of the screen (what the camera is looking at) and saves it.
        public static bool CaptureStartView(Rectangle area, string file)
        {
            try
            {
                int w = Math.Max(16, area.Width * 2 / 5), h = Math.Max(16, area.Height * 2 / 5);
                var src = new Point(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2);
                using (var bmp = new Bitmap(w, h))
                {
                    using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(src, Point.Empty, new Size(w, h));
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                }
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
