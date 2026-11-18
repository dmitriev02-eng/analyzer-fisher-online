using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace FisherLogger
{
    /// <summary>
    /// TROPHY-01 (A): «трофейный режим» — как у ВАС клевала нужная рыба нужного класса НА ЭТОМ водоёме (каждый водоём — своя рыба и точки):
    /// при каком раннем шансе (такты 2–4), на какой наживке, крючке и прикормке. По данным ALLDATA-01 крупная рыба клюёт при умеренном
    /// шансе (1–12): крупная снасть сама даёт низкий шанс — уходить из-за него не нужно. Описательно: частота, не вероятность.
    /// </summary>
    public sealed class TrophyProfile
    {
        public uint Fish; public int MinClass; public uint? Water;
        public int Hits;                                    // забросов с нужной рыбой (поклёвка или улов)
        public int WithChance; public double Lo, Hi, Median;
        public string Bait = "", Hook = "", Ground = ""; public int BaitN, HookN, GroundN;
        public bool Enough => WithChance >= 3;

        private static readonly Dictionary<string, (DateTime at, TrophyProfile? p)> Cache = new();
        private static readonly object Gate = new();

        public static TrophyProfile? For(string db, uint fish, int minClass, uint? water, DateTime now)
        {
            string key = db + "|" + fish + "|" + minClass + "|" + water;
            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var c) && (now - c.at).TotalMinutes < 10) return c.p;
                TrophyProfile? p; try { p = Load(db, fish, minClass, water); } catch { p = null; }
                Cache[key] = (now, p); return p;
            }
        }

        /// <summary>water = null — водоём, где таких поклёвок больше всего (не смешиваем водоёмы).</summary>
        public static TrophyProfile? Load(string db, uint fish, int minClass, uint? water)
        {
            using var c = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False");
            c.Open();
            var hit = new HashSet<long>();
            string cls = minClass > 0 ? " AND windex >= " + minClass : "";
            foreach (var sql in new[] { "SELECT cast_id FROM cast_events WHERE event_type = 'bite' AND cast_id IS NOT NULL AND fish_lang_id = $f" + cls,
                                        "SELECT cast_id FROM catches WHERE cast_id IS NOT NULL AND fish_lang_id = $f" + cls })
            {
                using var q = new SqliteCommand(sql, c); q.Parameters.AddWithValue("$f", (long)fish);
                using var r = q.ExecuteReader(); while (r.Read()) hit.Add(r.GetInt64(0));
            }
            if (hit.Count == 0) return null;
            string ids = string.Join(",", hit);
            var waterOf = new Dictionary<long, uint?>();
            using (var q = new SqliteCommand("SELECT cast_id, MAX(location_id) FROM server_checks WHERE cast_id IN (" + ids + ") GROUP BY cast_id", c))
            using (var r = q.ExecuteReader())
                while (r.Read()) waterOf[r.GetInt64(0)] = r.IsDBNull(1) ? null : BiteAnalytics.WaterFromSpawn(r.GetInt64(1), new Dictionary<long, uint>()) ?? (uint)r.GetInt64(1);
            uint? w = water ?? waterOf.Values.Where(v => v.HasValue).GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            var casts = hit.Where(id => w == null || (waterOf.TryGetValue(id, out var cw) && cw == w)).ToList();
            if (casts.Count == 0) return null;
            var p = new TrophyProfile { Fish = fish, MinClass = minClass, Water = w, Hits = casts.Count };
            string cids = string.Join(",", casts);
            // ранний шанс: такты 2–4 до первой поклёвки
            var ev = new Dictionary<long, List<(DateTime t, string type, double? v)>>();
            using (var q = new SqliteCommand("SELECT cast_id, timestamp, event_type, chance, raw_text FROM cast_events WHERE cast_id IN (" + cids + ") AND event_type IN ('wait','bite','hook')", c))
            using (var r = q.ExecuteReader())
                while (r.Read())
                {
                    if (r.IsDBNull(1) || !DateTime.TryParse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
                    double? v = r.IsDBNull(3) ? (r.IsDBNull(4) ? null : CatchDbWriter.ParseRawChance(r.GetString(4))) : r.GetDouble(3);
                    long id = r.GetInt64(0); if (!ev.TryGetValue(id, out var l)) ev[id] = l = new(); l.Add((t, r.GetString(2), v));
                }
            var ch = new List<double>();
            foreach (var l in ev.Values)
            {
                var fb = l.Where(x => x.type != "wait").Select(x => (DateTime?)x.t).Min();
                var e = l.Where(x => x.type == "wait" && x.v.HasValue).OrderBy(x => x.t).Skip(1).Take(3).Where(x => fb == null || x.t < fb).Select(x => x.v!.Value).ToList();
                if (e.Count > 0) ch.Add(e.Average());
            }
            ch.Sort();
            p.WithChance = ch.Count;
            if (ch.Count > 0) { p.Lo = ch[(int)(0.25 * (ch.Count - 1))]; p.Hi = ch[(int)Math.Ceiling(0.75 * (ch.Count - 1))]; p.Median = ch[ch.Count / 2]; }
            var baits = new List<string>(); var hooks = new List<string>(); var gbs = new List<string>();
            using (var q = new SqliteCommand("SELECT bait_name, hook_base_par, hook_name, gb_osnova, groundbait_name FROM cast_tackle WHERE cast_id IN (" + cids + ")", c))
            using (var r = q.ExecuteReader())
                while (r.Read())
                {
                    string S(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                    if (S(0).Length > 0) baits.Add(S(0));
                    if (S(1).Length > 0) hooks.Add(S(1) + "\u0001" + S(2));
                    if (S(3).Length > 0 || S(4).Length > 0) gbs.Add(S(3).Length > 0 ? S(3) : S(4));
                }
            (string v, int n) Top(List<string> l) => l.Count == 0 ? ("", 0) : l.GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => (g.Key, g.Count())).First();
            (p.Bait, p.BaitN) = Top(baits); (p.Hook, p.HookN) = Top(hooks); (p.Ground, p.GroundN) = Top(gbs);
            return p;
        }

        public string HookPar => Hook.Split('\u0001')[0];
        public string HookName => Hook.Contains('\u0001') ? Hook.Split('\u0001')[1] : Hook;

        private static string F(double v) => v.ToString(v < 10 ? "0.0" : "0", CultureInfo.GetCultureInfo("ru-RU"));

        /// <summary>Строка для «Цели»: при каком шансе и на чём клевала нужная рыба на этом водоёме.</summary>
        public string Describe(string what, string waterName, Func<string, string>? groundName = null)
        {
            var parts = new List<string>();
            string where = waterName.Length > 0 ? " (" + waterName + ")" : "";
            if (Enough) parts.Add($"Трофейный режим{where}: {what} — ваши поклёвки были при шансе {F(Lo)}–{F(Hi)} (обычно ≈{F(Median)}). Шанс выше — это чаще мелочь; низкий шанс на такой снасти — нормально, не уходите только из-за него.");
            else parts.Add($"Трофейный режим{where}: такой рыбы у вас {Hits} — диапазон шанса появится с 3 поклёвок.");
            var tk = new List<string>();
            if (Bait.Length > 0) tk.Add($"наживка «{Bait}» ({BaitN} из {Hits})");
            if (HookName.Length > 0) tk.Add($"крючок {HookName} ({HookN})");
            if (Ground.Length > 0) tk.Add($"прикормка {(groundName?.Invoke(Ground) ?? Ground)} ({GroundN})");
            if (tk.Count > 0) parts.Add("Чаще всего: " + string.Join(", ", tk) + ".");
            return string.Join(" ", parts);
        }

        /// <summary>Тренер: шанс удочки «нормален для крупной», если снасть как у крупной и шанс не ниже нижней границы × 0,7.</summary>
        public bool NormalFor(double chance, string bait, string hookPar)
            => Enough && chance >= Lo * 0.7 && ((Bait.Length > 0 && bait.Trim() == Bait.Trim()) || (HookPar.Length > 0 && hookPar.Trim() == HookPar.Trim()));
    }
}
