using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace FisherLogger
{
    /// <summary>
    /// FORMULA-06 (B1/B2): живая оценка клёва по шансу, который сервер присылает в каждом такте «wait» (~8 с).
    /// Шанс — не вероятность, а шкала интенсивности (CONFIRMED монотонно: чем выше, тем чаще поклёвка до следующего такта; единицы UNVERIFIED).
    /// Калибровка «шанс → доля тактов с поклёвкой» берётся из ваших забросов и сама обновляется; пока данных мало — из стартовой
    /// таблицы (DeepSeek MECH-01, 12 580 тактов 07–15.11). Первый такт после заброса — «прогрев», не учитывается.
    /// </summary>
    public sealed class LiveBite
    {
        public static readonly double[] Edges = { 0.5, 1, 2, 5, 10, 20, 40 };                 // границы групп шанса
        public static readonly double[] Default = { 0.0014, 0.0038, 0.0074, 0.0189, 0.053, 0.0687, 0.1234, 0.1793 };   // доля тактов с поклёвкой
        public const double TickSec = 8.07, Prior = 50;
        public const int MinTicks = 3000;

        public double[] P = (double[])Default.Clone();
        public int[] Ticks = new int[8], Bites = new int[8];
        public int TotalTicks;
        public bool Learned;                       // калибровка по вашим данным (иначе стартовая таблица)
        public DateTime BuiltAt;

        public static int Bin(double c) { int i = 0; while (i < Edges.Length && c >= Edges[i]) i++; return i; }

        // TROPHY-01 (B): шанс разных снастей несравним (на одной точке две удочки: 3,4 и 21,3) — калибровка и «обычный шанс» по группе снасти.
        // Ключ: «водоём|наживка|крючок» → «*|наживка|крючок» → общая. Каждый водоём — своя рыба и свои точки.
        public sealed class Group
        {
            public int[] Ticks = new int[8], Bites = new int[8]; public int Total;
            public double[]? P;                 // своя калибровка (≥ MinGroupTicks), иначе null
            public double Lo, Hi;               // обычный шанс этой снасти: 25–75 % тактов
            internal List<double> Vals = new();
        }
        public const int MinGroupTicks = 1500, MinTypicalTicks = 300;
        public Dictionary<string, Group> Groups = new();
        public static string Key(uint? water, string? bait, string? hook) => (water?.ToString(CultureInfo.InvariantCulture) ?? "*") + "|" + (bait ?? "").Trim() + "|" + (hook ?? "").Trim();
        private static string Any(string key) { int i = key.IndexOf('|'); return i < 0 ? key : "*" + key.Substring(i); }

        private Group? Find(string? key, Func<Group, bool> ok)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (Groups.TryGetValue(key, out var g) && ok(g)) return g;
            return Groups.TryGetValue(Any(key), out var g2) && ok(g2) ? g2 : null;
        }
        public double[] PFor(string? key) => Find(key, g => g.P != null)?.P ?? P;
        /// <summary>Обычный шанс для этой снасти (на этом водоёме, если хватает тактов): 25–75 % тактов.</summary>
        public (double lo, double hi)? Typical(string? key) => Find(key, g => g.Total >= MinTypicalTicks) is { } g ? (g.Lo, g.Hi) : null;

        /// <summary>
        /// LOGFIX-03 (тренер): уловы этой снасти на ЭТОМ водоёме — при каком шансе (среднее тактов до поклёвки, без прогрева) и через
        /// сколько минут после заброса клюнула рыба. Нужна, чтобы низкий шанс на «крупной» снасти не гнал с места: на Франции 15.11
        /// 4 рыбы из 7 взяли при шансе 0,35–0,8 через 19–23 мин. Только свой водоём (без «*»): у каждого водоёма своя рыба.
        /// </summary>
        public Dictionary<string, List<(double chance, double min)>> Wins = new();

        /// <summary>Сколько уловов этой снасти здесь было при шансе не выше нынешнего (с запасом ×1,5 / +0,3) и за сколько минут; меньше 2 — null.</summary>
        public (int n, double minMin, double maxMin)? PatientAt(double chance, string? key)
        {
            if (string.IsNullOrEmpty(key) || key.StartsWith("*", StringComparison.Ordinal) || !Wins.TryGetValue(key, out var l)) return null;
            double lim = Math.Max(chance * 1.5, chance + 0.3);
            var m = l.Where(x => x.chance <= lim).ToList();
            return m.Count >= 2 ? (m.Count, m.Min(x => x.min), m.Max(x => x.min)) : null;
        }

        /// <summary>Доля поклёвок за следующие N минут при таком шансе (такты независимы — приближение).</summary>
        public double Within(double chance, double minutes, string? key = null)
        {
            double p = PFor(key)[Bin(chance)];
            return 1 - Math.Pow(1 - p, Math.Max(0, minutes) * 60 / TickSec);
        }

        /// <summary>Группа для подсказки по доле поклёвок за 2 мин: 0 — очень хороший (≥45 %), 1 — хороший (≥20 %), 2 — средний (≥6 %), 3 — низкий.</summary>
        public int Grade(double c, string? key = null) { double w = Within(c, 2, key); return w >= 0.45 ? 0 : w >= 0.20 ? 1 : w >= 0.06 ? 2 : 3; }

        public static string Advice(int grade) => grade switch
        {
            0 => "очень хороший, не трогайте",
            1 => "хороший, ждите",
            2 => "средний, подождите пару минут",
            _ => "низкий, смените точку или наживку"
        };

        private static LiveBite? _cache; private static string _cacheDb = ""; private static readonly object Sync = new();
        /// <summary>Калибровка из БД (кэш 10 мин). Ошибка чтения — стартовая таблица.</summary>
        public static LiveBite For(string db, DateTime now)
        {
            lock (Sync)
            {
                if (_cache != null && _cacheDb == db && (now - _cache.BuiltAt).TotalMinutes < 10) return _cache;
                LiveBite lb;
                try { lb = Fit(db); } catch { lb = new LiveBite(); }
                lb.BuiltAt = now; _cache = lb; _cacheDb = db;
                return lb;
            }
        }

        /// <summary>Такты до первой настоящей поклёвки (bite/hook); такт «с поклёвкой» — если она пришла до следующего такта (≤16 с для последнего).</summary>
        public static LiveBite Fit(string db)
        {
            var lb = new LiveBite();
            var waits = new Dictionary<long, List<(DateTime t, double c)>>();
            var bite = new Dictionary<long, DateTime>();
            var keyOf = new Dictionary<long, string>();
            var caught = new HashSet<long>();
            using (var c = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False"))
            {
                c.Open();
                using var q = new SqliteCommand("SELECT cast_id, event_type, timestamp, chance, raw_text FROM cast_events WHERE cast_id IS NOT NULL AND event_type IN ('wait','bite','hook')", c);
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    long id = r.GetInt64(0);
                    if (r.IsDBNull(2) || !DateTime.TryParse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
                    string et = r.GetString(1);
                    if (et == "wait")
                    {
                        double? ch = r.IsDBNull(3) ? (r.IsDBNull(4) ? null : CatchDbWriter.ParseRawChance(r.GetString(4))) : r.GetDouble(3);
                        if (ch is double v) { if (!waits.TryGetValue(id, out var l)) waits[id] = l = new(); l.Add((t, v)); }
                    }
                    else if (!bite.TryGetValue(id, out var b) || t < b) bite[id] = t;
                }
                try
                {
                    using var q2 = new SqliteCommand("SELECT t.cast_id, t.bait_name, t.hook_base_par, (SELECT MAX(s.location_id) FROM server_checks s WHERE s.cast_id = t.cast_id) FROM cast_tackle t WHERE t.cast_id IS NOT NULL", c);
                    using var r2 = q2.ExecuteReader();
                    while (r2.Read())
                    {
                        uint? w = r2.IsDBNull(3) ? null : BiteAnalytics.WaterFromSpawn(r2.GetInt64(3), new Dictionary<long, uint>()) ?? (uint)r2.GetInt64(3);
                        keyOf[r2.GetInt64(0)] = Key(w, r2.IsDBNull(1) ? "" : r2.GetString(1), r2.IsDBNull(2) ? "" : Convert.ToString(r2.GetValue(2), CultureInfo.InvariantCulture));
                    }
                }
                catch { }
                try
                {
                    using var q3 = new SqliteCommand("SELECT id FROM casts WHERE outcome = 'caught'", c);
                    using var r3 = q3.ExecuteReader();
                    while (r3.Read()) caught.Add(r3.GetInt64(0));
                }
                catch { }
            }
            foreach (var (id, l0) in waits)
            {
                var l = l0.OrderBy(x => x.t).ToList();
                DateTime? fb = bite.TryGetValue(id, out var b) ? b : null;
                if (fb.HasValue && caught.Contains(id) && keyOf.TryGetValue(id, out var wk) && !wk.StartsWith("*", StringComparison.Ordinal))
                {
                    var before = l.Skip(1).Where(x => x.t < fb.Value).Select(x => x.c).ToList();
                    if (before.Count > 0)
                    {
                        if (!lb.Wins.TryGetValue(wk, out var wl)) lb.Wins[wk] = wl = new();
                        wl.Add((before.Average(), (fb.Value - l[0].t).TotalMinutes));
                    }
                }
                for (int i = 1; i < l.Count; i++)          // 0-й такт — прогрев
                {
                    if (fb.HasValue && l[i].t >= fb.Value) break;
                    DateTime next = i + 1 < l.Count ? l[i + 1].t : l[i].t.AddSeconds(2 * TickSec);
                    bool hit = fb.HasValue && fb.Value <= next;
                    int k = Bin(l[i].c); lb.Ticks[k]++; if (hit) lb.Bites[k]++;
                    lb.TotalTicks++;
                    if (keyOf.TryGetValue(id, out var key))
                        foreach (var kk in new[] { key, Any(key) })
                        {
                            if (!lb.Groups.TryGetValue(kk, out var g)) lb.Groups[kk] = g = new Group();
                            g.Ticks[k]++; if (hit) g.Bites[k]++; g.Total++; g.Vals.Add(l[i].c);
                        }
                    if (hit) break;
                }
            }
            if (lb.TotalTicks >= MinTicks)
            {
                lb.Learned = true;
                for (int k = 0; k < 8; k++) lb.P[k] = (lb.Bites[k] + Prior * Default[k]) / (lb.Ticks[k] + Prior);
                for (int k = 1; k < 8; k++) lb.P[k] = Math.Max(lb.P[k], lb.P[k - 1]);   // монотонно: выше шанс — не реже клюёт
            }
            foreach (var g in lb.Groups.Values)
            {
                if (g.Vals.Count > 0) { g.Vals.Sort(); g.Lo = g.Vals[(int)(0.25 * (g.Vals.Count - 1))]; g.Hi = g.Vals[(int)(0.75 * (g.Vals.Count - 1))]; }
                g.Vals.Clear();
                if (g.Total >= MinGroupTicks)
                {
                    g.P = new double[8];
                    for (int k = 0; k < 8; k++) g.P[k] = (g.Bites[k] + 200 * lb.P[k]) / (g.Ticks[k] + 200.0);   // сжатие к общей калибровке
                    for (int k = 1; k < 8; k++) g.P[k] = Math.Max(g.P[k], g.P[k - 1]);
                }
            }
            return lb;
        }

        public sealed class Check { public int N, Folds, FoldsBetter; public double LogLoss, Baseline, Auc; public double Improvement => Baseline > 0 ? (Baseline - LogLoss) / Baseline : 0; }

        /// <summary>
        /// Честная проверка (как у формулы): забросы по времени → 5 частей; модель «поклёвка за заброс ~ log(1+ранний шанс)» учится только на прошлом.
        /// </summary>
        public static Check? Validate(IEnumerable<Attempt> attempts, int parts = 5)
        {
            var d = attempts.Where(a => a.Bite.HasValue && a.EarlyChance.HasValue).OrderBy(a => a.Ts).ToList();
            if (d.Count < 60) return null;
            double X(Attempt a) => Math.Log(1 + Math.Max(0, a.EarlyChance!.Value));
            var chk = new Check();
            var preds = new List<(double p, bool y)>();
            double ll = 0, llb = 0; int n = 0;
            for (int f = 1; f < parts; f++)
            {
                int a0 = d.Count * f / parts, a1 = d.Count * (f + 1) / parts;
                var train = d.Take(a0).ToList(); var test = d.Skip(a0).Take(a1 - a0).ToList();
                if (train.Count < 20 || test.Count == 0) continue;
                double w0 = 0, w1 = 0;
                for (int it = 0; it < 2000; it++)
                {
                    double g0 = 0, g1 = 0;
                    foreach (var t in train) { double e = Sig(w0 + w1 * X(t)) - (t.Bite == true ? 1 : 0); g0 += e; g1 += e * X(t); }
                    w0 -= 0.5 * g0 / train.Count; w1 -= 0.5 * (g1 / train.Count + 0.01 * w1);
                }
                double pb = Math.Clamp(train.Count(t => t.Bite == true) / (double)train.Count, 0.01, 0.99);
                double fl = 0, fb = 0;
                foreach (var t in test)
                {
                    double p = Math.Clamp(Sig(w0 + w1 * X(t)), 0.001, 0.999); bool y = t.Bite == true;
                    fl += -(y ? Math.Log(p) : Math.Log(1 - p)); fb += -(y ? Math.Log(pb) : Math.Log(1 - pb));
                    preds.Add((p, y));
                }
                ll += fl; llb += fb; n += test.Count; chk.Folds++; if (fl < fb) chk.FoldsBetter++;
            }
            if (n == 0) return null;
            chk.N = n; chk.LogLoss = ll / n; chk.Baseline = llb / n; chk.Auc = Auc(preds);
            return chk;
        }

        private static double Sig(double z) => 1 / (1 + Math.Exp(-z));
        private static double Auc(List<(double p, bool y)> l)
        {
            var pos = l.Where(x => x.y).Select(x => x.p).ToList(); var neg = l.Where(x => !x.y).Select(x => x.p).ToList();
            if (pos.Count == 0 || neg.Count == 0) return 0.5;
            double s = 0; foreach (var a in pos) foreach (var b in neg) s += a > b ? 1 : a == b ? 0.5 : 0;
            return s / (pos.Count * (double)neg.Count);
        }
    }
}
