// Embris's own color picker: a saturation / brightness square for the chosen hue, a rainbow hue
// bar, a preview with an editable hex code, and Cancel / Apply. Replaces the plain Windows dialog.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Embris
{
    static class Hsv
    {
        // h 0..360, s and v 0..1
        public static Color ToColor(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromArgb((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
        }

        public static void FromColor(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
            s = max == 0 ? 0 : d / max;
            v = max;
        }
    }

    class ColorPickerForm : Form, IBack
    {
        public Color BackFill { get { return Theme.Current.Surface; } } // controls inside paint on the panel color
        double hue, sat, val;
        readonly SvSquare square;
        readonly HueBar bar;
        readonly UiControl preview;
        readonly TextField hex;     // shown only while the code is being edited
        readonly TextLabel hexLabel; // the code as plain bold text: click it to edit
        bool syncing;

        public Color Value { get { return Hsv.ToColor(hue, sat, val); } }

        public ColorPickerForm(Color start, string title)
        {
            Hsv.FromColor(start, out hue, out sat, out val);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = Theme.Current.Surface;
            Font = Ui.F(9.75f);
            int w = Ui.S(300), pad = Ui.S(20);
            ClientSize = new Size(w, Ui.S(392));

            var heading = new TextLabel { Text = title ?? "Custom color", Font = Ui.Semi(15f) };
            heading.SetBounds(pad, Ui.S(14), w - 2 * pad, Ui.S(34));
            square = new SvSquare(this);
            square.SetBounds(pad, Ui.S(56), w - 2 * pad, Ui.S(190));
            bar = new HueBar(this);
            bar.SetBounds(pad - Ui.S(4), Ui.S(256), w - 2 * pad + Ui.S(8), Ui.S(26));
            preview = new SwatchView(this);
            preview.SetBounds(pad, Ui.S(294), Ui.S(38), Ui.S(38));
            hex = new TextField { Font = Ui.Semi(12f), Visible = false };
            hex.SetBounds(pad + Ui.S(50), Ui.S(296), Ui.S(140), Ui.S(34));
            hex.Box.TextChanged += (s, e) => TypedHex();
            hex.Box.LostFocus += (s, e) => EndHexEdit();
            hexLabel = new TextLabel { Font = Ui.Semi(14f), Cursor = Cursors.IBeam };
            hexLabel.SetBounds(pad + Ui.S(52), Ui.S(294), w - 2 * pad - Ui.S(52), Ui.S(38));
            hexLabel.Click += (s, e) => BeginHexEdit();
            new ToolTip().SetToolTip(hexLabel, "Click to type a color code");
            var cancel = new RoundButton { Text = "Cancel" };
            cancel.SetBounds(pad, Ui.S(342), (w - 2 * pad - Ui.S(10)) / 2, Ui.S(36));
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            var apply = new RoundButton { Text = "Apply", Style = BtnStyle.Primary };
            apply.SetBounds(cancel.Right + Ui.S(10), Ui.S(342), cancel.Width, Ui.S(36));
            apply.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            Controls.AddRange(new Control[] { heading, square, bar, preview, hex, hexLabel, cancel, apply });
            AcceptButton = null;
            Changed(true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Current.Border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (hex.Visible && (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape)) { EndHexEdit(); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            else if (e.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; Close(); }
            base.OnKeyDown(e);
        }

        // Clicking anywhere else closes it without changing anything.
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (Visible && DialogResult == DialogResult.None) BeginInvoke((Action)(() => { if (!IsDisposed) Close(); }));
        }

        // Drop shadow around the borderless window.
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } // CS_DROPSHADOW
        }

        public void SetHue(double h) { hue = Math.Max(0, Math.Min(359.999, h)); square.HueChanged(); Changed(true); }
        public void SetSatVal(double s, double v) { sat = Math.Max(0, Math.Min(1, s)); val = Math.Max(0, Math.Min(1, v)); Changed(true); }
        public double Hue { get { return hue; } }
        public double Sat { get { return sat; } }
        public double Val { get { return val; } }

        void Changed(bool updateHex)
        {
            square.Invalidate();
            bar.Invalidate();
            preview.Invalidate();
            if (updateHex && !syncing)
            {
                syncing = true;
                hex.Text = "#" + Ui.Hex(Value);
                syncing = false;
            }
            hexLabel.Text = "#" + Ui.Hex(Value);
        }

        void BeginHexEdit()
        {
            hexLabel.Visible = false;
            hex.Visible = true;
            hex.Box.Focus();
            hex.Box.SelectAll();
        }

        void EndHexEdit()
        {
            if (!hex.Visible) return;
            hex.Visible = false;
            hexLabel.Text = "#" + Ui.Hex(Value);
            hexLabel.Visible = true;
        }

        void TypedHex()
        {
            if (syncing) return;
            string s = hex.Text.Trim().TrimStart('#');
            if (s.Length == 3) s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
            int v;
            if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out v)) return;
            Hsv.FromColor(Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255), out hue, out sat, out val);
            square.HueChanged();
            Changed(false);
        }

        // ---------------------------------------------------------------- parts

        class SvSquare : UiControl
        {
            readonly ColorPickerForm owner;
            Bitmap plane;  // the colors for the current hue
            double planeHue = -1;
            bool drag;

            public SvSquare(ColorPickerForm owner) { this.owner = owner; Cursor = Cursors.Cross; }

            public void HueChanged() { Invalidate(); }

            void Build()
            {
                int w = Math.Max(1, Width), h = Math.Max(1, Height);
                if (plane != null && plane.Width == w && plane.Height == h && planeHue == owner.Hue) return;
                if (plane != null) plane.Dispose();
                plane = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                var data = plane.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                var px = new int[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        px[y * w + x] = Hsv.ToColor(owner.Hue, x / (double)(w - 1), 1 - y / (double)(h - 1)).ToArgb();
                Marshal.Copy(px, 0, data.Scan0, px.Length);
                plane.UnlockBits(data);
                planeHue = owner.Hue;
            }

            void Pick(Point p) { owner.SetSatVal(p.X / (double)(Width - 1), 1 - p.Y / (double)(Height - 1)); }

            protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); drag = true; Pick(e.Location); }
            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (drag) Pick(e.Location); }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = false; }

            protected override void OnPaint(PaintEventArgs e)
            {
                Build();
                var g = Smooth(e);
                using (var path = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), Ui.S(8)))
                {
                    g.SetClip(path);
                    g.DrawImageUnscaled(plane, 0, 0);
                    g.ResetClip();
                }
                float cx = (float)(owner.Sat * (Width - 1)), cy = (float)((1 - owner.Val) * (Height - 1)), r = Ui.S(7);
                using (var dark = new Pen(Color.FromArgb(120, 0, 0, 0), Ui.S(3))) g.DrawEllipse(dark, cx - r, cy - r, 2 * r, 2 * r);
                using (var white = new Pen(Color.White, Ui.S(2))) g.DrawEllipse(white, cx - r, cy - r, 2 * r, 2 * r);
            }
        }

        class HueBar : UiControl
        {
            readonly ColorPickerForm owner;
            bool drag;

            public HueBar(ColorPickerForm owner) { this.owner = owner; Cursor = Cursors.Hand; }

            int Pad { get { return Ui.S(8); } }
            void Pick(int x) { owner.SetHue((x - Pad) * 360.0 / Math.Max(1, Width - 2 * Pad)); }

            protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); drag = true; Pick(e.X); }
            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (drag) Pick(e.X); }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = false; }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = Smooth(e);
                var r = new RectangleF(Pad - Ui.S(4), Ui.S(5), Width - 2 * Pad + Ui.S(8), Height - Ui.S(10));
                using (var path = Ui.Round(r, r.Height / 2))
                using (var b = new LinearGradientBrush(new RectangleF(Pad, 0, Width - 2 * Pad, Height), Color.Red, Color.Red, 0f))
                {
                    var blend = new ColorBlend(7);
                    for (int i = 0; i < 7; i++)
                    {
                        blend.Colors[i] = Hsv.ToColor(i * 60, 1, 1);
                        blend.Positions[i] = i / 6f;
                    }
                    b.InterpolationColors = blend;
                    g.FillPath(b, path);
                }
                float x = Pad + (float)(owner.Hue / 360 * (Width - 2 * Pad));
                var thumb = new RectangleF(x - Ui.S(5), 0, Ui.S(10), Height - 1);
                Ui.FillRound(g, thumb, Ui.S(5), Hsv.ToColor(owner.Hue, 1, 1), Color.White, Ui.S(2));
            }
        }

        class SwatchView : UiControl
        {
            readonly ColorPickerForm owner;
            public SwatchView(ColorPickerForm owner) { this.owner = owner; }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = Smooth(e);
                using (var b = new SolidBrush(owner.Value)) g.FillEllipse(b, 1, 1, Width - 3, Height - 3);
                using (var pen = new Pen(T.Border, 1.5f)) g.DrawEllipse(pen, 1, 1, Width - 3, Height - 3);
            }
        }
    }
}
