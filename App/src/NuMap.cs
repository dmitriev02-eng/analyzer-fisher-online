// UI-01 / FEAT-12: new interface step 2 — the depth map of the current water: your position, recent catches,
// bait in the water and bite circles (filter by level and by rod, colour + percent, circles never overlap).
// Read-only: everything comes from the same sources as the in-game window and the F12 map (catches.db, Player.log, traffic pose).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FisherLogger
{
    /// <summary>What the map shows; built in the background every few seconds.</summary>
    public sealed class NuMapData
    {
        public uint? LocationId;
        public string LocationName = "";
        public DateTime? LocationSince;
        public WaterbodyMapInfo? Map;
        public Image? MapImage;
        public BathyInfo? Bathy;
        public Image? BathyImage;
        public List<MapCatch> Catches = new();
        public List<OverlayCastMark> Casts = new();
        public List<ChanceSpots.Spot> Spots = new();
        /// <summary>SERVER-02: объединённые уловы со всех устройств, что шлют данные на сервер.</summary>
        public List<AfoCatch> MergedCatches = new();
        public List<NuRod> Rods = new();
        public List<string> Hints = new();
        public string CurveNote = "";
        public DateTime BuiltAt;
    }

    public sealed class NuRod
    {
        public int Slot;                 // 0-based like the game; shown as Slot+1
        public string Tackle = "", Bait = "";
        public bool Active, Spinning, TrophyNormal;
        public double? X, Z;
        public int? RodCode; public double SoakMin; public int Level = -1; public string Status = "";
        public double? Chance; public int Bites, Empty;
        /// <summary>Who touched the bait on this cast (fish id, weight) — s2c 1122, as in the coach.</summary>
        public List<(uint fish, int weight)> EmptyFish = new();
        /// <summary>UI-02-7: последние пустые поклёвки этой удочки (до 5, новые первыми) — «список до 5 рыб».</summary>
        public List<(uint fish, int weight)> EmptyRecent = new();
    }

    /// <summary>Background loader for the map (only while the new window is open).</summary>
    public static class NuMapFeed
    {
        public static NuMapData Build(OverlayDataSource src, string db, uint? loc, DateTime? since, Func<uint?, string>? locName, DateTime now)
        {
            var d = new NuMapData { BuiltAt = now };
            var snap = src.Refresh(loc, since, false);
            d.LocationId = snap.LocationId; d.LocationName = snap.LocationName; d.LocationSince = snap.LocationSince;
            d.Map = snap.Map; d.MapImage = snap.MapImage; d.Casts = snap.Casts;
            // PACKS-05: на карту водоёма попадают только уловы ЭТОГО водоёма (раньше чужие «путешествовали» по координатам).
            d.Catches = snap.Catches.Where(c => !c.LocationId.HasValue || !d.LocationId.HasValue || c.LocationId == d.LocationId).ToList();
            try { d.Bathy = Bathymetry.Get(d.LocationId); if (d.Bathy is { Implemented: true }) d.BathyImage = Bathymetry.LoadOverlay(d.Bathy); } catch { }
            try
            {
                var coach = SessionCoach.Build(db, null, now, locName);
                if (coach.LocationId == null || d.LocationId == null || coach.LocationId == d.LocationId)
                {
                    d.Spots = coach.Spots;
                    d.Rods = coach.Rods.Select(r => new NuRod
                    {
                        Slot = r.Slot, Bait = r.Setup.Bait.Length > 0 ? r.Setup.Bait : r.Bait, Active = r.Active,
                        Tackle = ChanceSpots.TackleKey(r.Setup.Bait.Length > 0 ? r.Setup.Bait : r.Bait, r.HookPar),
                        Spinning = r.RodCode is int c && c is not (0 or 1), X = r.Setup.X, Z = r.Setup.Z,
                        RodCode = r.RodCode, SoakMin = r.SoakMin, Level = r.Level, Status = r.Status, TrophyNormal = r.TrophyNormal, EmptyFish = r.EmptyFish.ToList(),
                        EmptyRecent = r.RecentEmpty.Select(x => (x.Fish, x.Weight)).ToList(),
                        Chance = r.Chance, Bites = r.Bites, Empty = r.Empty
                    }).OrderBy(r => r.Slot).ToList();
                    d.Hints = coach.Hints.ToList(); d.CurveNote = coach.CurveNote;
                }
            }
            catch { }
            Enrich(d.Spots, d.LocationId, db, d.Catches, now);
            return d;
        }

        /// <summary>UX-MERGE (A1/A2): уловы всех устройств вливаются в общий слой «как свои» (только поимки, дедуп клетка+рыба+вес).</summary>
        public static void MergeMerged(NuMapData d, Func<uint, string>? fishName)
        {
            if (d.MergedCatches.Count == 0) return;
            var t = MapGridCoordinates.For(d.LocationId);
            if (t == null || !t.HasTransform) return;
            var seen = new HashSet<string>();
            foreach (var c in d.Catches)
            {
                if (!c.X.HasValue || !c.Z.HasValue) continue;
                if (MapGridCoordinates.ToGrid(d.LocationId, c.X.Value, c.Z.Value) is { } g) seen.Add(g.X + ":" + g.Y + "|" + (c.Fish ?? "") + "|" + c.Weight);
            }
            foreach (var m in d.MergedCatches)
            {
                if (m.Kind != "catch") continue;   // «поимки», как свои
                string nm = (fishName != null && m.Fish is uint fid) ? (fishName(fid) ?? "") : "";
                if (nm.Length == 0) nm = "рыба";
                string key = m.CX + ":" + m.CZ + "|" + nm + "|" + (m.Weight ?? 0);
                if (seen.Contains(key)) continue;
                double wx = t.OriginX!.Value + (m.CX - 0.5) * t.Cell;
                double wz = t.OriginZ!.Value + (m.CZ - 0.5) * t.Cell;
                long ms = m.ServerTs > 0 ? m.ServerTs : m.Ts;
                DateTime? tm = ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : null;
                d.Catches.Add(new MapCatch
                {
                    X = wx, Z = wz, Fish = nm, FishId = m.Fish, Windex = m.Windex, Weight = m.Weight ?? 0,
                    LocationId = d.LocationId, TimeText = tm?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "",
                    Verified = true, Session = "server", RecordId = "afo:" + key
                });
            }
            // UX-MERGE (A4): общие круги клёва из серверного шанса (для точек, где сервер вернул chance)
            var extra = new List<ChanceSpots.Spot>();
            foreach (var m in d.MergedCatches)
            {
                if (m.Chance is not double ch || ch <= 0 || ch > 500) continue;
                extra.Add(new ChanceSpots.Spot
                {
                    X = t.OriginX!.Value + (m.CX - 0.5) * t.Cell,
                    Z = t.OriginZ!.Value + (m.CZ - 0.5) * t.Cell,
                    Median = ch, Ticks = 1, Casts = 1, Bait = ""
                });
            }
            if (extra.Count > 0) d.Spots.AddRange(extra);
        }

        /// <summary>Same rating as the F12 map (FEAT-07 / SPOTS-05): % of the usual chance of this tackle on this water, and strong spots.</summary>
        public static void Enrich(List<ChanceSpots.Spot> spots, uint? loc, string db, List<MapCatch> catches, DateTime now)
        {
            if (spots.Count == 0) return;
            try
            {
                var lb = LiveBite.For(db, now);
                foreach (var sp in spots)
                {
                    var parts = sp.Tackle.Split('|');
                    string key = LiveBite.Key(loc, sp.Bait, parts.Length > 1 ? parts[1] : "");
                    if (lb.Typical(key) is { } ty) { sp.Lo = ty.lo; sp.Hi = ty.hi; }
                    sp.Usual = lb.Usual(key);
                }
            }
            catch { }
            foreach (var grp in spots.Where(s => s.Ticks >= ChanceSpots.MinTicks).GroupBy(s => s.Tackle).Where(g => g.Count() >= 3))
                foreach (var sp in grp)
                {
                    var others = grp.Where(o => !ReferenceEquals(o, sp)).Select(o => o.Median).OrderBy(v => v).ToList();
                    double med = others[others.Count / 2];
                    if (med <= 0 || sp.Median < 2 * med) continue;
                    sp.Strong = true; sp.Ratio = sp.Median / med;
                }
            foreach (var sp in spots)
            {
                var near = catches.Where(c => c.Bait == sp.Bait && c.X.HasValue && c.Z.HasValue && ChanceSpots.Dist(c.X.Value, c.Z.Value, sp.X, sp.Z) <= 15).ToList();
                sp.Note = string.Join(", ", near.GroupBy(c => c.Fish).OrderByDescending(g => g.Count()).Take(2).Select(g => g.Key.ToLowerInvariant()));
            }
        }
    }

    /// <summary>The depth map control of the new window.</summary>
    public sealed class NuMapView : Control
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public NuMapData? Data { get => _data; set { bool newWater = value?.LocationId != _data?.LocationId; _data = value; if (newWater) { _fitted = false; _bgKey = ""; } EnsureRod(); Invalidate(); } }
        /// <summary>UI-02-6: на карте чужого водоёма не показываем свою позу/забросы/маркер игрока (это другая карта).</summary>
        public bool ShowPose = true;
        public Func<PlayerPose?>? PoseSource;
        public Action? OpenFullMap;
        public Func<string>? FullMapKeyText;

        // circles settings (kept by the window between data refreshes)
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool ZonesOn { get; set; } = true;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int ZoneMinLevel { get; set; } = 0;       // 0 все, 1 обычно и выше, 2 выше обычного
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int? ZoneRodSlot { get; set; }            // null — rods unknown: all tackles
        public event Action? ZoneSettingsChanged;

        // UI-02/5: «Места по уловам» — old catches grouped into places, with filters (kept by the window in the settings)
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool PlacesOn { get; set; } = true;
        /// <summary>UX-MERGE (B2): тепловая карта плотности уловов (по умолчанию выкл.).</summary>
        [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool HeatmapOn { get; set; } = false;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public NuPlaceFilter PlaceFilter { get; set; } = new();
        /// <summary>MAPS-FIX-01: имя рыбы по id — для фильтра объединённых уловов.</summary>
        public Func<uint, string>? FishName;
        public event Action? PlaceSettingsChanged;
        /// <summary>Click on a place: the journal filtered to this place.</summary>
        public Action<NuPlace, NuPlaceFilter>? OpenPlaceJournal;
        public List<NuPlace> CurrentPlaces => PlacesOn ? NuPlaces.Cached(_data, PlaceFilter) : new List<NuPlace>();
        private void PlacesReady() { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(Invalidate)); } catch { } }
        private List<(NuPlace p, PointF c, float r)> _placesShown = new();
        private NuPlace? _placeHover;
        /// <summary>Filters folded (click on the title): the layer stays on, the panel takes one line.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool PlacesFolded { get; set; }

        private NuMapData? _data;
        private double _cx, _cz, _scale = 1;
        private bool _fitted, _userMoved, _dragging;
        private Point _down, _last;
        private DateTime _userMovedAt;
        private readonly List<(Rectangle r, Action a)> _hits = new();
        private List<PlacedCircle> _placed = new();
        private PlacedCircle? _hover;
        private Point _mouse;
        public int HiddenCircles { get; private set; }

        public sealed class PlacedCircle { public ChanceSpots.Spot Spot = null!; public PointF C; public float R; }

        public NuMapView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            _bgIdle.Tick += (s, e) => { if (!Interactive) { _bgIdle.Stop(); Invalidate(); } };
            BackColor = Color.FromArgb(0x12, 0x30, 0x40); Cursor = Cursors.Default; AccessibleName = "Карта глубин водоёма"; AccessibleRole = AccessibleRole.Graphic;
        }

        // ------------------------------------------------------------ view
        public PointF W2S(double x, double z) => new((float)(Width / 2.0 + (x - _cx) * _scale), (float)(Height / 2.0 - (z - _cz) * _scale));
        public (double x, double z) S2W(Point p) => (_cx + (p.X - Width / 2.0) / _scale, _cz - (p.Y - Height / 2.0) / _scale);
        public double Scale => _scale;

        public PlayerPose? Pose
        {
            get
            {
                var p = PoseSource?.Invoke(); if (p == null) return null;
                if ((DateTime.Now - p.Time).TotalMinutes > 30) return null;
                if (_data?.LocationSince is DateTime s && p.Time < s.AddSeconds(-2)) return null;
                return p;
            }
        }

        /// <summary>Whole water if it is small; otherwise a ~500 m window around you.</summary>
        public void Fit()
        {
            var m = _data?.Map; var p = Pose;
            if (Width < 50 || Height < 50) return;
            if (m != null && m.SpanX > 0 && m.SpanZ > 0)
            {
                _cx = (m.MinX + m.MaxX) / 2; _cz = (m.MinZ + m.MaxZ) / 2;
                _scale = Math.Min((Width - 40) / m.SpanX, (Height - 40) / m.SpanZ);
                // you are on one pond of a big map: show ~320 m around you (never smaller than the whole map)
                if (p != null) { _cx = p.X; _cz = p.Z; _scale = Math.Max(_scale, Math.Min(Width, Height) / 220.0); }
                else if (FocusOfData() is { } fc) { _cx = fc.x; _cz = fc.z; _scale = Math.Max(_scale, Math.Min(Width, Height) / 220.0); }
            }
            else if (p != null) { _cx = p.X; _cz = p.Z; _scale = Math.Min(Width, Height) / 300.0; }
            else if (_data?.Catches.Count > 0)
            {
                var xs = _data.Catches.Where(c => c.X.HasValue).Select(c => c.X!.Value).ToList(); var zs = _data.Catches.Where(c => c.Z.HasValue).Select(c => c.Z!.Value).ToList();
                if (xs.Count > 0) { _cx = (xs.Min() + xs.Max()) / 2; _cz = (zs.Min() + zs.Max()) / 2; _scale = Math.Min((Width - 80) / Math.Max(60, xs.Max() - xs.Min()), (Height - 80) / Math.Max(60, zs.Max() - zs.Min())); }
            }
            _fitted = true; _userMoved = false; Invalidate();
        }

        private (double x, double z)? FocusOfData()
        {
            var pts = (_data?.Spots.Select(s => (s.X, s.Z)) ?? Enumerable.Empty<(double, double)>())
                .Concat(_data?.Casts.Select(c => (c.X, c.Z)) ?? Enumerable.Empty<(double, double)>()).ToList();
            if (pts.Count == 0) pts = _data?.Catches.Where(c => c.X.HasValue && c.Z.HasValue).OrderByDescending(OverlayDataSource.SortTime).Take(30).Select(c => (c.X!.Value, c.Z!.Value)).ToList() ?? new();
            return pts.Count == 0 ? null : (pts.Average(q => q.Item1), pts.Average(q => q.Item2));
        }

        private (double x, double z, string text, DateTime until)? _pin;
        /// <summary>Journal / statistics: show a catch place on the map (pin for 2 minutes).</summary>
        public void ShowPin(double x, double z, string text)
        {
            _pin = (x, z, text, DateTime.Now.AddMinutes(2));
            _cx = x; _cz = z; _scale = Math.Max(_scale, Math.Min(Width, Height) / 160.0); _fitted = true; _userMoved = true; _userMovedAt = DateTime.Now; Invalidate();
        }

        private void PaintPin(Graphics g)
        {
            if (_pin is not { } p) return;
            if (DateTime.Now > p.until) { _pin = null; return; }
            var s = W2S(p.x, p.z);
            using (var pn = new Pen(NuTheme.Accent, 2.5f)) g.DrawEllipse(pn, s.X - 12, s.Y - 12, 24, 24);
            using (var b = new SolidBrush(NuTheme.Accent)) g.FillEllipse(b, s.X - 4, s.Y - 4, 8, 8);
            using var f = NuTheme.F(9.5f, FontStyle.Bold);
            var sz = TextRenderer.MeasureText(g, p.text, f, Size.Empty, TextFormatFlags.NoPadding);
            var r = new Rectangle((int)s.X + 18, (int)s.Y - 13, sz.Width + 16, 26);
            Glass(g, r, 8, 235);
            TextRenderer.DrawText(g, p.text, f, new Point(r.X + 8, r.Y + 5), NuTheme.AccentText, TextFormatFlags.NoPadding);
        }

        public void CenterOnMe() { if (Pose is { } p) { _cx = p.X; _cz = p.Z; _userMoved = false; Invalidate(); } else Fit(); }

        protected override void OnResize(EventArgs e) { base.OnResize(e); if (!_userMoved) _fitted = false; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); Focus();
            _down = _last = e.Location; _dragging = false;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e); _mouse = e.Location;
            if (e.Button == MouseButtons.Left)
            {
                if (!_dragging && Math.Abs(e.X - _down.X) + Math.Abs(e.Y - _down.Y) > 3 && !_hits.Any(h => h.r.Contains(_down))) _dragging = true;
                if (_dragging) { _cx -= (e.X - _last.X) / _scale; _cz += (e.Y - _last.Y) / _scale; _userMoved = true; _userMovedAt = DateTime.Now; Cursor = Cursors.SizeAll; Invalidate(); }
                _last = e.Location; return;
            }
            bool overUi = _hits.Any(h => h.r.Contains(e.Location));
            var hv = overUi ? null : _placed.LastOrDefault(c => Math.Pow(c.C.X - e.X, 2) + Math.Pow(c.C.Y - e.Y, 2) <= c.R * c.R);
            bool overPanel = OverPanel(e.Location);   // ui02_review5 #4: the glass panels are not see-through for the mouse
            if (overPanel) hv = null;
            var ph = overUi || overPanel || hv != null ? null : PlaceAt(e.Location);
            Cursor = overUi || ph != null ? Cursors.Hand : Cursors.Default;
            if (!ReferenceEquals(hv, _hover) || !ReferenceEquals(ph, _placeHover)) { _hover = hv; _placeHover = ph; Invalidate(); }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e); Cursor = Cursors.Default;
            if (e.Button == MouseButtons.Left && !_dragging)
            {
                var h = _hits.LastOrDefault(x => x.r.Contains(e.Location));
                if (h.a != null) h.a();
                else if (!OverPanel(e.Location) && _placed.All(c => Math.Pow(c.C.X - e.X, 2) + Math.Pow(c.C.Y - e.Y, 2) > c.R * c.R) && PlaceAt(e.Location) is { } pl) OpenPlaceJournal?.Invoke(pl, PlaceFilter);
            }
            bool was = _dragging; _dragging = false;
            if (was) Invalidate();
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover != null || _placeHover != null) { _hover = null; _placeHover = null; Invalidate(); } }

        private bool OverPanel(Point pt) => _panel.Contains(pt) || _placesPanel.Contains(pt);

        /// <summary>The place under the mouse (the smallest one when they touch).</summary>
        public NuPlace? PlaceAt(Point pt) => _placesShown.Where(q => Math.Pow(q.c.X - pt.X, 2) + Math.Pow(q.c.Y - pt.Y, 2) <= (q.r + 3) * (q.r + 3)).OrderBy(q => q.r).Select(q => q.p).FirstOrDefault();
        /// <summary>Test / tour: show the tip of a place.</summary>
        public void SetPlaceHover(NuPlace? p) { _placeHover = p; Invalidate(); }
        /// <summary>Screen centre of a place (for the test).</summary>
        public PointF ScreenOf(NuPlace p) => W2S(p.X, p.Z);
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var (wx, wz) = S2W(e.Location);
            _lastWheel = DateTime.Now;
            _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.25 : 0.8), 0.02, 60);
            _cx = wx - (e.X - Width / 2.0) / _scale; _cz = wz + (e.Y - Height / 2.0) / _scale;
            _userMoved = true; _userMovedAt = DateTime.Now; Invalidate();
        }

        private void EnsureRod()
        {
            var rods = _data?.Rods;
            if (rods == null || rods.Count == 0) { ZoneRodSlot = null; return; }
            if (ZoneRodSlot is int s && rods.Any(r => r.Slot == s)) return;
            ZoneRodSlot = (rods.FirstOrDefault(r => r.Active && !r.Spinning) ?? rods.FirstOrDefault(r => !r.Spinning) ?? rods[0]).Slot;
        }

        // ------------------------------------------------------------ circles: filter and placement (tested)
        public static List<ChanceSpots.Spot> Filter(IEnumerable<ChanceSpots.Spot> spots, int minLevel, string? tackle) =>
            spots.Where(s => s.Ticks >= ChanceSpots.MinTicks && s.Level >= minLevel && (tackle == null || s.Tackle == tackle)).ToList();

        /// <summary>
        /// Circles never overlap: the better (then fresher) spot is placed first at its true size (≈12 m, at least 14 px);
        /// a later one that would overlap is shrunk to touch its neighbour (not below 9 px), otherwise it is hidden and counted.
        /// </summary>
        public static (List<PlacedCircle> placed, int hidden) Place(IEnumerable<(ChanceSpots.Spot s, PointF c, float r)> items)
        {
            var placed = new List<PlacedCircle>(); int hidden = 0;
            foreach (var (s, c, r0) in items.OrderByDescending(i => i.s.Level).ThenBy(i => i.s.AgeMin).ThenByDescending(i => i.s.Median))
            {
                float r = r0;
                foreach (var p in placed)
                {
                    float d = (float)Math.Sqrt(Math.Pow(p.C.X - c.X, 2) + Math.Pow(p.C.Y - c.Y, 2));
                    if (d < p.R + r + 2) r = Math.Min(r, d - p.R - 2);
                }
                if (r < 9) { hidden++; continue; }
                placed.Add(new PlacedCircle { Spot = s, C = c, R = r });
            }
            return (placed, hidden);
        }

        public static Color LevelColor(int level) => level == 2 ? NuTheme.Accent : level == 1 ? NuTheme.Ok : Color.FromArgb(0x9A, 0xA9, 0xA6);
        public static string LevelLabel(ChanceSpots.Spot s) => s.Pct is int p ? (s.Level == 2 ? "↑" : s.Level == 0 ? "↓" : "") + (p >= 1000 ? "×" + (p / 100.0).ToString("0", CultureInfo.InvariantCulture) : p + "%") : "новая";

        public string? SelectedTackle => ZoneRodSlot is int s ? _data?.Rods.FirstOrDefault(r => r.Slot == s)?.Tackle : null;

        // ------------------------------------------------------------ paint
        private Bitmap? _bg; private string _bgKey = ""; private double _bgCx, _bgCz, _bgScale; private DateTime _lastWheel;
        private readonly System.Windows.Forms.Timer _bgIdle = new() { Interval = 160 };
        private bool Interactive => _dragging || (DateTime.Now - _lastWheel).TotalMilliseconds < 160;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            if (!_fitted) Fit();
            if (ShowPose && !_userMoved && Pose is { } pf)
            {   // follow the player softly: re-centre when you leave the middle 60 %
                var sp = W2S(pf.X, pf.Z);
                if (sp.X < Width * 0.2 || sp.X > Width * 0.8 || sp.Y < Height * 0.2 || sp.Y > Height * 0.8) { _cx = pf.X; _cz = pf.Z; }
            }
            if (_userMoved && (DateTime.Now - _userMovedAt).TotalMinutes > 3) _userMoved = false;
            _hits.Clear();
            PaintBackground(g);
            var d = _data;
            if (d == null || d.LocationId == null)
            {
                Center(g, "Водоём ещё не определён", "Зайдите на водоём в игре — карта появится сама");
                return;
            }
            if (d.Map == null) Center(g, "Для этого водоёма нет карты", "Уловы и круги клёва показаны на сетке");
            if (PlacesOn) PaintPlaces(g, d); else { _placesShown.Clear(); _placeHover = null; }
            PaintHeatmap(g, d);   // UX-MERGE (B2): тепловая карта плотности
            PaintCatches(g, d);   // UX-MERGE: свои + всех устройств — единый слой
            if (ZonesOn) PaintCircles(g, d); else { _placed.Clear(); _hover = null; HiddenCircles = 0; }
            if (ShowPose) PaintCasts(g, d);
            if (ShowPose) PaintPlayer(g);
            PaintPin(g);
            PaintTopInfo(g, d);
            PaintZonesPanel(g, d);
            PaintPlacesPanel(g, d);
            PaintClutterHint(g, d);   // UX-MERGE (C3): плашка «Упростить» на плотной карте
            PaintDepthLegend(g, d);
            PaintButtons(g);
            if (_hover != null) PaintTip(g, _hover, d);
            else if (_placeHover != null) PaintPlaceTip(g, _placeHover, d);
        }

        private void Center(Graphics g, string a, string b)
        {
            using var f1 = NuTheme.F(14f, FontStyle.Bold); using var f2 = NuTheme.F(10f);
            var s1 = TextRenderer.MeasureText(g, a, f1); var s2 = TextRenderer.MeasureText(g, b, f2);
            int y = Height / 2 - 24;
            TextRenderer.DrawText(g, a, f1, new Point((Width - s1.Width) / 2, y), NuTheme.Text);
            TextRenderer.DrawText(g, b, f2, new Point((Width - s2.Width) / 2, y + s1.Height + 4), NuTheme.Soft);
        }

        private void PaintBackground(Graphics g)
        {
            var d = _data; var m = d?.Map;
            string skey = $"{Width}x{Height}|{m?.LocationId}|{d?.MapImage?.GetHashCode()}|{d?.BathyImage?.GetHashCode()}";
            string key = skey + $"|{_cx:0.###}|{_cz:0.###}|{_scale:0.#####}";
            if (_bg != null && _bgKey != key && _bgKey.StartsWith(skey + "|", StringComparison.Ordinal) && Interactive)
            {   // while dragging / wheeling: move and scale the last picture (fast); exact redraw when the motion stops (F12 trick)
                double k = _scale / _bgScale;
                double wl = _bgCx - Width / 2.0 / _bgScale, wt = _bgCz + Height / 2.0 / _bgScale;
                var tl = W2S(wl, wt);
                var im = g.InterpolationMode; g.InterpolationMode = InterpolationMode.Low;
                g.Clear(BackColor);
                g.DrawImage(_bg, new RectangleF(tl.X, tl.Y, (float)(Width * k), (float)(Height * k)));
                g.InterpolationMode = im;
                _bgIdle.Stop(); _bgIdle.Start();
                return;
            }
            if (_bg == null || _bgKey != key)
            {
                _bg?.Dispose(); _bg = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using var bg = Graphics.FromImage(_bg);
                bg.SmoothingMode = SmoothingMode.AntiAlias; bg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                bg.Clear(BackColor);
                try
                {
                    if (m != null && d!.MapImage != null) MapRenderer.DrawBackground(bg, m, d.MapImage, _cx, _cz, _scale, Width, Height);
                    if (m != null && d!.BathyImage != null) MapRenderer.DrawBackground(bg, m, d.BathyImage, _cx, _cz, _scale, Width, Height);
                }
                catch { }
                if (m == null)
                {   // neutral grid, 50 m
                    using var pen = new Pen(Color.FromArgb(28, 255, 255, 255));
                    double step = 50; var (x0, z1) = S2W(new Point(0, 0)); var (x1, z0) = S2W(new Point(Width, Height));
                    for (double x = Math.Floor(x0 / step) * step; x <= x1; x += step) { var p = W2S(x, 0); bg.DrawLine(pen, p.X, 0, p.X, Height); }
                    for (double z = Math.Floor(z0 / step) * step; z <= z1; z += step) { var p = W2S(0, z); bg.DrawLine(pen, 0, p.Y, Width, p.Y); }
                }
                else if (d!.Bathy is { Implemented: true } b)
                {
                    using var f = NuTheme.F(9f, FontStyle.Bold); var placed = new List<Rectangle>();
                    foreach (var l in b.Labels.OrderBy(q => q.Kind == "max" ? 0 : 1))
                    {
                        var p = W2S(l.X, l.Z); if (p.X < 0 || p.Y < 0 || p.X > Width || p.Y > Height) continue;
                        string t = l.DepthM.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")) + " м";
                        var sz = TextRenderer.MeasureText(bg, t, f, Size.Empty, TextFormatFlags.NoPadding);
                        var r = new Rectangle((int)p.X - sz.Width / 2, (int)p.Y - sz.Height / 2, sz.Width, sz.Height);
                        if (placed.Any(q => q.IntersectsWith(Rectangle.Inflate(r, 8, 4)))) continue; placed.Add(r);
                        TextRenderer.DrawText(bg, t, f, r.Location, Color.FromArgb(0xD9, 0xF2, 0xEC), TextFormatFlags.NoPadding);
                    }
                }
                _bgKey = key; _bgCx = _cx; _bgCz = _cz; _bgScale = _scale;
            }
            g.DrawImageUnscaled(_bg, 0, 0);
        }

        private void PaintCircles(Graphics g, NuMapData d)
        {
            var rod = ZoneRodSlot is int rs ? d.Rods.FirstOrDefault(r => r.Slot == rs) : null;
            var list = Filter(d.Spots, ZoneMinLevel, rod?.Tackle);
            float m12 = (float)(ChanceSpots.MergeM * _scale);
            var items = list.Select(s => (s, W2S(s.X, s.Z), Math.Clamp(m12, 14f, 40f)))
                            .Where(t => t.Item2.X > -50 && t.Item2.Y > -50 && t.Item2.X < Width + 50 && t.Item2.Y < Height + 50);
            var (placed, hidden) = Place(items);
            _placed = placed; HiddenCircles = hidden;
            if (_hover != null) _hover = placed.FirstOrDefault(q => ReferenceEquals(q.Spot, _hover.Spot));
            using var f = NuTheme.F(9.5f, FontStyle.Bold);
            foreach (var c in placed.OrderBy(c => c.Spot.Level))
            {
                var col = LevelColor(c.Spot.Level);
                double fresh = Math.Clamp(1 - c.Spot.AgeMin / ChanceSpots.MaxAgeMin, 0.4, 1);   // old circles fade
                using (var b = new SolidBrush(Color.FromArgb((int)(70 * fresh), col))) g.FillEllipse(b, c.C.X - c.R, c.C.Y - c.R, 2 * c.R, 2 * c.R);
                using (var p = new Pen(Color.FromArgb((int)(255 * fresh), col), ReferenceEquals(c, _hover) ? 3f : 2f)) g.DrawEllipse(p, c.C.X - c.R, c.C.Y - c.R, 2 * c.R, 2 * c.R);
                string t = LevelLabel(c.Spot);
                var sz = TextRenderer.MeasureText(g, t, f, Size.Empty, TextFormatFlags.NoPadding);
                if (sz.Width > 2 * c.R + 18) continue;
                var tr = new Rectangle((int)(c.C.X - sz.Width / 2f) - 4, (int)(c.C.Y - sz.Height / 2f) - 1, sz.Width + 8, sz.Height + 2);
                using (var path = NuTheme.Round(tr, tr.Height / 2)) { using var bb = new SolidBrush(Color.FromArgb((int)(215 * fresh), 0x0B, 0x14, 0x18)); g.FillPath(bb, path); }
                TextRenderer.DrawText(g, t, f, new Point(tr.X + 4, tr.Y + 1), Color.FromArgb((int)(255 * fresh), c.Spot.Level == 0 ? NuTheme.Soft : col), TextFormatFlags.NoPadding);
            }
        }

        private void PaintCatches(Graphics g, NuMapData d)
        {
            var since = DateTime.Now.AddHours(-6);
            int n = d.Catches.Count(c => c.X.HasValue && c.Z.HasValue && (OverlayDataSource.PlausibleTime(c.TimeText) ?? DateTime.MinValue) >= since);
            // UX-MERGE (B4): при большой плотности и дальнем зуме — только крупные/трофеи (LOD), иначе карта тонет
            bool dense = n > 300 && _scale < 2.0;
            foreach (var c in d.Catches.Where(c => c.X.HasValue && c.Z.HasValue && (OverlayDataSource.PlausibleTime(c.TimeText) ?? DateTime.MinValue) >= since))
            {
                if (dense && (c.Windex ?? 0) < 2) continue;   // UX-MERGE (B4)
                if (PlaceFilter.MinClass >= 0 && (c.Windex ?? -1) < PlaceFilter.MinClass) continue;   // UX-MERGE (C3): фильтр класса действует и на точки
                var p = W2S(c.X!.Value, c.Z!.Value); if (p.X < -10 || p.Y < -10 || p.X > Width + 10 || p.Y > Height + 10) continue;
                var col = c.Windex switch { 3 => Color.FromArgb(0xF5, 0xC4, 0x51), 2 => Color.FromArgb(0x5D, 0xB3, 0xFF), 1 => NuTheme.Ok, _ => Color.FromArgb(0xC9, 0xD4, 0xD1) };
                float r = c.Windex >= 2 ? 5 : 4;
                using (var b = new SolidBrush(col)) g.FillEllipse(b, p.X - r, p.Y - r, 2 * r, 2 * r);
                using (var pn = new Pen(NuTheme.Bar, 1.5f)) g.DrawEllipse(pn, p.X - r, p.Y - r, 2 * r, 2 * r);
            }
        }

        /// <summary>SERVER-02: объединённые уловы со всех устройств (голубые точки). MAPS-FIX-01: подчиняются фильтрам «Места по уловам».</summary>
        private void PaintMergedCatches(Graphics g, NuMapData d)
        {
            if (d.MergedCatches.Count == 0) return;
            var t = MapGridCoordinates.For(d.LocationId);
            if (t == null || !t.HasTransform) return;
            var pf = PlaceFilter; var now = DateTime.Now;
            using var fill = new SolidBrush(Color.FromArgb(150, 0x35, 0xE0, 0xFF));
            using var pen = new Pen(Color.FromArgb(0x8F, 0xE8, 0xFF), 1.4f);
            foreach (var c in d.MergedCatches)
            {
                double wx = t.OriginX!.Value + (c.CX - 0.5) * t.Cell;
                double wz = t.OriginZ!.Value + (c.CZ - 0.5) * t.Cell;
                if (!OkAfo(c, pf, d.LocationId, wx, wz, now)) continue;
                var p = W2S(wx, wz);
                if (p.X < -10 || p.X > Width + 10 || p.Y < -10 || p.Y > Height + 10) continue;
                float r = c.Windex >= 2 ? 5 : 4;
                g.FillEllipse(fill, p.X - r, p.Y - r, 2 * r, 2 * r);
                g.DrawEllipse(pen, p.X - r, p.Y - r, 2 * r, 2 * r);
            }
        }

        /// <summary>MAPS-FIX-01: проверка объединённого улова теми же фильтрами (рыба/класс/глубина/часть суток/период).</summary>
        private bool OkAfo(AfoCatch c, NuPlaceFilter f, uint? loc, double wx, double wz, DateTime now)
        {
            if (f.Fish.Length > 0)
            {
                string nm = FishName != null && c.Fish is uint fid ? (FishName(fid) ?? "").Trim() : "";
                if (!string.Equals(nm, f.Fish, StringComparison.CurrentCulture)) return false;
            }
            if (f.MinClass >= 0 && (c.Windex ?? -1) < f.MinClass) return false;
            if (f.Period != 3 && c.ServerTs > 0)
            {
                var t = DateTimeOffset.FromUnixTimeMilliseconds(c.ServerTs).LocalDateTime;
                if (f.From(now) is DateTime from && t < from) return false;
            }
            if (f.DepthBand >= 0)
            {
                var (lo, hi) = NuPlaces.Band(f.DepthBand);
                if (NuPlaces.DepthAt(loc, wx, wz) is not double dd || dd < lo || dd >= hi) return false;
            }
            if (f.Part.Length > 0)
            {
                long ms = c.ServerTs > 0 ? c.ServerTs : c.Ts;
                if (ms <= 0) return false;
                var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
                if (DayParts.Part(DayParts.GameHour(t)) != f.Part) return false;
            }
            return true;
        }

        private void PaintCasts(Graphics g, NuMapData d)
        {
            using var f = NuTheme.F(8f, FontStyle.Bold);
            foreach (var m in d.Casts.Where(k => k.Active))
            {
                var p = W2S(m.X, m.Z);
                var col = m.State is "bite" or "hook" ? NuTheme.Accent : Color.FromArgb(0xE8, 0xEF, 0xEC);
                if (m.State is "bite" or "hook") using (var halo = new Pen(Color.FromArgb(200, col), 2f)) g.DrawEllipse(halo, p.X - 10, p.Y - 10, 20, 20);
                using (var b = new SolidBrush(NuTheme.Bar)) g.FillEllipse(b, p.X - 7, p.Y - 7, 14, 14);
                using (var pn = new Pen(col, 1.6f)) g.DrawEllipse(pn, p.X - 7, p.Y - 7, 14, 14);
                string t = (m.Rod + 1).ToString(CultureInfo.InvariantCulture);
                var sz = TextRenderer.MeasureText(g, t, f, Size.Empty, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, t, f, new Point((int)(p.X - sz.Width / 2f), (int)(p.Y - sz.Height / 2f)), col, TextFormatFlags.NoPadding);
            }
        }

        private void PaintPlayer(Graphics g)
        {
            var p = Pose; if (p == null) return;
            var s = W2S(p.X, p.Z); bool live = (DateTime.Now - p.Time).TotalSeconds <= 40;
            if (p.Yaw.HasValue)
            {
                using var path = new GraphicsPath(); float rad = 46;
                path.AddPie(s.X - rad, s.Y - rad, 2 * rad, 2 * rad, (float)p.Yaw.Value - 90 - 22, 44);
                using var br = new PathGradientBrush(path) { CenterPoint = s, CenterColor = Color.FromArgb(live ? 120 : 60, NuTheme.Accent), SurroundColors = new[] { Color.FromArgb(0, NuTheme.Accent) } };
                g.FillPath(br, path);
            }
            using (var halo = new SolidBrush(Color.FromArgb(live ? 60 : 30, NuTheme.Accent))) g.FillEllipse(halo, s.X - 16, s.Y - 16, 32, 32);
            using (var b = new SolidBrush(live ? NuTheme.Accent : Color.FromArgb(150, NuTheme.Accent))) g.FillEllipse(b, s.X - 7, s.Y - 7, 14, 14);
            using (var pn = new Pen(Color.FromArgb(0xFF, 0xF4, 0xEA), 2f)) g.DrawEllipse(pn, s.X - 7, s.Y - 7, 14, 14);
        }

        private static void Glass(Graphics g, Rectangle r, int rad = 10, int alpha = 214)
        {
            using var path = NuTheme.Round(r, rad); using var b = new SolidBrush(Color.FromArgb(alpha, 0x0B, 0x14, 0x18)); g.FillPath(b, path);
        }

        private string Grid(uint? loc, double x, double z) => MapGridCoordinates.ToGrid(loc, x, z) is { } gg ? gg.X + " : " + gg.Y : Math.Round(x) + " : " + Math.Round(z);

        private void PaintTopInfo(Graphics g, NuMapData d)
        {
            var parts = new List<string>();
            var p = Pose;
            // ui01_review3 #10: the position comes only when you move — say how old it is
            double age = p != null ? (DateTime.Now - p.Time).TotalMinutes : 0;
            if (ShowPose)
                parts.Add(p == null ? "Ваше место появится после первого шага в игре"
                    : age >= 2 ? "Последнее место (" + Math.Floor(age) + " мин назад) · " + Grid(d.LocationId, p.X, p.Z) : "Вы здесь · " + Grid(d.LocationId, p.X, p.Z));
            var rod = ZoneRodSlot is int rs ? d.Rods.FirstOrDefault(r => r.Slot == rs) : null;
            var cast = rod != null ? d.Casts.FirstOrDefault(k => k.Active && k.Rod == rod.Slot) : d.Casts.FirstOrDefault(k => k.Active);
            if (ShowPose && cast != null)
            {
                string depth = Bathymetry.DepthAt(d.LocationId, cast.X, cast.Z) is double dm ? " · глубина " + dm.ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")) + " м" : "";
                parts.Add("заброс " + (cast.Rod + 1) + ": " + Grid(d.LocationId, cast.X, cast.Z) + depth);
            }
            if (d.MergedCatches.Count > 0) parts.Add("в т.ч. других устройств: " + d.MergedCatches.Count);
            string t = string.Join("   ·   ", parts);
            using var f = NuTheme.F(10f);
            var sz = TextRenderer.MeasureText(g, t, f, Size.Empty, TextFormatFlags.NoPadding);
            var r = new Rectangle(16, 16, sz.Width + 40, 34);
            Glass(g, r);
            using (var b = new SolidBrush(NuTheme.Accent)) g.FillEllipse(b, r.X + 12, r.Y + 12, 10, 10);
            using (var pn = new Pen(Color.FromArgb(0xFF, 0xF4, 0xEA), 1.6f)) g.DrawEllipse(pn, r.X + 12, r.Y + 12, 10, 10);
            TextRenderer.DrawText(g, t, f, new Point(r.X + 30, r.Y + (r.Height - sz.Height) / 2), NuTheme.Text, TextFormatFlags.NoPadding);
        }

        private Rectangle _panel;
        private void PaintZonesPanel(Graphics g, NuMapData d)
        {
            int x = 16, y = 60, w = Math.Max(240, Math.Min(368, Width - 32));   // ui01_review3 #22
            bool spinning = ZoneRodSlot is int rs0 && d.Rods.FirstOrDefault(r => r.Slot == rs0) is { Spinning: true };
            string? selT = ZoneRodSlot is int rs2 ? d.Rods.FirstOrDefault(r => r.Slot == rs2)?.Tackle : null;
            bool none = Filter(d.Spots, 0, selT).Count == 0;
            int h = ZonesOn ? (d.Rods.Count > 0 ? 246 : 192) + (HiddenCircles > 0 || spinning ? 20 : 0) + (none ? 40 : 0) : 46;
            _panel = new Rectangle(x, y, w, h);
            Glass(g, _panel, 12, 225);
            using var fb = NuTheme.F(10f, FontStyle.Bold); using var f = NuTheme.F(9f); using var fs = NuTheme.F(8.5f);
            TextRenderer.DrawText(g, "Круги клёва", fb, new Point(x + 12, y + 13), NuTheme.Text, TextFormatFlags.NoPadding);
            string? selTackle = ZoneRodSlot is int rs1 ? d.Rods.FirstOrDefault(r => r.Slot == rs1)?.Tackle : null;
            int cnt = Filter(d.Spots, 0, selTackle).Count, cntAll = Filter(d.Spots, 0, null).Count;
            if (cnt > 0) TextRenderer.DrawText(g, cnt + " у этой удочки", fs, new Point(x + 110, y + 15), NuTheme.Muted, TextFormatFlags.NoPadding);
            // switch
            var sw = new Rectangle(x + w - 52, y + 12, 40, 22);
            using (var path = NuTheme.Round(sw, 11)) { using var b = new SolidBrush(ZonesOn ? NuTheme.Accent : NuTheme.Line2); g.FillPath(b, path); }
            using (var k = new SolidBrush(Color.FromArgb(0xFF, 0xF4, 0xEA))) g.FillEllipse(k, ZonesOn ? sw.Right - 19 : sw.X + 3, sw.Y + 3, 16, 16);
            _hits.Add((Rectangle.Inflate(sw, 6, 6), () => { ZonesOn = !ZonesOn; ZoneSettingsChanged?.Invoke(); Invalidate(); }));
            if (!ZonesOn) return;
            int cy = y + 46;
            // level chips
            var seg = new Rectangle(x + 12, cy, w - 24, 32);
            using (var path = NuTheme.Round(seg, 9)) { using var b = new SolidBrush(NuTheme.Panel); g.FillPath(b, path); }
            string[] labels = { "Все", "Обычно и выше", "Выше обычного" };
            int cw = (seg.Width - 6) / 3;
            for (int i = 0; i < 3; i++)
            {
                var cr = new Rectangle(seg.X + 3 + i * cw, seg.Y + 3, cw - (i < 2 ? 2 : 0), seg.Height - 6);
                bool on = ZoneMinLevel == i;
                if (on) using (var path = NuTheme.Round(cr, 7)) { using var b = new SolidBrush(NuTheme.AccentDeep); using var pn = new Pen(Color.FromArgb(0x5A, 0x3A, 0x20)); g.FillPath(b, path); g.DrawPath(pn, path); }
                TextRenderer.DrawText(g, labels[i], f, cr, on ? NuTheme.AccentText : NuTheme.Soft, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                int lv = i; _hits.Add((cr, () => { ZoneMinLevel = lv; ZoneSettingsChanged?.Invoke(); Invalidate(); }));
            }
            cy += 42;
            // rod chips: circles of one tackle only (chances of different tackle cannot be compared)
            if (d.Rods.Count > 0)
            {
                int rx = x + 12;
                foreach (var r in d.Rods.Take(4))
                {
                    string t = "Удочка " + (r.Slot + 1);
                    var sz = TextRenderer.MeasureText(g, t, f, Size.Empty, TextFormatFlags.NoPadding);
                    var cr = new Rectangle(rx, cy, sz.Width + 22, 28);
                    bool on = ZoneRodSlot == r.Slot;
                    using (var path = NuTheme.Round(cr, 7))
                    {
                        using var b = new SolidBrush(on ? NuTheme.AccentDeep : Color.Transparent); using var pn = new Pen(on ? Color.FromArgb(0x5A, 0x3A, 0x20) : NuTheme.Line2);
                        g.FillPath(b, path); g.DrawPath(pn, path);
                    }
                    TextRenderer.DrawText(g, t, f, cr, on ? NuTheme.AccentText : r.Active ? NuTheme.Text : NuTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    int slot = r.Slot; _hits.Add((cr, () => { ZoneRodSlot = slot; ZoneSettingsChanged?.Invoke(); Invalidate(); }));
                    rx = cr.Right + 6;
                }
                cy += 34;
                var sel = d.Rods.FirstOrDefault(r => r.Slot == ZoneRodSlot);
                string bait = sel == null || sel.Bait.Length == 0 ? "" : "наживка: " + sel.Bait;
                TextRenderer.DrawText(g, bait, fs, new Rectangle(x + 12, cy, w - 24, 16), NuTheme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                cy += 20;
            }
            // legend
            void Leg(int lv, string t)
            {
                var col = LevelColor(lv);
                using (var b = new SolidBrush(Color.FromArgb(64, col))) g.FillEllipse(b, x + 13, cy + 1, 12, 12);
                using (var pn = new Pen(col, 2f)) g.DrawEllipse(pn, x + 13, cy + 1, 12, 12);
                TextRenderer.DrawText(g, t, f, new Point(x + 34, cy), NuTheme.Soft, TextFormatFlags.NoPadding);
                cy += 20;
            }
            Leg(2, "↑ выше обычного для этой снасти"); Leg(1, "как обычно"); Leg(0, "↓ ниже обычного");   // ui01_review3 #26: the level is the quartile of your usual chance, not fixed 150/50 %
            TextRenderer.DrawText(g, "100 % — ваш обычный клёв на этой снасти; старые бледнеют", fs, new Rectangle(x + 12, cy + 2, w - 24, 18), NuTheme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            cy += 22;
            if (none)
            {
                TextRenderer.DrawText(g, cntAll > 0 ? "У этой удочки свежих замеров нет — переключите удочку (замеры есть у другой снасти)."
                    : "Свежих замеров нет. Круги строятся по забросам поплавком или фидером с последней смены времени суток игры (04:30, 07:20, 19:30, 21:30): сделайте заброс — круг появится после 3 замеров шанса.",
                    fs, new Rectangle(x + 12, cy, w - 24, 40), NuTheme.AccentText, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                cy += 40;
            }
            if (spinning) TextRenderer.DrawText(g, "Для спиннинга круги не строятся: приманка движется.", fs, new Point(x + 12, cy), NuTheme.AccentText, TextFormatFlags.NoPadding);
            else if (HiddenCircles > 0) TextRenderer.DrawText(g, "Ещё " + HiddenCircles + " — приблизьте карту колёсиком", fs, new Point(x + 12, cy), NuTheme.AccentText, TextFormatFlags.NoPadding);
        }

        private void PaintDepthLegend(Graphics g, NuMapData d)
        {
            if (d.Bathy is not { Implemented: true } b || b.Bands.Count == 0) return;
            using var f = NuTheme.F(9f);
            var r = new Rectangle(16, Height - 50, 290, 34);
            Glass(g, r);
            TextRenderer.DrawText(g, "Глубина", f, new Point(r.X + 12, r.Y + 9), NuTheme.Muted, TextFormatFlags.NoPadding);
            int bx = r.X + 76, bw = 140, n = b.Bands.Count;
            for (int i = 0; i < n; i++)
            {
                var band = b.Bands[i];
                // the band is drawn over the light water of the map: blend it the same way
                double al = Math.Clamp(band.Alpha, 0, 1);
                var c = band.Rgb.Length >= 3 ? Color.FromArgb((int)(157 + (band.Rgb[0] - 157) * al), (int)(213 + (band.Rgb[1] - 213) * al), (int)(255 + (band.Rgb[2] - 255) * al)) : NuTheme.Water;
                using var br = new SolidBrush(c); g.FillRectangle(br, bx + i * bw / n, r.Y + 13, bw / n + 1, 8);
            }
            string t = b.Bands[0].FromM.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")) + " – " + b.Bands[^1].ToM.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")) + " м";
            TextRenderer.DrawText(g, t, f, new Point(bx + bw + 10, r.Y + 9), NuTheme.Soft, TextFormatFlags.NoPadding);
        }

        private void PaintButtons(Graphics g)
        {
            using var f = NuTheme.F(9.5f);
            int x = Width - 16;
            void Btn(string t, Action a)
            {
                var sz = TextRenderer.MeasureText(g, t, f, Size.Empty, TextFormatFlags.NoPadding);
                var r = new Rectangle(x - sz.Width - 24, 16, sz.Width + 24, 34);
                Glass(g, r);
                using (var path = NuTheme.Round(r, 10)) { using var pn = new Pen(NuTheme.Line2); g.DrawPath(pn, path); }
                TextRenderer.DrawText(g, t, f, r, NuTheme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                _hits.Add((r, a)); x = r.X - 8;
            }
            if (OpenFullMap != null) { string k = FullMapKeyText?.Invoke() ?? ""; Btn("На весь экран" + (k.Length > 0 ? " · " + k : ""), () => OpenFullMap()); }   // ui01_review3 #27
            Btn(HeatmapOn ? "Тепло ✓" : "Тепло", () => { HeatmapOn = !HeatmapOn; Invalidate(); });   // UX-MERGE (B2)
            Btn(Pose != null ? "Ко мне" : "Весь водоём", () => { if (Pose != null) CenterOnMe(); else Fit(); });
        }

        private void PaintTip(Graphics g, PlacedCircle c, NuMapData d)
        {
            var s = c.Spot;
            string head = s.Pct is int p
                ? (s.Level == 2 ? "↑ " + p + " % — в " + (p / 100.0).ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")) + " раза лучше обычного"
                   : s.Level == 0 ? "↓ " + p + " % — хуже обычного" : p + " % — как обычно")
                : "Новая снасть — сравнивать пока не с чем";
            var lines = new List<string> { "для «" + (s.Bait.Length > 0 ? s.Bait : "без наживки") + "» · забросов " + s.Casts + ", замеров " + s.Ticks };
            if (s.Note.Length > 0) lines.Add("здесь брали: " + s.Note);
            if (s.Strong) lines.Add("в " + s.Ratio.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")) + " раза лучше других мест этой снасти");
            lines.Add("последний замер " + (s.AgeMin < 1 ? "только что" : Math.Round(s.AgeMin) + " мин назад") + " · " + Grid(d.LocationId, s.X, s.Z));
            using var fb = NuTheme.F(10.5f, FontStyle.Bold); using var f = NuTheme.F(9f);
            int w = Math.Max(TextRenderer.MeasureText(g, head, fb).Width, lines.Max(l => TextRenderer.MeasureText(g, l, f).Width)) + 24;
            int h = 14 + 22 + lines.Count * 18 + 6;
            int x = (int)(c.C.X + c.R + 12), y = (int)(c.C.Y - h / 2f);
            if (x + w > Width - 8) x = (int)(c.C.X - c.R - 12 - w);
            y = Math.Clamp(y, 8, Math.Max(8, Height - h - 8));
            var r = new Rectangle(x, y, w, h);
            using (var path = NuTheme.Round(r, 10))
            {
                using var b = new SolidBrush(NuTheme.Bar); using var pn = new Pen(Color.FromArgb(0x3F, 0x2A, 0x18));
                g.FillPath(b, path); g.DrawPath(pn, path);
            }
            TextRenderer.DrawText(g, head, fb, new Point(x + 12, y + 10), s.Level == 2 ? NuTheme.AccentText : s.Level == 1 ? NuTheme.Ok : NuTheme.Soft, TextFormatFlags.NoPadding);
            int ly = y + 36;
            foreach (var l in lines) { TextRenderer.DrawText(g, l, f, new Point(x + 12, ly), NuTheme.Soft, TextFormatFlags.NoPadding); ly += 18; }
        }

        // ------------------------------------------------------------ UI-02/5: places from old catches
        private void PaintPlaces(Graphics g, NuMapData d)
        {
            var all = NuPlaces.Peek(d, PlaceFilter, PlacesReady);
            var shown = new List<(NuPlace p, PointF c, float r)>();
            foreach (var p in all)
            {
                var c = W2S(p.X, p.Z); float r = NuPlaces.Radius(p.Count);
                if (c.X < -r || c.Y < -r || c.X > Width + r || c.Y > Height + r) continue;
                shown.Add((p, c, r));
            }
            _placesShown = shown;
            if (_placeHover != null && !shown.Any(q => ReferenceEquals(q.p, _placeHover))) _placeHover = null;
            using var f = NuTheme.F(8.5f, FontStyle.Bold);
            // big places under small ones, so every place stays visible and clickable
            foreach (var (p, c, r) in shown.OrderByDescending(q => q.r))
            {
                var col = NuPlaces.ClassColor(p.BestClass);
                bool hot = ReferenceEquals(p, _placeHover);
                using (var b = new SolidBrush(Color.FromArgb(hot ? 150 : 105, col))) g.FillEllipse(b, c.X - r, c.Y - r, 2 * r, 2 * r);
                using (var pn = new Pen(Color.FromArgb(230, 0x0B, 0x14, 0x18), hot ? 2.6f : 1.6f)) g.DrawEllipse(pn, c.X - r, c.Y - r, 2 * r, 2 * r);
                if (r >= 10)
                {
                    string t = p.Count.ToString(CultureInfo.InvariantCulture);
                    var sz = TextRenderer.MeasureText(g, t, f, Size.Empty, TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, t, f, new Point((int)(c.X - sz.Width / 2f), (int)(c.Y - sz.Height / 2f)), Color.FromArgb(0x0B, 0x14, 0x18), TextFormatFlags.NoPadding);
                }
            }
        }

        private ContextMenuStrip? _menu;
        private void ShowMenu(Point at, IEnumerable<(string text, bool on, Action act)> items)
        {
            _menu?.Dispose();
            _menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true, Font = NuTheme.F(9.5f) };
            foreach (var (text, on, act) in items)
            {
                var it = new ToolStripMenuItem(text) { Checked = on };
                it.Click += (s, e) => { act(); PlaceSettingsChanged?.Invoke(); Invalidate(); };
                _menu.Items.Add(it);
            }
            _menu.Show(this, at);
        }

        private Rectangle _placesPanel;
        public Rectangle PlacesPanelBounds => _placesPanel;
        private void PaintPlacesPanel(Graphics g, NuMapData d)
        {
            int w = Math.Max(240, Math.Min(312, Width - 32));
            int x = Width - 16 - w, y = 60;
            if (x < _panel.Right + 12) { x = 16; y = _panel.Bottom + 10; }   // narrow window: under the circles panel
            var all = PlacesOn ? NuPlaces.Peek(d, PlaceFilter, PlacesReady) : new List<NuPlace>();
            bool folded = PlacesFolded || y + 258 > Height - 56;            // ui02_review5 #6: no room below — show one line (the setting is not changed)
            int h = PlacesOn && !folded ? 258 : 46;
            _placesPanel = new Rectangle(x, y, w, h);
            Glass(g, _placesPanel, 12, 225);
            using var fb = NuTheme.F(10f, FontStyle.Bold); using var f = NuTheme.F(9f); using var fs = NuTheme.F(8.5f);
            string ttl = PlacesOn ? (folded ? "Места по уловам ▸" : "Места по уловам ▾") : "Места по уловам";
            TextRenderer.DrawText(g, ttl, fb, new Point(x + 12, y + 13), NuTheme.Text, TextFormatFlags.NoPadding);
            if (PlacesOn) _hits.Add((new Rectangle(x + 4, y + 6, 140, 30), () => { PlacesFolded = !PlacesFolded; PlaceSettingsChanged?.Invoke(); Invalidate(); }));
            if (PlacesOn)
            {
                int fish = all.Sum(p => p.Count);
                string cnt = all.Count + " " + Plural(all.Count, "место", "места", "мест") + " · " + fish + " " + Plural(fish, "рыба", "рыбы", "рыб");
                TextRenderer.DrawText(g, cnt, fs, new Point(x + 150, y + 15), NuTheme.Muted, TextFormatFlags.NoPadding);
            }
            var sw = new Rectangle(x + w - 52, y + 12, 40, 22);
            using (var path = NuTheme.Round(sw, 11)) { using var b = new SolidBrush(PlacesOn ? NuTheme.Accent : NuTheme.Line2); g.FillPath(b, path); }
            using (var k = new SolidBrush(Color.FromArgb(0xFF, 0xF4, 0xEA))) g.FillEllipse(k, PlacesOn ? sw.Right - 19 : sw.X + 3, sw.Y + 3, 16, 16);
            _hits.Add((Rectangle.Inflate(sw, 6, 6), () => { PlacesOn = !PlacesOn; _placeHover = null; PlaceSettingsChanged?.Invoke(); Invalidate(); }));
            if (!PlacesOn || folded) return;
            var pf = PlaceFilter; var cs = d.Catches;
            // filter chips: two columns, three rows; each opens a menu
            var chips = new (string label, string value, bool set, Action<Point> open)[]
            {
                ("Рыба", pf.Fish.Length > 0 ? pf.Fish : "все", pf.Fish.Length > 0, at => ShowMenu(at, new[] { ("Все рыбы", pf.Fish.Length == 0, (Action)(() => pf.Fish = "")) }
                    .Concat(NuPlaces.FishOptions(cs).Take(30).Select(o => (o.v + "  (" + o.n + ")", o.v == pf.Fish, (Action)(() => pf.Fish = o.v)))))),
                ("Класс", NuPlaces.MinClassName(pf.MinClass), pf.MinClass >= 0, at => ShowMenu(at, new[] { -1, 1, 2, 3 }.Select(v => (NuPlaces.MinClassName(v), pf.MinClass == v, (Action)(() => pf.MinClass = v))))),
                ("Глубина", NuPlaces.BandName(pf.DepthBand), pf.DepthBand >= 0, at => ShowMenu(at, Enumerable.Range(-1, NuPlaces.DepthEdges.Length).Select(v => (v < 0 ? "любая глубина дна" : NuPlaces.BandName(v), pf.DepthBand == v, (Action)(() => pf.DepthBand = v))))),
                ("Наживка", pf.Bait.Length > 0 ? pf.Bait : "любая", pf.Bait.Length > 0, at => ShowMenu(at, new[] { ("Любая наживка", pf.Bait.Length == 0, (Action)(() => pf.Bait = "")) }
                    .Concat(NuPlaces.BaitOptions(cs).Take(30).Select(o => (o.v + "  (" + o.n + ")", o.v == pf.Bait, (Action)(() => pf.Bait = o.v)))))),
                ("Время", pf.Part.Length > 0 ? pf.Part : "любое", pf.Part.Length > 0, at => ShowMenu(at, new[] { ("В любое время суток", pf.Part.Length == 0, (Action)(() => pf.Part = "")) }
                    .Concat(NuPlaces.Parts.Select(v => (v + " в игре", pf.Part == v, (Action)(() => pf.Part = v)))))),
                ("Период", NuPlaces.PeriodName(pf.Period), pf.Period != 3, at => ShowMenu(at, new[] { 0, 1, 2, 3 }.Select(v => (NuPlaces.PeriodName(v), pf.Period == v, (Action)(() => pf.Period = v))))),
            };
            int cw = (w - 24 - 8) / 2, cy = y + 46;
            for (int i = 0; i < chips.Length; i++)
            {
                var cr = new Rectangle(x + 12 + (i % 2) * (cw + 8), cy + (i / 2) * 36, cw, 30);
                var ch = chips[i];
                using (var path = NuTheme.Round(cr, 8))
                {
                    using var b = new SolidBrush(ch.set ? NuTheme.AccentDeep : NuTheme.Panel); using var pn = new Pen(ch.set ? Color.FromArgb(0x5A, 0x3A, 0x20) : NuTheme.Line2);
                    g.FillPath(b, path); g.DrawPath(pn, path);
                }
                string lab = ch.label + ": ";
                var lsz = TextRenderer.MeasureText(g, lab, fs, Size.Empty, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, lab, fs, new Point(cr.X + 9, cr.Y + (cr.Height - lsz.Height) / 2), NuTheme.Muted, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, ch.value + " ▾", f, new Rectangle(cr.X + 9 + lsz.Width, cr.Y, cr.Width - 14 - lsz.Width, cr.Height), ch.set ? NuTheme.AccentText : NuTheme.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                var open = ch.open; var at = new Point(cr.X, cr.Bottom + 2);
                _hits.Add((cr, () => open(at)));
            }
            cy += 3 * 36 + 4;
            // legend: colour = best class, size = how many fish
            int lx = x + 12;
            foreach (var (cl, name) in new[] { (0, "обычн."), (1, "хорош."), (2, "крупн."), (3, "трофей") })
            {
                using (var b = new SolidBrush(Color.FromArgb(150, NuPlaces.ClassColor(cl)))) g.FillEllipse(b, lx, cy + 2, 11, 11);
                TextRenderer.DrawText(g, name, fs, new Point(lx + 14, cy), NuTheme.Soft, TextFormatFlags.NoPadding);
                lx += 20 + TextRenderer.MeasureText(g, name, fs, Size.Empty, TextFormatFlags.NoPadding).Width;
            }
            cy += 20;
            // MAPS-FIX-01: легенда объединённых уловов (голубые точки) — что они значат.
            using (var b = new SolidBrush(Color.FromArgb(150, 0x35, 0xE0, 0xFF))) g.FillEllipse(b, x + 12, cy + 2, 11, 11);
            TextRenderer.DrawText(g, "включая уловы других устройств (сервер)", fs, new Point(x + 12 + 14, cy), NuTheme.Soft, TextFormatFlags.NoPadding);
            cy += 19;
            string note = all.Count == 0
                ? (pf.IsDefault ? "Уловов с местом на этом водоёме пока нет." : "Под эти фильтры уловов нет — ослабьте фильтр.")
                : "Цвет — лучший класс, размер — сколько рыб. Щелчок — уловы этого места из журнала (текущая база).";
            TextRenderer.DrawText(g, note, fs, new Rectangle(x + 12, cy, w - 24, 34), all.Count == 0 ? NuTheme.AccentText : NuTheme.Muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            if (!pf.IsDefault)
            {
                const string rs = "сбросить фильтры";
                var rsz = TextRenderer.MeasureText(g, rs, fs, Size.Empty, TextFormatFlags.NoPadding);
                var rr = new Rectangle(x + w - 14 - rsz.Width, y + h - 4 - rsz.Height - 2, rsz.Width + 2, rsz.Height + 2);
                using var fu = NuTheme.F(8.5f, FontStyle.Underline);
                TextRenderer.DrawText(g, rs, fu, rr.Location, NuTheme.AccentText, TextFormatFlags.NoPadding);
                _hits.Add((rr, () => { PlaceFilter = new NuPlaceFilter(); PlaceSettingsChanged?.Invoke(); Invalidate(); }));
            }
        }

        private static string Plural(int n, string one, string few, string many) { int m = n % 100, k = n % 10; return m is >= 11 and <= 14 ? many : k == 1 ? one : k is >= 2 and <= 4 ? few : many; }

        /// <summary>UX-MERGE (C3): на плотной карте с дефолтными фильтрами — плашка «Упростить» (иначе «непонятно что»).</summary>
        private void PaintClutterHint(Graphics g, NuMapData d)
        {
            var since = DateTime.Now.AddHours(-6);
            int n = d.Catches.Count(c => c.X.HasValue && c.Z.HasValue && (OverlayDataSource.PlausibleTime(c.TimeText) ?? DateTime.MinValue) >= since);
            if (n < 300 || !PlaceFilter.IsDefault) return;
            int w = Math.Min(Width - 32, 640); var r = new Rectangle(16, Height - 78, w, 62);
            Glass(g, r, 10, 218);
            using var f = NuTheme.F(9.5f); using var f2 = NuTheme.F(9f, FontStyle.Bold);
            TextRenderer.DrawText(g, "На карте " + n + " уловов — может быть плотно. Упростить:", f, new Point(r.X + 12, r.Y + 8), NuTheme.Text, TextFormatFlags.NoPadding);
            int bx = r.X + 12, by = r.Y + 30;
            void Btn(string t, Action act)
            {
                var sz = TextRenderer.MeasureText(g, t, f2, Size.Empty, TextFormatFlags.NoPadding);
                var rr = new Rectangle(bx, by, sz.Width + 20, 24);
                using (var path = NuTheme.Round(rr, 7)) { using var b = new SolidBrush(NuTheme.AccentDeep); g.FillPath(b, path); }
                TextRenderer.DrawText(g, t, f2, rr, NuTheme.AccentText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                _hits.Add((rr, act)); bx = rr.Right + 8;
            }
            Btn("Только места", () => { PlacesOn = true; PlaceSettingsChanged?.Invoke(); Invalidate(); });
            Btn("Крупные и выше", () => { PlaceFilter.MinClass = 2; PlaceSettingsChanged?.Invoke(); Invalidate(); });
            Btn("Показать всё", () => { PlaceFilter = new NuPlaceFilter(); PlaceSettingsChanged?.Invoke(); Invalidate(); });
        }

        /// <summary>UX-MERGE (B2): плотность уловов — полупрозрачные пятна по клеткам (радиус ~клетка, насыщенность ~количество).</summary>
        private void PaintHeatmap(Graphics g, NuMapData d)
        {
            if (!HeatmapOn) return;
            var t = MapGridCoordinates.For(d.LocationId);
            if (t == null || !t.HasTransform) return;
            var cells = new Dictionary<(int X, int Z), int>();
            foreach (var c in d.Catches)
            {
                if (!c.X.HasValue || !c.Z.HasValue) continue;
                if (MapGridCoordinates.ToGrid(d.LocationId, c.X.Value, c.Z.Value) is not { } gg) continue;
                cells[(gg.X, gg.Y)] = cells.GetValueOrDefault((gg.X, gg.Y)) + 1;
            }
            if (cells.Count == 0) return;
            int max = cells.Values.Max();
            float cellPx = (float)(t.Cell * _scale);
            foreach (var kv in cells)
            {
                var p = W2S(t.OriginX!.Value + (kv.Key.X - 0.5) * t.Cell, t.OriginZ!.Value + (kv.Key.Z - 0.5) * t.Cell);
                if (p.X < -20 || p.Y < -20 || p.X > Width + 20 || p.Y > Height + 20) continue;
                float rad = Math.Max(5f, cellPx * 0.7f + 3f);
                int a = (int)Math.Clamp(18.0 + 92.0 * kv.Value / Math.Max(1, max), 18, 110);
                using var b = new SolidBrush(Color.FromArgb(a, 0xFF, 0x5A, 0x1A));
                g.FillEllipse(b, p.X - rad, p.Y - rad, 2 * rad, 2 * rad);
            }
        }

        private void PaintPlaceTip(Graphics g, NuPlace p, NuMapData d)
        {
            var ru = CultureInfo.GetCultureInfo("ru-RU");
            string head = p.Count + " " + Plural(p.Count, "рыба", "рыбы", "рыб") + " здесь" + (p.BestClass >= 0 ? " · лучший класс: " + NuPlaces.ClassName(p.BestClass) : "");
            var lines = new List<string>();
            if (p.Record != null) lines.Add("лучший улов: " + NuPlaces.FishName(p.Record) + " " + NuPlaces.Kg(p.Record.Weight) + (p.Record.Windex is int rw ? " (" + NuPlaces.ClassName(rw) + ")" : ""));
            lines.Add("рыба: " + string.Join(", ", p.Fish.Take(4).Select(q => q.name.ToLowerInvariant() + " " + q.n)) + (p.Fish.Count > 4 ? " …" : ""));
            var baits = p.Baits;
            if (baits.Count > 0) lines.Add("на что: " + string.Join(", ", baits.Take(3).Select(q => q.name + " " + q.n)) + (baits.Count > 3 ? " …" : ""));
            string dep = NuPlaces.Depth(p);
            lines.Add((dep.Length > 0 ? "глубина дна " + dep + " · " : "") + "клетка " + Grid(d.LocationId, p.X, p.Z));
            if (p.Last is DateTime lt) lines.Add("последний раз " + lt.ToString("dd.MM.yy HH:mm", ru));
            lines.Add("щелчок — уловы этого места в журнале (только текущая база)");
            using var fb = NuTheme.F(10.5f, FontStyle.Bold); using var f = NuTheme.F(9f);
            int w = Math.Min(Width - 16, Math.Max(TextRenderer.MeasureText(g, head, fb).Width, lines.Max(l => TextRenderer.MeasureText(g, l, f).Width)) + 24);
            int h = 14 + 22 + lines.Count * 18 + 6;
            var c = W2S(p.X, p.Z); float r = NuPlaces.Radius(p.Count);
            int x = (int)(c.X + r + 12), y = (int)(c.Y - h / 2f);
            if (x + w > Width - 8) x = (int)(c.X - r - 12 - w);
            x = Math.Clamp(x, 8, Math.Max(8, Width - w - 8));
            y = Math.Clamp(y, 8, Math.Max(8, Height - h - 8));
            var rect = new Rectangle(x, y, w, h);
            using (var path = NuTheme.Round(rect, 10)) { using var b = new SolidBrush(NuTheme.Bar); using var pn = new Pen(Color.FromArgb(0x3F, 0x2A, 0x18)); g.FillPath(b, path); g.DrawPath(pn, path); }
            TextRenderer.DrawText(g, head, fb, new Point(x + 12, y + 10), p.BestClass >= 1 ? NuPlaces.ClassColor(p.BestClass) : NuTheme.Text, TextFormatFlags.NoPadding);
            int ly = y + 36;
            for (int i = 0; i < lines.Count; i++) { TextRenderer.DrawText(g, lines[i], f, new Rectangle(x + 12, ly, w - 20, 18), i == lines.Count - 1 ? NuTheme.AccentText : NuTheme.Soft, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding); ly += 18; }
        }

        protected override void Dispose(bool disposing) { if (disposing) { _bg?.Dispose(); _bgIdle.Dispose(); _menu?.Dispose(); } base.Dispose(disposing); }
    }
}
