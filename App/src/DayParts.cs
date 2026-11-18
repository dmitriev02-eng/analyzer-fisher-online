using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace FisherLogger
{
    /// <summary>
    /// DAYPART-01: время суток игры и как оно меняет клёв — по вашим же забросам.
    /// Время игры = МСК × 12 (сутки игры = 2 реальных часа, MECH-02). Сервер меняет шанс сразу у всех удочек в 04:30 (рассвет),
    /// ≈07:20 (день), 19:30 (вечер), 21:30 (ночь). Для каждой наживки на каждом водоёме учим, во сколько раз меняется её шанс
    /// на каждой смене: внутри одного заброса (такты до/после) и между соседними забросами той же удочки в ту же точку (≤12 м).
    /// Шанс — про снасть, не про рыбу: какая рыба за этим стоит — по вашим уловам на эту наживку на этом водоёме.
    /// Водоёмы не смешиваются (у каждого своя рыба). Мало замеров — множитель сжимается к 1.
    /// </summary>
    public static class DayParts
    {
        public const double DawnAt = 4.5, DayAt = 7 + 20 / 60.0, EveningAt = 19.5, NightAt = 21.5;
        public static readonly (double At, string Name)[] Changes = { (DawnAt, "рассвет"), (DayAt, "день"), (EveningAt, "вечер"), (NightAt, "ночь") };
        public const int MinEvents = 2;

        public static string Part(double g) => g < DawnAt || g >= NightAt ? "ночь" : g < DayAt ? "рассвет" : g < EveningAt ? "день" : "вечер";

        public static double GameHour(DateTime local)
        {
            DateTime msk = local.Kind == DateTimeKind.Utc ? local.AddHours(3) : local.ToUniversalTime().AddHours(3);
            return GameClock.GameTimeOfDay(msk).TotalHours;
        }

        /// <summary>Сейчас в игре: час, часть суток, следующая смена, через сколько реальных минут и во сколько по часам ПК.</summary>
        public static (double Game, string Part, string Next, double Minutes, DateTime AtLocal) Now(DateTime local)
        {
            double g = GameHour(local);
            var nx = Changes.Select(c => (c, d: ((c.At - g) % 24 + 24) % 24)).Where(t => t.d > 1e-6).OrderBy(t => t.d).First();
            double min = nx.d * 60 / GameClock.DaysPerRealDay;
            return (g, Part(g), nx.c.Name, min, (local.Kind == DateTimeKind.Utc ? local.ToLocalTime() : local).AddMinutes(min));
        }

        public static string Hm(double g) => ((int)g % 24).ToString("00", CultureInfo.InvariantCulture) + ":" + ((int)Math.Round((g % 1) * 60) % 60).ToString("00", CultureInfo.InvariantCulture);

        /// <summary>«В игре 03:50, ночь · рассвет через 3 мин (в 22:22)».</summary>
        public static string Clock(DateTime local)
        {
            var n = Now(local);
            return $"В игре {Hm(n.Game)}, {n.Part} · {n.Next} через {Math.Max(1, Math.Ceiling(n.Minutes)):0} мин (в {n.AtLocal:HH:mm})";
        }

        public sealed class Model
        {
            /// <summary>water|bait → часть суток, в которую переходим → (множитель, замеров).</summary>
            public Dictionary<string, Dictionary<string, (double F, int N)>> F = new();
            /// <summary>water|bait → рыба (по уловам) по убыванию.</summary>
            public Dictionary<string, List<(uint Id, string Name, int N)>> Fish = new();
            public DateTime BuiltAt;
        }

        public static string Key(uint? water, string? bait) => (water?.ToString(CultureInfo.InvariantCulture) ?? "?") + "|" + (bait ?? "").Trim();

        private static Model? _cache; private static string _cacheDb = ""; private static readonly object Sync = new();
        public static Model For(string db, DateTime now)
        {
            lock (Sync)
            {
                if (_cache != null && _cacheDb == db && (now - _cache.BuiltAt).TotalMinutes < 10) return _cache;
                Model m; try { m = Fit(db); } catch { m = new Model(); }
                m.BuiltAt = now; _cache = m; _cacheDb = db; return m;
            }
        }

        public static Model Fit(string db)
        {
            var m = new Model();
            var ticks = new Dictionary<long, List<(DateTime t, double c)>>();
            var cast = new Dictionary<long, (DateTime t, string s, int rod, double? x, double? z)>();
            var bait = new Dictionary<long, string>(); var water = new Dictionary<long, uint>();
            using var c = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False");
            c.Open();
            static DateTime? T(SqliteDataReader r, int i) => !r.IsDBNull(i) && DateTime.TryParse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
            using (var q = new SqliteCommand("SELECT id, timestamp, session_id, rod_index, cast_x, cast_z FROM casts", c))
            using (var r = q.ExecuteReader())
                while (r.Read()) if (T(r, 1) is DateTime t) cast[r.GetInt64(0)] = (t, r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? -1 : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetDouble(4), r.IsDBNull(5) ? null : r.GetDouble(5));
            using (var q = new SqliteCommand("SELECT cast_id, bait_name FROM cast_tackle WHERE cast_id IS NOT NULL AND bait_name IS NOT NULL AND bait_name <> ''", c))
            using (var r = q.ExecuteReader()) while (r.Read()) bait[r.GetInt64(0)] = r.GetString(1);
            using (var q = new SqliteCommand("SELECT cast_id, MAX(location_id) FROM server_checks WHERE cast_id IS NOT NULL AND location_id IS NOT NULL GROUP BY cast_id", c))
            using (var r = q.ExecuteReader())
                while (r.Read()) { long sp = r.GetInt64(1); water[r.GetInt64(0)] = BiteAnalytics.WaterFromSpawn(sp, new Dictionary<long, uint>()) ?? (uint)sp; }
            using (var q = new SqliteCommand("SELECT cast_id, timestamp, chance, raw_text FROM cast_events WHERE event_type = 'wait' AND cast_id IS NOT NULL", c))
            using (var r = q.ExecuteReader())
                while (r.Read())
                {
                    if (T(r, 1) is not DateTime t) continue;
                    double? v = r.IsDBNull(2) ? (r.IsDBNull(3) ? null : CatchDbWriter.ParseRawChance(r.GetString(3))) : r.GetDouble(2);
                    if (v is not double vv || vv <= 0) continue;
                    long id = r.GetInt64(0); if (!ticks.TryGetValue(id, out var l)) ticks[id] = l = new(); l.Add((t, vv));
                }
            using (var q = new SqliteCommand("SELECT location_id, bait_name, fish_lang_id, MAX(fish_name), COUNT(*) FROM catches WHERE location_id IS NOT NULL AND bait_name IS NOT NULL AND fish_lang_id IS NOT NULL GROUP BY 1, 2, 3", c))
            using (var r = q.ExecuteReader())
                while (r.Read())
                {
                    string k = Key((uint)r.GetInt64(0), r.GetString(1));
                    if (!m.Fish.TryGetValue(k, out var l)) m.Fish[k] = l = new();
                    l.Add(((uint)r.GetInt64(2), r.IsDBNull(3) ? "" : r.GetString(3), r.GetInt32(4)));
                }
            foreach (var l in m.Fish.Values) l.Sort((a, b) => b.N.CompareTo(a.N));
            foreach (var l in ticks.Values) l.Sort((a, b) => a.t.CompareTo(b.t));

            var ev = new Dictionary<string, Dictionary<string, List<double>>>();
            void Add(long id, string to, double logr)
            {
                if (!bait.TryGetValue(id, out var b) || !water.TryGetValue(id, out var w)) return;
                string k = Key(w, b);
                if (!ev.TryGetValue(k, out var d)) ev[k] = d = new();
                if (!d.TryGetValue(to, out var l)) d[to] = l = new();
                l.Add(logr);
            }
            static double Med(IEnumerable<double> v) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? 0 : s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2; }
            // 1) внутри заброса: 4 такта до смены против 4 после (1-й такт — «прогрев», не берём)
            foreach (var (id, l) in ticks)
                for (int i = 2; i < l.Count - 2; i++)
                {
                    string a = Part(GameHour(l[i - 1].t)), b = Part(GameHour(l[i].t));
                    if (a == b) continue;
                    double pre = Med(l.Skip(Math.Max(1, i - 4)).Take(i - Math.Max(1, i - 4)).Select(x => x.c)), post = Med(l.Skip(i).Take(4).Select(x => x.c));
                    if (pre > 0.005 && post > 0.005) Add(id, b, Math.Log(post / pre));
                }
            // 2) следующий заброс той же удочки в ту же точку (≤12 м, ≤30 мин) через смену
            foreach (var g in cast.GroupBy(kv => (kv.Value.s, kv.Value.rod)))
            {
                var ids = g.OrderBy(kv => kv.Value.t).Select(kv => kv.Key).ToList();
                for (int i = 0; i + 1 < ids.Count; i++)
                {
                    long a = ids[i], b = ids[i + 1];
                    if (!bait.TryGetValue(a, out var ba) || !bait.TryGetValue(b, out var bb) || ba != bb) continue;
                    if (!ticks.TryGetValue(a, out var la) || !ticks.TryGetValue(b, out var lb) || la.Count < 3 || lb.Count < 3) continue;
                    var A = cast[a]; var B = cast[b];
                    if (A.x is null || B.x is null || Math.Sqrt(Math.Pow(A.x.Value - B.x.Value, 2) + Math.Pow(A.z!.Value - B.z!.Value, 2)) > 12 || (B.t - A.t).TotalMinutes > 30) continue;
                    string pa = Part(GameHour(la[^1].t)), pb = Part(GameHour(lb[1].t));
                    if (pa == pb) continue;
                    double va = Med(la.Skip(1).Take(3).Select(x => x.c)), vb = Med(lb.Skip(1).Take(3).Select(x => x.c));
                    if (va > 0.005 && vb > 0.005) Add(a, pb, Math.Log(vb / va));
                }
            }
            foreach (var (k, d) in ev)
            {
                var o = new Dictionary<string, (double, int)>();
                foreach (var (to, l) in d)
                {
                    if (l.Count < MinEvents) continue;
                    double med = Med(l);
                    double same = l.Count(x => Math.Sign(x) == Math.Sign(med)) / (double)l.Count;
                    if (same < 0.75) continue;                                       // замеры спорят — не учим
                    o[to] = (Math.Exp(med * l.Count / (l.Count + 2.0)), l.Count);     // сжатие к 1 при малом числе замеров
                }
                if (o.Count > 0) m.F[k] = o;
            }
            return m;
        }

        /// <summary>Во сколько раз меняется шанс этой наживки на этом водоёме при переходе в часть суток; null — не знаем.</summary>
        public static (double F, int N)? Factor(Model m, uint? water, string? bait, string to)
            => m.F.TryGetValue(Key(water, bait), out var d) && d.TryGetValue(to, out var f) ? f : null;

        private static readonly string[] Cycle = { "ночь", "рассвет", "день", "вечер" };   // смена «в X» идёт из предыдущей части круга

        /// <summary>Шанс по частям суток (лог) относительно одной из них — из множителей смен по кругу; только связанные части.</summary>
        public static Dictionary<string, (double L, int N)> Levels(Model m, uint? water, string? bait)
        {
            var res = new Dictionary<string, (double L, int N)>();
            if (!m.F.TryGetValue(Key(water, bait), out var d) || d.Count == 0) return res;
            string first = d.Keys.First();
            res[Cycle[(Array.IndexOf(Cycle, first) + 3) % 4]] = (0, 99);
            for (int pass = 0; pass < 4; pass++)
                for (int i = 0; i < 4; i++)
                {
                    string to = Cycle[i], from = Cycle[(i + 3) % 4];
                    if (!d.TryGetValue(to, out var f)) continue;
                    double lf = Math.Log(f.F);
                    if (res.TryGetValue(from, out var a) && !res.ContainsKey(to)) res[to] = (a.L + lf, Math.Min(a.N, f.N));
                    else if (res.TryGetValue(to, out var b) && !res.ContainsKey(from)) res[from] = (b.L - lf, Math.Min(b.N, f.N));
                }
            return res;
        }

        public static string Times(double f) => f >= 1 ? "вырастает в " + Num(f) + " раза" : "падает в " + Num(1 / f) + " раза";
        private static string Num(double v) => v.ToString(v < 10 ? "0.#" : "0", CultureInfo.GetCultureInfo("ru-RU"));

        public static string FishOf(Model m, uint? water, string? bait, int n = 2)
            => m.Fish.TryGetValue(Key(water, bait), out var l) && l.Count > 0 ? string.Join(", ", l.Take(n).Select(x => x.Name.ToLowerInvariant())) : "";

        /// <summary>Подсказка за ≤15 мин до смены: что будет с шансом этой наживки здесь. null — сказать нечего (мало данных или меняется слабо).</summary>
        public static string? Warn(Model m, uint? water, string? bait, DateTime now, double withinMin = 15)
        {
            var n = Now(now);
            if (n.Minutes > withinMin || string.IsNullOrWhiteSpace(bait) || Factor(m, water, bait, n.Next) is not { } f) return null;
            if (f.F < 1.4 && f.F > 1 / 1.4) return null;
            string fish = FishOf(m, water, bait);
            return $"через {Math.Max(1, Math.Ceiling(n.Minutes)):0} мин {n.Next}: клёв на «{bait}» здесь обычно " + (f.F >= 1 ? "усиливается в " + Num(f.F) : "слабеет в " + Num(1 / f.F)) + " раза" +
                   (fish.Length > 0 ? $" ({fish})" : "") + (f.F < 1 ? " — приготовьте другую снасть" : " — оставайтесь на месте") + $" (замечено {f.N} {(f.N % 10 is >= 2 and <= 4 && (f.N % 100 < 12 || f.N % 100 > 14) ? "раза" : "раз")})";
        }

        public static string RealWindow(string part) => part switch
        {
            "рассвет" => "игра 04:30–07:20 = МСК чётный час :22–:36",
            "день" => "игра 07:20–19:30 = МСК чётный :36 – нечётный :37",
            "вечер" => "игра 19:30–21:30 = МСК нечётный час :37–:47",
            _ => "игра 21:30–04:30 = МСК нечётный :47 – чётный :22"
        };

        /// <summary>Лучшая часть суток для рыбы на водоёме — по наживкам, на которые вы её здесь ловили. Пусто — данных нет.</summary>
        public static List<(string Part, double X, int N, string Bait, string Worst)> ForFish(Model m, uint fish, uint water)
        {
            var res = new List<(string, double, int, string, string)>();
            foreach (var (k, l) in m.Fish.Where(kv => kv.Key.StartsWith(water.ToString(CultureInfo.InvariantCulture) + "|", StringComparison.Ordinal) && kv.Value.Any(x => x.Id == fish))
                                          .OrderByDescending(kv => kv.Value.First(x => x.Id == fish).N))
            {
                string b = k[(k.IndexOf('|') + 1)..];
                var lv = Levels(m, water, b);
                if (lv.Count < 2) continue;
                var best = lv.OrderByDescending(x => x.Value.L).First();
                var worst = lv.OrderBy(x => x.Value.L).First();
                if (best.Value.L - worst.Value.L < Math.Log(1.4)) continue;
                res.Add((best.Key, Math.Exp(best.Value.L - worst.Value.L), Math.Min(best.Value.N, worst.Value.N), b, worst.Key));
                if (res.Count >= 2) break;
            }
            return res;
        }
    }
}
