using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace FisherLogger
{
    // =====================================================================================
    // COACH-01: подсказки во время рыбалки, пока формула копит статистику.
    //  * SoakCurve — поклёвки на 1000 тактов сервера по минуте в воде (по каждому типу удилища). На данных 12.11:
    //    фидер 16,1 (0–2 мин) → 0,8 (20+ мин). DeepSeek T20261112-2050 (CONFIRMED): это ОТБОР, а не затухание — у забросов,
    //    проживших ≥20 мин, поклёвок не было и в первые минуты. Поэтому подсказка не «перезабросьте в ту же точку», а
    //    «долго без поклёвки — здесь сейчас не клюёт: смените точку или наживку» (предсказание по вашим данным, не причина).
    //  * SessionCoach — снимок текущей рыбалки по удочкам из текущей БД (только чтение): время в воде, поклёвки,
    //    пустые поклёвки, крючок, наживка; правила-подсказки; сжатая сводка для ИИ (≈1–2 тыс. символов).
    //  * AgentUsage — учёт токенов агента за сутки (файл рядом с настройками), кэш последнего ответа тренера.
    // =====================================================================================

    public sealed class SoakCurve
    {
        public static readonly double[] Edges = { 0, 2, 5, 10, 20 };
        public const int MinTicks0 = 300, MinBites = 10, MinTicksBucket = 150;
        public sealed class Bucket
        {
            public double From, To; public int Ticks, Bites, Empty;
            public double? Rate => Ticks >= MinTicksBucket ? 1000.0 * Bites / Ticks : null;
            public string Label => To > 1000 ? From.ToString("0", CultureInfo.InvariantCulture) + "+ мин" : From.ToString("0", CultureInfo.InvariantCulture) + "–" + To.ToString("0", CultureInfo.InvariantCulture) + " мин";
        }
        public Dictionary<int, List<Bucket>> ByRod { get; } = new();

        public static int BucketOf(double min) { for (int i = Edges.Length - 1; i >= 0; i--) if (min >= Edges[i]) return i; return 0; }

        /// <summary>Из забросов (тип удилища, первый такт) и списков тактов и событий.</summary>
        public static SoakCurve FromAttempts(IEnumerable<Attempt> attempts, IEnumerable<(long cast, DateTime t, string type)> events, IEnumerable<(long cast, DateTime t)> ticks)
        {
            var c = new SoakCurve();
            var start = attempts.Where(a => a.FirstTick.HasValue && a.RodCode.HasValue).ToDictionary(a => a.CastId, a => (t: a.FirstTick!.Value, rod: a.RodCode!.Value));
            List<Bucket> L(int rod)
            {
                if (!c.ByRod.TryGetValue(rod, out var l))
                    c.ByRod[rod] = l = Edges.Select((e, i) => new Bucket { From = e, To = i + 1 < Edges.Length ? Edges[i + 1] : 1e9 }).ToList();
                return l;
            }
            foreach (var (cast, t) in ticks)
                if (start.TryGetValue(cast, out var s)) L(s.rod)[BucketOf((t - s.t).TotalMinutes)].Ticks++;
            foreach (var (cast, t, type) in events)
                if (start.TryGetValue(cast, out var s))
                {
                    var b = L(s.rod)[BucketOf((t - s.t).TotalMinutes)];
                    if (type == "bite") b.Bites++; else if (type == "empty_bite") b.Empty++;
                }
            return c;
        }

        /// <summary>Минуты, после которых клёв по вашим данным падает до ~60 % (жёлтый) и ~35 % (красный) от первых 2 минут. null — мало данных.</summary>
        public (double yellow, double red, double r0)? Thresholds(int rodCode)
        {
            if (!ByRod.TryGetValue(rodCode, out var l) || l[0].Ticks < MinTicks0 || l.Sum(b => b.Bites) < MinBites || l[0].Rate is not double r0 || r0 <= 0) return null;
            double? y = null, r = null;
            for (int i = 1; i < l.Count; i++)
            {
                if (l[i].Rate is not double ri) continue;
                if (y == null && ri <= 0.6 * r0) y = l[i].From;
                if (r == null && ri <= 0.35 * r0) r = l[i].From;
            }
            if (r == null) return null;
            return (y ?? r.Value, r.Value, r0);
        }

        public double? RateAt(int rodCode, double min) => ByRod.TryGetValue(rodCode, out var l) ? l[BucketOf(min)].Rate : null;

        public static string RodName(int? code) => code switch { 0 => "поплавок", 1 => "фидер", 2 => "спиннинг", 3 => "кастинг", 4 => "нахлыст", 6 => "зимняя", _ => "удочка" };

        public string Describe(int rodCode)
        {
            if (!ByRod.TryGetValue(rodCode, out var l)) return "нет данных";
            return string.Join(", ", l.Select(b => b.Label + " — " + (b.Rate is double r ? r.ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")) : "мало") + " (" + b.Ticks + " т.)"));
        }
    }

    /// <summary>FEAT-06: пустая поклёвка — вид, вес и класс сообщает сервер игры (s2c 1122).</summary>
    public sealed class EmptyBite
    {
        public uint Fish; public int Weight; public int? Windex; public DateTime At;
        public string Name => SessionCoach.FishNameOf(Fish);
    }

    public sealed class CoachRod
    {
        public int Slot; public long RodUid; public int? RodCode; public long CastId;
        public DateTime CastStart, LastTick; public double SoakMin;
        public bool Active;                 // такты идут прямо сейчас
        public int CastBites, CastEmpty;    // на текущем забросе
        public int Casts, Bites, Empty, Caught;   // за рыбалку
        public string Hook = "", HookPar = "", Bait = "", Fish = "";
        /// <summary>FIX-05: снасть и место текущего заброса; поймано на этой удочке за рыбалку (вид → штук).</summary>
        public RodSetup Setup = new();
        public Dictionary<string, int> CaughtFish = new();
        /// <summary>Забросы/поклёвки/пустые с ТОЙ ЖЕ снастью, что сейчас (смена наживки или крючка — новый счёт).</summary>
        public int SameCasts, SameBites, SameEmpty;
        public int Level = -1;              // -1 нет оценки, 0 норм, 1 скоро, 2 перезабросьте, 3 клюёт
        /// <summary>ZONES-02: расстояние наживки до ближайшего прикорма этой рыбалки за 60 мин, м; сколько минут назад внесён.</summary>
        public double? GbDistM, GbAgeMin;
        /// <summary>FORMULA-06 (B2): шанс сервера сейчас — среднее последних 3 тактов (без 1-го, «прогрева»); null — тактов ещё мало (&lt;3).</summary>
        public double? Chance; public int ChanceTicks; public double? Within2; public int ChanceGrade = -1;
        /// <summary>TROPHY-01: водоём удочки, «обычный шанс» этой снасти, нормален ли шанс для крупной цели, куда сдвинуться.</summary>
        public uint? Water; public (double lo, double hi)? Typical; public bool TrophyNormal;
        /// <summary>LOGFIX-03: низкий шанс, но эта снасть здесь уже ловила при таком — совет «ждите до ~N мин».</summary>
        public bool Patient;
        /// <summary>LOGFIX-01: кто трогал наживку на текущем забросе (s2c 1122: вид и вес рыбы, которая не засеклась).</summary>
        public List<(uint fish, int weight)> EmptyFish = new();
        /// <summary>FEAT-06: последние пустые поклёвки этой удочки за рыбалку (новые первыми, не больше EmptyListSize).</summary>
        public List<EmptyBite> RecentEmpty = new();
        public double? MoveX, MoveZ, MoveDist, MoveChance, MoveAge;
        public string Status = "";
    }

    /// <summary>FIX-05: снасть и место заброса — для сравнения удочек между собой.</summary>
    public sealed class RodSetup
    {
        public int? RodType; public string Bait = "", Hook = "", Leader = "", Line = "", Holder = "", Groundbait = "";
        public double? X, Z, Bottom, Above;
        public string Signature => string.Join("|", RodType, Bait, Hook, Leader, Line, Holder, Groundbait);

        /// <summary>Доля совпавших признаков среди известных у обеих удочек (0…1) и список различий.</summary>
        public static (double sim, int compared, List<string> same, List<string> diff) Compare(RodSetup a, RodSetup b)
        {
            var same = new List<string>(); var diff = new List<string>(); int n = 0;
            void T(string name, string x, string y) { if (x.Length == 0 || y.Length == 0) return; n++; if (x == y) same.Add(name); else diff.Add($"{name}: «{x}» и «{y}»"); }
            if (a.RodType.HasValue && b.RodType.HasValue) { n++; if (a.RodType == b.RodType) same.Add("тип удилища"); else diff.Add($"тип удилища: {SoakCurve.RodName(a.RodType)} и {SoakCurve.RodName(b.RodType)}"); }
            T("наживка", a.Bait, b.Bait); T("крючок", a.Hook, b.Hook); T("поводок", a.Leader, b.Leader); T("леска", a.Line, b.Line);
            T(a.RodType == 1 ? "кормушка" : "поплавок", a.Holder, b.Holder); T("прикорм", a.Groundbait, b.Groundbait);
            if (a.X.HasValue && b.X.HasValue && a.Z.HasValue && b.Z.HasValue)
            {
                n++; double d = Math.Sqrt(Math.Pow(a.X.Value - b.X.Value, 2) + Math.Pow(a.Z.Value - b.Z.Value, 2));
                if (d <= 10) same.Add($"место ({d:0} м друг от друга)"); else diff.Add($"место: {d:0} м друг от друга");
            }
            if (a.Bottom.HasValue && b.Bottom.HasValue) { n++; if (Math.Abs(a.Bottom.Value - b.Bottom.Value) <= 0.7) same.Add("глубина дна"); else diff.Add($"глубина дна: {a.Bottom:0.0} и {b.Bottom:0.0} м"); }
            if (a.Above.HasValue && b.Above.HasValue) { n++; if (Math.Abs(a.Above.Value - b.Above.Value) <= 0.3) same.Add("высота наживки над дном"); else diff.Add($"наживка над дном: {a.Above:0.0} и {b.Above:0.0} м"); }
            return (n == 0 ? 0 : (double)same.Count / n, n, same, diff);
        }
    }

    /// <summary>FIX-05: пара похожих удочек (сходство ≥ 50 %) с разным результатом.</summary>
    public sealed class RodPair { public CoachRod A = null!, B = null!; public double Sim; public List<string> Same = new(), Diff = new(); }

    public sealed class CoachSnapshot
    {
        public List<RodPair> Pairs = new();
        public List<(int a, int b, double sim)> Unlike = new();
        public DateTime At;
        public string Session = "";
        public uint? LocationId; public string LocationName = "";
        public List<CoachRod> Rods = new();
        public List<string> Hints = new();
        public string CurveNote = "";
        public bool HasData => Rods.Count > 0;
        /// <summary>Отпечаток состояния для кэша ИИ: меняется при новом забросе/событии, а не при каждом такте.</summary>
        public string Fingerprint = "";
        /// <summary>TROPHY-01 (C): свежие пятна шанса (≤15 мин) текущего водоёма — для мини-карты.</summary>
        public List<ChanceSpots.Spot> Spots = new();
        /// <summary>TROPHY-01 (A): как клевала нужная рыба нужного класса на этом водоёме (цель из настроек).</summary>
        public TrophyProfile? Trophy;
    }

    public static class SessionCoach
    {
        public const double ActiveWithinMin = 2.5, SessionGapMin = 45;
        /// <summary>OVERLAY-04: удочка «в воде», если последняя проверка сервера не старше 90 с (раньше 2,5 мин — вытащенная удочка долго считалась в воде).</summary>
        public const double ActiveWithinSec = 90;

        private static DateTime? Ts(object? o) => o is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) && t.Year >= 2020 ? t : null;
        private static long? L(object? o) => o switch { long l => l, int i => i, double d => (long)d, _ => null };

        /// <summary>Снимок последней рыбалки текущей БД (только чтение). curve — кривая по минуте в воде (может быть null).</summary>
        public static CoachSnapshot Build(string db, SoakCurve? curve, DateTime now, Func<uint?, string>? locName = null)
        {
            var snap = new CoachSnapshot { At = now };
            if (!File.Exists(db)) return snap;
            using var c = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False");
            c.Open();
            string? session = null; DateTime? lastAny = null;
            string nowS = now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);   // всё — не позже «сейчас» (и для разбора прошлых рыбалок)
            using (var q = new SqliteCommand("SELECT session_id, timestamp FROM server_checks WHERE timestamp <= $n ORDER BY timestamp DESC LIMIT 1", c))
            {
            q.Parameters.AddWithValue("$n", nowS);
            using (var r = q.ExecuteReader()) if (r.Read()) { session = r.IsDBNull(0) ? null : r.GetString(0); lastAny = Ts(r.IsDBNull(1) ? null : r.GetString(1)); }
            }
            if (session == null || lastAny == null || (now - lastAny.Value).TotalMinutes > SessionGapMin) return snap;   // сейчас не рыбачите
            snap.Session = session;
            DateTime from = lastAny.Value.AddHours(-6);
            var casts = new Dictionary<long, (long rod, int slot, int? code, DateTime first, DateTime last, long? loc)>();
            var place = new Dictionary<long, (double? x, double? z, double? bottom, double? above)>();
            using (var q = new SqliteCommand("SELECT cast_id, rod_uid, rod_index, rod_code, MIN(timestamp), MAX(timestamp), MAX(location_id), AVG(bait_x), AVG(bait_z), AVG(depth2_raw), AVG(depth2_raw - depth1_raw) FROM server_checks " +
                                             "WHERE session_id = $s AND cast_id IS NOT NULL AND timestamp >= $f AND timestamp <= $n GROUP BY cast_id", c))
            {
                q.Parameters.AddWithValue("$s", session); q.Parameters.AddWithValue("$n", nowS); q.Parameters.AddWithValue("$f", from.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                using var r = q.ExecuteReader();
                while (r.Read())
                    if (Ts(r.IsDBNull(4) ? null : r.GetString(4)) is DateTime f && Ts(r.IsDBNull(5) ? null : r.GetString(5)) is DateTime l)
                    {
                        casts[r.GetInt64(0)] = (r.IsDBNull(1) ? 0 : r.GetInt64(1), r.IsDBNull(2) ? 0 : (int)r.GetInt64(2), r.IsDBNull(3) ? null : (int)r.GetInt64(3), f, l, r.IsDBNull(6) ? null : r.GetInt64(6));
                        double? Dn(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
                        place[r.GetInt64(0)] = (Dn(7), Dn(8), Dn(9) is double b ? b / 100.0 : null, Dn(10) is double ab ? Math.Max(0, ab / 100.0) : null);
                    }
            }
            if (casts.Count == 0) return snap;
            var ev = new Dictionary<long, List<string>>();
            using (var q = new SqliteCommand("SELECT cast_id, event_type FROM cast_events WHERE cast_id IN (" + string.Join(",", casts.Keys) + ") AND event_type IN ('bite','empty_bite','catch','hook') AND timestamp <= $n", c))
            {
            q.Parameters.AddWithValue("$n", nowS);
            using (var r = q.ExecuteReader())
                while (r.Read()) { long id = r.GetInt64(0); if (!ev.TryGetValue(id, out var l)) ev[id] = l = new(); l.Add(r.GetString(1)); }
            }
            // FORMULA-06 (B2): такты «wait» с шансом сервера по текущим забросам (до B0 число было только в тексте события)
            var waits = new Dictionary<long, List<(DateTime t, double c)>>();
            using (var q = new SqliteCommand("SELECT cast_id, timestamp, chance, raw_text FROM cast_events WHERE cast_id IN (" + string.Join(",", casts.Keys) + ") AND event_type = 'wait' AND timestamp <= $n", c))
            {
                q.Parameters.AddWithValue("$n", nowS);
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    if (Ts(r.IsDBNull(1) ? null : r.GetString(1)) is not DateTime wt) continue;
                    double? ch = r.IsDBNull(2) ? (r.IsDBNull(3) ? null : CatchDbWriter.ParseRawChance(r.GetString(3))) : r.GetDouble(2);
                    if (ch is double v) { long id = r.GetInt64(0); if (!waits.TryGetValue(id, out var wl)) waits[id] = wl = new(); wl.Add((wt, v)); }
                }
            }
            LiveBite live = LiveBite.For(db, now);
            // LOGFIX-01: пустые поклёвки с видом и весом (сервер сообщает, какая рыба трогала наживку)
            var emptyBy = new Dictionary<long, List<(uint fish, int weight)>>();
            var emptyAll = new Dictionary<long, List<EmptyBite>>();   // FEAT-06: с классом и временем
            try
            {
                using var q = new SqliteCommand("SELECT cast_id, fish_lang_id, weight, windex, timestamp FROM cast_events WHERE cast_id IN (" + string.Join(",", casts.Keys) + ") AND event_type = 'empty_bite' AND fish_lang_id IS NOT NULL AND timestamp <= $n", c);
                q.Parameters.AddWithValue("$n", nowS);
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    long id = r.GetInt64(0); uint fid = (uint)r.GetInt64(1); int w = r.IsDBNull(2) ? 0 : (int)r.GetInt64(2);
                    if (!emptyBy.TryGetValue(id, out var el)) emptyBy[id] = el = new(); el.Add((fid, w));
                    if (!emptyAll.TryGetValue(id, out var ea)) emptyAll[id] = ea = new();
                    ea.Add(new EmptyBite { Fish = fid, Weight = w, Windex = r.IsDBNull(3) ? null : (int)r.GetInt64(3),
                                           At = DateTime.TryParse(r.IsDBNull(4) ? "" : r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : DateTime.MinValue });
                }
            }
            catch { }
            var tk = new Dictionary<long, (string hook, string hookPar, string bait)>();
            var setups = new Dictionary<long, RodSetup>();
            using (var q = new SqliteCommand("SELECT cast_id, hook_name, hook_base_par, bait_name, leader_name, line_name, feeder_name, float_name, gb_osnova, gb_aroma, gb_addextend, gb_addbaits, groundbait_name " +
                                             "FROM cast_tackle WHERE cast_id IN (" + string.Join(",", casts.Keys) + ")", c))
            using (var r = q.ExecuteReader())
                while (r.Read())
                {
                    string S(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                    long id = r.GetInt64(0);
                    tk[id] = (S(1), S(2), S(3));
                    string gb = string.Join("+", new[] { S(8), S(9), S(10), S(11) }.Where(x => x.Length > 0));
                    setups[id] = new RodSetup { Hook = S(1), Bait = S(3), Leader = S(4), Line = S(5), Holder = S(6).Length > 0 ? S(6) : S(7), Groundbait = gb.Length > 0 ? gb : S(12) };
                }
            var fishBy = new Dictionary<long, List<string>>();
            using (var q = new SqliteCommand("SELECT cast_id, fish_name FROM catches WHERE cast_id IN (" + string.Join(",", casts.Keys) + ") AND fish_name IS NOT NULL", c))
            using (var r = q.ExecuteReader())
                while (r.Read()) { long id = r.GetInt64(0); if (!fishBy.TryGetValue(id, out var l)) fishBy[id] = l = new(); l.Add(r.GetString(1)); }
            // ZONES-02: вносы прикорма этой рыбалки (игровые X/Z) — для подсказки «наживка вне точки прикорма»
            var gbs = new List<(DateTime t, double x, double z)>();
            try
            {
                using var q = new SqliteCommand("SELECT timestamp, game_x, game_z FROM groundbait_actions WHERE session_id = $s AND coord_system = 'game_world_xz' AND game_x IS NOT NULL AND timestamp <= $n", c);
                q.Parameters.AddWithValue("$s", session); q.Parameters.AddWithValue("$n", nowS);
                using var r = q.ExecuteReader();
                while (r.Read()) if (Ts(r.IsDBNull(0) ? null : r.GetString(0)) is DateTime gt) gbs.Add((gt, r.GetDouble(1), r.GetDouble(2)));
            }
            catch { }
            RodSetup SetupOf(long id, int? code)
            {
                var su = setups.TryGetValue(id, out var x) ? x : new RodSetup();
                su.RodType = code;
                if (place.TryGetValue(id, out var p)) { su.X = p.x; su.Z = p.z; su.Bottom = p.bottom; su.Above = p.above; }
                return su;
            }

            long? lastLoc = casts.Values.OrderByDescending(v => v.last).First().loc;
            if (lastLoc is long sp) { snap.LocationId = BiteAnalytics.WaterFromSpawn(sp, new Dictionary<long, uint>()); snap.LocationName = locName?.Invoke(snap.LocationId) ?? ""; }
            foreach (var g in casts.GroupBy(kv => kv.Value.rod))
            {
                var all = g.OrderBy(kv => kv.Value.first).ToList();
                var cur = all.Last();
                // COACHLOC-01: после переезда на другой водоём счётчики удочки начинаются заново — считаем только забросы текущего водоёма
                uint? Water(long? sp) => sp is long s ? BiteAnalytics.WaterFromSpawn(s, new Dictionary<long, uint>()) ?? (uint)s : null;
                var curWater = Water(cur.Value.loc);
                if (curWater.HasValue) all = all.Where(kv => Water(kv.Value.loc) is not uint w || w == curWater).ToList();
                var rod = new CoachRod
                {
                    RodUid = g.Key, Slot = cur.Value.slot, RodCode = cur.Value.code, CastId = cur.Key,
                    CastStart = cur.Value.first, LastTick = cur.Value.last, Casts = all.Count
                };
                // OVERLAY-04: в воде — проверки сервера идут (раз в ~8 с; при вываживании другой удочки пропуски до ~55 с) или эта удочка вываживается
                bool fighting = ev.TryGetValue(cur.Key, out var fe) && fe.Contains("hook") && !fe.Contains("catch") && (now - rod.LastTick).TotalMinutes <= 15;
                rod.Active = (now - rod.LastTick).TotalSeconds <= ActiveWithinSec || fighting;
                rod.SoakMin = ((rod.Active ? now : rod.LastTick) - rod.CastStart).TotalMinutes;
                foreach (var kv in all)
                    if (ev.TryGetValue(kv.Key, out var l)) { rod.Bites += l.Count(x => x == "bite"); rod.Empty += l.Count(x => x == "empty_bite"); rod.Caught += l.Count(x => x == "catch"); }
                if (ev.TryGetValue(cur.Key, out var cl)) { rod.CastBites = cl.Count(x => x == "bite"); rod.CastEmpty = cl.Count(x => x == "empty_bite"); }
                if (emptyBy.TryGetValue(cur.Key, out var efl)) rod.EmptyFish = efl;
                rod.RecentEmpty = all.SelectMany(kv => emptyAll.TryGetValue(kv.Key, out var ea) ? ea : new List<EmptyBite>())
                                     .OrderByDescending(x => x.At).Take(Math.Clamp(EmptyListSize, 1, 10)).ToList();   // FEAT-06
                if (tk.TryGetValue(cur.Key, out var t)) { rod.Hook = t.hook; rod.HookPar = t.hookPar; rod.Bait = t.bait; }
                rod.Setup = SetupOf(cur.Key, cur.Value.code);
                rod.Water = curWater;
                if (rod.Setup.X is double bx && rod.Setup.Z is double bz)
                {
                    var near = gbs.Where(g => g.t <= rod.CastStart.AddSeconds(5) && (rod.CastStart - g.t).TotalMinutes <= 60)
                                  .Select(g => (d: Math.Sqrt((g.x - bx) * (g.x - bx) + (g.z - bz) * (g.z - bz)), age: (now - g.t).TotalMinutes))
                                  .OrderBy(g => g.d).ToList();
                    if (near.Count > 0) { rod.GbDistM = near[0].d; rod.GbAgeMin = near[0].age; }
                }
                foreach (var kv in all)
                {
                    if (fishBy.TryGetValue(kv.Key, out var fl)) foreach (var fn in fl) rod.CaughtFish[fn] = rod.CaughtFish.TryGetValue(fn, out int fc) ? fc + 1 : 1;
                    if (SetupOf(kv.Key, kv.Value.code).Signature != rod.Setup.Signature) continue;
                    rod.SameCasts++;
                    if (ev.TryGetValue(kv.Key, out var l2)) { rod.SameBites += l2.Count(x => x == "bite"); rod.SameEmpty += l2.Count(x => x == "empty_bite"); }
                }
                if (waits.TryGetValue(cur.Key, out var wl2))
                {
                    var tl = wl2.OrderBy(x => x.t).Skip(1).ToList();
                    rod.ChanceTicks = tl.Count;
                    string key = LiveBite.Key(rod.Water, rod.Setup.Bait, rod.HookPar);   // TROPHY-01 (B): шанс оценивается по своей снасти
                    if (tl.Count >= 3) { rod.Chance = tl.Skip(tl.Count - 3).Average(x => x.c); rod.Within2 = live.Within(rod.Chance.Value, 2, key); rod.ChanceGrade = live.Grade(rod.Chance.Value, key); rod.Typical = live.Typical(key); }
                }
                Grade(rod, curve);
                snap.Rods.Add(rod);
            }
            // OVERLAY-04: после смены снасти игра даёт удочке новый номер (rod_uid) — в одном слоте показываем только последнюю удочку,
            // иначе висела строка «Удочка 3: не в воде с 23:21» рядом с той же удочкой в воде
            snap.Rods = snap.Rods.GroupBy(r => r.Slot).Select(g => g.OrderByDescending(r => r.LastTick).First()).OrderBy(r => r.Slot).ThenBy(r => r.RodUid).ToList();
            // TROPHY-01: трофейный режим и свежие пятна
            try
            {
                snap.Spots = ChanceSpots.Read(c, session, snap.LocationId, now);
                foreach (var spt in snap.Spots)   // SPOTS-05: цвет кружка — относительно обычного шанса своей снасти
                {
                    var tp2 = spt.Tackle.Split('|');
                    if (live.Typical(LiveBite.Key(snap.LocationId, spt.Bait, tp2.Length > 1 ? tp2[1] : "")) is { } ty2) { spt.Lo = ty2.lo; spt.Hi = ty2.hi; }
                    spt.Usual = live.Usual(LiveBite.Key(snap.LocationId, spt.Bait, tp2.Length > 1 ? tp2[1] : ""));   // FEAT-07
                }
                if ((GoalOverride ?? CurrentGoal()) is { } goal && goal.fish > 0 && goal.cls >= 1)
                    snap.Trophy = TrophyProfile.For(db, goal.fish, goal.cls, snap.LocationId, now);
            }
            catch { }
            foreach (var r in snap.Rods) Refine(r, snap, live);
            Hints(snap);
            // DAYPART-01: часы игры и что сделает смена времени суток с шансом этой наживки здесь (по вашим забросам)
            try
            {
                var dm = DayParts.For(db, now);
                // предупреждения о смене — первыми (действовать надо сейчас), строка часов — последней (справка)
                foreach (var r in snap.Rods.Where(r => r.Active).OrderByDescending(r => r.Slot))
                    if (DayParts.Warn(dm, r.Water, r.Setup.Bait, now) is string w) snap.Hints.Insert(0, $"Удочка {r.Slot + 1}: {w}.");
                snap.Hints.Add(DayParts.Clock(now) + ".");
            }
            catch { }
            int code = snap.Rods.FirstOrDefault(r => r.RodCode.HasValue)?.RodCode ?? 1;
            snap.CurveNote = curve?.Thresholds(code) is { } th
                ? $"По вашим данным ({SoakCurve.RodName(code)}): если {th.red:0} мин нет поклёвки, дальше клюёт в 3+ раза реже — обычно это «пустое» место или не та снасть. Перезаброс в ту же точку не помогает: меняйте точку или наживку на одной удочке."
                : "Подсказка «перезабросьте» появится, когда наберётся статистика забросов этим типом удилища.";
            snap.Fingerprint = string.Join("|", snap.Rods.Select(r => $"{r.RodUid}:{r.CastId}:{r.Bites}:{r.Empty}:{r.Caught}:{r.Level}:{r.Setup.Signature}"));
            return snap;
        }

        private static void Grade(CoachRod r, SoakCurve? curve)
        {
            if (!r.Active) { r.Level = -1; r.Status = "не в воде с " + r.LastTick.ToString("HH:mm", CultureInfo.InvariantCulture); return; }
            if (r.CastBites > 0) { r.Level = 3; r.Status = "поклёвка на этом забросе"; return; }
            string soak = r.SoakMin.ToString("0", CultureInfo.InvariantCulture) + " мин в воде";
            if (r.RodCode is int code && curve?.Thresholds(code) is { } th)
            {
                r.Level = r.SoakMin >= th.red ? 2 : r.SoakMin >= th.yellow ? 1 : 0;
                // удочка, которая уже ловила на этой рыбалке, — «рабочая»: не советуем бросать место, только освежить
                r.Status = soak + (r.Level == 2 ? (r.Bites > 0 ? " — давно тихо, перезабросьте (здесь уже клевало)" : " — смените точку/наживку") : r.Level == 1 ? " — клюёт реже обычного" : "");
            }
            else { r.Level = -1; r.Status = soak; }
            // FORMULA-06 (B2): шанс, который присылает сервер, точнее «минут в воде» (проверка: ошибка −31 % к среднему, AUC 0,83) — он главнее
            if (r.Chance is double ch && r.ChanceGrade >= 0)
            {
                int g = r.ChanceGrade;
                r.Level = g <= 1 ? 0 : g == 2 ? 1 : 2;
                // DESIGN-02: словами, без сырого числа (шанс — интенсивность для этой снасти, не вероятность); число — только в техрежиме
                string rel = Plain.Relative(ch, r.Typical);
                r.Status = soak + " · клёв " + LiveBite.Advice(g) + (rel.Length > 0 ? " (" + rel + ")" : "") + Plain.TechChance(ch, r.Typical);
            }
            if (r.CastEmpty > 0) r.Status += EmptyText(r);
        }

        private static string Fishes(int n) => (n % 10 == 1 && n % 100 != 11) ? "рыба" : (n % 10 is >= 2 and <= 4 && (n % 100 < 12 || n % 100 > 14)) ? "рыбы" : "рыб";

        private static string Num(double v) => v.ToString(v < 10 ? "0.0" : "0", CultureInfo.GetCultureInfo("ru-RU"));

        /// <summary>Для тестов: цель вместо настроек.</summary>
        public static (uint fish, int cls)? GoalOverride;
        private static (uint fish, int cls)? _goal; private static DateTime _goalAt;
        private static (uint fish, int cls)? CurrentGoal()
        {
            if ((DateTime.UtcNow - _goalAt).TotalSeconds < 30) return _goal;
            try { var g = AppSettings.Load().Goal; _goal = g != null && g.Fish > 0 ? (g.Fish, g.MinClass) : null; } catch { _goal = null; }
            _goalAt = DateTime.UtcNow; return _goal;
        }

        /// <summary>
        /// TROPHY-01: (A) снасть как у крупной рыбы цели и шанс не ниже её обычного — «для крупной это нормально, ждите»;
        /// (C) иначе при среднем/низком шансе — пятно той же снасти в 25–50 м, где ≤15 мин назад шанс был ≥2× выше.
        /// </summary>
        private static void Refine(CoachRod r, CoachSnapshot s, LiveBite? live = null)
        {
            if (!r.Active || r.CastBites > 0 || r.Chance is not double ch || r.ChanceGrade < 2) return;
            string soak = r.SoakMin.ToString("0", CultureInfo.InvariantCulture) + " мин в воде";
            if (s.Trophy is { } tp && tp.NormalFor(ch, r.Setup.Bait, r.HookPar))
            {
                r.TrophyNormal = true; r.Level = 0;
                r.Status = soak + " · клёв спокойный — для крупной рыбы цели это нормально (здесь она брала при таком же), ждите" + Plain.TechChance(ch, (tp.Lo, tp.Hi))
                         + (r.CastEmpty > 0 ? EmptyText(r) : "");
                return;
            }
            // LOGFIX-03: на этой снасти здесь уже ловили при таком же низком шансе — значит, тут берут не сразу: ждать, а не уходить
            if (live?.PatientAt(ch, LiveBite.Key(r.Water, r.Setup.Bait, r.HookPar)) is { } pa)
            {
                int wait = (int)Math.Ceiling(Math.Max(pa.maxMin + 2, 10) / 5.0) * 5;
                if (r.SoakMin <= wait)
                {
                    r.Patient = true; r.Level = 0;
                    r.Status = soak + Plain.TechChance(ch) + $" · на этой снасти здесь уже ловили при таком клёве ({pa.n} {Fishes(pa.n)}, клевало через "
                             + (Math.Round(pa.minMin) == Math.Round(pa.maxMin) ? $"{Math.Round(pa.maxMin):0}" : $"{Math.Round(pa.minMin):0}–{Math.Round(pa.maxMin):0}")
                             + $" мин после заброса), ждите до ~{wait} мин" + (r.CastEmpty > 0 ? EmptyText(r) : "");
                    return;
                }
            }
            if (r.Setup.X is double x && r.Setup.Z is double z && ChanceSpots.MoveTarget(s.Spots, x, z, ChanceSpots.TackleKey(r.Setup.Bait, r.HookPar), ch) is { } sp)
            {
                r.MoveX = sp.X; r.MoveZ = sp.Z; r.MoveDist = ChanceSpots.Dist(x, z, sp.X, sp.Z); r.MoveChance = sp.Median; r.MoveAge = sp.AgeMin;
                r.Status += $" · в ~{Math.Round(r.MoveDist.Value):0} м (стрелка на карте) {Math.Max(1, Math.Round(sp.AgeMin)):0} мин назад на этой же снасти клевало заметно лучше — попробуйте там"
                          + (Plain.Tech ? " [там было " + Num(sp.Median) + "]" : "");
            }
        }

        /// <summary>FEAT-06: сколько последних пустых поклёвок показывать по каждой удочке (1–10; из настроек «Вид»).</summary>
        public static int EmptyListSize { get; set; } = 5;

        /// <summary>FEAT-06: «Лещ белый 0,39 кг · хороший».</summary>
        public static string EmptyText(EmptyBite e)
            => e.Name + " " + (e.Weight / 1000.0).ToString("0.00", CultureInfo.GetCultureInfo("ru-RU")) + " кг" + (e.Windex is int w ? " · " + GradeWord(w) : "");
        public static string GradeWord(int w) => w switch { 0 => "обычный", 1 => "хороший", 2 => "крупный", 3 => "трофейный", _ => "" };

        private static Dictionary<uint, string>? _fishNames;
        /// <summary>Название вида по id (fish_full_database.json); нет — «вид N».</summary>
        public static string FishNameOf(uint id)
        {
            if (_fishNames == null)
            {
                var d = new Dictionary<uint, string>();
                try
                {
                    var json = AdvisorResources.LoadFishFullDatabase();
                    if (json != null)
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("fish", out var f) ? f : doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Array)
                            foreach (var it in root.EnumerateArray())
                                if (it.TryGetProperty("id", out var idp) && idp.TryGetUInt32(out uint fid) && it.TryGetProperty("name_ru", out var nm) && nm.ValueKind == JsonValueKind.String)
                                    d[fid] = nm.GetString() ?? "";
                    }
                }
                catch { }
                _fishNames = d;
            }
            return _fishNames.TryGetValue(id, out var n) && n.Length > 0 ? n : "вид " + id;
        }

        /// <summary>
        /// LOGFIX-01: «пустых: 13 (Плотва ×13 0,3–0,5 кг)». Пустая поклёвка (s2c 1122) — рыба тронула наживку и не засеклась; сервер сообщает её вид и вес.
        /// Много пустых одной мелкой рыбы — здесь стоит мелочь: подсказываем сдвинуться или поставить наживку покрупнее.
        /// </summary>
        private static string EmptyText(CoachRod r)
        {
            string t = " · пустых: " + r.CastEmpty;
            if (r.EmptyFish.Count == 0) return t;
            var ru = CultureInfo.GetCultureInfo("ru-RU");
            var groups = r.EmptyFish.GroupBy(x => x.fish).OrderByDescending(g => g.Count()).Take(2).Select(g =>
            {
                double lo = g.Min(x => x.weight) / 1000.0, hi = g.Max(x => x.weight) / 1000.0;
                string w = Math.Abs(hi - lo) < 0.05 ? lo.ToString("0.0", ru) : lo.ToString("0.0", ru) + "–" + hi.ToString("0.0", ru);
                return FishNameOf(g.Key) + (g.Count() > 1 ? " ×" + g.Count() : "") + " " + w + " кг";
            });
            t += " (" + string.Join(", ", groups) + ")";
            var top = r.EmptyFish.GroupBy(x => x.fish).OrderByDescending(g => g.Count()).First();
            if (r.CastEmpty >= 4 && top.Count() * 2 >= r.EmptyFish.Count && top.Max(x => x.weight) < 1000)
                t += " — здесь трогает мелочь: сдвиньтесь или поставьте наживку покрупнее";
            return t;
        }

        public const double SimilarAt = 0.5;
        /// <summary>ZONES-02: радиус пятна прикорма (гайд игроков: 5 м; наши данные согласуются).</summary>
        public const double GbSpotM = 5;

        private static void Hints(CoachSnapshot s)
        {
            // «перезабросьте» видно в строке удочки — отдельной подсказкой не дублируем.
            // FIX-05: сравниваем только похожие удочки (≥50 % совпадений снасти и места): у непохожих может быть другая целевая рыба —
            // переносить им чужую наживку или снасть нельзя (так тренер советовал удочке на раковом мясе бойл «Сливки»).
            var rods = s.Rods.ToList();
            for (int i = 0; i < rods.Count; i++)
                for (int j = i + 1; j < rods.Count; j++)
                {
                    var (sim, n, same, diff) = RodSetup.Compare(rods[i].Setup, rods[j].Setup);
                    if (n < 4) continue;
                    if (sim < SimilarAt) { s.Unlike.Add((rods[i].Slot + 1, rods[j].Slot + 1, sim)); continue; }
                    var (good, bad) = rods[i].SameBites >= rods[j].SameBites ? (rods[i], rods[j]) : (rods[j], rods[i]);
                    s.Pairs.Add(new RodPair { A = good, B = bad, Sim = sim, Same = same, Diff = diff });
                    // OVERLAY-04: совет «сделайте как на …» — только когда обе удочки в воде: вытащенную (например, для смены крючка)
                    // не советуем — её снасть «на момент заброса» уже может быть другой; сходство для разбора (ИИ) остаётся
                    if (rods[i].Active && rods[j].Active && good.SameBites >= 2 && bad.SameBites == 0 && (bad.SameCasts >= 2 || bad.SoakMin >= 10) && diff.Count > 0)
                    {
                        string what = diff.Count == 1 ? diff[0] : string.Join("; ", diff.Take(3));
                        s.Hints.Add($"Удочки {good.Slot + 1} и {bad.Slot + 1} похожи на {sim:P0} ({string.Join(", ", same.Take(4))}). Разница: {what}. " +
                                    $"На {good.Slot + 1} — {good.SameBites} {Plural(good.SameBites, "поклёвка", "поклёвки", "поклёвок")}, на {bad.Slot + 1} — 0" +
                                    (bad.SameEmpty > 0 ? $" ({bad.SameEmpty} пуст.)" : "") +
                                    $": попробуйте на {bad.Slot + 1} {(diff.Count == 1 ? "сделать как на " + (good.Slot + 1) : "менять по одному: сначала " + diff[0].Split(':')[0])} (проверка, не доказанный вывод).");
                    }
                }
            // ZONES-02: пятно прикорма в игре ≈5 м; по нашим данным в ≤2,5 м от точки ловится ≈0,8 рыбы на заброс, дальше — 0,1–0,4
            foreach (var r in s.Rods.Where(r => r.Active && r.CastBites == 0 && r.GbDistM is double d && d > GbSpotM && d <= 30))
                s.Hints.Add($"Удочка {r.Slot + 1}: наживка в {r.GbDistM:0} м от точки прикорма (пятно ≈{GbSpotM:0} м) — забросьте в точку, рыба собирается там.");
            foreach (var r in s.Rods.Where(r => r.SameEmpty >= 3 && r.SameBites == 0 && !s.Hints.Any(h => h.Contains($"на {r.Slot + 1} —"))))
                s.Hints.Add($"Удочка {r.Slot + 1}: {r.SameEmpty} {Plural(r.SameEmpty, "пустая поклёвка", "пустые поклёвки", "пустых поклёвок")} без засечки — рыба рядом. Проверьте крючок или наживку (меняйте один фактор).");
            if (s.Rods.Count(r => r.Active) >= 2 && s.Rods.All(r => r.Bites == 0) && s.Rods.Sum(r => r.Casts) >= 6)
                s.Hints.Add("За эту рыбалку поклёвок нет ни на одной удочке — смените место или наживку на одной из удочек (меняйте по одному фактору).");
        }

        public static string Plural(int n, string one, string few, string many)
        {
            int m10 = n % 10, m100 = n % 100;
            return m10 == 1 && m100 != 11 ? one : m10 is >= 2 and <= 4 && (m100 < 12 || m100 > 14) ? few : many;
        }

        /// <summary>Сжатая сводка для ИИ (без личных данных, uid, путей). ≈1–2 тыс. символов.</summary>
        public static string Summary(CoachSnapshot s, SoakCurve? curve, string extra = "")
        {
            var sb = new StringBuilder();
            sb.Append("Рыбалка: ").Append(s.LocationName.Length > 0 ? s.LocationName : "водоём не известен").Append(", сейчас ").Append(s.At.ToString("HH:mm", CultureInfo.InvariantCulture)).AppendLine(".");
            foreach (var r in s.Rods)
                sb.Append("Удочка ").Append(r.Slot + 1).Append(" (").Append(SoakCurve.RodName(r.RodCode)).Append("): забросов ").Append(r.Casts)
                  .Append(", поклёвок ").Append(r.Bites).Append(", пустых поклёвок ").Append(r.Empty).Append(", поймано ").Append(r.Caught)
                  .Append("; сейчас: ").Append(r.Status).Append("; крючок ").Append(r.Hook.Length > 0 ? r.Hook + " (" + r.HookPar + ", острота/размер/самоподсечка)" : "?")
                  .Append("; наживка ").Append(r.Bait.Length > 0 ? r.Bait : "?")
                  .Append("; поводок ").Append(r.Setup.Leader.Length > 0 ? r.Setup.Leader : "?").Append("; ").Append(r.RodCode == 1 ? "кормушка " : "поплавок ").Append(r.Setup.Holder.Length > 0 ? r.Setup.Holder : "?")
                  .Append("; прикорм ").Append(r.Setup.Groundbait.Length > 0 ? r.Setup.Groundbait : "нет/?")
                  .Append(r.GbDistM is double gd ? $"; до точки прикорма {gd:0} м (внесён {r.GbAgeMin:0} мин назад)" : "")
                  .Append(r.Setup.Bottom is double bd ? $"; дно {bd:0.0} м" : "").Append(r.Setup.Above is double ab ? $", наживка {ab:0.0} м над дном" : "")
                  .Append("; с этой снастью: забросов ").Append(r.SameCasts).Append(", поклёвок ").Append(r.SameBites).Append(", пустых ").Append(r.SameEmpty)
                  .Append(r.CaughtFish.Count > 0 ? "; поймано: " + string.Join(", ", r.CaughtFish.OrderByDescending(x => x.Value).Select(x => x.Key + " ×" + x.Value)) : "").AppendLine(".");
            foreach (var p in s.Pairs)
                sb.Append("Похожие удочки ").Append(p.A.Slot + 1).Append(" и ").Append(p.B.Slot + 1).Append(" (").Append(p.Sim.ToString("P0", CultureInfo.GetCultureInfo("ru-RU"))).Append("): одинаково — ")
                  .Append(string.Join(", ", p.Same)).Append("; разница — ").Append(p.Diff.Count > 0 ? string.Join("; ", p.Diff) : "нет").AppendLine(".");
            foreach (var (a, b, sim) in s.Unlike)
                sb.Append("Удочки ").Append(a).Append(" и ").Append(b).Append(" непохожи (").Append(sim.ToString("P0", CultureInfo.GetCultureInfo("ru-RU"))).AppendLine(") — вероятно, разная целевая рыба: их не сравнивать и снасть между ними не переносить.");
            int code = s.Rods.FirstOrDefault(r => r.RodCode.HasValue)?.RodCode ?? 1;
            if (curve != null) sb.Append("Поклёвок на 1000 тактов по минуте в воде (").Append(SoakCurve.RodName(code)).Append("): ").Append(curve.Describe(code))
                                 .AppendLine(". Внимание: спад — отбор (долго лежат забросы, где не клюёт с самого начала), а не затухание прикорма.");
            if (s.Hints.Count > 0) sb.Append("Правила программы уже подсказали: ").AppendLine(string.Join(" ", s.Hints));
            if (extra.Length > 0) sb.AppendLine(extra);
            string t = sb.ToString();
            return t.Length > 3200 ? t[..3200] : t;
        }
    }

    /// <summary>COACH-01: учёт токенов агента за сутки + кэш ответа тренера. Файл agent_usage.json рядом с настройками.</summary>
    public sealed class AgentUsage
    {
        public string Day { get; set; } = "";
        public int Tokens { get; set; }
        public int Requests { get; set; }
        public string CoachFingerprint { get; set; } = "";
        public string CoachAnswer { get; set; } = "";
        public DateTime? CoachAt { get; set; }

        public static string? PathOverride { get; set; }
        public static string FilePath => PathOverride ?? Path.Combine(Path.GetDirectoryName(AppSettings.FilePath) ?? ".", "agent_usage.json");
        private static readonly object Gate = new();

        public static AgentUsage Load()
        {
            lock (Gate)
            {
                AgentUsage u;
                try { u = File.Exists(FilePath) ? JsonSerializer.Deserialize<AgentUsage>(File.ReadAllText(FilePath)) ?? new() : new(); } catch { u = new(); }
                string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (u.Day != today) { u.Day = today; u.Tokens = 0; u.Requests = 0; }
                return u;
            }
        }

        public void Save()
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this));
                    File.Move(FilePath + ".tmp", FilePath, true);
                }
                catch { }
            }
        }

        /// <summary>Можно ли сделать ещё запрос: остаток бюджета больше ожидаемой стоимости.</summary>
        public bool CanSpend(AgentSettings s, int expected) => Tokens + expected <= s.DailyTokenBudget;
        public void Add(int tokens) { Tokens += Math.Max(0, tokens); Requests++; }
        public string Text(AgentSettings s) => $"ИИ сегодня: {Tokens:#,0} из {s.DailyTokenBudget:#,0} токенов, запросов {Requests}".Replace(',', ' ');
    }

    /// <summary>COACH-01: разбор сессии ИИ — один короткий запрос, кэш по отпечатку, интервал, суточный бюджет.</summary>
    public static class CoachAi
    {
        public const int ExpectedTokens = 1500;

        public sealed class Result { public bool Ok; public bool FromCache; public string Text = ""; public string Note = ""; }

        public static async Task<Result> AskAsync(AgentSettings a, CoachSnapshot snap, SoakCurve? curve, bool manual, CancellationToken ct = default,
                                                  System.Net.Http.HttpMessageHandler? handler = null)
        {
            var u = AgentUsage.Load();
            if (!snap.HasData) return new Result { Note = "Сейчас нет активной рыбалки — разбирать нечего." };
            if (!a.Enabled || !a.HasKey || a.BaseUrl.Length == 0 || a.Model.Length == 0) return new Result { Note = "ИИ-агент не настроен (Настройки → ИИ-агент)." };
            if (u.CoachFingerprint == snap.Fingerprint && u.CoachAnswer.Length > 0)
                return new Result { Ok = true, FromCache = true, Text = u.CoachAnswer, Note = "без изменений с " + u.CoachAt?.ToString("HH:mm", CultureInfo.InvariantCulture) + " — ответ из памяти, токены не потрачены" };
            if (!manual && u.CoachAt.HasValue && (DateTime.Now - u.CoachAt.Value).TotalMinutes < a.CoachIntervalMin)
                return new Result { Note = $"следующий автосовет не раньше {u.CoachAt.Value.AddMinutes(a.CoachIntervalMin):HH:mm}" };
            if (!u.CanSpend(a, ExpectedTokens)) return new Result { Note = "Суточный лимит токенов исчерпан (" + u.Text(a) + "). Изменить — Настройки → ИИ-агент." };
            string? key = a.GetApiKey();
            if (key == null) return new Result { Note = "Нет API-ключа." };
            var client = new AgentClient(a, key, handler);
            var reply = await client.AskCoachAsync(SessionCoach.Summary(snap, curve), ct);
            u = AgentUsage.Load();
            u.Add(client.LastTokens);
            if (reply.Ok) { u.CoachFingerprint = snap.Fingerprint; u.CoachAnswer = reply.Text; u.CoachAt = DateTime.Now; }
            u.Save();
            return reply.Ok ? new Result { Ok = true, Text = reply.Text, Note = client.LastTokens + " токенов · " + u.Text(a) } : new Result { Note = "Ошибка: " + reply.Error };
        }
    }
}
