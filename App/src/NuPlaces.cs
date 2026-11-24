// UI-02 / step 5: «Места по уловам» — old catches of this water grouped into places (layer of the new map and the minimap).
// Close catches (≤15 m from the place centre) make one place; size = how many fish, colour = the best class caught there.
// Filters: fish, class, bottom depth, bait, part of the game day, period. Counts of recorded catches (frequency), not chances.
// Read-only: the same verified catches the map already shows (catches.db of all sessions, coordinates confirmed by a cast).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace FisherLogger
{
    public sealed class NuPlaceFilter
    {
        public string Fish = "";          // "" — all fish
        public int MinClass = -1;          // -1 any, 1 good and better, 2 big and better, 3 trophies only
        public int DepthBand = -1;         // -1 any, else index into NuPlaces.DepthEdges
        public string Bait = "";           // "" — any bait
        public string Part = "";           // "" — any time, else DayParts.Part names: «рассвет», «день», «вечер», «ночь»
        public int Period = 3;             // 0 today, 1 7 days, 2 30 days, 3 all time

        public string Key => string.Join("|", Fish, MinClass.ToString(CultureInfo.InvariantCulture), DepthBand.ToString(CultureInfo.InvariantCulture), Bait, Part, Period.ToString(CultureInfo.InvariantCulture));
        public bool IsDefault => Fish.Length == 0 && MinClass < 0 && DepthBand < 0 && Bait.Length == 0 && Part.Length == 0 && Period == 3;
        public DateTime? From(DateTime now) => Period switch { 0 => now.Date, 1 => now.Date.AddDays(-6), 2 => now.Date.AddDays(-29), _ => null };

        public static NuPlaceFilter FromSettings(WindowSettings w) => new()
        {
            Fish = w.NuPlaceFish ?? "", MinClass = w.NuPlaceMinClass, DepthBand = w.NuPlaceDepth, Bait = w.NuPlaceBait ?? "", Part = w.NuPlacePart ?? "", Period = w.NuPlacePeriod
        };
        public void ToSettings(WindowSettings w) { w.NuPlaceFish = Fish; w.NuPlaceMinClass = MinClass; w.NuPlaceDepth = DepthBand; w.NuPlaceBait = Bait; w.NuPlacePart = Part; w.NuPlacePeriod = Period; }

        /// <summary>One catch against the filter; depth is the bottom depth at the catch point (null — not known), t its time.</summary>
        public bool Ok(MapCatch c, double? depth, DateTime? t, DateTime now)
        {
            if (Fish.Length > 0 && NuPlaces.FishName(c) != Fish) return false;
            if (MinClass >= 0 && (c.Windex ?? -1) < MinClass) return false;
            if (Bait.Length > 0 && (c.Bait ?? "").Trim() != Bait) return false;
            if (From(now) is DateTime f && (t ?? DateTime.MinValue) < f) return false;
            if (Part.Length > 0 && (t is not DateTime tt || DayParts.Part(DayParts.GameHour(tt)) != Part)) return false;
            if (DepthBand >= 0)
            {
                if (depth is not double d) return false;
                var (lo, hi) = NuPlaces.Band(DepthBand);
                if (d < lo || d >= hi) return false;
            }
            return true;
        }
    }

    public sealed class NuPlace
    {
        public double X, Z;
        public double SeedX, SeedZ;       // first catch of the place: the centre may not wander far from it
        /// <summary>Farthest catch from the centre, m (the journal of the place uses this circle).</summary>
        public double RadiusM => Members.Count == 0 ? 0 : Members.Max(m => Math.Sqrt((m.X!.Value - X) * (m.X!.Value - X) + (m.Z!.Value - Z) * (m.Z!.Value - Z)));
        public readonly List<MapCatch> Members = new();
        public int Count => Members.Count;
        public int BestClass = -1;
        public MapCatch? Record;           // best catch of the place: highest class, then heaviest
        public double? DepthMin, DepthMax;
        public DateTime? Last;
        public List<(string name, int n)> Fish => Members.GroupBy(NuPlaces.FishName).Select(g => (g.Key, g.Count())).OrderByDescending(q => q.Item2).ThenBy(q => q.Key, StringComparer.CurrentCulture).ToList();
        public List<(string name, int n)> Baits => Members.Where(m => !string.IsNullOrWhiteSpace(m.Bait)).GroupBy(m => m.Bait.Trim()).Select(g => (g.Key, g.Count())).OrderByDescending(q => q.Item2).ThenBy(q => q.Key, StringComparer.CurrentCulture).ToList();
    }

    public static class NuPlaces
    {
        public const double MergeM = 15;
        public static readonly double[] DepthEdges = { 0, 1.5, 3, 5, 8, double.PositiveInfinity };
        public static readonly string[] Parts = { "рассвет", "день", "вечер", "ночь" };
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        public static (double lo, double hi) Band(int i) => (DepthEdges[Math.Clamp(i, 0, DepthEdges.Length - 2)], DepthEdges[Math.Clamp(i, 0, DepthEdges.Length - 2) + 1]);
        public static string BandName(int i)
        {
            if (i < 0) return "любая";
            var (lo, hi) = Band(i);
            return double.IsPositiveInfinity(hi) ? "глубже " + lo.ToString("0.#", Ru) + " м" : lo <= 0 ? "до " + hi.ToString("0.#", Ru) + " м" : lo.ToString("0.#", Ru) + "–" + hi.ToString("0.#", Ru) + " м";
        }
        public static string ClassName(int w) => w switch { 3 => "трофей", 2 => "крупный", 1 => "хороший", 0 => "обычный", _ => "класс не известен" };
        public static string MinClassName(int w) => w switch { 1 => "хорошие и выше", 2 => "крупные и выше", 3 => "только трофеи", _ => "любой" };
        public static string PeriodName(int p) => p switch { 0 => "сегодня", 1 => "7 дней", 2 => "30 дней", _ => "всё время" };
        public static Color ClassColor(int w) => w switch { 3 => Color.FromArgb(0xF5, 0xC4, 0x51), 2 => Color.FromArgb(0x5D, 0xB3, 0xFF), 1 => NuTheme.Ok, _ => Color.FromArgb(0xC9, 0xD4, 0xD1) };
        public static string FishName(MapCatch c) => string.IsNullOrWhiteSpace(c.Fish) ? "неизвестно" : c.Fish.Trim();

        /// <summary>Radius on screen: grows with the number of fish (square root), 7…24 px.</summary>
        public static float Radius(int count) => (float)Math.Clamp(6 + 3 * Math.Sqrt(Math.Max(1, count)), 7, 24);

        private static readonly ConcurrentDictionary<(uint?, long, long), double?> DepthCache = new();
        /// <summary>Bottom depth at a point from the depth map of the water (cached; null — no depth map).</summary>
        public static double? DepthAt(uint? loc, double x, double z)
        {
            var key = (loc, (long)Math.Round(x * 10), (long)Math.Round(z * 10));
            if (DepthCache.TryGetValue(key, out var v)) return v;
            double? d = null;
            try { d = Bathymetry.DepthAt(loc, x, z); } catch { }
            if (DepthCache.Count > 200000) DepthCache.Clear();
            DepthCache[key] = d; return d;
        }

        /// <summary>
        /// Groups catches into places. Catches in time order; each joins the nearest place whose centre is ≤ mergeM away
        /// (the centre is the mean of its catches), otherwise starts a new place. Pure and deterministic (tested).
        /// </summary>
        public static List<NuPlace> Build(IEnumerable<MapCatch> catches, NuPlaceFilter f, DateTime now, Func<MapCatch, double?> depth, double mergeM = MergeM)
        {
            var list = new List<(MapCatch c, DateTime? t, double? d)>();
            foreach (var c in catches)
            {
                if (!c.X.HasValue || !c.Z.HasValue) continue;
                var t = OverlayDataSource.PlausibleTime(c.TimeText);
                var d = depth(c);
                if (f.Ok(c, d, t, now)) list.Add((c, t, d));
            }
            var places = new List<NuPlace>();
            foreach (var (c, t, d) in list.OrderBy(q => q.t ?? DateTime.MaxValue).ThenBy(q => q.c.TimeText, StringComparer.Ordinal).ThenBy(q => q.c.X).ThenBy(q => q.c.Z))
            {
                double x = c.X!.Value, z = c.Z!.Value;
                NuPlace? best = null; double bd = double.MaxValue;
                foreach (var p in places)
                {
                    double dd = Math.Sqrt((p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z));
                    // ui02_review5 #5: also near the first catch of the place, so a chain along the shore does not grow into one huge place
                    double ds = Math.Sqrt((p.SeedX - x) * (p.SeedX - x) + (p.SeedZ - z) * (p.SeedZ - z));
                    if (dd <= mergeM && ds <= 1.5 * mergeM && dd < bd) { bd = dd; best = p; }
                }
                if (best == null) { best = new NuPlace { X = x, Z = z, SeedX = x, SeedZ = z }; places.Add(best); }
                else { int n = best.Count; best.X = (best.X * n + x) / (n + 1); best.Z = (best.Z * n + z) / (n + 1); }
                best.Members.Add(c);
                if ((c.Windex ?? -1) > best.BestClass) best.BestClass = c.Windex ?? -1;
                if (best.Record == null || (c.Windex ?? -1) > (best.Record.Windex ?? -1) || (c.Windex ?? -1) == (best.Record.Windex ?? -1) && c.Weight > best.Record.Weight) best.Record = c;
                if (d is double dv) { best.DepthMin = Math.Min(best.DepthMin ?? dv, dv); best.DepthMax = Math.Max(best.DepthMax ?? dv, dv); }
                if (t is DateTime tv && (best.Last == null || tv > best.Last)) best.Last = tv;
            }
            return places.OrderByDescending(p => p.Count).ThenByDescending(p => p.BestClass).ToList();
        }

        // one shared result for the big map and the minimap (same data object and same filter → no recount)
        private static readonly object Sync = new();
        private static object? _cData; private static string _cKey = ""; private static List<NuPlace> _cList = new(); private static DateTime _cDay;
        public static List<NuPlace> Cached(NuMapData? d, NuPlaceFilter f)
        {
            if (d == null || d.LocationId == null) return new List<NuPlace>();
            lock (Sync)
            {
                var now = DateTime.Now;
                if (ReferenceEquals(_cData, d.Catches) && _cKey == f.Key && _cDay == now.Date) return _cList;
                uint? loc = d.LocationId;
                var list = Build(d.Catches, f, now, c => DepthAt(loc, c.X!.Value, c.Z!.Value));
                _cList = list; _cData = d.Catches; _cKey = f.Key; _cDay = now.Date;
                _snap = new Snap(d.Catches, f.Key, now.Date, d.LocationId, list);
                return list;
            }
        }

        // fixr3_review #2: painting never counts places — it reads the last published result (no lock) and asks the background for a new one
        private sealed record Snap(object Catches, string Key, DateTime Day, uint? Loc, List<NuPlace> List);
        private static volatile Snap? _snap;
        private static int _building;
        public static List<NuPlace> Peek(NuMapData? d, NuPlaceFilter f, Action? ready)
        {
            if (d == null || d.LocationId == null) return new List<NuPlace>();
            var s = _snap; string key = f.Key;
            if (s != null && ReferenceEquals(s.Catches, d.Catches) && s.Key == key && s.Day == DateTime.Now.Date) return s.List;
            if (Interlocked.CompareExchange(ref _building, 1, 0) == 0)
            {
                var copy = new NuPlaceFilter { Fish = f.Fish, MinClass = f.MinClass, DepthBand = f.DepthBand, Bait = f.Bait, Part = f.Part, Period = f.Period };
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { Cached(d, copy); } catch { _snap = new Snap(d.Catches, copy.Key, DateTime.Now.Date, d.LocationId, new List<NuPlace>()); }   // no endless retries
                    finally { Interlocked.Exchange(ref _building, 0); }
                    try { ready?.Invoke(); } catch { }
                });
            }
            // until the new result is ready: the old places of the same water (another filter) or nothing
            return s != null && s.Loc == d.LocationId ? s.List : new List<NuPlace>();
        }

        /// <summary>Options for the filter menus from the catches of this water: (value, count), most frequent first.</summary>
        public static List<(string v, int n)> FishOptions(IEnumerable<MapCatch> cs) => cs.Where(c => c.X.HasValue).GroupBy(FishName).Select(g => (g.Key, g.Count())).OrderByDescending(q => q.Item2).ToList();
        public static List<(string v, int n)> BaitOptions(IEnumerable<MapCatch> cs) => cs.Where(c => c.X.HasValue && !string.IsNullOrWhiteSpace(c.Bait)).GroupBy(c => c.Bait.Trim()).Select(g => (g.Key, g.Count())).OrderByDescending(q => q.Item2).ToList();

        public static string Kg(int g) => g >= 1000 ? (g / 1000.0).ToString("0.00", Ru) + " кг" : g + " г";
        public static string Depth(NuPlace p) => p.DepthMin is double a && p.DepthMax is double b ? (Math.Abs(a - b) < 0.15 ? a.ToString("0.0", Ru) : a.ToString("0.0", Ru) + "–" + b.ToString("0.0", Ru)) + " м" : "";
    }
}
