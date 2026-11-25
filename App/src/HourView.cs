using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace FisherLogger
{
    /// <summary>HOURS-01: вкладка Советника «По часам» — водоём, рыба, период; 24 игровых часа; частая рыба над часами; список рыбы по часу.</summary>
    public sealed class HourView : UserControl
    {
        internal readonly ComboBox WaterCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        internal readonly ComboBox FishCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        internal readonly ComboBox PeriodCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        internal readonly ComboBox SeriesCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        internal readonly Label Status = UiKit.Pill("", Theme.ButtonNeutral);
        internal readonly HourBars Chart = new() { Dock = DockStyle.Top, Height = 240 };
        internal readonly Label ListTitle = new() { Dock = DockStyle.Top, Height = 30, Font = UiKit.SectionFont, ForeColor = Theme.Text, Padding = new Padding(2, 6, 0, 0) };
        internal readonly StatList List = new() { Dock = DockStyle.Fill, EmptyText = "Нет записей" };
        private readonly Label _note = new() { Dock = DockStyle.Bottom, Height = 40, ForeColor = Theme.TextMuted, Font = UiKit.Small, Padding = new Padding(4) };
        private readonly Func<uint, string>? _fishName;
        private readonly Func<uint?, string>? _locName;
        private List<HourRec> _recs = new();
        private List<HourCov> _cov = new();
        private List<HourCast> _casts = new();
        private Dictionary<string, HashSet<uint>> _habitat = new();
        private bool _filling;
        private HabitatIndex? _habitatIndex;
        internal HourReport? Report { get; private set; }
        internal DateTime? NowOverride;   // для тестов

        private sealed class Item { public string Text = ""; public object? Val; public override string ToString() => Text; }

        public HourView(Func<uint, string>? fishName = null, Func<uint?, string>? locName = null)
        {
            _fishName = fishName; _locName = locName;
            BackColor = Theme.Background; ForeColor = Theme.Text; Font = Theme.Ui; Dock = DockStyle.Fill;
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 88, Padding = new Padding(8, 6, 8, 2), BackColor = Theme.Surface, WrapContents = true };
            Label L(string t) => new() { Text = t, AutoSize = true, Margin = new Padding(8, 8, 4, 0), ForeColor = Theme.TextMuted };
            bar.Controls.AddRange(new Control[] { L("Водоём:"), WaterCombo, L("Рыба:"), FishCombo, L("Период:"), PeriodCombo, L("Чьи:"), SeriesCombo });
            foreach (var (t, v) in new[] { ("Авто (сутки → 3 суток → всё)", "auto"), ("Последние сутки", "day"), ("Последние 3 суток", "3days"), ("Всё время", "all") })
                PeriodCombo.Items.Add(new Item { Text = t, Val = v });
            foreach (var (t, v) in new[] { ("Все: мои и других игроков", "auto"), ("Только мои уловы", "mine"), ("Только других игроков", "pub") })
                SeriesCombo.Items.Add(new Item { Text = t, Val = v });
            PeriodCombo.SelectedIndex = 0; SeriesCombo.SelectedIndex = 0;
            foreach (var c in new[] { WaterCombo, FishCombo, PeriodCombo, SeriesCombo }) c.SelectedIndexChanged += (s, e) => { if (!_filling) Recalc(); };
            WaterCombo.SelectedIndexChanged += (s, e) => { if (!_filling) { FillFish((FishCombo.SelectedItem as Item)?.Val); Recalc(); } };
            var top = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(12, 4, 12, 0) };
            Status.Dock = DockStyle.Fill; top.Controls.Add(Status);
            var lower = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4) };
            lower.Controls.Add(List); lower.Controls.Add(ListTitle);
            Chart.HourClicked += h => ShowList();
            Controls.Add(lower); Controls.Add(Chart); Controls.Add(top); Controls.Add(_note); Controls.Add(bar);
            Chart.Margin = new Padding(12);
        }

        /// <summary>Загрузить записи (все сессии, только чтение) и пересчитать.</summary>
        public void Reload(string dbPath, IEnumerable<Attempt>? attempts = null, HabitatIndex? habitat = null)
        {
            _habitatIndex = habitat;
            _casts = attempts != null ? HourStats.Casts(attempts) : new();
            try
            {
                string? dataDir = Path.GetDirectoryName(dbPath);
                string sessionDir = dataDir != null ? Path.GetDirectoryName(dataDir) ?? dataDir : "";
                var ds = AnalyticsLoader.Load(CompanionServer.Sources(new CompanionContext { CurrentDb = dbPath, SessionDir = sessionDir }, true), null);
                (_recs, _cov) = HourStats.From(ds);
                if (_fishName != null)
                    foreach (var r in _recs.Where(r => r.FishId.HasValue && r.Fish.StartsWith("Неизвестно", StringComparison.Ordinal)))
                    { var n = _fishName(r.FishId!.Value); if (n.Length > 0) r.Fish = n; }
            }
            catch { _recs = new(); _cov = new(); }
            _habitat = _habitatIndex != null ? HourStats.HabitatMap(_habitatIndex, _recs) : new();
            HourStats.AssignLocations(_cov, _recs, _casts);
            FillCombos();
            Recalc();
        }

        /// <summary>BRIDGE-FB-01: для графика «Когда» в карточке рыбы — свои и чужие записи вида, всё время, все водоёмы.</summary>
        internal (HourReport mine, HourReport pub) ForFish(uint fishId)
        {
            string key = "id:" + fishId.ToString(CultureInfo.InvariantCulture);
            HourReport B(string series) => HourStats.Build(_recs, _cov, _casts, new HourQuery
                { Series = series, SpeciesKey = key, Period = "all", Now = NowOverride ?? DateTime.Now, Habitat = _habitat });
            return (B("mine"), B("pub"));
        }

        internal void SetData(List<HourRec> recs, List<HourCov> cov, List<HourCast> casts) { _recs = recs; _cov = cov; _casts = casts; FillCombos(); Recalc(); }

        private void FillCombos()
        {
            _filling = true;
            object? wSel = (WaterCombo.SelectedItem as Item)?.Val, fSel = (FishCombo.SelectedItem as Item)?.Val;
            WaterCombo.Items.Clear(); WaterCombo.Items.Add(new Item { Text = "Все водоёмы", Val = null });
            foreach (var g in _recs.Where(r => r.LocationId.HasValue).GroupBy(r => r.LocationId!.Value).OrderByDescending(g => g.Count()))
            {
                string n = _locName?.Invoke(g.Key) ?? ""; if (n.Length == 0) n = MapRegistry.Get(g.Key)?.Name ?? "водоём " + g.Key;
                WaterCombo.Items.Add(new Item { Text = n, Val = g.Key });
            }
            Select(WaterCombo, wSel);
            FillFish(fSel);
            _filling = false;
        }

        /// <summary>AUDIT-02: при выбранном водоёме — только рыба, которая там водится (или записана там).</summary>
        private void FillFish(object? fSel)
        {
            bool was = _filling; _filling = true;
            var loc = (WaterCombo.SelectedItem as Item)?.Val as uint?;
            FishCombo.Items.Clear(); FishCombo.Items.Add(new Item { Text = "Вся рыба", Val = null });
            foreach (var g in _recs.GroupBy(r => r.Key).OrderByDescending(g => g.Count(r => r.Mine)).ThenByDescending(g => g.Count()))
            {
                if (loc.HasValue && !g.Any(r => r.Mine && r.LocationId == loc) && !(_habitat.TryGetValue(g.Key, out var hs) && hs.Contains(loc.Value))) continue;
                FishCombo.Items.Add(new Item { Text = g.First().Fish, Val = g.Key });
            }
            Select(FishCombo, fSel);
            _filling = was;
        }

        private static void Select(ComboBox c, object? v)
        {
            for (int i = 0; i < c.Items.Count; i++) if (Equals((c.Items[i] as Item)?.Val, v)) { c.SelectedIndex = i; return; }
            c.SelectedIndex = 0;
        }

        /// <summary>Выбрать рыбу (из вкладки «Рыба»). null — вся рыба.</summary>
        public void SetFish(uint? fishId)
        {
            string? key = fishId.HasValue ? "id:" + fishId.Value.ToString(CultureInfo.InvariantCulture) : null;
            _filling = true;
            if (key != null && !FishCombo.Items.Cast<Item>().Any(i => Equals(i.Val, key)))
                FishCombo.Items.Add(new Item { Text = (_fishName?.Invoke(fishId!.Value) is { Length: > 0 } n ? n : "вид " + fishId) + " (нет записей)", Val = key });
            Select(FishCombo, key); _filling = false;
            Recalc();
        }

        internal void Recalc()
        {
            var q = new HourQuery
            {
                LocationId = (WaterCombo.SelectedItem as Item)?.Val as uint?, SpeciesKey = (FishCombo.SelectedItem as Item)?.Val as string,
                Period = (string)((PeriodCombo.SelectedItem as Item)?.Val ?? "auto"), Series = (string)((SeriesCombo.SelectedItem as Item)?.Val ?? "auto"),
                Now = NowOverride ?? DateTime.Now, Habitat = _habitat
            };
            Report = HourStats.Build(_recs, _cov, _casts, q);
            var r = Report;
            string head = (q.SpeciesKey != null ? (FishCombo.SelectedItem as Item)!.Text : "Вся рыба") + " · " + r.SeriesText + " · " + r.PeriodText;
            UiKit.SetPill(Status, head + "  —  " + (r.AutoNote.Length > 0 ? r.AutoNote + " " : "") + r.LevelText,
                r.Level == "ok" ? Theme.AccentGreen : r.Level == "low" ? Theme.Warning : Theme.ButtonNeutral);
            Chart.SetReport(r, q.SpeciesKey == null);
            _note.Text = r.Note + " Светлее — лучшие часы (" + r.RateUnit + "), серым — " + r.LowText + ".";
            ShowList();
        }

        private void ShowList()
        {
            var r = Report; if (r == null) return;
            int h = Chart.SelectedHour;
            List<HourSpecies> sp; string title;
            if (h >= 0)
            {
                var b = r.Hours[h];
                sp = b.Species;
                title = $"Игровой час {h:00}:00–{h:00}:59 — записей: {b.N}" + (r.CastsTotal > 0 ? ", " + CompanionAdvisor.Casts(b.Casts) : $", наблюдение {b.Cov:0.#} мин") +
                        (b.Low ? " (" + r.LowText + ")" : b.N < HourStats.IconMinN ? " (мало записей)" : "") + (b.Tie ? " · явного лидера нет" : "");
            }
            else if (r.Hours.All(b => b.Species.Count <= 1) && r.Top.Count == 1)
            {
                // выбрана одна рыба: список часов — лучшие сверху (записей на 10 мин наблюдения; часы с малым наблюдением — в конце, приглушённо)
                ListTitle.Text = "Когда ловится " + r.Top[0].Name + " за " + r.PeriodText + " (нажмите на час)";
                int mx = Math.Max(1, r.Hours.Max(b => b.N));
                List.SetRows(r.Hours.Where(b => b.N > 0).OrderBy(b => b.Low).ThenByDescending(b => b.Rate ?? 0).ThenByDescending(b => b.N).Select(b => new StatRow
                {
                    Title = $"{b.H:00}:00–{b.H:00}:59", Subtitle = (r.CastsTotal > 0 ? CompanionAdvisor.Casts(b.Casts) : $"наблюдение {b.Cov:0.#} мин") + (b.Rate.HasValue ? $" · {b.Rate:0.##} {r.RateUnit}" : " · " + r.LowText),
                    Value = b.N.ToString(), Fraction = (double)b.N / mx, Accent = r.Series == "pub" ? Theme.Others : Theme.AccentGreen, Muted = b.Low || r.Level != "ok"
                }));
                return;
            }
            else
            {
                sp = r.Top;
                title = "Чаще всего за " + r.PeriodText + (r.BestHours.Count > 0 ? "  ·  лучшие часы: " + string.Join(", ", r.BestHours.Select(x => x.ToString("00") + ":00")) : "") + "  (нажмите на час)";
            }
            ListTitle.Text = title;
            int max = Math.Max(1, sp.Select(s => s.N).DefaultIfEmpty(1).Max());
            bool weak = r.Level != "ok" || (h >= 0 && (r.Hours[h].Low || r.Hours[h].N < HourStats.IconMinN));
            List.SetRows(sp.Select(s => new StatRow
            {
                Icon = UiKit.FishIcon(s.Id), Title = s.Name, Subtitle = (s.Share * 100).ToString("0", CultureInfo.InvariantCulture) + "% записей",
                Value = s.N.ToString(), Fraction = (double)s.N / max, Accent = r.Series == "pub" ? Theme.Others : Theme.AccentGreen, Muted = weak
            }));
        }
    }

    /// <summary>24 столбца по игровым часам; над ними — значок самой частой рыбы (если есть явный лидер); полоса наблюдения снизу.</summary>
    public sealed class HourBars : Control
    {
        private HourReport? _r;
        private bool _icons;
        [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int SelectedHour { get; internal set; } = -1;
        public event Action<int>? HourClicked;
        private Rectangle _plot;
        private readonly ToolTip _tip = new() { InitialDelay = 150 };
        private int _hover = -1;

        public HourBars()
        {
            DoubleBuffered = true; BackColor = Theme.Surface; ForeColor = Theme.Text; Font = UiKit.Small;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        public void SetReport(HourReport r, bool icons) { _r = r; _icons = icons; Invalidate(); }

        private int HourAt(int x) => _plot.Width <= 0 || x < _plot.Left || x >= _plot.Right ? -1 : Math.Clamp((x - _plot.Left) * 24 / _plot.Width, 0, 23);

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int h = HourAt(e.X);
            SelectedHour = h == SelectedHour ? -1 : h;
            Invalidate(); HourClicked?.Invoke(SelectedHour);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HourAt(e.X);
            if (h == _hover) return;
            _hover = h; Invalidate();
            if (h < 0 || _r == null) { _tip.Hide(this); return; }
            var b = _r.Hours[h];
            string t = $"{h:00}:00–{h:00}:59: записей {b.N}" + (_r.CastsTotal > 0 ? $", забросов {b.Casts}" : "") + $", наблюдение {b.Cov:0.#} мин" + (b.Low ? " — " + _r.LowText : "") +
                       (b.Rate.HasValue ? $"\n{b.Rate:0.##} {_r.RateUnit}" : "") + (b.Tie ? "\nявного лидера нет" : "") +
                       (b.Species.Count > 0 ? "\n" + string.Join("\n", b.Species.Take(5).Select(s => s.Name + " — " + s.N)) : "");
            _tip.Show(t, this, e.X + 14, e.Y + 14, 8000);
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; _tip.Hide(this); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            if (_r == null) return;
            int iconH = _icons ? 38 : 0;
            _plot = new Rectangle(44, 22 + iconH, Width - 56, Height - 70 - iconH);
            if (_plot.Width < 100 || _plot.Height < 60) return;
            int max = Math.Max(1, _r.Hours.Max(b => b.N));
            double step = NiceStep(max / 4.0);
            int top = (int)(Math.Ceiling(max / step) * step);
            using var grid = new Pen(Color.FromArgb(40, Theme.Text));
            using var txt = new SolidBrush(Theme.TextMuted);
            for (double v = 0; v <= top + 0.01; v += step)
            {
                int y = _plot.Bottom - (int)(v / top * _plot.Height);
                g.DrawLine(grid, _plot.Left, y, _plot.Right, y);
                g.DrawStringT(v.ToString("0", CultureInfo.InvariantCulture), Font, txt, 4, y - 8);
            }
            float w = _plot.Width / 24f;
            var col = _r.Series == "pub" ? Theme.Others : Theme.AccentGreen;
            var best = new HashSet<int>(_r.BestHours);
            for (int h = 0; h < 24; h++)
            {
                var b = _r.Hours[h];
                float x = _plot.Left + h * w;
                if (h == SelectedHour || h == _hover)
                    using (var hb = new SolidBrush(Color.FromArgb(h == SelectedHour ? 60 : 30, Theme.Accent))) g.FillRectangle(hb, x, _plot.Top - iconH, w, _plot.Height + iconH);
                int bh = (int)((double)b.N / top * _plot.Height);
                var c = b.Low ? Theme.TextMuted : best.Contains(h) ? Theme.Lighten(col, 0.25) : col;
                using (var br = new SolidBrush(Color.FromArgb(b.Low ? 110 : 230, c))) g.FillRectangle(br, x + w * 0.15f, _plot.Bottom - bh, w * 0.7f, bh);
                if (b.Low && b.N > 0)
                    using (var hp = new HatchBrush(HatchStyle.BackwardDiagonal, Color.FromArgb(120, Theme.Background), Color.Transparent))
                        g.FillRectangle(hp, x + w * 0.15f, _plot.Bottom - bh, w * 0.7f, bh);
                if (b.N > 0) g.DrawStringT(b.N.ToString(CultureInfo.InvariantCulture), Font, txt, x + w / 2 - 6, _plot.Bottom - bh - 15);
                g.DrawStringT(h.ToString("00", CultureInfo.InvariantCulture), Font, txt, x + w / 2 - 8, _plot.Bottom + 3);
                // усилие: забросы (мои) или минуты наблюдения (объявления) — тонкая полоса относительно максимума
                bool byCasts = _r.CastsTotal > 0;
                double cmax = Math.Max(1, byCasts ? _r.Hours.Max(q => q.Casts) : _r.Hours.Max(q => q.Cov));
                int ch = Math.Max(1, (int)(8 * (byCasts ? b.Casts : b.Cov) / cmax));
                using (var cb = new SolidBrush(b.Low ? Theme.Danger : Theme.AccentTeal)) g.FillRectangle(cb, x + 2, _plot.Bottom + 20 + (8 - ch), w - 4, ch);
                if (_icons && b.Icon != null && UiKit.FishIcon(b.Icon.Id) is Image img)
                {
                    int sz = (int)Math.Min(36, w - 2);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, x + (w - sz) / 2, _plot.Top - iconH + 2, sz, sz);
                }
                else if (_icons && b.Tie) g.DrawStringT("≈", UiKit.SectionFont, txt, x + w / 2 - 6, _plot.Top - iconH + 6);
            }
            g.DrawStringT(_r.CastsTotal > 0 ? "забросы" : "наблюдение", Font, txt, _plot.Left, _plot.Bottom + 30);
            if (_icons) g.DrawStringT("частая рыба часа (только явный лидер; ≈ — лидеров несколько)", Font, txt, _plot.Left, 2);
        }

        private static double NiceStep(double raw)
        {
            if (raw <= 1) return 1;
            double p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double m = raw / p;
            return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * p;
        }
    }
}
