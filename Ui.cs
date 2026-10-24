// UI building blocks for Embris: theme colors and owner-drawn controls.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Embris")]
[assembly: System.Reflection.AssemblyProduct("Embris")]
[assembly: System.Reflection.AssemblyVersion("1.7.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.7.0.0")]

namespace Embris
{
    class Theme
    {
        public string Name;
        public Color Bg, Surface, Accent, Text, Border;

        public Theme(string name, Color bg, Color surface, Color accent, Color text, Color border)
        {
            Name = name; Bg = bg; Surface = surface; Accent = accent; Text = text; Border = border;
        }

        public Theme(string name, string bg, string surface, string accent, string text, string border)
            : this(name, Ui.ParseHex(bg), Ui.ParseHex(surface), Ui.ParseHex(accent), Ui.ParseHex(text), Ui.ParseHex(border)) { }

        public Theme Copy(string name) { return new Theme(name, Bg, Surface, Accent, Text, Border); }

        public bool IsLight { get { return Ui.Luma(Bg) > 0.5; } }
        public Color Sidebar { get { return Ui.Blend(Bg, Surface, 0.5f); } }
        public Color Input { get { return IsLight ? Ui.Blend(Surface, Bg, 0.55f) : Ui.Blend(Bg, Surface, 0.3f); } }
        public Color SubText { get { return Ui.Blend(Text, Surface, 0.4f); } }
        public Color OnAccent { get { return Ui.Luma(Accent) > 0.62 ? Color.FromArgb(20, 22, 28) : Color.White; } }

        public static readonly Theme[] Presets = {
            new Theme("Ember",    "151619", "202226", "E89545", "ECEDEF", "36383D"),
            new Theme("Ocean",    "0A1A24", "11293A", "1EC8E8", "EEF8FB", "24495F"),
            new Theme("Forest",   "0F1712", "18251C", "2FBF71", "EEF6F0", "2F4A38"),
            new Theme("Sunset",   "1A1114", "271A1F", "FF7A45", "FBF1EE", "4A2F36"),
            new Theme("Lavender", "15121F", "211C30", "A970FF", "F4F0FB", "3D3456"),
            new Theme("Light",    "F3F5F8", "FFFFFF", "1677FF", "1B2430", "D5DBE3"),
        };

        public static Theme Default { get { return Presets[0].Copy(Presets[0].Name); } }
        public static Theme Current = Default;
        public static event Action Changed;

        public static void Set(Theme t)
        {
            Current = t;
            if (Changed != null) Changed();
        }
    }

    static class Ui
    {
        public const string Version = "1.7.0"; // keep in step with the assembly attributes above

        public static readonly Color Danger = Color.FromArgb(229, 72, 77);
        public static readonly Color Success = Color.FromArgb(48, 196, 141);
        public static readonly Color Warning = Color.FromArgb(245, 166, 35);

        static float scale;
        public static float Scale
        {
            get
            {
                if (scale == 0) using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
                return scale;
            }
        }
        public static int S(float v) { return (int)Math.Round(v * Scale); }

        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        public static Font F(float size) { return GetFont("Segoe UI", size); }
        public static Font Semi(float size) { return GetFont("Segoe UI Semibold", size); }
        public static Font Icons(float size) { return GetFont("Segoe MDL2 Assets", size); }
        static Font GetFont(string family, float size)
        {
            string key = family + "|" + size;
            Font f;
            if (!fonts.TryGetValue(key, out f)) { f = new Font(family, size); fonts[key] = f; }
            return f;
        }

        public static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
        public static double Luma(Color c) { return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255; }
        public static string Hex(Color c) { return c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }
        public static Color ParseHex(string s)
        {
            int v = int.Parse(s.TrimStart('#'), NumberStyles.HexNumber);
            return Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
        }

        // Background color of the nearest container that defines one.
        public static Color BackOf(Control c)
        {
            for (; c != null; c = c.Parent)
            {
                var b = c as IBack;
                if (b != null) return b.BackFill;
            }
            return Theme.Current.Bg;
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, RectangleF r, float radius, Color fill, Color border, float borderWidth)
        {
            using (var path = Round(r, radius))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (borderWidth > 0) using (var pen = new Pen(border, borderWidth)) g.DrawPath(pen, path);
            }
        }

        public static void Text(Graphics g, string text, Font font, Rectangle r, Color color, TextFormatFlags extra)
        {
            TextRenderer.DrawText(g, text, font, r, color,
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | extra);
        }

        public static string KeyName(Keys k)
        {
            if (k >= Keys.D0 && k <= Keys.D9) return ((int)(k - Keys.D0)).ToString();
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return "Num " + (int)(k - Keys.NumPad0);
            switch (k)
            {
                case Keys.Space: return "Space";
                case Keys.Return: return "Enter";
                case Keys.Escape: return "Esc";
                case Keys.Back: return "Backspace";
                case Keys.ControlKey: case Keys.LControlKey: return "Ctrl";
                case Keys.RControlKey: return "Right Ctrl";
                case Keys.ShiftKey: case Keys.LShiftKey: return "Shift";
                case Keys.RShiftKey: return "Right Shift";
                case Keys.Menu: case Keys.LMenu: return "Alt";
                case Keys.RMenu: return "Right Alt";
                case Keys.LWin: return "Win";
                case Keys.RWin: return "Right Win";
                case Keys.Apps: return "Apps";
                case Keys.Capital: return "Caps Lock";
                case Keys.PageUp: return "Page Up";
                case Keys.PageDown: return "Page Down";
                case Keys.Decimal: return "Num .";
                case Keys.Add: return "Num +";
                case Keys.Subtract: return "Num -";
                case Keys.Multiply: return "Num *";
                case Keys.Divide: return "Num /";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.OemQuestion: return "/";
                case Keys.OemSemicolon: return ";";
                case Keys.OemQuotes: return "'";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemPipe: return "\\";
                case Keys.Oemtilde: return "`";
                default: return k.ToString();
            }
        }

        // Reads a key name as KeyName writes it ("Space", "F6", "Num 7", "E", ",") or a Keys name.
        public static bool TryParseKey(string name, out Keys key)
        {
            key = Keys.None;
            name = (name ?? "").Trim();
            if (name.Length == 0) return false;
            string brace = name.Trim('{', '}').Replace('_', ' '); // AutoHotkey style {Space}; Mac/Linux style page_up
            foreach (Keys k in Enum.GetValues(typeof(Keys)))
            {
                if ((int)k <= 0 || (int)k > 0xFE) continue;
                if (string.Equals(KeyName(k), brace, StringComparison.OrdinalIgnoreCase)) { key = k; return true; }
            }
            switch (brace.ToLowerInvariant())
            {
                case "esc": case "escape": key = Keys.Escape; return true;
                case "enter": case "return": key = Keys.Return; return true;
                case "ctrl": case "control": key = Keys.ControlKey; return true;
                case "shift": key = Keys.ShiftKey; return true;
                case "alt": case "menu": key = Keys.Menu; return true; // older scripts called Alt "Menu"
                case "win": case "cmd": key = Keys.LWin; return true;
                case "del": key = Keys.Delete; return true;
                case "pgup": key = Keys.PageUp; return true;
                case "pgdn": key = Keys.PageDown; return true;
                case "lbutton": case "rbutton": return false;
            }
            if (brace.Length == 1 && char.IsLetter(brace[0])) { key = (Keys)char.ToUpperInvariant(brace[0]); return true; }
            return Enum.TryParse(brace, true, out key) && (int)key > 0 && (int)key <= 0xFE;
        }

        // With Num Lock off, numpad keys arrive as Home/End/arrows etc. without the
        // "extended" flag; map them back so the numpad 7 key reads as Num 7.
        public static Keys NormalizeKey(Keys k, bool extended)
        {
            if (extended) return k;
            switch (k)
            {
                case Keys.Insert: return Keys.NumPad0;
                case Keys.End: return Keys.NumPad1;
                case Keys.Down: return Keys.NumPad2;
                case Keys.PageDown: return Keys.NumPad3;
                case Keys.Left: return Keys.NumPad4;
                case Keys.Clear: return Keys.NumPad5;
                case Keys.Right: return Keys.NumPad6;
                case Keys.Home: return Keys.NumPad7;
                case Keys.Up: return Keys.NumPad8;
                case Keys.PageUp: return Keys.NumPad9;
                case Keys.Delete: return Keys.Decimal;
                default: return k;
            }
        }

        // App icon and logo are embedded in the exe by build.bat.
        static Icon appIcon;
        static Image logo;
        public static Icon AppIcon
        {
            get
            {
                if (appIcon == null)
                    using (var s = typeof(Ui).Assembly.GetManifestResourceStream("Embris.ico")) appIcon = new Icon(s);
                return appIcon;
            }
        }
        public static Image LogoImage
        {
            get
            {
                if (logo == null)
                    using (var s = typeof(Ui).Assembly.GetManifestResourceStream("embris-logo.png")) logo = new Bitmap(Image.FromStream(s));
                return logo;
            }
        }
    }

    interface IBack { Color BackFill { get; } }

    // A custom background behind the whole window. Pages, the sidebar and cards become see-through
    // tints over it; every control paints the part of the picture behind itself first.
    //
    // For speed, each frame is put together once with every see-through layer already tinted in
    // (on the video thread for videos), and controls then just copy the part behind them.
    static class Backdrop
    {
        static Bitmap frame;  // current frame: scaled to cover the window, layers tinted in
        static Bitmap plain;  // the same frame without tints, kept to re-tint after the layout changes
        public static int Dim = 55; // how much the page darkens the picture, in %
        public static bool Active { get { return frame != null; } }
        public static int CardAlpha { get { return SolidCards ? 255 : 210; } }
        // Solid cards: the video only shows around the cards, so their contents don't need redrawing
        // on every frame (much smoother in a big window).
        public static bool SolidCards;

        // See-through layers (pages, sidebar, bars, cards) in window coordinates, outermost first.
        struct Layer { public Rectangle Bounds; public Color Tint; public bool Round; }
        static Layer[] layers = new Layer[0];
        static Control root;
        static bool layoutDirty = true;
        public static event Action Retinted; // the layout changed and the frame was re-tinted: repaint

        public static void Attach(Control form) { root = form; }

        // Called by boxes when they move, resize, show or hide.
        public static void LayoutChanged()
        {
            if (layoutDirty || root == null || !root.IsHandleCreated) { layoutDirty = true; return; }
            layoutDirty = true;
            try { root.BeginInvoke((Action)Refresh); } catch (InvalidOperationException) { }
        }

        // Re-reads the layers and re-tints the current frame (window thread).
        public static void Refresh()
        {
            if (!layoutDirty) return;
            ReadLayers();
            Bitmap p = plain;
            if (p == null) return;
            Bitmap f;
            lock (p) f = Compose(p);
            if (f == null) return;
            var old = frame;
            frame = f;
            if (old != null) old.Dispose();
            if (Retinted != null) Retinted();
        }

        static void ReadLayers()
        {
            layoutDirty = false;
            var list = new List<Layer>();
            if (root != null) Collect(root, list);
            layers = list.ToArray();
        }

        static void Collect(Control parent, List<Layer> list)
        {
            var t = Theme.Current;
            foreach (Control c in parent.Controls)
            {
                var b = c as Box;
                if (b == null || !b.Visible) continue;
                var r = root.RectangleToClient(b.RectangleToScreen(b.ClientRectangle));
                switch (b.Role)
                {
                    case BoxRole.Page: list.Add(new Layer { Bounds = r, Tint = Color.FromArgb(Dim * 255 / 100, t.Bg) }); break;
                    case BoxRole.Sidebar:
                    case BoxRole.Bar: list.Add(new Layer { Bounds = r, Tint = Color.FromArgb(Math.Max(Dim, 72) * 255 / 100, t.Sidebar) }); break;
                    case BoxRole.Card: list.Add(new Layer { Bounds = r, Tint = Color.FromArgb(CardAlpha, t.Surface), Round = true }); break;
                }
                Collect(b, list);
            }
        }

        // Spare window-sized frames: a video makes several a second, so finished ones are reused
        // instead of allocating (and zeroing) new full-window pictures every time.
        static readonly List<Bitmap> spare = new List<Bitmap>();

        // A frame of the given size and format, recycled if there is one. Any thread.
        public static Bitmap Take(int w, int h, System.Drawing.Imaging.PixelFormat format)
        {
            lock (spare)
                for (int i = spare.Count - 1; i >= 0; i--)
                {
                    var b = spare[i];
                    if (b.Width != w || b.Height != h || b.PixelFormat != format) continue;
                    spare.RemoveAt(i);
                    return b;
                }
            return new Bitmap(w, h, format);
        }

        // Frees the spare frames (the background stopped or was unloaded).
        public static void ClearSpares()
        {
            lock (spare)
            {
                foreach (var b in spare) b.Dispose();
                spare.Clear();
            }
        }

        // Hands a frame back for reuse (keeps a couple; the rest are freed).
        public static void Give(Bitmap b)
        {
            if (b == null) return;
            Bitmap drop = null;
            lock (spare)
            {
                spare.Add(b);
                if (spare.Count > 2) { drop = spare[0]; spare.RemoveAt(0); }
            }
            if (drop != null) drop.Dispose();
        }

        // Tints a scaled frame with the current layers. Safe to call from the video thread.
        public static Bitmap Compose(Bitmap scaled)
        {
            if (scaled == null) return null;
            var ls = layers;
            var bmp = Take(scaled.Width, scaled.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(scaled, 0, 0);
                g.CompositingMode = CompositingMode.SourceOver;
                foreach (var l in ls)
                {
                    using (var br = new SolidBrush(l.Tint))
                    {
                        if (!l.Round) { g.FillRectangle(br, l.Bounds); continue; }
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        using (var path = Ui.Round(new RectangleF(l.Bounds.X + 0.5f, l.Bounds.Y + 0.5f, l.Bounds.Width - 1, l.Bounds.Height - 1), Ui.S(10)))
                            g.FillPath(br, path);
                        g.SmoothingMode = SmoothingMode.None;
                    }
                }
            }
            return bmp;
        }

        // Takes a new scaled frame (untinted) and, if given, its already tinted version.
        public static void SetFrame(Bitmap scaled, Bitmap composed)
        {
            if (scaled != null && layoutDirty) { ReadLayers(); if (composed != null) composed.Dispose(); composed = null; }
            if (scaled != null && composed == null) composed = Compose(scaled);
            var oldPlain = plain;
            var oldFrame = frame;
            plain = scaled;
            frame = composed;
            if (oldPlain != null && oldPlain != scaled) lock (oldPlain) Give(oldPlain);
            if (oldFrame != null && oldFrame != composed) Give(oldFrame);
        }

        public static void SetFrame(Bitmap scaled) { SetFrame(scaled, null); }

        // Theme or darkness changed: tint again.
        public static void Retint()
        {
            layoutDirty = true;
            Refresh();
        }

        // Scales a picture to fill the given size, cropping the overflow (like a desktop wallpaper).
        public static Bitmap Cover(Image src, Size size)
        {
            if (size.Width <= 0 || size.Height <= 0) return null;
            var bmp = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.PixelOffsetMode = PixelOffsetMode.Half;
                float scale = Math.Max(size.Width / (float)src.Width, size.Height / (float)src.Height);
                if (Math.Abs(scale - 1) < 0.01f)
                {
                    // Already the right size (videos are scaled by the graphics card): just crop the middle.
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.DrawImage(src, new Rectangle(0, 0, size.Width, size.Height),
                        new Rectangle((src.Width - size.Width) / 2, (src.Height - size.Height) / 2, size.Width, size.Height), GraphicsUnit.Pixel);
                    return bmp;
                }
                g.InterpolationMode = InterpolationMode.Bilinear;
                float w = src.Width * scale, h = src.Height * scale;
                g.DrawImage(src, (size.Width - w) / 2, (size.Height - h) / 2, w, h);
            }
            return bmp;
        }

        // Copies the (already tinted) part of the frame behind a control.
        public static void PaintBehind(Graphics g, Control c)
        {
            var f = frame;
            // Only controls in the main window sit on the background; others (like the color picker) are solid.
            if (f == null || root == null || c.FindForm() != root) { g.Clear(Ui.BackOf(c.Parent)); return; }
            Point p = root.PointToClient(c.PointToScreen(Point.Empty));
            var old = g.CompositingMode;
            g.CompositingMode = CompositingMode.SourceCopy;
            var rs = root.ClientSize;
            if (f.Width == rs.Width && f.Height == rs.Height)
                g.DrawImage(f, new Rectangle(0, 0, c.Width, c.Height), new Rectangle(p.X, p.Y, c.Width, c.Height), GraphicsUnit.Pixel);
            else if (rs.Width > 0 && rs.Height > 0)
            {
                // The window is being resized and the frame hasn't caught up yet: stretch it to fit.
                float sx = f.Width / (float)rs.Width, sy = f.Height / (float)rs.Height;
                var oldMode = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(f, new RectangleF(0, 0, c.Width, c.Height), new RectangleF(p.X * sx, p.Y * sy, c.Width * sx, c.Height * sy), GraphicsUnit.Pixel);
                g.InterpolationMode = oldMode;
            }
            g.CompositingMode = old;
        }
    }

    enum BoxRole { Page, Sidebar, Card, Bar, Inner }

    // Panel that paints a themed page, sidebar, bottom bar or rounded card.
    class Box : Panel, IBack
    {
        public BoxRole Role;
        public string Title;

        public Box(BoxRole role)
        {
            Role = role;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            if (role != BoxRole.Inner)
            {
                VisibleChanged += (s, e) => Backdrop.LayoutChanged();
                SizeChanged += (s, e) => Backdrop.LayoutChanged();
                LocationChanged += (s, e) => Backdrop.LayoutChanged();
            }
        }

        public Color BackFill
        {
            get
            {
                var t = Theme.Current;
                switch (Role)
                {
                    case BoxRole.Card:
                    case BoxRole.Inner: return t.Surface;
                    case BoxRole.Page: return t.Bg;
                    default: return t.Sidebar;
                }
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var t = Theme.Current;
            var g = e.Graphics;
            if (Role == BoxRole.Card)
            {
                bool see = Backdrop.Active;
                if (see) Backdrop.PaintBehind(g, this); // the card's tint is already in the frame
                else g.Clear(Ui.BackOf(Parent));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Ui.FillRound(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Ui.S(10),
                    see ? Color.Transparent : t.Surface, t.Border, 1);
                if (Title != null)
                    TextRenderer.DrawText(g, Title, Ui.Semi(11f), new Point(Ui.S(18), Ui.S(14)), Enabled ? t.Text : t.SubText,
                        TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                return;
            }
            if (Backdrop.Active) Backdrop.PaintBehind(g, this);
            else g.Clear(BackFill);
            if (Role == BoxRole.Inner) return;
            using (var pen = new Pen(t.Border))
            {
                if (Role == BoxRole.Sidebar) g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
                if (Role == BoxRole.Bar) g.DrawLine(pen, 0, 0, Width, 0);
            }
        }

        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    }

    class UiControl : Control
    {
        protected bool hover, pressed;

        public UiControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Font = Ui.F(9.75f);
        }

        protected Theme T { get { return Theme.Current; } }
        protected Color BackFill { get { return Ui.BackOf(Parent); } }
        protected Color Dim(Color c) { return Enabled ? c : Ui.Blend(c, BackFill, 0.55f); }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Backdrop.Active) Backdrop.PaintBehind(e.Graphics, this);
            else e.Graphics.Clear(BackFill);
        }

        protected static Graphics Smooth(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            return e.Graphics;
        }
    }

    class TextLabel : UiControl
    {
        public bool Sub, Wrap, Faint;
        public Color? TextColor; // overrides the theme color (e.g. for errors)

        protected override void OnPaint(PaintEventArgs e)
        {
            Color c = TextColor.HasValue ? TextColor.Value : Faint ? Ui.Blend(T.SubText, BackFill, 0.4f) : Sub ? T.SubText : T.Text;
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding |
                        (Wrap ? TextFormatFlags.WordBreak : TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Dim(c), flags);
        }
    }

    class Logo : UiControl
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            int sz = Math.Min(Ui.S(36), Height);
            g.DrawImage(Ui.LogoImage, new Rectangle(0, (Height - sz) / 2, sz, sz));
            if (Width > sz + Ui.S(40)) // just the icon when squeezed
                Ui.Text(g, Text, Ui.Semi(sz < Ui.S(34) ? 11.5f : 13f), new Rectangle(sz + Ui.S(10), 0, Width - sz - Ui.S(10), Height), T.Text, 0);
        }
    }

    enum BtnStyle { Primary, Secondary, Chip, Danger }

    partial class RoundButton : UiControl
    {
        public BtnStyle Style = BtnStyle.Secondary;
        public string Icon;
        public bool Selected;
        public bool RecordDot; // draws a red dot as the icon
        public bool PinIcon;   // draws an upright pushpin, filled when Selected
        public bool MenuIcon;  // draws three centred lines (the sidebar's collapse button)
        public bool MagnetIcon; // draws a horseshoe magnet (snapping)

        public RoundButton() { Cursor = Cursors.Hand; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            Color fill, border, fore;
            switch (Style)
            {
                case BtnStyle.Primary: fill = border = T.Accent; fore = T.OnAccent; break;
                case BtnStyle.Danger: fill = border = Ui.Danger; fore = Color.White; break;
                case BtnStyle.Chip:
                    fill = Selected ? Ui.Blend(T.Input, T.Accent, 0.25f) : T.Input;
                    border = Selected ? T.Accent : T.Border;
                    fore = Selected ? T.Text : T.SubText;
                    break;
                default: fill = T.Input; border = T.Border; fore = T.Text; break;
            }
            bool solid = Style == BtnStyle.Primary || Style == BtnStyle.Danger;
            if (pressed) fill = Ui.Blend(fill, System.Drawing.Color.Black, 0.12f);
            else if (hover) fill = Ui.Blend(fill, solid ? System.Drawing.Color.White : T.Text, 0.08f);

            Ui.FillRound(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Ui.S(8), Dim(fill), Dim(border), 1);

            if (PinIcon)
            {
                DrawPin(g, Width / 2f, Height / 2f, Dim(Selected ? T.Accent : T.SubText), Selected);
                return;
            }
            if (MagnetIcon)
            {
                // A horseshoe magnet: a thick U with light tips.
                float u = Ui.Scale, cx = Width / 2f, cy = Height / 2f + 1 * u;
                using (var pen = new Pen(Dim(Selected ? T.Accent : fore), 3.2f * u))
                {
                    g.DrawArc(pen, cx - 6 * u, cy - 6 * u, 12 * u, 12 * u, 0, 180);
                    g.DrawLine(pen, cx - 6 * u, cy, cx - 6 * u, cy - 7 * u);
                    g.DrawLine(pen, cx + 6 * u, cy, cx + 6 * u, cy - 7 * u);
                }
                using (var tip = new Pen(Dim(T.Text), 3.2f * u))
                {
                    g.DrawLine(tip, cx - 6 * u, cy - 7 * u, cx - 6 * u, cy - 10 * u);
                    g.DrawLine(tip, cx + 6 * u, cy - 7 * u, cx + 6 * u, cy - 10 * u);
                }
                return;
            }
            if (MenuIcon)
            {
                // Drawn rather than taken from the icon font, whose glyph sits low in the button.
                float u = Ui.Scale, cx = Width / 2f, cy = Height / 2f;
                using (var pen = new Pen(Dim(fore), 1.5f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    for (int i = -1; i <= 1; i++)
                        g.DrawLine(pen, cx - 7 * u, cy + i * 5 * u, cx + 7 * u, cy + i * 5 * u);
                return;
            }

            var iconFont = Ui.Icons(Font.Size);
            if (string.IsNullOrEmpty(Text) && Icon != null && !RecordDot)
            {
                // Icon-only button: centre the glyph by its real drawn size.
                var sz = TextRenderer.MeasureText(g, Icon, iconFont, Size.Empty, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, Icon, iconFont, new Point((Width - sz.Width) / 2 + 1, (Height - sz.Height) / 2 + 1), Dim(fore),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                return;
            }
            int iconW = RecordDot ? Ui.S(12) : Icon == null ? 0 : TextRenderer.MeasureText(Icon, iconFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            int gap = iconW == 0 ? 0 : Ui.S(10);
            int textW = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            if (string.IsNullOrEmpty(Text)) gap = 0; // icon-only button
            bool showIcon = iconW > 0 && (textW == 0 || iconW + gap + textW <= Width - Ui.S(16)); // drop the icon when squeezed
            if (!showIcon) iconW = gap = 0;
            int x = (Width - iconW - gap - textW) / 2;
            if (showIcon && RecordDot)
                using (var b = new SolidBrush(Dim(Style == BtnStyle.Danger ? System.Drawing.Color.White : Ui.Danger)))
                    g.FillEllipse(b, x, (Height - iconW) / 2f, iconW, iconW);
            else if (showIcon) Ui.Text(g, Icon, iconFont, new Rectangle(x, 1, iconW + 2, Height), Dim(fore), 0);
            Ui.Text(g, Text, Font, new Rectangle(x + iconW + gap, 0, textW + 2, Height), Dim(fore), 0);
        }
    }

    partial class RoundButton
    {
        // Upright pushpin centred on (cx, cy): rounded head, tapered body, wide collar, then the needle.
        static void DrawPin(Graphics g, float cx, float cy, Color color, bool filled)
        {
            float u = Ui.Scale;
            float top = cy - 8 * u;
            using (var path = new GraphicsPath())
            {
                path.AddLine(cx - 3.5f * u, top, cx + 3.5f * u, top);
                path.AddLine(cx + 3.5f * u, top, cx + 3 * u, top + 6 * u);
                path.AddLine(cx + 3 * u, top + 6 * u, cx + 6 * u, top + 9.5f * u);
                path.AddLine(cx + 6 * u, top + 9.5f * u, cx - 6 * u, top + 9.5f * u);
                path.AddLine(cx - 6 * u, top + 9.5f * u, cx - 3 * u, top + 6 * u);
                path.CloseFigure();
                using (var pen = new Pen(color, 1.4f * u) { LineJoin = LineJoin.Round })
                {
                    if (filled) using (var b = new SolidBrush(color)) g.FillPath(b, path);
                    g.DrawPath(pen, path);
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, cx, top + 9.5f * u, cx, top + 16 * u);
                }
            }
        }
    }

    class NavButton : UiControl
    {
        public string Icon;
        public bool Selected;

        public NavButton() { Cursor = Cursors.Hand; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            var bg = BackFill;
            if (Selected || hover)
                Ui.FillRound(g, new RectangleF(0, 0, Width, Height), Ui.S(8), Ui.Blend(bg, T.Text, Selected ? 0.08f : 0.04f), bg, 0);
            if (Selected)
                Ui.FillRound(g, new RectangleF(0, Ui.S(11), Ui.S(3), Height - Ui.S(22)), Ui.S(1.5f), T.Accent, T.Accent, 0);
            bool iconOnly = Width < Ui.S(80); // collapsed sidebar
            Ui.Text(g, Icon, Ui.Icons(12f), new Rectangle(iconOnly ? 0 : Ui.S(16), 1, iconOnly ? Width : Ui.S(26), Height), Selected ? T.Accent : T.SubText,
                iconOnly ? TextFormatFlags.HorizontalCenter : 0);
            if (!iconOnly)
                Ui.Text(g, Text, Selected ? Ui.Semi(10f) : Ui.F(10f), new Rectangle(Ui.S(52), 0, Width - Ui.S(56), Height),
                    Selected ? T.Text : T.SubText, 0);
        }
    }

    class Toggle : UiControl
    {
        bool isOn;
        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return isOn; }
            set
            {
                if (isOn == value) return;
                isOn = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public Toggle() { Cursor = Cursors.Hand; }

        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            float h = Ui.S(22), w = Ui.S(40), y = (Height - h) / 2;
            var r = new RectangleF(0.5f, y, w, h);
            Color track = isOn ? T.Accent : T.Input;
            if (hover) track = Ui.Blend(track, T.Text, 0.08f);
            Ui.FillRound(g, r, h / 2, Dim(track), Dim(isOn ? T.Accent : T.Border), 1);
            float k = h - Ui.S(8);
            float kx = isOn ? r.Right - Ui.S(4) - k : r.X + Ui.S(4);
            using (var b = new SolidBrush(Dim(isOn ? T.OnAccent : T.SubText))) g.FillEllipse(b, kx, y + Ui.S(4), k, k);
            int tx = (int)w + Ui.S(10);
            Ui.Text(g, Text, Font, new Rectangle(tx, 0, Width - tx, Height), Dim(T.Text), 0);
        }
    }

    // Base for focusable controls drawn as a rounded input field.
    class InputBase : UiControl
    {
        public InputBase()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }

        protected void PaintFrame(Graphics g)
        {
            Color border = Focused ? T.Accent : hover ? Ui.Blend(T.Border, T.Text, 0.2f) : T.Border;
            Ui.FillRound(g, new RectangleF(0.75f, 0.75f, Width - 1.5f, Height - 1.5f), Ui.S(6), Dim(T.Input), Dim(border), Focused ? 1.5f : 1f);
        }
    }

    // Number field: a real text box (click to place the cursor, select, edit), with arrows on the
    // right; ↑/↓ and the mouse wheel step the value; Enter or leaving the field applies it.
    class NumberBox : InputBase
    {
        readonly TextBox box = new TextBox { BorderStyle = BorderStyle.None };
        decimal val, min, max = 100, step = 1;
        int decimals;
        public event EventHandler ValueChanged;
        public static event Action<bool> Editing; // any number field gains (true) or loses (false) the cursor

        public decimal Minimum { get { return min; } set { min = value; } }
        public decimal Maximum { get { return max; } set { max = value; } }
        public decimal Increment { get { return step; } set { step = value; } }
        public int DecimalPlaces { get { return decimals; } set { decimals = value; ShowValue(); } }
        public string Suffix; // shown after the number ("x" for speeds); the number then drops trailing zeros

        public NumberBox()
        {
            box.Font = Font;
            box.GotFocus += (s, e) =>
            {
                box.Text = Plain;  // no suffix while editing
                BeginInvoke((Action)box.SelectAll); // select it all, so typing replaces the value
                Invalidate();
                if (Editing != null) Editing(true);
            };
            box.LostFocus += (s, e) => { Commit(); Invalidate(); if (Editing != null) Editing(false); };
            box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
                {
                    Commit();
                    Value += e.KeyCode == Keys.Up ? step : -step;
                    box.Text = Plain;
                    box.SelectAll();
                    e.Handled = e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Enter) { Commit(); box.Text = Plain; box.SelectAll(); e.Handled = e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) { box.Text = Plain; box.SelectAll(); e.Handled = e.SuppressKeyPress = true; }
            };
            box.KeyPress += (s, e) =>
            {
                char c = e.KeyChar;
                bool ok = char.IsDigit(c) || char.IsControl(c) || (c == '.' && decimals > 0) || (c == '-' && min < 0);
                if (!ok) e.Handled = true;
            };
            box.MouseWheel += (s, e) => { Commit(); Value += e.Delta > 0 ? step : -step; box.Text = Plain; };
            Controls.Add(box);
            ShowValue();
        }

        public decimal Value
        {
            get { return val; }
            set
            {
                decimal v = Math.Round(Math.Max(min, Math.Min(max, value)), decimals);
                if (v == val) return;
                val = v;
                ShowValue();
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        string Plain
        {
            get
            {
                return Suffix != null ? val.ToString("0." + new string('#', Math.Max(1, decimals)), CultureInfo.InvariantCulture)
                                      : val.ToString("F" + decimals, CultureInfo.InvariantCulture);
            }
        }

        void ShowValue()
        {
            if (box.Focused) return; // don't fight the person typing
            string text = Plain + (Suffix ?? "");
            if (box.Text != text) box.Text = text;
        }

        // Applies what was typed (if it's a number).
        public void Commit()
        {
            string s = box.Text.Trim();
            if (Suffix != null && s.EndsWith(Suffix.Trim())) s = s.Substring(0, s.Length - Suffix.Trim().Length).Trim();
            decimal v;
            if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out v)) Value = v;
            if (!box.Focused) ShowValue();
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); box.Focus(); }
        protected override void OnFontChanged(EventArgs e) { box.Font = Font; base.OnFontChanged(e); PerformLayout(); }
        protected override void OnEnabledChanged(EventArgs e) { box.Enabled = Enabled; base.OnEnabledChanged(e); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int h = box.PreferredHeight;
            box.SetBounds(Ui.S(10), (Height - h) / 2, Math.Max(Ui.S(10), Width - Ui.S(40)), h);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && e.X >= Width - Ui.S(26))
            {
                Commit();
                Value += e.Y < Height / 2 ? step : -step;
                if (box.Focused) { box.Text = Plain; box.SelectAll(); }
            }
            else box.Focus();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (box.Focused) { Commit(); Value += e.Delta > 0 ? step : -step; box.Text = Plain; }
            base.OnMouseWheel(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            bool focused = box.Focused;
            Color border = focused ? T.Accent : hover ? Ui.Blend(T.Border, T.Text, 0.2f) : T.Border;
            Ui.FillRound(g, new RectangleF(0.75f, 0.75f, Width - 1.5f, Height - 1.5f), Ui.S(6), Dim(T.Input), Dim(border), focused ? 1.5f : 1f);
            if (box.BackColor != Dim(T.Input)) box.BackColor = Dim(T.Input);
            if (box.ForeColor != T.Text) box.ForeColor = T.Text;
            int ax = Width - Ui.S(26);
            Color arrow = Dim(hover ? T.Text : T.SubText);
            Ui.Text(g, "\uE70E", Ui.Icons(7f), new Rectangle(ax, Ui.S(3), Ui.S(20), Height / 2 - Ui.S(3)), arrow, TextFormatFlags.HorizontalCenter);
            Ui.Text(g, "\uE70D", Ui.Icons(7f), new Rectangle(ax, Height / 2, Ui.S(20), Height / 2 - Ui.S(3)), arrow, TextFormatFlags.HorizontalCenter);
        }
    }
    // Records the next key pressed while focused.
    class KeyBox : InputBase
    {
        Keys key;
        public event EventHandler KeyChanged;
        public Keys Key { get { return key; } set { key = value; Invalidate(); } }
        public string Caption; // shown instead of "None" when no key is set

        bool extendedKey;

        protected override bool IsInputKey(Keys k) { return true; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x100 || m.Msg == 0x104) extendedKey = (((long)m.LParam >> 24) & 1) != 0; // WM_(SYS)KEYDOWN
            base.WndProc(ref m);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            Keys k = Ui.NormalizeKey(e.KeyCode, extendedKey);
            if (k == Keys.ShiftKey || k == Keys.ControlKey || k == Keys.Menu) return;
            key = k;
            Invalidate();
            if (KeyChanged != null) KeyChanged(this, EventArgs.Empty);
            var f = FindForm();
            if (f != null) f.ActiveControl = null;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            PaintFrame(g);
            Ui.Text(g, "", Ui.Icons(11f), new Rectangle(Ui.S(11), 1, Ui.S(22), Height), Dim(Focused ? T.Accent : T.SubText), 0);
            var tr = new Rectangle(Ui.S(40), 0, Width - Ui.S(48), Height);
            if (Focused) Ui.Text(g, "Press a key...", Font, tr, T.SubText, 0);
            else if (key == Keys.None && Caption != null) Ui.Text(g, Caption, Font, tr, Dim(T.Text), 0);
            else Ui.Text(g, Ui.KeyName(key), Ui.Semi(Font.Size), tr, Dim(T.Text), 0);
        }
    }

    class Dropdown : InputBase
    {
        public readonly List<object> Items = new List<object>();
        int index = -1;
        public event EventHandler SelectedIndexChanged, Opening;

        public int SelectedIndex
        {
            get { return index; }
            set
            {
                if (value == index) return;
                index = value;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public object SelectedItem { get { return index >= 0 && index < Items.Count ? Items[index] : null; } }

        protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down && index < Items.Count - 1) SelectedIndex = index + 1;
            else if (e.KeyCode == Keys.Up && index > 0) SelectedIndex = index - 1;
            else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) Open();
            base.OnKeyDown(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) Open();
        }

        void Open()
        {
            if (Opening != null) Opening(this, EventArgs.Empty);
            var menu = new ContextMenuStrip
            {
                ShowImageMargin = false, ShowCheckMargin = false, Renderer = new UiMenuRenderer(),
                Font = Font, Padding = new Padding(Ui.S(3)), DropShadowEnabled = true
            };
            for (int i = 0; i < Items.Count; i++)
            {
                int n = i;
                var item = new ToolStripMenuItem(Items[i].ToString()) { AutoSize = false, Tag = i == index };
                item.Size = new Size(Width - Ui.S(8), Ui.S(34));
                item.Click += (s, e) => SelectedIndex = n;
                menu.Items.Add(item);
            }
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.Show(this, new Point(0, Height + Ui.S(4)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            PaintFrame(g);
            var item = SelectedItem;
            Ui.Text(g, item == null ? "" : item.ToString(), Font, new Rectangle(Ui.S(12), 0, Width - Ui.S(44), Height), Dim(T.Text), 0);
            Ui.Text(g, "", Ui.Icons(8f), new Rectangle(Width - Ui.S(30), 1, Ui.S(18), Height), Dim(T.SubText), TextFormatFlags.HorizontalCenter);
        }
    }

    // Single-line text input: a borderless TextBox inside a themed frame.
    class TextField : UiControl
    {
        readonly TextBox box = new TextBox { BorderStyle = BorderStyle.None };
        public string Placeholder;

        public TextField()
        {
            box.Font = Font;
            box.GotFocus += (s, e) => Invalidate();
            box.LostFocus += (s, e) => Invalidate();
            Controls.Add(box);
        }

        public override string Text { get { return box.Text; } set { box.Text = value; } }
        public TextBox Box { get { return box; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (Placeholder != null) SendMessage(box.Handle, 0x1501, (IntPtr)1, Placeholder); // EM_SETCUEBANNER
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int h = box.PreferredHeight;
            box.SetBounds(Ui.S(10), (Height - h) / 2, Width - Ui.S(20), h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            bool focused = box.Focused;
            Ui.FillRound(g, new RectangleF(0.75f, 0.75f, Width - 1.5f, Height - 1.5f), Ui.S(6), Dim(T.Input),
                Dim(focused ? T.Accent : T.Border), focused ? 1.5f : 1f);
            if (box.BackColor != Dim(T.Input)) box.BackColor = Dim(T.Input);
            if (box.ForeColor != T.Text) box.ForeColor = T.Text;
        }
    }

    // Scrollable list of names with one selected row.
    // Multi-line text editor: a borderless TextBox inside a themed frame, with dark scrollbars in dark themes.
    class TextArea : UiControl
    {
        readonly TextBox box = new TextBox
        {
            BorderStyle = BorderStyle.None, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            AcceptsTab = true, AcceptsReturn = true
        };
        bool? themedDark;

        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string appName, string idList);

        public TextArea()
        {
            box.GotFocus += (s, e) => Invalidate();
            box.LostFocus += (s, e) => Invalidate();
            Controls.Add(box);
        }

        public override string Text { get { return box.Text; } set { box.Text = value; } }
        public TextBox Box { get { return box; } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            box.SetBounds(Ui.S(8), Ui.S(6), Width - Ui.S(12), Height - Ui.S(10));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            Ui.FillRound(g, new RectangleF(0.75f, 0.75f, Width - 1.5f, Height - 1.5f), Ui.S(6), T.Input,
                box.Focused ? T.Accent : T.Border, box.Focused ? 1.5f : 1f);
            if (box.BackColor != T.Input) box.BackColor = T.Input;
            if (box.ForeColor != T.Text) box.ForeColor = T.Text;
            bool dark = !T.IsLight;
            if (themedDark != dark && box.IsHandleCreated)
            {
                themedDark = dark;
                SetWindowTheme(box.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
            }
        }
    }

    // Scrollable list of rows. With MultiSelect, Shift+click or Shift+arrows selects a range.
    class ItemList : UiControl
    {
        public readonly List<string> Items = new List<string>();
        public string EmptyText = "Nothing here yet.";
        public bool MultiSelect, ShowIcon = true;
        public int RowHeight = 36;
        int index = -1, rangeEnd = -1, top, hoverRow = -1;
        public event EventHandler SelectedIndexChanged, ItemActivated;

        public ItemList()
        {
            SetStyle(ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
        }

        int RowH { get { return Ui.S(RowHeight); } }
        int VisibleRows { get { return Math.Max(1, (Height - Ui.S(8)) / RowH); } }

        public int SelectedIndex
        {
            get { return index; }
            set
            {
                index = value;
                rangeEnd = -1;
                ScrollTo(index);
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        // The selected range (both ends are the selected row when nothing extra is selected).
        public int SelectionStart { get { return rangeEnd < 0 ? index : Math.Min(index, rangeEnd); } }
        public int SelectionEnd { get { return rangeEnd < 0 ? index : Math.Max(index, rangeEnd); } }

        public void SelectRange(int from, int to)
        {
            index = from;
            rangeEnd = to == from ? -1 : to;
            ScrollTo(to);
            Invalidate();
            if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
        }

        void ScrollTo(int row)
        {
            if (row < 0) return;
            if (row < top) top = row;
            if (row >= top + VisibleRows) top = row - VisibleRows + 1;
            top = Math.Max(0, Math.Min(top, Math.Max(0, Items.Count - VisibleRows)));
        }

        int RowAt(int y)
        {
            int r = top + (y - Ui.S(4)) / RowH;
            return r >= 0 && r < Items.Count ? r : -1;
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int step = e.KeyCode == Keys.Down ? 1 : e.KeyCode == Keys.Up ? -1 : 0;
            if (step != 0 && Items.Count > 0)
            {
                if (MultiSelect && e.Shift && index >= 0)
                    SelectRange(index, Math.Max(0, Math.Min(Items.Count - 1, (rangeEnd < 0 ? index : rangeEnd) + step)));
                else SelectedIndex = Math.Max(0, Math.Min(Items.Count - 1, index + step));
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (Items.Count > VisibleRows && e.X >= Width - Ui.S(14))
            {
                // Click on the scrollbar: jump there.
                top = (int)((Items.Count - VisibleRows) * Math.Max(0.0, Math.Min(1.0, (e.Y - Ui.S(6)) / (double)(Height - Ui.S(12)))));
                Invalidate();
                return;
            }
            int r = RowAt(e.Y);
            if (r >= 0)
            {
                if (MultiSelect && (ModifierKeys & Keys.Shift) != 0 && index >= 0) SelectRange(index, r);
                else SelectedIndex = r;
            }
            base.OnMouseDown(e);
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            if (index >= 0 && ItemActivated != null) ItemActivated(this, EventArgs.Empty);
            base.OnDoubleClick(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int r = RowAt(e.Y);
            if (r != hoverRow) { hoverRow = r; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hoverRow = -1; base.OnMouseLeave(e); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int lines = Items.Count > 200 ? 6 : 1;
            top = Math.Max(0, Math.Min(Math.Max(0, Items.Count - VisibleRows), top - Math.Sign(e.Delta) * lines));
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            Ui.FillRound(g, new RectangleF(0.75f, 0.75f, Width - 1.5f, Height - 1.5f), Ui.S(6), Dim(T.Input), Dim(T.Border), 1);
            if (Items.Count == 0)
            {
                var er = new Rectangle(Ui.S(12), Ui.S(8), Width - Ui.S(24), Height - Ui.S(16));
                TextRenderer.DrawText(g, EmptyText, Ui.F(9f), er, T.SubText, TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }
            int s0 = SelectionStart, s1 = SelectionEnd;
            for (int i = top; i < Items.Count && i < top + VisibleRows; i++)
            {
                var r = new Rectangle(Ui.S(4), Ui.S(4) + (i - top) * RowH, Width - Ui.S(14), RowH - Ui.S(2));
                bool sel = i >= s0 && i <= s1 && s0 >= 0;
                if (sel) Ui.FillRound(g, r, Ui.S(5), Dim(Ui.Blend(T.Input, T.Accent, 0.25f)), i == index ? T.Accent : Ui.Blend(T.Input, T.Accent, 0.25f), 1);
                else if (i == hoverRow) Ui.FillRound(g, r, Ui.S(5), Ui.Blend(T.Input, T.Text, 0.05f), T.Input, 0);
                int tx = r.X + Ui.S(10);
                if (ShowIcon)
                {
                    Ui.Text(g, "", Ui.Icons(10f), new Rectangle(tx, r.Y + 1, Ui.S(20), r.Height), Dim(sel ? T.Accent : T.SubText), 0);
                    tx = r.X + Ui.S(36);
                }
                Ui.Text(g, Items[i], sel && ShowIcon ? Ui.Semi(Font.Size) : Font, new Rectangle(tx, r.Y, r.Right - tx - Ui.S(6), r.Height), Dim(T.Text), 0);
            }
            if (Items.Count > VisibleRows)
            {
                float trackH = Height - Ui.S(12), thumbH = Math.Max(Ui.S(20), trackH * VisibleRows / Items.Count);
                float y = Ui.S(6) + (trackH - thumbH) * top / Math.Max(1, Items.Count - VisibleRows);
                Ui.FillRound(g, new RectangleF(Width - Ui.S(8), y, Ui.S(4), thumbH), Ui.S(2), T.Border, T.Border, 0);
            }
        }
    }
    class UiMenuRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(Theme.Current.Surface);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var p = new Pen(Theme.Current.Border))
                e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var t = Theme.Current;
            bool current = e.Item.Tag is bool && (bool)e.Item.Tag;
            if (!e.Item.Selected && !current) return;
            Color c = e.Item.Selected ? Ui.Blend(t.Surface, t.Text, 0.09f) : Ui.Blend(t.Surface, t.Accent, 0.2f);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Ui.FillRound(e.Graphics, new RectangleF(1, 1, e.Item.Width - 2, e.Item.Height - 2), Ui.S(5), c, c, 0);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            Ui.Text(e.Graphics, e.Item.Text, e.TextFont, new Rectangle(Ui.S(10), 0, e.Item.Width - Ui.S(16), e.Item.Height),
                e.Item.Enabled ? Theme.Current.Text : Theme.Current.SubText, 0);
        }
    }

    class ColorBox : InputBase
    {
        Color color;
        public event EventHandler ValueChanged;
        public Color Value { get { return color; } set { color = value; Invalidate(); } }
        public string Title; // heading of the color picker ("Accent color"...)

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            using (var dlg = new ColorPickerForm(color, Title))
            {
                // Just below the box (or above it near the bottom of the screen).
                var at = PointToScreen(new Point(0, Height + Ui.S(6)));
                var area = Screen.FromControl(this).WorkingArea;
                if (at.Y + dlg.Height > area.Bottom) at.Y = PointToScreen(Point.Empty).Y - dlg.Height - Ui.S(6);
                dlg.Location = new Point(Math.Max(area.Left, Math.Min(at.X, area.Right - dlg.Width)), Math.Max(area.Top, at.Y));
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                color = dlg.Value;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            PaintFrame(g);
            int sw = Height - Ui.S(14);
            Ui.FillRound(g, new RectangleF(Ui.S(8), Ui.S(7), sw, sw), Ui.S(4), color, T.Border, 1);
            int tx = Ui.S(8) + sw + Ui.S(12);
            Ui.Text(g, "#" + Ui.Hex(color), Font, new Rectangle(tx, 0, Width - tx - Ui.S(34), Height), Dim(T.Text), 0);
            Ui.Text(g, "", Ui.Icons(8f), new Rectangle(Width - Ui.S(30), 1, Ui.S(18), Height), Dim(T.SubText), TextFormatFlags.HorizontalCenter);
        }
    }

    class ThemeTile : UiControl
    {
        public Theme Preset;
        public bool Selected;

        public ThemeTile() { Cursor = Cursors.Hand; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            var p = Preset;
            Color border = Selected ? T.Accent : hover ? Ui.Blend(T.Border, T.Text, 0.25f) : T.Border;
            Ui.FillRound(g, new RectangleF(1, 1, Width - 2, Height - 2), Ui.S(8), p.Surface, border, Selected ? 2 : 1);
            if (p == Theme.Presets[0])
            {
                var badge = new Rectangle((Width - Ui.S(50)) / 2, Ui.S(5), Ui.S(50), Ui.S(16));
                Ui.FillRound(g, badge, Ui.S(8), p.Accent, p.Accent, 0);
                Ui.Text(g, "Default", Ui.Semi(7f), badge, p.OnAccent, TextFormatFlags.HorizontalCenter);
            }
            int d = Ui.S(14), gap = Ui.S(5);
            int x = (Width - 3 * d - 2 * gap) / 2, y = Ui.S(26);
            Color[] dots = { Ui.Blend(p.Accent, p.Surface, 0.5f), p.Accent, Ui.Blend(p.Accent, System.Drawing.Color.White, 0.45f) };
            for (int i = 0; i < 3; i++)
                using (var b = new SolidBrush(dots[i])) g.FillEllipse(b, x + i * (d + gap), y, d, d);
            Ui.Text(g, p.Name, Ui.F(9f), new Rectangle(0, Ui.S(46), Width, Height - Ui.S(52)), p.Text, TextFormatFlags.HorizontalCenter);
        }
    }

    enum StatusKind { Ready, Running, Waiting, Problem }

    class StatusView : UiControl
    {
        StatusKind kind;
        public StatusKind Kind { get { return kind; } set { kind = value; Invalidate(); } }
        public bool Small; // compact view: smaller dot and text

        // One timer animates every visible status light: a slow breathing glow when ready,
        // sonar ripples while running, recording or waiting.
        static readonly List<StatusView> all = new List<StatusView>();
        static readonly Timer anim = new Timer { Interval = 40 };
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        public StatusView()
        {
            all.Add(this);
            if (all.Count == 1) anim.Tick += (s, e) => { foreach (var v in all) if (v.Visible && v.IsHandleCreated) v.Invalidate(new Rectangle(0, 0, v.Height + Ui.S(4), v.Height)); };
            anim.Start();
        }

        protected override void Dispose(bool disposing) { all.Remove(this); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            Color dot = kind == StatusKind.Running ? T.Accent : kind == StatusKind.Waiting ? Ui.Warning :
                        kind == StatusKind.Problem ? Ui.Danger : Ui.Success;
            float box = Ui.S(Small ? 18 : 22), cx = box / 2f, cy = Height / 2f;
            float core = Ui.S(Small ? 7 : 9);
            double now = clock.Elapsed.TotalSeconds; // always counts up (Environment.TickCount turns negative after ~25 days)
            if (kind == StatusKind.Ready)
            {
                // Breathing glow.
                float k = (float)(0.5 + 0.5 * Math.Sin(now * 2 * Math.PI / 2.6));
                float r = core / 2f + Ui.S(2.5f) + k * Ui.S(2.5f);
                using (var b = new SolidBrush(Color.FromArgb(Math.Max(0, Math.Min(255, (int)(40 + 45 * k))), dot))) g.FillEllipse(b, cx - r, cy - r, 2 * r, 2 * r);
            }
            else
            {
                // Two ripples spreading out from the light.
                double period = kind == StatusKind.Waiting ? 1.8 : 1.1;
                for (int i = 0; i < 2; i++)
                {
                    double t = ((now / period) + i * 0.5) % 1.0;
                    float r = core / 2f + (float)t * (box / 2f - core / 2f);
                    int alpha = Math.Max(0, Math.Min(255, (int)(170 * (1 - t))));
                    using (var pen = new Pen(Color.FromArgb(alpha, dot), Ui.S(1.6f))) g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
                }
                using (var b = new SolidBrush(Color.FromArgb(60, dot))) g.FillEllipse(b, cx - core / 2f - Ui.S(2), cy - core / 2f - Ui.S(2), core + Ui.S(4), core + Ui.S(4));
            }
            // The light itself: a lit centre fading to the colour, with a crisp rim.
            var rect = new RectangleF(cx - core / 2f, cy - core / 2f, core, core);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(rect);
                using (var b = new PathGradientBrush(path) { CenterColor = Ui.Blend(dot, Color.White, 0.55f), SurroundColors = new[] { dot },
                                                            CenterPoint = new PointF(cx - core * 0.15f, cy - core * 0.15f) })
                    g.FillPath(b, path);
            }
            using (var pen = new Pen(Ui.Blend(dot, Color.White, 0.25f), 1f)) g.DrawEllipse(pen, rect);
            int tx = (int)box + Ui.S(Small ? 5 : 8);
            Ui.Text(g, Text, Ui.Semi(Small ? 9f : 10f), new Rectangle(tx, 0, Width - tx, Height), T.Text, 0);
        }
    }
}
