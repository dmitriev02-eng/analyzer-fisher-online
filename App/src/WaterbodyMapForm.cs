using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;

namespace FisherLogger
{
    // =====================================================================================
    // DESIGN: окно «Карта водоёма».
    //
    // Честность данных:
    //  * NEXT-135: подложка карты берётся из реестра Data\assets\maps\map_registry.json (MapRegistry.cs)
    //    по location_id (база TFBase_N), не по spawn_locid. Изображения — реконструкции карты игры
    //    (Cartografer.renderMap) из TerrainData; у каждой записи указаны способ проверки и достоверность.
    //    submap*.png сами по себе — фон интерфейса, а не карта.
    //  * Точки рисуются в игровых координатах (X вправо = восток, Z вверх = север) через WorldToScreen;
    //    подложка рисуется тем же преобразованием, поэтому метки и карта совпадают при любом масштабе.
    //    Точки вне границ карты не переносятся на край. Для водоёмов без карты — нейтральная сетка.
    //  * Показываются только уловы, чьи координаты совпадают с записанным забросом (таблица casts
    //    той же БД, |dX|,|dZ| < 0.05). Прочие («общий контекст» старых сессий) скрыты по умолчанию.
    //  * Позиция игрока не показывается: источник её координат не подтверждён.
    //  * Слой «плотность» = число записанных уловов в ячейке; это не вероятность поимки.
    // =====================================================================================

    public sealed class MapCatch
    {
        public string RecordId = "";
        public string Session = "";
        public string SessionId = "";
        public string TimeText = "";
        public string Fish = "";
        public uint? FishId;
        public int Weight;
        public string Bait = "";
        public float? DepthCm;
        public double? X, Y, Z;
        public uint? LocationId, SpawnLocId;
        public double? AirTemp, WindMs, Humidity, WindKmh;
        public uint? RodId, ReelId, LineId, HookId, FloatId;
        public string Status = "";
        public long? CastId;
        public int? CastRod;
        public string CastOutcome = "";
        public bool Verified;
        public string IconPath = "";
        public uint? BaitId;          // OVERLAY-03: id наживки (catches.bait_id) и её картинка
        public string BaitIconPath = "";
        public int? Windex;   // ADVISOR-UX-01: сырой класс улова (0–3), NULL — не известен
        public string WaterKey => "L" + (LocationId?.ToString() ?? "?") + "/S" + (SpawnLocId?.ToString() ?? "?");
    }

    public sealed class MapCast
    {
        public string Session = "";
        public long Id;
        public int Rod;
        public double X, Z;
        public string Outcome = "";
        public string WaterKey = "";
    }

    /// <summary>Чтение уловов/забросов только для чтения (Mode=ReadOnly) из catches.db сессий.</summary>
    public static class MapDataLoader
    {
        public const double CastMatchTolerance = 0.05;

        public static (List<MapCatch> catches, List<MapCast> casts, List<string> errors) Load(
            IEnumerable<(string session, string dbPath)> sources, PacketDeepParser? parser)
        {
            var catches = new List<MapCatch>();
            var casts = new List<MapCast>();
            var errors = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (session, dbPath) in sources)
            {
                if (!File.Exists(dbPath)) continue;
                try
                {
                    using var con = new SqliteConnection("Data Source=" + dbPath + ";Mode=ReadOnly;Pooling=False");
                    con.Open();
                    var dbCasts = new List<MapCast>();
                    if (TableExists(con, "casts"))
                    {
                        using var cmd = new SqliteCommand("SELECT * FROM casts", con);
                        using var r = cmd.ExecuteReader();
                        while (r.Read())
                        {
                            double? x = D(r, "cast_x"), z = D(r, "cast_z");
                            if (!x.HasValue || !z.HasValue) continue;
                            dbCasts.Add(new MapCast
                            {
                                Session = session, Id = (long)(L(r, "id") ?? 0), Rod = (int)(L(r, "rod_index") ?? -1),
                                X = x.Value, Z = z.Value, Outcome = S(r, "outcome")
                            });
                        }
                    }

                    // PRERELEASE-01: заброс для улова ищется по клеткам (то же условие и тот же «первый» заброс, без перебора всех)
                    var castGrid = new PointGrid<MapCast>(dbCasts, k => k.X, k => k.Z, CastMatchTolerance);
                    var dbCatches = new List<MapCatch>();
                    using (var cmd = new SqliteCommand("SELECT * FROM catches", con))
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var c = new MapCatch
                            {
                                RecordId = S(r, "record_id"),
                                Session = session,
                                SessionId = S(r, "session_id"),
                                TimeText = S(r, "timestamp"),
                                Fish = S(r, "fish_name"),
                                FishId = U(r, "fish_lang_id"),
                                Weight = (int)(L(r, "weight") ?? 0),
                                Bait = S(r, "bait_name"), BaitId = U(r, "bait_id"),
                                DepthCm = (float?)D(r, "depth_cm"),
                                X = D(r, "cast_x"), Y = D(r, "cast_y"), Z = D(r, "cast_z"),
                                LocationId = U(r, "location_id"), SpawnLocId = U(r, "spawn_locid"),
                                AirTemp = D(r, "air_temp"), WindMs = D(r, "wind_ms"), Humidity = D(r, "humidity"), WindKmh = D(r, "wind_kmh"),
                                RodId = U(r, "rod_id"), ReelId = U(r, "reel_id"), LineId = U(r, "line_id"), HookId = U(r, "hook_id"), FloatId = U(r, "float_id"),
                                Status = S(r, "status"),
                                Windex = L(r, "windex") is long wx && wx >= 0 && wx <= 3 ? (int)wx : null
                            };
                            if (c.Status == "skipped") continue;
                            string key = string.IsNullOrEmpty(c.RecordId) ? session + "#" + (L(r, "id") ?? 0) : c.RecordId;
                            if (!seen.Add(key)) continue; // same record copied into several session DBs

                            var fish = parser?.GetFish(c.FishId) ?? parser?.GetFishByName(c.Fish);
                            if (fish != null)
                            {
                                if (string.IsNullOrWhiteSpace(c.Fish)) c.Fish = fish.Name;
                                c.IconPath = fish.IconPath;
                                c.FishId ??= fish.Id;
                            }
                            if (string.IsNullOrWhiteSpace(c.Fish)) c.Fish = "Неизвестно";
                            if (c.BaitId is uint bid && bid > 0 && parser != null)
                            {
                                var bi = parser.GetBait(bid); var it = bi == null || bi.IconPath.Length == 0 ? parser.GetItem(bid) : null;
                                c.BaitIconPath = bi != null && bi.IconPath.Length > 0 ? bi.IconPath : it?.IconPath ?? "";
                                if (string.IsNullOrWhiteSpace(c.Bait)) c.Bait = bi?.Name ?? it?.Name ?? "";
                            }

                            if (c.X.HasValue && c.Z.HasValue)
                            {
                                var m = castGrid.First(c.X.Value, c.Z.Value, CastMatchTolerance);
                                if (m != null)
                                {
                                    c.Verified = true;
                                    c.CastId = m.Id;
                                    c.CastRod = m.Rod;
                                    c.CastOutcome = m.Outcome;
                                }
                            }
                            dbCatches.Add(c);
                        }
                    }
                    // A cast belongs to a waterbody only through a verified catch of the same DB (casts have no location column).
                    string? dbKey = dbCatches.Where(k => k.Verified).Select(k => k.WaterKey).Distinct().Count() == 1
                        ? dbCatches.First(k => k.Verified).WaterKey : null;
                    foreach (var k in dbCasts) k.WaterKey = dbKey ?? "";
                    catches.AddRange(dbCatches);
                    casts.AddRange(dbCasts.Where(k => k.WaterKey != ""));
                }
                catch (Exception ex)
                {
                    errors.Add(session + ": " + ex.Message);
                }
            }
            return (catches, casts, errors);
        }

        private static bool TableExists(SqliteConnection con, string name)
        {
            using var cmd = new SqliteCommand("SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$n", con);
            cmd.Parameters.AddWithValue("$n", name);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }

        private static int Ord(SqliteDataReader r, string name)
        {
            for (int i = 0; i < r.FieldCount; i++)
                if (string.Equals(r.GetName(i), name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
        private static string S(SqliteDataReader r, string n) { int i = Ord(r, n); return i < 0 || r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? ""; }
        private static double? D(SqliteDataReader r, string n) { int i = Ord(r, n); return i < 0 || r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture); }
        private static long? L(SqliteDataReader r, string n) { int i = Ord(r, n); return i < 0 || r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture); }
        private static uint? U(SqliteDataReader r, string n) { var v = L(r, n); return v.HasValue && v.Value >= 0 && v.Value <= uint.MaxValue ? (uint)v.Value : null; }
    }

    // =====================================================================================
    /// <summary>Холст: игровые координаты X/Z, масштаб колесом, перетаскивание, группировка меток.</summary>
    public sealed class MapCanvas : Control
    {
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public List<MapCatch> Points { get; set; } = new();
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public List<MapCast> Casts { get; set; } = new();
        public bool ShowMarkers = true, ShowDensity = false, ShowCasts = false, ShowGrid = true, Cluster = true, ShowLabels = true;
        /// <summary>PRERELEASE-01: отдельные метки уловов класса ниже этого не рисуются (0 — все). Зоны и статистика не меняются.</summary>
        public int MarkerMinClass;
        // NEXT-ANDROID-02: зоны активности (мои уловы, агрегировано) и батиметрия (общий набор Data\assets\bathymetry)
        public bool ShowZones = false, ShowBathy = false;
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public List<ActivityZone> Zones { get; set; } = new();
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public double ZoneCell { get; set; } = 20;
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public BathyInfo? Bathy { get; set; }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Image? BathyImage { get; set; }
        public event Action<ActivityZone>? ZoneClicked;
        public event Action<double, double>? MapClicked;
        // BRIDGE-FB-01: точки других игроков (справочник fisher-hub), открытые из Советника — поверх всего, своим цветом
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public List<(double X, double Z, string G)> RefPoints { get; set; } = new();
        public string RefLabel = "";
        public bool RefVisible = false;
        /// <summary>SPOTS-02: пятна шанса по вашим забросам в этом времени суток игры (карта F12).</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public List<ChanceSpots.Spot> Spots { get; set; } = new();
        /// <summary>SPOTS-03: лучшая проверенная точка (снасть последнего заброса, это же время суток игры) или null.</summary>
        public ChanceSpots.Spot? BestSpot => BestAllowed ? ChanceSpots.Best(Spots) : null;
        /// <summary>SPOTS-04: false — выбрана рыба, которую на наживку лучшей точки здесь не ловили (точка не про неё).</summary>
        public bool BestAllowed = true;
        /// <summary>SPOTS-04: какая рыба ловилась на наживку лучшей точки на этом водоёме (для пояснения по клику).</summary>
        public string BestFish = "";
        public static readonly Color RefColor = Color.FromArgb(255, 79, 216);
        private string _cursorDepth = "";
        public bool ShowMap = true;                // NEXT-135: background map layer (if MapInfo/MapImage set)
        public double CenterX, CenterZ, ViewScale = 40; // pixels per game unit
        // NEXT-135: lower limit allows a 4096 m map to fit in ~80 px; upper limit unchanged.
        public const double MinViewScale = 0.02, MaxViewScale = 5000;

        /// <summary>NEXT-135: verified background map of the selected waterbody (null = neutral grid).</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public WaterbodyMapInfo? MapInfo { get; set; }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Image? MapImage { get; set; }
        /// <summary>NEXT-MAP-RESOLUTION-01: необязательная подложка повышенного разрешения той же рамки (MapInfo).
        /// Рисуется вместо MapImage только при увеличении, когда она не уменьшается сильнее чем в 2 раза;
        /// в обзоре остаётся MapImage (та же картинка, без потери скорости). Координаты от неё не зависят.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Image? MapImageHd { get; set; }
        public bool HasMap => MapInfo != null && MapImage != null;
        public double DensityCell = 0.5;           // game units
        public int ClusterPixels = 34;
        public event Action<List<MapCatch>>? MarkerClicked;

        private Point _down, _last;
        private bool _dragging;

        public MapCanvas()
        {
            _bgIdle.Tick += (s, e) => { if (!Interactive) { _bgIdle.Stop(); Invalidate(); } };
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.FromArgb(10, 17, 27);
            Cursor = Cursors.Cross;
        }

        public PointF WorldToScreen(double x, double z) =>
            new PointF((float)(Width / 2.0 + (x - CenterX) * ViewScale), (float)(Height / 2.0 - (z - CenterZ) * ViewScale));

        public (double x, double z) ScreenToWorld(Point p) =>
            (CenterX + (p.X - Width / 2.0) / ViewScale, CenterZ - (p.Y - Height / 2.0) / ViewScale);

        public void FitToData()
        {
            var xs = Points.Where(p => p.X.HasValue).Select(p => p.X!.Value).Concat(ShowCasts ? Casts.Select(c => c.X) : Enumerable.Empty<double>()).ToList();
            var zs = Points.Where(p => p.Z.HasValue).Select(p => p.Z!.Value).Concat(ShowCasts ? Casts.Select(c => c.Z) : Enumerable.Empty<double>()).ToList();
            if (xs.Count == 0)
            {
                if (HasMap) { FitToMap(); return; }
                CenterX = CenterZ = 0; ViewScale = 40; Invalidate(); return;
            }
            double minX = xs.Min(), maxX = xs.Max(), minZ = zs.Min(), maxZ = zs.Max();
            CenterX = (minX + maxX) / 2; CenterZ = (minZ + maxZ) / 2;
            double spanX = Math.Max(maxX - minX, 1.0), spanZ = Math.Max(maxZ - minZ, 1.0);
            double w = Math.Max(Width - 120, 100), h = Math.Max(Height - 140, 100);
            ViewScale = Math.Clamp(Math.Min(w / spanX, h / spanZ), MinViewScale, 2000);
            DensityCell = NiceStep(Math.Max(spanX, spanZ) / 10.0);
            Invalidate();
        }

        /// <summary>NEXT-135: вписать всю карту водоёма (без искажения пропорций).</summary>
        public void FitToMap()
        {
            var m = MapInfo;
            if (m == null) { FitToData(); return; }
            CenterX = (m.MinX + m.MaxX) / 2; CenterZ = (m.MinZ + m.MaxZ) / 2;
            double w = Math.Max(Width - 24, 50), h = Math.Max(Height - 24, 50);
            ViewScale = Math.Clamp(Math.Min(w / m.SpanX, h / m.SpanZ), MinViewScale, MaxViewScale);
            Invalidate();
        }

        /// <summary>NEXT-135: число точек с координатами вне границ текущей карты (0, если карты нет).</summary>
        public int PointsOutsideMap() =>
            MapInfo == null ? 0 : Points.Count(p => p.X.HasValue && p.Z.HasValue && !MapInfo.Contains(p.X.Value, p.Z.Value));

        public void ZoomAt(Point p, double factor)
        {
            var (wx, wz) = ScreenToWorld(p);
            ViewScale = Math.Clamp(ViewScale * factor, MinViewScale, MaxViewScale);
            CenterX = wx - (p.X - Width / 2.0) / ViewScale;
            CenterZ = wz + (p.Y - Height / 2.0) / ViewScale;
            Invalidate();
        }

        public void PanBy(int dx, int dy)
        {
            CenterX -= dx / ViewScale;
            CenterZ += dy / ViewScale;
            Invalidate();
        }

        /// <summary>Группы меток в экранных координатах (одиночная метка = группа из 1).</summary>
        public List<(PointF pos, List<MapCatch> items)> Groups()
        {
            var result = new List<(PointF, List<MapCatch>)>();
            // PRERELEASE-01: когда уловов много, отдельные метки обычных/хороших скрываются (места-круги считают все)
            var pts = Points.Where(p => p.X.HasValue && p.Z.HasValue && (MarkerMinClass <= 0 || p.Windex == null || p.Windex >= MarkerMinClass)).ToList();
            if (!Cluster)
            {
                foreach (var p in pts) result.Add((WorldToScreen(p.X!.Value, p.Z!.Value), new List<MapCatch> { p }));
                return result;
            }
            // Greedy distance clustering in screen space: a marker joins the first group whose centre
            // is closer than ClusterPixels; deterministic order (by X, then Z).
            var groups = new List<(float sx, float sy, List<MapCatch> items)>();
            foreach (var p in pts.OrderBy(p => p.X).ThenBy(p => p.Z))
            {
                var s = WorldToScreen(p.X!.Value, p.Z!.Value);
                int gi = groups.FindIndex(g =>
                {
                    float cx = g.sx / g.items.Count, cy = g.sy / g.items.Count;
                    return (cx - s.X) * (cx - s.X) + (cy - s.Y) * (cy - s.Y) < ClusterPixels * ClusterPixels;
                });
                if (gi < 0) groups.Add((s.X, s.Y, new List<MapCatch> { p }));
                else { var g = groups[gi]; g.items.Add(p); groups[gi] = (g.sx + s.X, g.sy + s.Y, g.items); }
            }
            foreach (var g in groups) result.Add((new PointF(g.sx / g.items.Count, g.sy / g.items.Count), g.items));
            return result;
        }

        public List<MapCatch>? HitTest(Point p)
        {
            if (!ShowMarkers) return null;
            List<MapCatch>? best = null; double bestD = double.MaxValue;
            foreach (var (pos, items) in Groups())
            {
                double r = items.Count > 1 ? 18 : 14;
                double d = Math.Sqrt((pos.X - p.X) * (pos.X - p.X) + (pos.Y - p.Y) * (pos.Y - p.Y));
                if (d <= r && d < bestD) { bestD = d; best = items; }
            }
            return best;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            _anim?.Stop(); _anim?.Dispose(); _anim = null;   // ZONES-UX-01: пользователь перехватывает управление
            _down = _last = e.Location; _dragging = false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (ShowBathy && Bathy != null && Bathy.Implemented)
            {
                var (wx, wz) = ScreenToWorld(e.Location);
                var d = Bathymetry.DepthAt(Bathy.LocationId, wx, wz);
                string t = d.HasValue ? "Глубина под курсором: " + d.Value.ToString("0.0", CultureInfo.InvariantCulture) + " м" : "";
                if (t != _cursorDepth) { _cursorDepth = t; Invalidate(new Rectangle(0, Height - 64, 420, 64)); }
            }
            if (e.Button == MouseButtons.None)   // ZONES-UX-01: рука над меткой зоны — её можно нажать
            {
                var want = HitZoneCluster(e.Location) != null || HitCell(e.Location) != null ? Cursors.Hand : Cursors.Cross;
                if (Cursor != want) Cursor = want;
            }
            if (e.Button == MouseButtons.Left)
            {
                if (Math.Abs(e.X - _down.X) + Math.Abs(e.Y - _down.Y) > 3) _dragging = true;
                if (_dragging) { PanBy(e.X - _last.X, e.Y - _last.Y); Cursor = Cursors.SizeAll; }
                _last = e.Location;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Cursor = Cursors.Cross;
            if (e.Button == MouseButtons.Left && !_dragging)
            {
                var hit = HitTest(e.Location);
                if (hit != null) MarkerClicked?.Invoke(hit);
                else
                {
                    var zc = HitZoneCluster(e.Location);   // ZONES-UX-01: группа → приблизить, зона (метка или ячейка) → подробности
                    var cell = zc == null ? HitCell(e.Location) : null;
                    if (zc != null && zc.IsGroup) { ZoomToCluster(zc); ZoneGroupClicked?.Invoke(zc); }
                    else if (zc != null || cell != null) { var z1 = zc?.Top ?? cell!; SelectedZone = z1; Invalidate(); ZoneClicked?.Invoke(z1); }
                    else
                    {
                        if (SelectedZone != null) { SelectedZone = null; Invalidate(); }
                        var (wx, wz) = ScreenToWorld(e.Location); MapClicked?.Invoke(wx, wz);
                    }
                }
            }
            bool was = _dragging;
            _dragging = false;
            if (was) Invalidate();   // GAMEMAP-02: после перетаскивания — точная перерисовка подложки
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _anim?.Stop(); _anim?.Dispose(); _anim = null;
            _lastWheel = DateTime.UtcNow;
            ZoomAt(e.Location, e.Delta > 0 ? 1.25 : 0.8);
        }

        public static double NiceStep(double raw)
        {
            if (raw <= 0 || double.IsNaN(raw)) return 1;
            double exp = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double f = raw / exp;
            double nice = f < 1.5 ? 1 : f < 3.5 ? 2 : f < 7.5 ? 5 : 10;
            return nice * exp;
        }

        public static Color SpeciesColor(uint? id)
        {
            Color[] pal = { Color.FromArgb(41, 182, 230), Color.FromArgb(0, 196, 176), Color.FromArgb(242, 192, 87), Color.FromArgb(108, 204, 140),
                            Color.FromArgb(222, 104, 88), Color.FromArgb(170, 140, 240), Color.FromArgb(240, 140, 190), Color.FromArgb(150, 200, 60) };
            return pal[(int)((id ?? 0) % (uint)pal.Length)];
        }

        // GAMEMAP-02: плавность — подложка, глубины и сетка рисуются один раз в картинку под текущий вид; пока карту тянут,
        // крутят колёсико или идёт анимация, эта картинка только сдвигается/масштабируется (быстро), а точная перерисовка — когда движение закончилось.
        private Bitmap? _bg; private double _bgCx, _bgCz, _bgScale; private Size _bgSize; private string _bgKey = "";
        private DateTime _lastWheel;
        private readonly System.Windows.Forms.Timer _bgIdle = new() { Interval = 160 };
        public int BackgroundRenders { get; private set; }   // для тестов
        private bool Interactive => _dragging || _anim != null || (DateTime.UtcNow - _lastWheel).TotalMilliseconds < 160;
        private string BgKey() => $"{ShowMap}|{ShowBathy}|{ShowGrid}|{MapImage?.GetHashCode()}|{MapImageHd?.GetHashCode()}|{BathyImage?.GetHashCode()}|{MapInfo?.LocationId}|{Bathy?.LocationId}";
        public void InvalidateBackground() { _bgKey = ""; Invalidate(); }

        private void PaintBackground(Graphics g)
        {
            string key = BgKey();
            bool sameView = _bg != null && _bgSize == Size && _bgKey == key && _bgCx == CenterX && _bgCz == CenterZ && _bgScale == ViewScale;
            if (sameView) { g.DrawImageUnscaled(_bg!, 0, 0); return; }
            if (_bg != null && _bgSize == Size && _bgKey == key && Interactive)
            {
                // старая картинка: левый-верхний угол и размер в координатах текущего вида
                double k = ViewScale / _bgScale;
                double wl = _bgCx - Width / 2.0 / _bgScale, wt = _bgCz + Height / 2.0 / _bgScale;
                var tl = WorldToScreen(wl, wt);
                var im = g.InterpolationMode; g.InterpolationMode = InterpolationMode.Low;
                g.DrawImage(_bg!, new RectangleF(tl.X, tl.Y, (float)(Width * k), (float)(Height * k)));
                g.InterpolationMode = im;
                _bgIdle.Stop(); _bgIdle.Start();
                return;
            }
            if (Width <= 0 || Height <= 0) return;
            _bg?.Dispose();
            _bg = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var bg = Graphics.FromImage(_bg))
            {
                bg.SmoothingMode = SmoothingMode.AntiAlias; bg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                bg.Clear(BackColor);
                if (ShowMap) DrawMap(bg);
                if (ShowBathy) DrawBathy(bg);
                if (ShowGrid) DrawGrid(bg);
            }
            _bgCx = CenterX; _bgCz = CenterZ; _bgScale = ViewScale; _bgSize = Size; _bgKey = key; BackgroundRenders++;
            g.DrawImageUnscaled(_bg, 0, 0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(BackColor);

            PaintBackground(g);
            if (ShowDensity) DrawDensity(g);
            if (ShowZones) DrawZones(g);

            if (ShowCasts)
            {
                using var pen = new Pen(Color.FromArgb(150, 135, 155, 178), 1.2f);
                foreach (var c in Casts)
                {
                    var s = WorldToScreen(c.X, c.Z);
                    g.DrawEllipse(pen, s.X - 4, s.Y - 4, 8, 8);
                }
            }

            if (Spots.Count > 0) DrawSpots(g);
            if (ShowMarkers) DrawMarkers(g);
            if (RefVisible && RefPoints.Count > 0) DrawRefPoints(g);
            DrawScaleBar(g);
            if (ShowBathy) DrawBathyLegend(g);

            if (!Points.Any(p => p.X.HasValue))
            {
                UiText.DrawText(g, "Нет уловов с подтверждёнными координатами для выбранных фильтров",
                    Theme.Ui, ClientRectangle, Theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
        }

        /// <summary>
        /// NEXT-135: подложка карты. Прямоугольник карты вычисляется тем же преобразованием, что и метки
        /// (WorldToScreen: X вправо, Z вверх), с единым ViewScale — пропорции сохраняются.
        /// Рисуется только видимая часть (src/dest), чтобы при большом увеличении не создавать огромные прямоугольники.
        /// </summary>
        private void DrawMap(Graphics g)
        {
            var m = MapInfo; var img = MapImage;
            if (m == null || img == null) return;
            var hd = MapImageHd;   // NEXT-MAP-RESOLUTION-01: HD only when zoomed in (HD image px per screen px <= 2)
            if (hd != null && hd.Width / (m.SpanX * ViewScale) <= 2.0) img = hd;
            MapRenderer.DrawBackground(g, m, img, CenterX, CenterZ, ViewScale, Width, Height);   // overlay: same renderer
        }

        /// <summary>SPOTS-02: круги пятен шанса (как на мини-карте): цвет — хорошо/средне/слабо, цифра — шанс, чёрная на белой плашке.</summary>
        private void DrawSpots(Graphics g)
        {
            var placed = new List<Rectangle>();
            foreach (var sp in Spots.Where(s => s.Ticks >= ChanceSpots.MinTicks).OrderByDescending(s => s.Median))
            {
                var s = WorldToScreen(sp.X, sp.Z); var e = WorldToScreen(sp.X + ChanceSpots.MergeM, sp.Z);
                if (s.X < -40 || s.Y < -40 || s.X > Width + 40 || s.Y > Height + 40) continue;
                float rad = Math.Max(11, Math.Abs(e.X - s.X));
                Color col = sp.Level == 2 ? Theme.AccentGreen : sp.Level == 1 ? Theme.Warning : Color.FromArgb(150, 160, 175);   // SPOTS-05: относительно своей снасти
                using (var b = new SolidBrush(Color.FromArgb(90, col))) g.FillEllipse(b, s.X - rad, s.Y - rad, 2 * rad, 2 * rad);
                using (var pn = new Pen(Color.FromArgb(230, col), 2f)) g.DrawEllipse(pn, s.X - rad, s.Y - rad, 2 * rad, 2 * rad);
                OverlayForm.SpotLabel(g, sp.Median.ToString(sp.Median < 10 ? "0.0" : "0", CultureInfo.GetCultureInfo("ru-RU")), s, placed);
                // SPOTS-05: точка с большим преимуществом (≥2× других точек этой снасти) — зелёное кольцо и какая рыба здесь брала
                if (sp.Strong && !ReferenceEquals(sp, BestSpot))
                {
                    using (var pn = new Pen(Theme.AccentGreen, 3f)) g.DrawEllipse(pn, s.X - rad - 3, s.Y - rad - 3, 2 * rad + 6, 2 * rad + 6);
                    if (sp.Note.Length > 0) { using var nf = new Font("Segoe UI Semibold", 9f); UiText.DrawText(g, sp.Note, nf, new Point((int)(s.X + rad + 5), (int)(s.Y - 8)), Color.Black, TextFormatFlags.NoPadding); }
                }
            }
            // SPOTS-03: лучшая проверенная точка — толстое чёрное кольцо и подпись «лучший клёв» сбоку (не поверх цифр)
            if (BestSpot is { } best)
            {
                var c = WorldToScreen(best.X, best.Z); var e = WorldToScreen(best.X + ChanceSpots.MergeM, best.Z);
                float rad = Math.Max(11, Math.Abs(e.X - c.X)) + 5;
                using (var pn = new Pen(Color.Black, 3f)) g.DrawEllipse(pn, c.X - rad, c.Y - rad, 2 * rad, 2 * rad);
                using var f = new Font("Segoe UI Semibold", 10f);
                string gc = MapGridCoordinates.ToGrid(MapInfo?.LocationId, best.X, best.Z) is { } gg ? "  " + MapGridCoordinates.FormatGrid(gg) : "";
                UiText.DrawText(g, "★ лучше всего на «" + best.Bait + "»" + (best.Note.Length > 0 ? " — " + best.Note : "") + gc, f, new Point((int)(c.X + rad + 4), (int)(c.Y - 9)), Color.Black, TextFormatFlags.NoPadding);
            }
        }

        /// <summary>BRIDGE-FB-01: ромбы точек других игроков (центр клетки карты игры «NNN:NNN») и подпись слоя.</summary>
        private void DrawRefPoints(Graphics g)
        {
            const float r = 7f;
            using var white = new SolidBrush(Color.White);
            using var fill = new SolidBrush(RefColor);
            using var shadow = new SolidBrush(Color.FromArgb(130, 0, 0, 0));
            static PointF[] Dia(PointF p, float rr) => new[] { new PointF(p.X, p.Y - rr), new PointF(p.X + rr, p.Y), new PointF(p.X, p.Y + rr), new PointF(p.X - rr, p.Y) };
            foreach (var (x, z, _) in RefPoints)
            {
                var p = WorldToScreen(x, z);
                if (p.X < -20 || p.Y < -20 || p.X > Width + 20 || p.Y > Height + 20) continue;
                g.FillPolygon(shadow, Dia(new PointF(p.X, p.Y + 1.5f), r + 3));
                g.FillPolygon(white, Dia(p, r + 1.5f));
                g.FillPolygon(fill, Dia(p, r));
            }
            if (RefLabel.Length > 0)
            {
                var sz = UiText.MeasureText(RefLabel, Theme.UiBold);
                var rc = new Rectangle(10, 42, sz.Width + 30, sz.Height + 8);   // ниже масштабной линейки
                using (var bg = new SolidBrush(Color.FromArgb(210, 13, 20, 31))) g.FillRectangle(bg, rc);
                g.FillPolygon(fill, Dia(new PointF(rc.X + 12, rc.Y + rc.Height / 2f), 6));
                UiText.DrawText(g, RefLabel, Theme.UiBold, new Rectangle(rc.X + 22, rc.Y, rc.Width - 22, rc.Height), Theme.Text, TextFormatFlags.VerticalCenter);
            }
        }

        /// <summary>NEXT-ANDROID-02: слой батиметрии — та же рамка и то же преобразование, что подложка карты.</summary>
        private void DrawBathy(Graphics g)
        {
            var m = MapInfo; var img = BathyImage; var b = Bathy;
            if (m == null || img == null || b == null || !b.Implemented) return;
            MapRenderer.DrawBackground(g, m, img, CenterX, CenterZ, ViewScale, Width, Height);
            using var f = new Font("Segoe UI Semibold", 9f);
            var placed = new List<Rectangle>();
            foreach (var l in b.Labels.OrderBy(q => q.Kind == "max" ? 0 : 1))
            {
                var p = WorldToScreen(l.X, l.Z);
                if (p.X < -40 || p.Y < -20 || p.X > Width + 40 || p.Y > Height + 20) continue;
                string t = l.Kind == "max" ? "макс " + l.DepthM.ToString("0.#", CultureInfo.InvariantCulture) + " м" : l.DepthM.ToString("0.#", CultureInfo.InvariantCulture) + " м";
                var sz = UiText.MeasureText(t, f);
                var r = new Rectangle((int)p.X - sz.Width / 2 - 3, (int)p.Y - sz.Height / 2, sz.Width + 6, sz.Height);
                if (placed.Any(q => q.IntersectsWith(Rectangle.Inflate(r, 4, 2)))) continue;   // подписи не налезают друг на друга
                placed.Add(r);
                using (var bg = new SolidBrush(Color.FromArgb(170, 10, 25, 45))) g.FillRectangle(bg, r);
                UiText.DrawText(g, t, f, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        private void DrawBathyLegend(Graphics g)
        {
            var b = Bathy;
            string head = b == null ? "Батиметрия: нет данных для водоёма" : !b.Implemented ? "Батиметрия: " + Bathymetry.StatusText(b.Status) : "Глубина, м (рельеф дна игры)";
            using var f = new Font("Segoe UI", 8.5f);
            int rows = b != null && b.Implemented ? b.Bands.Count : 0;
            int w = 230, h = 22 + rows * 16 + 4, x = Width - w - 10, y = 10;
            using (var bg = new SolidBrush(Color.FromArgb(200, 13, 20, 31))) g.FillRectangle(bg, x, y, w, h);
            UiText.DrawText(g, head, f, new Rectangle(x + 6, y + 3, w - 8, 18), Theme.Text, TextFormatFlags.EndEllipsis);
            for (int i = 0; i < rows; i++)
            {
                var band = b!.Bands[i];
                using (var water = new SolidBrush(Color.FromArgb(157, 213, 255))) g.FillRectangle(water, x + 8, y + 24 + i * 16, 22, 12);   // как вода на карте
                using (var br = new SolidBrush(Color.FromArgb((int)(band.Alpha * 255), band.Rgb[0], band.Rgb[1], band.Rgb[2])))
                    g.FillRectangle(br, x + 8, y + 24 + i * 16, 22, 12);
                using (var pen = new Pen(Color.FromArgb(90, 225, 233, 242))) g.DrawRectangle(pen, x + 8, y + 24 + i * 16, 22, 12);
                UiText.DrawText(g, band.FromM.ToString("0.#", CultureInfo.InvariantCulture) + "–" + band.ToM.ToString("0.#", CultureInfo.InvariantCulture) + " м",
                    f, new Point(x + 36, y + 21 + i * 16), Theme.Text);
            }
            if (_cursorDepth.Length > 0)
            {
                var sz = UiText.MeasureText(_cursorDepth, Theme.UiBold);
                var r = new Rectangle(10, Height - 40, sz.Width + 12, sz.Height + 6);
                using (var bg = new SolidBrush(Color.FromArgb(210, 13, 20, 31))) g.FillRectangle(bg, r);
                UiText.DrawText(g, _cursorDepth, Theme.UiBold, r, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        // ZONES-UX-01: компактные метки зон активности.
        //  * Метки не перекрываются: зоны, которые при текущем масштабе слились бы, показываются одной меткой-группой
        //    с суммой уловов (ZoneClustering, тот же алгоритм в Android); при приближении группа распадается.
        //  * Размер метки 8…14 px (число уловов), цвет — от бирюзового (мало) через золотой к коралловому (много).
        //  * При крупном масштабе дополнительно рисуются сами ячейки зоны.
        //  * Щелчок по группе — плавное приближение к ней; по зоне — подробности справа.
        public const float ZoneMinR = 8f, ZoneMaxR = 14f, ZoneGap = 4f, ZoneCellMinPx = 18f;
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public ActivityZone? SelectedZone { get; set; }
        public event Action<ZoneCluster>? ZoneGroupClicked;
        /// <summary>false — переходы без анимации (тесты).</summary>
        public bool Animate = true;
        private static readonly Font ZoneFont = new("Segoe UI", 7.75f, FontStyle.Bold);
        private List<ZoneCluster> _zc = new();
        private List<ActivityZone>? _zcZones;
        private double _zcScale = double.NaN;
        private System.Windows.Forms.Timer? _anim;

        /// <summary>Метки зон при текущем масштабе (кэш по набору зон и масштабу; сдвиг карты группировку не меняет).</summary>
        public List<ZoneCluster> ZoneClusters()
        {
            if (!ReferenceEquals(_zcZones, Zones) || _zcScale != ViewScale)
            {
                _zc = ZoneClustering.Build(Zones, (x, z) => { var p = WorldToScreen(x, z); return (p.X, p.Y); }, 2 * ZoneMaxR + ZoneGap, ZoneAnchor);
                _zcZones = Zones; _zcScale = ViewScale;
            }
            return _zc;
        }

        /// <summary>Якорь метки зоны: центр её точек; когда ячейка крупнее метки — прижат внутрь ячейки с отступом
        /// (радиус + полпромежутка), поэтому метки соседних ячеек не сливаются и при максимальном приближении.</summary>
        public (double X, double Z) ZoneAnchor(ActivityZone z)
        {
            double m = (ZoneMaxR + ZoneGap / 2) / ViewScale;
            if (z.MaxX - z.MinX < 2 * m || z.MaxZ - z.MinZ < 2 * m) return (z.CenterX, z.CenterZ);
            return (Math.Clamp(z.CenterX, z.MinX + m, z.MaxX - m), Math.Clamp(z.CenterZ, z.MinZ + m, z.MaxZ - m));
        }

        private int ZoneMaxCount() => Zones.Count == 0 ? 1 : Math.Max(1, Zones.Max(q => q.Count));

        /// <summary>Радиус метки: 8…14 px по числу уловов (относительно самой «богатой» зоны), но не меньше, чем нужно для числа.</summary>
        public float BadgeRadius(int count)
        {
            int max = ZoneMaxCount();
            float r = ZoneMinR + (ZoneMaxR - ZoneMinR) * (float)Math.Sqrt(Math.Min(1.0, count / (double)max));
            var tsz = UiText.MeasureText(count.ToString(CultureInfo.InvariantCulture), ZoneFont);
            return Math.Max(r, tsz.Width / 2f + 2f);
        }

        /// <summary>0…1 по логарифму числа уловов: 1 улов = 0, самая «богатая» зона (и группы больше неё) = 1.</summary>
        public static double HeatT(int count, int max) => max <= 1 ? 1 : Math.Clamp(Math.Log(Math.Max(1, count)) / Math.Log(max), 0, 1);

        public static Color HeatColor(double t) => t < 0.5
            ? Blend(Color.FromArgb(0, 168, 160), Color.FromArgb(242, 192, 87), t * 2)
            : Blend(Color.FromArgb(242, 192, 87), Color.FromArgb(232, 98, 72), (t - 0.5) * 2);

        /// <summary>ZONES-02: квадрат, описанный вокруг круга зоны (центр точек ± радиус).</summary>
        private RectangleF CellRect(ActivityZone z)
        {
            double rad = z.Radius > 0 ? z.Radius : ZoneCell / 2;
            var a = WorldToScreen(z.CenterX - rad, z.CenterZ + rad); var b = WorldToScreen(z.CenterX + rad, z.CenterZ - rad);
            return RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        private void DrawZones(Graphics g)
        {
            if (Zones.Count == 0) return;
            int max = ZoneMaxCount();
            float cellPx = (float)(ZoneCell * ViewScale);
            if (cellPx >= ZoneCellMinPx)
            {
                var sm = g.SmoothingMode; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                foreach (var z in Zones)   // ZONES-02: зона — круг радиусом z.Radius вокруг центра точек
                {
                    var r = CellRect(z);
                    if (r.Right < 0 || r.Bottom < 0 || r.Left > Width || r.Top > Height) continue;
                    var col = HeatColor(HeatT(z.Count, max));
                    using (var br = new SolidBrush(Color.FromArgb(70, col))) g.FillEllipse(br, r);
                    using (var pen = new Pen(Color.FromArgb(ReferenceEquals(z, SelectedZone) ? 255 : 170, col), ReferenceEquals(z, SelectedZone) ? 2f : 1f)) g.DrawEllipse(pen, r.X, r.Y, r.Width, r.Height);
                }
                g.SmoothingMode = sm;
            }
            foreach (var c in ZoneClusters().OrderBy(q => q.Count))
            {
                var p = WorldToScreen(c.DrawX, c.DrawZ);
                float r = BadgeRadius(c.Count);
                if (p.X < -r - 8 || p.Y < -r - 8 || p.X > Width + r + 8 || p.Y > Height + r + 8) continue;
                double t = HeatT(c.Count, max);
                var col = HeatColor(t);
                using (var sh = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) g.FillEllipse(sh, p.X - r - 1.5f, p.Y - r, 2 * r + 3, 2 * r + 3);
                if (c.IsGroup)
                    using (var ring = new Pen(Color.FromArgb(215, 255, 255, 255), 1.3f)) g.DrawEllipse(ring, p.X - r - 3.5f, p.Y - r - 3.5f, 2 * r + 7, 2 * r + 7);
                using (var br = new SolidBrush(Color.FromArgb(240, col))) g.FillEllipse(br, p.X - r, p.Y - r, 2 * r, 2 * r);
                using (var pen = new Pen(Color.FromArgb(240, 255, 255, 255), 1.5f)) g.DrawEllipse(pen, p.X - r, p.Y - r, 2 * r, 2 * r);
                bool light = col.R * 0.299 + col.G * 0.587 + col.B * 0.114 > 150;
                UiText.DrawText(g, c.Count.ToString(CultureInfo.InvariantCulture), ZoneFont,
                    new Rectangle((int)Math.Floor(p.X - r - 2), (int)Math.Floor(p.Y - r), (int)Math.Ceiling(2 * r + 4), (int)Math.Ceiling(2 * r)),
                    light ? Color.FromArgb(20, 28, 40) : Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (SelectedZone != null && c.Zones.Contains(SelectedZone))
                    using (var sel = new Pen(Color.FromArgb(255, 255, 236, 140), 2.5f)) g.DrawEllipse(sel, p.X - r - 6, p.Y - r - 6, 2 * r + 12, 2 * r + 12);
            }
            DrawZoneLegend(g);
        }

        private void DrawZoneLegend(Graphics g)
        {
            using var f = new Font("Segoe UI", 8.25f);
            const string a = "мало", b = "много уловов", c2 = "группа — щёлкните, чтобы приблизить";
            int w = Math.Max(UiText.MeasureText(c2, f).Width + 40, UiText.MeasureText(a + " → " + b, f).Width + 62), h = 46, x = Width - w - 10, y = Height - h - 10;
            using (var bg = new SolidBrush(Color.FromArgb(205, 13, 20, 31))) g.FillRectangle(bg, x, y, w, h);
            float cx = x + 14;
            foreach (var t in new[] { 0.0, 0.5, 1.0 })
            {
                using var br = new SolidBrush(HeatColor(t)); g.FillEllipse(br, cx - 5, y + 9, 10, 10); cx += 14;
            }
            UiText.DrawText(g, a + " → " + b, f, new Point(x + 52, y + 5), Theme.Text);
            using (var br = new SolidBrush(HeatColor(0.5))) g.FillEllipse(br, x + 9, y + 27, 10, 10);
            using (var ring = new Pen(Color.White, 1.2f)) g.DrawEllipse(ring, x + 6, y + 24, 16, 16);
            UiText.DrawText(g, c2, f, new Point(x + 28, y + 24), Theme.Text);
        }

        /// <summary>Метка (зона или группа) под курсором.</summary>
        public ZoneCluster? HitZoneCluster(Point p)
        {
            if (!ShowZones || Zones.Count == 0) return null;
            ZoneCluster? best = null; double bd = double.MaxValue;
            var cl = ZoneClusters();
            foreach (var c in cl)
            {
                var s = WorldToScreen(c.DrawX, c.DrawZ);
                double d = Math.Sqrt((s.X - p.X) * (s.X - p.X) + (s.Y - p.Y) * (s.Y - p.Y));
                if (d <= BadgeRadius(c.Count) + 4 && d < bd) { bd = d; best = c; }
            }
            return best;
        }

        /// <summary>Ячейка зоны под курсором (только когда ячейки нарисованы).</summary>
        public ActivityZone? HitCell(Point p)
        {
            if (!ShowZones || ZoneCell * ViewScale < ZoneCellMinPx) return null;
            return Zones.FirstOrDefault(z =>
            {
                var r = CellRect(z); float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, rr = r.Width / 2;
                return (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy) <= rr * rr;
            });
        }

        /// <summary>Совместимость (NEXT-ANDROID-02): зона под курсором; для группы — её крупнейшая зона.</summary>
        public ActivityZone? HitZone(Point p) => HitZoneCluster(p)?.Top ?? HitCell(p);

        /// <summary>Плавный переход к центру/масштабу (или сразу, если Animate = false или окно ещё не показано).</summary>
        public void AnimateTo(double cx, double cz, double scale)
        {
            scale = Math.Clamp(scale, MinViewScale, MaxViewScale);
            _anim?.Stop(); _anim?.Dispose(); _anim = null;
            if (!Animate || !IsHandleCreated || !Visible) { CenterX = cx; CenterZ = cz; ViewScale = scale; Invalidate(); return; }
            double x0 = CenterX, z0 = CenterZ, s0 = ViewScale;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var tm = new System.Windows.Forms.Timer { Interval = 15 };
            tm.Tick += (s, e) =>
            {
                double t = Math.Min(1, sw.ElapsedMilliseconds / 240.0), k = 1 - Math.Pow(1 - t, 3);
                ViewScale = s0 * Math.Pow(scale / s0, k); CenterX = x0 + (cx - x0) * k; CenterZ = z0 + (cz - z0) * k;
                Invalidate();
                if (t >= 1) { tm.Stop(); tm.Dispose(); if (ReferenceEquals(_anim, tm)) _anim = null; }
            };
            _anim = tm; tm.Start();
        }

        /// <summary>Приблизить так, чтобы группа заняла часть окна и распалась на зоны (минимум ×2 к текущему масштабу).</summary>
        public void ZoomToCluster(ZoneCluster c)
        {
            double pad = ZoneCell;
            double sx = Math.Max(c.MaxX - c.MinX + 2 * pad, 4 * ZoneCell), sz = Math.Max(c.MaxZ - c.MinZ + 2 * pad, 4 * ZoneCell);
            double target = Math.Min(Math.Max(Width - 120, 80) / sx, Math.Max(Height - 120, 80) / sz);
            AnimateTo(c.CenterX, c.CenterZ, Math.Max(target, ViewScale * 2));
        }

        /// <summary>Показать зону: центр окна, ячейка не меньше ~70 px и зона — отдельная метка (не внутри группы).</summary>
        public void FocusZone(ActivityZone z)
        {
            SelectedZone = z;
            double target = Math.Max(ViewScale, 70.0 / Math.Max(ZoneCell, 1e-6));
            for (int i = 0; i < 20 && target < MaxViewScale; i++)   // приблизить, пока зона не станет отдельной меткой
            {
                double t = target;
                double m = (ZoneMaxR + ZoneGap / 2) / t;
                (double, double) anc(ActivityZone q) => q.MaxX - q.MinX < 2 * m || q.MaxZ - q.MinZ < 2 * m ? (q.CenterX, q.CenterZ)
                    : (Math.Clamp(q.CenterX, q.MinX + m, q.MaxX - m), Math.Clamp(q.CenterZ, q.MinZ + m, q.MaxZ - m));
                if (ZoneClustering.Build(Zones, (x, zz) => ((float)(x * t), (float)(-zz * t)), 2 * ZoneMaxR + ZoneGap, anc).Any(c => !c.IsGroup && ReferenceEquals(c.Top, z))) break;
                target *= 1.5;
            }
            AnimateTo(z.CenterX, z.CenterZ, target);
        }

        private void DrawGrid(Graphics g)
        {
            double step = NiceStep(90 / ViewScale);
            var (x0, z1) = ScreenToWorld(new Point(0, 0));
            var (x1, z0) = ScreenToWorld(new Point(Width, Height));
            using var pen = new Pen(Color.FromArgb(28, 44, 62), 1f);
            using var axisPen = new Pen(Color.FromArgb(48, 72, 98), 1f);
            for (double x = Math.Floor(x0 / step) * step; x <= x1; x += step)
            {
                float sx = WorldToScreen(x, 0).X;
                g.DrawLine(pen, sx, 0, sx, Height);
                UiText.DrawText(g, x.ToString("0.##", CultureInfo.InvariantCulture), Theme.Grid, new Point((int)sx + 3, Height - 20), Theme.TextMuted);
            }
            for (double z = Math.Floor(z0 / step) * step; z <= z1; z += step)
            {
                float sy = WorldToScreen(0, z).Y;
                g.DrawLine(pen, 0, sy, Width, sy);
                UiText.DrawText(g, z.ToString("0.##", CultureInfo.InvariantCulture), Theme.Grid, new Point(4, (int)sy - 18), Theme.TextMuted);
            }
            g.DrawRectangle(axisPen, 0, 0, Width - 1, Height - 1);
            UiText.DrawText(g, "X →   Z ↑   (координаты сцены, м)", Theme.Grid, new Point(Width - 260, Height - 40), Theme.TextMuted);
        }

        private void DrawDensity(Graphics g)
        {
            var pts = Points.Where(p => p.X.HasValue && p.Z.HasValue).ToList();
            if (pts.Count == 0) return;
            double cell = DensityCell <= 0 ? 0.5 : DensityCell;
            var bins = pts.GroupBy(p => ((long)Math.Floor(p.X!.Value / cell), (long)Math.Floor(p.Z!.Value / cell)))
                          .Select(gr => (gr.Key, n: gr.Count())).ToList();
            int max = bins.Max(b => b.n);
            foreach (var (key, n) in bins)
            {
                var a = WorldToScreen(key.Item1 * cell, (key.Item2 + 1) * cell);
                var b = WorldToScreen((key.Item1 + 1) * cell, key.Item2 * cell);
                double t = max == 1 ? 1 : (n - 1) / (double)(max - 1);
                var col = Blend(Color.FromArgb(0, 150, 160), Color.FromArgb(242, 192, 87), t);
                using var br = new SolidBrush(Color.FromArgb(70 + (int)(110 * t), col));
                g.FillRectangle(br, a.X, a.Y, b.X - a.X, b.Y - a.Y);
                if (b.X - a.X > 22)
                    UiText.DrawText(g, n.ToString(), Theme.UiBold, new Rectangle((int)a.X, (int)a.Y, (int)(b.X - a.X), (int)(b.Y - a.Y)),
                        Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        private static Color Blend(Color a, Color b, double t) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        private void DrawMarkers(Graphics g)
        {
            foreach (var (pos, items) in Groups())
            {
                if (items.Count > 1)
                {
                    float r = Math.Min(14 + items.Count, 24);
                    using var br = new SolidBrush(Color.FromArgb(220, 18, 92, 122));
                    using var pen = new Pen(Theme.AccentTeal, 2f);
                    g.FillEllipse(br, pos.X - r, pos.Y - r, 2 * r, 2 * r);
                    g.DrawEllipse(pen, pos.X - r, pos.Y - r, 2 * r, 2 * r);
                    UiText.DrawText(g, items.Count.ToString(), Theme.UiBold, new Rectangle((int)(pos.X - r), (int)(pos.Y - r), (int)(2 * r), (int)(2 * r)),
                        Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    continue;
                }
                var c = items[0];
                var col = SpeciesColor(c.FishId);
                using (var br = new SolidBrush(Color.FromArgb(235, 20, 32, 48)))
                    g.FillEllipse(br, pos.X - 15, pos.Y - 15, 30, 30);
                using (var pen = new Pen(c.Verified ? col : Color.Gray, 2.2f) { DashStyle = c.Verified ? DashStyle.Solid : DashStyle.Dash })
                    g.DrawEllipse(pen, pos.X - 15, pos.Y - 15, 30, 30);
                var icon = IconLoader.Load(c.IconPath);
                if (icon != null) g.DrawImage(icon, new RectangleF(pos.X - 17, pos.Y - 17, 34, 34)); // icons have transparent padding
                else using (var br = new SolidBrush(col)) g.FillEllipse(br, pos.X - 5, pos.Y - 5, 10, 10);
                if (ShowLabels)
                    UiText.DrawText(g, c.Weight + " г", Theme.Grid, new Point((int)pos.X + 15, (int)pos.Y - 8), Theme.Text);
            }
        }

        private void DrawScaleBar(Graphics g)
        {
            double unit = NiceStep(100 / ViewScale);
            float px = (float)(unit * ViewScale);
            int x = 16, y = 14;
            using var pen = new Pen(Theme.Text, 2f);
            g.DrawLine(pen, x, y + 8, x + px, y + 8);
            g.DrawLine(pen, x, y + 3, x, y + 13);
            g.DrawLine(pen, x + px, y + 3, x + px, y + 13);
            UiText.DrawText(g, unit.ToString("0.##", CultureInfo.InvariantCulture) + " м", Theme.Grid, new Point(x + (int)px + 6, y), Theme.Text);
            // HINTS-01: неброско под масштабом — цвет поводка, только если игра его называет для этого водоёма
            if (GameKnowledge.LeaderColor(MapInfo?.LocationId) is string lc)
                UiText.DrawText(g, "поводок: " + lc + " — чуть больше трофеев", Theme.Grid, new Point(x, y + 20), Theme.TextMuted);
        }
    }

    // =====================================================================================
    public sealed partial class WaterbodyMapForm : Form
    {
        private readonly PacketDeepParser _parser;
        private readonly string _currentDb;
        private readonly string _sessionDir;

        public MapCanvas Canvas { get; } = new MapCanvas { Dock = DockStyle.Fill };
        // NEXT-MAP-RESOLUTION-01: HD-подложка, которой владеет это окно (не общий кэш); освобождается при смене водоёма/закрытии
        private Bitmap? _hdImage;
        private string? _hdKey;
        private readonly ComboBox _water = new() { DropDownStyle = ComboBoxStyle.DropDownList, MaxDropDownItems = 30, DropDownWidth = 340 };
        private readonly ComboBox _species = new() { DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 26 };
        private readonly RadioButton _srcCurrent = new() { Text = "Текущая сессия", AutoSize = true };
        private readonly RadioButton _srcAll = new() { Text = "Все сессии (история)", AutoSize = true, Checked = true };
        private readonly CheckBox _chkMarkers = new() { Text = "Каждый улов точкой", Checked = false, AutoSize = true };   // NEXT-ANDROID-02: по умолчанию выкл.
        private readonly CheckBox _chkZones = new() { Text = "Места с уловами (круги)", Checked = true, AutoSize = true };  // NEXT-ANDROID-02
        private readonly CheckBox _chkBathy = new() { Text = "Глубины", AutoSize = true };                    // NEXT-ANDROID-02
        private readonly CheckBox _allDates = new() { Text = "Весь период", Checked = true, AutoSize = true };                 // NEXT-ANDROID-02
        private readonly DateTimePicker _from = new() { Format = DateTimePickerFormat.Short, Width = 125, Enabled = false };
        private readonly DateTimePicker _to = new() { Format = DateTimePickerFormat.Short, Width = 125, Enabled = false };
        private readonly CheckBox _chkCluster = new() { Text = "Группировать близкие метки", Checked = true, AutoSize = true };
        private readonly CheckBox _chkLabels = new() { Text = "Подписи веса", Checked = true, AutoSize = true };
        private readonly CheckBox _chkDensity = new() { Text = "Плотность записанных уловов", AutoSize = true };
        private readonly CheckBox _chkCasts = new() { Text = "Забросы (попытки)", AutoSize = true };
        private readonly CheckBox _chkGrid = new() { Text = "Координатная сетка", Checked = true, AutoSize = true };
        private readonly CheckBox _chkUnverified = new() { Text = "Неподтверждённые координаты", AutoSize = true };
        private readonly CheckBox _chkMap = new() { Text = "Подложка карты", Checked = true, AutoSize = true };   // NEXT-135
        private readonly Button _fitMapBtn = new() { Text = "Вся карта", Width = 128, Height = 34 };               // NEXT-135
        private Label _note = null!;                                                                                // NEXT-135: per-waterbody note
        private readonly Label _stats = new() { AutoSize = false, Height = 92, Dock = DockStyle.Top };
        private readonly PictureBox _detIcon = new() { Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(25, 37, 54) };
        private readonly Label _detTitle = new() { AutoSize = false, Height = 56, Dock = DockStyle.Top };
        private readonly TextBox _detText = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None };
        private readonly ListBox _clusterList = new() { Dock = DockStyle.Bottom, Height = 150, BorderStyle = BorderStyle.None };
        // ZONES-UX-01: «Лучшие места» — зоны по числу моих уловов; щелчок показывает зону на карте
        private readonly Panel _topPanel = new() { Dock = DockStyle.Bottom, Height = 300, Padding = new Padding(0, 8, 0, 0) };
        private readonly Label _topHdr = new() { Dock = DockStyle.Top, Height = 22, Text = "Лучшие места (мои уловы)" };
        private readonly ListBox _topList = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 34, IntegralHeight = false };
        private List<ActivityZone> _ranked = new();
        // ADVISOR-UX-01: карточка места — «что ловится» и «на что клевало» (тот же стиль, что в «Советнике»)
        private readonly FlowLayoutPanel _zoneCard = UiKit.Stack(new Padding(0, 4, 4, 4));
        private readonly Label _zoneWhere = new() { AutoSize = false, Height = 44, ForeColor = Theme.Text, Margin = new Padding(0, 2, 0, 4) };
        private readonly StatList _zoneFish = new() { EmptyText = "Нет уловов" };
        private readonly StatList _zoneBaits = new() { EmptyText = "Наживка не записана" };
        /// <summary>MAPCARD-01: подсказка «реже, но крупнее» под списком наживок.</summary>
        private readonly Label _zoneInsight = new() { AutoSize = false, Height = 44, Dock = DockStyle.Top, ForeColor = Theme.Warning, Font = UiKit.Small, Visible = false, Padding = new Padding(2, 0, 2, 6) };
        public Label ZoneInsight => _zoneInsight;   // для тестов
        private readonly Dictionary<ActivityZone, string> _zoneLine = new();
        /// <summary>GAMEMAP-02: кто пытался клевать здесь, но не засёкся (пустые поклёвки) и на что эту рыбу ловить.</summary>
        private readonly Label _zoneEmpty = new() { AutoSize = false, Height = 60, Dock = DockStyle.Top, ForeColor = Theme.Text, Font = UiKit.Small, Visible = false, Padding = new Padding(2, 2, 2, 6) };
        public Label ZoneEmpty => _zoneEmpty;   // для тестов
        public StatList ZoneFishList => _zoneFish;     // для тестов
        public StatList ZoneBaitList => _zoneBaits;    // для тестов
        public bool ZoneCardVisible => _zoneCard.Visible;
        public IReadOnlyList<ActivityZone> RankedZones => _ranked;          // для тестов
        public ListBox TopPlacesList => _topList;                             // для тестов

        private List<MapCatch> _all = new();
        private List<MapCast> _casts = new();
        private List<string> _errors = new();

        public int PlottedCount => Canvas.Points.Count(p => p.X.HasValue);
        public WaterbodyMapInfo? CurrentMap => Canvas.HasMap ? Canvas.MapInfo : null;   // NEXT-135
        public string NoteText => _noteFull;
        private string _noteFull = "";
        /// <summary>PRERELEASE-01: правило показа из настроек (только вид).</summary>
        private readonly DisplaySettings _display = LoadDisplay();
        private static DisplaySettings LoadDisplay() { try { var d = AppSettings.Load().Display ?? new DisplaySettings(); d.Normalize(); return d; } catch { return new DisplaySettings(); } }
        private readonly ToolTip _noteTip = new() { AutoPopDelay = 30000 };                                           // NEXT-135
        public IReadOnlyList<MapCatch> AllCatches => _all;
        public string DetailsText => _detTitle.Text + Environment.NewLine + _detText.Text;
        public bool BathyAvailable => _chkBathy.Enabled;                                                        // NEXT-ANDROID-02
        public void SetDateRange(DateTime? from, DateTime? to)
        {
            if (from == null && to == null) { _allDates.Checked = true; return; }
            _from.Value = from ?? _from.Value; _to.Value = to ?? _to.Value; _allDates.Checked = false; ApplyFilters(fit: false);
        }

        private sealed class SpeciesItem
        {
            public uint? FishId; public string Name = ""; public string IconPath = ""; public int Count;
            public override string ToString() => Name + (Count > 0 ? "  (" + Count + ")" : "");
        }
        private const string SeparatorKey = "--sep--";
        private bool _firstLoad = true;
        /// <summary>UIFIX-02: точки других игроков, запрошенные до первой загрузки списка водоёмов (окно только что открыто).</summary>
        private (uint loc, string fish, List<(double X, double Z, string G)> pts)? _pendingRef;
        private uint? _pendingWater;
        private int _lastWaterIdx;

        private sealed class WaterItem
        {
            public string Key = ""; public string Text = ""; public int Count;
            public uint? LocationId;   // NEXT-135: map selection key (TFBase_N), not WaterKey/spawn
            public override string ToString() => Text;
        }

        public WaterbodyMapForm(PacketDeepParser parser, string currentDbPath, string sessionDir, bool gameMode = false)
        {
            _parser = parser;
            _currentDb = currentDbPath;
            _sessionDir = sessionDir;

            Text = "Карта водоёма — FisherLogger";
            Shown += (s0, e0) => Theme.ApplyInputs(this);   // DESIGN-01: тёмные поля ввода
            Size = new Size(1320, 820);
            MinimumSize = new Size(1000, 620);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = Theme.Ui;
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;

            // ---------- left: filters / layers ----------
            var left = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 290, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                                             AutoScroll = true, Padding = new Padding(12, 10, 8, 10), BackColor = Theme.Surface };
            Label H(string t) => new Label { Text = t, AutoSize = true, ForeColor = Theme.TextMuted, Font = Theme.UiBold, Margin = new Padding(0, 14, 0, 4) };   // DESIGN-01: подписи групп спокойные
            _water.Width = _species.Width = 262;
            foreach (var cb in new[] { _water, _species }) { cb.BackColor = Theme.SurfaceAlt; cb.ForeColor = Theme.Text; cb.FlatStyle = FlatStyle.Flat; }
            left.Controls.Add(H("Водоём"));
            left.Controls.Add(_water);
            left.Controls.Add(H("Чьи данные"));
            left.Controls.Add(_srcAll);
            left.Controls.Add(_srcCurrent);
            left.Controls.Add(H("Вид рыбы"));
            left.Controls.Add(_species);
            left.Controls.Add(H("Период"));
            _allDates.ForeColor = Theme.Text;
            left.Controls.Add(_allDates);
            var dateRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
            dateRow.Controls.Add(_from); dateRow.Controls.Add(_to);
            left.Controls.Add(dateRow);
            left.Controls.Add(H("Что показать на карте"));
            foreach (var c in new Control[] { _chkZones, _chkBathy, _chkMarkers, _chkMap, _chkCluster, _chkLabels, _chkDensity, _chkCasts, _chkGrid, _chkUnverified })
            {
                c.ForeColor = Theme.Text;
                c.Margin = new Padding(2, 2, 0, 2);
                left.Controls.Add(c);
            }
            var fitBtn = new Button { Text = "Показать всё", Width = 128, Height = 34 };
            var reloadBtn = new Button { Text = "Обновить", Width = 128, Height = 34 };
            Theme.StyleButton(fitBtn, Theme.ButtonSecondary);
            Theme.StyleButton(reloadBtn, Theme.ButtonNeutral);
            Theme.StyleButton(_fitMapBtn, Theme.ButtonSecondary);
            var btnRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 6) };
            btnRow.Controls.Add(fitBtn); btnRow.Controls.Add(reloadBtn); btnRow.Controls.Add(_fitMapBtn);
            left.Controls.Add(btnRow);
            // DESIGN-01: под фильтрами — одна короткая строка; технические подробности карты — во всплывающей подсказке
            _note = new Label
            {
                AutoSize = false, Width = 262, Height = 64, ForeColor = Theme.TextMuted, Font = UiKit.Small,
                Text = NoMapNote
            };
            _noteFull = NoMapNote;
            left.Controls.Add(_note);

            // ---------- right: details ----------
            var right = new Panel { Dock = DockStyle.Right, Width = 360, Padding = new Padding(12), BackColor = Theme.Surface };
            var top = new Panel { Dock = DockStyle.Top, Height = 72 };
            _detIcon.Location = new Point(0, 4);
            _detTitle.Dock = DockStyle.None; _detTitle.Location = new Point(74, 4); _detTitle.Size = new Size(270, 64);
            _detTitle.Font = Theme.Heading; _detTitle.ForeColor = Theme.Text; _detTitle.Text = "Выберите место на карте";
            _detText.Text = "Нажмите на круг — увидите, что там ловилось, на что клевало и какая глубина.\r\n\r\nНажмите в любом месте воды — покажу глубину в этой точке.\r\nКолёсико мыши — масштаб, перетаскивание — сдвиг карты.";
            top.Controls.Add(_detIcon); top.Controls.Add(_detTitle);
            _detText.BackColor = Theme.Surface; _detText.ForeColor = Theme.Text; _detText.Font = new Font("Segoe UI", 9.5f);
            _clusterList.BackColor = Theme.SurfaceAlt; _clusterList.ForeColor = Theme.Text; _clusterList.Visible = false;
            right.Controls.Add(_detText);
            _zoneCard.Dock = DockStyle.Fill; _zoneCard.Visible = false; _zoneCard.BackColor = Theme.Surface;
            _zoneCard.Controls.AddRange(new Control[]
            {
                _zoneWhere,
                UiKit.Section("Что ловится", "мои уловы"), _zoneFish,
                UiKit.Section("На что клевало"), _zoneBaits, _zoneInsight, _zoneEmpty,
                UiKit.Note("Точка — место заброса. Числа — сколько уловов записано здесь, это не вероятность клёва.")
            });
            right.Controls.Add(_zoneCard);
            right.Controls.Add(_clusterList);
            _topHdr.ForeColor = Theme.Accent; _topHdr.Font = Theme.UiBold;
            _topList.BackColor = Theme.SurfaceAlt; _topList.ForeColor = Theme.Text;
            _topList.DrawItem += DrawTopItem;
            _topPanel.Controls.Add(_topList); _topPanel.Controls.Add(_topHdr);
            _topPanel.Visible = false;
            right.Controls.Add(_topPanel);
            right.Controls.Add(top);
            _stats.ForeColor = Theme.TextMuted; _stats.Font = new Font("Segoe UI", 8.75f);
            right.Controls.Add(_stats);

            Controls.Add(Canvas);
            Controls.Add(right);
            Controls.Add(left);

            // ---------- events ----------
            _species.DrawItem += DrawSpeciesItem;
            _water.SelectedIndexChanged += (s, e) =>
            {
                // FIX-05: разделитель не выбирается — перескакиваем в сторону движения
                if ((_water.SelectedItem as WaterItem)?.Key == SeparatorKey)
                {
                    int i = _water.SelectedIndex, to = i > _lastWaterIdx ? i + 1 : i - 1;
                    _water.SelectedIndex = Math.Clamp(to, 0, _water.Items.Count - 1) == i ? Math.Max(0, i - 1) : Math.Clamp(to, 0, _water.Items.Count - 1);
                    return;
                }
                _lastWaterIdx = _water.SelectedIndex;
                UpdateMapForSelection(); RebuildSpecies(); ApplyFilters(fit: !_keepView);
                GameRefresh();
            };
            _species.SelectedIndexChanged += (s, e) => { ApplyFilters(fit: false); GameRefresh(); };
            _srcAll.CheckedChanged += (s, e) => { if (_srcAll.Checked) LoadData(); };
            _srcCurrent.CheckedChanged += (s, e) => { if (_srcCurrent.Checked) LoadData(); };
            foreach (var c in new[] { _chkMap, _chkMarkers, _chkCluster, _chkLabels, _chkDensity, _chkCasts, _chkGrid, _chkZones, _chkBathy })
                c.CheckedChanged += (s, e) => ApplyLayers();
            _allDates.CheckedChanged += (s, e) => { _from.Enabled = _to.Enabled = !_allDates.Checked; ApplyFilters(fit: false); };
            _from.ValueChanged += (s, e) => { if (!_allDates.Checked) ApplyFilters(fit: false); };
            _to.ValueChanged += (s, e) => { if (!_allDates.Checked) ApplyFilters(fit: false); };
            Canvas.ZoneClicked += ShowZone;
            Canvas.ZoneGroupClicked += ShowZoneGroup;
            Canvas.MapClicked += (x, z) => { if (Canvas.SelectedZone == null) SyncTopSelection(null); };
            _topList.SelectedIndexChanged += (s, e) =>
            {
                if (_suppressTop || _topList.SelectedItem is not TopEntry te) return;
                Canvas.FocusZone(te.Zone); ShowZone(te.Zone);
            };
            Canvas.MapClicked += (x, z) => { if (GameMode) ShowGamePoint(x, z); else ShowPointDepth(x, z); };
            _fitMapBtn.Click += (s, e) => Canvas.FitToMap();
            _chkUnverified.CheckedChanged += (s, e) => { RebuildSpecies(); ApplyFilters(fit: false); };
            fitBtn.Click += (s, e) => Canvas.FitToData();
            reloadBtn.Click += (s, e) => LoadData();
            Canvas.MarkerClicked += ShowDetails;
            _clusterList.SelectedIndexChanged += (s, e) => { if (_clusterList.SelectedItem is ClusterEntry ce) ShowOne(ce.Catch); };

            Shown += (s, e) =>
            {
                LoadData();
                // MASTER-01: новые уловы появляются на карте сами (раньше — только после «Обновить» или повторного открытия)
                _stamp = LiveWatch.Stamp(_currentDb);
                _watch.Tick += (s2, e2) =>
                {
                    try { string st = LiveWatch.Stamp(_currentDb); if (st.Length > 0 && st != _stamp) { _stamp = st; LoadInBackground(); } } catch { }   // AUDIT-04 (B4)
                };
                _watch.Start();
            };
            FormClosed += (s, e) => { try { _watch.Stop(); _watch.Dispose(); } catch { } };
            if (gameMode) InitGameMode(left, right);   // GAMEMAP-01: карта на весь экран поверх игры (F12)
        }

        // NEXT-135: текст для водоёмов без проверенной карты (прежнее поведение — нейтральная сетка).
        private const string NoMapNote =
            "Карта водоёма для этого location_id отсутствует в реестре — показана нейтральная сетка.\r\n" +
            "Точки показаны в игровых координатах X/Z (X вправо, Z вверх).\r\n" +
            "Показаны только уловы, координаты которых совпадают с записанным забросом.\r\n" +
            "Позиция игрока не показывается: источник не подтверждён.\r\n" +
            "Плотность — число записанных уловов в ячейке, НЕ вероятность поимки.";

        private string MapNote(WaterbodyMapInfo m) =>
            "Подложка: реконструкция карты игры (TFBase_" + m.LocationId + ", " + m.MapVersion + ")" +
            (Canvas.MapImage != null ? ", " + Canvas.MapImage.Width + "×" + Canvas.MapImage.Height + " пикс." : "") +
            (Canvas.MapImageHd != null ? ", при увеличении " + Canvas.MapImageHd.Width + "×" + Canvas.MapImageHd.Height : "") + ".\r\n" +
            "Достоверность: " + m.Confidence + ". " + m.ValidationText + "\r\n" +
            "Границы X " + m.MinX.ToString("0.#", CultureInfo.InvariantCulture) + "…" + m.MaxX.ToString("0.#", CultureInfo.InvariantCulture) +
            ", Z " + m.MinZ.ToString("0.#", CultureInfo.InvariantCulture) + "…" + m.MaxZ.ToString("0.#", CultureInfo.InvariantCulture) + " (север сверху).\r\n" +
            "Точки вне границ карты не переносятся на край.\r\n" +
            "Показаны только уловы, координаты которых совпадают с записанным забросом. " +
            "Плотность — число записанных уловов в ячейке, НЕ вероятность поимки.";

        private static string BathyNote(BathyInfo? b)
        {
            if (b == null) return "Батиметрия: нет данных для водоёма.";
            if (!b.Implemented) return "Батиметрия: " + Bathymetry.StatusText(b.Status) + (b.Note.Length > 0 ? " (" + b.Note + ")" : "") + ".";
            var ci = CultureInfo.InvariantCulture;
            return "Батиметрия: глубина = уровень воды сцены (" + b.WaterLevel?.ToString("0.##", ci) + ") − высота рельефа дна, метры. " +
                   "Макс. " + b.MaxDepthM?.ToString("0.#", ci) + " м, изобаты через " + b.ContourIntervalM?.ToString("0.#", ci) + " м. " +
                   "Глубина: CONFIRMED; совмещение с картой: " + b.AlignmentConfidence + ". Камни, коряги и постройки в рельеф не входят.";
        }

        /// <summary>NEXT-135: выбрать подложку по location_id выбранного водоёма (реестр карт).</summary>
        private void UpdateMapForSelection()
        {
            var item = _water.SelectedItem as WaterItem;
            var info = MapRegistry.Get(item?.LocationId);
            Image? img = info != null ? MapRegistry.LoadImage(info) : null;
            if (img == null) info = null;
            // NEXT-MAP-RESOLUTION-01: для показа — проверенная HD-подложка той же рамки (если есть), иначе прежняя карта
            Bitmap? hd = null; string? hdKey = null;
            if (info != null && info.HasHd)
            {
                hdKey = info.HdFile + "|" + info.HdSha256;
                hd = hdKey == _hdKey ? _hdImage : MapRegistry.LoadHdImage(info);
            }
            bool changed = !ReferenceEquals(info, Canvas.MapInfo);
            Canvas.RefVisible = _refLoc.HasValue && item?.LocationId == _refLoc;   // BRIDGE-FB-01: только на «своём» водоёме
            Canvas.MapInfo = info;
            Canvas.MapImage = img;
            Canvas.MapImageHd = hd;
            if (!ReferenceEquals(hd, _hdImage)) { _hdImage?.Dispose(); _hdImage = hd; }
            _hdKey = hd != null ? hdKey : null;
            _chkMap.Enabled = info != null;
            _fitMapBtn.Enabled = info != null;
            // NEXT-ANDROID-02: батиметрия — только поверх проверенной карты реестра (та же рамка)
            var bathy = Bathymetry.Get(item?.LocationId);
            Canvas.Bathy = bathy;
            Canvas.BathyImage = info != null && bathy != null && bathy.Implemented ? Bathymetry.LoadOverlay(bathy) : null;
            _chkBathy.Enabled = Canvas.BathyImage != null;
            _chkBathy.Text = Canvas.BathyImage != null ? "Глубины" : "Глубины: " + (bathy == null ? "нет данных" : Bathymetry.StatusText(bathy.Status));
            if (changed) _chkGrid.Checked = info == null;   // grid off by default with a verified map; user may re-enable
            var errs = MapRegistry.Errors;
            _noteFull = (info != null ? MapNote(info) : NoMapNote) +
                         (info == null && errs.Count > 0 ? "\r\nРеестр карт: " + string.Join("; ", errs) : "") +
                         "\r\n\r\n" + BathyNote(bathy) +
                         "\r\nЗоны активности: число МОИХ записанных уловов (точка заброса), не вероятность клёва. Объявления других игроков координат не имеют и на карте не показываются.";
            _note.Text = (info != null ? "Карта водоёма из файлов игры." : "Для этого водоёма нет карты — показана сетка координат.") +
                         " Круг — место ваших уловов, число — сколько их записано (не вероятность клёва). Подробнее — наведите сюда.";
            _noteTip.SetToolTipT(_note, _noteFull);
        }

        // NEXT-MAP-RESOLUTION-01: освободить HD-подложку окна (при закрытии и при уничтожении формы)
        private void ReleaseHd()
        {
            if (_hdImage == null) return;
            if (ReferenceEquals(Canvas.MapImageHd, _hdImage)) Canvas.MapImageHd = null;
            _hdImage.Dispose(); _hdImage = null; _hdKey = null;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            ReleaseHd();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ReleaseHd();
            base.Dispose(disposing);
        }

        private sealed class ClusterEntry
        {
            public MapCatch Catch = null!;
            public override string ToString() => Catch.Fish + " " + Catch.Weight + " г  " + Catch.TimeText;
        }

        private readonly ToolTip _statsTip = new() { AutoPopDelay = 20000 };
        private bool _keepView;
        private string _stamp = "";
        private readonly System.Windows.Forms.Timer _watch = new() { Interval = 20000 };

        /// <summary>keepView — не менять масштаб и положение карты (автообновление во время рыбалки).</summary>
        public void LoadData(bool keepView = false)
        {
            _keepView = keepView;
            try { LoadDataCore(); } finally { _keepView = false; }
        }

        private bool _bgLoading;

        /// <summary>AUDIT-04 (B4): автообновление — чтение БД в фоне, применение к окну — в потоке окна (масштаб и положение сохраняются).</summary>
        private void LoadInBackground()
        {
            if (_bgLoading || IsDisposed) return;
            _bgLoading = true;
            var sources = MapSources();
            System.Threading.Tasks.Task.Run(() => MapDataLoader.Load(sources, _parser)).ContinueWith(t =>
            {
                _bgLoading = false;
                if (IsDisposed || t.Exception != null) return;
                _keepView = true;
                try { ApplyLoaded(t.Result); } finally { _keepView = false; }
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void LoadDataCore() => ApplyLoaded(MapDataLoader.Load(MapSources(), _parser));

        private List<(string, string)> MapSources()
        {
            var sources = new List<(string, string)>();
            if (_srcAll.Checked)
            {
                // all session folders next to the active one (Sessions\*\data\catches.db), active first
                sources.Add((Path.GetFileName(_sessionDir.TrimEnd('\\', '/')), _currentDb));
                string? root = Directory.GetParent(_sessionDir.TrimEnd('\\', '/'))?.FullName;
                if (root != null && Directory.Exists(root))
                    foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    {
                        string db = Path.Combine(dir, "data", "catches.db");
                        if (!string.Equals(Path.GetFullPath(db), Path.GetFullPath(_currentDb), StringComparison.OrdinalIgnoreCase))
                            sources.Add((Path.GetFileName(dir), db));
                    }
            }
            else sources.Add((Path.GetFileName(_sessionDir.TrimEnd('\\', '/')), _currentDb));
            return sources;
        }

        private void ApplyLoaded((List<MapCatch>, List<MapCast>, List<string>) loaded)
        {
            (_all, _casts, _errors) = loaded;
            _empties = null;   // GAMEMAP-02: пустые поклёвки перечитаем
            var dts = _all.Select(c => OverlayDataSource.PlausibleTime(c.TimeText)).Where(t => t.HasValue).Select(t => t!.Value.Date).ToList();   // NEXT-ANDROID-02
            if (dts.Count > 0 && _allDates.Checked) { _from.Value = dts.Min(); _to.Value = dts.Max(); }

            string? prev = (_water.SelectedItem as WaterItem)?.Key;
            _water.Items.Clear();
            // FIX-05: один пункт на водоём (все точки входа вместе) — свои водоёмы сверху по числу уловов, затем разделитель и
            // водоёмы без моих уловов по алфавиту. Раньше точки входа («↳ вход N») шли вперемешку с другими водоёмами.
            var mine = _all.Where(c => c.X.HasValue && c.Z.HasValue && c.Verified && c.LocationId.HasValue)
                           .GroupBy(c => c.LocationId!.Value).OrderByDescending(g => g.Count()).ToList();
            string NameOf(uint id) => MapRegistry.Get(id)?.Name ?? _parser.GetLocation(id)?.Name ?? ("водоём " + id);
            foreach (var g in mine)
                _water.Items.Add(new WaterItem { Key = "loc:" + g.Key, Count = g.Count(), LocationId = g.Key, Text = NameOf(g.Key) + "  ·  " + g.Count() + " ул." });
            var withCatches = new HashSet<uint>(mine.Select(g => g.Key));
            var rest = MapRegistry.All.Where(m => !withCatches.Contains(m.LocationId)).OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (rest.Count > 0)
            {
                _water.Items.Add(new WaterItem { Key = SeparatorKey, Text = "──────  без моих уловов  ──────" });
                foreach (var m in rest) _water.Items.Add(new WaterItem { Key = "map:" + m.LocationId, LocationId = m.LocationId, Count = 0, Text = m.Name });
            }
            if (_water.Items.Count == 0) _water.Items.Add(new WaterItem { Key = "", Text = "нет уловов с подтверждёнными координатами" });
            // старый ключ точки входа (L12/S121) → весь водоём
            if (prev != null && !prev.StartsWith("loc:", StringComparison.Ordinal) && !prev.StartsWith("map:", StringComparison.Ordinal))
                prev = _all.FirstOrDefault(c => c.WaterKey == prev)?.LocationId is uint pl ? "loc:" + pl : null;
            // первое открытие: водоём последнего улова (водоём, где вы сейчас, выбирает Form1 через SelectWaterbody) — FIX-05
            if (prev == null && _firstLoad)
            {
                uint? cur = _all.Where(c => c.LocationId.HasValue).OrderByDescending(c => OverlayDataSource.PlausibleTime(c.TimeText) ?? DateTime.MinValue).FirstOrDefault()?.LocationId;
                if (cur is uint cl) prev = withCatches.Contains(cl) ? "loc:" + cl : "map:" + cl;
            }
            _firstLoad = false;
            int idx = prev == null ? 0 : Math.Max(0, _water.Items.Cast<WaterItem>().ToList().FindIndex(w => w.Key == prev));
            if ((_water.Items[idx] as WaterItem)?.Key == SeparatorKey) idx = Math.Min(idx + 1, _water.Items.Count - 1);
            _water.SelectedIndex = idx; // triggers RebuildSpecies + ApplyFilters
            if (_pendingWater is uint pw) { _pendingWater = null; SelectWaterbody(pw); }
            if (_pendingRef is { } pr) { _pendingRef = null; ShowReferencePoints(pr.loc, pr.fish, pr.pts); }
            GameRefresh();
        }

        private IEnumerable<MapCatch> InWater()
        {
            string key = (_water.SelectedItem as WaterItem)?.Key ?? "";
            if (key.StartsWith("loc:", StringComparison.Ordinal) && uint.TryParse(key[4..], out uint lid)) return _all.Where(c => c.LocationId == lid);   // NEXT-ANDROID-02
            return _all.Where(c => c.WaterKey == key);
        }

        private void RebuildSpecies()
        {
            uint? prevId = (_species.SelectedItem as SpeciesItem)?.FishId;
            string? prevName = (_species.SelectedItem as SpeciesItem)?.Name;
            _species.Items.Clear();
            var inWater = InWater().Where(c => c.X.HasValue && (c.Verified || _chkUnverified.Checked)).ToList();
            _species.Items.Add(new SpeciesItem { Name = "Все виды", Count = inWater.Count });
            foreach (var g in inWater.GroupBy(c => c.Fish).OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
                _species.Items.Add(new SpeciesItem { Name = g.Key, FishId = g.First().FishId, IconPath = g.First().IconPath, Count = g.Count() });
            int idx = _species.Items.Cast<SpeciesItem>().ToList().FindIndex(s => s.Name == prevName && s.FishId == prevId);
            _species.SelectedIndex = idx < 0 ? 0 : idx;
        }

        /// <summary>Выбор вида по имени (для тестов/автоматизации). Возвращает false, если вида нет в списке.</summary>
        public bool SelectSpecies(string name)
        {
            for (int i = 0; i < _species.Items.Count; i++)
                if (_species.Items[i] is SpeciesItem s && s.Name == name) { _species.SelectedIndex = i; return true; }
            return false;
        }

        private uint? _refLoc;

        /// <summary>BRIDGE-FB-01: открыть водоём и показать поверх точки других игроков (из Советника → «Другие игроки»).
        /// Точки — центры клеток карты игры; вид кадра — все точки с запасом. false — водоёма нет в списке карты.</summary>
        public bool ShowReferencePoints(uint locationId, string fish, IReadOnlyList<(double X, double Z, string G)> pts)
        {
            if (_firstLoad || _water.Items.Count == 0) { _pendingRef = (locationId, fish, pts.ToList()); return true; }   // UIFIX-02: покажем после загрузки
            _refLoc = locationId;
            Canvas.RefPoints = pts.ToList();
            Canvas.RefLabel = "Точки других игроков: " + fish + " · " + pts.Count + " (справочник чужих уловов, не ваша статистика)";
            bool ok = (_water.SelectedItem as WaterItem)?.LocationId == locationId || SelectWaterbody(locationId);
            Canvas.RefVisible = ok && (_water.SelectedItem as WaterItem)?.LocationId == locationId;
            if (Canvas.RefVisible && pts.Count > 0)
            {
                double x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), z0 = pts.Min(p => p.Z), z1 = pts.Max(p => p.Z);
                double span = Math.Max(Math.Max(x1 - x0, z1 - z0) + 60, 120);
                double scale = Math.Min(Math.Max(1, Canvas.Width), Math.Max(1, Canvas.Height)) * 0.8 / span;
                Canvas.AnimateTo((x0 + x1) / 2, (z0 + z1) / 2, scale);
            }
            Canvas.Invalidate();
            return ok;
        }

        /// <summary>NEXT-ANDROID-02: выбрать водоём по location_id (для тестов/автоматизации).</summary>
        public bool SelectWaterbody(uint locationId)
        {
            if (_firstLoad || _water.Items.Count == 0) { _pendingWater = locationId; return true; }   // GAMEMAP-01: окно ещё не загрузило список — выберем после загрузки
            for (int i = 0; i < _water.Items.Count; i++)
                if (_water.Items[i] is WaterItem w && w.LocationId == locationId) { _water.SelectedIndex = i; return true; }
            return false;
        }

        public void SetLayer(string layer, bool on)
        {
            var map = new Dictionary<string, CheckBox> { ["map"] = _chkMap, ["markers"] = _chkMarkers, ["cluster"] = _chkCluster, ["labels"] = _chkLabels,
                ["zones"] = _chkZones, ["bathy"] = _chkBathy, ["alldates"] = _allDates,
                ["density"] = _chkDensity, ["casts"] = _chkCasts, ["grid"] = _chkGrid, ["unverified"] = _chkUnverified };
            if (map.TryGetValue(layer, out var cb)) cb.Checked = on;
        }

        private void ApplyFilters(bool fit)
        {
            var sp = _species.SelectedItem as SpeciesItem;
            var inWater = InWater().ToList();
            var shown = inWater.Where(c => c.X.HasValue && c.Z.HasValue && (c.Verified || _chkUnverified.Checked))
                               .Where(c => sp == null || sp.Name == "Все виды" || c.Fish == sp.Name)
                               .Where(InDateRange).ToList();
            Canvas.Points = shown;
            // PRERELEASE-01: правило показа (как в истории): авто — при > 300 точек прячем обычные, при > 1500 — и хорошие
            int dmode = _display.Mode;
            Canvas.MarkerMinClass = dmode >= 0 ? dmode : shown.Count > 1500 ? 2 : shown.Count > 300 ? 1 : 0;
            int hiddenMarks = Canvas.MarkerMinClass > 0 ? shown.Count(c => c.Windex is int w && w < Canvas.MarkerMinClass) : 0;
            // NEXT-ANDROID-02: зоны — тот же ActivityZones, что в сервисе для Android
            double span = Canvas.MapInfo != null ? Math.Max(Canvas.MapInfo.SpanX, Canvas.MapInfo.SpanZ) :
                (shown.Count > 0 ? Math.Max(shown.Max(c => c.X!.Value) - shown.Min(c => c.X!.Value), shown.Max(c => c.Z!.Value) - shown.Min(c => c.Z!.Value)) * 6 : 300);
            Canvas.ZoneCell = ActivityZones.CellSize(span);
            Canvas.Zones = ActivityZones.Compute(shown, Canvas.ZoneCell);
            Canvas.SelectedZone = null;
            if (_zoneCard.Visible) { ShowCard(false); _detText.Text = "Фильтр изменён — выберите место на карте или в списке «Лучшие места»."; }
            RebuildTopPlaces();
            string key = (_water.SelectedItem as WaterItem)?.Key ?? "";
            Canvas.Casts = key.StartsWith("loc:", StringComparison.Ordinal)
                ? _casts.Where(k => k.WaterKey.StartsWith("L" + key[4..] + "/", StringComparison.Ordinal)).ToList()
                : _casts.Where(k => k.WaterKey == key && key != "").ToList();
            int noCoords = _all.Count(c => !c.X.HasValue);
            int unverified = _all.Count(c => c.X.HasValue && !c.Verified);
            // MASTER-01: игроку — одна понятная строка; служебные счётчики — в подсказке при наведении
            _stats.Text = "На карте " + shown.Count + " ул." + (sp != null && sp.Name != "Все виды" ? " (" + sp.Name + ")" : "") +
                          " · мест с уловами: " + Canvas.Zones.Count + " · забросов: " + Canvas.Casts.Count +
                          (hiddenMarks > 0 && Canvas.ShowMarkers ? "\r\nметки " + hiddenMarks + " " + (Canvas.MarkerMinClass == 1 ? "обычных" : "обычных и хороших") + " уловов скрыты (места учитывают все)" : "") +
                          (_allDates.Checked ? "" : "\r\nпериод " + _from.Value.ToString("dd.MM.yyyy") + "–" + _to.Value.ToString("dd.MM.yyyy")) +
                          (_errors.Count > 0 ? "\r\nОшибки чтения: " + string.Join("; ", _errors) : "");
            _statsTip.SetToolTipT(_stats, "Всего уловов в базе: " + _all.Count + "; без координат: " + noCoords +
                          "; координаты не подтверждены забросом: " + unverified + (_chkUnverified.Checked ? " (показаны пунктиром)" : " (скрыты)") +
                          "; радиус места " + (Canvas.ZoneCell / 2).ToString("0.#", CultureInfo.InvariantCulture) + " м" +
                          (Canvas.HasMap ? "; вне границ карты: " + Canvas.PointsOutsideMap() + " ул." : ""));
            ApplyLayers();
            if (fit) { if (Canvas.HasMap) Canvas.FitToMap(); else Canvas.FitToData(); } else Canvas.Invalidate();
        }

        private bool InDateRange(MapCatch c)
        {
            if (_allDates.Checked) return true;
            var t = OverlayDataSource.PlausibleTime(c.TimeText);
            return t.HasValue && t.Value.Date >= _from.Value.Date && t.Value.Date <= _to.Value.Date;
        }

        /// <summary>NEXT-ANDROID-02: выбранная зона активности.</summary>
        private void ShowCard(bool on) { _zoneCard.Visible = on; _detText.Visible = !on; }

        /// <summary>ADVISOR-UX-01: уловы, попавшие в ячейку зоны (из того же отфильтрованного набора, что и зоны).</summary>
        private List<MapCatch> ZoneCatches(IEnumerable<ActivityZone> zones)
        {
            // ZONES-02: зона хранит свои уловы (круг), а не номер клетки
            return zones.SelectMany(z => z.Members).Distinct().ToList();
        }

        private void FillZoneCard(List<MapCatch> cs, double cx, double cz)
        {
            var ci = CultureInfo.InvariantCulture;
            uint? loc = cs.Select(c => c.LocationId).FirstOrDefault(l => l.HasValue) ?? Canvas.MapInfo?.LocationId;
            string water = loc.HasValue && _parser.GetLocation(loc.Value)?.Name is string wn && wn.Length > 0 ? wn : "Водоём";
            var parts = new List<string>();
            if (MapGridCoordinates.ToGrid(loc, cx, cz) is { } g) parts.Add("координаты " + MapGridCoordinates.FormatGrid(g));
            else parts.Add("X " + cx.ToString("0", ci) + ", Z " + cz.ToString("0", ci));
            if (Bathymetry.DepthAt(Canvas.Bathy?.LocationId ?? loc, cx, cz) is double d) parts.Add("глубина " + d.ToString("0.0", ci) + " м");
            if (BiteHere(cs, cx, cz) is string bh) parts.Add(bh);   // GAMEMAP-01: «клюёт часто / иногда / редко» — без расчётов на экране
            _zoneWhere.Text = water + "\r\n" + string.Join("  ·  ", parts);
            _zoneWhere.Font = Theme.UiBold;
            // MAPCARD-01: не только «сколько», но и «какие»: средний и лучший вес, доли классов в полосе
            var fish = PlaceStats.ByFish(cs);
            int top = fish.Count > 0 ? fish[0].N : 1;
            _zoneFish.SetRows(fish.Take(8).Select(f => new StatRow
            {
                Icon = IconLoader.Load(f.IconPath), Title = f.Name,
                Subtitle = PlaceStats.Subtitle(f, false) + (f.Last.HasValue ? " · последний " + f.Last.Value.ToString("dd.MM", ci) : ""),
                Value = f.N + " ул.", Fraction = (double)f.N / top, Accent = f.Best >= 2 ? UiKit.GradeColor(f.Best) : Theme.AccentTeal, Classes = f.Classes,
                Tooltip = ClassTip(f)
            }));
            var baits = PlaceStats.ByBait(cs);
            int topB = baits.Count > 0 ? baits[0].N : 1;
            _zoneBaits.SetRows(baits.Take(6).Select(b => new StatRow
            {
                Title = b.Name, Subtitle = PlaceStats.Subtitle(b, true), Value = b.N + " ул.", Fraction = (double)b.N / topB,
                Accent = b.Best == 3 ? UiKit.GradeColor(3) : Theme.AccentGreen, Classes = b.Classes, Tooltip = ClassTip(b)
            }));
            string insight = PlaceStats.Insight(baits);
            _zoneInsight.Text = insight; _zoneInsight.Visible = insight.Length > 0;
            string empt = EmptyNote(loc, cx, cz, ZoneRadius(cs, cx, cz) + 3);
            _zoneEmpty.Text = empt; _zoneEmpty.Visible = empt.Length > 0;
            if (empt.Length > 0) _zoneEmpty.Height = UiText.MeasureText(empt, _zoneEmpty.Font, new Size(Math.Max(200, _zoneBaits.Width - 8), 0), TextFormatFlags.WordBreak).Height + 12;
            if (insight.Length > 0)
            {
                int w = Math.Max(120, (_zoneInsight.Width > 50 ? _zoneInsight.Width : _zoneBaits.Width) - _zoneInsight.Padding.Horizontal - 4);
                _zoneInsight.Height = UiText.MeasureText(insight, _zoneInsight.Font, new Size(w, 0), TextFormatFlags.WordBreak).Height + _zoneInsight.Padding.Vertical + 4;
            }
            _detIcon.Image = fish.Count > 0 ? IconLoader.Load(fish[0].IconPath) : null;
            ShowCard(true);
        }

        private static string ClassTip(PlaceStats.Group g)
        {
            var parts = new List<string>();
            for (int k = 3; k >= 0; k--) if (g.Classes[k] > 0) parts.Add(GradeBadge.ShortName(k).Replace("★ ", "") + ": " + g.Classes[k]);
            int unk = g.N - g.Classes.Sum();
            if (unk > 0) parts.Add("класс не записан: " + unk);
            return "Уловы по классу — " + string.Join(", ", parts) + (g.N >= 2 ? ". Средний вес " + UiKit.Grams(g.AvgG) + ", лучший " + UiKit.Grams(g.MaxG) + "." : ".");
        }

        private void ShowZone(ActivityZone z)
        {
            _clusterList.Visible = false;
            _detIcon.Image = null;
            var ci = CultureInfo.InvariantCulture;
            int rank = _ranked.IndexOf(z) + 1;
            Canvas.SelectedZone = z; Canvas.Invalidate();
            SyncTopSelection(z);
            _detTitle.Text = (rank > 0 ? "Место №" + rank + " из " + _ranked.Count : "Место") + "\r\n" + z.Count + " " + Plural(z.Count, "улов", "улова", "уловов");
            var d = Bathymetry.DepthAt(Canvas.Bathy?.LocationId, z.CenterX, z.CenterZ);
            _detText.Text = string.Join("\r\n", new[]
            {
                "Записанных уловов в зоне: " + z.Count + (rank > 0 ? " (место №" + rank + " из " + _ranked.Count + " по числу уловов)" : ""),
                "Виды: " + string.Join(", ", z.Species.Select(s => s.fish + " — " + s.count)),
                "Период: " + (z.First.Length > 0 ? z.First + " … " + z.Last : "дата не определена"),
                "Круг радиусом " + z.Radius.ToString("0.#", ci) + " м вокруг центра точек (уловы в пределах " + (Canvas.ZoneCell / 2).ToString("0.#", ci) + " м от центра группы)",
                "Центр точек: X " + z.CenterX.ToString("0.0", ci) + ", Z " + z.CenterZ.ToString("0.0", ci),
                "Глубина дна в центре (рельеф игры): " + (d.HasValue ? d.Value.ToString("0.0", ci) + " м" : "нет данных"),
                "",
                "Координаты — точки заброса, а не подтверждённое место вываживания.",
                "Число — сколько уловов записано, НЕ вероятность клёва. Объявления других игроков в зоны не входят (у них нет координат)."
            });
            FillZoneCard(ZoneCatches(new[] { z }), z.CenterX, z.CenterZ);   // ADVISOR-UX-01
        }

        private static string Plural(int n, string one, string few, string many)
        {
            int m10 = n % 10, m100 = n % 100;
            return m10 == 1 && m100 != 11 ? one : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? few : many;
        }

        private void ShowPointDepth(double x, double z)
        {
            if (!Canvas.ShowBathy || Canvas.Bathy == null || !Canvas.Bathy.Implemented) return;
            var d = Bathymetry.DepthAt(Canvas.Bathy.LocationId, x, z);
            var ci = CultureInfo.InvariantCulture;
            _clusterList.Visible = false; _detIcon.Image = null; ShowCard(false);
            _detTitle.Text = "Точка карты\r\n" + (d.HasValue ? d.Value.ToString("0.0", ci) + " м" : "суша / нет данных");
            _detText.Text = "X " + x.ToString("0.0", ci) + ", Z " + z.ToString("0.0", ci) + "\r\n" +
                            "Глубина дна по рельефу игры (уровень воды − высота рельефа), сетка " + Canvas.Bathy.GridW + "×" + Canvas.Bathy.GridH + ".";
        }

        private void ApplyLayers()
        {
            Canvas.ShowZones = _chkZones.Checked;
            Canvas.ShowBathy = _chkBathy.Checked && _chkBathy.Enabled;
            Canvas.ShowMap = _chkMap.Checked;
            Canvas.ShowMarkers = _chkMarkers.Checked;
            Canvas.Cluster = _chkCluster.Checked;
            Canvas.ShowLabels = _chkLabels.Checked;
            Canvas.ShowDensity = _chkDensity.Checked;
            Canvas.ShowCasts = _chkCasts.Checked;
            Canvas.ShowGrid = _chkGrid.Checked;
            _topPanel.Visible = Canvas.ShowZones && _ranked.Count > 0;
            Canvas.Invalidate();
        }

        // ---------- ZONES-UX-01: лучшие места и группы зон ----------
        private bool _suppressTop;
        private sealed class TopEntry
        {
            public ActivityZone Zone = null!; public int Rank;
            public override string ToString() => Rank + ". " + Zone.Count + " ул.";
        }

        private void RebuildTopPlaces()
        {
            _ranked = Canvas.Zones.OrderByDescending(z => z.Count).ThenBy(z => z.Ix).ThenBy(z => z.Iz).ToList();
            _suppressTop = true;
            _topList.BeginUpdate();
            _topList.Items.Clear();
            for (int i = 0; i < Math.Min(12, _ranked.Count); i++) _topList.Items.Add(new TopEntry { Zone = _ranked[i], Rank = i + 1 });
            _topList.EndUpdate();
            _suppressTop = false;
            _topHdr.Text = "Лучшие места (мои уловы)" + (_ranked.Count > 12 ? " — 12 из " + _ranked.Count : "");
            _zoneLine.Clear();
            foreach (var z in _ranked.Take(12))
            {
                var cs = ZoneCatches(new[] { z });
                string sp = string.Join(", ", z.Species.Take(2).Select(q => q.fish + " " + q.count)) + (z.Species.Count > 2 ? " +" + (z.Species.Count - 2) : "");
                var bait = cs.Where(c => !string.IsNullOrWhiteSpace(c.Bait)).GroupBy(c => c.Bait.Trim()).OrderByDescending(gr => gr.Count()).Select(gr => gr.Key).FirstOrDefault();
                _zoneLine[z] = sp + (bait != null ? " · на " + bait.ToLowerInvariant() : "");
            }
            _topPanel.Visible = Canvas.ShowZones && _ranked.Count > 0;
        }

        private void DrawTopItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || _topList.Items[e.Index] is not TopEntry te) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(sel ? Theme.Accent : Theme.SurfaceAlt)) e.Graphics.FillRectangle(bg, e.Bounds);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var col = MapCanvas.HeatColor(MapCanvas.HeatT(te.Zone.Count, _ranked.Count == 0 ? 1 : _ranked[0].Count));
            var c = new Rectangle(e.Bounds.X + 6, e.Bounds.Y + 6, 22, 22);
            using (var br = new SolidBrush(col)) e.Graphics.FillEllipse(br, c);
            bool light = col.R * 0.299 + col.G * 0.587 + col.B * 0.114 > 150;
            UiText.DrawText(e.Graphics, te.Rank.ToString(CultureInfo.InvariantCulture), Theme.UiBold, c, light ? Color.FromArgb(20, 28, 40) : Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            var fg = sel ? Color.White : Theme.Text;
            UiText.DrawText(e.Graphics, te.Zone.Count + " " + Plural(te.Zone.Count, "улов", "улова", "уловов"), Theme.UiBold, new Point(e.Bounds.X + 34, e.Bounds.Y + 1), fg);
            string sp = _zoneLine.TryGetValue(te.Zone, out var line) ? line : string.Join(", ", te.Zone.Species.Take(3).Select(q => q.fish + " " + q.count));
            using var small = new Font("Segoe UI", 8.25f);
            UiText.DrawText(e.Graphics, sp, small, new Rectangle(e.Bounds.X + 34, e.Bounds.Y + 17, e.Bounds.Width - 38, 16), sel ? Color.White : Theme.TextMuted,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void SyncTopSelection(ActivityZone? z)
        {
            _suppressTop = true;
            int idx = -1;
            for (int i = 0; i < _topList.Items.Count; i++) if (_topList.Items[i] is TopEntry te && ReferenceEquals(te.Zone, z)) { idx = i; break; }
            if (_topList.SelectedIndex != idx) _topList.SelectedIndex = idx;
            _suppressTop = false;
        }

        private void ShowZoneGroup(ZoneCluster c)
        {
            _clusterList.Visible = false; _detIcon.Image = null;
            SyncTopSelection(null);
            _detTitle.Text = "Группа мест\r\n" + c.Count + " " + Plural(c.Count, "улов", "улова", "уловов") + " в " + c.Zones.Count + " местах";
            _detText.Text = string.Join("\r\n", new[]
            {
                "Здесь рядом несколько зон — при этом масштабе они объединены в одну метку.",
                "Карта приближена: метка распадается на отдельные зоны. Щёлкните по зоне — появятся подробности.",
                "",
                "Виды: " + string.Join(", ", c.Species().Select(s => s.fish + " — " + s.count)),
                "Крупнейшая зона группы: " + c.Top.Count + " ул.",
                "",
                "Число — сколько МОИХ уловов записано, НЕ вероятность клёва."
            });
            FillZoneCard(ZoneCatches(c.Zones), c.CenterX, c.CenterZ);   // ADVISOR-UX-01: щелчок по группе приближает карту; ниже — сводка группы
        }

        private void DrawSpeciesItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(sel ? Theme.Selection : Theme.SurfaceAlt)) e.Graphics.FillRectangle(bg, e.Bounds);
            if (_species.Items[e.Index] is not SpeciesItem it) return;
            int x = e.Bounds.Left + 4;
            var icon = IconLoader.Load(it.IconPath);
            if (icon != null) e.Graphics.DrawImage(icon, new Rectangle(x, e.Bounds.Top + 1, e.Bounds.Height - 2, e.Bounds.Height - 2));
            x += e.Bounds.Height + 4;
            UiText.DrawText(e.Graphics, it.ToString(), Theme.Ui, new Rectangle(x, e.Bounds.Top, e.Bounds.Right - x, e.Bounds.Height),
                Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void ShowDetails(List<MapCatch> items)
        {
            ShowCard(false);
            if (items.Count == 1)
            {
                _clusterList.Visible = false;
                ShowOne(items[0]);
                return;
            }
            _clusterList.Items.Clear();
            foreach (var c in items.OrderByDescending(c => c.TimeText)) _clusterList.Items.Add(new ClusterEntry { Catch = c });
            _clusterList.Visible = true;
            _detIcon.Image = null;
            _detTitle.Text = "Группа: " + items.Count + " ул.";
            _detText.Text = "Близкие метки сгруппированы. Выберите улов в списке ниже или приблизьте карту колесом мыши.";
        }

        private string ItemName(uint? id) => id.HasValue ? (_parser.GetItem(id)?.Name is string n && n.Length > 0 ? n : "ID " + id) : "";

        private void ShowOne(MapCatch c)
        {
            _detIcon.Image = IconLoader.Load(c.IconPath);
            _detTitle.Text = c.Fish + "\r\n" + c.Weight + " г";
            var ci = CultureInfo.InvariantCulture;
            var tackle = new[] { ("Удилище", c.RodId), ("Катушка", c.ReelId), ("Леска", c.LineId), ("Крючок", c.HookId), ("Поплавок", c.FloatId) }
                .Where(t => t.Item2.HasValue).Select(t => t.Item1 + ": " + ItemName(t.Item2)).ToList();
            var weather = new List<string>();
            if (c.AirTemp.HasValue) weather.Add(c.AirTemp.Value.ToString("0.#", ci) + " °C");
            if (c.WindMs.HasValue) weather.Add("ветер " + c.WindMs.Value.ToString("0.#", ci) + " м/с");
            else if (c.WindKmh.HasValue) weather.Add("ветер " + c.WindKmh.Value.ToString("0.#", ci) + " км/ч");
            if (c.Humidity.HasValue) weather.Add("влажность " + c.Humidity.Value.ToString("0", ci) + " %");

            _detText.Text = string.Join("\r\n", new[]
            {
                "Наживка: " + (string.IsNullOrWhiteSpace(c.Bait) ? "не записана" : c.Bait),
                "Снасть: " + (tackle.Count > 0 ? string.Join("; ", tackle) : "не записана"),
                "Глубина: " + (c.DepthCm.HasValue ? (c.DepthCm.Value / 100.0).ToString("0.00", ci) + " м" : "не записана"),
                "Глубина дна в точке заброса (рельеф игры): " + (Bathymetry.DepthAt(c.LocationId, c.X ?? double.NaN, c.Z ?? double.NaN) is double bd ? bd.ToString("0.0", ci) + " м" : "нет данных"),
                "Погода: " + (weather.Count > 0 ? string.Join(", ", weather) : "не записана"),
                "Дата и время (ПК): " + c.TimeText,
                "Сессия: " + c.Session + (string.IsNullOrEmpty(c.SessionId) ? "" : " (" + (c.SessionId.Length > 8 ? c.SessionId[..8] : c.SessionId) + ")"),
                "Статус записи: " + c.Status,
                "",
                "Координаты заброса: X " + (c.X?.ToString("0.00", ci) ?? "?") + "; Y " + (c.Y?.ToString("0.00", ci) ?? "?") + "; Z " + (c.Z?.ToString("0.00", ci) ?? "?"),
                c.Verified ? "Проверка: совпадают с забросом #" + c.CastId + " (удочка " + c.CastRod + ", исход " + c.CastOutcome + ")"
                           : "Проверка: НЕ подтверждены записанным забросом",
                "Водоём: location_id " + (c.LocationId?.ToString() ?? "?") + " / spawn_locid " + (c.SpawnLocId?.ToString() ?? "?"),
                "Игровое время: не показывается (источник не проверен)"
            });
        }
    }
}
