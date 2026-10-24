// Timeline view of a sequence: one track per key and mouse button (grouped into Keyboard and Mouse),
// with each press drawn as a bar. Drag a bar to move it, drag its ends to change when it starts or
// how long it's held; the cut tool splits a hold in two; double-click an empty spot to add a press.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Embris
{
    class TlEvent
    {
        public string Track;
        public int Kind;      // 0 key, 1 left click, 2 right click, 3 mouse move, 4 typing
        public int Code, X, Y;
        public string Text;
        public double Start, Dur; // seconds
        public int Order;     // original position, keeps ties in order

        public int Group { get { return Kind == 0 || Kind == 4 ? 0 : 1; } } // 0 keyboard, 1 mouse
        public TlEvent Copy() { return (TlEvent)MemberwiseClone(); }
    }

    class TimelineEditor : UiControl
    {
        public readonly List<TlEvent> Items = new List<TlEvent>();
        readonly List<string> tracks = new List<string>();            // in order of first use
        readonly Dictionary<string, int> trackKeys = new Dictionary<string, int>(); // keyboard track -> key
        readonly List<string> extraTracks = new List<string>();       // added with "Add track", kept while empty
        readonly HashSet<string> hidden = new HashSet<string>();      // eye turned off: bars not drawn
        readonly bool[] collapsed = new bool[2];
        readonly List<Row> rows = new List<Row>();
        double total, trailing; // length in seconds; trailing wait after the last input
        double pxPerSec = 200, scrollX;
        int scrollRow;
        TlEvent selected, dragging;  // selected: the one selected bar (null when none or several)
        readonly List<TlEvent> sel = new List<TlEvent>(); // every selected bar
        bool banding;                // dragging a selection rectangle
        Point bandFrom, bandTo;
        int groupDrag;               // several bars: 1 move, 2 stretch from the left end, 3 from the right end
        double groupStart0, groupEnd0, groupGrab;
        readonly Dictionary<TlEvent, double[]> groupOrig = new Dictionary<TlEvent, double[]>();
        double cropA = -1, cropB = -1; // crop bars (seconds); -1 when crop is off
        int cropDrag;                // 1 left bar, 2 right bar
        public event EventHandler CropChanged;
        int dragMode; // 1 move, 2 left edge, 3 right edge
        double dragOffset;
        bool rulerDrag, thumbDrag;
        int thumbGrab;
        double playhead = -1;
        public int SnapMs = 10;   // 0 = no snapping
        public bool CutTool;
        public event EventHandler SelectionChanged, Changed, ZoomChanged;

        class Row { public bool Header; public int Group; public string Track; }

        const int Gutter = 142, Ruler = 30, RowH = 36, ScrollH = 12;

        public TimelineEditor()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Font = Ui.F(8.5f);
        }

        public TlEvent Selected { get { return selected; } }
        public int SelectionCount { get { return sel.Count; } }
        public bool Cropping { get { return cropA >= 0; } }

        // Start and end of everything selected.
        public double GroupStart { get { double s = double.MaxValue; foreach (var e in sel) s = Math.Min(s, e.Start); return sel.Count == 0 ? 0 : s; } }
        public double GroupEnd { get { double s = 0; foreach (var e in sel) s = Math.Max(s, e.Start + e.Dur); return s; } }

        void SetSelection(IEnumerable<TlEvent> events)
        {
            sel.Clear();
            if (events != null) foreach (var e in events) if (e != null && !sel.Contains(e)) sel.Add(e);
            selected = sel.Count == 1 ? sel[0] : null;
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            Invalidate();
        }

        void SelectOnly(TlEvent e) { SetSelection(e == null ? null : new[] { e }); }

        public void SelectAll() { SetSelection(Items.FindAll(x => !hidden.Contains(x.Track))); }
        public double Total { get { return total; } }
        public double Playhead { get { return playhead; } set { playhead = value; Invalidate(); } }
        public double PxPerSec
        {
            get { return pxPerSec; }
            set { pxPerSec = Math.Max(5, Math.Min(5000, value)); Invalidate(); }
        }

        // ---------------------------------------------------------------- sequence <-> events

        static string KeyTrack(int code) { return Ui.KeyName((Keys)code); }

        public void Load(List<RecStep> seq)
        {
            Items.Clear();
            tracks.Clear();
            var openKeys = new Dictionary<int, TlEvent>();
            var openButtons = new Dictionary<int, TlEvent>();
            double t = 0, lastInput = 0;
            int order = 0;
            foreach (var s in seq)
            {
                t += s.Delay / 1000.0;
                TlEvent e = null;
                switch (s.Kind)
                {
                    case 0:
                        if (!openKeys.ContainsKey(s.Code))
                            openKeys[s.Code] = e = new TlEvent { Kind = 0, Code = s.Code, Track = KeyTrack(s.Code), Start = t };
                        break;
                    case 1:
                        TlEvent k;
                        if (openKeys.TryGetValue(s.Code, out k)) { k.Dur = t - k.Start; openKeys.Remove(s.Code); }
                        break;
                    case 2:
                        if (!openButtons.ContainsKey(s.Code))
                            openButtons[s.Code] = e = new TlEvent { Kind = s.Code == 1 ? 2 : 1, Code = s.Code, X = s.X, Y = s.Y, Start = t,
                                                                    Track = s.Code == 1 ? "Right click" : "Left click" };
                        break;
                    case 3:
                        TlEvent b;
                        if (openButtons.TryGetValue(s.Code, out b)) { b.Dur = t - b.Start; openButtons.Remove(s.Code); }
                        break;
                    case 5: e = new TlEvent { Kind = 3, X = s.X, Y = s.Y, Start = t, Track = "Mouse moves" }; break;
                    case 6: e = new TlEvent { Kind = 4, Text = s.Text, Start = t, Track = "Typing" }; break;
                }
                if (s.Kind != 4) lastInput = t;
                if (e != null)
                {
                    e.Order = order++;
                    Items.Add(e);
                }
            }
            foreach (var e in openKeys.Values) e.Dur = Math.Max(0, t - e.Start);   // never released: held to the end
            foreach (var e in openButtons.Values) e.Dur = Math.Max(0, t - e.Start);
            total = t;
            trailing = Math.Max(0, t - lastInput);
            var kept = new List<TlEvent>();
            foreach (var old in sel)
            {
                var o = old;
                var m = Items.Find(x => x.Kind == o.Kind && x.Code == o.Code && Math.Abs(x.Start - o.Start) < 0.0005);
                if (m != null) kept.Add(m);
            }
            sel.Clear();
            sel.AddRange(kept);
            selected = sel.Count == 1 ? sel[0] : null;
            BuildRows();
            Invalidate();
        }

        void BuildRows()
        {
            tracks.Clear();
            foreach (var e in Items)
            {
                if (tracks.Contains(e.Track)) continue;
                tracks.Add(e.Track);
                if (e.Kind == 0) trackKeys[e.Track] = e.Code;
            }
            foreach (var x in extraTracks) if (!tracks.Contains(x)) tracks.Add(x);
            rows.Clear();
            for (int g = 0; g < 2; g++)
            {
                var mine = tracks.FindAll(tr => GroupOf(tr) == g);
                if (mine.Count == 0) continue;
                rows.Add(new Row { Header = true, Group = g });
                if (collapsed[g]) continue;
                foreach (var tr in mine) rows.Add(new Row { Group = g, Track = tr });
            }
            scrollRow = Math.Max(0, Math.Min(scrollRow, Math.Max(0, rows.Count - 1)));
        }

        int GroupOf(string track)
        {
            return track == "Left click" || track == "Right click" || track == "Mouse moves" ? 1 : 0;
        }

        // Turns the bars back into steps. Delays come from rounded absolute times, so nothing drifts.
        public List<RecStep> ToSteps()
        {
            var atoms = new List<KeyValuePair<double, RecStep>>();
            foreach (var e in Items)
            {
                switch (e.Kind)
                {
                    case 0:
                        atoms.Add(new KeyValuePair<double, RecStep>(e.Start, new RecStep { Kind = 0, Code = e.Code }));
                        atoms.Add(new KeyValuePair<double, RecStep>(e.Start + e.Dur, new RecStep { Kind = 1, Code = e.Code }));
                        break;
                    case 1:
                    case 2:
                        atoms.Add(new KeyValuePair<double, RecStep>(e.Start, new RecStep { Kind = 2, Code = e.Code, X = e.X, Y = e.Y }));
                        atoms.Add(new KeyValuePair<double, RecStep>(e.Start + e.Dur, new RecStep { Kind = 3, Code = e.Code, X = e.X, Y = e.Y }));
                        break;
                    case 3: atoms.Add(new KeyValuePair<double, RecStep>(e.Start, new RecStep { Kind = 5, X = e.X, Y = e.Y })); break;
                    case 4: atoms.Add(new KeyValuePair<double, RecStep>(e.Start, new RecStep { Kind = 6, Text = e.Text })); break;
                }
            }
            // Stable sort by time (ties keep their order, and a release never comes before its press).
            var indexed = new List<int>();
            for (int i = 0; i < atoms.Count; i++) indexed.Add(i);
            indexed.Sort((a, b) =>
            {
                int c = atoms[a].Key.CompareTo(atoms[b].Key);
                return c != 0 ? c : a.CompareTo(b);
            });
            var steps = new List<RecStep>();
            long last = 0;
            foreach (int i in indexed)
            {
                long ms = (long)Math.Round(atoms[i].Key * 1000);
                var s = atoms[i].Value;
                s.Delay = (int)Math.Max(0, ms - last);
                last = Math.Max(last, ms);
                steps.Add(s);
            }
            if (trailing > 0.0005) steps.Add(new RecStep { Kind = 4, Delay = (int)Math.Round(trailing * 1000) });
            return steps;
        }

        // ---------------------------------------------------------------- editing

        public void SetSelected(double start, double dur)
        {
            if (selected == null) { SetGroup(start, dur); return; }
            selected.Start = Math.Max(0, start);
            if (selected.Kind <= 2) selected.Dur = Math.Max(0, dur);
            Commit();
        }

        public void SetSelectedPosition(int x, int y)
        {
            if (selected == null) return;
            selected.X = x;
            selected.Y = y;
            Commit();
        }

        public void SetSelectedKey(Keys key)
        {
            if (selected == null || selected.Kind != 0) return;
            selected.Code = (int)key;
            selected.Track = KeyTrack((int)key);
            Commit();
        }

        public void SetSelectedText(string text)
        {
            if (selected == null || selected.Kind != 4) return;
            selected.Text = text;
            Commit();
        }

        // Action type: 0 key hold, 1 left click, 2 right click, 3 mouse move, 4 typing.
        public void SetSelectedKind(int kind, Keys key)
        {
            var e = selected;
            if (e == null || e.Kind == kind) return;
            e.Kind = kind;
            switch (kind)
            {
                case 0: e.Code = (int)key; e.Track = KeyTrack(e.Code); if (e.Dur <= 0) e.Dur = 0.05; break;
                case 1: e.Code = 0; e.Track = "Left click"; e.X = e.Y = RecStep.NoPosition; if (e.Dur <= 0) e.Dur = 0.04; break;
                case 2: e.Code = 1; e.Track = "Right click"; e.X = e.Y = RecStep.NoPosition; if (e.Dur <= 0) e.Dur = 0.04; break;
                case 3: e.Track = "Mouse moves"; e.X = 10; e.Y = 0; e.Dur = 0; break;
                default: e.Track = "Typing"; e.Text = "hello"; e.Dur = 0; break;
            }
            Commit();
        }

        public void DeleteSelected()
        {
            if (sel.Count == 0) return;
            foreach (var e in sel) Items.Remove(e);
            Commit();
            SetSelection(null);
        }

        // Copies right after the originals (a group stays together).
        public void DuplicateSelected()
        {
            if (sel.Count == 0) return;
            double shift = Math.Max(GroupEnd - GroupStart, 0.01) + SnapSec(0.05);
            var copies = new List<TlEvent>();
            foreach (var e in sel)
            {
                var c = e.Copy();
                c.Start = e.Start + shift;
                Items.Add(c);
                copies.Add(c);
            }
            Commit();
            SetSelection(copies);
        }

        // Several bars: move them to start at "start" and squish or stretch them to last "span" seconds.
        public void SetGroup(double start, double span)
        {
            if (sel.Count < 2) return;
            double s0 = GroupStart, len = GroupEnd - s0;
            double k = len > 0.0005 ? Math.Max(0, span) / len : 1;
            foreach (var e in sel)
            {
                e.Start = Math.Max(0, start) + (e.Start - s0) * k;
                e.Dur *= k;
            }
            Commit();
        }

        // ---------------------------------------------------------------- cut & crop

        // Cuts every hold that crosses this time, splitting the sequence into sections.
        public void SplitAll(double at)
        {
            foreach (var e in Items.ToArray())
                if (e.Kind <= 2 && !hidden.Contains(e.Track) && at > e.Start + 0.02 && at < e.Start + e.Dur - 0.02) Split(e, at, false);
            Commit();
            Playhead = at;
        }

        // Shows the two crop bars (around the selection, or the whole sequence).
        public void StartCrop()
        {
            if (sel.Count > 1) { cropA = GroupStart; cropB = GroupEnd; }
            else { cropA = 0; cropB = Math.Max(0.1, total); }
            if (CropChanged != null) CropChanged(this, EventArgs.Empty);
            Invalidate();
        }

        public void CancelCrop()
        {
            cropA = cropB = -1;
            if (CropChanged != null) CropChanged(this, EventArgs.Empty);
            Invalidate();
        }

        // Keeps only the part between the crop bars (holds crossing a bar are trimmed) and starts it at 0.
        public void ApplyCrop()
        {
            if (cropA < 0) return;
            double a = Math.Min(cropA, cropB), b = Math.Max(cropA, cropB);
            foreach (var e in Items.ToArray())
            {
                double end = e.Start + e.Dur;
                if (end < a || e.Start > b || (e.Dur == 0 && e.Start < a)) { Items.Remove(e); continue; }
                double s = Math.Max(e.Start, a);
                e.Dur = Math.Min(end, b) - s;
                e.Start = s - a;
            }
            double last = 0;
            foreach (var e in Items) last = Math.Max(last, e.Start + e.Dur);
            trailing = Math.Max(0, (b - a) - last);
            cropA = cropB = -1;
            sel.Clear();
            selected = null;
            Commit();
            if (CropChanged != null) CropChanged(this, EventArgs.Empty);
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        // An empty track for a key, ready to add presses to (double-click it).
        public void AddTrack(Keys key)
        {
            string name = KeyTrack((int)key);
            trackKeys[name] = (int)key;
            if (!extraTracks.Contains(name)) extraTracks.Add(name);
            collapsed[0] = false;
            BuildRows();
            int r = rows.FindIndex(x => x.Track == name);
            if (r >= 0) EnsureRowVisible(r);
            Invalidate();
        }

        public void Zoom(double factor)
        {
            PxPerSec = pxPerSec * factor;
            if (ZoomChanged != null) ZoomChanged(this, EventArgs.Empty);
        }

        public void Fit()
        {
            double span = Math.Max(0.5, total);
            pxPerSec = Math.Max(5, (Width - Ui.S(Gutter) - Ui.S(24)) / span);
            scrollX = 0;
            Invalidate();
            if (ZoomChanged != null) ZoomChanged(this, EventArgs.Empty);
        }

        void Commit()
        {
            Items.Sort((a, b) => a.Start.CompareTo(b.Start));
            double end = 0;
            foreach (var e in Items) end = Math.Max(end, e.Start + e.Dur);
            total = end + trailing;
            BuildRows();
            if (Changed != null) Changed(this, EventArgs.Empty);
            Invalidate();
        }

        // ---------------------------------------------------------------- geometry

        int X(double sec) { return Ui.S(Gutter) + (int)Math.Round((sec - scrollX) * pxPerSec); }
        double Sec(int x) { return scrollX + (x - Ui.S(Gutter)) / pxPerSec; }
        int RowY(int row) { return Ui.S(Ruler) + (row - scrollRow) * Ui.S(RowH); }
        int RowsBottom { get { return Height - Ui.S(ScrollH) - Ui.S(4); } }
        int VisibleRows { get { return Math.Max(1, (RowsBottom - Ui.S(Ruler)) / Ui.S(RowH)); } }
        double Span { get { return Math.Max(total, scrollX + (Width - Ui.S(Gutter)) / pxPerSec) + 0.5; } }

        void EnsureRowVisible(int r)
        {
            if (r < scrollRow) scrollRow = r;
            else if (r >= scrollRow + VisibleRows) scrollRow = r - VisibleRows + 1;
        }

        Rectangle BarRect(TlEvent e)
        {
            int row = rows.FindIndex(rw => !rw.Header && rw.Track == e.Track);
            if (row < 0) return Rectangle.Empty;
            int x = X(e.Start), w = Math.Max(Ui.S(e.Kind >= 3 ? 3 : 10), (int)Math.Round(e.Dur * pxPerSec));
            return new Rectangle(x, RowY(row) + Ui.S(5), w, Ui.S(RowH) - Ui.S(10));
        }

        Rectangle ScrollTrack { get { return new Rectangle(Ui.S(Gutter), Height - Ui.S(ScrollH) - Ui.S(2), Width - Ui.S(Gutter) - Ui.S(6), Ui.S(ScrollH) - Ui.S(2)); } }

        Rectangle Thumb
        {
            get
            {
                var tr = ScrollTrack;
                double view = (Width - Ui.S(Gutter)) / pxPerSec;
                int w = Math.Max(Ui.S(30), (int)(tr.Width * Math.Min(1, view / Span)));
                int x = tr.X + (int)((tr.Width - w) * Math.Min(1, scrollX / Math.Max(0.001, Span - view)));
                return new Rectangle(x, tr.Y + Ui.S(2), w, tr.Height - Ui.S(4));
            }
        }

        TlEvent HitTest(Point p, out int mode)
        {
            mode = 0;
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                var e = Items[i];
                if (hidden.Contains(e.Track)) continue;
                var r = BarRect(e);
                if (r.IsEmpty || r.Top < Ui.S(Ruler) - 2) continue;
                r.Inflate(Ui.S(3), 0);
                if (!r.Contains(p)) continue;
                if (e.Kind <= 2 && r.Width > Ui.S(20))
                {
                    if (p.X <= r.Left + Ui.S(7)) mode = 2;
                    else if (p.X >= r.Right - Ui.S(7)) mode = 3;
                    else mode = 1;
                }
                else mode = 1;
                return e;
            }
            return null;
        }

        double SnapSec(double sec) { return SnapMs <= 0 ? sec : Math.Round(sec * 1000 / SnapMs) * SnapMs / 1000.0; }

        // ---------------------------------------------------------------- mouse & keys

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            base.OnMouseDown(e);
            int gutter = Ui.S(Gutter);
            if (e.Y >= ScrollTrack.Y - Ui.S(2) && e.X >= gutter)
            {
                var th = Thumb;
                if (th.Contains(new Point(e.X, th.Y + 1))) { thumbDrag = true; thumbGrab = e.X - th.X; }
                else { scrollX = Math.Max(0, scrollX + (e.X < th.X ? -1 : 1) * (Width - gutter) * 0.8 / pxPerSec); Invalidate(); }
                return;
            }
            if (e.Y < Ui.S(Ruler) && e.X >= gutter)
            {
                rulerDrag = true;
                Playhead = Math.Max(0, SnapSec(Sec(e.X)));
                return;
            }
            if (e.X < gutter)
            {
                int r = scrollRow + (e.Y - Ui.S(Ruler)) / Ui.S(RowH);
                if (e.Y < Ui.S(Ruler) || r < 0 || r >= rows.Count) return;
                var row = rows[r];
                if (row.Header) { collapsed[row.Group] = !collapsed[row.Group]; BuildRows(); }
                else if (e.X >= gutter - Ui.S(30))
                {
                    if (!hidden.Remove(row.Track)) hidden.Add(row.Track);
                    if (sel.Exists(x => hidden.Contains(x.Track))) SetSelection(sel.FindAll(x => !hidden.Contains(x.Track)));
                }
                Invalidate();
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            // Crop bars and their buttons.
            if (cropA >= 0)
            {
                if (cropApply.Contains(e.Location)) { ApplyCrop(); return; }
                if (cropCancel.Contains(e.Location)) { CancelCrop(); return; }
                int hitBar = CropBarAt(e.X);
                if (hitBar > 0) { cropDrag = hitBar; return; }
            }
            int mode;
            var hit = HitTest(e.Location, out mode);
            if (CutTool)
            {
                // Cut tool: a bar is split where clicked; an empty spot cuts every track there.
                double at = SnapSec(Sec(e.X));
                if (hit != null && hit.Kind <= 2) Split(hit, at, true);
                else if (hit == null) SplitAll(at);
                return;
            }
            // Several bars selected: drag the ends of the group to squish or stretch it.
            int groupEdge = GroupEdgeAt(e.Location);
            if (groupEdge > 0) { BeginGroupDrag(groupEdge, e.X); return; }
            bool add = (ModifierKeys & (Keys.Shift | Keys.Control)) != 0;
            if (hit == null)
            {
                // Empty spot: drag a rectangle to select several bars.
                if (!add) SetSelection(null);
                banding = true;
                bandFrom = bandTo = e.Location;
                return;
            }
            if (add)
            {
                var list = new List<TlEvent>(sel);
                if (!list.Remove(hit)) list.Add(hit);
                SetSelection(list);
                return;
            }
            if (sel.Count > 1 && sel.Contains(hit)) { BeginGroupDrag(1, e.X); return; } // move them all
            SelectOnly(hit);
            dragging = hit;
            dragMode = mode;
            dragOffset = Sec(e.X) - hit.Start;
        }

        Rectangle cropApply, cropCancel; // the crop bar buttons, as last drawn

        int CropBarAt(int x)
        {
            if (cropA < 0) return 0;
            if (Math.Abs(x - X(cropA)) <= Ui.S(6)) return 1;
            if (Math.Abs(x - X(cropB)) <= Ui.S(6)) return 2;
            return 0;
        }

        // The box around several selected bars (empty when fewer than two).
        Rectangle GroupRect
        {
            get
            {
                if (sel.Count < 2) return Rectangle.Empty;
                Rectangle r = Rectangle.Empty;
                foreach (var e in sel)
                {
                    var b = BarRect(e);
                    if (b.IsEmpty) continue;
                    r = r.IsEmpty ? b : Rectangle.Union(r, b);
                }
                if (r.IsEmpty) return r;
                int x1 = X(GroupStart), x2 = Math.Max(X(GroupEnd), x1 + Ui.S(6));
                return new Rectangle(x1, r.Top - Ui.S(3), x2 - x1, r.Height + Ui.S(6));
            }
        }

        int GroupEdgeAt(Point p)
        {
            var g = GroupRect;
            if (g.IsEmpty || p.Y < g.Top || p.Y > g.Bottom) return 0;
            if (Math.Abs(p.X - g.Left) <= Ui.S(6)) return 2;
            if (Math.Abs(p.X - g.Right) <= Ui.S(6)) return 3;
            return 0;
        }

        void BeginGroupDrag(int kind, int x)
        {
            groupDrag = kind;
            groupStart0 = GroupStart;
            groupEnd0 = GroupEnd;
            groupGrab = Sec(x);
            groupOrig.Clear();
            foreach (var e in sel) groupOrig[e] = new[] { e.Start, e.Dur };
        }

        // Moves (1) or rescales (2 = left end, 3 = right end) the selected bars from where the drag began.
        void GroupDragTo(int x)
        {
            double a = groupStart0, b = groupEnd0, len = Math.Max(0.0005, b - a);
            double s = groupDrag == 1 ? 0 : SnapToKey(SnapSec(Sec(x)), sel);
            double move = SnapSec(Sec(x) - groupGrab);
            if (groupDrag == 1) move = SnapMove(a + move, b + move, sel) - a; // either end of the group may catch a key
            foreach (var kv in groupOrig)
            {
                double st = kv.Value[0], du = kv.Value[1];
                if (groupDrag == 1)
                {
                    kv.Key.Start = Math.Max(0, st + Math.Max(move, -a));
                }
                else if (groupDrag == 3)
                {
                    double k = Math.Max(0.01, (s - a) / len);
                    kv.Key.Start = a + (st - a) * k;
                    kv.Key.Dur = du * k;
                }
                else
                {
                    double k = Math.Max(0.01, (b - Math.Max(0, s)) / len);
                    kv.Key.Start = b - (b - st) * k;
                    kv.Key.Dur = du * k;
                }
            }
            foreach (var e in sel) total = Math.Max(total, e.Start + e.Dur);
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            Invalidate();
        }

        // Cuts a hold in two at the given time, with a 10 ms gap so the key is really let go.
        void Split(TlEvent e, double at, bool commit)
        {
            double end = e.Start + e.Dur;
            if (at <= e.Start + 0.02 || at >= end - 0.02) return;
            var second = e.Copy();
            e.Dur = at - e.Start - 0.01;
            second.Start = at;
            second.Dur = end - at;
            Items.Add(second);
            if (!commit) return;
            Commit();
            SelectOnly(second);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int gutter = Ui.S(Gutter);
            if (e.X < gutter || e.Y < Ui.S(Ruler) || e.Y > RowsBottom) return;
            int mode;
            if (HitTest(e.Location, out mode) != null) return;
            int r = scrollRow + (e.Y - Ui.S(Ruler)) / Ui.S(RowH);
            if (r < 0 || r >= rows.Count || rows[r].Header) return;
            string track = rows[r].Track;
            // Double-click an empty spot on a track: add a press there.
            var n = new TlEvent { Track = track, Start = Math.Max(0, SnapSec(Sec(e.X))), Dur = 0.1 };
            if (track == "Left click" || track == "Right click") { n.Kind = track == "Left click" ? 1 : 2; n.Code = n.Kind - 1; n.X = n.Y = RecStep.NoPosition; }
            else if (track == "Mouse moves") { n.Kind = 3; n.X = 10; n.Dur = 0; }
            else if (track == "Typing") { n.Kind = 4; n.Text = "hello"; n.Dur = 0; }
            else
            {
                int code;
                if (!trackKeys.TryGetValue(track, out code)) return;
                n.Kind = 0;
                n.Code = code;
            }
            n.Order = Items.Count;
            Items.Add(n);
            Commit();
            SelectOnly(n);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (thumbDrag)
            {
                var tr = ScrollTrack;
                double view = (Width - Ui.S(Gutter)) / pxPerSec;
                int room = tr.Width - Thumb.Width;
                if (room > 0) scrollX = Math.Max(0, (e.X - tr.X - thumbGrab) * (Span - view) / room);
                Invalidate();
                return;
            }
            if (rulerDrag) { Playhead = Math.Max(0, SnapSec(Sec(e.X))); return; }
            if (cropDrag > 0)
            {
                double s = Math.Max(0, SnapToKey(SnapSec(Sec(e.X)), null));
                if (cropDrag == 1) cropA = s; else cropB = s;
                Invalidate();
                return;
            }
            if (banding) { bandTo = e.Location; Invalidate(); return; }
            if (groupDrag > 0) { GroupDragTo(e.X); return; }
            if (dragging != null)
            {
                var self = new[] { dragging };
                double s = dragMode == 1 ? 0 : SnapToKey(SnapSec(Sec(e.X)), self);
                if (dragMode == 1)
                {
                    double start = Math.Max(0, SnapSec(Sec(e.X) - dragOffset));
                    dragging.Start = Math.Max(0, SnapMove(start, start + dragging.Dur, self)); // its start or end may catch a key
                }
                else if (dragMode == 2)
                {
                    double end = dragging.Start + dragging.Dur;
                    dragging.Start = Math.Max(0, Math.Min(s, end));
                    dragging.Dur = end - dragging.Start;
                }
                else if (dragMode == 3) dragging.Dur = Math.Max(0, s - dragging.Start);
                total = Math.Max(total, dragging.Start + dragging.Dur);
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
                Invalidate();
                return;
            }
            if (e.X < Ui.S(Gutter)) { Cursor = Cursors.Hand; return; }
            if (CropBarAt(e.X) > 0 || GroupEdgeAt(e.Location) > 0) { Cursor = Cursors.SizeWE; return; }
            if (cropApply.Contains(e.Location) || cropCancel.Contains(e.Location)) { Cursor = Cursors.Hand; return; }
            int mode;
            var hit = HitTest(e.Location, out mode);
            Cursor = hit == null ? (CutTool ? Cursors.VSplit : e.Y < Ui.S(Ruler) ? Cursors.Hand : Cursors.Default)
                   : CutTool && hit.Kind <= 2 ? Cursors.VSplit
                   : mode == 2 || mode == 3 ? Cursors.SizeWE : Cursors.Hand;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            rulerDrag = thumbDrag = false;
            cropDrag = 0;
            if (snapGuide >= 0) { snapGuide = -1; Invalidate(); }
            if (banding)
            {
                // Select every visible bar the rectangle touches.
                banding = false;
                var band = BandRect;
                var list = (ModifierKeys & (Keys.Shift | Keys.Control)) != 0 ? new List<TlEvent>(sel) : new List<TlEvent>();
                foreach (var ev in Items)
                {
                    if (hidden.Contains(ev.Track) || list.Contains(ev)) continue;
                    var r = BarRect(ev);
                    if (!r.IsEmpty && r.IntersectsWith(band)) list.Add(ev);
                }
                SetSelection(list);
                return;
            }
            if (groupDrag > 0)
            {
                groupDrag = 0;
                Commit();
                return;
            }
            if (dragging != null)
            {
                dragging = null;
                Commit();
            }
        }

        // ---------------------------------------------------------------- snapping to keys

        public bool SnapToKeys = true;   // edges catch the starts and ends of other bars (and the playhead)
        double snapGuide = -1;           // where the last snap happened, drawn as a guide line while dragging

        // The nearest key edge within a few pixels of t, or -1 when there's none (or snapping is off).
        double NearestKey(double t, ICollection<TlEvent> exclude, out double distance)
        {
            distance = double.MaxValue;
            if (!SnapToKeys) return -1;
            double reach = Ui.S(8) / pxPerSec, best = -1, nearest = double.MaxValue;
            var keys = new List<double> { 0 };
            if (playhead >= 0) keys.Add(playhead);
            foreach (var e in Items)
            {
                if (hidden.Contains(e.Track) || (exclude != null && exclude.Contains(e))) continue;
                keys.Add(e.Start);
                if (e.Dur > 0) keys.Add(e.Start + e.Dur);
            }
            foreach (double k in keys)
            {
                double d = Math.Abs(k - t);
                if (d <= reach && d < nearest) { nearest = d; best = k; }
            }
            distance = nearest;
            return best;
        }

        // An edge being dragged: catches a nearby key.
        double SnapToKey(double t, ICollection<TlEvent> exclude)
        {
            double dist, k = NearestKey(t, exclude, out dist);
            snapGuide = k;
            return k >= 0 ? k : t;
        }

        // Something being moved, from start to end: whichever end is closer to a key catches it.
        // Returns the new start.
        double SnapMove(double start, double end, ICollection<TlEvent> exclude)
        {
            double d1, d2;
            double k1 = NearestKey(start, exclude, out d1), k2 = NearestKey(end, exclude, out d2);
            if (k1 < 0 && k2 < 0) { snapGuide = -1; return start; }
            if (k1 >= 0 && d1 <= d2) { snapGuide = k1; return k1; }
            snapGuide = k2;
            return start + (k2 - end);
        }

        Rectangle BandRect
        {
            get
            {
                return Rectangle.FromLTRB(Math.Min(bandFrom.X, bandTo.X), Math.Min(bandFrom.Y, bandTo.Y),
                                          Math.Max(bandFrom.X, bandTo.X) + 1, Math.Max(bandFrom.Y, bandTo.Y) + 1);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) != 0)
            {
                double at = Sec(e.X);
                Zoom(e.Delta > 0 ? 1.25 : 0.8);
                scrollX = Math.Max(0, at - (e.X - Ui.S(Gutter)) / pxPerSec); // zoom around the mouse
            }
            else if ((ModifierKeys & Keys.Shift) == 0 && rows.Count > VisibleRows)
                scrollRow = Math.Max(0, Math.Min(rows.Count - VisibleRows, scrollRow - Math.Sign(e.Delta)));
            else
                scrollX = Math.Max(0, Math.Min(Math.Max(0, total - 0.5), scrollX - Math.Sign(e.Delta) * 80 / pxPerSec));
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override bool IsInputKey(Keys k)
        {
            var c = k & Keys.KeyCode;
            return c == Keys.Delete || c == Keys.Left || c == Keys.Right || c == Keys.Escape || c == Keys.Enter || base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete) DeleteSelected();
            else if (e.KeyCode == Keys.D && e.Control) DuplicateSelected();
            else if (e.KeyCode == Keys.A && e.Control) SelectAll();
            else if (e.KeyCode == Keys.Enter && cropA >= 0) ApplyCrop();
            else if (e.KeyCode == Keys.Escape) { if (cropA >= 0) CancelCrop(); else SetSelection(null); }
            else if (sel.Count > 0 && (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right))
            {
                // Nudge the selected bars by one snap step.
                double step = (e.KeyCode == Keys.Right ? 1 : -1) * Math.Max(1, SnapMs) / 1000.0;
                if (GroupStart + step < 0) step = -GroupStart;
                foreach (var x in sel) x.Start += step;
                Commit();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
            base.OnKeyDown(e);
        }

        // ---------------------------------------------------------------- drawing

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = Smooth(pe);
            var t = T;
            Ui.FillRound(g, new RectangleF(0.75f, 0.75f, Width - 1.5f, Height - 1.5f), Ui.S(6), t.Input, Focused ? Ui.Blend(t.Border, t.Accent, 0.5f) : t.Border, 1);
            int gutter = Ui.S(Gutter), ruler = Ui.S(Ruler), rowH = Ui.S(RowH), bottom = RowsBottom;
            if (Items.Count == 0 && extraTracks.Count == 0)
            {
                Ui.Text(g, "Nothing to show yet. Record a sequence or add steps, and each key gets its own track here.",
                    Ui.F(9f), new Rectangle(Ui.S(16), 0, Width - Ui.S(32), Height), t.SubText, TextFormatFlags.HorizontalCenter);
                return;
            }

            // Track rows, group headers and names.
            g.SetClip(new Rectangle(0, ruler, Width, bottom - ruler));
            for (int r = scrollRow; r < rows.Count; r++)
            {
                int y = RowY(r);
                if (y > bottom) break;
                var row = rows[r];
                if (row.Header)
                {
                    Ui.Text(g, collapsed[row.Group] ? "" : "", Ui.Icons(8f), new Rectangle(Ui.S(12), y, Ui.S(16), rowH), t.SubText, 0);
                    Ui.Text(g, row.Group == 0 ? "KEYBOARD" : "MOUSE", Ui.Semi(8.5f), new Rectangle(Ui.S(32), y, gutter - Ui.S(36), rowH), t.Text, 0);
                    continue;
                }
                using (var b = new SolidBrush(Ui.Blend(t.Input, t.Text, r % 2 == 0 ? 0.03f : 0.015f))) g.FillRectangle(b, gutter, y + 1, Width - gutter - Ui.S(2), rowH - 2);
                bool off = hidden.Contains(row.Track);
                Color nameColor = off ? Ui.Blend(t.SubText, t.Input, 0.4f) : t.Text;
                bool icon = row.Group == 1 || row.Track == "Typing";
                // Keys show their name on a key cap; mouse tracks and typing get an icon and a name.
                var chip = new RectangleF(Ui.S(12), y + (rowH - Ui.S(24)) / 2f, icon ? Ui.S(30) : gutter - Ui.S(48), Ui.S(24));
                Ui.FillRound(g, chip, Ui.S(4), off ? Ui.Blend(t.Input, t.Text, 0.06f) : Ui.Blend(t.Input, t.Accent, 0.16f),
                    off ? Ui.Blend(t.Border, t.Text, 0.15f) : Ui.Blend(t.Border, t.Accent, 0.55f), 1);
                string chipText = row.Group == 1 ? (row.Track == "Mouse moves" ? "\uE7C2" : "\uE962") : row.Track == "Typing" ? "\uE8D2" : row.Track;
                Ui.Text(g, chipText, icon ? Ui.Icons(9f) : Ui.F(8.5f), Rectangle.Round(chip), nameColor, TextFormatFlags.HorizontalCenter);
                if (icon) Ui.Text(g, row.Track, Ui.F(9f), new Rectangle(Ui.S(48), y, gutter - Ui.S(82), rowH), nameColor, 0);
                Ui.Text(g, off ? "" : "", Ui.Icons(10f), new Rectangle(gutter - Ui.S(28), y, Ui.S(22), rowH), off ? t.SubText : t.Accent, TextFormatFlags.HorizontalCenter);
            }
            g.ResetClip();
            using (var pen = new Pen(t.Border)) g.DrawLine(pen, gutter - 1, Ui.S(4), gutter - 1, Height - Ui.S(4));

            // Ruler with a label every "nice" interval.
            double[] steps = { 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 30, 60, 120, 300, 600, 1800, 3600 };
            double stepSec = 3600;
            foreach (var s in steps) if (s * pxPerSec >= Ui.S(70)) { stepSec = s; break; }
            var clipState = g.Save();
            g.SetClip(new Rectangle(gutter, 0, Width - gutter - Ui.S(2), Height));
            using (var tick = new Pen(Ui.Blend(t.Border, t.Input, 0.3f)))
            using (var minor = new Pen(Ui.Blend(t.Border, t.Input, 0.55f)))
            {
                double first = Math.Floor(scrollX / stepSec) * stepSec;
                for (double s = first; X(s) < Width; s += stepSec)
                {
                    int x = X(s);
                    for (int m = 1; m < 5; m++)
                    {
                        int mx = X(s + stepSec * m / 5);
                        if (mx > gutter) g.DrawLine(minor, mx, ruler - Ui.S(5), mx, ruler - 1);
                    }
                    if (x < gutter) continue;
                    g.DrawLine(tick, x, ruler - Ui.S(10), x, bottom);
                    Ui.Text(g, s < 60 ? s.ToString(stepSec < 0.1 ? "0.00" : "0.0") + "s" : (s / 60).ToString("0.#") + "m",
                        Font, new Rectangle(x + Ui.S(3), Ui.S(3), Ui.S(60), ruler - Ui.S(14)), t.SubText, 0);
                }
            }
            // End marker.
            using (var endPen = new Pen(Color.FromArgb(120, t.Accent), 1) { DashStyle = DashStyle.Dash })
                g.DrawLine(endPen, X(total), ruler, X(total), bottom);

            // Bars.
            g.SetClip(new Rectangle(gutter, ruler, Width - gutter - Ui.S(2), bottom - ruler));
            int lastMoveX = int.MinValue; // mouse moves come in by the thousand: draw one per pixel column
            foreach (var e in Items)
            {
                if (hidden.Contains(e.Track)) continue;
                var r = BarRect(e);
                if (r.IsEmpty || r.Right < gutter || r.Left > Width || r.Bottom < ruler || r.Top > bottom) continue;
                if (e.Kind == 3 && e != selected) { if (r.X == lastMoveX) continue; lastMoveX = r.X; }
                // Theme colors: keys in the accent, clicks in a lighter accent, typing in green.
                Color tone = e.Kind == 0 ? t.Accent : e.Kind <= 2 ? Ui.Blend(t.Accent, t.Text, 0.45f) : e.Kind == 3 ? t.SubText : Ui.Success;
                Color fill = Ui.Blend(t.Input, tone, e.Kind == 3 ? 0.45f : 0.42f);
                bool isSel = sel.Contains(e);
                Ui.FillRound(g, r, Ui.S(5), fill, isSel ? t.Accent : Ui.Blend(fill, tone, 0.6f), isSel ? 2 : 1);
                if (e.Kind != 3 && r.Width > Ui.S(26))
                {
                    string label = e.Kind == 4 ? "\"" + e.Text + "\"" : e.Kind == 0 ? e.Track : e.Track;
                    if (e.Dur >= 0.12 && e.Kind == 0) label += " (hold)";
                    Ui.Text(g, label, Font, new Rectangle(r.X + Ui.S(6), r.Y, r.Width - Ui.S(10), r.Height), t.Text, TextFormatFlags.HorizontalCenter);
                }
                if (e == selected && e.Kind <= 2)
                {
                    Ui.FillRound(g, new RectangleF(r.Left - Ui.S(2), r.Top + Ui.S(5), Ui.S(5), r.Height - Ui.S(10)), Ui.S(2), t.Accent, t.Accent, 0);
                    Ui.FillRound(g, new RectangleF(r.Right - Ui.S(3), r.Top + Ui.S(5), Ui.S(5), r.Height - Ui.S(10)), Ui.S(2), t.Accent, t.Accent, 0);
                }
            }

            // Several selected: a box around them with a handle on each end to squish or stretch them.
            var grp = GroupRect;
            if (!grp.IsEmpty)
            {
                using (var pen = new Pen(Color.FromArgb(200, t.Accent), 1) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(pen, grp);
                foreach (int hx in new[] { grp.Left, grp.Right })
                    Ui.FillRound(g, new RectangleF(hx - Ui.S(3), grp.Top + grp.Height / 2f - Ui.S(10), Ui.S(6), Ui.S(20)), Ui.S(3), t.Accent, t.Text, 1);
            }
            // Snap guide while dragging onto a key.
            if (snapGuide >= 0 && (dragging != null || groupDrag > 0 || cropDrag > 0))
                using (var pen = new Pen(Color.FromArgb(220, t.Accent), Ui.S(1.5f)) { DashStyle = DashStyle.Dot })
                    g.DrawLine(pen, X(snapGuide), ruler, X(snapGuide), bottom);
            if (banding)
            {
                var band = BandRect;
                using (var b = new SolidBrush(Color.FromArgb(40, t.Accent))) g.FillRectangle(b, band);
                using (var pen = new Pen(t.Accent, 1)) g.DrawRectangle(pen, band);
            }
            g.Restore(clipState);

            // Crop: everything outside the two bars is dimmed; drag the bars, then click Crop.
            cropApply = cropCancel = Rectangle.Empty;
            if (cropA >= 0)
            {
                int xa = X(Math.Min(cropA, cropB)), xb = X(Math.Max(cropA, cropB));
                var keep = g.Save();
                g.SetClip(new Rectangle(gutter, 0, Width - gutter - Ui.S(2), bottom));
                using (var shade = new SolidBrush(Color.FromArgb(150, t.Input)))
                {
                    g.FillRectangle(shade, gutter, ruler, Math.Max(0, xa - gutter), bottom - ruler);
                    g.FillRectangle(shade, xb, ruler, Math.Max(0, Width - xb), bottom - ruler);
                }
                foreach (int x in new[] { xa, xb })
                {
                    using (var pen = new Pen(t.Accent, Ui.S(2))) g.DrawLine(pen, x, Ui.S(2), x, bottom);
                    Ui.FillRound(g, new RectangleF(x - Ui.S(4), ruler + (bottom - ruler) / 2f - Ui.S(14), Ui.S(8), Ui.S(28)), Ui.S(4), t.Accent, t.Text, 1);
                }
                g.Restore(keep);
            }

            // Playhead with its time.
            if (playhead >= 0 && X(playhead) >= gutter && X(playhead) < Width)
            {
                int x = X(playhead);
                using (var pen = new Pen(t.Text, Ui.S(1.5f))) g.DrawLine(pen, x, ruler - Ui.S(4), x, bottom);
                string label = playhead.ToString("0.00") + "s";
                int w = TextRenderer.MeasureText(label, Ui.Semi(8f)).Width + Ui.S(10);
                var badge = new RectangleF(Math.Max(gutter, x - w / 2f), Ui.S(2), w, Ui.S(18));
                Ui.FillRound(g, badge, Ui.S(4), t.Accent, t.Accent, 0);
                Ui.Text(g, label, Ui.Semi(8f), Rectangle.Round(badge), t.OnAccent, TextFormatFlags.HorizontalCenter);
                using (var b = new SolidBrush(t.Text)) g.FillEllipse(b, x - Ui.S(4), ruler - Ui.S(8), Ui.S(8), Ui.S(8));
            }

            // Crop and cancel buttons, on top of everything, just under the ruler between the bars.
            if (cropA >= 0)
            {
                int xa = X(Math.Min(cropA, cropB)), xb = X(Math.Max(cropA, cropB));
                var f = Ui.Semi(8.5f);
                int bw = TextRenderer.MeasureText("Crop", f).Width + Ui.S(18), bh = Ui.S(22);
                int mid = Math.Max(gutter + Ui.S(4), Math.Min(Width - bw - bh - Ui.S(12), (xa + xb) / 2 - (bw + bh + Ui.S(4)) / 2));
                cropApply = new Rectangle(mid, ruler + Ui.S(4), bw, bh);
                cropCancel = new Rectangle(mid + bw + Ui.S(4), ruler + Ui.S(4), bh, bh);
                Ui.FillRound(g, cropApply, Ui.S(5), t.Accent, t.Accent, 0);
                Ui.Text(g, "Crop", f, cropApply, t.OnAccent, TextFormatFlags.HorizontalCenter);
                Ui.FillRound(g, cropCancel, Ui.S(5), Ui.Blend(t.Input, t.Text, 0.12f), t.Border, 1);
                Ui.Text(g, "\u00d7", f, cropCancel, t.Text, TextFormatFlags.HorizontalCenter);
            }

            // Ruler line, row scroll hint and the time scrollbar.
            using (var pen = new Pen(t.Border)) g.DrawLine(pen, Ui.S(4), ruler - 1, Width - Ui.S(4), ruler - 1);
            if (rows.Count > VisibleRows)
                Ui.Text(g, "Wheel: more tracks", Ui.F(7.5f), new Rectangle(Ui.S(12), Ui.S(4), gutter - Ui.S(16), ruler - Ui.S(10)), t.SubText, 0);
            var track = ScrollTrack;
            Ui.FillRound(g, track, Ui.S(4), Ui.Blend(t.Input, t.Text, 0.04f), Color.Transparent, 0);
            Ui.FillRound(g, Thumb, Ui.S(3), Ui.Blend(t.Border, t.Text, 0.2f), Color.Transparent, 0);
        }
    }
}
