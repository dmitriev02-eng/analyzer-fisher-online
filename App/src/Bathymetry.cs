using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace FisherLogger
{
    // =====================================================================================
    // NEXT-ANDROID-02: общий набор батиметрии (Data\assets\bathymetry, генератор build_bathymetry.py).
    //  * Глубина = уровень воды сцены (SceneData.water.world_pos.y) − высота рельефа TerrainData, метры.
    //  * Слой bathy_NNN_2048.png имеет ту же рамку и размер, что карта реестра → рисуется тем же MapRenderer.
    //  * Сетка depth_NNN_1024.u16.gz — глубина в сантиметрах для запроса «глубина в точке».
    //  * Файлы проверяются по SHA-256 из реестра батиметрии; при несовпадении слой не показывается.
    //  * Windows и Android (через CompanionServer) используют одни и те же файлы.
    // =====================================================================================
    public sealed class BathyLabel { public double X { get; set; } public double Z { get; set; } public double DepthM { get; set; } public string Kind { get; set; } = ""; }
    public sealed class BathyBand { public double FromM { get; set; } public double ToM { get; set; } public int[] Rgb { get; set; } = Array.Empty<int>(); public double Alpha { get; set; } }

    public sealed class BathyInfo
    {
        public uint LocationId;
        public string Name = "", Status = "", Note = "", UnderwaterRelief = "", AbsoluteDepth = "";
        public string DepthConfidence = "", AlignmentConfidence = "", Coverage = "";
        public double? MaxDepthM, MeanDepthM, WaterLevel, ContourIntervalM, WaterFraction;
        public double MinX, MaxX, MinZ, MaxZ;
        public string OverlayFile = "", OverlaySha = "", GridFile = "", GridSha = "";
        public int GridW, GridH;
        public List<BathyLabel> Labels = new();
        public List<BathyBand> Bands = new();
        public bool Implemented => Status == "IMPLEMENTED";
    }

    public static class Bathymetry
    {
        public const string RegistryRelativePath = "assets/bathymetry/bathymetry_registry.json";
        private static readonly object Sync = new();
        private static Dictionary<uint, BathyInfo>? _all;
        private static readonly Dictionary<uint, Bitmap?> Overlays = new();
        private static readonly Dictionary<uint, ushort[]?> Grids = new();
        private static readonly List<string> _errors = new();

        public static IReadOnlyList<string> Errors { get { lock (Sync) return _errors.ToArray(); } }
        public static string Dir => Path.GetDirectoryName(IconLoader.ResolvePath(RegistryRelativePath)) ?? "";

        public static IReadOnlyCollection<BathyInfo> All { get { lock (Sync) { EnsureLoaded(); return _all!.Values.ToList(); } } }

        public static BathyInfo? Get(uint? locationId)
        {
            if (!locationId.HasValue) return null;
            lock (Sync) { EnsureLoaded(); return _all!.TryGetValue(locationId.Value, out var b) ? b : null; }
        }

        private static double? D(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        private static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static void EnsureLoaded()
        {
            if (_all != null) return;
            _all = new Dictionary<uint, BathyInfo>();
            string path = IconLoader.ResolvePath(RegistryRelativePath);
            try
            {
                if (!File.Exists(path)) { _errors.Add("нет реестра батиметрии: " + path); return; }
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var w in doc.RootElement.GetProperty("waterbodies").EnumerateArray())
                {
                    var b = new BathyInfo
                    {
                        LocationId = (uint)w.GetProperty("location_id").GetInt32(), Name = S(w, "name"), Status = S(w, "status"), Note = S(w, "note"),
                        UnderwaterRelief = S(w, "underwater_relief"), AbsoluteDepth = S(w, "absolute_depth_m"),
                        DepthConfidence = S(w, "depth_confidence"), AlignmentConfidence = S(w, "alignment_confidence"), Coverage = S(w, "coverage"),
                        MaxDepthM = D(w, "max_depth_m"), MeanDepthM = D(w, "mean_depth_m"), WaterLevel = D(w, "water_level_world_y"),
                        ContourIntervalM = D(w, "contour_interval_m"), WaterFraction = D(w, "water_fraction")
                    };
                    if (w.TryGetProperty("frame", out var f))
                    { b.MinX = f.GetProperty("min_x").GetDouble(); b.MaxX = f.GetProperty("max_x").GetDouble(); b.MinZ = f.GetProperty("min_z").GetDouble(); b.MaxZ = f.GetProperty("max_z").GetDouble(); }
                    if (w.TryGetProperty("overlay", out var o)) { b.OverlayFile = Path.GetFileName(S(o, "file")); b.OverlaySha = S(o, "sha256"); }
                    if (w.TryGetProperty("grid", out var g)) { b.GridFile = Path.GetFileName(S(g, "file")); b.GridSha = S(g, "sha256"); b.GridW = g.GetProperty("width").GetInt32(); b.GridH = g.GetProperty("height").GetInt32(); }
                    if (w.TryGetProperty("labels", out var ls))
                        foreach (var l in ls.EnumerateArray()) b.Labels.Add(new BathyLabel { X = l.GetProperty("x").GetDouble(), Z = l.GetProperty("z").GetDouble(), DepthM = l.GetProperty("depth_m").GetDouble(), Kind = S(l, "kind") });
                    if (w.TryGetProperty("bands", out var bs))
                        foreach (var x in bs.EnumerateArray())
                            b.Bands.Add(new BathyBand { FromM = x.GetProperty("from_m").GetDouble(), ToM = x.GetProperty("to_m").GetDouble(), Alpha = x.GetProperty("alpha").GetDouble(),
                                                        Rgb = x.GetProperty("rgb").EnumerateArray().Select(v => v.GetInt32()).ToArray() });
                    _all[b.LocationId] = b;
                }
            }
            catch (Exception ex) { _errors.Add("реестр батиметрии не читается: " + ex.Message); }
        }

        /// <summary>Байты файла набора, только если SHA-256 совпадает с реестром (иначе null).</summary>
        public static byte[]? VerifiedBytes(string file, string sha)
        {
            if (string.IsNullOrEmpty(file)) return null;
            string p = Path.Combine(Dir, Path.GetFileName(file));
            if (!File.Exists(p)) { lock (Sync) _errors.Add("нет файла батиметрии: " + file); return null; }
            byte[] bytes = File.ReadAllBytes(p);
            if (!string.IsNullOrEmpty(sha) && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(sha, StringComparison.OrdinalIgnoreCase))
            { lock (Sync) _errors.Add("SHA-256 не совпадает: " + file); return null; }
            return bytes;
        }

        public static Bitmap? LoadOverlay(BathyInfo b)
        {
            lock (Sync)
            {
                if (Overlays.TryGetValue(b.LocationId, out var c)) return c;
                Bitmap? res = null;
                try
                {
                    var bytes = b.Implemented ? VerifiedBytes(b.OverlayFile, b.OverlaySha) : null;
                    if (bytes != null)
                    {
                        using var ms = new MemoryStream(bytes);
                        using var src = new Bitmap(ms);
                        res = src.Clone(new Rectangle(0, 0, src.Width, src.Height), PixelFormat.Format32bppPArgb);
                    }
                }
                catch (Exception ex) { _errors.Add("слой батиметрии не читается: " + ex.Message); }
                Overlays[b.LocationId] = res;
                return res;
            }
        }

        private static ushort[]? Grid(BathyInfo b)
        {
            lock (Sync)
            {
                if (Grids.TryGetValue(b.LocationId, out var g)) return g;
                ushort[]? res = null;
                try
                {
                    var bytes = b.Implemented ? VerifiedBytes(b.GridFile, b.GridSha) : null;
                    if (bytes != null)
                    {
                        using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                        using var ms = new MemoryStream();
                        gz.CopyTo(ms);
                        var raw = ms.ToArray();
                        if (raw.Length == b.GridW * b.GridH * 2)
                        {
                            res = new ushort[b.GridW * b.GridH];
                            Buffer.BlockCopy(raw, 0, res, 0, raw.Length);   // little-endian (Windows/Android x86/ARM)
                        }
                        else _errors.Add("размер сетки глубин не совпадает: " + b.GridFile);
                    }
                }
                catch (Exception ex) { _errors.Add("сетка глубин не читается: " + ex.Message); }
                Grids[b.LocationId] = res;
                return res;
            }
        }

        /// <summary>Глубина (м) в точке X/Z: значение ячейки сетки 1024² (ближайшая ячейка). null — суша/вне рамки/нет данных.</summary>
        public static double? DepthAt(uint? locationId, double x, double z)
        {
            var b = Get(locationId);
            if (b == null || !b.Implemented || double.IsNaN(x) || double.IsNaN(z)) return null;
            if (x < b.MinX || x > b.MaxX || z < b.MinZ || z > b.MaxZ) return null;
            var g = Grid(b);
            if (g == null) return null;
            int i = (int)Math.Floor((x - b.MinX) / (b.MaxX - b.MinX) * b.GridW);
            int j = (int)Math.Floor((b.MaxZ - z) / (b.MaxZ - b.MinZ) * b.GridH);
            i = Math.Clamp(i, 0, b.GridW - 1); j = Math.Clamp(j, 0, b.GridH - 1);
            ushort v = g[j * b.GridW + i];
            return v == 65535 ? null : v / 100.0;
        }

        public static string StatusText(string status) => status switch
        {
            "IMPLEMENTED" => "батиметрия построена",
            "NOT_PUBLISHED_NO_VERIFIED_MAP" => "рельеф есть, но нет проверенной карты — слой не показывается",
            "NOT_IMPLEMENTED_MULTI_TERRAIN" => "несколько террейнов — не построена",
            "NO_SOURCE" => "нет данных рельефа",
            _ => status
        };
    }

    // =====================================================================================
    // NEXT-ANDROID-02: зоны активности — агрегирование МОИХ записанных уловов по ячейкам сетки (игровые X/Z).
    // Координаты — точка заброса (не подтверждённое место вываживания). Публичные объявления координат не имеют
    // и в зоны не входят. Число в зоне — количество записанных уловов, НЕ вероятность поклёвки.
    // =====================================================================================
    public sealed class ActivityZone
    {
        public long Ix, Iz;
        /// <summary>ZONES-02: зона — круг (центр = среднее точек, Radius — м); MinX…MaxZ — описанный квадрат круга (для масштаба/якоря).</summary>
        public double MinX, MaxX, MinZ, MaxZ, CenterX, CenterZ, Radius;
        /// <summary>ZONES-02: уловы зоны (для карточки места).</summary>
        public List<MapCatch> Members = new();
        public int Count;
        public List<(string fish, int count)> Species = new();
        public string First = "", Last = "";
    }

    /// <summary>ZONES-UX-01: группа зон, которые при текущем масштабе слились бы на экране (одна зона = группа из 1).
    /// Только отображение: сами зоны (ячейки) и их счётчики не меняются.</summary>
    public sealed class ZoneCluster
    {
        public List<ActivityZone> Zones = new();
        public int Count;
        public double CenterX, CenterZ, MinX, MaxX, MinZ, MaxZ;
        public float ScreenX, ScreenY;
        /// <summary>Где рисовать метку (мировые координаты): взвешенный по уловам центр «якорей» зон.
        /// Якорь = центр точек зоны; при крупном масштабе он прижат внутрь своей ячейки, чтобы метки соседних ячеек не сливались.</summary>
        public double DrawX, DrawZ;
        public bool IsGroup => Zones.Count > 1;
        public ActivityZone Top => Zones[0];

        internal void Add(ActivityZone z, (double X, double Z) a)
        {
            DrawX = (DrawX * Count + a.X * z.Count) / (Count + z.Count);
            DrawZ = (DrawZ * Count + a.Z * z.Count) / (Count + z.Count);
            if (Zones.Count == 0) { MinX = z.MinX; MaxX = z.MaxX; MinZ = z.MinZ; MaxZ = z.MaxZ; }
            else { MinX = Math.Min(MinX, z.MinX); MaxX = Math.Max(MaxX, z.MaxX); MinZ = Math.Min(MinZ, z.MinZ); MaxZ = Math.Max(MaxZ, z.MaxZ); }
            CenterX = (CenterX * Count + z.CenterX * z.Count) / (Count + z.Count);
            CenterZ = (CenterZ * Count + z.CenterZ * z.Count) / (Count + z.Count);
            Count += z.Count;
            Zones.Add(z);
        }

        /// <summary>Виды по всей группе (сумма по зонам), по убыванию.</summary>
        public List<(string fish, int count)> Species() =>
            Zones.SelectMany(z => z.Species).GroupBy(s => s.fish).Select(g => (g.Key, g.Sum(s => s.count)))
                 .OrderByDescending(s => s.Item2).ThenBy(s => s.Item1).ToList();
    }

    /// <summary>ZONES-UX-01: экранная группировка зон (тот же алгоритм в Android MapsScreen.kt).
    /// Зоны, центры которых на экране ближе mergePx, объединяются в одну метку с суммой уловов —
    /// метки не перекрываются при любом масштабе; при приближении группы распадаются на отдельные зоны.</summary>
    public static class ZoneClustering
    {
        public static List<ZoneCluster> Build(IEnumerable<ActivityZone> zones, Func<double, double, (float X, float Y)> toScreen, float mergePx,
                                              Func<ActivityZone, (double X, double Z)>? anchor = null)
        {
            anchor ??= z => (z.CenterX, z.CenterZ);
            var ordered = zones.OrderByDescending(z => z.Count).ThenBy(z => z.Ix).ThenBy(z => z.Iz).ToList();
            var res = new List<ZoneCluster>();
            double m2 = (double)mergePx * mergePx;
            // 1) лидеры: крупнейшая зона забирает соседей в радиусе mergePx
            foreach (var z in ordered)
            {
                var a = anchor(z);
                var s = toScreen(a.X, a.Z);
                ZoneCluster? best = null; double bd = double.MaxValue;
                foreach (var c in res)
                {
                    double d = (c.ScreenX - s.X) * (double)(c.ScreenX - s.X) + (c.ScreenY - s.Y) * (double)(c.ScreenY - s.Y);
                    if (d < m2 && d < bd) { bd = d; best = c; }
                }
                if (best == null) { best = new ZoneCluster { ScreenX = s.X, ScreenY = s.Y }; res.Add(best); }
                best.Add(z, a);
            }
            // 2) центры групп сместились к «весу» — сливаем группы, которые теперь снова слишком близко
            bool merged = true;
            while (merged)
            {
                merged = false;
                foreach (var c in res) { var s = toScreen(c.DrawX, c.DrawZ); c.ScreenX = s.X; c.ScreenY = s.Y; }
                for (int i = 0; i < res.Count && !merged; i++)
                    for (int j = i + 1; j < res.Count && !merged; j++)
                    {
                        double dx = res[i].ScreenX - res[j].ScreenX, dy = res[i].ScreenY - res[j].ScreenY;
                        if (dx * dx + dy * dy < m2)
                        {
                            foreach (var z in res[j].Zones) res[i].Add(z, anchor(z));
                            res[i].Zones = res[i].Zones.OrderByDescending(z => z.Count).ThenBy(z => z.Ix).ThenBy(z => z.Iz).ToList();
                            res.RemoveAt(j); merged = true;
                        }
                    }
            }
            return res.OrderByDescending(c => c.Count).ThenBy(c => c.Top.Ix).ThenBy(c => c.Top.Iz).ToList();
        }
    }

    /// <summary>ZONES-02: группировка точек по радиусу (вместо клеток сетки). Тот же алгоритм в Android (Offline.kt) и в советнике.
    /// 1) у каждой точки — число соседей в радиусе r; 2) точки по убыванию соседей (при равенстве — по X, Z) становятся центрами:
    /// центр забирает все ещё свободные точки в радиусе r. Результат не зависит от порядка входа.</summary>
    public static class RadiusClustering
    {
        public static List<List<int>> Group(IReadOnlyList<(double X, double Z)> pts, double r)
        {
            int n = pts.Count; var res = new List<List<int>>();
            if (n == 0) return res;
            if (!(r > 0)) r = 5;
            double r2 = r * r;
            var buckets = new Dictionary<(long, long), List<int>>();
            (long, long) B(double x, double z) => ((long)Math.Floor(x / r), (long)Math.Floor(z / r));
            for (int i = 0; i < n; i++) { var k = B(pts[i].X, pts[i].Z); if (!buckets.TryGetValue(k, out var l)) buckets[k] = l = new(); l.Add(i); }
            IEnumerable<int> Near(int i)
            {
                var (bx, bz) = B(pts[i].X, pts[i].Z);
                for (long dx = -1; dx <= 1; dx++) for (long dz = -1; dz <= 1; dz++)
                    if (buckets.TryGetValue((bx + dx, bz + dz), out var l))
                        foreach (int j in l) { double ex = pts[j].X - pts[i].X, ez = pts[j].Z - pts[i].Z; if (ex * ex + ez * ez <= r2 + 1e-9) yield return j; }
            }
            var cnt = new int[n]; for (int i = 0; i < n; i++) cnt[i] = Near(i).Count();
            var order = Enumerable.Range(0, n).OrderByDescending(i => cnt[i]).ThenBy(i => pts[i].X).ThenBy(i => pts[i].Z).ThenBy(i => i).ToList();
            var used = new bool[n];
            foreach (int c in order)
            {
                if (used[c]) continue;
                var g = Near(c).Where(j => !used[j]).OrderBy(j => j).ToList();
                foreach (int j in g) used[j] = true;
                res.Add(g);
            }
            return res;
        }
    }

    public static class ActivityZones
    {
        /// <summary>ZONES-02: ДИАМЕТР зоны (м): на обычных водоёмах 10 м (радиус 5 м — пятно прикорма игры), на очень больших
        /// картах — ~1/120 стороны («круглое» значение). Радиус зоны = CellSize / 2.</summary>
        public static double CellSize(double span) => Math.Max(10, MapCanvas.NiceStep(Math.Max(span, 50) / 120.0));

        /// <summary>ZONES-02: зоны-круги радиусом cell/2 (см. RadiusClustering). Ix — номер зоны по порядку, Iz = 0.</summary>
        public static List<ActivityZone> Compute(IEnumerable<MapCatch> points, double cell)
        {
            var pts = points.Where(p => p.X.HasValue && p.Z.HasValue).ToList();
            double r = cell / 2;
            var groups = RadiusClustering.Group(pts.Select(p => (p.X!.Value, p.Z!.Value)).ToList(), r);
            var zones = groups.Select(ix =>
                {
                    var g = ix.Select(i => pts[i]).ToList();
                    double cx = g.Average(p => p.X!.Value), cz = g.Average(p => p.Z!.Value);
                    double rad = Math.Max(r, g.Max(p => Math.Sqrt((p.X!.Value - cx) * (p.X!.Value - cx) + (p.Z!.Value - cz) * (p.Z!.Value - cz))));
                    var times = g.Select(p => OverlayDataSource.PlausibleTime(p.TimeText)).Where(t => t.HasValue).Select(t => t!.Value).OrderBy(t => t).ToList();
                    return new ActivityZone
                    {
                        Count = g.Count, Members = g, Radius = rad,
                        MinX = cx - rad, MaxX = cx + rad, MinZ = cz - rad, MaxZ = cz + rad, CenterX = cx, CenterZ = cz,
                        Species = g.GroupBy(p => p.Fish).Select(s => (s.Key, s.Count())).OrderByDescending(s => s.Item2).ThenBy(s => s.Key).ToList(),
                        First = times.Count > 0 ? times[0].ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "",
                        Last = times.Count > 0 ? times[^1].ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : ""
                    };
                })
                .OrderByDescending(z => z.Count).ThenBy(z => z.CenterX).ThenBy(z => z.CenterZ).ToList();
            for (int i = 0; i < zones.Count; i++) { zones[i].Ix = i; zones[i].Iz = 0; }
            return zones;
        }

        /// <summary>Общий фильтр точек карты (Windows и Android): водоём, вид, даты (реальные), проверка заброса.</summary>
        public static List<MapCatch> Filter(IEnumerable<MapCatch> all, uint? locationId, string? species, DateTime? from, DateTime? to, bool includeUnverified)
        {
            return all.Where(c => c.LocationId == locationId && c.X.HasValue && c.Z.HasValue && (c.Verified || includeUnverified))
                      .Where(c => string.IsNullOrEmpty(species) || c.Fish == species)
                      .Where(c =>
                      {
                          if (!from.HasValue && !to.HasValue) return true;
                          var t = OverlayDataSource.PlausibleTime(c.TimeText);
                          if (!t.HasValue) return false;
                          return (!from.HasValue || t.Value.Date >= from.Value.Date) && (!to.HasValue || t.Value.Date <= to.Value.Date);
                      }).ToList();
        }
    }
}
