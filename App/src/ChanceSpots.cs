using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace FisherLogger
{
    /// <summary>
    /// TROPHY-01 (C): свежие «пятна» шанса клёва на текущем водоёме — из ваших же забросов (никаких запросов к серверу).
    /// По данным (DeepSeek CHANCEMAP-01/ALLDATA-01): пятно ≈10–25 м, замер живёт 10–15 мин, шанс разных снастей несравним,
    /// сдвиг на 3–10 м ничего не даёт, на 25–50 м — уже другое пятно. Поэтому: только замеры ≤15 мин, только стоящая снасть
    /// (поплавок/фидер), пятна по одной снасти (наживка+крючок), совет — к пятну в 25–50 м той же снасти.
    /// </summary>
    public static class ChanceSpots
    {
        /// <summary>
        /// SPOTS-02: замер держится, пока не сменится время суток игры (MECH-02: без смены та же точка через 15–30 мин ×1,23,
        /// через смену ×1,8–2,7). Поэтому окно — от последней смены (игровые 04:30, 07:20, 19:30, 21:30), но не больше MaxAgeMin.
        /// </summary>
        public const double MaxAgeMin = 60, MergeM = 12, MoveMinM = 25, MoveMaxM = 50, MinRatio = 2;
        public static readonly double[] GameBoundaries = { 4.5, 7.0 + 20.0 / 60, 19.5, 21.5 };

        /// <summary>Сколько реальных минут прошло с последней смены времени суток игры (не больше MaxAgeMin). Время игры = МСК × 12.</summary>
        public static double WindowMin(DateTime nowLocal)
        {
            DateTime msk = nowLocal.Kind == DateTimeKind.Utc ? nowLocal.AddHours(3) : nowLocal.ToUniversalTime().AddHours(3);
            double g = GameClock.GameTimeOfDay(msk).TotalHours;
            double back = GameBoundaries.Min(b => ((g - b) % 24 + 24) % 24) * 60.0 / GameClock.DaysPerRealDay;   // игровые часы → реальные минуты
            return Math.Min(MaxAgeMin, back);
        }
        public const int MinTicks = 3;

        public sealed class Spot
        {
            public double X, Z, Median, AgeMin; public int Ticks, Casts; public string Tackle = "", Bait = "";
            /// <summary>SPOTS-05: обычный шанс этой снасти на этом водоёме (p25–p75, LiveBite) — оценка «выше/ниже обычного».</summary>
            public double? Lo, Hi;
            /// <summary>SPOTS-05: шанс ≥2× медианы других точек той же снасти — пометка на карте; Note — какая рыба здесь брала на эту наживку.</summary>
            public bool Strong; public double Ratio; public string Note = "";
            /// <summary>SPOTS-05: оценка простым словом относительно своей снасти (нет обычного — по общей шкале).</summary>
            public string Grade => Lo.HasValue && Hi.HasValue ? (Median >= Hi.Value ? "выше обычного для этой снасти" : Median >= Lo.Value ? "как обычно для этой снасти" : "ниже обычного для этой снасти")
                                                              : (Median >= 5 ? "хорошо" : Median >= 1 ? "средне" : "слабо");
            public int Level => Lo.HasValue && Hi.HasValue ? (Median >= Hi.Value ? 2 : Median >= Lo.Value ? 1 : 0) : (Median >= 5 ? 2 : Median >= 1 ? 1 : 0);
            internal List<double> Vals = new();
        }

        public static string TackleKey(string? bait, string? hook) => (bait ?? "").Trim() + "|" + (hook ?? "").Trim();

        public static List<Spot> Read(SqliteConnection c, string session, uint? water, DateTime now)
        {
            var res = new List<Spot>();
            try
            {
                string nowS = now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                double win = WindowMin(now);
                string fromS = now.AddMinutes(-win).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                var casts = new Dictionary<long, (double x, double z)>();
                using (var q = new SqliteCommand("SELECT cast_id, AVG(bait_x), AVG(bait_z), MAX(location_id), MAX(rod_code) FROM server_checks WHERE session_id = $s AND cast_id IS NOT NULL " +
                                                 "AND timestamp >= $f AND timestamp <= $n AND bait_x IS NOT NULL GROUP BY cast_id", c))
                {
                    q.Parameters.AddWithValue("$s", session); q.Parameters.AddWithValue("$f", fromS); q.Parameters.AddWithValue("$n", nowS);
                    using var r = q.ExecuteReader();
                    while (r.Read())
                    {
                        if (!r.IsDBNull(4) && r.GetInt64(4) is not (0 or 1)) continue;          // спиннинг: приманка движется — не точка замера
                        uint? w = r.IsDBNull(3) ? null : BiteAnalytics.WaterFromSpawn(r.GetInt64(3), new Dictionary<long, uint>()) ?? (uint)r.GetInt64(3);
                        if (water.HasValue && w.HasValue && w != water) continue;
                        casts[r.GetInt64(0)] = (r.GetDouble(1), r.GetDouble(2));
                    }
                }
                if (casts.Count == 0) return res;
                string ids = string.Join(",", casts.Keys);
                var tackle = new Dictionary<long, (string key, string bait)>();
                using (var q = new SqliteCommand("SELECT cast_id, bait_name, hook_base_par FROM cast_tackle WHERE cast_id IN (" + ids + ")", c))
                using (var r = q.ExecuteReader())
                    while (r.Read())
                    {
                        string b = r.IsDBNull(1) ? "" : r.GetString(1);
                        tackle[r.GetInt64(0)] = (TackleKey(b, r.IsDBNull(2) ? "" : Convert.ToString(r.GetValue(2), CultureInfo.InvariantCulture)), b);
                    }
                var ticks = new Dictionary<long, List<(DateTime t, double v)>>();
                using (var q = new SqliteCommand("SELECT cast_id, timestamp, chance, raw_text FROM cast_events WHERE event_type = 'wait' AND cast_id IN (" + ids + ") AND timestamp <= $n", c))
                {
                    q.Parameters.AddWithValue("$n", nowS);
                    using var r = q.ExecuteReader();
                    while (r.Read())
                    {
                        if (r.IsDBNull(1) || !DateTime.TryParse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
                        double? v = r.IsDBNull(2) ? (r.IsDBNull(3) ? null : CatchDbWriter.ParseRawChance(r.GetString(3))) : r.GetDouble(2);
                        if (v is not double vv) continue;
                        long id = r.GetInt64(0); if (!ticks.TryGetValue(id, out var l)) ticks[id] = l = new(); l.Add((t, vv));
                    }
                }
                // замер заброса: такты (без 1-го, «прогрева») за последние 15 мин
                var samples = new List<(double x, double z, string key, string bait, List<double> vals, double age)>();
                foreach (var (id, l0) in ticks)
                {
                    var l = l0.OrderBy(x => x.t).Skip(1).Where(x => (now - x.t).TotalMinutes <= win).ToList();
                    if (l.Count == 0 || !casts.TryGetValue(id, out var p)) continue;
                    var tk = tackle.TryGetValue(id, out var tt) ? tt : (TackleKey("", ""), "");
                    samples.Add((p.x, p.z, tk.Item1, tk.Item2, l.Select(x => x.v).ToList(), (now - l.Max(x => x.t)).TotalMinutes));
                }
                foreach (var s in samples.OrderBy(s => s.age))
                {
                    var sp = res.FirstOrDefault(r => r.Tackle == s.key && Dist(r.X, r.Z, s.x, s.z) <= MergeM);
                    if (sp == null) { sp = new Spot { X = s.x, Z = s.z, Tackle = s.key, Bait = s.bait, AgeMin = s.age }; res.Add(sp); }
                    else { sp.X = (sp.X * sp.Casts + s.x) / (sp.Casts + 1); sp.Z = (sp.Z * sp.Casts + s.z) / (sp.Casts + 1); sp.AgeMin = Math.Min(sp.AgeMin, s.age); }
                    sp.Casts++; sp.Vals.AddRange(s.vals);
                }
                foreach (var sp in res) { sp.Ticks = sp.Vals.Count; var v = sp.Vals.OrderBy(x => x).ToList(); sp.Median = v.Count == 0 ? 0 : v[v.Count / 2]; sp.Vals.Clear(); }
            }
            catch { }
            return res;
        }

        /// <summary>
        /// SPOTS-03: лучшая проверенная точка — среди пятен снасти самого свежего заброса (шанс разных снастей несравним), ≥3 тактов, ≥2 пятен
        /// для сравнения. Предсказать шанс между точками нельзя (проверка: по соседям не лучше среднего), а повторный заброс в ту же
        /// точку в то же время суток игры даёт тот же шанс (×1,05) — поэтому показываем лучшую из проверенных.
        /// </summary>
        public static Spot? Best(IEnumerable<Spot> spots)
        {
            var ok = spots.Where(s => s.Ticks >= MinTicks).ToList();
            var fresh = ok.OrderBy(s => s.AgeMin).FirstOrDefault();
            if (fresh == null) return null;
            var same = ok.Where(s => s.Tackle == fresh.Tackle).ToList();
            return same.Count >= 2 ? same.OrderByDescending(s => s.Median).First() : null;
        }

        public static double Dist(double ax, double az, double bx, double bz) => Math.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

        /// <summary>Куда сдвинуться: пятно той же снасти в 25–50 м, ≥3 тактов, в этом же времени суток игры, шанс ≥2× текущего. null — советовать нечего.</summary>
        public static Spot? MoveTarget(IEnumerable<Spot> spots, double x, double z, string tackle, double current)
            => spots.Where(s => s.Tackle == tackle && s.Ticks >= MinTicks && s.AgeMin <= MaxAgeMin && s.Median >= 1 && s.Median >= MinRatio * Math.Max(current, 0.1))
                    .Select(s => (s, d: Dist(s.X, s.Z, x, z))).Where(t => t.d >= MoveMinM && t.d <= MoveMaxM)
                    .OrderByDescending(t => t.s.Median).Select(t => t.s).FirstOrDefault();
    }
}
