using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace FisherLogger
{
    /// <summary>
    /// GAMEMAP-01: «карта на весь экран» поверх игры (клавиша F12). Тот же холст, что у окна карты, но для игрока:
    /// сверху — водоём и кнопки рыбы этого водоёма, «Глубины», «Другие игроки», «Закрыть»; справа — что здесь ловится и на что,
    /// нажатие в воду — координаты и глубина точки и что ловилось рядом. Без настроек фильтров, терминов и расчётов.
    /// </summary>
    public sealed partial class WaterbodyMapForm
    {
        public bool GameMode { get; private set; }
        /// <summary>Вернуть фокус игре после закрытия.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Action? ReturnFocus { get; set; }
        /// <summary>Точки других игроков: (водоём, вид или null = все виды, имя вида) → мировые X/Z.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Func<uint, uint?, string, List<(double X, double Z, string G)>>? OthersProvider { get; set; }
        public bool ShowOthers { get; private set; } = true;
        /// <summary>GAMEMAP-02: что известно о точке другого игрока (водоём, «NNN:NNN») → какая рыба и на что её обычно ловят.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Func<uint, string, string?>? OthersInfo { get; set; }
        /// <summary>SPOTS-02: пятна шанса по вашим забросам (водоём → пятна в этом времени суток игры).</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Func<uint, List<ChanceSpots.Spot>>? SpotsProvider { get; set; }
        private System.Windows.Forms.Timer? _spotsTimer;

        private void RefreshSpots()
        {
            try
            {
                uint? loc = (_water.SelectedItem as WaterItem)?.LocationId;
                Canvas.Spots = loc.HasValue && SpotsProvider != null ? SpotsProvider(loc.Value) : new();
                // SPOTS-05: оценка относительно обычного шанса своей снасти на этом водоёме и пометка точек с большим преимуществом
                var lb = LiveBite.For(_currentDb, DateTime.Now);
                foreach (var sp in Canvas.Spots)
                {
                    var parts = sp.Tackle.Split('|');
                    if (lb.Typical(LiveBite.Key(loc, sp.Bait, parts.Length > 1 ? parts[1] : "")) is { } ty) { sp.Lo = ty.lo; sp.Hi = ty.hi; }
                    sp.Usual = lb.Usual(LiveBite.Key(loc, sp.Bait, parts.Length > 1 ? parts[1] : ""));   // FEAT-07
                }
                foreach (var grp in Canvas.Spots.Where(s => s.Ticks >= ChanceSpots.MinTicks).GroupBy(s => s.Tackle).Where(gr => gr.Count() >= 3))
                    foreach (var sp in grp)
                    {
                        var others = grp.Where(o => !ReferenceEquals(o, sp)).Select(o => o.Median).OrderBy(v => v).ToList();
                        double med = others[others.Count / 2];
                        if (med <= 0 || sp.Median < 2 * med) continue;
                        sp.Strong = true; sp.Ratio = sp.Median / med;
                        var near = _all.Where(c => c.LocationId == loc && c.Bait == sp.Bait && c.X.HasValue && c.Z.HasValue && Math.Sqrt(Math.Pow(c.X.Value - sp.X, 2) + Math.Pow(c.Z.Value - sp.Z, 2)) <= 15).ToList();
                        var src = near.Count > 0 ? near : _all.Where(c => c.LocationId == loc && c.Bait == sp.Bait).ToList();
                        string fish = string.Join(", ", src.GroupBy(c => c.Fish).OrderByDescending(gr => gr.Count()).Take(2).Select(gr => gr.Key.ToLowerInvariant()));
                        sp.Note = (fish.Length > 0 ? fish + " " : "") + "×" + sp.Ratio.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU"));
                    }
                // SPOTS-04: шанс — про снасть, а не про рыбу: лучшая точка только для рыбы, которую берёт эта наживка
                Canvas.BestAllowed = true; Canvas.BestFish = "";
                if (Canvas.BestSpot is { } best)
                {
                    var onBait = _all.Where(c => c.LocationId == loc && c.Bait == best.Bait).ToList();
                    Canvas.BestFish = string.Join(", ", onBait.GroupBy(c => c.Fish).OrderByDescending(gr => gr.Count()).Take(4).Select(gr => gr.Key + " ×" + gr.Count()));
                    if (_species.SelectedIndex > 0 && _species.SelectedItem is SpeciesItem si && !onBait.Any(c => c.FishId == si.FishId)) Canvas.BestAllowed = false;
                }
                Canvas.Invalidate();
            }
            catch { }
        }

        // ---------- GAMEMAP-02: пустые поклёвки по местам ----------
        private List<(uint? water, double x, double z, uint fish, int weight)>? _empties;

        private List<(uint? water, double x, double z, uint fish, int weight)> Empties()
        {
            if (_empties != null) return _empties;
            var l = new List<(uint?, double, double, uint, int)>();
            try
            {
                using var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + _currentDb + ";Mode=ReadOnly;Pooling=False");
                c.Open();
                using var q = new Microsoft.Data.Sqlite.SqliteCommand(
                    "SELECT e.fish_lang_id, e.weight, k.cast_x, k.cast_z, (SELECT MAX(s.location_id) FROM server_checks s WHERE s.cast_id = e.cast_id) " +
                    "FROM cast_events e JOIN casts k ON k.id = e.cast_id WHERE e.event_type = 'empty_bite' AND e.fish_lang_id IS NOT NULL AND k.cast_x IS NOT NULL", c);
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    uint? w = r.IsDBNull(4) ? null : BiteAnalytics.WaterFromSpawn(r.GetInt64(4), new Dictionary<long, uint>()) ?? (uint)r.GetInt64(4);
                    l.Add((w, r.GetDouble(2), r.GetDouble(3), (uint)r.GetInt64(0), r.IsDBNull(1) ? 0 : (int)r.GetInt64(1)));
                }
            }
            catch { }
            return _empties = l;
        }

        private static double ZoneRadius(List<MapCatch> cs, double cx, double cz)
            => Math.Max(5, cs.Where(c => c.X.HasValue && c.Z.HasValue).Select(c => Math.Sqrt(Math.Pow(c.X!.Value - cx, 2) + Math.Pow(c.Z!.Value - cz, 2))).DefaultIfEmpty(0).Max());

        /// <summary>
        /// «Пытались клевать, но не засеклись: Плотва ×13 (0,3–0,5 кг)… Чтобы ловить карпа: «Бойл Сливки» — так вы его ловили здесь».
        /// Пустая поклёвка — рыба тронула наживку; сервер называет её вид и вес. Совет — наживка, на которую ВЫ ловили эту рыбу на этом
        /// водоёме, иначе — из описаний игры. Пусто — пустых поклёвок здесь не было.
        /// </summary>
        private string EmptyNote(uint? water, double x, double z, double radius)
        {
            var near = Empties().Where(e => (water == null || e.water == null || e.water == water) && Math.Sqrt(Math.Pow(e.x - x, 2) + Math.Pow(e.z - z, 2)) <= radius).ToList();
            if (near.Count == 0) return "";
            var ru = CultureInfo.GetCultureInfo("ru-RU");
            var groups = near.GroupBy(e => e.fish).OrderByDescending(g => g.Count()).Take(3).ToList();
            string W(IEnumerable<int> ws) { double lo = ws.Min() / 1000.0, hi = ws.Max() / 1000.0; return Math.Abs(hi - lo) < 0.05 ? lo.ToString("0.0", ru) + " кг" : lo.ToString("0.0", ru) + "–" + hi.ToString("0.0", ru) + " кг"; }
            var lines = new List<string> { "Пытались клевать, но не засеклись: " + string.Join(", ", groups.Select(g => SessionCoach.FishNameOf(g.Key) + (g.Count() > 1 ? " ×" + g.Count() : "") + " (" + W(g.Select(e => e.weight)) + ")")) + "." };
            // совет по наживке для самой крупной из «пытавшихся» рыб
            var target = groups.OrderByDescending(g => g.Max(e => e.weight)).First();
            string fname = SessionCoach.FishNameOf(target.Key);
            var own = _all.Where(c => c.FishId == target.Key && (water == null || c.LocationId == water) && c.Bait.Length > 0)
                          .GroupBy(c => c.Bait).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            if (own != null) lines.Add($"Чтобы ловить: {fname.ToLowerInvariant()} — наживка «{own}» (на неё вы ловили эту рыбу на этом водоёме).");
            else if (GameKnowledge.BaitsFor(target.Key).Take(3).ToList() is { Count: > 0 } gb) lines.Add($"Чтобы ловить: {fname.ToLowerInvariant()} — по описанию игры подойдёт: {string.Join(", ", gb)}.");
            return string.Join("\r\n", lines);
        }
        public IReadOnlyList<Button> FishChips => _chipButtons;

        private Panel? _gameBar;
        private Label? _gameTitle, _gameClock;
        private FlowLayoutPanel? _chips;
        private readonly List<Button> _chipButtons = new();
        private Button? _btnDepth, _btnOthers;
        private Label? _legend;

        private void InitGameMode(Control left, Control right)
        {
            GameMode = true;
            Text = "Карта водоёма — FisherLogger";
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; KeyPreview = true; StartPosition = FormStartPosition.Manual; MinimumSize = Size.Empty;
            left.Visible = false;
            right.Width = 420;
            _chkBathy.Checked = true; _chkGrid.Checked = false; _chkLabels.Checked = false; _chkMarkers.Checked = false; _chkZones.Checked = true;
            _detTitle.Text = "Нажмите на карту";
            _detText.Text = "Круг — место, где вы ловили. Нажмите на круг — увидите, какая рыба здесь клюёт, на что и как часто.\r\n\r\n" +
                            "Нажмите в любом месте воды — покажу координаты, глубину и что ловилось рядом.\r\n\r\n" +
                            "Колёсико — приблизить, зажать мышь — двигать карту. Закрыть — F12 или Esc.";

            _gameBar = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Theme.Surface, Padding = new Padding(16, 10, 16, 10) };
            _gameTitle = new Label { Dock = DockStyle.Left, AutoSize = false, Width = 330, Font = Theme.Heading, ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft, Text = "Карта водоёма" };
            var rightBtns = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Theme.Surface, Padding = new Padding(0, 2, 0, 0) };
            _btnDepth = Pill("Глубины", (s, e) => { _chkBathy.Checked = !_chkBathy.Checked; UpdatePills(); });
            _btnOthers = Pill("Другие игроки", (s, e) => { ShowOthers = !ShowOthers; GameRefresh(); });
            var close = Pill("✕  Закрыть  (F12)", (s, e) => HideGame());
            Theme.StyleButton(close, Theme.ButtonNeutral);
            rightBtns.Controls.AddRange(new Control[] { _btnDepth, _btnOthers, close });
            _chips = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = false, BackColor = Theme.Surface, Padding = new Padding(0, 2, 0, 0) };
            // DAYPART-01: часы игры и отсчёт до смены времени суток (клёв меняется сразу у всех удочек)
            _gameClock = new Label { Dock = DockStyle.Left, AutoSize = false, Width = 330, Font = new Font("Segoe UI", 10f), ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft, Text = DayParts.Clock(DateTime.Now) };
            _gameBar.Controls.Add(_chips); _gameBar.Controls.Add(rightBtns); _gameBar.Controls.Add(_gameClock); _gameBar.Controls.Add(_gameTitle);
            _legend = new Label
            {
                Dock = DockStyle.Bottom, Height = 30, BackColor = Theme.Surface, ForeColor = Theme.TextMuted, Font = UiKit.Small, Padding = new Padding(16, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft,
                Text = "●  круг — ваше место: ярче — больше рыбы      ◆  здесь ловили другие игроки      цвет воды — глубина: светлее — мельче"
            };
            Controls.Add(_legend);
            Controls.Add(_gameBar);
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; HideGame(); } };
            FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing && GameMode) { e.Cancel = true; HideGame(); } };
            UpdatePills();
        }

        private Button Pill(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, Height = 38, Margin = new Padding(8, 0, 0, 0), Padding = new Padding(10, 0, 10, 0), Cursor = Cursors.Hand };
            Theme.StyleButton(b, Theme.ButtonSecondary); b.Click += click; return b;
        }

        private void UpdatePills()
        {
            if (_btnDepth != null) { Theme.StyleButton(_btnDepth, _chkBathy.Checked && _chkBathy.Enabled ? Theme.Accent : Theme.ButtonSecondary); _btnDepth.Enabled = _chkBathy.Enabled; }
            if (_btnOthers != null) Theme.StyleButton(_btnOthers, ShowOthers ? Theme.Accent : Theme.ButtonSecondary);
        }

        public void HideGame() { Hide(); ReturnFocus?.Invoke(); }

        /// <summary>Обновить верхнюю панель (водоём, кнопки рыбы) и точки других игроков после смены водоёма/рыбы/загрузки.</summary>
        private void GameRefresh()
        {
            if (!GameMode || _chips == null) return;
            var wi = _water.SelectedItem as WaterItem;
            string name = wi?.LocationId is uint l ? (MapRegistry.Get(l)?.Name ?? _parser.GetLocation(l)?.Name ?? "Водоём") : "Водоём";
            if (_gameTitle != null) _gameTitle.Text = name;
            // кнопки рыбы этого водоёма (по вашим уловам), «Все рыбы» — первая
            _chips.SuspendLayout();
            _chips.Controls.Clear(); _chipButtons.Clear();
            int sel = _species.SelectedIndex;
            for (int i = 0; i < _species.Items.Count && i < 8; i++)
            {
                if (_species.Items[i] is not SpeciesItem si) continue;
                int idx = i;
                var b = Pill(i == 0 ? "Все рыбы" : si.Name, (s, e) => { if (_species.SelectedIndex != idx) _species.SelectedIndex = idx; });
                if (i > 0 && IconLoader.Load(si.IconPath) is Image img) { b.Image = new Bitmap(img, new Size(28, 18)); b.ImageAlign = ContentAlignment.MiddleLeft; b.TextImageRelation = TextImageRelation.ImageBeforeText; }
                Theme.StyleButton(b, idx == sel ? Theme.Accent : Theme.ButtonNeutral);
                _chips.Controls.Add(b); _chipButtons.Add(b);
            }
            _chips.ResumeLayout();
            // точки других игроков: выбранная рыба или все виды этого водоёма
            if (wi?.LocationId is uint loc && ShowOthers && OthersProvider != null)
            {
                var sp = _species.SelectedItem as SpeciesItem;
                var pts = _species.SelectedIndex <= 0 ? OthersProvider(loc, null, "") : OthersProvider(loc, sp?.FishId, sp?.Name ?? "");
                Canvas.RefPoints = pts; _refLoc = loc; Canvas.RefVisible = pts.Count > 0;
                Canvas.RefLabel = pts.Count > 0 ? "Здесь ловили другие игроки" + (_species.SelectedIndex > 0 && sp != null ? ": " + sp.Name : "") : "";
            }
            else { Canvas.RefVisible = false; Canvas.RefLabel = ""; }
            RefreshSpots();
            if (_gameClock != null) _gameClock.Text = DayParts.Clock(DateTime.Now);
            if (_spotsTimer == null && SpotsProvider != null)
            {
                _spotsTimer = new System.Windows.Forms.Timer { Interval = 8000 };   // новый такт шанса ~8 с
                _spotsTimer.Tick += (s, e) => { if (Visible && GameMode) { RefreshSpots(); if (_gameClock != null) _gameClock.Text = DayParts.Clock(DateTime.Now); } };
                _spotsTimer.Start();
            }
            UpdatePills();
            Canvas.Invalidate();
        }

        /// <summary>«Клюёт здесь: часто/иногда/редко» — по вашим уловам и забросам в этом круге; мало забросов — не судим.</summary>
        private string? BiteHere(List<MapCatch> cs, double cx, double cz)
        {
            if (cs.Count == 0) return null;
            double r = Math.Max(5, cs.Where(c => c.X.HasValue && c.Z.HasValue).Select(c => Math.Sqrt(Math.Pow(c.X!.Value - cx, 2) + Math.Pow(c.Z!.Value - cz, 2))).DefaultIfEmpty(0).Max() + 1);
            var key = cs.FirstOrDefault()?.WaterKey;
            uint? loc = cs.Select(c => c.LocationId).FirstOrDefault(l => l.HasValue);
            int casts = _casts.Count(k => (loc == null || k.WaterKey.StartsWith("L" + loc + "/", StringComparison.Ordinal)) && Math.Sqrt(Math.Pow(k.X - cx, 2) + Math.Pow(k.Z - cz, 2)) <= r);
            int fish = cs.Select(c => c.CastId).Where(x => x.HasValue).Distinct().Count() + cs.Count(c => !c.CastId.HasValue);
            if (casts < 5) return null;
            double rate = Math.Min(1, fish / (double)casts);
            return "клюёт здесь " + (rate >= 0.5 ? "часто" : rate >= 0.2 ? "иногда" : "редко");
        }

        /// <summary>Нажатие в воду: координаты, глубина и что ловилось рядом (до 25 м) — простыми словами.</summary>
        private void ShowGamePoint(double x, double z)
        {
            if (Canvas.SelectedZone != null) return;
            var ci = CultureInfo.GetCultureInfo("ru-RU");
            uint? loc = (_water.SelectedItem as WaterItem)?.LocationId ?? Canvas.MapInfo?.LocationId;
            string coord = MapGridCoordinates.ToGrid(loc, x, z) is { } g ? "координаты " + MapGridCoordinates.FormatGrid(g) : "X " + x.ToString("0", ci) + ", Z " + z.ToString("0", ci);
            double? d = Bathymetry.DepthAt(Canvas.Bathy?.LocationId ?? loc, x, z);
            _clusterList.Visible = false; _detIcon.Image = null; ShowCard(false);
            _detTitle.Text = (d.HasValue ? "Глубина " + d.Value.ToString("0.0", ci) + " м" : "Точка на карте") + "\r\n" + coord;
            var near = Canvas.Points.Where(c => c.X.HasValue && c.Z.HasValue && Math.Sqrt(Math.Pow(c.X!.Value - x, 2) + Math.Pow(c.Z!.Value - z, 2)) <= 25).ToList();
            var lines = new List<string>();
            if (near.Count > 0)
            {
                lines.Add("Рядом (до 25 м) у вас ловилось:");
                foreach (var gr in near.GroupBy(c => c.Fish).OrderByDescending(gg => gg.Count()).Take(5))
                {
                    var bait = gr.Where(c => c.Bait.Length > 0).GroupBy(c => c.Bait).OrderByDescending(b => b.Count()).Select(b => b.Key).FirstOrDefault();
                    lines.Add("• " + gr.Key + " — " + gr.Count() + " " + PlaceStats.Plural(gr.Count()) + ", до " + UiKit.Grams(gr.Max(c => c.Weight)) + (bait != null ? ", на «" + bait + "»" : ""));
                }
            }
            else lines.Add("Рядом (до 25 м) у вас уловов не было.");
            // SPOTS-02: пятно шанса под точкой — что показали ваши забросы здесь в этом времени суток игры
            var spot = Canvas.Spots.Where(s => s.Ticks >= ChanceSpots.MinTicks).Select(s => (s, dd: Math.Sqrt(Math.Pow(s.X - x, 2) + Math.Pow(s.Z - z, 2))))
                                   .Where(t => t.dd <= Math.Max(ChanceSpots.MergeM, 12 / Math.Max(0.01, Canvas.ViewScale))).OrderBy(t => t.dd).Select(t => t.s).FirstOrDefault();
            if (spot != null && ReferenceEquals(spot, Canvas.BestSpot))
                lines.Insert(0, "★ Лучшая из проверенных точек только для наживки «" + spot.Bait + "» на этом крючке. Шанс показывает, как часто клюёт на эту снасть, " +
                                "а не какая рыба: для другой рыбы нужна своя снасть и своя разведка. " +
                                (Canvas.BestFish.Length > 0 ? "На эту наживку здесь у вас ловились: " + Canvas.BestFish + ". " : "На эту наживку здесь у вас уловов ещё не было. ") +
                                "В непроверенных местах может быть лучше — проверяйте забросами в 25–30 м друг от друга.\r\n");
            if (spot != null)
                lines.Insert(spot == Canvas.BestSpot ? 1 : 0, (spot.Pct is int pc ? "Клёв здесь " + pc + "% от обычного для этой снасти (шанс " : "Новая снасть, сравнить не с чем (шанс ") +   // FEAT-07
                                spot.Median.ToString(spot.Median < 10 ? "0.0" : "0", ci) + ") — " + spot.Grade +
                                (spot.Lo.HasValue ? " (обычно " + spot.Lo.Value.ToString("0.0", ci) + "–" + spot.Hi!.Value.ToString("0.0", ci) + ")" : "") +
                                (spot.Strong ? $", в {spot.Ratio.ToString("0.#", ci)} раза лучше других ваших точек этой снасти" + (spot.Note.Length > 0 ? " (" + spot.Note.Split('×')[0].Trim() + ")" : "") : "") +
                                " (ваши забросы " + Math.Max(1, Math.Round(spot.AgeMin)).ToString("0", ci) + " мин назад" + (spot.Bait.Length > 0 ? ", наживка «" + spot.Bait + "»" : "") +
                                "). Сравнивайте только с кругами той же снасти; после смены времени суток игры шанс меняется.\r\n");
            string empt = EmptyNote(loc, x, z, 25);
            if (empt.Length > 0) lines.Add("\r\n" + empt);
            // точки других игроков: нажали на ромб (или рядом) — какая рыба и на что её обычно ловят
            if (Canvas.RefVisible && Canvas.RefPoints.Count > 0)
            {
                double hitM = 14 / Math.Max(0.01, Canvas.ViewScale);
                var hit = Canvas.RefPoints.Select(p => (p, d: Math.Sqrt(Math.Pow(p.X - x, 2) + Math.Pow(p.Z - z, 2)))).Where(t => t.d <= Math.Max(hitM, 3)).OrderBy(t => t.d).Select(t => t.p).FirstOrDefault();
                if (hit.G != null && loc.HasValue && OthersInfo?.Invoke(loc.Value, hit.G) is string info && info.Length > 0)
                {
                    _detTitle.Text = "Точка других игроков\r\n" + (d.HasValue ? "глубина " + d.Value.ToString("0.0", ci) + " м · " : "") + "координаты " + hit.G;
                    lines.Insert(0, info + "\r\n");
                }
                else
                {
                    int others = Canvas.RefPoints.Count(p => Math.Sqrt(Math.Pow(p.X - x, 2) + Math.Pow(p.Z - z, 2)) <= 25);
                    if (others > 0) lines.Add("\r\nДругие игроки ловили рядом: " + others + " " + (others == 1 ? "точка" : others < 5 ? "точки" : "точек") + " — нажмите на ромб, увидите, какая рыба и на что.");
                }
            }
            _detText.Text = string.Join("\r\n", lines);
        }
    }
}
