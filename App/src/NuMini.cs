// UI-01 / FEAT-14: the new in-game minimap (approved mockup «Мини-карта поверх игры — части отдельно»).
// Three separate pieces over the game: a header strip (water, bite window, game time), a square radar with rounded corners
// (you and the last catch with coordinates right on it, bite circles, bait in the water) and a rods panel
// (bait, time in the water, advice, who touched the bait). The rods panel sticks to the side of the radar it is brought to
// (bottom / right / left / top) or stays free. Sizes S/M/L or by the corner, radius, opacity, each piece can be hidden.
// Never takes focus from the game (WS_EX_NOACTIVATE); when pinned, clicks pass through to the game — hold Ctrl to move.
// Passive: only FisherLogger data (database, Player.log, traffic pose); nothing is sent to the game.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FisherLogger
{
    /// <summary>One floating piece over the game.</summary>
    public class NuPiece : Form
    {
        private const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000,
            WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3, GWL_EXSTYLE = -20;
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_NOOWNERZORDER = 0x200;
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);

        public bool ClickThrough { get; private set; }
        /// <summary>Corners to round (TL, TR, BR, BL); a docked side is square.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool[] Corners { get; set; } = { true, true, true, true };
        public const int Radius = 14, StripH = 26;
        /// <summary>Drag by the strip: (delta) during, (done) at the end.</summary>
        public event Action<NuPiece, Point>? Dragging;
        public event Action<NuPiece>? DragDone;
        public event Action<NuPiece, MouseEventArgs>? StripClick;
        protected bool Interactive => !ClickThrough;
        private Point _dragFrom; private bool _drag, _moved;

        public NuPiece()
        {
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual; ShowInTaskbar = false;
            BackColor = NuTheme.Ground; ForeColor = NuTheme.Text; Font = NuTheme.F(9f); Text = "FisherLogger — мини-карта";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Opacity = 0.92;
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_LAYERED; if (ClickThrough) cp.ExStyle |= WS_EX_TRANSPARENT; return cp; }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }
        public void SetClickThrough(bool on)
        {
            if (on == ClickThrough) return;
            ClickThrough = on;
            if (!IsHandleCreated) return;
            long ex = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
            ex = on ? (ex | WS_EX_TRANSPARENT | WS_EX_LAYERED) : (ex & ~WS_EX_TRANSPARENT);
            SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(ex));
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_NOOWNERZORDER);
            Invalidate();
        }
        public void KeepOnTop()
        {
            if (IsHandleCreated && Visible && GetWindow(Handle, 3) != IntPtr.Zero)
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
        public void ApplyShape()
        {
            if (Width <= 0 || Height <= 0) return;
            using var p = RoundPath(new Rectangle(0, 0, Width, Height), Radius, Corners);
            var old = Region; Region = new Region(p); old?.Dispose();
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); ApplyShape(); Invalidate(); }

        public static GraphicsPath RoundPath(Rectangle r, int rad, bool[] c)
        {
            var p = new GraphicsPath(); int d = rad * 2;
            if (c[0]) p.AddArc(r.X, r.Y, d, d, 180, 90); else p.AddLine(r.X, r.Y, r.X, r.Y);
            if (c[1]) p.AddArc(r.Right - d, r.Y, d, d, 270, 90); else p.AddLine(r.Right, r.Y, r.Right, r.Y);
            if (c[2]) p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); else p.AddLine(r.Right, r.Bottom, r.Right, r.Bottom);
            if (c[3]) p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); else p.AddLine(r.X, r.Bottom, r.X, r.Bottom);
            p.CloseFigure(); return p;
        }

        protected virtual Rectangle StripRect => new(0, 0, Width, StripH);
        /// <summary>Strip buttons handle their own clicks first.</summary>
        protected virtual bool StripButton(Point p, MouseButtons b) => false;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && StripRect.Contains(e.Location)) { _drag = true; _moved = false; _dragFrom = Cursor.Position; Capture = true; }
        }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture && _drag) { bool was = _moved; _drag = false; if (was) DragDone?.Invoke(this); } }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_drag && MouseButtons != MouseButtons.Left) { _drag = false; Capture = false; }
            if (!_drag) { Cursor = StripRect.Contains(e.Location) ? Cursors.SizeAll : Cursors.Default; return; }
            var c = Cursor.Position; var d = new Point(c.X - _dragFrom.X, c.Y - _dragFrom.Y);
            if (!_moved && Math.Abs(d.X) + Math.Abs(d.Y) < 3) return;
            _moved = true; _dragFrom = c; Dragging?.Invoke(this, d);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool was = _drag && _moved; _drag = false; Capture = false;
            if (was) { DragDone?.Invoke(this); return; }
            if (e.Button == MouseButtons.Right) { StripClick?.Invoke(this, e); return; }   // menu from anywhere on the piece
            if (StripRect.Contains(e.Location) && !StripButton(e.Location, e.Button)) StripClick?.Invoke(this, e);
        }

        protected void PaintFrame(Graphics g, string title, string? note = null)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(NuTheme.Ground);
            using (var b = new SolidBrush(Color.FromArgb(0x0B, 0x14, 0x18))) g.FillRectangle(b, 0, 0, Width, StripH);
            using (var dot = new SolidBrush(Color.FromArgb(0x6E, 0x83, 0x80)))
                foreach (var (x, y) in new[] { (9, 9), (15, 9), (9, 15), (15, 15) }) g.FillEllipse(dot, x - 1.3f, y + 0.5f - 1.3f, 2.6f, 2.6f);
            using var f = NuTheme.F(8.5f);
            TextRenderer.DrawText(g, title, f, new Point(24, 6), NuTheme.Muted, TextFormatFlags.NoPadding);
            if (note != null) TextRenderer.DrawText(g, note, f, new Point(24 + TextRenderer.MeasureText(g, title, f, Size.Empty, TextFormatFlags.NoPadding).Width + 8, 6), NuTheme.AccentText, TextFormatFlags.NoPadding);
        }
        protected void PaintBorder(Graphics g, Rectangle? edge = null)
        {
            using (var p = RoundPath(new Rectangle(0, 0, Width - 1, Height - 1), Radius, Corners)) { using var pn = new Pen(Color.FromArgb(128, 0x3F, 0x8E, 0x9A)); g.DrawPath(pn, p); }
            if (edge is Rectangle e2) using (var b = new SolidBrush(Color.FromArgb(220, NuTheme.Accent))) g.FillRectangle(b, e2);
        }
        protected static void Minus(Graphics g, Rectangle r, Color c) { using var pn = new Pen(c, 1.8f); g.DrawLine(pn, r.X + r.Width / 2 - 4, r.Y + r.Height / 2, r.X + r.Width / 2 + 4, r.Y + r.Height / 2); }

        /// <summary>Text with a dark outline, readable on any water colour.</summary>
        public static void Outlined(Graphics g, string t, Font f, PointF at, Color fill, bool center = false)
        {
            using var path = new GraphicsPath();
            float em = g.DpiY * f.SizeInPoints / 72f;
            var sz = g.MeasureString(t, f);
            var o = center ? new PointF(at.X - sz.Width / 2f, at.Y) : at;
            path.AddString(t, f.FontFamily, (int)f.Style, em, o, StringFormat.GenericDefault);
            using (var pn = new Pen(Color.FromArgb(210, 0x0B, 0x14, 0x18), 3f) { LineJoin = LineJoin.Round }) g.DrawPath(pn, path);
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        }
    }

    public sealed class NuHeaderPiece : NuPiece
    {
        public Func<string>? Water;
        public event Action? HideMe;
        private Rectangle _hide;
        protected override Rectangle StripRect => new(0, 0, Width - 30, Height);
        public NuHeaderPiece() { Size = new Size(420, 36); }
        /// <summary>Fits the width to the text (called by the timer, not from OnPaint), never wider than the screen.</summary>
        public void FitWidth()
        {
            using var g = CreateGraphics();
            var (game, window, left) = NuTheme.GameClock(NuTheme.MoscowNow());
            string w = Water?.Invoke() ?? "";
            int x0 = 28;
            void M(string t, Font f) { if (t.Length > 0) x0 += TextRenderer.MeasureText(g, t, f, Size.Empty, TextFormatFlags.NoPadding).Width + 12; }
            M(w.Length > 0 ? w : "водоём не определён", NuFonts.Get(9.5f, FontStyle.Bold));
            if (window != null) M(window.ToLowerInvariant() + " · ещё " + Math.Max(1, (int)Math.Ceiling(left.TotalMinutes)) + " мин", NuFonts.Get(9f, FontStyle.Bold));
            M("в игре " + game, NuFonts.Get(9f));
            int want = Math.Min(x0 + 26, Screen.FromControl(this).WorkingArea.Width - 20);
            if (Math.Abs(want - Width) > 6) Width = want;
        }
        protected override void OnMouseUp(MouseEventArgs e) { if (e.Button == MouseButtons.Left && _hide.Contains(e.Location)) { HideMe?.Invoke(); return; } base.OnMouseUp(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(NuTheme.Ground);
            using (var dot = new SolidBrush(Color.FromArgb(0x6E, 0x83, 0x80)))
                foreach (var (x, y) in new[] { (12, 14), (18, 14), (12, 20), (18, 20) }) g.FillEllipse(dot, x - 1.3f, y - 1.3f, 2.6f, 2.6f);
            using var fb = NuTheme.F(9.5f, FontStyle.Bold); using var f = NuTheme.F(9f); using var fa = NuTheme.F(9f, FontStyle.Bold);
            int x0 = 28;
            void Txt(string t, Font ft, Color c) { if (t.Length == 0) return; TextRenderer.DrawText(g, t, ft, new Point(x0, (Height - ft.Height) / 2), c, TextFormatFlags.NoPadding); x0 += TextRenderer.MeasureText(g, t, ft, Size.Empty, TextFormatFlags.NoPadding).Width + 12; }
            var (game, window, left) = NuTheme.GameClock(NuTheme.MoscowNow());
            string w = Water?.Invoke() ?? ""; Txt(w.Length > 0 ? w : "водоём не определён", fb, NuTheme.Text);
            if (window != null) Txt(window.ToLowerInvariant() + " · ещё " + Math.Max(1, (int)Math.Ceiling(left.TotalMinutes)) + " мин", fa, NuTheme.AccentText);
            Txt("в игре " + game, f, NuTheme.Muted);
            _hide = new Rectangle(Width - 28, (Height - 22) / 2, 22, 22);
            Minus(g, _hide, NuTheme.Muted);
            PaintBorder(g);
        }
    }

    public sealed class NuRadarPiece : NuPiece
    {
        public Func<NuMapData?>? DataSource;
        public Func<PlayerPose?>? PoseSource;
        public Func<int?>? RodSlot;          // circles of this rod's tackle
        public Func<int>? MinLevel;
        /// <summary>UI-02/5: places from old catches (same filter as the big map); null — layer off.</summary>
        public Func<List<NuPlace>?>? Places;
        public double RadiusM = 50;
        public bool Pinned = true;
        public bool CtrlHeld;
        public string? SnapEdge;             // "bottom"/"top"/"left"/"right" — highlight while the panel is near
        public event Action? RadiusClicked, PinClicked;
        public event Action<int>? Resized;    // new square size
        public event Action? ResizeDone;
        private Rectangle _radiusBtn, _pinBtn, _grip;
        private bool _resizing; private Point _resFrom; private int _resStart;
        public NuRadarPiece() { Size = new Size(260, 260 + StripH); }
        protected override Rectangle StripRect => new(0, 0, Width - 90, StripH);

        protected override bool StripButton(Point p, MouseButtons b) => false;
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _grip.Contains(e.Location)) { _resizing = true; _resFrom = Cursor.Position; _resStart = Width; Capture = true; return; }
            if (e.Button == MouseButtons.Left && (_radiusBtn.Contains(e.Location) || _pinBtn.Contains(e.Location))) return;   // buttons: no drag
            base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_resizing && MouseButtons != MouseButtons.Left) { _resizing = false; Capture = false; ResizeDone?.Invoke(); }
            if (_resizing) { var c = Cursor.Position; int d = Math.Max(c.X - _resFrom.X, c.Y - _resFrom.Y); int s = Math.Clamp(_resStart + d, 160, 520); if (s != Width) Resized?.Invoke(s); return; }
            if (_grip.Contains(e.Location)) { Cursor = Cursors.SizeNWSE; return; }
            if (_radiusBtn.Contains(e.Location) || _pinBtn.Contains(e.Location)) { Cursor = Cursors.Hand; return; }
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_resizing) { _resizing = false; Capture = false; ResizeDone?.Invoke(); return; }
            if (e.Button == MouseButtons.Left && _radiusBtn.Contains(e.Location)) { RadiusClicked?.Invoke(); return; }
            if (e.Button == MouseButtons.Left && _pinBtn.Contains(e.Location)) { PinClicked?.Invoke(); return; }
            base.OnMouseUp(e);
        }

        public PointF W2S(double x, double z, double cx, double cz, double scale, Rectangle map) =>
            new((float)(map.X + map.Width / 2.0 + (x - cx) * scale), (float)(map.Y + map.Height / 2.0 - (z - cz) * scale));

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            PaintFrame(g, "мини-карта", Pinned && !CtrlHeld ? "Ctrl — двигать" : null);
            using var f = NuTheme.F(8.5f); using var fb = NuTheme.F(8.5f, FontStyle.Bold);
            string rt = RadiusM.ToString("0", CultureInfo.InvariantCulture) + " м ▾";
            var rs = TextRenderer.MeasureText(g, rt, f, Size.Empty, TextFormatFlags.NoPadding);
            _pinBtn = new Rectangle(Width - 28, 2, 22, 22);
            _radiusBtn = new Rectangle(_pinBtn.X - rs.Width - 14, 3, rs.Width + 12, 20);
            TextRenderer.DrawText(g, rt, f, _radiusBtn, NuTheme.Soft, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            using (var pn = new Pen(Pinned ? NuTheme.Accent : NuTheme.Muted, 1.6f) { LineJoin = LineJoin.Round })
            {   // pin
                float px = _pinBtn.X + 5, py = _pinBtn.Y + 4;
                g.DrawPolygon(pn, new[] { new PointF(px + 3, py), new PointF(px + 9, py), new PointF(px + 8, py + 5), new PointF(px + 11, py + 8), new PointF(px + 1, py + 8), new PointF(px + 4, py + 5) });
                g.DrawLine(pn, px + 6, py + 8, px + 6, py + 13);
            }
            var map = new Rectangle(0, StripH, Width, Height - StripH);
            var d = DataSource?.Invoke(); var pose = PoseSource?.Invoke();
            double scale = (Math.Min(map.Width, map.Height) / 2.0) / Math.Max(5, RadiusM);
            double cx, cz;
            var lastCatch = d?.Catches.Where(c => c.X.HasValue && c.Z.HasValue).OrderByDescending(OverlayDataSource.SortTime).FirstOrDefault();
            if (pose != null) { cx = pose.X; cz = pose.Z; }
            else if (d?.Casts.FirstOrDefault(k => k.Active) is { } ac) { cx = ac.X; cz = ac.Z; }
            else if (lastCatch != null) { cx = lastCatch.X!.Value; cz = lastCatch.Z!.Value; }
            else if (d?.Map is { } mm) { cx = (mm.MinX + mm.MaxX) / 2; cz = (mm.MinZ + mm.MaxZ) / 2; }
            else { cx = cz = 0; }
            var clipState = g.Save(); g.SetClip(map);
            using (var water = new SolidBrush(Color.FromArgb(0x6F, 0xB8, 0xB0))) g.FillRectangle(water, map);
            if (d?.Map != null)
            {
                try
                {
                    var st = g.Save(); g.TranslateTransform(map.X, map.Y);
                    try
                    {
                        if (d.MapImage != null) MapRenderer.DrawBackground(g, d.Map, d.MapImage, cx, cz, scale, map.Width, map.Height);
                        if (d.BathyImage != null) MapRenderer.DrawBackground(g, d.Map, d.BathyImage, cx, cz, scale, map.Width, map.Height);
                    }
                    finally { g.Restore(st); }
                }
                catch { }
            }
            if (d == null || d.LocationId == null)
            {
                using var fc = NuTheme.F(9f, FontStyle.Bold);
                Outlined(g, "водоём ещё не определён", fc, new PointF(map.X + map.Width / 2f, map.Y + map.Height / 2f - 8), NuTheme.Text, true);
            }
            else
            {
                // places from old catches: small dots under everything (colour = best class, size = how many fish)
                List<NuPlace>? places = null;
                try { places = Places?.Invoke(); } catch { }
                if (places != null)
                    foreach (var pl in places.OrderByDescending(q => q.Count))
                    {
                        var c = W2S(pl.X, pl.Z, cx, cz, scale, map);
                        float pr = (float)Math.Clamp(2.5 + 1.6 * Math.Sqrt(pl.Count), 3, 10);
                        if (c.X < map.X - pr || c.Y < map.Y - pr || c.X > map.Right + pr || c.Y > map.Bottom + pr) continue;
                        using (var b = new SolidBrush(Color.FromArgb(170, NuPlaces.ClassColor(pl.BestClass)))) g.FillEllipse(b, c.X - pr, c.Y - pr, 2 * pr, 2 * pr);
                        using (var pn = new Pen(Color.FromArgb(200, 0x0B, 0x14, 0x18), 1.2f)) g.DrawEllipse(pn, c.X - pr, c.Y - pr, 2 * pr, 2 * pr);
                    }
                // bite circles of one tackle (the rod chosen on the big map, else the first rod in the water)
                int? slot = RodSlot?.Invoke();
                var rod = slot is int s0 ? d.Rods.FirstOrDefault(r => r.Slot == s0) : d.Rods.FirstOrDefault(r => r.Active && !r.Spinning) ?? d.Rods.FirstOrDefault(r => !r.Spinning);
                var spots = NuMapView.Filter(d.Spots, MinLevel?.Invoke() ?? 0, rod?.Tackle);
                float r12 = (float)Math.Clamp(ChanceSpots.MergeM * scale, 10, 30);   // never below the 9-px hide limit of Place()
                var (placed, _) = NuMapView.Place(spots.Select(sp => (sp, W2S(sp.X, sp.Z, cx, cz, scale, map), r12))
                    .Where(q => q.Item2.X > map.X - 30 && q.Item2.Y > map.Y - 30 && q.Item2.X < map.Right + 30 && q.Item2.Y < map.Bottom + 30));
                using var fl = NuTheme.F(7.5f, FontStyle.Bold);
                foreach (var c in placed.OrderBy(c => c.Spot.Level))
                {
                    var col = NuMapView.LevelColor(c.Spot.Level); double fresh = Math.Clamp(1 - c.Spot.AgeMin / ChanceSpots.MaxAgeMin, 0.4, 1);
                    using (var b = new SolidBrush(Color.FromArgb((int)(55 * fresh), col))) g.FillEllipse(b, c.C.X - c.R, c.C.Y - c.R, 2 * c.R, 2 * c.R);
                    using (var pn = new Pen(Color.FromArgb((int)(255 * fresh), col), 1.8f)) g.DrawEllipse(pn, c.C.X - c.R, c.C.Y - c.R, 2 * c.R, 2 * c.R);
                    if (c.R >= 12 && c.Spot.Level != 1 || c.R >= 16) Outlined(g, NuMapView.LevelLabel(c.Spot), fl, new PointF(c.C.X, c.C.Y - 6), c.Spot.Level == 2 ? Color.FromArgb(0xFF, 0xE2, 0xCC) : NuTheme.Text, true);
                }
                // bait in the water
                foreach (var k in d.Casts.Where(k => k.Active))
                {
                    var p = W2S(k.X, k.Z, cx, cz, scale, map);
                    var col = k.State is "bite" or "hook" ? NuTheme.Accent : Color.FromArgb(0xE8, 0xEF, 0xEC);
                    using (var b = new SolidBrush(NuTheme.Bar)) g.FillEllipse(b, p.X - 6, p.Y - 6, 12, 12);
                    using (var pn = new Pen(col, 1.5f)) g.DrawEllipse(pn, p.X - 6, p.Y - 6, 12, 12);
                    using var fn = NuTheme.F(7f, FontStyle.Bold);
                    TextRenderer.DrawText(g, (k.Rod + 1).ToString(CultureInfo.InvariantCulture), fn, new Rectangle((int)p.X - 6, (int)p.Y - 6, 12, 12), col, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                // last catch with its coordinates right on the map
                if (lastCatch != null)
                {
                    var p = W2S(lastCatch.X!.Value, lastCatch.Z!.Value, cx, cz, scale, map);
                    bool inside = map.Contains(Point.Round(p));
                    p = new PointF(Math.Clamp(p.X, map.X + 6, map.Right - 6), Math.Clamp(p.Y, map.Y + 6, map.Bottom - 6));
                    using (var b = new SolidBrush(Color.FromArgb(0x8F, 0xE3, 0xA8))) g.FillEllipse(b, p.X - 4.5f, p.Y - 4.5f, 9, 9);
                    using (var pn = new Pen(NuTheme.Bar, 1.5f)) g.DrawEllipse(pn, p.X - 4.5f, p.Y - 4.5f, 9, 9);
                    string grid = Grid(d.LocationId, lastCatch.X.Value, lastCatch.Z.Value) + (inside ? "" : " →");
                    string what = lastCatch.Fish.ToLowerInvariant() + " " + lastCatch.Weight + " г";
                    using var fs = NuTheme.F(7.5f);
                    float tx = p.X + 8, wmax = Math.Max(g.MeasureString(grid, fb).Width, g.MeasureString(what, fs).Width);
                    if (tx + wmax > map.Right - 4) tx = p.X - 8 - wmax;
                    tx = Math.Clamp(tx, map.X + 4, Math.Max(map.X + 4, map.Right - 4 - wmax));
                    float ty = Math.Clamp(p.Y - 14, map.Y + 2, map.Bottom - 28);
                    Outlined(g, grid, fb, new PointF(tx, ty), NuTheme.Text);
                    Outlined(g, what, fs, new PointF(tx, ty + 13), Color.FromArgb(0xD6, 0xF5, 0xE0));
                }
                // you
                if (pose != null)
                {
                    var s = W2S(pose.X, pose.Z, cx, cz, scale, map);
                    if (pose.Yaw.HasValue)
                    {
                        using var path = new GraphicsPath(); float rad = Math.Min(map.Width, map.Height) * 0.2f;
                        path.AddPie(s.X - rad, s.Y - rad, 2 * rad, 2 * rad, (float)pose.Yaw.Value - 90 - 22, 44);
                        using var br = new SolidBrush(Color.FromArgb(46, NuTheme.Accent)); g.FillPath(br, path);
                    }
                    using (var b = new SolidBrush(NuTheme.Accent)) g.FillEllipse(b, s.X - 5.5f, s.Y - 5.5f, 11, 11);
                    using (var pn = new Pen(Color.FromArgb(0xFF, 0xF4, 0xEA), 1.8f)) g.DrawEllipse(pn, s.X - 5.5f, s.Y - 5.5f, 11, 11);
                    double ageMin = (DateTime.Now - pose.Time).TotalMinutes;   // ui01_review3 #10
                    string you = (ageMin >= 2 ? "вы (" + Math.Floor(ageMin) + " мин) " : "вы ") + Grid(d.LocationId, pose.X, pose.Z); float yw = g.MeasureString(you, fb).Width;
                    Outlined(g, you, fb, new PointF(Math.Clamp(s.X, map.X + yw / 2 + 2, map.Right - yw / 2 - 2), Math.Min(s.Y + 9, map.Bottom - 30)), Color.FromArgb(0xFF, 0xE2, 0xCC), true);
                    if (Bathymetry.DepthAt(d.LocationId, pose.X, pose.Z) is double dm)
                        Outlined(g, "глубина под вами " + dm.ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")) + " м", f, new PointF(map.X + 8, map.Bottom - 18), NuTheme.Text);
                }
                else
                {
                    using var fc = NuTheme.F(8f);
                    Outlined(g, "ваше место появится после первого шага в игре", fc, new PointF(map.X + map.Width / 2f, map.Bottom - 18), NuTheme.Soft, true);
                }
            }
            g.Restore(clipState);
            // resize grip
            _grip = new Rectangle(Width - 18, Height - 18, 16, 16);
            using (var pn = new Pen(Color.FromArgb(150, 0xE8, 0xEF, 0xEC), 1.5f)) { g.DrawLine(pn, Width - 5, Height - 13, Width - 13, Height - 5); g.DrawLine(pn, Width - 5, Height - 9, Width - 9, Height - 5); }
            Rectangle? edge = SnapEdge switch
            {
                "bottom" => new Rectangle(0, Height - 3, Width, 3), "top" => new Rectangle(0, 0, Width, 3),
                "right" => new Rectangle(Width - 3, 0, 3, Height), "left" => new Rectangle(0, 0, 3, Height), _ => null
            };
            PaintBorder(g, edge);
        }

        public static string Grid(uint? loc, double x, double z) => MapGridCoordinates.ToGrid(loc, x, z) is { } gg ? gg.X + " : " + gg.Y : Math.Round(x) + " : " + Math.Round(z);
    }

    public sealed class NuRodsPiece : NuPiece
    {
        public Func<NuMapData?>? DataSource;
        public Func<uint?, string>? FishName;
        public event Action? HideMe;
        private Rectangle _hide;
        protected override Rectangle StripRect => new(0, 0, Width - 30, StripH);
        public NuRodsPiece() { Size = new Size(260, 200); }
        protected override void OnMouseUp(MouseEventArgs e) { if (e.Button == MouseButtons.Left && _hide.Contains(e.Location)) { HideMe?.Invoke(); return; } base.OnMouseUp(e); }

        /// <summary>Height the content needs (rows + who touched the bait).</summary>
        public int ContentHeight(NuMapData? d)
        {
            int rows = Math.Max(1, d?.Rods.Count ?? 0);
            bool touched = d?.Rods.Any(r => r.Active && r.EmptyFish.Count > 0) == true;
            return StripH + 8 + rows * 46 + (touched ? 44 : 0) + 6;
        }

        public static (string text, Color fg, Color bg) Pill(NuRod r)
        {
            if (!r.Active) return ("не в воде", NuTheme.Soft, Color.FromArgb(0x24, 0x36, 0x3F));
            return r.Level switch
            {
                3 => ("клюёт!", NuTheme.AccentText, NuTheme.AccentDeep),
                2 => ("перезабросить", NuTheme.AccentText, NuTheme.AccentDeep),
                1 => ("реже обычного", NuTheme.Soft, Color.FromArgb(0x24, 0x36, 0x3F)),
                0 => (r.TrophyNormal ? "ждать, норма" : "ждать", Color.FromArgb(0x8F, 0xE3, 0xA8), Color.FromArgb(0x17, 0x3A, 0x26)),
                _ => ("в воде", NuTheme.Soft, Color.FromArgb(0x24, 0x36, 0x3F))
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; PaintFrame(g, "удочки");
            _hide = new Rectangle(Width - 28, 2, 22, 22); Minus(g, _hide, NuTheme.Muted);
            var d = DataSource?.Invoke();
            using var fb = NuTheme.F(9f, FontStyle.Bold); using var f = NuTheme.F(8.5f); using var fn = NuTheme.F(9f, FontStyle.Bold); using var fp = NuTheme.F(8f, FontStyle.Bold);
            int y = StripH + 8;
            if (d == null || d.Rods.Count == 0)
            {
                TextRenderer.DrawText(g, "Забросьте удочку — здесь появятся наживка, время в воде и совет.", f, new Rectangle(10, y + 4, Width - 20, 40), NuTheme.Muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                PaintBorder(g); return;
            }
            foreach (var r in d.Rods)
            {
                var row = new Rectangle(8, y, Width - 16, 40);
                using (var path = NuTheme.Round(row, 8)) { using var b = new SolidBrush(Color.FromArgb(180, 0x1E, 0x31, 0x3B)); g.FillPath(b, path); }
                TextRenderer.DrawText(g, (r.Slot + 1).ToString(CultureInfo.InvariantCulture), fn, new Point(row.X + 8, row.Y + 12), NuTheme.Muted, TextFormatFlags.NoPadding);
                var (pt, pfg, pbg) = Pill(r);
                var ps = TextRenderer.MeasureText(g, pt, fp, Size.Empty, TextFormatFlags.NoPadding);
                var pr = new Rectangle(row.Right - ps.Width - 22, row.Y + 11, ps.Width + 14, 18);
                using (var path = NuTheme.Round(pr, 9)) { using var b = new SolidBrush(pbg); g.FillPath(b, path); }
                TextRenderer.DrawText(g, pt, fp, new Point(pr.X + 7, pr.Y + 3), pfg, TextFormatFlags.NoPadding);
                int tx = row.X + 26, tw = pr.X - tx - 8;
                string bait = r.Bait.Length > 0 ? r.Bait : SoakCurve.RodName(r.RodCode);
                TextRenderer.DrawText(g, bait, fb, new Rectangle(tx, row.Y + 5, tw, 16), NuTheme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                // time in the water: bar (15 min = full) + minutes
                string time = r.Active ? TimeSpan.FromMinutes(r.SoakMin).ToString(r.SoakMin >= 60 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture) : "";
                var tsz = TextRenderer.MeasureText(g, time, f, Size.Empty, TextFormatFlags.NoPadding);
                var bar = new Rectangle(tx, row.Y + 27, Math.Max(10, tw - tsz.Width - 8), 4);
                using (var b = new SolidBrush(Color.FromArgb(0x24, 0x36, 0x3F))) g.FillRectangle(b, bar);
                if (r.Active)
                {
                    var col = r.Level == 0 ? NuTheme.Ok : r.Level >= 2 ? NuTheme.Accent : Color.FromArgb(0x9A, 0xA9, 0xA6);
                    using var b = new SolidBrush(col); g.FillRectangle(b, bar.X, bar.Y, (int)(bar.Width * Math.Clamp(r.SoakMin / 15.0, 0.03, 1)), bar.Height);
                }
                TextRenderer.DrawText(g, time, f, new Point(bar.Right + 6, row.Y + 23), NuTheme.Muted, TextFormatFlags.NoPadding);
                y += 46;
            }
            // who touched the bait on this cast (s2c 1122: fish and weight that did not hook)
            var touched = d.Rods.Where(r => r.Active && r.EmptyFish.Count > 0).SelectMany(r => r.EmptyFish.Select(x => (r.Slot, x.fish, x.weight))).LastOrDefault();
            if (touched.fish != 0)
            {
                var box = new Rectangle(8, y, Width - 16, 38);
                using (var path = NuTheme.Round(box, 8)) { using var b = new SolidBrush(Color.FromArgb(115, 0x1E, 0x31, 0x3B)); g.FillPath(b, path); }
                string nm = FishName?.Invoke(touched.fish) ?? ""; if (nm.Length == 0) nm = "рыба";
                TextRenderer.DrawText(g, "Тронул наживку (удочка " + (touched.Slot + 1) + "): " + nm.ToLowerInvariant() + " " + touched.weight + " г", f,
                    new Rectangle(box.X + 8, box.Y + 4, box.Width - 16, box.Height - 6), NuTheme.Soft, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            }
            PaintBorder(g);
        }
    }

    /// <summary>Layout, docking, settings and timers of the three pieces.</summary>
    public sealed class NuMiniMap : IDisposable
    {
        public readonly NuHeaderPiece Header = new();
        public readonly NuRadarPiece Radar = new();
        public readonly NuRodsPiece Rods = new();
        public NuMapData? Data { get; set; }
        /// <summary>«Скрыть мини-карту» from the menu goes through the in-game window manager (remembers it for the next start).</summary>
        public Action? HideRequested;
        public bool Visible { get; private set; }
        public const int SnapPx = 28, PanelW = 280;
        private readonly Func<WindowSettings> _ws;
        private readonly Action _save;
        private readonly System.Windows.Forms.Timer _fast = new() { Interval = 50 }, _slow = new() { Interval = 2000 };
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

        public static readonly int[] Sizes = { 200, 260, 340 };
        public static readonly double[] Radii = { 25, 50, 100, 200 };

        public NuMiniMap(Func<WindowSettings> ws, Action save, Func<PlayerPose?> pose, Func<uint?, string> fishName, Func<string> water, Func<int?> rodSlot)
        {
            _ws = ws; _save = save;
            Radar.DataSource = () => Data; Radar.PoseSource = pose; Radar.RodSlot = rodSlot; Radar.MinLevel = () => _ws().NuZoneMinLevel;
            Radar.Places = () => { var w = _ws(); return w.NuPlacesOn && w.NuPlacesMini ? NuPlaces.Peek(Data, NuPlaceFilter.FromSettings(w), () => { try { if (Radar.IsHandleCreated && !Radar.IsDisposed) Radar.BeginInvoke(new Action(Radar.Invalidate)); } catch { } }) : null; };
            Rods.DataSource = () => Data; Rods.FishName = fishName;
            try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged; } catch { }
            Header.Water = water;
            foreach (var p in new NuPiece[] { Header, Radar, Rods })
            {
                p.Dragging += OnDrag; p.DragDone += OnDragDone;
                p.StripClick += (s, e) => { if (e.Button == MouseButtons.Right) Menu(s, e.Location); };
            }
            Header.HideMe += () => { _ws().MiniHdr = false; Header.Hide(); _save(); };
            Rods.HideMe += () => { _ws().MiniPanel = false; Rods.Hide(); _save(); };
            Radar.RadiusClicked += () => { var w = _ws(); int i = Array.IndexOf(Radii, w.MiniRadius); w.MiniRadius = Radii[(i + 1 + Radii.Length) % Radii.Length]; Radar.RadiusM = w.MiniRadius; Radar.Invalidate(); _save(); };
            Radar.PinClicked += () => { var w = _ws(); w.MiniPinned = !w.MiniPinned; Radar.Pinned = w.MiniPinned; ApplyClickThrough(false); _save(); };
            Radar.Resized += s => { _ws().MiniSize = s; Layout(); };
            Radar.ResizeDone += () => _save();
            _fast.Tick += (s, e) =>
            {
                bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
                if (ctrl != Radar.CtrlHeld) { Radar.CtrlHeld = ctrl; Radar.Invalidate(); }
                ApplyClickThrough(ctrl);
            };
            _slow.Tick += (s, e) => { if (Header.Visible) Header.FitWidth(); foreach (var p in Pieces) { if (p.Visible) { p.KeepOnTop(); p.Invalidate(); } } FitRods(); };
        }

        private IEnumerable<NuPiece> Pieces => new NuPiece[] { Header, Radar, Rods };

        private void ApplyClickThrough(bool ctrl)
        {
            bool through = _ws().MiniPinned && !ctrl;
            foreach (var p in Pieces) p.SetClickThrough(through);
        }

        public void Toggle() { if (Visible) Hide(); else Show(); }

        public void Show()
        {
            var w = _ws();
            Radar.RadiusM = Radii.Contains(w.MiniRadius) ? w.MiniRadius : 50; Radar.Pinned = w.MiniPinned;
            foreach (var p in Pieces) p.Opacity = Math.Clamp(w.MiniOpacity, 40, 100) / 100.0 * 0.99;
            Header.FitWidth();
            Layout(firstTime: true);
            Radar.Show();
            if (w.MiniHdr) Header.Show(); else Header.Hide();
            if (w.MiniPanel) Rods.Show(); else Rods.Hide();
            ApplyClickThrough(false);
            Visible = true; _fast.Start(); _slow.Start();
        }

        public void Hide()
        {
            foreach (var p in Pieces) p.Hide();
            Visible = false; _fast.Stop(); _slow.Stop();
        }

        public void Refresh() { if (!Visible) return; FitRods(); foreach (var p in Pieces) if (p.Visible) p.Invalidate(); }

        private void FitRods()
        {
            if (!Visible) return;
            int h = Rods.ContentHeight(Data);
            var w = _ws();
            if (w.MiniDock is "left" or "right") h = Math.Max(h, Radar.Height);
            if (Rods.Height != h) { Rods.Height = h; Layout(); }
        }

        /// <summary>Puts the pieces where the settings say; the docked rods panel follows the radar.</summary>
        public void Layout(bool firstTime = false)
        {
            var w = _ws();
            int s = Math.Clamp(w.MiniSize, 160, 520);
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;   // the monitor the player looks at
            if (w.MiniX < -20000 || w.MiniY < -20000 || firstTime && !OnScreen(new Rectangle(w.MiniX, w.MiniY, s, s + NuPiece.StripH)))
            { w.MiniX = wa.Right - 16 - s; w.MiniY = wa.Top + 64; }
            Radar.Bounds = new Rectangle(w.MiniX, w.MiniY, s, s + NuPiece.StripH);
            if (w.HdrX < -20000 || firstTime && !OnScreen(new Rectangle(w.HdrX, w.HdrY, Header.Width, Header.Height))) { w.HdrX = wa.Left + (wa.Width - Header.Width) / 2; w.HdrY = wa.Top + 12; }
            Header.Location = new Point(w.HdrX, w.HdrY);
            var r = Radar.Bounds; int ph = Rods.ContentHeight(Data);
            bool[] all = { true, true, true, true };
            switch (w.MiniDock)
            {
                case "bottom": Rods.Bounds = new Rectangle(r.X, r.Bottom, r.Width, ph); Radar.Corners = new[] { true, true, false, false }; Rods.Corners = new[] { false, false, true, true }; break;
                case "top": Rods.Bounds = new Rectangle(r.X, r.Y - ph, r.Width, ph); Radar.Corners = new[] { false, false, true, true }; Rods.Corners = new[] { true, true, false, false }; break;
                case "right": Rods.Bounds = new Rectangle(r.Right, r.Y, PanelW, Math.Max(ph, r.Height)); Radar.Corners = new[] { true, false, false, true }; Rods.Corners = new[] { false, true, true, false }; break;
                case "left": Rods.Bounds = new Rectangle(r.X - PanelW, r.Y, PanelW, Math.Max(ph, r.Height)); Radar.Corners = new[] { false, true, true, false }; Rods.Corners = new[] { true, false, false, true }; break;
                default:
                    if (w.PanelX < -20000 || !OnScreen(new Rectangle(w.PanelX, w.PanelY, PanelW, ph))) { w.PanelX = r.X; w.PanelY = r.Bottom + 12; }
                    Rods.Bounds = new Rectangle(w.PanelX, w.PanelY, PanelW, ph); Radar.Corners = all; Rods.Corners = (bool[])all.Clone(); break;
            }
            if (!w.MiniPanel) Radar.Corners = (bool[])all.Clone();
            // a docked panel that would leave the screen: top → bottom, then clamp into the monitor of the radar
            var swa = Screen.FromRectangle(r).WorkingArea;
            if (w.MiniDock == "top" && Rods.Top < swa.Top) { w.MiniDock = "bottom"; Layout(); return; }
            if (w.MiniDock == "left" && Rods.Left < swa.Left) { w.MiniDock = "right"; Layout(); return; }
            if (w.MiniDock == "right" && Rods.Right > swa.Right && r.X - PanelW >= swa.Left) { w.MiniDock = "left"; Layout(); return; }
            // ui01_review3 #9: no room below the radar — show the panel above it this time (the setting stays «bottom»)
            if (w.MiniDock == "bottom" && w.MiniPanel && Rods.Bottom > swa.Bottom && r.Y - ph >= swa.Top)
            { Rods.Bounds = new Rectangle(r.X, r.Y - ph, r.Width, ph); Radar.Corners = new[] { false, false, true, true }; Rods.Corners = new[] { true, true, false, false }; }
            // ui01_review3 #14: a free panel stays on the monitor it was dropped on
            if (w.MiniDock == "free") Rods.Location = Clamp(Rods.Bounds, Screen.FromRectangle(Rods.Bounds).WorkingArea);
            Radar.ApplyShape(); Rods.ApplyShape(); Header.ApplyShape();
            Radar.Invalidate(); Rods.Invalidate();
        }

        private static bool OnScreen(Rectangle r) => Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(Rectangle.Inflate(r, -20, -20)));
        /// <summary>Keep at least the drag strip on the monitor (so the piece can always be pulled back).</summary>
        private static Point Clamp(Rectangle r, Rectangle wa) => new(Math.Clamp(r.X, wa.Left - r.Width + 60, wa.Right - 60), Math.Clamp(r.Y, wa.Top, wa.Bottom - NuPiece.StripH));

        private void OnDrag(NuPiece p, Point d)
        {
            var w = _ws();
            var vs = SystemInformation.VirtualScreen;
            if (ReferenceEquals(p, Header)) { var c = Clamp(new Rectangle(w.HdrX + d.X, w.HdrY + d.Y, Header.Width, Header.Height), vs); w.HdrX = c.X; w.HdrY = c.Y; Header.Location = c; return; }
            if (ReferenceEquals(p, Radar)) { var c = Clamp(new Rectangle(w.MiniX + d.X, w.MiniY + d.Y, Radar.Width, Radar.Height), vs); w.MiniX = c.X; w.MiniY = c.Y; Layout(); return; }
            // the rods panel: free while dragged, shows which side of the radar it would stick to
            if (w.MiniDock != "free") { w.PanelX = Rods.Left; w.PanelY = Rods.Top; w.MiniDock = "free"; Layout(); }
            w.PanelX += d.X; w.PanelY += d.Y; Rods.Location = new Point(w.PanelX, w.PanelY);
            string? edge = SnapSide(Radar.Bounds, Rods.Bounds);
            if (edge != Radar.SnapEdge) { Radar.SnapEdge = edge; Radar.Invalidate(); }
        }

        private void OnDragDone(NuPiece p)
        {
            var w = _ws();
            if (ReferenceEquals(p, Rods))
            {
                string? edge = SnapSide(Radar.Bounds, Rods.Bounds);
                w.MiniDock = edge ?? "free";
                Radar.SnapEdge = null; Layout();
            }
            _save();
        }

        /// <summary>Which side of the radar the panel is brought to (within SnapPx), or null.</summary>
        public static string? SnapSide(Rectangle radar, Rectangle panel)
        {
            bool hOverlap = panel.Right > radar.Left + 20 && panel.Left < radar.Right - 20;
            bool vOverlap = panel.Bottom > radar.Top + 20 && panel.Top < radar.Bottom - 20;
            var c = new List<(string side, int dist)>();
            if (hOverlap) { c.Add(("bottom", Math.Abs(panel.Top - radar.Bottom))); c.Add(("top", Math.Abs(panel.Bottom - radar.Top))); }
            if (vOverlap) { c.Add(("right", Math.Abs(panel.Left - radar.Right))); c.Add(("left", Math.Abs(panel.Right - radar.Left))); }
            var best = c.Where(x => x.dist <= SnapPx).OrderBy(x => x.dist).FirstOrDefault();
            return best.side;
        }

        private void Menu(NuPiece owner, Point at)
        {
            var w = _ws();
            var m = new ContextMenuStrip { ShowImageMargin = false };
            ToolStripMenuItem Item(string t, bool chk, Action a) { var i = new ToolStripMenuItem(t) { Checked = chk }; i.Click += (s, e) => { a(); _save(); }; return i; }
            var size = new ToolStripMenuItem("Размер карты");
            foreach (var (n, v) in new[] { ("S", Sizes[0]), ("M", Sizes[1]), ("L", Sizes[2]) }) size.DropDownItems.Add(Item(n + " — " + v + " пикселей", w.MiniSize == v, () => { w.MiniSize = v; Layout(); }));
            var rad = new ToolStripMenuItem("Радиус");
            foreach (var v in Radii) rad.DropDownItems.Add(Item(v + " м", w.MiniRadius == v, () => { w.MiniRadius = v; Radar.RadiusM = v; Radar.Invalidate(); }));
            var op = new ToolStripMenuItem("Прозрачность");
            foreach (var v in new[] { 100, 92, 80, 70, 60, 50, 40 }) op.DropDownItems.Add(Item(v + " %", w.MiniOpacity == v, () => { w.MiniOpacity = v; foreach (var p in Pieces) p.Opacity = v / 100.0 * 0.99; }));
            var dock = new ToolStripMenuItem("Удочки");
            foreach (var (id, t) in new[] { ("bottom", "снизу"), ("right", "справа"), ("left", "слева"), ("top", "сверху"), ("free", "отдельно") })
                dock.DropDownItems.Add(Item(t, w.MiniDock == id, () => { w.MiniDock = id; w.MiniPanel = true; Rods.Show(); Layout(); }));
            m.Items.AddRange(new ToolStripItem[] { size, rad, op, dock, new ToolStripSeparator() });
            m.Items.Add(Item("Верхняя строка", w.MiniHdr, () => { w.MiniHdr = !w.MiniHdr; if (w.MiniHdr) { Header.Show(); Layout(firstTime: true); } else Header.Hide(); }));
            m.Items.Add(Item("Места по уловам (фильтры — на большой карте)", w.NuPlacesOn && w.NuPlacesMini, () => { bool on = !(w.NuPlacesOn && w.NuPlacesMini); w.NuPlacesMini = on; if (on) w.NuPlacesOn = true; Radar.Invalidate(); }));
            m.Items.Add(Item("Удочки", w.MiniPanel, () => { w.MiniPanel = !w.MiniPanel; if (w.MiniPanel) Rods.Show(); else Rods.Hide(); Layout(); }));
            m.Items.Add(Item("Закрепить (щелчки проходят в игру; Ctrl — двигать)", w.MiniPinned, () => { w.MiniPinned = !w.MiniPinned; Radar.Pinned = w.MiniPinned; Radar.Invalidate(); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item("Скрыть мини-карту (клавиша \\)", false, () => { if (HideRequested != null) HideRequested(); else Hide(); }));
            m.Closed += (s, e) => m.Dispose();
            m.Show(owner, at);
        }

        // ui01_review3 #13: a monitor was unplugged or the resolution changed — bring the pieces back onto a screen
        private void OnDisplayChanged(object? sender, EventArgs e)
        {
            try { if (Visible) Radar.BeginInvoke(new Action(() => { if (Visible) Layout(firstTime: true); })); } catch { }
        }

        public void Dispose()
        {
            try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged; } catch { }
            _fast.Dispose(); _slow.Dispose();
            foreach (var p in Pieces) p.Dispose();
        }
    }
}
