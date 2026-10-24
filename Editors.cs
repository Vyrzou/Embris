// The sequence editor's pieces: a model that groups raw steps into editable rows ("Key press",
// "Delay"...), the step table, the keyboard preview and the icon tiles under the table.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Embris
{
    enum RowType { KeyPress, KeyDown, KeyUp, Click, MouseDown, MouseUp, Move, Type, Delay }

    // One row of the editor: steps First..Last of the sequence.
    class SeqRow
    {
        public RowType Type;
        public int First, Last;
        public int Count { get { return Last - First + 1; } }
        public bool IsKey { get { return Type <= RowType.KeyUp; } }
        public bool IsMouse { get { return Type >= RowType.Click && Type <= RowType.MouseUp; } }
    }

    static class SeqModel
    {
        public const int MoveGapMs = 250; // a pause longer than this splits camera movement into separate rows
        public static readonly string[] Names = { "Key press", "Key down", "Key up", "Click", "Mouse down", "Mouse up", "Mouse move", "Type text", "Delay" };
        public static readonly string[] Icons = { "", "", "", "", "", "", "", "", "" };

        static RecStep Clone(RecStep s) { return new RecStep { Delay = s.Delay, Kind = s.Kind, Code = s.Code, X = s.X, Y = s.Y, Text = s.Text }; }

        // How many steps belong together starting at i: a press and its release, a click, or a run of camera movement.
        static int BlockLength(List<RecStep> seq, int i)
        {
            var st = seq[i];
            var nx = i + 1 < seq.Count ? seq[i + 1] : null;
            if (st.Kind == 0 && nx != null && nx.Kind == 1 && nx.Code == st.Code) return 2;
            if (st.Kind == 2 && nx != null && nx.Kind == 3 && nx.Code == st.Code) return 2;
            if (st.Kind == 5)
            {
                int j = i + 1;
                while (j < seq.Count && seq[j].Kind == 5 && seq[j].Delay <= MoveGapMs) j++;
                return j - i;
            }
            return 1;
        }

        // Puts every pause in a Delay step of its own, so each row is one action. Timing stays exactly the same.
        public static void Normalize(List<RecStep> seq)
        {
            var result = new List<RecStep>(seq.Count + 16);
            int i = 0;
            while (i < seq.Count)
            {
                var st = seq[i];
                if (st.Kind == 4) { result.Add(st); i++; continue; }
                int len = BlockLength(seq, i);
                if (st.Delay > 0)
                {
                    // A copy, so a sequence that's playing right now keeps its own steps unchanged.
                    result.Add(new RecStep { Kind = 4, Delay = st.Delay });
                    st = Clone(st);
                    st.Delay = 0;
                }
                result.Add(st);
                for (int j = 1; j < len; j++) result.Add(seq[i + j]);
                i += len;
            }
            seq.Clear();
            seq.AddRange(result);
        }

        public static List<SeqRow> Rows(List<RecStep> seq)
        {
            var rows = new List<SeqRow>();
            int i = 0;
            while (i < seq.Count)
            {
                int len = seq[i].Kind == 4 ? 1 : BlockLength(seq, i);
                RowType t;
                switch (seq[i].Kind)
                {
                    case 0: t = len == 2 ? RowType.KeyPress : RowType.KeyDown; break;
                    case 1: t = RowType.KeyUp; break;
                    case 2: t = len == 2 ? RowType.Click : RowType.MouseDown; break;
                    case 3: t = RowType.MouseUp; break;
                    case 5: t = RowType.Move; break;
                    case 6: t = RowType.Type; break;
                    default: t = RowType.Delay; break;
                }
                rows.Add(new SeqRow { Type = t, First = i, Last = i + len - 1 });
                i += len;
            }
            return rows;
        }

        // ---------------------------------------------------------------- reading a row

        public static int Hold(List<RecStep> seq, SeqRow r)
        {
            switch (r.Type)
            {
                case RowType.KeyPress:
                case RowType.Click: return seq[r.Last].Delay;
                case RowType.Delay: return seq[r.First].Delay;
                case RowType.Move:
                    int sum = 0;
                    for (int i = r.First + 1; i <= r.Last; i++) sum += seq[i].Delay;
                    return sum;
                default: return 0;
            }
        }

        public static Point MoveTotal(List<RecStep> seq, SeqRow r)
        {
            int x = 0, y = 0;
            for (int i = r.First; i <= r.Last; i++) { x += seq[i].X; y += seq[i].Y; }
            return new Point(x, y);
        }

        public static string KeyText(List<RecStep> seq, SeqRow r)
        {
            var s = seq[r.First];
            if (r.IsKey) return Ui.KeyName((Keys)s.Code);
            if (r.IsMouse) return s.Code == 1 ? "Right" : "Left";
            if (r.Type == RowType.Move)
            {
                var p = MoveTotal(seq, r);
                return (p.X >= 0 ? "+" : "") + p.X + ", " + (p.Y >= 0 ? "+" : "") + p.Y;
            }
            if (r.Type == RowType.Type) return s.Text ?? "";
            return "";
        }

        public static string DurationText(List<RecStep> seq, SeqRow r)
        {
            switch (r.Type)
            {
                case RowType.KeyPress:
                case RowType.Click:
                case RowType.Delay: return Ms(Hold(seq, r));
                case RowType.Move: return r.Count > 1 ? Ms(Hold(seq, r)) : "\u2014";
                default: return "\u2014";
            }
        }

        public static string Ms(int ms) { return ms >= 10000 ? (ms / 1000.0).ToString("0.##") + " s" : ms + " ms"; }

        // ---------------------------------------------------------------- editing a row

        public static void SetHold(List<RecStep> seq, SeqRow r, int ms)
        {
            switch (r.Type)
            {
                case RowType.KeyPress:
                case RowType.Click: seq[r.Last].Delay = ms; break;
                case RowType.Delay: seq[r.First].Delay = ms; break;
                case RowType.Move:
                    // Spread the new length over the movement, keeping its rhythm.
                    int n = r.Count - 1, old = Hold(seq, r), done = 0;
                    if (n <= 0) break;
                    for (int i = 1; i <= n; i++)
                    {
                        int d = i == n ? ms - done : old > 0 ? (int)Math.Round((double)seq[r.First + i].Delay * ms / old) : ms / n;
                        d = Math.Max(0, Math.Min(d, ms - done));
                        seq[r.First + i].Delay = d;
                        done += d;
                    }
                    break;
            }
        }

        // Changes a key, keeping a separate press and release of it matched.
        public static void SetKey(List<RecStep> seq, SeqRow r, Keys key)
        {
            int old = seq[r.First].Code;
            for (int i = r.First; i <= r.Last; i++) seq[i].Code = (int)key;
            if (r.Type == RowType.KeyDown) Partner(seq, r.First, 1, old, 1, (int)key);
            if (r.Type == RowType.KeyUp) Partner(seq, r.First, 0, old, -1, (int)key);
        }

        public static void SetButton(List<RecStep> seq, SeqRow r, bool right)
        {
            int old = seq[r.First].Code, b = right ? 1 : 0;
            for (int i = r.First; i <= r.Last; i++) seq[i].Code = b;
            if (r.Type == RowType.MouseDown) Partner(seq, r.First, 3, old, 1, b);
            if (r.Type == RowType.MouseUp) Partner(seq, r.First, 2, old, -1, b);
        }

        static void Partner(List<RecStep> seq, int from, int kind, int code, int dir, int newCode)
        {
            for (int j = from + dir; j >= 0 && j < seq.Count; j += dir)
                if (seq[j].Kind == kind && seq[j].Code == code) { seq[j].Code = newCode; return; }
        }

        public static void SetPosition(List<RecStep> seq, SeqRow r, int x, int y)
        {
            for (int i = r.First; i <= r.Last; i++) { seq[i].X = x; seq[i].Y = y; }
        }

        // Sets a camera movement's total distance, spread over its steps in the same proportions.
        public static void SetMove(List<RecStep> seq, SeqRow r, int dx, int dy)
        {
            var old = MoveTotal(seq, r);
            int doneX = 0, doneY = 0;
            for (int i = r.First; i <= r.Last; i++)
            {
                bool last = i == r.Last;
                int x = last ? dx - doneX : old.X != 0 ? (int)Math.Round((double)seq[i].X * dx / old.X) : 0;
                int y = last ? dy - doneY : old.Y != 0 ? (int)Math.Round((double)seq[i].Y * dy / old.Y) : 0;
                seq[i].X = x;
                seq[i].Y = y;
                doneX += x;
                doneY += y;
            }
        }

        // Builds the steps for a new row of the given type.
        public static List<RecStep> Make(RowType type, Keys key, bool right, int x, int y, int hold)
        {
            int k = (int)key, b = right ? 1 : 0;
            var list = new List<RecStep>();
            switch (type)
            {
                case RowType.KeyPress:
                    list.Add(new RecStep { Kind = 0, Code = k });
                    list.Add(new RecStep { Kind = 1, Code = k, Delay = hold });
                    break;
                case RowType.KeyDown: list.Add(new RecStep { Kind = 0, Code = k }); break;
                case RowType.KeyUp: list.Add(new RecStep { Kind = 1, Code = k }); break;
                case RowType.Click:
                    list.Add(new RecStep { Kind = 2, Code = b, X = x, Y = y });
                    list.Add(new RecStep { Kind = 3, Code = b, X = x, Y = y, Delay = hold });
                    break;
                case RowType.MouseDown: list.Add(new RecStep { Kind = 2, Code = b, X = x, Y = y }); break;
                case RowType.MouseUp: list.Add(new RecStep { Kind = 3, Code = b, X = x, Y = y }); break;
                case RowType.Move: list.Add(new RecStep { Kind = 5, X = 10, Y = 0 }); break;
                case RowType.Type: list.Add(new RecStep { Kind = 6, Text = "hello" }); break;
                default: list.Add(new RecStep { Kind = 4, Delay = hold }); break;
            }
            return list;
        }

        // Turns a row into another kind of action, keeping its key, button, position and length where they fit.
        public static void ChangeType(List<RecStep> seq, SeqRow r, RowType type)
        {
            if (r.Type == type) return;
            var s = seq[r.First];
            Keys key = r.IsKey ? (Keys)s.Code : Keys.E;
            bool right = r.IsMouse && s.Code == 1;
            int x = r.IsMouse ? s.X : RecStep.NoPosition, y = r.IsMouse ? s.Y : RecStep.NoPosition;
            int hold = Hold(seq, r);
            if (hold <= 0) hold = type == RowType.Delay ? 250 : type == RowType.Click ? 40 : 50;
            seq.RemoveRange(r.First, r.Count);
            seq.InsertRange(r.First, Make(type, key, right, x, y, hold));
        }

        public static void Insert(List<RecStep> seq, int at, List<RecStep> steps)
        {
            seq.InsertRange(Math.Max(0, Math.Min(at, seq.Count)), steps);
        }

        public static void Duplicate(List<RecStep> seq, SeqRow r)
        {
            var copy = new List<RecStep>();
            for (int i = r.First; i <= r.Last; i++) copy.Add(Clone(seq[i]));
            seq.InsertRange(r.Last + 1, copy);
        }

        // Rows a..b (inclusive) of the given rows.
        public static void Delete(List<RecStep> seq, List<SeqRow> rows, int a, int b)
        {
            seq.RemoveRange(rows[a].First, rows[b].Last - rows[a].First + 1);
        }

        public static void Crop(List<RecStep> seq, List<SeqRow> rows, int a, int b)
        {
            var keep = seq.GetRange(rows[a].First, rows[b].Last - rows[a].First + 1);
            seq.Clear();
            seq.AddRange(keep);
        }

        // Moves rows a..b so they sit before row "to" (rows.Count = the end).
        public static void MoveRows(List<RecStep> seq, List<SeqRow> rows, int a, int b, int to)
        {
            if (to >= a && to <= b + 1) return;
            int first = rows[a].First, len = rows[b].Last - first + 1;
            int at = to < rows.Count ? rows[to].First : seq.Count;
            var block = seq.GetRange(first, len);
            seq.RemoveRange(first, len);
            if (at > first) at -= len;
            seq.InsertRange(at, block);
        }

        // The pause after a row is the Delay row that follows it (0 if there's none).
        public static int DelayAfter(List<RecStep> seq, List<SeqRow> rows, int i)
        {
            return i + 1 < rows.Count && rows[i + 1].Type == RowType.Delay ? seq[rows[i + 1].First].Delay : 0;
        }

        public static void SetDelayAfter(List<RecStep> seq, List<SeqRow> rows, int i, int ms)
        {
            if (i + 1 < rows.Count && rows[i + 1].Type == RowType.Delay)
            {
                if (ms > 0) seq[rows[i + 1].First].Delay = ms;
                else seq.RemoveAt(rows[i + 1].First);
            }
            else if (ms > 0) seq.Insert(rows[i].Last + 1, new RecStep { Kind = 4, Delay = ms });
        }

        // Index of the row containing a step (for keeping the selection after edits).
        public static int RowOf(List<SeqRow> rows, int step)
        {
            for (int i = 0; i < rows.Count; i++) if (rows[i].Last >= step) return i;
            return rows.Count - 1;
        }
    }

    // ---------------------------------------------------------------- step table

    class TableRow
    {
        public string Icon, Action, Keys, Duration;
        public bool Chip;  // draw Keys as a key cap
    }

    // Table of steps: Step / Action / Keys / Duration, with drag handles to reorder and a menu button per row.
    class StepTable : UiControl
    {
        public readonly List<TableRow> Rows = new List<TableRow>();
        public string EmptyText = "";
        int anchor = -1, caret = -1, scroll, hoverRow = -1;
        int dragFrom = -1, dropAt = -1;
        bool thumbDrag;
        int thumbGrab;
        public event EventHandler SelectionChanged;
        public event Action<int, int, int> RowsMoved;    // first, last, before
        public event Action<int, Point> MenuRequested;  // row, screen point
        public event EventHandler DeleteRequested;

        const int HeadH = 34, RowH = 42;

        public StepTable()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Font = Ui.F(9.75f);
        }

        public int SelectionStart { get { return Math.Min(anchor, caret); } }
        public int SelectionEnd { get { return Math.Max(anchor, caret); } }

        public void SelectRows(int a, int b)
        {
            anchor = Math.Min(a, Rows.Count - 1);
            caret = Math.Min(b, Rows.Count - 1);
            if (anchor < 0 || caret < 0) anchor = caret = -1;
            EnsureVisible(caret);
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        public void SetRows(List<TableRow> rows)
        {
            Rows.Clear();
            Rows.AddRange(rows);
            if (caret >= Rows.Count || anchor >= Rows.Count) { anchor = Math.Min(anchor, Rows.Count - 1); caret = Math.Min(caret, Rows.Count - 1); }
            scroll = Math.Max(0, Math.Min(scroll, MaxScroll));
            Invalidate();
        }

        int RowHeight { get { return Ui.S(RowH); } }
        int Top0 { get { return Ui.S(HeadH); } }
        int VisibleRows { get { return Math.Max(1, (Height - Top0) / RowHeight); } }
        int MaxScroll { get { return Math.Max(0, Rows.Count - VisibleRows); } }
        int RowAt(int y) { return y < Top0 ? -1 : scroll + (y - Top0) / RowHeight; }

        void EnsureVisible(int r)
        {
            if (r < 0) return;
            if (r < scroll) scroll = r;
            else if (r >= scroll + VisibleRows) scroll = r - VisibleRows + 1;
            scroll = Math.Max(0, Math.Min(scroll, MaxScroll));
        }

        // Column positions, as fractions of the width after the handle and number.
        void Columns(out int handle, out int num, out int action, out int keys, out int dur, out int menu)
        {
            handle = Ui.S(10);
            num = Ui.S(30);
            int right = Width - Ui.S(12) - (MaxScroll > 0 ? Ui.S(8) : 0);
            menu = right - Ui.S(22);
            int rest = menu - Ui.S(80);
            action = num + Ui.S(40);
            keys = action + (int)(rest * 0.42) + Ui.S(10);
            dur = keys + (int)(rest * 0.36);
        }

        Rectangle Thumb
        {
            get
            {
                int track = Height - Top0 - Ui.S(8);
                int h = Math.Max(Ui.S(24), track * VisibleRows / Math.Max(1, Rows.Count));
                int y = Top0 + Ui.S(4) + (MaxScroll == 0 ? 0 : (track - h) * scroll / MaxScroll);
                return new Rectangle(Width - Ui.S(10), y, Ui.S(5), h);
            }
        }

        protected override bool IsInputKey(Keys k)
        {
            var c = k & Keys.KeyCode;
            return c == Keys.Up || c == Keys.Down || c == Keys.Delete || c == Keys.Home || c == Keys.End || base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Rows.Count > 0)
            {
                int c = caret < 0 ? 0 : caret;
                bool move = true;
                switch (e.KeyCode)
                {
                    case Keys.Up: c--; break;
                    case Keys.Down: c++; break;
                    case Keys.Home: c = 0; break;
                    case Keys.End: c = Rows.Count - 1; break;
                    case Keys.Delete: move = false; if (DeleteRequested != null) DeleteRequested(this, EventArgs.Empty); break;
                    default: move = false; break;
                }
                if (move)
                {
                    c = Math.Max(0, Math.Min(Rows.Count - 1, c));
                    if (e.Alt && caret >= 0 && RowsMoved != null)
                    {
                        // Alt+Up/Down moves the selected rows.
                        int a = SelectionStart, b = SelectionEnd;
                        if (e.KeyCode == Keys.Up && a > 0) RowsMoved(a, b, a - 1);
                        else if (e.KeyCode == Keys.Down && b < Rows.Count - 1) RowsMoved(a, b, b + 2);
                    }
                    else SelectRows(e.Shift && anchor >= 0 ? anchor : c, c);
                    e.Handled = true;
                }
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            base.OnMouseDown(e);
            if (MaxScroll > 0 && e.X >= Width - Ui.S(14) && e.Y >= Top0)
            {
                var t = Thumb;
                if (t.Contains(new Point(t.X, e.Y))) { thumbDrag = true; thumbGrab = e.Y - t.Y; }
                else { scroll = Math.Max(0, Math.Min(MaxScroll, scroll + (e.Y < t.Y ? -VisibleRows : VisibleRows))); Invalidate(); }
                return;
            }
            int r = RowAt(e.Y);
            if (r < 0 || r >= Rows.Count) return;
            int handle, num, action, keys, dur, menu;
            Columns(out handle, out num, out action, out keys, out dur, out menu);
            if (e.Button == MouseButtons.Left && e.X >= menu)
            {
                if (r < SelectionStart || r > SelectionEnd) SelectRows(r, r);
                if (MenuRequested != null) MenuRequested(r, PointToScreen(new Point(menu, RowY(r) + RowHeight)));
                return;
            }
            if (e.Button == MouseButtons.Right)
            {
                if (r < SelectionStart || r > SelectionEnd) SelectRows(r, r);
                if (MenuRequested != null) MenuRequested(r, PointToScreen(e.Location));
                return;
            }
            if ((ModifierKeys & Keys.Shift) != 0 && anchor >= 0) SelectRows(anchor, r);
            else SelectRows(r, r);
            if (e.X < num + Ui.S(4) || (e.Button == MouseButtons.Left && r >= SelectionStart && r <= SelectionEnd)) dragFrom = r;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (thumbDrag)
            {
                int track = Height - Top0 - Ui.S(8) - Thumb.Height;
                if (track > 0) scroll = Math.Max(0, Math.Min(MaxScroll, (e.Y - Top0 - Ui.S(4) - thumbGrab) * MaxScroll / track));
                Invalidate();
                return;
            }
            if (dragFrom >= 0 && e.Button == MouseButtons.Left)
            {
                int y = e.Y - Top0;
                int at = Math.Max(0, Math.Min(Rows.Count, scroll + (y + RowHeight / 2) / RowHeight));
                if (e.Y < Top0 + Ui.S(6) && scroll > 0) scroll--;
                if (e.Y > Height - Ui.S(6) && scroll < MaxScroll) scroll++;
                if (at != dropAt) { dropAt = at; Invalidate(); }
                Cursor = Cursors.SizeNS;
                return;
            }
            int handle, num, action, keys, dur, menu;
            Columns(out handle, out num, out action, out keys, out dur, out menu);
            int r = RowAt(e.Y);
            Cursor = r >= 0 && r < Rows.Count && e.X < num + Ui.S(4) ? Cursors.SizeNS : Cursors.Default;
            if (r != hoverRow) { hoverRow = r; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            thumbDrag = false;
            if (dragFrom >= 0 && dropAt >= 0 && RowsMoved != null)
            {
                int a = SelectionStart, b = SelectionEnd;
                if (dragFrom < a || dragFrom > b) { a = b = dragFrom; }
                if (dropAt < a || dropAt > b + 1) RowsMoved(a, b, dropAt);
            }
            dragFrom = dropAt = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e) { hoverRow = -1; base.OnMouseLeave(e); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll = Math.Max(0, Math.Min(MaxScroll, scroll - Math.Sign(e.Delta) * 3));
            Invalidate();
            base.OnMouseWheel(e);
        }

        int RowY(int r) { return Top0 + (r - scroll) * RowHeight; }

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = Smooth(pe);
            var t = T;
            int handle, num, action, keys, dur, menu;
            Columns(out handle, out num, out action, out keys, out dur, out menu);
            var head = Ui.F(9f);
            int hh = Top0;
            Ui.Text(g, "Step", head, new Rectangle(num - Ui.S(4), 0, Ui.S(44), hh), t.SubText, 0);
            Ui.Text(g, "Action", head, new Rectangle(action, 0, keys - action, hh), t.SubText, 0);
            Ui.Text(g, "Keys", head, new Rectangle(keys, 0, dur - keys, hh), t.SubText, 0);
            Ui.Text(g, "Duration", head, new Rectangle(dur, 0, menu - dur, hh), t.SubText, 0);
            using (var pen = new Pen(t.Border)) g.DrawLine(pen, 0, hh - 1, Width, hh - 1);

            if (Rows.Count == 0)
            {
                Ui.Text(g, EmptyText, Ui.F(9f), new Rectangle(Ui.S(16), hh + Ui.S(8), Width - Ui.S(32), Height - hh - Ui.S(16)), t.SubText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
                return;
            }
            int rh = RowHeight;
            var iconFont = Ui.Icons(12f);
            var numFont = Ui.F(9.75f);
            g.SetClip(new Rectangle(0, hh, Width, Height - hh));
            for (int r = scroll; r < Rows.Count; r++)
            {
                int y = RowY(r);
                if (y > Height) break;
                var row = Rows[r];
                bool sel = r >= SelectionStart && r <= SelectionEnd && caret >= 0;
                var rect = new RectangleF(Ui.S(2), y + Ui.S(3), Width - Ui.S(4) - (MaxScroll > 0 ? Ui.S(10) : 0), rh - Ui.S(6));
                if (sel) Ui.FillRound(g, rect, Ui.S(8), Ui.Blend(t.Input, t.Accent, 0.12f), t.Accent, 1.4f);
                else if (r == hoverRow) Ui.FillRound(g, rect, Ui.S(8), Ui.Blend(t.Input, t.Text, 0.04f), Color.Transparent, 0);
                else using (var pen = new Pen(Ui.Blend(t.Border, t.Surface, 0.4f))) g.DrawLine(pen, Ui.S(6), y + rh - 1, Width - Ui.S(6), y + rh - 1);

                // Drag handle: two columns of dots.
                using (var dot = new SolidBrush(Ui.Blend(t.SubText, t.Surface, 0.3f)))
                    for (int i = 0; i < 3; i++)
                        for (int j = 0; j < 2; j++)
                            g.FillEllipse(dot, handle + j * Ui.S(5), y + rh / 2 - Ui.S(6) + i * Ui.S(5), Ui.S(2.6f), Ui.S(2.6f));
                Ui.Text(g, (r + 1).ToString("00"), numFont, new Rectangle(num, y, Ui.S(36), rh), t.Text, 0);
                Ui.Text(g, row.Icon, iconFont, new Rectangle(action, y, Ui.S(26), rh), t.SubText, 0);
                Ui.Text(g, row.Action, Font, new Rectangle(action + Ui.S(30), y, keys - action - Ui.S(34), rh), t.Text, 0);
                if (!string.IsNullOrEmpty(row.Keys))
                {
                    if (row.Chip)
                    {
                        int w = Math.Min(dur - keys - Ui.S(8), TextRenderer.MeasureText(row.Keys, Ui.F(9f)).Width + Ui.S(16));
                        var chip = new RectangleF(keys, y + (rh - Ui.S(28)) / 2f, Math.Max(Ui.S(30), w), Ui.S(28));
                        Ui.FillRound(g, chip, Ui.S(5), Ui.Blend(t.Input, t.Text, 0.07f), Ui.Blend(t.Border, t.Text, 0.12f), 1);
                        Ui.Text(g, row.Keys, Ui.F(9f), Rectangle.Round(chip), t.Text, TextFormatFlags.HorizontalCenter);
                    }
                    else Ui.Text(g, row.Keys, Ui.F(9f), new Rectangle(keys, y, dur - keys - Ui.S(6), rh), t.SubText, 0);
                }
                Ui.Text(g, row.Duration, Font, new Rectangle(dur, y, menu - dur, rh), row.Duration == "\u2014" ? t.SubText : t.Text, 0);
                // Menu button: three dots.
                using (var dot = new SolidBrush(r == hoverRow || sel ? t.Text : t.SubText))
                    for (int i = 0; i < 3; i++)
                        g.FillEllipse(dot, menu + Ui.S(9), y + rh / 2 - Ui.S(7) + i * Ui.S(6), Ui.S(3), Ui.S(3));
            }
            if (dropAt >= 0)
            {
                int y = RowY(dropAt);
                using (var pen = new Pen(t.Accent, Ui.S(2))) g.DrawLine(pen, Ui.S(4), y, Width - Ui.S(14), y);
            }
            g.ResetClip();
            if (MaxScroll > 0) Ui.FillRound(g, Thumb, Ui.S(2.5f), Ui.Blend(t.Border, t.Text, 0.15f), Color.Transparent, 0);
        }
    }

    // ---------------------------------------------------------------- keyboard preview

    // A small keyboard that lights up the key(s) of the selected step.
    class KeyboardPreview : UiControl
    {
        struct Cap { public string Label; public float X, W; public int Row; public Keys[] Keys; }
        static readonly List<Cap> caps = BuildLayout();
        public readonly HashSet<Keys> Lit = new HashSet<Keys>();

        static List<Cap> BuildLayout()
        {
            var list = new List<Cap>();
            Action<int, float, float, string, Keys[]> add = (row, x, w, label, keys) => list.Add(new Cap { Row = row, X = x, W = w, Label = label, Keys = keys });
            add(0, 0, 1, "Esc", new[] { Keys.Escape });
            for (int i = 0; i < 12; i++) add(0, 2 + i + (i / 4) * 0.5f, 1, "F" + (i + 1), new[] { Keys.F1 + i });
            string top = "`1234567890-=";
            Keys[] topKeys = { Keys.Oemtilde, Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5, Keys.D6, Keys.D7, Keys.D8, Keys.D9, Keys.D0, Keys.OemMinus, Keys.Oemplus };
            for (int i = 0; i < 13; i++) add(1, i, 1, top[i].ToString(), i >= 1 && i <= 10 ? new[] { topKeys[i], Keys.NumPad0 + (i == 10 ? 0 : i) } : new[] { topKeys[i] });
            add(1, 13, 2, "Backspace", new[] { Keys.Back });
            add(2, 0, 1.5f, "Tab", new[] { Keys.Tab });
            string q = "QWERTYUIOP";
            for (int i = 0; i < 10; i++) add(2, 1.5f + i, 1, q[i].ToString(), new[] { (Keys)q[i] });
            add(2, 11.5f, 1, "[", new[] { Keys.OemOpenBrackets });
            add(2, 12.5f, 1, "]", new[] { Keys.OemCloseBrackets });
            add(2, 13.5f, 1.5f, "\\", new[] { Keys.OemPipe });
            add(3, 0, 1.75f, "Caps", new[] { Keys.Capital });
            string a = "ASDFGHJKL";
            for (int i = 0; i < 9; i++) add(3, 1.75f + i, 1, a[i].ToString(), new[] { (Keys)a[i] });
            add(3, 10.75f, 1, ";", new[] { Keys.OemSemicolon });
            add(3, 11.75f, 1, "'", new[] { Keys.OemQuotes });
            add(3, 12.75f, 2.25f, "Enter", new[] { Keys.Return });
            add(4, 0, 2.25f, "Shift", new[] { Keys.ShiftKey, Keys.LShiftKey });
            string z = "ZXCVBNM";
            for (int i = 0; i < 7; i++) add(4, 2.25f + i, 1, z[i].ToString(), new[] { (Keys)z[i] });
            add(4, 9.25f, 1, ",", new[] { Keys.Oemcomma });
            add(4, 10.25f, 1, ".", new[] { Keys.OemPeriod });
            add(4, 11.25f, 1, "/", new[] { Keys.OemQuestion });
            add(4, 12.25f, 2.75f, "Shift", new[] { Keys.RShiftKey });
            add(5, 0, 1.25f, "Ctrl", new[] { Keys.ControlKey, Keys.LControlKey });
            add(5, 1.25f, 1.25f, "Win", new[] { Keys.LWin });
            add(5, 2.5f, 1.25f, "Alt", new[] { Keys.Menu, Keys.LMenu });
            add(5, 3.75f, 6.25f, "", new[] { Keys.Space });
            add(5, 10, 1.25f, "Alt", new[] { Keys.RMenu });
            add(5, 11.25f, 1.25f, "Win", new[] { Keys.RWin });
            add(5, 12.5f, 1.25f, "Menu", new[] { Keys.Apps });
            add(5, 13.75f, 1.25f, "Ctrl", new[] { Keys.RControlKey });
            return list;
        }

        // Keys that aren't on the main block (arrows, Home, numpad symbols...).
        public static bool OnBoard(Keys k)
        {
            foreach (var c in caps) if (Array.IndexOf(c.Keys, k) >= 0) return true;
            return false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            var t = T;
            float unit = (Width - 1) / 15f, gap = Math.Max(2f, unit * 0.12f);
            float rowH = (Height - 1 - gap * 5) / 6f;
            float fontSize = Math.Max(5.5f, Math.Min(8.5f, rowH / Ui.Scale * 0.42f));
            var font = Ui.F((float)Math.Round(fontSize * 2) / 2);
            var small = Ui.F(Math.Max(5f, (float)Math.Round(fontSize * 1.6f) / 2));
            foreach (var c in caps)
            {
                bool lit = false;
                foreach (var k in c.Keys) if (Lit.Contains(k)) { lit = true; break; }
                var r = new RectangleF(c.X * unit, c.Row * (rowH + gap), c.W * unit - gap, rowH);
                Ui.FillRound(g, r, Math.Max(2f, rowH * 0.18f), lit ? t.Accent : Ui.Blend(t.Input, t.Text, 0.05f),
                    lit ? t.Accent : Ui.Blend(t.Border, t.Text, 0.08f), 1);
                Ui.Text(g, c.Label, c.Label.Length > 3 ? small : font, Rectangle.Round(r), lit ? t.OnAccent : t.SubText, TextFormatFlags.HorizontalCenter);
            }
        }
    }

    // ---------------------------------------------------------------- slider

    // A horizontal slider from 0 to 1 (the timeline's zoom).
    class Slider : UiControl
    {
        double val = 0.5;
        bool drag;
        public event EventHandler ValueChanged;

        public Slider() { Cursor = Cursors.Hand; }

        public double Value
        {
            get { return val; }
            set { val = Math.Max(0, Math.Min(1, value)); Invalidate(); }
        }

        void SetFromMouse(int x)
        {
            int pad = Ui.S(8);
            Value = (x - pad) / (double)Math.Max(1, Width - 2 * pad);
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { drag = true; SetFromMouse(e.X); } }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (drag) SetFromMouse(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = false; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            int pad = Ui.S(8), cy = Height / 2;
            float x = pad + (float)(val * (Width - 2 * pad));
            Ui.FillRound(g, new RectangleF(pad, cy - Ui.S(2), Width - 2 * pad, Ui.S(4)), Ui.S(2), Ui.Blend(T.Input, T.Text, 0.15f), Color.Transparent, 0);
            Ui.FillRound(g, new RectangleF(pad, cy - Ui.S(2), x - pad, Ui.S(4)), Ui.S(2), Dim(T.Accent), Color.Transparent, 0);
            float k = Ui.S(14);
            using (var b = new SolidBrush(Dim(T.Text))) g.FillEllipse(b, x - k / 2, cy - k / 2, k, k);
        }
    }

    // ---------------------------------------------------------------- icon tiles

    // A small button with its icon above the text ("Key press", "Delay"...).
    class ToolTile : UiControl
    {
        public string Icon;

        public ToolTile() { Cursor = Cursors.Hand; Font = Ui.F(8.5f); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            Color fill = T.Input;
            if (pressed) fill = Ui.Blend(fill, Color.Black, 0.12f);
            else if (hover) fill = Ui.Blend(fill, T.Text, 0.08f);
            Ui.FillRound(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Ui.S(8), Dim(fill), Dim(T.Border), 1);
            int half = Height / 2;
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, Icon, Ui.Icons(12f), new Rectangle(0, Ui.S(2), Width, half), Dim(T.Text), flags | TextFormatFlags.Bottom);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(Ui.S(2), half + Ui.S(3), Width - Ui.S(4), half - Ui.S(5)), Dim(T.Text), flags | TextFormatFlags.Top);
        }
    }
}
