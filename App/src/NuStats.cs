// UI-01 / FEAT-13: new interface step 4 (part) — «Журнал» and «Статистика» inside the new window.
// One filter bar (water, fish, period, class) for both. Statistics: summary and records, charts by time,
// tackle table (bait / hook / leader / groundbait / rod), places and depths. Read-only from catches.db.
// Bites and catches are counts of recorded events (frequency), not probabilities.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;

namespace FisherLogger
{
    public sealed class NuCatchRow
    {
        public long Id; public DateTime? Time; public string Fish = ""; public uint? FishId; public int Weight; public int? Windex;
        public string Bait = "", Hook = "", Leader = "", Groundbait = "", Rod = "";
        public uint? Water; public string WaterName = ""; public double? X, Z, DepthM; public long? CastId; public double? GameHour; public double? SoakMin;
        public string Cell = "";
    }

    public sealed class NuCastRow
    {
        public long Id; public DateTime? Time; public uint? Water; public double? X, Z, DepthM, GameHour, SoakMin;
        public string Bait = "", Hook = "", Leader = "", Groundbait = "", Rod = "";
        /// <summary>bite / empty_bite / catch events of this cast with fish and class.</summary>
        public List<(string type, uint? fish, int? windex, DateTime? at)> Events = new();
    }

    public sealed class NuStatsData
    {
        public List<NuCatchRow> Catches = new();
        public List<NuCastRow> Casts = new();
        public string Stamp = "";
        public List<string> Errors = new();

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Changes only when catches change (the WAL changes on every bite-chance tick, ~8 s — not a reason to reload).</summary>
        public static string StampOf(string db)
        {
            try
            {
                if (!File.Exists(db)) return "";
                using var c = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False"); c.Open();
                // ui01_review3 #6: also notice later fixes of a catch (class, fish name, status) — they change no count and no id
                try
                {
                    using var q = new SqliteCommand("SELECT COUNT(*), IFNULL(MAX(id),0), (SELECT COUNT(*) FROM catches WHERE status = 'skipped'), " +
                        "TOTAL(IFNULL(windex,-1) * 7 + LENGTH(IFNULL(fish_name,'')) * 3 + LENGTH(IFNULL(status,'')) + IFNULL(fish_lang_id,0) % 100003) FROM catches", c);
                    using var r = q.ExecuteReader(); r.Read();
                    return r.GetInt64(0) + "|" + r.GetInt64(1) + "|" + r.GetInt64(2) + "|" + r.GetDouble(3).ToString("R", CultureInfo.InvariantCulture);
                }
                catch (SqliteException)
                {   // an old base without these columns
                    using var q = new SqliteCommand("SELECT COUNT(*), IFNULL(MAX(id),0), (SELECT COUNT(*) FROM catches WHERE status = 'skipped') FROM catches", c);
                    using var r = q.ExecuteReader(); r.Read();
                    return r.GetInt64(0) + "|" + r.GetInt64(1) + "|" + r.GetInt64(2);
                }
            }
            catch { return ""; }
        }

        public static double GameHourOf(DateTime local)
        {
            var msk = local.Kind == DateTimeKind.Utc ? local.AddHours(3) : local.ToUniversalTime().AddHours(3);
            return GameClock.GameTimeOfDay(msk).TotalHours;
        }

        public static NuStatsData Load(string db, PacketDeepParser? parser, Func<uint?, string>? locName)
        {
            var d = new NuStatsData { Stamp = StampOf(db) };
            if (!File.Exists(db)) return d;
            using var c = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False");
            c.Open();
            string S(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), Inv) ?? "";
            double? D(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i), Inv);
            long? L(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i), Inv);

            var tackle = new Dictionary<long, (string bait, string hook, string leader, string gb, string rod)>();
            try
            {
                using var q = new SqliteCommand("SELECT cast_id, bait_name, hook_name, leader_name, groundbait_name, rod_name, lure_name FROM cast_tackle", c);
                using var r = q.ExecuteReader();
                while (r.Read()) { string b = S(r, 1); if (b.Length == 0) b = S(r, 6); tackle[r.GetInt64(0)] = (b, S(r, 2), S(r, 3), S(r, 4), S(r, 5)); }
            }
            catch (Exception ex) { d.Errors.Add("cast_tackle: " + ex.Message); }

            var water = new Dictionary<long, uint?>();
            try
            {
                using var q = new SqliteCommand("SELECT cast_id, MAX(location_id) FROM server_checks WHERE cast_id IS NOT NULL GROUP BY cast_id", c);
                using var r = q.ExecuteReader(); var map = new Dictionary<long, uint>();
                while (r.Read()) water[r.GetInt64(0)] = r.IsDBNull(1) ? null : BiteAnalytics.WaterFromSpawn(r.GetInt64(1), map) ?? (uint)r.GetInt64(1);
            }
            catch (Exception ex) { d.Errors.Add("server_checks: " + ex.Message); }

            var firstWait = new Dictionary<long, DateTime>(); var firstBite = new Dictionary<long, DateTime>();
            var events = new Dictionary<long, List<(string, uint?, int?, DateTime?)>>();
            try
            {
                using var q = new SqliteCommand("SELECT cast_id, event_type, fish_lang_id, windex, timestamp FROM cast_events WHERE cast_id IS NOT NULL AND event_type IN ('bite','empty_bite','catch','wait')", c);
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    long id = r.GetInt64(0); string t = S(r, 1);
                    var ts = OverlayDataSource.PlausibleTime(S(r, 4));
                    if (t == "wait") { if (ts is DateTime w && (!firstWait.TryGetValue(id, out var fw) || w < fw)) firstWait[id] = w; continue; }
                    if (t is "bite" or "empty_bite" && ts is DateTime b && (!firstBite.TryGetValue(id, out var fb) || b < fb)) firstBite[id] = b;
                    if (!events.TryGetValue(id, out var l)) events[id] = l = new();
                    l.Add((t, L(r, 2) is long f && f > 0 ? (uint)f : null, L(r, 3) is long wx && wx is >= 0 and <= 3 ? (int)wx : null, ts));
                }
            }
            catch (Exception ex) { d.Errors.Add("cast_events: " + ex.Message); }

            double? Soak(long id) => firstWait.TryGetValue(id, out var w) && firstBite.TryGetValue(id, out var b) && b >= w ? (b - w).TotalMinutes : null;

            try
            {
                using var q = new SqliteCommand("SELECT id, timestamp, cast_x, cast_z, bait_name FROM casts", c);
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    long id = r.GetInt64(0);
                    var row = new NuCastRow { Id = id, Time = OverlayDataSource.PlausibleTime(S(r, 1)), X = D(r, 2), Z = D(r, 3), Bait = S(r, 4) };
                    if (tackle.TryGetValue(id, out var tk)) { if (tk.bait.Length > 0) row.Bait = tk.bait; row.Hook = tk.hook; row.Leader = tk.leader; row.Groundbait = tk.gb; row.Rod = tk.rod; }
                    row.Water = water.TryGetValue(id, out var w) ? w : null;
                    if (events.TryGetValue(id, out var ev)) row.Events = ev;
                    if (row.Time is DateTime tt) row.GameHour = GameHourOf(tt);
                    row.SoakMin = Soak(id);
                    if (row.X is double x && row.Z is double z && row.Water is uint wl) row.DepthM = Bathymetry.DepthAt(wl, x, z);
                    d.Casts.Add(row);
                }
            }
            catch (Exception ex) { d.Errors.Add("casts: " + ex.Message); }

            try
            {
                using var q = new SqliteCommand("SELECT id, timestamp, fish_name, fish_lang_id, weight, windex, bait_name, location_id, cast_id, cast_x, cast_z, status FROM catches", c);
                using var r = q.ExecuteReader();
                var names = new Dictionary<uint, string>();
                while (r.Read())
                {
                    if (S(r, 11) == "skipped") continue;
                    var row = new NuCatchRow
                    {
                        Id = r.GetInt64(0), Time = OverlayDataSource.PlausibleTime(S(r, 1)), Fish = S(r, 2), FishId = L(r, 3) is long f && f > 0 ? (uint)f : null,
                        Weight = (int)(L(r, 4) ?? 0), Windex = L(r, 5) is long wx && wx is >= 0 and <= 3 ? (int)wx : null, Bait = S(r, 6),
                        Water = L(r, 7) is long lc && lc >= 0 ? (uint)lc : null, CastId = L(r, 8), X = D(r, 9), Z = D(r, 10)
                    };
                    if (row.Fish.Length == 0 && parser?.GetFish(row.FishId) is { } fi) row.Fish = fi.Name;
                    if (row.Fish.Length == 0) row.Fish = "Неизвестно";
                    if (row.CastId is long cid && tackle.TryGetValue(cid, out var tk))
                    { if (row.Bait.Length == 0) row.Bait = tk.bait; row.Hook = tk.hook; row.Leader = tk.leader; row.Groundbait = tk.gb; row.Rod = tk.rod; }
                    if (row.CastId is long cid2) row.SoakMin = Soak(cid2);
                    if (row.Water is null) row.WaterName = BiteAnalytics.NoWater;   // UI-02/3
                    if (row.Water is uint wl)
                    {
                        if (!names.TryGetValue(wl, out var nm)) names[wl] = nm = locName?.Invoke(wl) ?? "";
                        row.WaterName = nm.Length > 0 ? nm : "водоём " + wl;
                        if (row.X is double x && row.Z is double z && row.CastId.HasValue)   // only coordinates confirmed by a recorded cast
                        {
                            row.DepthM = Bathymetry.DepthAt(wl, x, z);
                            row.Cell = MapGridCoordinates.ToGrid(wl, x, z) is { } gg ? MapGridCoordinates.FormatGrid(gg) : "";
                        }
                    }
                    if (row.Time is DateTime tt) row.GameHour = GameHourOf(tt);
                    d.Catches.Add(row);
                }
            }
            catch (Exception ex) { d.Errors.Add("catches: " + ex.Message); }
            d.Catches = d.Catches.OrderByDescending(k => k.Time ?? DateTime.MinValue).ThenByDescending(k => k.Id).ToList();
            return d;
        }
    }

    /// <summary>Filters shared by the journal and the statistics.</summary>
    public sealed class NuFilter
    {
        public uint? Water; public uint? Fish; public string FishName = "";
        /// <summary>All ids seen with the chosen fish name (some records have no id) — for bite events, which carry only the id.</summary>
        public HashSet<uint> FishIds = new();
        public int Period = 3;   // 0 today, 1 7 days, 2 30 days, 3 all
        public int MinClass = -1; // -1 all, 1 good+, 2 big+, 3 trophy
        public string Search = "";
        /// <summary>UI-02/5: catches of one place of the map (water, centre, radius m); null — everywhere.</summary>
        public (uint water, double x, double z, double r, string label)? Area;
        public bool InArea(uint? water, double? x, double? z) =>
            Area is not { } a || water == a.water && x is double xx && z is double zz && (xx - a.x) * (xx - a.x) + (zz - a.z) * (zz - a.z) <= a.r * a.r;

        public DateTime? From(DateTime now) => Period switch { 0 => now.Date, 1 => now.Date.AddDays(-6), 2 => now.Date.AddDays(-29), _ => null };

        public bool Ok(NuCatchRow c, DateTime now)
        {
            if (Water.HasValue && c.Water != Water) return false;
            if (FishName.Length > 0 && c.Fish != FishName) return false;
            if (From(now) is DateTime f && (c.Time ?? DateTime.MinValue) < f) return false;
            if (MinClass >= 0 && (c.Windex ?? -1) < MinClass) return false;
            if (!InArea(c.Water, c.X, c.Z)) return false;
            if (Search.Length > 0)
            {
                string s = Search.Trim();
                if (!(c.Fish + " " + c.Bait + " " + c.Hook + " " + c.Leader + " " + c.Groundbait + " " + c.WaterName + " " + c.Rod).Contains(s, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        public bool Ok(NuCastRow k, DateTime now)
        {
            if (Water.HasValue && k.Water != Water) return false;
            if (From(now) is DateTime f && (k.Time ?? DateTime.MinValue) < f) return false;
            if (!InArea(k.Water, k.X, k.Z)) return false;
            return true;
        }

        /// <summary>Events of a cast that pass the fish and class filters.</summary>
        public IEnumerable<(string type, uint? fish, int? windex)> Events(NuCastRow k) =>
            k.Events.Where(e => (FishName.Length == 0 || e.fish.HasValue && FishIds.Contains(e.fish.Value)) && (MinClass < 0 || e.type != "catch" || (e.windex ?? -1) >= MinClass))
                    .Select(e => (e.type, e.fish, e.windex));
    }

    /// <summary>Numbers shown in the statistics; pure functions (tested).</summary>
    public static class NuStatsCalc
    {
        public sealed class TackleRow
        {
            public string Key = ""; public int Casts, Bites, Empty, Caught, EvCaught, Big, BestWeight; public string BestFish = "", Fish = "";
            /// <summary>Share of recorded bites that ended with a fish — events only (old catches without events are not in it), at most 100 %.</summary>
            public double Realize => Bites + Empty == 0 ? 0 : Math.Min(1.0, (double)EvCaught / (Bites + Empty));
            public double BitesPerCast => Casts == 0 ? 0 : (double)(Bites + Empty) / Casts;
        }

        public static string KeyOf(int group, string bait, string hook, string leader, string gb, string rod) => group switch
        {
            1 => hook, 2 => leader, 3 => gb, 4 => rod, _ => bait
        };

        public static List<TackleRow> Tackle(NuStatsData d, NuFilter f, int group, DateTime now, Func<uint?, string> fishName)
        {
            var rows = new Dictionary<string, TackleRow>();
            TackleRow Row(string k) { if (k.Length == 0) k = "не записано"; if (!rows.TryGetValue(k, out var r)) rows[k] = r = new TackleRow { Key = k }; return r; }
            foreach (var k in d.Casts.Where(k => f.Ok(k, now)))
            {
                var r = Row(KeyOf(group, k.Bait, k.Hook, k.Leader, k.Groundbait, k.Rod)); r.Casts++;
                foreach (var e in f.Events(k))
                {
                    if (e.type == "bite") r.Bites++; else if (e.type == "empty_bite") r.Empty++;
                    else if (e.type == "catch") { r.Caught++; r.EvCaught++; if ((e.windex ?? -1) >= 2) r.Big++; }
                }
            }
            foreach (var g in d.Catches.Where(c => f.Ok(c, now)).GroupBy(c => KeyOf(group, c.Bait, c.Hook, c.Leader, c.Groundbait, c.Rod)))
            {
                var r = Row(g.Key); var best = g.OrderByDescending(c => c.Weight).First();
                r.BestWeight = best.Weight; r.BestFish = best.Fish;
                r.Fish = string.Join(", ", g.GroupBy(c => c.Fish).OrderByDescending(x => x.Count()).Take(3).Select(x => x.Key.ToLowerInvariant() + " " + x.Count()));
                r.Caught = Math.Max(r.Caught, g.Count());      // catches recorded without a cast event (old data) still count
                r.Big = Math.Max(r.Big, g.Count(c => (c.Windex ?? -1) >= 2));   // class lives in the catches table
            }
            return rows.Values.Where(r => r.Casts > 0 || r.Caught > 0).OrderByDescending(r => r.Caught).ThenByDescending(r => r.Bites).ToList();
        }

        /// <summary>Per game hour 0..23: bites (incl. empty) and catches.</summary>
        public static (int[] bites, int[] caught) ByGameHour(NuStatsData d, NuFilter f, DateTime now)
        {
            var b = new int[24]; var c = new int[24];
            foreach (var k in d.Casts.Where(k => f.Ok(k, now)))
                foreach (var e in k.Events.Where(e => e.type is "bite" or "empty_bite" && (f.FishName.Length == 0 || e.fish.HasValue && f.FishIds.Contains(e.fish.Value))))
                {
                    var at = e.at ?? k.Time; if (at == null) continue;   // the hour of the bite itself (a game hour is 5 real minutes)
                    b[(int)NuStatsData.GameHourOf(at.Value) % 24]++;
                }
            foreach (var x in d.Catches.Where(x => f.Ok(x, now) && x.GameHour.HasValue)) c[(int)x.GameHour!.Value % 24]++;
            return (b, c);
        }

        /// <summary>Per real day (last n days ending today): catches and big+.</summary>
        public static (string[] labels, int[] caught, int[] big) ByDay(NuStatsData d, NuFilter f, DateTime now, int n)
        {
            var labels = new string[n]; var c = new int[n]; var big = new int[n];
            for (int i = 0; i < n; i++) labels[i] = now.Date.AddDays(i - n + 1).ToString("dd.MM", CultureInfo.InvariantCulture);
            foreach (var x in d.Catches.Where(x => f.Ok(x, now) && x.Time.HasValue))
            {
                int i = (int)(x.Time!.Value.Date - now.Date.AddDays(-n + 1)).TotalDays;
                if (i < 0 || i >= n) continue; c[i]++; if ((x.Windex ?? -1) >= 2) big[i]++;
            }
            return (labels, c, big);
        }

        public static readonly double[] SoakEdges = { 0, 1, 2, 3, 5, 8, 12, 20, 30, 1e9 };
        /// <summary>Minutes in the water before the first bite: bites, catches per bucket.</summary>
        public static (string[] labels, int[] bites, int[] caught) BySoak(NuStatsData d, NuFilter f, DateTime now)
        {
            int n = SoakEdges.Length - 1; var labels = new string[n]; var b = new int[n]; var c = new int[n];
            for (int i = 0; i < n; i++) labels[i] = i == n - 1 ? "30+" : SoakEdges[i] + "–" + SoakEdges[i + 1];
            int Bucket(double m) { for (int i = 0; i < n; i++) if (m < SoakEdges[i + 1]) return i; return n - 1; }
            foreach (var k in d.Casts.Where(k => f.Ok(k, now) && k.SoakMin.HasValue))
            {
                var ev = f.Events(k).ToList(); if (ev.Count == 0) continue;
                int i = Bucket(k.SoakMin!.Value);
                if (ev.Any(e => e.type is "bite" or "empty_bite")) b[i]++;
                if (ev.Any(e => e.type == "catch")) c[i]++;
            }
            return (labels, b, c);
        }

        public static readonly double[] DepthEdges = { 0, 0.5, 1, 1.5, 2, 2.5, 3, 4, 5, 7, 10, 1e9 };
        public static (string[] labels, int[] casts, int[] caught) ByDepth(NuStatsData d, NuFilter f, DateTime now)
        {
            int n = DepthEdges.Length - 1; var labels = new string[n]; var k1 = new int[n]; var c = new int[n];
            var ru = CultureInfo.GetCultureInfo("ru-RU");
            for (int i = 0; i < n; i++) labels[i] = i == n - 1 ? "10+" : DepthEdges[i].ToString("0.#", ru) + "–" + DepthEdges[i + 1].ToString("0.#", ru);
            int Bucket(double m) { for (int i = 0; i < n; i++) if (m < DepthEdges[i + 1]) return i; return n - 1; }
            foreach (var k in d.Casts.Where(k => f.Ok(k, now) && k.DepthM.HasValue)) k1[Bucket(k.DepthM!.Value)]++;
            foreach (var x in d.Catches.Where(x => f.Ok(x, now) && x.DepthM.HasValue)) c[Bucket(x.DepthM!.Value)]++;
            return (labels, k1, c);
        }
    }

    // ================================================================== controls

    /// <summary>Segmented switch (one choice).</summary>
    public sealed class NuSeg : Control
    {
        private string[] _items = Array.Empty<string>();
        private int _sel;
        public event Action<int>? Changed;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string[] Items { get => _items; set { _items = value; Width = Measure(); Invalidate(); } }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Selected { get => _sel; set { if (_sel == value) return; _sel = value; Invalidate(); Changed?.Invoke(value); } }
        public NuSeg() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); Height = 34; Font = NuTheme.F(9.5f); Cursor = Cursors.Hand; Margin = new Padding(0, 0, 10, 0); }
        private int[] Widths() => _items.Select(s => TextRenderer.MeasureText(s, Font).Width + 22).ToArray();
        private int Measure() => Widths().Sum() + 6;
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Parent?.BackColor ?? NuTheme.Ground);
            using (var path = NuTheme.Round(new Rectangle(0, 0, Width - 1, Height - 1), 9)) { using var b = new SolidBrush(NuTheme.Panel); using var p = new Pen(NuTheme.Line); g.FillPath(b, path); g.DrawPath(p, path); }
            int x = 3; var w = Widths();
            for (int i = 0; i < _items.Length; i++)
            {
                var r = new Rectangle(x, 3, w[i], Height - 7);
                if (i == _sel) using (var path = NuTheme.Round(r, 7)) { using var b = new SolidBrush(NuTheme.AccentDeep); using var p = new Pen(Color.FromArgb(0x5A, 0x3A, 0x20)); g.FillPath(b, path); g.DrawPath(p, path); }
                TextRenderer.DrawText(g, _items[i], Font, r, i == _sel ? NuTheme.AccentText : NuTheme.Soft, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                x += w[i];
            }
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            int x = 3; var w = Widths();
            for (int i = 0; i < w.Length; i++) { if (e.X >= x && e.X < x + w[i]) { Selected = i; return; } x += w[i]; }
        }
    }

    /// <summary>Bar chart: optional back series (light) and front series (accent), hover value.</summary>
    public sealed class NuBarChart : Control
    {
        public string Title = "", BackName = "", FrontName = "", Note = "";
        public string[] Labels = Array.Empty<string>();
        public int[]? Back; public int[] Front = Array.Empty<int>();
        public Color FrontColor = NuTheme.Accent;
        public int LabelEvery = 1;
        private int _hover = -1;
        public NuBarChart() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); BackColor = NuTheme.Panel; Margin = new Padding(0, 0, 0, 12); }
        private Rectangle Plot => new(48, 44, Math.Max(10, Width - 64), Math.Max(10, Height - 44 - 30));
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int n = Labels.Length; var p = Plot; int i = n == 0 ? -1 : (int)Math.Floor((e.X - p.X) / (p.Width / (double)n));
            if (i < 0 || i >= n || e.Y < p.Y - 10 || e.Y > p.Bottom + 24) i = -1;
            if (i != _hover) { _hover = i; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(BackColor);
            using var ft = NuTheme.F(10.5f, FontStyle.Bold); using var f = NuTheme.F(8.5f); using var fn = NuTheme.F(8.5f);
            TextRenderer.DrawText(g, Title, ft, new Point(14, 12), NuTheme.Text, TextFormatFlags.NoPadding);
            // legend
            int lx = 14 + TextRenderer.MeasureText(g, Title, ft, Size.Empty, TextFormatFlags.NoPadding).Width + 18;
            void Leg(Color c, string t) { if (t.Length == 0) return; using (var b = new SolidBrush(c)) g.FillRectangle(b, lx, 17, 10, 10); TextRenderer.DrawText(g, t, f, new Point(lx + 14, 15), NuTheme.Soft, TextFormatFlags.NoPadding); lx += 28 + TextRenderer.MeasureText(g, t, f, Size.Empty, TextFormatFlags.NoPadding).Width; }
            if (Back != null) Leg(NuTheme.Line2, BackName); Leg(FrontColor, FrontName);
            if (Note.Length > 0) TextRenderer.DrawText(g, Note, fn, new Point(lx + 6, 15), NuTheme.Muted, TextFormatFlags.NoPadding);
            var p = Plot; int n = Labels.Length; if (n == 0) return;
            int max = Math.Max(1, Math.Max(Back?.DefaultIfEmpty(0).Max() ?? 0, Front.DefaultIfEmpty(0).Max()));
            double step = MapCanvas.NiceStep(max / 4.0); double top = Math.Ceiling(max / step) * step;
            using (var grid = new Pen(Color.FromArgb(40, 255, 255, 255)))
                for (double v = 0; v <= top + 1e-9; v += step)
                {
                    int y = p.Bottom - (int)(v / top * p.Height);
                    g.DrawLine(grid, p.X, y, p.Right, y);
                    TextRenderer.DrawText(g, v.ToString("0", CultureInfo.InvariantCulture), f, new Rectangle(0, y - 8, p.X - 8, 16), NuTheme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }
            double bw = p.Width / (double)n;
            for (int i = 0; i < n; i++)
            {
                float x = (float)(p.X + i * bw + bw * 0.14), w = (float)(bw * 0.72);
                if (i == _hover) using (var hb = new SolidBrush(Color.FromArgb(30, 255, 255, 255))) g.FillRectangle(hb, (float)(p.X + i * bw), p.Y - 6, (float)bw, p.Height + 6);
                if (Back != null && i < Back.Length && Back[i] > 0) { float h = (float)(Back[i] / top * p.Height); using var b = new SolidBrush(NuTheme.Line2); g.FillRectangle(b, x, p.Bottom - h, w, h); }
                if (i < Front.Length && Front[i] > 0) { float h = (float)(Front[i] / top * p.Height); float fw = Back != null ? w * 0.6f : w; using var b = new SolidBrush(FrontColor); g.FillRectangle(b, x + (w - fw) / 2, p.Bottom - h, fw, h); }
                if (i % Math.Max(1, LabelEvery) == 0 || i == _hover)
                {
                    var lr = new Rectangle((int)(p.X + i * bw) - 20, p.Bottom + 6, (int)bw + 40, 16);
                    TextRenderer.DrawText(g, Labels[i], f, lr, i == _hover ? NuTheme.Text : NuTheme.Muted, TextFormatFlags.HorizontalCenter);
                }
            }
            if (_hover >= 0)
            {
                string t = Labels[_hover] + ":  " + (Back != null ? BackName + " " + (Back.Length > _hover ? Back[_hover] : 0) + ",  " : "") + FrontName + " " + (Front.Length > _hover ? Front[_hover] : 0);
                var sz = TextRenderer.MeasureText(g, t, f);
                int tx = Math.Clamp((int)(p.X + _hover * bw + bw / 2 - sz.Width / 2), 4, Width - sz.Width - 12);
                var r = new Rectangle(tx, p.Y - 2, sz.Width + 12, sz.Height + 6);
                using (var path = NuTheme.Round(r, 6)) { using var b = new SolidBrush(NuTheme.Bar); using var pn = new Pen(NuTheme.Line2); g.FillPath(b, path); g.DrawPath(pn, path); }
                TextRenderer.DrawText(g, t, f, new Point(r.X + 6, r.Y + 3), NuTheme.Text);
            }
        }
    }

    /// <summary>Summary tiles.</summary>
    public sealed class NuTiles : Control
    {
        public List<(string value, string label, Color color)> Items = new();
        public int[] Classes = new int[4]; public int NoClass;
        public NuTiles() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); Height = 150; BackColor = NuTheme.Ground; Margin = new Padding(0, 0, 0, 12); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(BackColor);
            using var fv = NuTheme.F(17f, FontStyle.Bold); using var fl = NuTheme.F(9f);
            int n = Math.Max(1, Items.Count), gap = 10, w = (Width - gap * (n - 1)) / n;
            for (int i = 0; i < Items.Count; i++)
            {
                var r = new Rectangle(i * (w + gap), 0, w, 84);
                using (var path = NuTheme.Round(r, 12)) { using var b = new SolidBrush(NuTheme.Panel); g.FillPath(b, path); }
                TextRenderer.DrawText(g, Items[i].value, fv, new Point(r.X + 16, r.Y + 14), Items[i].color, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, Items[i].label, fl, new Rectangle(r.X + 16, r.Y + 52, r.Width - 24, 20), NuTheme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            // class bar
            int tot = Classes.Sum() + NoClass; var bar = new Rectangle(0, 100, Width, 14);
            using (var path = NuTheme.Round(bar, 7)) { using var b = new SolidBrush(NuTheme.Panel); g.FillPath(b, path); }
            if (tot > 0)
            {
                Color[] cols = { Color.FromArgb(150, 150, 150), NuTheme.Ok, Color.FromArgb(0x5D, 0xB3, 0xFF), Color.FromArgb(0xF5, 0xC4, 0x51) };
                string[] names = { "обычные", "хорошие", "крупные", "трофеи" };
                float x = 0; using var old = g.Clip;
                using (var path = NuTheme.Round(bar, 7)) g.SetClip(path);
                for (int i = 0; i < 4; i++) { float w2 = (float)Classes[i] / tot * Width; using var b = new SolidBrush(cols[i]); g.FillRectangle(b, x, bar.Y, w2, bar.Height); x += w2; }
                g.Clip = old;
                int lx = 0;
                for (int i = 0; i < 4; i++)
                {
                    using (var b = new SolidBrush(cols[i])) g.FillEllipse(b, lx, 126, 10, 10);
                    string t = names[i] + " " + Classes[i] + " (" + Math.Round(100.0 * Classes[i] / tot) + " %)";
                    TextRenderer.DrawText(g, t, fl, new Point(lx + 14, 123), NuTheme.Soft, TextFormatFlags.NoPadding);
                    lx += 30 + TextRenderer.MeasureText(g, t, fl, Size.Empty, TextFormatFlags.NoPadding).Width;
                }
                if (NoClass > 0) TextRenderer.DrawText(g, "класс не известен " + NoClass, fl, new Point(lx, 123), NuTheme.Muted, TextFormatFlags.NoPadding);
            }
        }
    }

    public static class NuGrid
    {
        public static DataGridView Make()
        {
            var g = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
                BackgroundColor = NuTheme.Ground, BorderStyle = BorderStyle.None, GridColor = NuTheme.Line, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                EnableHeadersVisualStyles = false, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, ColumnHeadersHeight = 34,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, Font = NuFonts.Get(9.5f)
            };
            g.CellFormatting += (s, e) => { if (e.Value is double dv && double.IsNaN(dv)) { e.Value = "—"; e.FormattingApplied = true; } };
            g.RowTemplate.Height = 30;
            g.DefaultCellStyle.BackColor = NuTheme.Ground; g.DefaultCellStyle.ForeColor = NuTheme.Text;
            g.DefaultCellStyle.SelectionBackColor = NuTheme.Raised; g.DefaultCellStyle.SelectionForeColor = NuTheme.Text;
            g.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
            g.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(0x12, 0x1F, 0x26);
            g.ColumnHeadersDefaultCellStyle.BackColor = NuTheme.Panel; g.ColumnHeadersDefaultCellStyle.ForeColor = NuTheme.Muted;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = NuTheme.Panel; g.ColumnHeadersDefaultCellStyle.Font = NuFonts.Get(9f, FontStyle.Bold);
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(g, true);
            return g;
        }

        public static DataGridViewTextBoxColumn Col(string name, float weight, bool number = false)
        {
            var c = new DataGridViewTextBoxColumn { HeaderText = name, FillWeight = weight, SortMode = DataGridViewColumnSortMode.Automatic };
            if (number) { c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight; c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight; }
            return c;
        }
    }

    /// <summary>Journal and statistics view (one filter bar).</summary>
    public sealed class NuStatsView : Panel
    {
        public Func<NuStatsData>? Loader;
        public Action<NuCatchRow>? ShowOnMap;
        public Action? OpenLegacyJournal;
        /// <summary>ui01_review3 #7: id of a fish by its name (game reference), so bites of a fish whose catches have no id still count.</summary>
        public Func<string, uint?>? FishIdByName;
        public NuFilter Filter { get; } = new();
        public NuStatsData? Data { get; private set; }
        public bool JournalMode { get; private set; }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Section { get => _section.Selected; set => _section.Selected = value; }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int TackleGroup { get => _group.Selected; set => _group.Selected = value; }

        private readonly Panel _top = new() { Dock = DockStyle.Top, Height = 124, Padding = new Padding(24, 18, 24, 0) };
        private readonly Label _title = new() { AutoSize = true, Font = NuTheme.F(15f, FontStyle.Bold), ForeColor = NuTheme.Text, Location = new Point(24, 16) };
        private readonly Label _count = new() { AutoSize = true, Font = NuTheme.F(10f), ForeColor = NuTheme.Muted };
        private readonly Label _area = new() { AutoSize = true, Font = NuTheme.F(9.5f), ForeColor = NuTheme.AccentText, BackColor = NuTheme.AccentDeep, Cursor = Cursors.Hand, Visible = false, Padding = new Padding(8, 4, 8, 4) };
        private readonly TextBox _search = new() { Width = 260, BorderStyle = BorderStyle.FixedSingle, BackColor = NuTheme.Raised, ForeColor = NuTheme.Text, Font = NuTheme.F(10f), PlaceholderText = "Рыба, наживка, крючок, водоём…" };
        private readonly ComboBox _water = Combo(220), _fish = Combo(190), _period = Combo(130), _class = Combo(150);
        private readonly NuSeg _section = new() { Items = new[] { "Сводка и рекорды", "Графики", "Снасти", "Места и глубины" } };
        private readonly NuSeg _group = new() { Items = new[] { "Наживка", "Крючок", "Поводок", "Прикормка", "Удилище" } };
        private readonly Label _legacy = new() { AutoSize = true, Text = "Прежний журнал (удаление дублей)", ForeColor = NuTheme.Muted, Font = NuTheme.F(9f, FontStyle.Underline), Cursor = Cursors.Hand };
        private readonly Panel _body = new() { Dock = DockStyle.Fill, Padding = new Padding(24, 8, 24, 16) };
        private readonly DataGridView _journal = NuGrid.Make();
        private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 26, ForeColor = NuTheme.Muted, Font = NuTheme.F(9f), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(24, 0, 0, 0) };
        private bool _loading, _filling;
        private readonly System.Windows.Forms.Timer _reload = new() { Interval = 15000 };
        private readonly System.Windows.Forms.Timer _searchDelay = new() { Interval = 300 };

        private static ComboBox Combo(int w)
        {
            var c = new ComboBox { Width = w, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = NuTheme.Raised, ForeColor = NuTheme.Text, Font = NuTheme.F(9.5f),
                Margin = new Padding(0, 0, 10, 0), DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 24, MaxDropDownItems = 16 };
            c.DrawItem += (s, e) =>
            {
                bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0, sel = (e.State & DrawItemState.Selected) != 0 && !edit;
                using (var b = new SolidBrush(sel ? NuTheme.AccentDeep : NuTheme.Raised)) e.Graphics.FillRectangle(b, e.Bounds);
                if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, c.Items[e.Index]?.ToString() ?? "", c.Font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height),
                    sel ? NuTheme.AccentText : NuTheme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            return c;
        }

        public NuStatsView()
        {
            BackColor = NuTheme.Ground; Dock = DockStyle.Fill; DoubleBuffered = true;
            _top.BackColor = NuTheme.Ground; _body.BackColor = NuTheme.Ground; _status.BackColor = NuTheme.Ground;
            _top.Controls.Add(_title); _top.Controls.Add(_count); _top.Controls.Add(_area);
            _area.Click += (s, e) => { Filter.Area = null; _area.Visible = false; Refill(); };
            var row = new FlowLayoutPanel { Location = new Point(24, 58), AutoSize = true, WrapContents = false, BackColor = NuTheme.Ground };
            row.Controls.AddRange(new Control[] { _water, _fish, _period, _class, _search });
            _top.Controls.Add(row);
            _top.Controls.Add(_legacy);
            _period.Items.AddRange(new object[] { "Сегодня", "7 дней", "30 дней", "Всё время" }); _period.SelectedIndex = 3;
            _class.Items.AddRange(new object[] { "Все классы", "Хорошие и выше", "Крупные и выше", "Только трофеи" }); _class.SelectedIndex = 0;
            _water.SelectedIndexChanged += (s, e) => { if (_filling) return; Filter.Water = (_water.SelectedItem as Opt)?.Id; if (Filter.Area is { } ar && ar.water != Filter.Water) Filter.Area = null; FillFish(); Refill(); };
            _fish.SelectedIndexChanged += (s, e) => { if (_filling) return; if (_fish.SelectedItem is Opt o) ApplyFish(o); Refill(); };
            _period.SelectedIndexChanged += (s, e) => { Filter.Period = _period.SelectedIndex; Refill(); };
            _class.SelectedIndexChanged += (s, e) => { Filter.MinClass = _class.SelectedIndex == 0 ? -1 : _class.SelectedIndex; Refill(); };
            _searchDelay.Tick += (s, e) => { _searchDelay.Stop(); Filter.Search = _search.Text; Refill(); };
            _search.TextChanged += (s, e) => { _searchDelay.Stop(); _searchDelay.Start(); };
            _section.Changed += i => Refill(); _group.Changed += i => Refill();
            _legacy.Click += (s, e) => OpenLegacyJournal?.Invoke();
            _journal.Columns.AddRange(NuGrid.Col("Время", 90), NuGrid.Col("Водоём", 120), NuGrid.Col("Рыба", 110), NuGrid.Col("Вес, г", 60, true), NuGrid.Col("Класс", 70),
                NuGrid.Col("Наживка", 110), NuGrid.Col("Крючок", 100), NuGrid.Col("Поводок", 90), NuGrid.Col("Прикормка", 110), NuGrid.Col("Глубина", 60, true), NuGrid.Col("Клетка", 70));
            _journal.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && _journal.Rows[e.RowIndex].Tag is NuCatchRow c) ShowOnMap?.Invoke(c); };
            _journal.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || _journal.Rows[e.RowIndex].Tag is not NuCatchRow c) return;
                if (e.ColumnIndex is 2 or 4) e.CellStyle!.ForeColor = c.Windex switch { 3 => Color.FromArgb(0xF5, 0xC4, 0x51), 2 => Color.FromArgb(0x5D, 0xB3, 0xFF), 1 => NuTheme.Ok, _ => NuTheme.Soft };
            };
            Controls.Add(_body); Controls.Add(_status); Controls.Add(_top);
            _top.Resize += (s, e) => PlaceTop();
            _reload.Tick += (s, e) => { if (Visible) Reload(false); };
            VisibleChanged += (s, e) => { if (Visible) { Reload(false); _reload.Start(); } else _reload.Stop(); };
        }

        private void PlaceTop()
        {
            // ui01_review3 #21: the filter row must fit at the minimum window width
            int avail = Math.Max(400, _top.Width - 48 - 10 * 5);
            int[] full = { 220, 190, 130, 150, 260 }, min = { 150, 130, 100, 120, 140 };
            double k = Math.Clamp((avail - min.Sum()) / (double)Math.Max(1, full.Sum() - min.Sum()), 0, 1);
            var cs = new Control[] { _water, _fish, _period, _class, _search };
            for (int i = 0; i < cs.Length; i++) { int w = (int)(min[i] + (full[i] - min[i]) * k); if (cs[i].Width != w) cs[i].Width = w; }
            _count.Location = new Point(_title.Right + 12, 24);
            _area.Location = new Point(_count.Right + 14, 19);
            _legacy.Location = new Point(_top.Width - _legacy.Width - 24, 24);
        }

        /// <summary>UI-02/5: the journal of one place of the map: water + circle, fish and class from the map filter, all time.</summary>
        public void ShowPlace(uint water, double x, double z, double r, string label, string fish, int minClass, int period)
        {
            Filter.Area = (water, x, z, r, label);
            Filter.Period = Math.Clamp(period, 0, 3); _period.SelectedIndex = Filter.Period;
            Filter.MinClass = minClass is 1 or 2 or 3 ? minClass : -1; _class.SelectedIndex = Math.Max(0, Filter.MinClass);
            if (_search.Text.Length > 0) { _searchDelay.Stop(); _search.Text = ""; Filter.Search = ""; }
            Filter.Water = water;
            if (Data != null)
            {
                _filling = true;
                _water.SelectedItem = _water.Items.Cast<Opt>().FirstOrDefault(o => o.Id == water) ?? _water.SelectedItem;
                Filter.Water = water;
                _filling = false;
                FillFish();
                _filling = true;
                var fo = _fish.Items.Cast<Opt>().Skip(1).FirstOrDefault(o => fish.Length > 0 && o.Name == fish);
                _fish.SelectedItem = fo ?? _fish.Items[0]; ApplyFish((Opt)_fish.SelectedItem!);
                _filling = false;
            }
            else { Filter.FishName = fish; }
            _area.Text = "Место: " + label + "   ✕";
            _area.Visible = true; PlaceTop();
            Refill();
        }

        public void SetMode(bool journal)
        {
            JournalMode = journal;
            if (!journal && (Filter.Search.Length > 0 || _search.Text.Length > 0)) { _searchDelay.Stop(); _search.Text = ""; Filter.Search = ""; }   // hidden search must not filter the statistics
            _title.Text = journal ? "Журнал поимок" : "Статистика";
            _search.Visible = journal; _legacy.Visible = journal;
            PlaceTop(); Refill();
        }

        public void Reload(bool force)
        {
            if (Loader == null || _loading) return;
            string db = AppPaths.CatchDb;
            if (!force && Data != null && Data.Stamp.Length > 0 && Data.Stamp == NuStatsData.StampOf(db)) return;
            _loading = true; if (Data == null) _status.Text = "Загружаю уловы…";
            var load = Loader;
            Task.Run(() => load()).ContinueWith(t =>
            {
                _loading = false;
                if (IsDisposed) return;
                if (t.Exception != null) { _status.Text = "Не удалось прочитать базу: " + t.Exception.InnerException?.Message; return; }
                SetData(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public event Action? DataChanged;

        public void SetData(NuStatsData d)
        {
            Data = d; _filling = true;
            var keepW = Filter.Water;
            _water.Items.Clear(); _water.Items.Add(new Opt(null, "Все водоёмы"));
            foreach (var g in d.Catches.Where(c => c.Water.HasValue).GroupBy(c => c.Water!.Value).OrderByDescending(g => g.Count()))
                _water.Items.Add(new Opt(g.Key, g.First().WaterName + "  (" + g.Count() + ")"));
            _water.SelectedItem = _water.Items.Cast<Opt>().FirstOrDefault(o => o.Id == keepW) ?? _water.Items[0];
            Filter.Water = (_water.SelectedItem as Opt)?.Id;
            _filling = false;
            FillFish(); Refill();
            DataChanged?.Invoke();
        }

        private void FillFish()
        {
            if (Data == null) return;
            _filling = true;
            var keep = Filter.Fish; var keepName = Filter.FishName;
            _fish.Items.Clear(); _fish.Items.Add(new Opt(null, "Все рыбы"));
            foreach (var g in Data.Catches.Where(c => !Filter.Water.HasValue || c.Water == Filter.Water).GroupBy(c => c.Fish).OrderByDescending(g => g.Count()))
                {
                var ids = g.Where(c => c.FishId.HasValue).Select(c => c.FishId!.Value).ToHashSet();
                try { if (FishIdByName?.Invoke(g.Key) is uint byName) ids.Add(byName); } catch { }
                _fish.Items.Add(new Opt(null, g.Key) { Label = g.Key + "  (" + g.Count() + ")", Ids = ids });
            }
            _fish.SelectedItem = _fish.Items.Cast<Opt>().Skip(1).FirstOrDefault(o => keepName.Length > 0 && o.Name == keepName) ?? _fish.Items[0];
            ApplyFish((Opt)_fish.SelectedItem!);
            _filling = false;
        }

        private void ApplyFish(Opt o)
        {
            bool all = _fish.Items.Count == 0 || ReferenceEquals(o, _fish.Items[0]);
            Filter.Fish = null; Filter.FishName = all ? "" : o.Name; Filter.FishIds = all ? new HashSet<uint>() : new HashSet<uint>(o.Ids);
        }

        private sealed class Opt
        {
            public uint? Id; public string Name; public string? Label; public HashSet<uint> Ids = new();
            public Opt(uint? id, string name) { Id = id; Name = name; }
            public override string ToString() => Label ?? Name;
        }

        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
        private static string ClassName(int? w) => w switch { 3 => "трофей", 2 => "крупный", 1 => "хороший", 0 => "обычный", _ => "—" };
        private static string Kg(long g) => g >= 1000 ? (g / 1000.0).ToString("0.0", Ru) + " кг" : g + " г";

        public void Refill()
        {
            if (_filling) return;
            var d = Data; var now = DateTime.Now;
            _body.SuspendLayout();
            _section.Parent?.Controls.Remove(_section); _group.Parent?.Controls.Remove(_group); _journal.Parent?.Controls.Remove(_journal);
            foreach (Control c in _body.Controls.Cast<Control>().ToList()) c.Dispose();
            _body.Controls.Clear();
            if (d == null) { _body.ResumeLayout(); return; }
            var catches = d.Catches.Where(c => Filter.Ok(c, now)).ToList();
            _count.Text = catches.Count + " " + Plural(catches.Count, "рыба", "рыбы", "рыб") + (catches.Count > 0 ? " · " + Kg(catches.Sum(c => (long)c.Weight)) : "");
            _area.Visible = Filter.Area != null; PlaceTop();
            if (JournalMode) FillJournal(catches);
            else
            {
                var head = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, BackColor = NuTheme.Ground, WrapContents = false };
                head.Controls.Add(_section);
                if (_section.Selected == 2) head.Controls.Add(_group);
                Control content = _section.Selected switch { 1 => Charts(d, now), 2 => TackleGrid(d, now), 3 => Places(d, catches, now), _ => Summary(d, catches, now) };
                _body.Controls.Add(content); _body.Controls.Add(head);
            }
            _status.Text = d.Errors.Count > 0 ? "Часть данных не прочитана: " + string.Join("; ", d.Errors)
                : JournalMode ? (catches.Count > JournalMax ? "Показаны последние " + JournalMax + " из " + catches.Count + " — сузьте фильтр. " : "") + "Двойной щелчок по строке — показать место на карте"
                : "Поклёвки и уловы — сколько записано событий (частота), а не вероятность.";
            _body.ResumeLayout();
        }

        private static string Plural(int n, string one, string few, string many) { int m = n % 100, k = n % 10; return m is >= 11 and <= 14 ? many : k == 1 ? one : k is >= 2 and <= 4 ? few : many; }

        public const int JournalMax = 3000;
        private void FillJournal(List<NuCatchRow> catches)
        {
            int first = _journal.Rows.Count > 0 ? Math.Max(0, _journal.FirstDisplayedScrollingRowIndex) : 0;
            var sortCol = _journal.SortedColumn; var sortDir = _journal.SortOrder;
            _journal.SuspendLayout(); _journal.Rows.Clear();
            _journal.Columns[9].DefaultCellStyle.Format = "0.0"; _journal.Columns[9].DefaultCellStyle.FormatProvider = Ru;
            foreach (var c in catches.Take(JournalMax))
            {
                int i = _journal.Rows.Add(c.Time ?? DateTime.MinValue, c.WaterName, c.Fish, c.Weight, ClassName(c.Windex), c.Bait, c.Hook, c.Leader, c.Groundbait,
                    c.DepthM ?? double.NaN, c.Cell);
                _journal.Rows[i].Tag = c;
            }
            _journal.Columns[0].DefaultCellStyle.Format = "dd.MM HH:mm";
            if (sortCol != null && sortDir != SortOrder.None) _journal.Sort(sortCol, sortDir == SortOrder.Ascending ? System.ComponentModel.ListSortDirection.Ascending : System.ComponentModel.ListSortDirection.Descending);
            _journal.ResumeLayout();
            _body.Controls.Add(_journal);
            if (first > 0 && first < _journal.Rows.Count) try { _journal.FirstDisplayedScrollingRowIndex = first; } catch { }
        }

        private Control Summary(NuStatsData d, List<NuCatchRow> catches, DateTime now)
        {
            var casts = d.Casts.Where(k => Filter.Ok(k, now)).ToList();
            int bites = 0, empty = 0, caughtEv = 0;
            foreach (var k in casts) foreach (var e in Filter.Events(k)) { if (e.type == "bite") bites++; else if (e.type == "empty_bite") empty++; else if (e.type == "catch") caughtEv++; }
            var tiles = new NuTiles { Dock = DockStyle.Top };
            tiles.Items.Add((catches.Count.ToString(Ru), "рыб поймано", NuTheme.Text));
            tiles.Items.Add((Kg(catches.Sum(c => (long)c.Weight)), "общий вес", NuTheme.Text));
            tiles.Items.Add((catches.Count(c => c.Windex >= 2).ToString(Ru), "крупных и трофеев", Color.FromArgb(0x5D, 0xB3, 0xFF)));
            tiles.Items.Add((casts.Count.ToString(Ru), "забросов записано", NuTheme.Text));
            tiles.Items.Add(((bites + empty).ToString(Ru), "поклёвок (пустых " + empty + ")", NuTheme.Text));
            // fixr3_review #1: with a class filter the bites are not filtered by class — no honest share, show «—»
            tiles.Items.Add((bites + empty == 0 || Filter.MinClass >= 0 ? "—" : Math.Min(100, Math.Round(100.0 * caughtEv / (bites + empty))) + " %",
                Filter.MinClass >= 0 ? "поклёвок в рыбу — без фильтра класса" : "поклёвок закончились рыбой", NuTheme.AccentText));
            for (int i = 0; i < 4; i++) tiles.Classes[i] = catches.Count(c => c.Windex == i);
            tiles.NoClass = catches.Count(c => c.Windex == null);
            var bestDay = catches.Where(c => c.Time.HasValue).GroupBy(c => c.Time!.Value.Date).OrderByDescending(g => g.Count()).FirstOrDefault();
            var lbl = new Label { Dock = DockStyle.Top, Height = 34, ForeColor = NuTheme.Soft, Font = NuTheme.F(10f), TextAlign = ContentAlignment.MiddleLeft, BackColor = NuTheme.Ground,
                Text = bestDay == null ? "" : "Лучший день: " + bestDay.Key.ToString("d MMMM", Ru) + " — " + bestDay.Count() + " рыб, " + Kg(bestDay.Sum(c => (long)c.Weight)) + ".   Рекорды по каждой рыбе:" };
            var grid = NuGrid.Make();
            grid.Columns.AddRange(NuGrid.Col("Рыба", 120), NuGrid.Col("Поймано", 60, true), NuGrid.Col("Рекорд, г", 70, true), NuGrid.Col("Средний, г", 70, true), NuGrid.Col("Крупных+", 60, true),
                NuGrid.Col("Рекорд: когда", 90), NuGrid.Col("на что", 120), NuGrid.Col("где", 140));
            foreach (var g in catches.GroupBy(c => c.Fish).OrderByDescending(g => g.Max(c => c.Weight)))
            {
                var best = g.OrderByDescending(c => c.Weight).First();
                int i = grid.Rows.Add(g.Key, g.Count(), best.Weight, (int)g.Average(c => c.Weight), g.Count(c => c.Windex >= 2), best.Time?.ToString("dd.MM.yy HH:mm", CultureInfo.InvariantCulture) ?? "",
                    best.Bait + (best.Hook.Length > 0 ? " · " + best.Hook : ""), best.WaterName + (best.Cell.Length > 0 ? " · " + best.Cell : ""));
                grid.Rows[i].Tag = best;
            }
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && grid.Rows[e.RowIndex].Tag is NuCatchRow c) ShowOnMap?.Invoke(c); };
            var p = new Panel { Dock = DockStyle.Fill, BackColor = NuTheme.Ground };
            p.Controls.Add(grid); p.Controls.Add(lbl); p.Controls.Add(tiles);
            return p;
        }

        private Control Charts(NuStatsData d, DateTime now)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = NuTheme.Ground };
            for (int i = 0; i < 3; i++) t.RowStyles.Add(new RowStyle(SizeType.Percent, 33.3f));
            var (hb, hc) = NuStatsCalc.ByGameHour(d, Filter, now);
            t.Controls.Add(new NuBarChart { Dock = DockStyle.Fill, Title = "По часам игры", Labels = Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToArray(), Back = hb, Front = hc, BackName = "поклёвки", FrontName = "поймано", LabelEvery = 2, Note = "утро 04:30–07:20, вечер 19:30–21:30" }, 0, 0);
            int days = Filter.Period switch { 0 => 1, 1 => 7, 2 => 30, _ => Math.Clamp((int)Math.Ceiling((now.Date - (d.Catches.Where(c => c.Time.HasValue).Select(c => c.Time!.Value.Date).DefaultIfEmpty(now.Date).Min())).TotalDays) + 1, 7, 730) };
            var (dl, dc, db) = NuStatsCalc.ByDay(d, Filter, now, days);
            bool cut = Filter.Period == 3 && d.Catches.Any(c => c.Time.HasValue && c.Time.Value.Date < now.Date.AddDays(-(days - 1)));
            t.Controls.Add(new NuBarChart { Dock = DockStyle.Fill, Title = "По дням", Note = cut ? "последние " + days + " дней" : "", Labels = dl, Back = dc, Front = db, BackName = "все рыбы", FrontName = "крупные и трофеи", FrontColor = Color.FromArgb(0x5D, 0xB3, 0xFF), LabelEvery = Math.Max(1, days / 14) }, 0, 1);
            var (sl, sb, sc) = NuStatsCalc.BySoak(d, Filter, now);
            t.Controls.Add(new NuBarChart { Dock = DockStyle.Fill, Title = "Сколько минут в воде до первой поклёвки", Labels = sl, Back = sb, Front = sc, BackName = "забросов с поклёвкой", FrontName = "с рыбой" }, 0, 2);
            return t;
        }

        private Control TackleGrid(NuStatsData d, DateTime now)
        {
            var rows = NuStatsCalc.Tackle(d, Filter, _group.Selected, now, id => "");
            var grid = NuGrid.Make();
            grid.Columns.AddRange(NuGrid.Col(_group.Items[_group.Selected], 160), NuGrid.Col("Забросов", 60, true), NuGrid.Col("Поклёвок", 60, true), NuGrid.Col("Пустых", 55, true),
                NuGrid.Col("Поймано", 60, true), NuGrid.Col("Крупных+", 60, true), NuGrid.Col("Поклёвок на заброс", 80, true), NuGrid.Col("Поклёвок в рыбу", 75, true),
                NuGrid.Col("Лучшая", 120), NuGrid.Col("Кто брал", 180));
            grid.Columns[6].DefaultCellStyle.Format = "0.00"; grid.Columns[7].DefaultCellStyle.Format = "0\" %\""; grid.Columns[6].DefaultCellStyle.FormatProvider = Ru;
            foreach (var r in rows)
                grid.Rows.Add(r.Key, r.Casts, r.Bites, r.Empty, r.Caught, r.Big, Math.Round(r.BitesPerCast, 2), r.Bites + r.Empty == 0 || Filter.MinClass >= 0 ? double.NaN : Math.Round(r.Realize * 100),
                    r.BestWeight > 0 ? r.BestFish + " " + r.BestWeight + " г" : "", r.Fish);
            var note = new Label { Dock = DockStyle.Bottom, Height = 28, ForeColor = NuTheme.Muted, Font = NuTheme.F(9f), BackColor = NuTheme.Ground, TextAlign = ContentAlignment.MiddleLeft,
                Text = "Сравнивайте строки с большим числом забросов: при 5–10 забросах разница может быть случайной. Щелчок по заголовку — сортировка." };
            var p = new Panel { Dock = DockStyle.Fill, BackColor = NuTheme.Ground };
            p.Controls.Add(grid); p.Controls.Add(note);
            return p;
        }

        private Control Places(NuStatsData d, List<NuCatchRow> catches, DateTime now)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = NuTheme.Ground };
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 42f)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 58f));
            var (dl, dk, dc) = NuStatsCalc.ByDepth(d, Filter, now);
            t.Controls.Add(new NuBarChart { Dock = DockStyle.Fill, Title = "Глубина дна в месте заброса, м", Labels = dl, Back = dk, Front = dc, BackName = "забросов", FrontName = "поймано", Note = "по карте глубин водоёма" }, 0, 0);
            var grid = NuGrid.Make();
            grid.Columns.AddRange(NuGrid.Col("Водоём", 140), NuGrid.Col("Клетка", 70), NuGrid.Col("Поймано", 60, true), NuGrid.Col("Крупных+", 60, true), NuGrid.Col("Лучшая", 120),
                NuGrid.Col("Глубина", 60, true), NuGrid.Col("Наживки", 160), NuGrid.Col("Последний раз", 90));
            foreach (var g in catches.Where(c => c.Cell.Length > 0).GroupBy(c => (c.Water, c.Cell)).OrderByDescending(g => g.Count()).Take(200))
            {
                var best = g.OrderByDescending(c => c.Weight).First();
                int i = grid.Rows.Add(best.WaterName, g.Key.Cell, g.Count(), g.Count(c => c.Windex >= 2), best.Fish + " " + best.Weight + " г",
                    g.Where(c => c.DepthM.HasValue).Select(c => c.DepthM!.Value).DefaultIfEmpty(double.NaN).Average(),
                    string.Join(", ", g.GroupBy(c => c.Bait).OrderByDescending(x => x.Count()).Take(3).Select(x => x.Key)), g.Max(c => c.Time)?.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture) ?? "");
                grid.Rows[i].Tag = best;
            }
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && grid.Rows[e.RowIndex].Tag is NuCatchRow c) ShowOnMap?.Invoke(c); };
            var p = new Panel { Dock = DockStyle.Fill, BackColor = NuTheme.Ground, Padding = new Padding(0, 4, 0, 0) };
            var lbl = new Label { Dock = DockStyle.Top, Height = 30, Text = "Лучшие клетки (как координаты в игре)", ForeColor = NuTheme.Text, Font = NuTheme.F(10.5f, FontStyle.Bold), BackColor = NuTheme.Ground, TextAlign = ContentAlignment.MiddleLeft };
            p.Controls.Add(grid); p.Controls.Add(lbl);
            t.Controls.Add(p, 0, 1);
            return t;
        }

        protected override void Dispose(bool disposing) { if (disposing) { _reload.Dispose(); _searchDelay.Dispose(); _journal.Dispose(); } base.Dispose(disposing); }
    }
}
