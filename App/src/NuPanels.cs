// UI-01 / FEAT-15: the rest of the new window — rods strip (step 3), side panels: recent catches, tips, bite by hours (steps 3–4),
// and a host for the old forms that move inside the new window as they are (settings, phone, advisor).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace FisherLogger
{
    /// <summary>Cached fonts of the new interface (one instance per size/style — no new Font per paint).</summary>
    public static class NuFonts
    {
        private static readonly Dictionary<(float, FontStyle), Font> Cache = new();
        public static Font Get(float size, FontStyle st = FontStyle.Regular)
        {
            lock (Cache) { if (!Cache.TryGetValue((size, st), out var f)) Cache[(size, st)] = f = NuTheme.F(size, st); return f; }
        }
    }

    /// <summary>Three rods at the bottom of the map: bait, time in the water, chance, bites, advice.</summary>
    public sealed class NuRodsStrip : Control
    {
        public Func<NuMapData?>? DataSource;
        public NuRodsStrip() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(NuTheme.Line);
            var d = DataSource?.Invoke();
            var rods = d?.Rods.OrderBy(r => r.Slot).ToList() ?? new List<NuRod>();
            int n = Math.Max(3, Math.Min(rods.Count, 4)), gap = 1, w = (Width - gap * (n - 1)) / n;
            var fL = NuFonts.Get(9f); var fB = NuFonts.Get(10f, FontStyle.Bold); var fS = NuFonts.Get(9f);
            for (int i = 0; i < n; i++)
            {
                var r = new Rectangle(i * (w + gap), 1, i == n - 1 ? Width - i * (w + gap) : w, Height - 1);
                using (var b = new SolidBrush(NuTheme.Panel)) g.FillRectangle(b, r);
                var rod = i < rods.Count ? rods[i] : null;
                if (rod == null)
                {
                    TextRenderer.DrawText(g, "Удочка " + (i + 1), fL, new Point(r.X + 16, r.Y + 12), NuTheme.Muted, TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, "нет данных — забросьте, и здесь появится наживка и совет", fS, new Rectangle(r.X + 16, r.Y + 34, r.Width - 28, 36), NuTheme.Muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                    continue;
                }
                string head = "Удочка " + (rod.Slot + 1);
                TextRenderer.DrawText(g, head, fL, new Point(r.X + 16, r.Y + 12), NuTheme.Muted, TextFormatFlags.NoPadding);
                int hx = r.X + 16 + TextRenderer.MeasureText(g, head, fL, Size.Empty, TextFormatFlags.NoPadding).Width + 8;
                string bait = rod.Bait.Length > 0 ? rod.Bait : SoakCurve.RodName(rod.RodCode);
                TextRenderer.DrawText(g, bait, fB, new Rectangle(hx, r.Y + 10, r.Right - hx - 12, 18), NuTheme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                var parts = new List<string>();
                if (rod.Active) parts.Add("в воде " + TimeSpan.FromMinutes(rod.SoakMin).ToString(rod.SoakMin >= 60 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture));
                else parts.Add("не в воде");
                if (Plain.Tech && rod.Chance is double ch) parts.Add("шанс " + ch.ToString(ch < 10 ? "0.0" : "0", CultureInfo.GetCultureInfo("ru-RU")));
                parts.Add("поклёвок " + rod.Bites + (rod.Empty > 0 ? " (пустых " + rod.Empty + ")" : ""));
                TextRenderer.DrawText(g, string.Join("   ", parts), fS, new Rectangle(r.X + 16, r.Y + 34, r.Width - 28, 16), NuTheme.Soft, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                var (pt, pfg, _) = NuRodsPiece.Pill(rod);
                string advice = rod.Status.Length > 0 ? rod.Status : pt;
                var col = rod.Level >= 2 ? NuTheme.AccentText : rod.Level == 0 ? NuTheme.Ok : pfg;
                TextRenderer.DrawText(g, advice, fS, new Rectangle(r.X + 16, r.Y + 54, r.Width - 28, r.Height - 58), col, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }
    }

    /// <summary>Side panel: recent catches on this water with class chips.</summary>
    public sealed class NuCatchList : Control
    {
        public Func<NuMapData?>? DataSource;
        public Action<MapCatch>? ShowOnMap;
        private int _minClass = 0;
        private readonly List<(Rectangle r, Action a)> _hits = new();
        private readonly List<(Rectangle r, MapCatch c)> _rows = new();
        private int _scroll;
        public NuCatchList() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); BackColor = NuTheme.Panel; }
        protected override void OnMouseWheel(MouseEventArgs e) { _scroll = Math.Max(0, _scroll - Math.Sign(e.Delta) * 3); Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e) { var h = _hits.LastOrDefault(x => x.r.Contains(e.Location)); h.a?.Invoke(); }
        protected override void OnMouseDoubleClick(MouseEventArgs e) { var r = _rows.FirstOrDefault(x => x.r.Contains(e.Location)); if (r.c != null) ShowOnMap?.Invoke(r.c); }
        protected override void OnMouseMove(MouseEventArgs e) { Cursor = _hits.Any(x => x.r.Contains(e.Location)) || _rows.Any(x => x.r.Contains(e.Location)) ? Cursors.Hand : Cursors.Default; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(BackColor);
            _hits.Clear(); _rows.Clear();
            using (var pen = new Pen(NuTheme.Line)) g.DrawLine(pen, 0, 0, 0, Height);
            var d = DataSource?.Invoke();
            TextRenderer.DrawText(g, "Последние уловы", NuFonts.Get(12f, FontStyle.Bold), new Point(16, 16), NuTheme.Text);
            var all = d?.Catches.OrderByDescending(OverlayDataSource.SortTime).ToList() ?? new List<MapCatch>();
            var today = all.Where(c => OverlayDataSource.SortTime(c).Date == DateTime.Today).ToList();
            TextRenderer.DrawText(g, (d?.LocationName.Length > 0 ? d.LocationName + " · " : "") + "сегодня " + today.Count + " · " + Kg(today.Sum(c => (long)c.Weight)), NuFonts.Get(9f), new Rectangle(16, 44, Width - 24, 18), NuTheme.Muted, TextFormatFlags.EndEllipsis);
            // class chips
            string[] chips = { "Все", "Хорошие и выше", "Крупные и выше", "Трофей" };
            int x = 16, y = 70;
            for (int i = 0; i < chips.Length; i++)
            {
                var sz = TextRenderer.MeasureText(g, chips[i], NuFonts.Get(9f));
                var r = new Rectangle(x, y, sz.Width + 14, 26);
                if (r.Right > Width - 8) { x = 16; y += 30; r = new Rectangle(x, y, sz.Width + 14, 26); }   // UX-FIX: wrap long labels
                bool on = _minClass == i;
                using (var path = NuTheme.Round(r, 7)) { using var b = new SolidBrush(on ? NuTheme.AccentDeep : NuTheme.Raised); g.FillPath(b, path); }
                TextRenderer.DrawText(g, chips[i], NuFonts.Get(9f), r, on ? NuTheme.AccentText : NuTheme.Soft, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                int k = i; _hits.Add((r, () => { _minClass = k; _scroll = 0; Invalidate(); }));
                x = r.Right + 6;
            }
            var list = all.Where(c => _minClass == 0 || (c.Windex ?? -1) >= _minClass).ToList();
            y = 108;
            if (list.Count == 0)
            {
                TextRenderer.DrawText(g, d?.LocationId == null ? "Водоём ещё не определён." : "Пока нет уловов на этом водоёме — первый появится здесь сразу после поимки.", NuFonts.Get(9.5f),
                    new Rectangle(16, y, Width - 32, 60), NuTheme.Muted, TextFormatFlags.WordBreak);
                return;
            }
            _scroll = Math.Min(_scroll, Math.Max(0, list.Count - 3));
            foreach (var c in list.Skip(_scroll))
            {
                if (y + 54 > Height) break;
                var r = new Rectangle(12, y, Width - 24, 50);
                using (var path = NuTheme.Round(r, 9)) { using var b = new SolidBrush(NuTheme.Raised); g.FillPath(b, path); }
                var gc = GradeColor(c.Windex);
                using (var b = new SolidBrush(gc)) g.FillRectangle(b, r.X + 1, r.Y + 8, 3, r.Height - 16);
                string t = OverlayDataSource.PlausibleTime(c.TimeText) is DateTime dt ? Ago(dt) : "";
                var tsz = TextRenderer.MeasureText(g, t, NuFonts.Get(8.5f), Size.Empty, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, t, NuFonts.Get(8.5f), new Point(r.Right - tsz.Width - 10, r.Y + 8), NuTheme.Muted, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, c.Fish + "  " + c.Weight + " г", NuFonts.Get(10f, FontStyle.Bold), new Rectangle(r.X + 14, r.Y + 6, r.Width - tsz.Width - 30, 18), c.Windex >= 1 ? gc : NuTheme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                string cell = c.Verified && c.X.HasValue && c.Z.HasValue ? NuRadarPiece.Grid(c.LocationId, c.X.Value, c.Z.Value) : "";
                TextRenderer.DrawText(g, c.Bait + (cell.Length > 0 ? " · " + cell : ""), NuFonts.Get(8.5f), new Rectangle(r.X + 14, r.Y + 28, r.Width - 24, 16), NuTheme.Soft, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                _rows.Add((r, c));
                y += 56;
            }
            if (list.Count > 3) TextRenderer.DrawText(g, "колесо — листать · двойной щелчок — на карте", NuFonts.Get(8f), new Point(16, Height - 20), NuTheme.Muted, TextFormatFlags.NoPadding);
        }
        public static Color GradeColor(int? w) => w switch { 3 => Color.FromArgb(0xF5, 0xC4, 0x51), 2 => Color.FromArgb(0x5D, 0xB3, 0xFF), 1 => NuTheme.Ok, _ => Color.FromArgb(0x9A, 0xA9, 0xA6) };
        public static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalMinutes < 1) return "только что";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " мин назад";
            if (d.TotalHours < 24) return (int)d.TotalHours + " ч назад";
            return t.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
        }
        private static string Kg(long g) => g >= 1000 ? (g / 1000.0).ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")) + " кг" : g + " г";
    }

    /// <summary>Side panel: hints of the coach for this fishing (same as «Советы» of the old in-game window).</summary>
    public sealed class NuTipsPanel : Control
    {
        public Func<NuMapData?>? DataSource;
        public NuTipsPanel() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); BackColor = NuTheme.Panel; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(BackColor);
            using (var pen = new Pen(NuTheme.Line)) g.DrawLine(pen, 0, 0, 0, Height);
            TextRenderer.DrawText(g, "Подсказки", NuFonts.Get(12f, FontStyle.Bold), new Point(16, 16), NuTheme.Text);
            var d = DataSource?.Invoke();
            int y = 50; var f = NuFonts.Get(9.5f);
            var (_, window, left) = NuTheme.GameClock(NuTheme.MoscowNow());
            var lines = new List<(string t, Color c)>();
            if (window != null) lines.Add((window + " — ещё " + (int)Math.Ceiling(left.TotalMinutes) + " мин: самое время забрасывать.", NuTheme.AccentText));
            foreach (var r in d?.Rods ?? new List<NuRod>()) if (r.Status.Length > 0) lines.Add(("Удочка " + (r.Slot + 1) + ": " + r.Status, r.Level >= 2 ? NuTheme.AccentText : r.Level == 0 ? NuTheme.Ok : NuTheme.Soft));
            foreach (var h in d?.Hints ?? new List<string>()) lines.Add(("• " + h, NuTheme.Soft));
            if (d?.CurveNote.Length > 0) lines.Add((d.CurveNote, NuTheme.Muted));
            if (lines.Count == 0) lines.Add(("Подсказки появятся, когда вы забросите удочку: время в воде, шанс, когда перезабросить и куда сдвинуться.", NuTheme.Muted));
            foreach (var (t, c) in lines)
            {
                var sz = TextRenderer.MeasureText(g, t, f, new Size(Width - 32, 1000), TextFormatFlags.WordBreak);
                if (y + sz.Height > Height - 8) break;
                TextRenderer.DrawText(g, t, f, new Rectangle(16, y, Width - 32, sz.Height), c, TextFormatFlags.WordBreak);
                y += sz.Height + 10;
            }
        }
    }

    /// <summary>Side panel: bite windows now and by game hour on this water (your recorded catches).</summary>
    public sealed class NuHoursPanel : Panel
    {
        public Func<NuStatsData?>? Stats;
        public Func<uint?>? Water;
        private readonly NuBarChart _chart = new() { Dock = DockStyle.Fill, Title = "По часам игры", BackName = "поклёвки", FrontName = "поймано", LabelEvery = 3 };
        private readonly Control _card;
        public NuHoursPanel()
        {
            BackColor = NuTheme.Panel; Padding = new Padding(8, 8, 8, 8);
            _card = new NuWindowCard { Dock = DockStyle.Top, Height = 150 };
            Controls.Add(_chart); Controls.Add(_card);
            VisibleChanged += (s, e) => { if (Visible) { Fill(); _tick.Start(); } else _tick.Stop(); };
            // ui01_review3 #8: the clock card ticks every second; the chart follows new catches and a change of water
            _tick.Tick += (s, e) =>
            {
                _card.Invalidate();
                var w = Water?.Invoke();
                if (++_n % 30 == 0 || w != _lastWater) Fill();
            };
        }
        private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
        private int _n; private uint? _lastWater;
        protected override void Dispose(bool disposing) { if (disposing) _tick.Dispose(); base.Dispose(disposing); }
        public void Fill()
        {
            var d = Stats?.Invoke();
            if (d == null) { _chart.Note = "загружаю…"; _chart.Labels = Array.Empty<string>(); _chart.Invalidate(); return; }
            var f = new NuFilter { Water = Water?.Invoke() }; _lastWater = f.Water;
            var (b, c) = NuStatsCalc.ByGameHour(d, f, DateTime.Now);
            _chart.Labels = Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToArray(); _chart.Back = b; _chart.Front = c;
            _chart.Note = f.Water.HasValue ? "этот водоём" : "все водоёмы";
            _chart.Invalidate(); _card.Invalidate();
        }
        private sealed class NuWindowCard : Control
        {
            public NuWindowCard() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(NuTheme.Panel);
                TextRenderer.DrawText(g, "Клёв по часам", NuFonts.Get(12f, FontStyle.Bold), new Point(8, 8), NuTheme.Text);
                var msk = NuTheme.MoscowNow(); var (game, window, left) = NuTheme.GameClock(msk);
                var box = new Rectangle(8, 42, Width - 16, 64);
                using (var path = NuTheme.Round(box, 12)) { using var b = new SolidBrush(window != null ? NuTheme.AccentDeep : NuTheme.Raised); g.FillPath(b, path); }
                TextRenderer.DrawText(g, window ?? "Сейчас обычный клёв · в игре " + game, NuFonts.Get(9.5f), new Point(22, 50), window != null ? NuTheme.AccentText : NuTheme.Muted);
                // next windows (MSK): morning = even hour +22:30, evening = odd hour +37:30
                var next = new List<(DateTime at, string name)>();
                var h0 = msk.Date.AddHours(msk.Hour);
                for (int i = 0; i < 4; i++)
                {
                    var h = h0.AddHours(i);
                    var at = h.Hour % 2 == 0 ? h.AddMinutes(22.5) : h.AddMinutes(37.5);
                    if (at > msk) next.Add((at, h.Hour % 2 == 0 ? "утро" : "вечер"));
                }
                string nx = window != null ? "ещё " + left.ToString(@"mm\:ss") : "ближайшие: " + string.Join(", ", next.Take(2).Select(n => n.name + " в " + n.at.ToString("HH:mm", CultureInfo.InvariantCulture)));
                TextRenderer.DrawText(g, nx, NuFonts.Get(window != null ? 15f : 10f, FontStyle.Bold), new Point(22, 74), NuTheme.Text);
                TextRenderer.DrawText(g, "Утро 04:30–07:20 и вечер 19:30–21:30 игрового времени (по МСК).", NuFonts.Get(8.5f), new Rectangle(8, 114, Width - 16, 30), NuTheme.Muted, TextFormatFlags.WordBreak);
            }
        }
    }

    /// <summary>Hosts an old form inside the new window (no frame, fills the panel).</summary>
    public sealed class NuFormHost : Panel
    {
        private Form? _form;
        public Func<Form?>? Create;
        public string EmptyText = "";
        /// <summary>The hosted form closed itself (e.g. «Сохранить» / «Отмена» in the settings): leave the panel. Not set — open it again.</summary>
        public Action? Closed;
        public Action<string>? Log;
        public NuFormHost() { BackColor = NuTheme.Ground; Dock = DockStyle.Fill; }
        public Form? Hosted => _form;
        public void Ensure()
        {
            if (_form != null && !_form.IsDisposed) return;
            Controls.Clear();
            Form? f = null;
            try
            {
                f = Create?.Invoke();
                if (f != null)
                {
                    f.TopLevel = false; f.FormBorderStyle = FormBorderStyle.None; f.MinimumSize = Size.Empty; f.MaximumSize = Size.Empty; f.Dock = DockStyle.Fill; f.ShowInTaskbar = false;
                    var ff = f;
                    f.FormClosed += (s, e) =>
                    {
                        if (!ReferenceEquals(_form, ff)) return;
                        _form = null;
                        if (IsDisposed) return;
                        if (Closed != null) BeginInvoke(Closed);
                        else if (Visible) BeginInvoke(new Action(Ensure));
                    };
                    _form = f; Controls.Add(f); f.Show();   // ui01_review3 #2: Show() runs OnLoad of the old form — inside the try
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke("[NEWUI] встроенное окно не открылось: " + ex.Message);
                try { if (f != null) { Controls.Remove(f); f.Dispose(); } } catch { }
                _form = null; f = null;
            }
            if (f == null) { Controls.Clear(); Controls.Add(new Label { Text = EmptyText, Dock = DockStyle.Fill, ForeColor = NuTheme.Muted, TextAlign = ContentAlignment.MiddleCenter }); }
        }
        public void Drop() { var f = _form; _form = null; try { f?.Close(); } catch { } try { f?.Dispose(); } catch { } }
        // ui01_review3 #11: close the hosted form so its own FormClosed stops timers (advisor 15 s, phone 1 s)
        protected override void Dispose(bool disposing) { if (disposing) Drop(); base.Dispose(disposing); }
    }
}
