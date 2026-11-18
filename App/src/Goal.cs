using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FisherLogger
{
    // =====================================================================================
    //  MASTER-01 — «Цель»: как приблизить поимку НУЖНОЙ рыбы НУЖНОГО класса.
    //
    //  Событие-цель: на забросе клюнула рыба вида F класса не ниже K (класс и вид известны из поклёвки
    //  s2c 1120 ещё до подсечки, поэтому сорвавшаяся рыба тоже считается).
    //  Знаменатель: свои забросы с известным исходом на водоёмах, где вид водится (Habitat).
    //  Для каждого фактора (место 20×20 м, глубина, наживка, прикормка, игровое время, крючок, поводок, леска)
    //  считается «k нужных на n забросов» и сглаженная оценка (k + m·p0)/(n + m): редкие группы тянутся к среднему,
    //  поэтому «1 из 1» не становится лучшим советом.
    //  Формула: logit p = logit p0 + λ·Σ (logit оценки фактора − logit p0). λ подбирается проверкой «вперёд по
    //  времени» (каждый заброс предсказывается только по более ранним). Если формула не лучше среднего — она
    //  не используется, остаётся описательная статистика. Это частота по вашим данным, а не гарантия клёва.
    // =====================================================================================

    public sealed class GoalSpec
    {
        public uint Fish;
        public int MinClass;          // 0 — любая, 1 — зелёная и выше, 2 — крупная (синяя) и выше, 3 — трофейная (оранжевая)
        public uint? Location;        // null — все водоёмы, где вид водится
    }

    public sealed class GoalOption
    {
        public string Kind = "", Key = "", Name = "", Sub = "";
        public int N, K;
        public double Score, Lo, Hi;
        public bool Enough;
        public int Sessions;          // в скольких рыбалках (запусках программы) была нужная рыба в этой группе
        public bool Solid;            // ≥30 забросов, ≥10 нужных рыб, ≥3 рыбалки
        public uint? LocationId;
        public double? X, Z;
        public long? BaitTid;
        public double Rate => N > 0 ? (double)K / N : 0;
    }

    public sealed class GoalFormula
    {
        public double Lambda;
        public int Tested;
        public double LogLoss, BaseLogLoss;
        public bool Better;
        public double Improvement => BaseLogLoss > 0 ? 1 - LogLoss / BaseLogLoss : 0;
    }

    public sealed class GoalLive
    {
        public string Tone = "idle";          // idle / ok / move / bait / warn
        public string Headline = "", Detail = "";
        public GoalOption? MoveTo;
        public int MissesHere, Threshold;
        public uint? Location;
    }

    public sealed class GameHint { public string Item = "", Category = ""; public double Modifier; }

    public sealed class GoalPlan
    {
        public GoalSpec Spec = new();
        public string Fish = "";
        public int ClassUsed;
        public int N, K;                       // забросов в выборке, из них с нужной рыбой
        public int Sessions;                   // в скольких рыбалках была нужная рыба
        public int KRequested;                 // с рыбой запрошенного класса (до понижения класса)
        public bool HabitatKnown;
        public List<uint> Waters = new();
        public Dictionary<string, List<GoalOption>> Options = new();
        public GoalFormula Formula = new();
        public double P0, P;                   // средняя доля и оценка для лучшего сочетания
        public int MoveAfter;
        public int OthersHere;                 // объявлений других игроков о такой рыбе (пока вы были на водоёме цели)
        public List<GameHint> GameHints = new();
        public List<string> GameBaits = new();           // «ловится на» по описаниям предметов игры
        public int? ClassFromG, ClassToG;                // наблюдавшийся вес рыбы запрошенного класса (свои поклёвки + объявления)
        public string HookAdvice = "";                   // номера крючков по таблице игры для такого веса
        public List<string> Notes = new();
        public bool HasOwn => K >= Goal.MinK;
        public GoalOption? Best(string kind) => Options.TryGetValue(kind, out var l) ? l.FirstOrDefault(o => o.Enough) : null;
        public GoalOption? Top(string kind) => Options.TryGetValue(kind, out var l) ? l.FirstOrDefault(o => o.K > 0) : null;
        public int PerCasts => P > 0 ? (int)Math.Max(1, Math.Round(1 / P)) : 0;
    }

    public static class Goal
    {
        public const int MinN = 8, MinK = 2;
        public const int SolidN = 30, SolidK = 10, SolidSessions = 3;
        /// <summary>Радиус места, м: забросы ближе друг к другу считаются одним местом. Клетка сетки 20 м для этого не годится —
        /// её граница делит одну точку ловли на «хорошую» и «плохую» (замечание DeepSeek, MASTER-01b).</summary>
        public const double PlaceRadiusM = 8;

        /// <summary>Разбить забросы на места: по времени, к ближайшему центру в радиусе, иначе новое место. Исход заброса не используется.</summary>
        public static void EnsurePlaces(BiteAnalytics an)
        {
            if (an.PlacesReady) return;
            an.Places.Clear();
            var byLoc = new Dictionary<uint, List<(string key, double x, double z, int n)>>();
            foreach (var a in an.Attempts.OrderBy(a => a.Ts))
            {
                a.PlaceKey = null;
                // COACH-01: место — где лежит наживка (проверки сервера), а не точка заброса
                if (!a.Location.HasValue || a.PlaceX is not double ax || a.PlaceZ is not double az) continue;
                if (!byLoc.TryGetValue(a.Location.Value, out var list)) byLoc[a.Location.Value] = list = new();
                int bi = -1; double bd = PlaceRadiusM;
                for (int i = 0; i < list.Count; i++)
                {
                    double d = Math.Sqrt(Math.Pow(list[i].x - ax, 2) + Math.Pow(list[i].z - az, 2));
                    if (d <= bd) { bd = d; bi = i; }
                }
                if (bi < 0) { list.Add((a.Location.Value.ToString(CultureInfo.InvariantCulture) + "|" + list.Count.ToString(CultureInfo.InvariantCulture), ax, az, 1)); bi = list.Count - 1; }
                else { var c = list[bi]; list[bi] = (c.key, (c.x * c.n + ax) / (c.n + 1), (c.z * c.n + az) / (c.n + 1), c.n + 1); }
                a.PlaceKey = list[bi].key;
            }
            foreach (var l in byLoc.Values) foreach (var c in l) an.Places[c.key] = (c.x, c.z, c.n);
            an.PlacesReady = true;
        }
        private const double Prior = 8;
        public static readonly string[] Kinds = { "Место", "Глубина", "Наживка", "Прикормка", "Время", "Крючок", "Поводок", "Леска" };
        private static readonly string[] FormulaKinds = { "Место", "Глубина", "Наживка", "Прикормка", "Время" };
        public const string NoGroundbait = "без прикормки";

        // названия классов — как в игре: windex 2 «Крупный» (синий), 3 «Трофейный» (оранжевый); 1 — зелёный, без приставки
        public static string ClassText(int k) => k switch { <= 0 => "любая", 1 => "хорошая (зелёная) и крупнее", 2 => "крупная (синяя) и трофейная", _ => "трофейная (оранжевая)" };
        public static string ClassShort(int k) => k switch { <= 0 => "любой размер", 1 => "зелёная и крупнее", 2 => "крупная (синяя) и трофейная", _ => "трофейная" };

        public static bool IsHit(Attempt a, uint fish, int minClass)
        {
            bool Cls(int? w) => minClass <= 0 || (w.HasValue && w.Value >= minClass);
            if (a.BiteFish == fish && Cls(a.BiteWindex ?? (a.FishId == fish ? a.Windex : null))) return true;
            if (a.FishId == fish && Cls(a.Windex ?? (a.BiteFish == fish ? a.BiteWindex : null))) return true;
            return false;
        }

        public static string? KeyOf(BiteAnalytics an, Attempt a, string kind)
        {
            switch (kind)
            {
                case "Место":
                    EnsurePlaces(an);
                    return a.PlaceKey;
                case "Глубина":
                    return a.BaitDepthCm is double d && d > 0 && d < 10000 ? ((int)Math.Floor(d / 50)).ToString(CultureInfo.InvariantCulture) : null;
                case "Наживка":
                    return a.BaitTid.HasValue && a.BaitTid.Value > 0 ? a.BaitTid.Value.ToString(CultureInfo.InvariantCulture) : null;
                case "Прикормка":
                    if (a.Recipe.Length > 0) return a.Recipe;
                    if ((a.GroundbaitNear ?? 0) > 0) return null;                 // прикормлено, но состав неизвестен — не гадаем
                    return a.TackleStatus.Length > 0 ? NoGroundbait : null;
                case "Время":
                    return a.GameHour.HasValue ? BiteAnalytics.HourBucket(a.GameHour.Value) : null;
                case "Крючок": return a.Hook.Length > 0 ? a.Hook : null;
                case "Поводок": return a.Leader.Length > 0 ? a.Leader : null;
                case "Леска": return a.Line.Length > 0 ? a.Line : null;
            }
            return null;
        }

        private static double Logit(double p) { p = Math.Clamp(p, 1e-4, 1 - 1e-4); return Math.Log(p / (1 - p)); }
        private static double Sigmoid(double z) => 1 / (1 + Math.Exp(-z));

        /// <summary>Забросы, по которым известно, клюнула ли нужная рыба: исход известен либо поклёвка с видом записана.</summary>
        public static List<Attempt> Pool(BiteAnalytics an, GoalSpec spec)
        {
            var all = an.Attempts.Where(a => a.Bite.HasValue || a.BiteFish.HasValue);
            if (spec.Location.HasValue) return all.Where(a => a.Location == spec.Location).OrderBy(a => a.Ts).ToList();
            bool known = an.Habitat.Known(spec.Fish);
            return all.Where(a => !known || an.Habitat.Lives(spec.Fish, a.Location) || IsHit(a, spec.Fish, 0)).OrderBy(a => a.Ts).ToList();
        }

        public static GoalPlan Build(BiteAnalytics an, GoalSpec spec, Func<long, string>? itemName = null)
        {
            EnsurePlaces(an);
            var plan = new GoalPlan { Spec = spec, Fish = an.FishName(spec.Fish), ClassUsed = Math.Clamp(spec.MinClass, 0, 3) };
            plan.HabitatKnown = an.Habitat.Known(spec.Fish);
            plan.Waters = plan.HabitatKnown ? an.Habitat.LocationsOf(spec.Fish).OrderBy(x => x).ToList() : new List<uint>();
            var pool = Pool(an, spec);
            plan.N = pool.Count;
            plan.KRequested = pool.Count(a => IsHit(a, spec.Fish, plan.ClassUsed));
            // мало рыбы нужного класса — совет строится по классу ниже (где водится вид, там и ищут крупную)
            while (plan.ClassUsed > 0 && pool.Count(a => IsHit(a, spec.Fish, plan.ClassUsed)) < 3) plan.ClassUsed--;
            int cls = plan.ClassUsed;
            bool Hit(Attempt a) => IsHit(a, spec.Fish, cls);
            plan.K = pool.Count(Hit);
            plan.Sessions = pool.Where(Hit).Select(a => a.Session).Distinct().Count();
            plan.P0 = plan.N > 0 ? (plan.K + 0.5) / (plan.N + 1) : 0;

            // LEARN-01: без выбранного водоёма — общий по рыбе вывод только если он совпадает на водоёмах:
            // вариант лучше средней доли на одном водоёме и хуже на другом — «на водоёмах по-разному», в совет не идёт
            var baseW = pool.Where(a => a.Location.HasValue).GroupBy(a => a.Location!.Value).ToDictionary(w => w.Key, w => w.Count(Hit) / (double)w.Count());
            foreach (var kind in Kinds)
            {
                var list = new List<GoalOption>();
                foreach (var g in pool.Select(a => (a, key: KeyOf(an, a, kind))).Where(x => x.key != null).GroupBy(x => x.key!))
                {
                    int n = g.Count(), k = g.Count(x => Hit(x.a));
                    var (lo, hi) = BiteAnalytics.Wilson(k, n);
                    var first = g.First().a;
                    int ses = g.Where(x => Hit(x.a)).Select(x => x.a.Session).Distinct().Count();
                    var o = new GoalOption { Kind = kind, Key = g.Key, N = n, K = k, Lo = lo, Hi = hi, Score = (k + Prior * plan.P0) / (n + Prior), Enough = n >= MinN && k >= MinK,
                                             Sessions = ses, Solid = n >= SolidN && k >= SolidK && ses >= SolidSessions };
                    Describe(an, o, first, itemName);
                    if (!spec.Location.HasValue && kind != "Место")
                    {
                        int ag = 0, di = 0;
                        foreach (var w in g.Where(x => x.a.Location.HasValue).GroupBy(x => x.a.Location!.Value).Where(w => w.Count() >= MinN))
                        {
                            double rate = w.Count(x => Hit(x.a)) / (double)w.Count(), b0 = baseW.TryGetValue(w.Key, out var bv) ? bv : 0;
                            if (rate > b0) ag++; else if (rate < b0) di++;
                        }
                        if (ag + di >= 2 && di > 0 && ag <= di) { o.Enough = false; o.Sub = (o.Sub + " · на водоёмах по-разному").Trim(' ', '·'); }
                        else if (ag >= 2 && di == 0) o.Sub = (o.Sub + $" · подтверждено на {ag} водоёмах").Trim(' ', '·');
                    }
                    list.Add(o);
                }
                plan.Options[kind] = list.OrderByDescending(o => o.Enough).ThenByDescending(o => o.Score).ThenByDescending(o => o.K).ThenByDescending(o => o.N).ToList();
            }

            plan.Formula = Fit(an, pool, Hit);
            var best = FormulaKinds.Select(k => plan.Best(k)).Where(o => o != null).Select(o => o!).ToList();
            if (plan.K == 0) plan.P = 0;
            else if (best.Count == 0) plan.P = plan.P0;
            else
            {
                double single = best.Max(o => o.Score);
                plan.P = plan.Formula.Better ? Sigmoid(Logit(plan.P0) + plan.Formula.Lambda * best.Sum(o => Logit(o.Score) - Logit(plan.P0))) : single;
                // Осторожность: лучшее место и лучшая наживка часто одни и те же забросы, поэтому оценка сочетания
                // не может быть лучше, чем уже измеренное: если сочетание «место + наживка» встречалось — берём его
                // сглаженную долю; иначе не даём формуле уйти дальше чем на четверть от лучшего одиночного фактора.
                var bp = plan.Best("Место"); var bb = plan.Best("Наживка");
                var combo = bp != null && bb != null ? pool.Where(a => KeyOf(an, a, "Место") == bp.Key && KeyOf(an, a, "Наживка") == bb.Key).ToList() : new List<Attempt>();
                if (combo.Count >= MinN) plan.P = Math.Min(plan.P, Math.Max(single, (combo.Count(Hit) + Prior * plan.P0) / (combo.Count + Prior)));
                else plan.P = Math.Min(plan.P, single * 1.25);
            }
            plan.MoveAfter = MoveThreshold(plan.Best("Место")?.Score ?? plan.P0);

            uint? where = spec.Location;
            plan.OthersHere = an.PublicDetail.Count(p => p.fish == spec.Fish && (spec.MinClass <= 0 || (p.windex ?? -1) >= spec.MinClass) && (!where.HasValue || p.loc == where));
            plan.GameHints = GameKnowledge.For(spec.Fish);
            plan.GameBaits = GameKnowledge.BaitsFor(spec.Fish);
            // вес рыбы запрошенного класса — по наблюдениям (класс задаёт сервер; пороги у каждого вида свои)
            var ws = an.Attempts.Where(a => a.BiteFish == spec.Fish && a.BiteWeight is > 0 && (spec.MinClass <= 0 || (a.BiteWindex ?? -1) >= spec.MinClass)).Select(a => a.BiteWeight!.Value)
                .Concat(an.PublicDetail.Where(p => p.fish == spec.Fish && p.weight is > 0 && (spec.MinClass <= 0 || (p.windex ?? -1) >= spec.MinClass)).Select(p => p.weight!.Value)).ToList();
            if (ws.Count >= 3)
            {
                plan.ClassFromG = ws.Min(); plan.ClassToG = ws.Max();
                plan.HookAdvice = GameKnowledge.HooksFor(plan.ClassFromG.Value / 1000.0, plan.ClassToG.Value / 1000.0);
            }

            if (plan.ClassUsed < spec.MinClass)
                plan.Notes.Add(plan.KRequested == 0
                    ? $"Рыбы класса «{ClassText(spec.MinClass)}» в ваших данных ещё не было — совет построен по классу «{ClassText(plan.ClassUsed)}»."
                    : $"Рыбы класса «{ClassText(spec.MinClass)}» пока мало ({plan.KRequested}) — совет построен по классу «{ClassText(plan.ClassUsed)}».");
            if (plan.HabitatKnown && spec.Location.HasValue && !an.Habitat.Lives(spec.Fish, spec.Location))
                plan.Notes.Add("На выбранном водоёме этот вид не подтверждён ни вашими уловами, ни списками игры, ни справочником.");
            return plan;
        }

        public static int MoveThreshold(double p)
        {
            if (p <= 0) return 12;
            p = Math.Clamp(p, 0.02, 0.9);
            return (int)Math.Clamp(Math.Ceiling(Math.Log(0.2) / Math.Log(1 - p)), 3, 20);   // с вероятностью ~80 % рыба уже клюнула бы
        }

        private static void Describe(BiteAnalytics an, GoalOption o, Attempt first, Func<long, string>? itemName)
        {
            switch (o.Kind)
            {
                case "Место":
                    {
                        o.LocationId = first.Location;
                        double cx = first.X ?? 0, cz = first.Z ?? 0;
                        if (an.Places.TryGetValue(o.Key, out var pc)) { cx = pc.x; cz = pc.z; }
                        o.X = cx; o.Z = cz;
                        string name;
                        try { name = MapGridCoordinates.ToGrid(o.LocationId, cx, cz) is { } g ? "координаты " + MapGridCoordinates.FormatGrid(g) : FormattableString.Invariant($"X {cx:0}, Z {cz:0}"); }
                        catch { name = FormattableString.Invariant($"X {cx:0}, Z {cz:0}"); }
                        o.Name = name;
                        var sub = new List<string> { an.LocName(o.LocationId) };
                        try { if (Bathymetry.DepthAt(o.LocationId, cx, cz) is double d) sub.Add(FormattableString.Invariant($"глубина {d:0.0} м")); } catch { }
                        o.Sub = string.Join(" · ", sub);
                        break;
                    }
                case "Глубина":
                    {
                        int b = int.Parse(o.Key, CultureInfo.InvariantCulture);
                        o.Name = FormattableString.Invariant($"{b * 0.5:0.0}–{(b + 1) * 0.5:0.0} м"); o.Sub = "глубина наживки";
                        break;
                    }
                case "Наживка":
                    o.BaitTid = first.BaitTid;
                    o.Name = first.Bait.Length > 0 ? first.Bait : (itemName != null && first.BaitTid.HasValue ? Safe(itemName, first.BaitTid.Value) : "");
                    if (o.Name.Length == 0) o.Name = "наживка " + o.Key;
                    o.Name = GameKnowledge.Readable(o.Name);
                    break;
                default:
                    o.Name = GameKnowledge.Readable(o.Key);
                    break;
            }
        }

        private static string Safe(Func<long, string> f, long id) { try { return f(id) ?? ""; } catch { return ""; } }

        /// <summary>Проверка формулы «вперёд по времени»: каждый заброс предсказывается только по более ранним.</summary>
        public static GoalFormula Fit(BiteAnalytics an, List<Attempt> pool, Func<Attempt, bool> hit)
        {
            double[] lambdas = { 0.25, 0.5, 0.75, 1.0 };
            var ll = new double[lambdas.Length]; double baseLl = 0; int tested = 0, n = 0, k = 0;
            var cnt = FormulaKinds.ToDictionary(x => x, x => new Dictionary<string, (int n, int k)>());
            foreach (var a in pool)
            {
                bool y = hit(a);
                var keys = FormulaKinds.Select(kind => (kind, key: KeyOf(an, a, kind))).ToList();
                if (n >= 40 && k >= 3)
                {
                    double p0 = (k + 0.5) / (n + 1), l0 = Logit(p0), sum = 0;
                    foreach (var (kind, key) in keys)
                        if (key != null && cnt[kind].TryGetValue(key, out var c) && c.n > 0) sum += Logit((c.k + Prior * p0) / (c.n + Prior)) - l0;
                    for (int i = 0; i < lambdas.Length; i++)
                    {
                        double p = Math.Clamp(Sigmoid(l0 + lambdas[i] * sum), 1e-4, 1 - 1e-4);
                        ll[i] -= y ? Math.Log(p) : Math.Log(1 - p);
                    }
                    baseLl -= y ? Math.Log(p0) : Math.Log(1 - p0);
                    tested++;
                }
                foreach (var (kind, key) in keys)
                    if (key != null) { cnt[kind].TryGetValue(key, out var c); cnt[kind][key] = (c.n + 1, c.k + (y ? 1 : 0)); }
                n++; if (y) k++;
            }
            var f = new GoalFormula { Tested = tested, BaseLogLoss = baseLl, Lambda = 0, LogLoss = baseLl };
            if (tested == 0) return f;
            int bi = Array.IndexOf(ll, ll.Min());
            f.Lambda = lambdas[bi]; f.LogLoss = ll[bi];
            f.Better = tested >= 30 && ll[bi] < baseLl * 0.98;
            return f;
        }

        /// <summary>Подсказка во время рыбалки: продолжать, сменить наживку или перейти на другое место.</summary>
        public static GoalLive Live(BiteAnalytics an, GoalPlan plan, DateTime now, Func<long, string>? itemName = null)
        {
            var live = new GoalLive();
            var last = an.Attempts.OrderByDescending(a => a.Ts).FirstOrDefault();
            if (last == null || (now - last.Ts).TotalMinutes > 30 || last.Ts > now.AddMinutes(5))
            {
                live.Headline = "Сейчас вы не ловите";
                live.Detail = "Подсказки появятся после первого заброса: программа сама увидит место, наживку и скажет, продолжать или менять.";
                return live;
            }
            var recent = an.Attempts.Where(a => a.Session == last.Session && (last.Ts - a.Ts).TotalHours <= 3).OrderBy(a => a.Ts).ToList();
            uint? loc = last.Location ?? recent.LastOrDefault(a => a.Location.HasValue)?.Location;
            live.Location = loc;
            int cls = plan.ClassUsed; uint fish = plan.Spec.Fish;
            const string what = "нужная рыба";

            if (loc.HasValue && plan.HabitatKnown && !an.Habitat.Lives(fish, loc))
            {
                live.Tone = "warn";
                live.Headline = $"На этом водоёме {plan.Fish} не подтверждён";
                live.Detail = plan.Waters.Count > 0 ? "Этот вид ловится: " + string.Join(", ", plan.Waters.Take(4).Select(w => an.LocName(w))) + "." : "";
                return live;
            }
            if (plan.Spec.Location.HasValue && loc.HasValue && loc != plan.Spec.Location)
            {
                live.Tone = "warn";
                live.Headline = "Вы на другом водоёме: " + an.LocName(loc);
                live.Detail = "Цель задана для: " + an.LocName(plan.Spec.Location) + ". Смените водоём в цели или переезжайте.";
                return live;
            }

            // цель «любой водоём»: во время рыбалки сравниваем места и наживки только текущего водоёма
            if (!plan.Spec.Location.HasValue && loc.HasValue)
            {
                var local = Build(an, new GoalSpec { Fish = fish, MinClass = cls, Location = loc }, itemName);
                if (local.ClassUsed == cls) plan = local;
                else { plan = local; cls = local.ClassUsed; }
            }
            string? cur = KeyOf(an, last, "Место");
            var places = plan.Options.TryGetValue("Место", out var pl) ? pl : new List<GoalOption>();
            var here = cur != null ? places.FirstOrDefault(o => o.Key == cur) : null;
            int misses = 0;
            foreach (var a in Enumerable.Reverse(recent))
            {
                if (KeyOf(an, a, "Место") != cur) continue;
                if (!(a.Bite.HasValue || a.BiteFish.HasValue)) continue;           // заброс ещё в воде
                if (IsHit(a, fish, cls)) break;
                misses++;
            }
            live.MissesHere = misses;
            live.Threshold = MoveThreshold(here != null && here.N >= MinN ? here.Score : plan.P0);
            if (plan.K < MinK)
            {
                // своей статистики по этой рыбе здесь ещё нет — не изображаем уверенность и не гоняем игрока с места на место
                live.Tone = "ok";
                live.Headline = "Данных по этой рыбе здесь пока мало";
                live.Detail = "Ловите как обычно: программа записывает место, наживку и поклёвки. Совет «продолжать или менять» появится после первых поклёвок нужной рыбы.";
                return live;
            }
            var bestBait = plan.Best("Наживка");
            var curBait = plan.Options.TryGetValue("Наживка", out var bl) && last.BaitTid.HasValue ? bl.FirstOrDefault(o => o.BaitTid == last.BaitTid) : null;

            if (misses >= live.Threshold)
            {
                live.MoveTo = places.Where(o => o.Key != cur && o.K > 0 && (!loc.HasValue || o.LocationId == loc)).OrderByDescending(o => o.Enough).ThenByDescending(o => o.Score).FirstOrDefault();
                live.Tone = "move";
                live.Headline = $"Пора сменить место: {UiKit.Casts(misses)} без нужной рыбы";
                if (live.MoveTo != null)
                {
                    string dist = "";
                    if (last.X.HasValue && last.Z.HasValue && live.MoveTo.X.HasValue && live.MoveTo.Z.HasValue)
                        dist = FormattableString.Invariant($", примерно {Math.Sqrt(Math.Pow(live.MoveTo.X.Value - last.X.Value, 2) + Math.Pow(live.MoveTo.Z.Value - last.Z.Value, 2)):0} м отсюда");
                    live.Detail = $"Переходите: {live.MoveTo.Name}{(live.MoveTo.Sub.Length > 0 ? " (" + live.MoveTo.Sub + ")" : "")}{dist} — там нужная рыба: {live.MoveTo.K} на {UiKit.Casts(live.MoveTo.N)}.";
                }
                else live.Detail = "Других проверенных мест для этой рыбы пока нет — сместитесь на 20–40 м и попробуйте другую глубину.";
                return live;
            }
            if (bestBait != null && last.BaitTid.HasValue && bestBait.BaitTid != last.BaitTid && bestBait.Score > 1.5 * (curBait?.Score ?? plan.P0) && misses >= 2)
            {
                live.Tone = "bait";
                live.Headline = "Поставьте наживку: " + bestBait.Name;
                live.Detail = $"На неё {what}: {bestBait.K} на {UiKit.Casts(bestBait.N)}" + (curBait != null ? $", на текущую ({curBait.Name}) — {curBait.K} на {UiKit.Casts(curBait.N)}." : "; на текущую наживку таких поклёвок ещё не было.");
                return live;
            }
            live.Tone = "ok";
            live.Headline = misses == 0 ? "Продолжайте здесь" : $"Продолжайте здесь: {UiKit.Casts(misses)} без нужной рыбы";
            live.Detail = here != null && here.K > 0
                ? $"На этом месте {what}: {here.K} на {UiKit.Casts(here.N)}. Если не клюнет ещё {UiKit.Casts(Math.Max(1, live.Threshold - misses))} — подскажу, куда перейти."
                : $"Здесь нужной рыбы ещё не было. Дайте месту {UiKit.Casts(live.Threshold)} — потом подскажу, куда перейти.";
            return live;
        }
    }

    /// <summary>MASTER-01: знание из файлов игры (Weapons.txt → bait_modifiers.json): предметы с модификатором «целевое привлечение»
    /// (kind 2) для вида. Смысл kind 2 — PROBABLE (совпадает с названиями прикормок: «Линь-Карась» → Линь, Карась, Карась золотой).</summary>
    public static class GameKnowledge
    {
        private static Dictionary<uint, List<GameHint>>? _byFish;
        private static readonly object Gate = new();

        public static List<GameHint> For(uint fish)
        {
            lock (Gate) { _byFish ??= Load(); }
            return _byFish.TryGetValue(fish, out var l) ? l : new List<GameHint>();
        }

        private static Dictionary<uint, List<GameHint>> Load()
        {
            var res = new Dictionary<uint, List<GameHint>>();
            try
            {
                string? path = new[] { SafePath(() => Path.Combine(PacketDeepParser.ResolveDefaultDataDir(), "bait_modifiers.json")), SafePath(() => AppPaths.BaitModifiersPath) }
                    .FirstOrDefault(p => p != null && File.Exists(p));
                if (path == null) return res;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("items", out var items)) return res;
                foreach (var it in items.EnumerateArray())
                {
                    string name = it.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    string cat = it.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "";
                    if (name.Length == 0 || name.StartsWith("--") || !it.TryGetProperty("modifiers", out var mods)) continue;
                    foreach (var m in mods.EnumerateArray())
                    {
                        if (!m.TryGetProperty("kind", out var k) || k.GetInt32() != 2) continue;
                        if (!m.TryGetProperty("fish_id", out var f) || !f.TryGetUInt32(out uint fid)) continue;
                        double mod = m.TryGetProperty("modifier", out var mv) && mv.TryGetDouble(out double d) ? d : 0;
                        if (mod <= 0) continue;
                        if (!res.TryGetValue(fid, out var l)) res[fid] = l = new List<GameHint>();
                        if (!l.Any(x => x.Item == name)) l.Add(new GameHint { Item = name, Category = cat, Modifier = mod });
                    }
                }
                foreach (var l in res.Values) l.Sort((a, b) => b.Modifier.CompareTo(a.Modifier));
            }
            catch { }
            return res;
        }

        private static string? SafePath(Func<string> f) { try { return f(); } catch { return null; } }

        /// <summary>Три теста в игре названы иероглифами (так в файлах игры) — добавляем понятное имя из немецкой/английской версии.</summary>
        public static string Readable(string name) => name
            .Replace("速攻1号", "速攻1号 (Fast Attack)").Replace("速攻2号", "速攻2号 (Quick Attack)").Replace("蓝鲫3号", "蓝鲫3号 (Blue Carp)");

        private static Dictionary<uint, List<string>>? _baits;
        /// <summary>Наживки и приманки, в описании которых игра называет этот вид («ловится на»). Это рекомендация игры, не запрет на остальное;
        /// список есть примерно у трети предметов.</summary>
        public static List<string> BaitsFor(uint fish)
        {
            lock (Gate)
            {
                if (_baits == null)
                {
                    _baits = new Dictionary<uint, List<string>>();
                    try
                    {
                        string? path = new[] { SafePath(() => Path.Combine(PacketDeepParser.ResolveDefaultDataDir(), "baits_map.json")), SafePath(() => Path.Combine(AppPaths.DataDirectory, "baits_map.json")) }
                            .FirstOrDefault(p => p != null && File.Exists(p));
                        if (path != null)
                        {
                            using var doc = JsonDocument.Parse(File.ReadAllText(path));
                            if (doc.RootElement.TryGetProperty("baits", out var baits))
                                foreach (var b in baits.EnumerateObject())
                                {
                                    string name = b.Value.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                                    string cat = b.Value.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "";
                                    if (name.Length == 0 || name.StartsWith("--") || !(cat == "bait" || cat == "lure" || cat == "fly")) continue;
                                    if (!b.Value.TryGetProperty("fish_ids", out var ids) || ids.ValueKind != JsonValueKind.Array) continue;
                                    foreach (var id in ids.EnumerateArray())
                                        if (id.TryGetUInt32(out uint fid))
                                        {
                                            if (!_baits.TryGetValue(fid, out var l)) _baits[fid] = l = new List<string>();
                                            if (cat == "bait") l.Insert(0, name); else l.Add(name);     // натуральные наживки — первыми
                                        }
                                }
                        }
                    }
                    catch { }
                }
                return _baits.TryGetValue(fish, out var res) ? res : new List<string>();
            }
        }

        // Таблица игры hooksize.txt (подсказка клиента getFishSize4HookStr): номер крючка → вес рыбы, кг (от; до)
        private static readonly (int no, double min, double max)[] HookTable =
        {
            (25, 0.005, 2.0), (24, 0.005, 2.0), (23, 0.022, 2.0), (22, 0.054, 2.0), (21, 0.087, 2.0), (20, 0.120, 2.0), (19, 0.153, 2.0), (18, 0.186, 2.0), (17, 0.252, 2.0),
            (16, 0.348, 2.606), (15, 0.443, 3.325), (14, 0.531, 3.986), (13, 0.610, 4.572), (12, 0.688, 5.157), (11, 0.766, 5.743), (10, 0.844, 6.329), (9, 0.922, 6.914),
            (8, 1.000, 7.502), (7, 1.524, 11.429), (6, 2.141, 16.056), (5, 4.096, 30.722), (4, 6.282, 47.112), (3, 8.134, 61.003), (2, 9.986, 74.893), (1, 10.710, 80.323),
            (0, 11.427, 85.700), (-1, 12.143, 91.076), (-2, 12.860, 96.452), (-3, 13.577, 101.828), (-4, 14.294, 107.204), (-5, 15.011, 112.580), (-6, 16.607, 124.550),
            (-7, 18.245, 136.837), (-8, 19.883, 149.124), (-9, 21.522, 161.411), (-10, 23.160, 173.698),
            // HINTS-01: статья игры «Крючки» (book_2333) — №17/0 (FO #4) 35–120 кг, №22/0 (FO #5) 50–400 кг; примерные, в hooksize.txt их нет
            (-16, 35.0, 120.0), (-21, 50.0, 400.0)
        };
        /// <summary>ZONES-02: диапазон веса рыбы (кг) для крючка с номером base_par по таблице игры; null — номера нет в таблице.</summary>
        public static (double min, double max)? HookRange(int no) { foreach (var h in HookTable) if (h.no == no) return (h.min, h.max); return null; }
        public static string HookNo(int no) => no > 0 ? "№" + no : no == 0 ? "№1/0" : "№" + (1 - no) + "/0";

        /// <summary>
        /// HINTS-01: цвет поводка под цвет воды — немного больше поклёвок трофея (статья игры «Поводки», book_2334).
        /// Только водоёмы, названные в игре: Франция — жёлтый, Финляндия — голубой, Каспий — морской. Остальные неизвестны — null (не подсказываем).
        /// </summary>
        public static string? LeaderColor(uint? locationId) => locationId switch { 3 => "жёлтый", 4 => "голубой", 10 => "морской", _ => null };

        /// <summary>
        /// HINTS-01: крючок под рыбу на границе «крупная (синяя) → трофейная» (порог трофея из fh_fish_weights, поле unique).
        /// Подходит крючок, который такую рыбу не отсекает (нижняя граница ≤ порога) и держит трофей до +50 % (верхняя ≥ 1,5 × порога).
        /// Крупнее крючок из диапазона — меньше мелочи (статья игры); мельче — чаще сход крупной. Порога нет — подсказки нет (null).
        /// </summary>
        public static string? TrophyHookHint(uint? fishId, string? fishName)
        {
            if (FishWeightsCatalog.Resolve(fishId, fishName) is not { UniqueG: > 0 } w) return null;
            double kg = w.UniqueG / 1000.0;
            var ok = HookTable.Where(h => h.min <= kg && h.max >= kg * 1.5).Select(h => h.no).ToList();
            if (ok.Count == 0) return null;
            // лучший — самый крупный, который рыбу с границы ещё не отсекает (мелочь клюёт реже); запас — два следующих размера мельче
            var pick = ok.OrderBy(n => n).Take(3).ToList();
            string wt = kg >= 1 ? kg.ToString(kg >= 10 ? "0" : "0.#", CultureInfo.GetCultureInfo("ru-RU")) + " кг" : w.UniqueG + " г";
            string alt = pick.Count switch { 1 => "", 2 => " (или " + HookNo(pick[1]) + ")", _ => " (или " + HookNo(pick[1]) + "–" + HookNo(pick[2]) + ")" };
            return $"Крючок под крупную и трофейную (трофей от ~{wt}): {HookNo(pick[0])}{alt}. Крупнее — отсечёт и её, мельче — больше мелочи и сходов.";
        }

        /// <summary>Номера крючков, которые таблица игры считает подходящими для рыбы весом от minKg до maxKg.</summary>
        public static string HooksFor(double minKg, double maxKg)
        {
            var ok = HookTable.Where(h => h.min <= minKg && h.max >= maxKg).Select(h => h.no).ToList();
            if (ok.Count == 0) return "";
            return ok.Count == 1 ? HookNo(ok[0]) : HookNo(ok.Min()) + "–" + HookNo(ok.Max());
        }
    }
}
