using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FisherLogger
{
    // =====================================================================================
    // HOURS-01: «По игровым часам» для Советника (ПК и телефон — одинаковый расчёт, на телефоне повторён в Kotlin).
    //  * Одна серия за раз: «мои уловы» ИЛИ «объявления других игроков» (не складываются: разный состав видов и классов;
    //    70 % моих уловов есть и в объявлениях — они вычитаются).
    //  * Фильтры: водоём (для объявлений — где были ВЫ при приёме), рыба, период (последние сутки / 3 суток / всё время).
    //  * Нормировка (HOURS-01, проверено DeepSeek на данных): у своих уловов «пик» почти целиком = когда вы больше забрасывали
    //    (корреляция с числом забросов 0,91), поэтому для своих — уловов на 10 забросов; для объявлений — на 10 мин наблюдения.
    //  * Период «достаточен», только если записей хватает и они распределены по часам (не один длинный заход):
    //    сутки — ≥30 зап. и ≥8 часов с ≥3 записями; 3 суток — ≥60 и ≥12 (для одной рыбы 20/6 и 30/8). «Авто» расширяет период.
    //  * Час «мало данных» (мало забросов / мало наблюдений) — не «не ловится». Значок частой рыбы — только явный лидер.
    //  * Это частота записанных уловов/объявлений, НЕ вероятность клёва.
    // =====================================================================================

    public sealed class HourRec
    {
        public bool Mine;
        public DateTime Real;
        public int Hour;
        public string Key = "";
        public string Fish = "";
        public uint? FishId;
        public uint? LocationId;
        public bool MatchedOwn;
    }

    /// <summary>Наблюдение: реальный 5-минутный отрезок → игровой час, минуты записи.</summary>
    public sealed class HourCov { public DateTime Slot; public int Hour; public double Minutes; public uint? Loc; }
    /// <summary>Свой заброс (усилие): время, игровой час, водоём.</summary>
    public sealed class HourCast { public DateTime Real; public int Hour; public uint? LocationId; }

    public sealed class HourQuery
    {
        public string Series = "auto";      // auto | mine | pub
        public uint? LocationId;
        public string? SpeciesKey;
        public string Period = "auto";      // auto | day | 3days | all
        public DateTime Now = DateTime.Now;
        /// <summary>AUDIT-02: где водится вид (ключ вида → водоёмы). Для своей серии при выбранной рыбе забросы считаются
        /// только на этих водоёмах: на других этот вид не поймать, и такие забросы не «разбавляют» частоту.</summary>
        public Dictionary<string, HashSet<uint>>? Habitat;
    }

    public sealed class HourSpecies { public string Key = ""; public string Name = ""; public uint? Id; public int N; public double Share; }

    public sealed class HourBin
    {
        public int H;
        public int N;
        public double Cov;            // минуты наблюдения
        public int Casts;             // мои забросы в этом часе (серия «мои»)
        public bool Low;              // мало забросов / мало наблюдений
        public double? Rate;          // мои: уловов на 10 забросов; объявления: на 10 мин наблюдения
        public List<HourSpecies> Species = new();
        public HourSpecies? Icon;     // явный лидер
        public bool Tie;              // лидеров несколько — значок не ставится
    }

    public sealed class HourReport
    {
        public string Series = "mine", SeriesText = "";
        public string Period = "all", PeriodText = "", AutoNote = "";
        public string Level = "none", LevelText = "";
        public string RateUnit = "", LowText = "";
        public int Total, HoursWith3, CastsTotal;
        public double CovTotal;
        public List<HourBin> Hours = new(), Hours3 = new();
        public List<HourSpecies> Top = new();
        public List<int> BestHours = new();
        public string Note = "";
    }

    public static class HourStats
    {
        public const int DayMinN = 30, DayMinHours = 8, ThreeMinN = 60, ThreeMinHours = 12;           // вся рыба
        public const int DayMinN1 = 20, DayMinHours1 = 6, ThreeMinN1 = 30, ThreeMinHours1 = 8;        // одна рыба
        public const int AllMinN = 30, AllMinHours = 8;                                               // «достаточно» за всё время
        public const int HourRecs = 3;                // «час с данными» — ≥3 записей
        public const double CovMin = 10.0;            // мин наблюдения на час (объявления)
        public const int CastsMin = 10;               // забросов на час (мои)
        public const int IconMinN = 5, IconMinN3 = 15, IconMinLeader = 3;
        public const double IconMinShare = 0.15, IconLead = 1.5;
        public const int IconLeadAbs = 2;

        public static readonly string[] Periods = { "day", "3days", "all" };
        public static string PeriodName(string p) => p switch { "day" => "последние сутки", "3days" => "последние 3 суток", _ => "всё время" };
        public static string SeriesName(string s) => s switch { "pub" => "объявления других игроков", "all" => "мои уловы и уловы других игроков", _ => "мои уловы" };

        public static (List<HourRec> recs, List<HourCov> cov) From(AnalyticsDataset ds)
        {
            var recs = new List<HourRec>();
            foreach (var a in ds.Mine.Concat(ds.Public))
            {
                if (!a.Real.HasValue || !a.Hour.HasValue) continue;
                recs.Add(new HourRec { Mine = a.IsMine, Real = a.Real.Value, Hour = a.Hour.Value, Key = a.SpeciesKey, Fish = a.Fish, FishId = a.FishId, LocationId = a.LocationId, MatchedOwn = a.MatchedOwn });
            }
            var cov = new Dictionary<(DateTime, int), double>();
            foreach (var (start, sec) in ds.Coverage)
            {
                var slot = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute / 5 * 5, 0);
                int h = (int)GameClock.GameTimeOfDay(start).TotalHours % 24;
                cov[(slot, h)] = cov.TryGetValue((slot, h), out var m) ? m + sec / 60.0 : sec / 60.0;
            }
            return (recs, cov.Select(kv => new HourCov { Slot = kv.Key.Item1, Hour = kv.Key.Item2, Minutes = Math.Round(kv.Value, 4) /* = как уходит на телефон */ }).OrderBy(c => c.Slot).ToList());
        }

        /// <summary>AUDIT-02: водоём 5-минутного отрезка наблюдения — где вы были: чаще всего по своим забросам, затем по своим
        /// уловам, затем по водоёму приёма объявлений в этом отрезке. Без сведений — null (при выбранном водоёме не учитывается).</summary>
        public static void AssignLocations(List<HourCov> cov, List<HourRec> recs, List<HourCast> casts)
        {
            static DateTime Slot(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute / 5 * 5, 0);
            var byCast = casts.Where(k => k.LocationId.HasValue).GroupBy(k => Slot(k.Real)).ToDictionary(g => g.Key, g => g.Select(k => k.LocationId!.Value).ToList());
            var byMine = recs.Where(r => r.Mine && r.LocationId.HasValue).GroupBy(r => Slot(r.Real)).ToDictionary(g => g.Key, g => g.Select(r => r.LocationId!.Value).ToList());
            var byPub = recs.Where(r => !r.Mine && r.LocationId.HasValue).GroupBy(r => Slot(r.Real)).ToDictionary(g => g.Key, g => g.Select(r => r.LocationId!.Value).ToList());
            static uint? Mode(List<uint>? l) => l == null || l.Count == 0 ? null : l.GroupBy(x => x).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
            foreach (var c in cov)
                c.Loc = Mode(byCast.GetValueOrDefault(c.Slot)) ?? Mode(byMine.GetValueOrDefault(c.Slot)) ?? Mode(byPub.GetValueOrDefault(c.Slot));
        }

        /// <summary>Забросы из Советника (BiteAnalytics.Attempts): только с известным игровым часом.</summary>
        public static List<HourCast> Casts(IEnumerable<Attempt> attempts) =>
            attempts.Where(a => a.GameHour.HasValue).Select(a => new HourCast { Real = a.Ts, Hour = a.GameHour!.Value, LocationId = a.Location }).ToList();

        private static DateTime? Since(string p, DateTime now) => p switch { "day" => now.AddHours(-24), "3days" => now.AddHours(-72), _ => null };

        private static List<HourRec> Pick(List<HourRec> recs, HourQuery q, string series, DateTime? since) =>
            recs.Where(r => series == "all" ? (r.Mine || !r.MatchedOwn) : r.Mine == (series == "mine") && !(series == "pub" && r.MatchedOwn))   // FEAT-06
                .Where(r => q.LocationId == null || r.LocationId == q.LocationId)
                .Where(r => q.SpeciesKey == null || r.Key == q.SpeciesKey)
                .Where(r => since == null || r.Real >= since.Value).ToList();

        private static int With3(List<HourRec> rs) => rs.GroupBy(r => r.Hour).Count(g => g.Count() >= HourRecs);

        private static bool Enough(string p, List<HourRec> rs, bool one)
        {
            int n = rs.Count, h = With3(rs);
            return p switch
            {
                "day" => n >= (one ? DayMinN1 : DayMinN) && h >= (one ? DayMinHours1 : DayMinHours),
                "3days" => n >= (one ? ThreeMinN1 : ThreeMinN) && h >= (one ? ThreeMinHours1 : ThreeMinHours),
                _ => n >= (one ? DayMinN1 : AllMinN) && h >= (one ? DayMinHours1 : AllMinHours)
            };
        }

        private static List<HourSpecies> Rank(IEnumerable<HourRec> rs)
        {
            var list = rs.ToList();
            int n = list.Count;
            return list.GroupBy(r => r.Key).Select(g => new HourSpecies
            {
                Key = g.Key, Name = g.First().Fish, Id = g.Select(r => r.FishId).FirstOrDefault(i => i.HasValue), N = g.Count(),
                Share = n > 0 ? Math.Round((double)g.Count() / n, 4) : 0
            }).OrderByDescending(s => s.N).ThenBy(s => s.Key, StringComparer.Ordinal).ToList();
        }

        /// <summary>Явный лидер: n ≥ minN, лидер ≥ 3, доля ≥ 15 % и (≥ 1,5× второго или отрыв ≥ 2). Ничья — без значка.</summary>
        private static (HourSpecies? icon, bool tie) Leader(List<HourSpecies> sp, int n, int minN)
        {
            if (n < minN || sp.Count == 0) return (null, false);
            var a = sp[0];
            if (a.N < IconMinLeader || a.Share < IconMinShare) return (null, false);
            if (sp.Count > 1)
            {
                var b = sp[1];
                if (b.N == a.N) return (null, true);
                if (a.N < IconLead * b.N && a.N - b.N < IconLeadAbs) return (null, true);
            }
            return (a, false);
        }

        public static HourReport Build(List<HourRec> recs, List<HourCov> cov, List<HourCast> casts, HourQuery q)
        {
            bool one = q.SpeciesKey != null;
            // FEAT-06: «Авто» — свои уловы вместе с уловами других игроков (свои объявления не считаются дважды)
            string series = q.Series is "mine" or "pub" or "all" ? q.Series : "all";
            string period = q.Period, autoNote = "";
            if (period == "auto")
            {
                period = "all";
                foreach (var p in new[] { "day", "3days" })
                    if (Enough(p, Pick(recs, q, series, Since(p, q.Now)), one)) { period = p; break; }
                if (period != "day")
                {
                    var d = Pick(recs, q, series, Since("day", q.Now));
                    autoNote = $"За последние сутки мало записей ({d.Count}, хорошо покрыто часов: {With3(d)}) — показано за {PeriodName(period)}.";
                }
            }
            if (series == "all") autoNote = "Учтены и ваши уловы, и уловы других игроков. " + autoNote;
            var since = Since(period, q.Now);
            var rs = Pick(recs, q, series, since);
            var c = new double[24];
            // AUDIT-02: при выбранном водоёме — только наблюдение, когда вы были на нём
            foreach (var x in cov) if ((since == null || x.Slot >= since.Value) && (q.LocationId == null || x.Loc == q.LocationId)) c[x.Hour] += x.Minutes;
            bool mine = series == "mine";
            var cs = new int[24];
            HashSet<uint>? hab = one && q.Habitat != null && q.Habitat.TryGetValue(q.SpeciesKey!, out var hs) && hs.Count > 0 ? hs : null;
            if (mine) foreach (var k in casts)
                    if ((since == null || k.Real >= since.Value) && (q.LocationId == null || k.LocationId == q.LocationId) &&
                        (hab == null || (k.LocationId.HasValue && hab.Contains(k.LocationId.Value)))) cs[k.Hour]++;
            bool useCasts = mine && cs.Sum() > 0;
            // уловы для нормировки по забросам — только с тех пор, как записываются забросы (раньше забросов в БД нет)
            DateTime? castsFrom = useCasts ? casts.Where(k => (since == null || k.Real >= since.Value) && (q.LocationId == null || k.LocationId == q.LocationId)).Select(k => (DateTime?)k.Real).Min() : null;
            bool enough = Enough(period, rs, one);
            var rep = new HourReport
            {
                Series = series, SeriesText = SeriesName(series), Period = period, PeriodText = PeriodName(period), AutoNote = autoNote,
                Total = rs.Count, HoursWith3 = With3(rs), CovTotal = Math.Round(c.Sum(), 1), CastsTotal = cs.Sum(),
                RateUnit = useCasts ? "уловов на 10 забросов" : "записей на 10 мин наблюдения",
                LowText = useCasts ? "мало забросов" : "мало наблюдений"
            };
            rep.Level = rs.Count == 0 ? "none" : enough ? "ok" : "low";
            int needN = period switch { "day" => one ? DayMinN1 : DayMinN, "3days" => one ? ThreeMinN1 : ThreeMinN, _ => one ? DayMinN1 : AllMinN };
            int needH = period switch { "day" => one ? DayMinHours1 : DayMinHours, "3days" => one ? ThreeMinHours1 : ThreeMinHours, _ => one ? DayMinHours1 : AllMinHours };
            rep.LevelText = rep.Level switch
            {
                "none" => "Нет записей для этого выбора.",
                "low" => $"Пока мало данных: {rs.Count} записей, хорошо покрыто часов: {rep.HoursWith3} (нужно {needN} записей и {needH} ч). Выводы делать рано.",
                _ => $"{rs.Count} записей, хорошо покрыто часов: {rep.HoursWith3} из 24."
            };
            HourBin Bin(int h0, int len, int minN, double covMin, int castsMin)
            {
                var inH = rs.Where(r => r.Hour >= h0 && r.Hour < h0 + len).ToList();
                var sp = Rank(inH);
                double cv = 0; int ca = 0;
                for (int h = h0; h < h0 + len; h++) { cv += c[h]; ca += cs[h]; }
                bool low = useCasts ? ca < castsMin : cv < covMin;
                double? rate = null;
                if (!low)
                {
                    if (useCasts) { int nn = inH.Count(r => castsFrom == null || r.Real >= castsFrom.Value); rate = Math.Round(nn * 10.0 / ca, 2); }
                    else rate = Math.Round(inH.Count * 10.0 / cv, 2);
                }
                var (icon, tie) = !one && !low && enough ? Leader(sp, inH.Count, minN) : (null, false);
                return new HourBin { H = h0, N = inH.Count, Cov = Math.Round(cv, 1), Casts = ca, Low = low, Rate = rate, Species = sp.Take(12).ToList(), Icon = icon, Tie = tie };
            }
            for (int h = 0; h < 24; h++) rep.Hours.Add(Bin(h, 1, IconMinN, CovMin, CastsMin));
            for (int h = 0; h < 24; h += 3) rep.Hours3.Add(Bin(h, 3, IconMinN3, CovMin * 3, CastsMin * 3));
            rep.Top = Rank(rs).Take(10).ToList();
            if (enough)
                rep.BestHours = rep.Hours.Where(b => b.Rate.HasValue && b.N >= IconMinN).OrderByDescending(b => b.Rate).ThenBy(b => b.H).Take(3).Select(b => b.H).ToList();
            rep.Note = "Частота записанных " + (mine ? "уловов" : series == "all" ? "уловов (ваших и других игроков)" : "объявлений") + " по игровым часам (оценка времени ±3 игр. мин), не вероятность клёва. " +
                       (useCasts ? "«Лучшие часы» — по уловам на 10 ваших забросов: просто «больше уловов» обычно значит «больше забрасывали». "
                                 : mine ? "Забросы за этот период не записаны — нормировка по наблюдению. " : "Водоём объявления — где были вы при приёме (места в объявлении нет). ") +
                       "«" + rep.LowText + "» — не «не ловится».";
            return rep;
        }

        /// <summary>AUDIT-02: справочник «где водится» в ключах видов этого расчёта.</summary>
        public static Dictionary<string, HashSet<uint>> HabitatMap(HabitatIndex h, IEnumerable<HourRec> recs) =>
            recs.Where(r => r.FishId.HasValue).Select(r => r.FishId!.Value).Distinct()
                .Where(h.Known).ToDictionary(f => "id:" + f.ToString(CultureInfo.InvariantCulture), f => h.LocationsOf(f).ToHashSet());

        /// <summary>PHONE-02: объявления других игроков старше этого (часы) отдаются телефону сгруппированными — они уже не
        /// попадают ни в «сутки», ни в «3 суток» (72 ч) при любом более позднем «сейчас»; запас 2 ч — на расхождение часов ПК и телефона.</summary>
        public const double OldPublicHours = 74;
        /// <summary>Время-заглушка для сгруппированных старых объявлений (раньше любого порога периода).</summary>
        public const string OldReal = "2000-01-01 00:00:00";

        /// <summary>v3 (PHONE-02): старые объявления других игроков — группами «час, вид, водоём, совпало со своим, число»
        /// (pubOld), вместо одной строки на объявление. Свои уловы, забросы и покрытие — как раньше. Телефон разворачивает группы
        /// обратно в записи, поэтому расчёт «По часам» не меняется (для pub-серии время записи нужно только для фильтра периода).</summary>
        public static object Export(List<HourRec> recs, List<HourCov> cov, List<HourCast> casts, Dictionary<string, HashSet<uint>>? habitat = null, DateTime? now = null)
        {
            var nowT = now ?? DateTime.Now;
            var cut = nowT.AddHours(-OldPublicHours);
            bool Old(HourRec r) => !r.Mine && r.Real < cut;
            var oldGroups = recs.Where(Old).GroupBy(r => (r.Hour, r.Key, r.Fish, r.FishId, r.LocationId, r.MatchedOwn))
                .Select(g => new object?[] { g.Key.Hour, g.Key.Key, g.Key.Fish, g.Key.FishId, g.Key.LocationId, g.Key.MatchedOwn ? 1 : 0, g.Count() }).ToList();
            return new
            {
                v = 3,
                habitat = (habitat ?? new()).ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(x => x).ToArray()),
                oldReal = OldReal,
                pubOld = oldGroups,
                recs = recs.Where(r => !Old(r)).Select(r => new object?[] { r.Mine ? 1 : 0, r.Real.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), r.Hour, r.Key, r.Fish, r.FishId, r.LocationId, r.MatchedOwn ? 1 : 0 }),
                cov = cov.Select(x => new object?[] { x.Slot.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), x.Hour, Math.Round(x.Minutes, 4), x.Loc }),
                casts = casts.Select(k => new object?[] { k.Real.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), k.Hour, k.LocationId }),
                now = nowT.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            };
        }

        /// <summary>PHONE-02: обратное чтение ответа /api/hourdata (как на телефоне) — для проверки, что сжатие не меняет расчёт.</summary>
        public static (List<HourRec> recs, List<HourCov> cov, List<HourCast> casts) Import(System.Text.Json.JsonElement j)
        {
            static uint? U(System.Text.Json.JsonElement e) => e.ValueKind == System.Text.Json.JsonValueKind.Null ? null : e.GetUInt32();
            static DateTime T(string s, string f) => DateTime.ParseExact(s, f, CultureInfo.InvariantCulture);
            var recs = new List<HourRec>();
            if (j.TryGetProperty("pubOld", out var po))
            {
                var real = T(j.TryGetProperty("oldReal", out var orl) ? orl.GetString()! : OldReal, "yyyy-MM-dd HH:mm:ss");
                foreach (var a in po.EnumerateArray())
                    for (int k = 0, n = a[6].GetInt32(); k < n; k++)
                        recs.Add(new HourRec { Mine = false, Real = real, Hour = a[0].GetInt32(), Key = a[1].GetString()!, Fish = a[2].GetString()!, FishId = U(a[3]), LocationId = U(a[4]), MatchedOwn = a[5].GetInt32() == 1 });
            }
            foreach (var a in j.GetProperty("recs").EnumerateArray())
                recs.Add(new HourRec { Mine = a[0].GetInt32() == 1, Real = T(a[1].GetString()!, "yyyy-MM-dd HH:mm:ss"), Hour = a[2].GetInt32(), Key = a[3].GetString()!, Fish = a[4].GetString()!, FishId = U(a[5]), LocationId = U(a[6]), MatchedOwn = a[7].GetInt32() == 1 });
            var cov = j.GetProperty("cov").EnumerateArray().Select(a => new HourCov { Slot = T(a[0].GetString()!, "yyyy-MM-dd HH:mm"), Hour = a[1].GetInt32(), Minutes = a[2].GetDouble(), Loc = U(a[3]) }).ToList();
            var casts = j.GetProperty("casts").EnumerateArray().Select(a => new HourCast { Real = T(a[0].GetString()!, "yyyy-MM-dd HH:mm:ss"), Hour = a[1].GetInt32(), LocationId = U(a[2]) }).ToList();
            return (recs, cov, casts);
        }
    }
}
