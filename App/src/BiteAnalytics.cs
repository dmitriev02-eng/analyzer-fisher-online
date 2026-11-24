using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace FisherLogger
{
    // =====================================================================================
    // ADVISOR-01: самообучающийся «Советник».
    //
    // Принципы честности (проверяются тестами):
    //  * База открывается ТОЛЬКО для чтения (Mode=ReadOnly). Ничего в catches.db не пишется.
    //  * Попытка = свой заброс (таблица casts). Метка (FORMULA-05): поклёвка = caught, или событие bite/hook у missed/unknown/pending;
    //    missed только с пустой поклёвкой (empty_bite) — без поклёвки; no_bite — без поклёвки. unknown/pending без событий — не идут в расчёт.
    //  * Совет выдаётся только при достаточных данных (настройки: мин. забросов и поимок в группе).
    //    Иначе — «мало данных» с указанием, чего не хватает. Догадки не выдаются за совет.
    //  * Доля поимок даётся с 80%-интервалом (Уилсон); сортировка — по нижней границе интервала.
    //  * Публичные объявления (cmd 6) — только индекс активности «где эту рыбу ловят другие»,
    //    никогда не знаменатель вероятности (в объявлении нет водоёма — используется водоём,
    //    где были ВЫ при приёме).
    //  * Формула клёва — логистическая регрессия «поклёвка за заброс». Время ожидания в признаки НЕ входит (зависит от исхода).
    //  * FIX-03: водоём заброса — по точке спавна из проверок сервера (одинаково для всех исходов), а не из строки улова.
    //    Подбирается только при достаточном объёме и принимается, только если на кросс-проверке
    //    (5 частей) она лучше «среднего по всем забросам». Принятая формула дополнительно проверяется
    //    на новых забросах, которых не видела; если там она хуже среднего — отключается.
    //    История всех подобранных формул хранится (только дописывается) в analytics_models.json.
    // =====================================================================================

    public sealed class Attempt
    {
        public long CastId;
        public DateTime Ts;
        public string Session = "";
        public int Rod;
        public double? X, Z;
        public uint? Location;
        public bool LocationInferred;
        public bool LocationByRule;        // AUDIT-02: водоём по правилу «точка спавна = водоём×10+k» (PROBABLE), не по наблюдённой паре
        public long? BaitTid;
        public string Bait = "";
        public string Outcome = "";
        public bool? Bite;                 // null — исход неизвестен
        public uint? FishId;               // пойманный вид (если поймано)
        public string Fish = "";
        public int? Weight;
        public int? Windex;
        public double? ExposureMin;        // минут от заброса до поклёвки / последнего события
        public double? BaitDepthCm;        // средняя depth1 проверок «на дне»
        public int? GroundbaitNear;        // вносов прикорма рядом до заброса
        public double? MeanChance;         // среднее «chance» из ответов сервера (смысл PROBABLE)
        /// <summary>FORMULA-06: ранний шанс сервера — среднее тактов 2–4 (≈16–32 с; 1-й такт — «прогрев») до первой поклёвки. Только для живой оценки,
        /// в формулу «до заброса» не входит (его нет до заброса).</summary>
        public double? EarlyChance;
        public int ChanceTicks;
        public double? AirTemp, Cloud, Rain;
        public int? GameHour;              // игровой час (множитель времени PROBABLE)
        // MASTER-01: цель «нужная рыба нужного класса». Первая поклёвка заброса (s2c 1120 flag=1) известна ДО подсечки:
        // вид, вес и класс — поэтому считается и сорвавшаяся рыба.
        public uint? BiteFish;
        public int? BiteWeight;
        public int? BiteWindex;
        // снасть заброса (cast_tackle); пусто, если снимка нет или он неоднозначен
        public string Line = "", Leader = "", Hook = "", Feeder = "";
        public double? LeaderKg;           // первое число base_par поводка (тест, кг) — PROBABLE
        public string TackleStatus = "";
        /// <summary>Состав прикорма в кормушке/на забросе: «основа + аромат + добавки» (названия предметов), пусто — без прикорма/неизвестно.</summary>
        public string Recipe = "";
        public List<long> RecipeItems = new();
        public long? GroundbaitItem;
        /// <summary>UI-02-4-FIX (правка A): целевая рыба прикорма (kind 2) — fish_lang_id → сила модификатора. Пусто — без прикорма/не записано.</summary>
        public List<(uint Fish, double Strength)> GroundbaitTargets = new();
        /// <summary>MASTER-01: место заброса — группа точек в радиусе нескольких метров (не клетка сетки), см. Goal.EnsurePlaces.</summary>
        public string? PlaceKey;
        // COACH-01 / FORMULA-01: признаки заброса из проверок сервера (c2s 1120), известны до исхода
        /// <summary>Где лежит наживка: медиана bait_x/z проверок «на дне» (не точка заброса; в сессии 11.11 расхождение до 8,5 м).</summary>
        public double? BaitX, BaitZ;
        /// <summary>Клетка сетки 5 м, которую клиент отправляет серверу (grid_x:grid_z, чаще всего встречавшаяся).</summary>
        public string Cell5 = "";
        /// <summary>Глубина дна в точке наживки по рельефу игры (Bathymetry), м; null — нет рельефа водоёма.</summary>
        public double? BottomDepthM;
        /// <summary>На сколько наживка выше дна, м (depth2 − depth1; ≈0 — на дне, у поплавка обычно ~1 м).</summary>
        public double? AboveBottomM;
        /// <summary>Тип удилища из пакета (0 поплавок, 1 фидер, 2 спиннинг…), идентификатор удочки.</summary>
        public int? RodCode;
        public long? RodUid;
        /// <summary>Первый и последний такт проверки сервера; число тактов.</summary>
        public DateTime? FirstTick, LastTick;
        public int Ticks;
        /// <summary>Пустые поклёвки (empty_bite: рыба тронула наживку, засечки нет) на этом забросе.</summary>
        public int EmptyBites;
        /// <summary>Крючок по base_par игры: острота / размер (смысл — по локализации игры, влияние на засечку — PROBABLE).</summary>
        public int? HookSharp, HookSize;
        // ZONES-02 / RESEARCH-01: прикорм, крючок, кормушка, поводок (известны ДО исхода заброса)
        /// <summary>Расстояние от наживки до ближайшего вноса прикорма за 60 мин до заброса, м (≤30; нет прикорма — 30).</summary>
        public double? GroundbaitDistM;
        /// <summary>Сколько минут работает прикорм в точке наживки (внос в радиусе GroundbaitRadiusM); нет — 60.</summary>
        public double? GroundbaitAgeMin;
        /// <summary>Крючок по таблице игры hooksize.txt: рыба от MinKg до MaxKg.</summary>
        public double? HookMinKg, HookMaxKg;
        /// <summary>Кормушка (base_par «граммы/время/маскировка»): время прикорма, реал. мин; маскировка, %. Без кормушки — 0.</summary>
        public double? FeederTimeMin, FeederCamo;
        /// <summary>Поводок (base_par «кг/толщина мм/длина»): толщина, мм; длина (единицы игры, обычно см).</summary>
        public double? LeaderMm, LeaderLen;
        public double? SoakMin => FirstTick.HasValue && LastTick.HasValue ? (LastTick.Value - FirstTick.Value).TotalMinutes : null;
        /// <summary>Точка заброса для «мест»: где лежит наживка, иначе точка заброса.</summary>
        public double? PlaceX => BaitX ?? X;
        public double? PlaceZ => BaitZ ?? Z;
    }

    public sealed class EvidenceRow
    {
        public string Group { get; set; } = "";     // «Водоём», «Участок», «Наживка», «Игровое время»
        public string Item { get; set; } = "";
        public int Attempts { get; set; }
        public int Successes { get; set; }
        public double Rate { get; set; }
        public double Lo { get; set; }
        public double Hi { get; set; }
        public bool Advice { get; set; }
        public string Verdict { get; set; } = "";
        public string Level { get; set; } = "";     // CONFIRMED / PROBABLE
        [JsonIgnore] public uint? LocationId { get; set; }
        // ADVISOR-UX-01: structured parts for the player card (Item stays for the analyst table)
        [JsonIgnore] public string Name { get; set; } = "";     // waterbody / bait / time name without technical suffixes
        [JsonIgnore] public double? X { get; set; }             // centre of the 20 m cell («Участок»), world X/Z
        [JsonIgnore] public double? Z { get; set; }
        [JsonIgnore] public long? BaitTid { get; set; }
    }

    public sealed class PresenceRow
    {
        public uint? LocationId { get; set; }
        public string Waterbody { get; set; } = "";
        public uint FishId { get; set; }
        public string Fish { get; set; } = "";
        public int OwnCatches { get; set; }
        public int PublicSeen { get; set; }
        public int MaxWindex { get; set; } = -1;
        public int Zones { get; set; }
    }

    public sealed class SpeciesAdvice
    {
        public uint FishId { get; set; }
        public string Fish { get; set; } = "";
        public bool HasAdvice { get; set; }
        public int OwnCatches { get; set; }
        public int PublicSeen { get; set; }
        /// <summary>AUDIT-02: водоёмы, где вид подтверждён (пусто — сведений нет).</summary>
        public List<uint> HabitatLocations { get; set; } = new();
        public List<PresenceRow> Presence { get; set; } = new();
        public List<EvidenceRow> Rows { get; set; } = new();
        public List<string> Notes { get; set; } = new();
    }

    public sealed class FormulaModel
    {
        public int Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ActivatedAt { get; set; }
        public DateTime TrainedUntil { get; set; }
        public bool Accepted { get; set; }
        public bool Active { get; set; }
        public string Status { get; set; } = "";
        public int N { get; set; }
        public int Pos { get; set; }
        public int Neg { get; set; }
        public double BaseRate { get; set; }
        public double CvLogLoss { get; set; }
        public double BaselineLogLoss { get; set; }
        public double CvBrier { get; set; }
        public double Improvement { get; set; }
        public int FoldsBetter { get; set; }
        public int Folds { get; set; }
        public List<string> Features { get; set; } = new();
        public double[] Weights { get; set; } = Array.Empty<double>();
        public double Bias { get; set; }
        public double[] Means { get; set; } = Array.Empty<double>();
        public double[] Stds { get; set; } = Array.Empty<double>();
        public int ForwardN { get; set; }
        public double? ForwardLogLoss { get; set; }
        public double? ForwardBaseline { get; set; }
        public string DeactivatedReason { get; set; } = "";
        /// <summary>FIX-03: version of the feature construction. 0 = before FIX-03 (waterbody leaked the outcome).</summary>
        public int FeatureSchema { get; set; }
    }

    public sealed class ModelRegistry
    {
        public List<FormulaModel> History { get; set; } = new();
        public string LastStatus { get; set; } = "";
        public DateTime? LastCheck { get; set; }
        [JsonIgnore] public FormulaModel? ActiveModel => History.LastOrDefault(m => m.Active);

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
        public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "analytics_models.json");

        public static ModelRegistry Load(string path)
        {
            try { if (File.Exists(path)) return JsonSerializer.Deserialize<ModelRegistry>(File.ReadAllText(path), Json) ?? new(); }
            catch { }
            return new ModelRegistry();
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, true);
        }
    }

    public sealed class AnalyticsOverview
    {
        public int Casts { get; set; }
        public int Labeled { get; set; }
        public int Bites { get; set; }
        public int NoBites { get; set; }
        public int UnknownOutcome { get; set; }
        public int OwnCatches { get; set; }
        public int CatchesLinkedToCasts { get; set; }
        public int PublicAnnouncements { get; set; }
        public int Species { get; set; }
        public int Waterbodies { get; set; }
        public string FormulaStatus { get; set; } = "";
        public List<string> Missing { get; set; } = new();
    }

    public sealed class BiteAnalytics
    {
        public AnalyticsSettings S { get; }
        /// <summary>GB-CALC-01: путь к БД, из которой загружена аналитика (для калькулятора прикормки).</summary>
        public string DbPath { get; private set; } = "";
        public List<Attempt> Attempts { get; private set; } = new();
        /// <summary>Свои подтверждённые уловы (в т.ч. без привязки к забросу): вид, водоём, windex, время, координаты.</summary>
        public List<(uint fish, uint? loc, int? windex, DateTime ts, double? x, double? z)> OwnCatches { get; private set; } = new();
        public List<(uint fish, uint? loc, DateTime ts)> Public { get; private set; } = new();
        /// <summary>MASTER-01: объявления других игроков с классом и весом (водоём — где были вы при приёме).</summary>
        public List<(uint fish, uint? loc, int? windex, int? weight, DateTime ts)> PublicDetail { get; private set; } = new();
        /// <summary>MASTER-01: центры мест заброса (ключ «водоём|номер»), заполняется Goal.EnsurePlaces.</summary>
        public Dictionary<string, (double x, double z, int n)> Places { get; } = new();
        public bool PlacesReady;
        private readonly Func<uint, string> _fishName;
        private readonly Func<uint?, string> _locName;

        public BiteAnalytics(AnalyticsSettings s, Func<uint, string>? fishName = null, Func<uint?, string>? locName = null)
        {
            S = s;
            _fishName = fishName ?? (id => "вид " + id);
            _locName = locName ?? (id => id.HasValue ? "водоём " + id.Value : NoWater);
        }

        /// <summary>UI-02/3: records without a water. Announcements of other players before 07.11.2026 did not store where you were,
        /// old imports have none either; there is nothing to restore them from (no own catches near in time, Player.log keeps only
        /// the last sessions). Never offered as a water to choose.</summary>
        public const string NoWater = "водоём не записан (старые записи)";
        /// <summary>How many records have no water: own catches and announcements of other players.</summary>
        public (int own, int pub) NoWaterCount => (OwnCatches.Count(x => !x.loc.HasValue), Public.Count(p => !p.loc.HasValue));

        public string FishName(uint id) { try { var n = _fishName(id); return string.IsNullOrWhiteSpace(n) ? "вид " + id : n; } catch { return "вид " + id; } }
        public string LocName(uint? id) { if (!id.HasValue) return NoWater; try { var n = _locName(id); return string.IsNullOrWhiteSpace(n) ? "водоём " + id : n; } catch { return "водоём " + id; } }

        public static BiteAnalytics FromData(AnalyticsSettings s, List<Attempt> attempts,
            List<(uint, uint?, int?, DateTime, double?, double?)>? own = null, List<(uint, uint?, DateTime)>? pub = null)
            => new(s) { Attempts = attempts, OwnCatches = own ?? new(), Public = pub ?? new() };

        // ------------------------------------------------------------------ загрузка (только чтение)
        private static readonly string[] TsFormats = { "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss", "o" };
        private static DateTime? Ts(object? o)
        {
            if (o is not string s || s.Length == 0) return null;
            if (!DateTime.TryParseExact(s, TsFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) &&
                !DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return null;
            if (t.Year < 2020 || t > DateTime.Now.AddDays(1)) return null;   // неправдоподобные даты (напр. 3995 из импорта)
            return t;
        }
        private static double? D(object? o) => o switch { null => null, DBNull => null, double d => d, long l => l, int i => i, float f => f,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v, _ => null };
        private static long? L(object? o) => o switch { long l => l, int i => i, double d => (long)d, string s when long.TryParse(s, out var v) => v, _ => null };

        private static List<object?[]> Q(SqliteConnection c, string sql)
        {
            var res = new List<object?[]>();
            try
            {
                using var q = new SqliteCommand(sql, c); using var r = q.ExecuteReader();
                while (r.Read()) { var row = new object?[r.FieldCount]; for (int i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i); res.Add(row); }
            }
            catch (SqliteException) { }   // таблицы может не быть в старой базе — тогда просто нет данных
            return res;
        }

        public static BiteAnalytics Load(string dbPath, AnalyticsSettings s, Func<uint, string>? fishName = null, Func<uint?, string>? locName = null, Func<long, string>? baitName = null)
        {
            var a = new BiteAnalytics(s, fishName, locName) { DbPath = dbPath };
            using var c = new SqliteConnection("Data Source=" + dbPath + ";Mode=ReadOnly;Pooling=False");
            c.Open();

            var catchByCast = new Dictionary<long, object?[]>();
            foreach (var r in Q(c, "SELECT cast_id, fish_lang_id, weight, windex, location_id FROM catches WHERE cast_id IS NOT NULL AND status <> 'skipped' ORDER BY id"))
                if (L(r[0]) is long cid) catchByCast[cid] = r;

            foreach (var r in Q(c, "SELECT fish_lang_id, location_id, windex, timestamp, cast_x, cast_z FROM catches WHERE status = 'confirmed' AND fish_lang_id IS NOT NULL"))
                if (L(r[0]) is long f && f > 0 && Ts(r[3]) is DateTime t)
                    a.OwnCatches.Add(((uint)f, L(r[1]) is long lo ? (uint)lo : null, L(r[2]) is long w ? (int)w : null, t, D(r[4]), D(r[5])));

            foreach (var r in Q(c, "SELECT fish_lang_id, MAX(weight) FROM catches WHERE status = 'confirmed' AND fish_lang_id IS NOT NULL AND weight IS NOT NULL GROUP BY fish_lang_id"))
                if (L(r[0]) is long f && f > 0 && L(r[1]) is long w && w > 0) a.OwnMaxWeight[(uint)f] = (int)Math.Min(w, int.MaxValue);

            foreach (var r in Q(c, "SELECT fish_lang_id, receiver_location_id, received_at, windex, weight FROM public_catches WHERE fish_lang_id IS NOT NULL"))
                if (L(r[0]) is long f && f > 0 && Ts(r[2]) is DateTime t)
                {
                    a.Public.Add(((uint)f, L(r[1]) is long lo ? (uint)lo : null, t));
                    a.PublicDetail.Add(((uint)f, L(r[1]) is long lo2 ? (uint)lo2 : null, (int?)L(r[3]), (int?)L(r[4]), t));
                }

            var checks = new Dictionary<long, (List<long> locs, List<double> depth, List<double> chance, DateTime? last)>();
            var ticks = new Dictionary<long, TickInfo>();   // COACH-01
            foreach (var r in Q(c, "SELECT cast_id, location_id, depth1_raw, flag_on_ground, response_shance, timestamp, bait_x, bait_z, grid_x, grid_z, rod_code, rod_uid, depth2_raw FROM server_checks WHERE cast_id IS NOT NULL"))
            {
                if (L(r[0]) is not long cid) continue;
                if (!checks.TryGetValue(cid, out var e)) e = (new(), new(), new(), null);
                if (L(r[1]) is long lo) e.locs.Add(lo);
                if (L(r[3]) == 1 && D(r[2]) is double d) e.depth.Add(d);
                if (Ts(r[5]) is DateTime t && (e.last == null || t > e.last)) e.last = t;
                checks[cid] = e;
                if (!ticks.TryGetValue(cid, out var ti)) ticks[cid] = ti = new TickInfo();
                ti.N++;
                if (Ts(r[5]) is DateTime tt) { if (ti.First == null || tt < ti.First) ti.First = tt; if (ti.Last == null || tt > ti.Last) ti.Last = tt; }
                // точка наживки: такты «на дне»; у поплавка их нет — тогда все такты (наживка в толще воды)
                bool onGround = L(r[3]) == 1;
                if (D(r[6]) is double bx && D(r[7]) is double bz) { if (onGround) { ti.Xs.Add(bx); ti.Zs.Add(bz); } else { ti.AllX.Add(bx); ti.AllZ.Add(bz); } }
                if (D(r[8]) is double gx && D(r[9]) is double gz) { string k = gx.ToString("0.#", CultureInfo.InvariantCulture) + ":" + gz.ToString("0.#", CultureInfo.InvariantCulture); var cells = onGround ? ti.Cells : ti.AllCells; cells[k] = cells.TryGetValue(k, out int cn) ? cn + 1 : 1; }
                if (L(r[10]) is long rc) ti.RodCode = (int)rc;
                // depth2 = глубина дна в точке наживки (CONFIRMED DeepSeek T20261112-2050: совпадает с рельефом игры, 99 % в пределах 20 см);
                // depth1 = глубина наживки (спуск); depth2 − depth1 — насколько наживка выше дна
                if (D(r[12]) is double d2 && d2 > 0 && d2 < 100000) { ti.Bottom.Add(d2); if (D(r[2]) is double d1 && d1 > 0) ti.Above.Add(d2 - d1); }
                if (L(r[11]) is long ru) ti.RodUid = ru;
            }
            // FORMULA-05 (B2): настоящая поклёвка = событие bite или hook; одна «пустая поклёвка» (empty_bite) поклёвкой не считается
            var realBite = new HashSet<long>();
            foreach (var r in Q(c, "SELECT DISTINCT cast_id FROM cast_events WHERE event_type IN ('bite', 'hook') AND cast_id IS NOT NULL"))
                if (L(r[0]) is long rb) realBite.Add(rb);
            var empties = new Dictionary<long, int>();
            foreach (var r in Q(c, "SELECT cast_id, count(*) FROM cast_events WHERE event_type = 'empty_bite' AND cast_id IS NOT NULL GROUP BY cast_id"))
                if (L(r[0]) is long cid && L(r[1]) is long n) empties[cid] = (int)n;

            var events = new Dictionary<long, (DateTime? firstBite, DateTime? last, List<(DateTime t, double v)> chance)>();
            var biteMeta = new Dictionary<long, (DateTime t, uint fish, int? weight, int? windex)>();   // MASTER-01
            foreach (var r in Q(c, "SELECT cast_id, event_type, timestamp, COALESCE(chance, CASE WHEN event_type = 'wait' THEN raw_text END), fish_lang_id, weight, windex FROM cast_events WHERE cast_id IS NOT NULL"))
            {
                if (L(r[0]) is not long cid || Ts(r[2]) is not DateTime t) continue;
                if (!events.TryGetValue(cid, out var e)) e = (null, null, new());
                string et = r[1] as string ?? "";
                if (et == "bite" && L(r[4]) is long bf && bf > 0 && (!biteMeta.TryGetValue(cid, out var bm) || t < bm.t))
                    biteMeta[cid] = (t, (uint)bf, (int?)L(r[5]), (int?)L(r[6]));
                if ((et == "bite" || et == "hook" || et == "empty_bite") && (e.firstBite == null || t < e.firstBite)) e.firstBite = t;
                if (e.last == null || t > e.last) e.last = t;
                if (et == "wait" && (D(r[3]) ?? (r[3] is string rs ? CatchDbWriter.ParseRawChance(rs) : null)) is double ch) e.chance.Add((t, ch));   // FORMULA-06: до B0 число было только в тексте
                events[cid] = e;
            }

            // MASTER-01: снасть и прикорм заброса
            var tackle = new Dictionary<long, object?[]>();
            foreach (var r in Q(c, "SELECT cast_id, status, line_name, leader_name, hook_name, feeder_name, leader_base_par, groundbait_item_id, gb_osnova, gb_aroma, gb_addextend, gb_addbaits, hook_base_par, line_base_par, feeder_base_par, gb_klevprop FROM cast_tackle"))
                if (L(r[0]) is long cid) tackle[cid] = r;
            var gbOnCast = new Dictionary<long, long>();
            foreach (var r in Q(c, "SELECT cast_id, groundbait_item_id FROM groundbait_actions WHERE cast_id IS NOT NULL AND groundbait_item_id IS NOT NULL"))
                if (L(r[0]) is long cid && L(r[1]) is long gi) gbOnCast[cid] = gi;
            string ItemName(long id) { try { return baitName != null ? (baitName(id) ?? "") : ""; } catch { return ""; } }

            var weather = Q(c, "SELECT timestamp, tempr, cloudly, rain FROM weather ORDER BY timestamp")
                .Select(r => (t: Ts(r[0]), temp: D(r[1]), cloud: D(r[2]), rain: D(r[3]))).Where(w => w.t.HasValue).ToList();
            var ground = Q(c, "SELECT timestamp, game_x, game_z FROM groundbait_actions WHERE coord_system = 'game_world_xz'")
                .Select(r => (t: Ts(r[0]), x: D(r[1]), z: D(r[2]))).Where(g => g.t.HasValue && g.x.HasValue && g.z.HasValue).ToList();
            // PRERELEASE-01: погода и прикорм по времени — двоичный поиск вместо перебора на каждый заброс (то же условие)
            weather = weather.OrderBy(w0 => w0.t!.Value).ToList();
            ground = ground.OrderBy(g0 => g0.t!.Value).ToList();
            var catchLocs = Q(c, "SELECT session_id, timestamp, location_id FROM catches WHERE location_id IS NOT NULL")
                .Select(r => (s: r[0] as string ?? "", t: Ts(r[1]), loc: L(r[2]))).Where(x => x.t.HasValue && x.loc.HasValue).ToList();
            // FIX-03: server_checks.location_id is the SPAWN POINT id (c2s 1120 locid, e.g. 21/22/24/25 on waterbody 2), not the
            // waterbody id. Observed pairs catches.spawn_locid → catches.location_id give the mapping.
            var spawnPairs = Q(c, "SELECT spawn_locid, location_id, count(*) FROM catches WHERE spawn_locid IS NOT NULL AND location_id IS NOT NULL GROUP BY 1, 2")
                .Select(r => (sp: L(r[0]), loc: L(r[1]))).Where(x => x.sp.HasValue && x.loc.HasValue).ToList();
            var spawnToWater = SpawnMap(spawnPairs.Select(x => (x.sp!.Value, x.loc!.Value)));

            foreach (var r in Q(c, "SELECT id, timestamp, session_id, rod_index, cast_x, cast_z, bait_tid, bait_name, outcome FROM casts ORDER BY id"))
            {
                if (L(r[0]) is not long id || Ts(r[1]) is not DateTime ts) continue;
                var at = new Attempt
                {
                    CastId = id, Ts = ts, Session = r[2] as string ?? "", Rod = (int)(L(r[3]) ?? 0), X = D(r[4]), Z = D(r[5]),
                    BaitTid = L(r[6]), Bait = r[7] as string ?? "", Outcome = r[8] as string ?? ""
                };
                if (at.Bait.Length == 0 && at.BaitTid is long bt && baitName != null) { try { at.Bait = baitName(bt) ?? ""; } catch { } }
                // FORMULA-05: caught — поклёвка; missed — поклёвка, только если было событие bite/hook (иначе это лишь пустая поклёвка → нет);
                // unknown/pending с событием bite/hook — поклёвка была (исход вываживания не важен); без события — исход неизвестен (не no_bite)
                at.Bite = at.Outcome switch
                {
                    "caught" => true,
                    "missed" => realBite.Contains(id),
                    "no_bite" => false,
                    _ => realBite.Contains(id) ? true : null
                };
                if (catchByCast.TryGetValue(id, out var cr))
                {
                    if (L(cr[1]) is long f && f > 0) { at.FishId = (uint)f; at.Fish = a.FishName((uint)f); }
                    at.Weight = (int?)L(cr[2]); at.Windex = (int?)L(cr[3]);
                    // FIX-03: the waterbody is NOT taken from the cast's own catch row any more. Only caught casts have one, so
                    // «водоём» became a proxy of the outcome (leakage) and used a different id space than uncaught casts.
                }
                if (biteMeta.TryGetValue(id, out var bmeta)) { at.BiteFish = bmeta.fish; at.BiteWeight = bmeta.weight; at.BiteWindex = bmeta.windex; }
                if (tackle.TryGetValue(id, out var tk))
                {
                    at.TackleStatus = tk[1] as string ?? "";
                    if (at.TackleStatus == "complete")
                    {
                        at.Line = tk[2] as string ?? ""; at.Leader = tk[3] as string ?? ""; at.Hook = tk[4] as string ?? ""; at.Feeder = tk[5] as string ?? "";
                        // base_par (по коду игры): поводок/леска — разрыв кг / толщина мм / длина м; крючок — острота / размер / самоподсечка %
                        if ((tk[6] as string)?.Split('/') is { Length: > 0 } lp && double.TryParse(lp[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double kg) && kg > 0 && kg < 500)
                        {
                            at.LeaderKg = kg;
                            if (at.Leader.Length > 0 && !at.Leader.Contains("кг")) at.Leader += " " + kg.ToString("0.#", CultureInfo.InvariantCulture) + " кг";
                        }
                        if (at.Hook.Length > 0 && (tk[12] as string)?.Split('/') is { Length: > 1 } hp && int.TryParse(hp[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hs) && !at.Hook.Contains("№"))
                            at.Hook += " " + GameKnowledge.HookNo(hs);
                        if (at.Line.Length > 0 && (tk[13] as string)?.Split('/') is { Length: > 0 } np && double.TryParse(np[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lkg) && lkg > 0 && lkg < 500)
                            at.Line += " " + lkg.ToString("0.#", CultureInfo.InvariantCulture) + " кг";
                    }
                    at.GroundbaitItem = L(tk[7]);
                    foreach (var part in new[] { tk[8], tk[9], tk[10], tk[11] })
                        foreach (var piece in (part as string ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                            if (long.TryParse(piece.Split('/')[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long iid) && iid > 0 && !at.RecipeItems.Contains(iid)) at.RecipeItems.Add(iid);
                    // UI-02-4-FIX (правка A): целевая рыба прикорма (только kind 2) из gb_klevprop.
                    at.GroundbaitTargets = GroundbaitCalc.ParseKind2(tk[15] as string).Select(kv => (Fish: kv.Key, Strength: kv.Value)).ToList();
                }
                if (at.GroundbaitItem == null && gbOnCast.TryGetValue(id, out var gbi)) at.GroundbaitItem = gbi;
                if (at.RecipeItems.Count > 0)
                    at.Recipe = string.Join(" + ", at.RecipeItems.Select(i => ItemName(i) is { Length: > 0 } nm ? nm : "предмет " + i));
                else if (at.GroundbaitItem is long g0 && g0 > 0)
                    at.Recipe = ItemName(g0) is { Length: > 0 } gn ? gn : "прикорм " + g0;
                checks.TryGetValue(id, out var ck);
                if (ck.locs != null && ck.locs.Count > 0)
                {
                    long spawn = ck.locs.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
                    if (WaterFromSpawn(spawn, spawnToWater) is uint wb) { at.Location = wb; at.LocationByRule = !spawnToWater.ContainsKey(spawn); }
                }
                // AUDIT-02: прежний запасной путь «водоём ближайшего улова ±3 ч» убран — улов есть в основном у пойманных
                // забросов, то есть признак зависел от исхода (DeepSeek, FIX-04). Теперь — ниже, по соседним ЗАБРОСАМ.
                if (ck.depth != null && ck.depth.Count > 0) at.BaitDepthCm = ck.depth.Average();
                if (ticks.TryGetValue(id, out var tki))
                {
                    at.Ticks = tki.N; at.FirstTick = tki.First; at.LastTick = tki.Last; at.RodCode = tki.RodCode; at.RodUid = tki.RodUid;
                    if (tki.Xs.Count > 0) { at.BaitX = Median(tki.Xs); at.BaitZ = Median(tki.Zs); }
                    else if (tki.AllX.Count > 0) { at.BaitX = Median(tki.AllX); at.BaitZ = Median(tki.AllZ); }
                    if (tki.Bottom.Count > 0) at.BottomDepthM = Median(tki.Bottom) / 100.0;
                    if (tki.Above.Count > 0) at.AboveBottomM = Math.Max(0, Median(tki.Above) / 100.0);
                    var cells = tki.Cells.Count > 0 ? tki.Cells : tki.AllCells;
                    if (cells.Count > 0) at.Cell5 = cells.OrderByDescending(kv => kv.Value).First().Key;
                }
                if (empties.TryGetValue(id, out int emp)) at.EmptyBites = emp;
                if (tackle.TryGetValue(id, out var tk2) && (tk2[12] as string)?.Split('/') is { Length: > 1 } hb)
                {
                    if (int.TryParse(hb[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sh)) at.HookSharp = sh;
                    if (int.TryParse(hb[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sz)) at.HookSize = sz;
                }
                if (at.HookSize is int hsz && GameKnowledge.HookRange(hsz) is { } hr) { at.HookMinKg = hr.min; at.HookMaxKg = hr.max; }
                if (tackle.TryGetValue(id, out var tk3) && (tk3[1] as string) == "complete")
                {
                    double? P(object? o, int i) => (o as string)?.Split('/') is { } pp && pp.Length > i && double.TryParse(pp[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
                    bool hasFeeder = (tk3[5] as string ?? "").Length > 0;
                    at.FeederTimeMin = hasFeeder ? P(tk3[14], 1) : 0; at.FeederCamo = hasFeeder ? P(tk3[14], 2) : 0;
                    if ((tk3[3] as string ?? "").Length > 0) { at.LeaderMm = P(tk3[6], 1); at.LeaderLen = P(tk3[6], 2); }
                }
                events.TryGetValue(id, out var ev);
                // «chance» — только из ответов «wait» (до поклёвки), чтобы признак не содержал исход
                var before = ev.chance?.Where(x => ev.firstBite == null || x.t < ev.firstBite).Select(x => x.v).ToList();
                if (before != null && before.Count > 0) at.MeanChance = before.Average();
                var ord = ev.chance?.OrderBy(x => x.t).ToList();
                if (ord != null)
                {
                    at.ChanceTicks = ord.Count;
                    var early = ord.Skip(1).Take(3).Where(x => ev.firstBite == null || x.t < ev.firstBite).Select(x => x.v).ToList();
                    if (early.Count > 0) at.EarlyChance = early.Average();
                }
                DateTime? end = ev.firstBite ?? Max(ev.last, ck.last);
                if (end.HasValue && end.Value > ts) at.ExposureMin = (end.Value - ts).TotalMinutes;
                int wi = TimeSearch.UpperBound(weather, x => x.t!.Value, ts) - 1;
                var w = wi >= 0 && (ts - weather[wi].t!.Value).TotalMinutes <= 30 ? weather[wi] : default;
                int g0i = TimeSearch.LowerBound(ground, x => x.t!.Value, ts.AddMinutes(-Math.Max(60, s.GroundbaitWindowMin)));
                int g1i = TimeSearch.UpperBound(ground, x => x.t!.Value, ts.AddSeconds(5));
                var gwin = ground.GetRange(g0i, Math.Max(0, g1i - g0i));
                if (w.t.HasValue) { at.AirTemp = w.temp; at.Cloud = w.cloud; at.Rain = w.rain; }
                if (at.X.HasValue && at.Z.HasValue)
                    at.GroundbaitNear = gwin.Count(g => g.t <= ts.AddSeconds(5) && (ts - g.t!.Value).TotalMinutes <= s.GroundbaitWindowMin &&
                                                          Math.Sqrt(Sq(g.x!.Value - at.X.Value) + Sq(g.z!.Value - at.Z.Value)) <= s.GroundbaitRadiusM);
                if (at.PlaceX is double gx0 && at.PlaceZ is double gz0)
                {
                    // ZONES-02: по нашим данным улов/заброс ≈0,8 в ≤2,5 м от точки прикорма против 0,1–0,4 дальше (RESEARCH-01)
                    var recent = gwin.Where(g => g.t <= ts.AddSeconds(5) && (ts - g.t!.Value).TotalMinutes <= 60)
                                       .Select(g => (d: Math.Sqrt(Sq(g.x!.Value - gx0) + Sq(g.z!.Value - gz0)), age: (ts - g.t!.Value).TotalMinutes)).ToList();
                    at.GroundbaitDistM = recent.Count == 0 ? 30 : Math.Min(30, recent.Min(g => g.d));
                    var inSpot = recent.Where(g => g.d <= s.GroundbaitRadiusM).ToList();
                    at.GroundbaitAgeMin = inSpot.Count == 0 ? 60 : Math.Max(0, inSpot.Min(g => g.age));
                }
                var (gt, _) = GameClock.Estimate(ts, null);
                if (gt.Length >= 2 && int.TryParse(gt[..2], out int gh)) at.GameHour = gh % 24;
                a.Attempts.Add(at);
            }
            // AUDIT-02: заброс без точки спавна — водоём ближайшего по времени заброса той же сессии (±3 ч), у которого водоём
            // известен по точке спавна. Не зависит от исхода; помечается как «определён по соседним забросам» (PROBABLE).
            var known = a.Attempts.Where(x => x.Location.HasValue && !x.LocationInferred).ToList();
            foreach (var at in a.Attempts.Where(x => x.Location == null))
            {
                var near = known.Where(x => x.Session == at.Session && Math.Abs((x.Ts - at.Ts).TotalHours) <= 3).OrderBy(x => Math.Abs((x.Ts - at.Ts).TotalSeconds)).FirstOrDefault();
                if (near != null) { at.Location = near.Location; at.LocationInferred = true; at.LocationByRule = near.LocationByRule; continue; }
                // старые сессии без точек спавна: водоём сессии — только если ВСЕ уловы сессии в ±3 ч с одного водоёма
                // (одинаково для пойманных и непойманных забросов, поэтому исход не подсказывает водоём)
                var locs = catchLocs.Where(x => x.s == at.Session && Math.Abs((x.t!.Value - at.Ts).TotalHours) <= 3).Select(x => x.loc!.Value).Distinct().ToList();
                if (locs.Count == 1) { at.Location = (uint)locs[0]; at.LocationInferred = true; }
            }
            // COACH-01: глубина дна в точке наживки — после того, как водоём известен у всех забросов
            foreach (var at in a.Attempts)
                if (at.BottomDepthM == null && at.Location.HasValue && at.PlaceX is double px && at.PlaceZ is double pz)   // запасной путь — рельеф
                    try { at.BottomDepthM = Bathymetry.DepthAt(at.Location, px, pz); } catch { }
            try { a.Soak = SoakCurve.FromAttempts(a.Attempts, Q(c, "SELECT cast_id, timestamp, event_type FROM cast_events WHERE event_type IN ('bite','empty_bite') AND cast_id IS NOT NULL")
                                                 .Where(r => L(r[0]) != null && Ts(r[1]) != null).Select(r => (L(r[0])!.Value, Ts(r[1])!.Value, r[2] as string ?? "")),
                                                 Q(c, "SELECT cast_id, timestamp FROM server_checks WHERE cast_id IS NOT NULL").Where(r => L(r[0]) != null && Ts(r[1]) != null).Select(r => (L(r[0])!.Value, Ts(r[1])!.Value))); }
            catch { }
            // AUDIT-02: на каждом водоёме водится свой набор рыбы — учитываем, где вид подтверждён
            try
            {
                var game = global::FisherLogger.Habitat.GameLists();
                ReferenceProvider? refs = null; try { refs = new ReferenceProvider(); } catch { }
                a.Habitat = global::FisherLogger.Habitat.Build(a.OwnCatches.Select(x => (x.fish, x.loc)), l => game.TryGetValue(l, out var v) ? v : Enumerable.Empty<int>(), game.Keys, refs, a.FishName);
            }
            catch { }
            return a;
        }
        /// <summary>FIX-03: spawn point id → waterbody id from observed (spawn_locid, location_id) pairs; a spawn seen with
        /// two different waterbodies is dropped (ambiguous).</summary>
        public static Dictionary<long, uint> SpawnMap(IEnumerable<(long spawn, long water)> pairs)
        {
            var map = new Dictionary<long, uint>(); var bad = new HashSet<long>();
            foreach (var (sp, w) in pairs)
            {
                if (w <= 0 || w > uint.MaxValue || bad.Contains(sp)) continue;
                if (map.TryGetValue(sp, out var cur) && cur != (uint)w) { map.Remove(sp); bad.Add(sp); continue; }
                map[sp] = (uint)w;
            }
            return map;
        }

        /// <summary>
        /// FIX-03: waterbody of a spawn point id. 1) observed mapping; 2) otherwise the game's numbering spawn = waterbody×10 + k
        /// (k = 1…9; seen on 16 waterbodies in NEXT-MAP-ALL-WATERBODIES-01, e.g. 21→2, 63→6, 1011→101) — PROBABLE, used only
        /// when there is no observed pair; 3) otherwise null (never guessed further).
        /// </summary>
        public static uint? WaterFromSpawn(long spawn, IReadOnlyDictionary<long, uint> map)
        {
            if (map.TryGetValue(spawn, out var w)) return w;
            if (spawn >= 11 && spawn % 10 != 0) return (uint)(spawn / 10);
            return null;
        }

        private static DateTime? Max(DateTime? a, DateTime? b) => a == null ? b : b == null ? a : (a > b ? a : b);
        private sealed class TickInfo { public int N; public DateTime? First, Last; public List<double> Xs = new(), Zs = new(), AllX = new(), AllZ = new(); public Dictionary<string, int> Cells = new(), AllCells = new(); public List<double> Bottom = new(), Above = new(); public int? RodCode; public long? RodUid; }
        private static double Median(List<double> v) { var s = v.OrderBy(x => x).ToList(); int n = s.Count; return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2; }
        /// <summary>COACH-01: поклёвки по минуте в воде (по тактам сервера), для подсказки «перезабросьте».</summary>
        public SoakCurve? Soak { get; private set; }
        private static double Sq(double v) => v * v;

        // ------------------------------------------------------------------ сводка
        public AnalyticsOverview Overview(ModelRegistry? reg = null)
        {
            var lab = Attempts.Where(x => x.Bite.HasValue).ToList();
            var o = new AnalyticsOverview
            {
                Casts = Attempts.Count, Labeled = lab.Count, Bites = lab.Count(x => x.Bite == true), NoBites = lab.Count(x => x.Bite == false),
                UnknownOutcome = Attempts.Count - lab.Count, OwnCatches = OwnCatches.Count, CatchesLinkedToCasts = Attempts.Count(x => x.FishId.HasValue),
                PublicAnnouncements = Public.Count,
                Species = OwnCatches.Select(x => x.fish).Concat(Public.Select(p => p.fish)).Distinct().Count(),
                Waterbodies = OwnCatches.Where(x => x.loc.HasValue).Select(x => x.loc).Distinct().Count()
            };
            if (o.Labeled < S.MinLabeledForFormula) o.Missing.Add($"забросов с известным исходом: {o.Labeled} из {S.MinLabeledForFormula}");
            if (o.Bites < S.MinPositivesForFormula) o.Missing.Add($"поклёвок: {o.Bites} из {S.MinPositivesForFormula}");
            if (o.NoBites < S.MinNegativesForFormula) o.Missing.Add($"забросов без поклёвки: {o.NoBites} из {S.MinNegativesForFormula}");
            var act = reg?.ActiveModel;
            o.FormulaStatus = act != null
                ? $"формула v{act.Version} активна (лучше среднего на {act.Improvement:P1}, проверено на {act.N} забросах)"
                : o.Missing.Count > 0 ? "формулы нет — мало данных: " + string.Join("; ", o.Missing)
                : (reg?.LastStatus is { Length: > 0 } ls ? ls : "формулы нет — данных достаточно для попытки, ожидает пересчёта");
            return o;
        }

        // ------------------------------------------------------------------ советы по виду
        public static (double lo, double hi) Wilson(int k, int n, double z = 1.2816)
        {
            if (n <= 0) return (0, 1);
            double p = (double)k / n, z2 = z * z, den = 1 + z2 / n;
            double centre = (p + z2 / (2 * n)) / den, half = z * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n)) / den;
            return (Math.Max(0, centre - half), Math.Min(1, centre + half));
        }

        private EvidenceRow Row(string group, string item, int n, int k, string level, uint? loc = null)
        {
            var (lo, hi) = Wilson(k, n);
            bool adv = n >= S.MinAttemptsForAdvice && k >= S.MinCatchesForAdvice;
            string verdict = adv ? $"совет: {k} из {n} забросов ({lo:P0}–{hi:P0})"
                : "мало данных: " + string.Join(", ", new[]
                  {
                      n < S.MinAttemptsForAdvice ? $"забросов {n} из {S.MinAttemptsForAdvice}" : null,
                      k < S.MinCatchesForAdvice ? $"поимок {k} из {S.MinCatchesForAdvice}" : null
                  }.Where(x => x != null));
            return new EvidenceRow { Group = group, Item = item, Attempts = n, Successes = k, Rate = n > 0 ? (double)k / n : 0, Lo = lo, Hi = hi, Advice = adv, Verdict = verdict, Level = level, LocationId = loc };
        }

        public static string HourBucket(int h) => (h / 6) switch { 0 => "ночь 00–06", 1 => "утро 06–12", 2 => "день 12–18", _ => "вечер 18–24" };

        public string CellKey(double x, double z) => Math.Floor(x / S.ZoneCellMeters).ToString(CultureInfo.InvariantCulture) + ":" + Math.Floor(z / S.ZoneCellMeters).ToString(CultureInfo.InvariantCulture);
        public (double x, double z) CellCenter(string key)
        {
            var p = key.Split(':');
            return ((double.Parse(p[0], CultureInfo.InvariantCulture) + 0.5) * S.ZoneCellMeters, (double.Parse(p[1], CultureInfo.InvariantCulture) + 0.5) * S.ZoneCellMeters);
        }

        /// <summary>ADVISOR-UX-01: best own class (windex) of a species, -1 if unknown.</summary>
        public int MaxWindex(uint fishId) => OwnCatches.Where(c => c.fish == fishId && c.windex.HasValue).Select(c => c.windex!.Value).DefaultIfEmpty(-1).Max();
        /// <summary>ADVISOR-UX-01: рекорд по подтверждённым своим уловам (включая не связанные с забросом).</summary>
        public int MaxWeight(uint fishId) => OwnMaxWeight.TryGetValue(fishId, out var w) ? w : 0;
        public readonly Dictionary<uint, int> OwnMaxWeight = new();
        /// <summary>AUDIT-02: где вид водится (свои уловы + список игры + справочник). «Нет в списке» = не подтверждено.</summary>
        public HabitatIndex Habitat { get; set; } = new();

        public string CellLabel(string key)
        {
            var p = key.Split(':'); double cx = (double.Parse(p[0], CultureInfo.InvariantCulture) + 0.5) * S.ZoneCellMeters, cz = (double.Parse(p[1], CultureInfo.InvariantCulture) + 0.5) * S.ZoneCellMeters;
            return $"участок {S.ZoneCellMeters:0}×{S.ZoneCellMeters:0} м, центр X {cx:0}, Z {cz:0}";
        }

        /// <summary>ZONES-02: подпись места-круга для советника.</summary>
        public string PlaceLabel(double cx, double cz) => $"место радиусом {S.PlaceRadiusM:0.#} м, центр X {cx:0}, Z {cz:0}";

        public List<PresenceRow> Presence(uint? onlyLocation = null) => Presence(onlyLocation, null);

        /// <summary>PRERELEASE-01: уловы сгруппированы по (водоём, вид) один раз — прежде на каждую пару перебирались все свои
        /// уловы и все объявления. onlyFish — только для одного вида (советник по виду).</summary>
        public List<PresenceRow> Presence(uint? onlyLocation, uint? onlyFish)
        {
            var ownBy = OwnCatches.Where(x => !onlyFish.HasValue || x.fish == onlyFish).ToLookup(x => (x.loc, x.fish));
            var pubBy = Public.Where(p => !onlyFish.HasValue || p.fish == onlyFish).GroupBy(p => (p.loc, p.fish)).ToDictionary(g => g.Key, g => g.Count());
            var keys = OwnCatches.Select(x => (x.loc, x.fish)).Concat(Public.Select(p => (p.loc, p.fish))).Distinct()
                .Where(k => (!onlyLocation.HasValue || k.loc == onlyLocation) && (!onlyFish.HasValue || k.fish == onlyFish));
            var res = new List<PresenceRow>();
            foreach (var (loc, fish) in keys)
            {
                var own = ownBy[(loc, fish)].ToList();
                res.Add(new PresenceRow
                {
                    LocationId = loc, Waterbody = LocName(loc), FishId = fish, Fish = FishName(fish), OwnCatches = own.Count,
                    PublicSeen = pubBy.TryGetValue((loc, fish), out int pn) ? pn : 0,
                    MaxWindex = own.Where(x => x.windex.HasValue).Select(x => x.windex!.Value).DefaultIfEmpty(-1).Max(),
                    Zones = RadiusClustering.Group(own.Where(x => x.x.HasValue && x.z.HasValue).Select(x => (x.x!.Value, x.z!.Value)).ToList(), S.PlaceRadiusM).Count
                });
            }
            return res.OrderByDescending(r => r.OwnCatches).ThenByDescending(r => r.PublicSeen).ThenBy(r => r.Fish).ToList();
        }

        public List<(uint id, string name, int own, int pub)> KnownSpecies()
        {
            // PRERELEASE-01: счёт по виду одним проходом
            var own = OwnCatches.GroupBy(x => x.fish).ToDictionary(g => g.Key, g => g.Count());
            var pub = Public.GroupBy(p => p.fish).ToDictionary(g => g.Key, g => g.Count());
            return OwnCatches.Select(x => x.fish).Concat(Public.Select(p => p.fish)).Distinct()
                .Select(f => (f, FishName(f), own.TryGetValue(f, out int o) ? o : 0, pub.TryGetValue(f, out int q) ? q : 0))
                .OrderByDescending(t => t.Item3).ThenByDescending(t => t.Item4).ThenBy(t => t.Item2).ToList();
        }

        /// <summary>AUDIT-02: надёжность водоёма группы забросов: CONFIRMED только если у всех водоём по наблюдённой паре спавн→водоём.</summary>
        private static string LocLevel(IEnumerable<Attempt> g)
        {
            var l = g.ToList();
            if (l.Any(a => a.LocationInferred)) return "PROBABLE (водоём части забросов определён по соседним забросам)";
            if (l.Any(a => a.LocationByRule)) return "PROBABLE (водоём части забросов — по правилу номера точки спавна)";
            return "CONFIRMED";
        }

        public SpeciesAdvice Advise(uint fishId)
        {
            var adv = new SpeciesAdvice { FishId = fishId, Fish = FishName(fishId), OwnCatches = OwnCatches.Count(x => x.fish == fishId), PublicSeen = Public.Count(p => p.fish == fishId) };
            adv.Presence = Presence(null, fishId);
            var all = Attempts.Where(x => x.Bite.HasValue).ToList();
            bool Hit(Attempt a) => a.FishId == fishId;
            // AUDIT-02: этот вид ловится только на своих водоёмах. Забросы туда, где он не водится, не должны «разбавлять»
            // долю поимок по наживке и времени. Если о виде нет сведений ни в одном источнике — учитываются все забросы.
            bool known = Habitat.Known(fishId);
            var lab = known ? all.Where(a => Habitat.Lives(fishId, a.Location) || Hit(a)).ToList() : all;
            adv.HabitatLocations = known ? Habitat.LocationsOf(fishId).OrderBy(x => x).ToList() : new List<uint>();

            foreach (var g in lab.Where(a => a.Location.HasValue).GroupBy(a => a.Location))
            {
                // водоём — только если вид там подтверждён (свои уловы / игра / справочник); объявления других места не доказывают
                if (!(g.Any(Hit) || Habitat.Lives(fishId, g.Key))) continue;
                string src = global::FisherLogger.HabitatIndex.SourceText(Habitat.Sources(fishId, g.Key!.Value));
                var r = Row("Водоём", LocName(g.Key), g.Count(), g.Count(Hit), LocLevel(g) +
                            (src.Length > 0 ? "; вид здесь водится: " + src : ""), g.Key);
                r.Name = LocName(g.Key); adv.Rows.Add(r);
            }

            // ZONES-02: «Участок» — не клетка сетки, а место-круг радиусом PlaceRadiusM (как пятно прикорма игры) вокруг точки наживки
            foreach (var lg in lab.Where(a => a.Location.HasValue && a.PlaceX.HasValue && a.PlaceZ.HasValue).GroupBy(a => a.Location))
            {
                var la = lg.OrderBy(a => a.CastId).ToList();
                foreach (var ix in RadiusClustering.Group(la.Select(a => (a.PlaceX!.Value, a.PlaceZ!.Value)).ToList(), S.PlaceRadiusM))
                {
                    var g = ix.Select(i => la[i]).ToList();
                    if (!g.Any(Hit)) continue;
                    double cx = g.Average(a => a.PlaceX!.Value), cz = g.Average(a => a.PlaceZ!.Value);
                    var r = Row("Участок", LocName(lg.Key) + ": " + PlaceLabel(cx, cz), g.Count, g.Count(Hit), LocLevel(g) + " (координаты — где лежала наживка, иначе точка заброса)", lg.Key);
                    r.Name = LocName(lg.Key); r.X = cx; r.Z = cz; adv.Rows.Add(r);
                }
            }

            // LEARN-01: наживка и время — сначала по каждому водоёму (у каждого своя рыба); строка «на всех водоёмах» — только если
            // на ≥2 водоёмах (≥8 забросов на каждом) этот вариант лучше средней доли там и ни на одном не хуже
            var baseW = lab.Where(a => a.Location.HasValue).GroupBy(a => a.Location!.Value).ToDictionary(w => w.Key, w => w.Count(Hit) / (double)w.Count());
            (int agree, int dis) Agree(IEnumerable<Attempt> g)
            {
                int ag = 0, di = 0;
                foreach (var w in g.Where(a => a.Location.HasValue).GroupBy(a => a.Location!.Value).Where(w => w.Count() >= 8))
                {
                    double rate = w.Count(Hit) / (double)w.Count(), b0 = baseW.TryGetValue(w.Key, out var bv) ? bv : 0;
                    if (rate > b0) ag++; else if (rate < b0) di++;
                }
                return (ag, di);
            }
            foreach (var g in lab.Where(a => a.BaitTid.HasValue && a.Location.HasValue).GroupBy(a => (a.BaitTid, a.Location)))
                if (g.Any(Hit))
                {
                    string bn = g.Select(a => a.Bait).FirstOrDefault(b => b.Length > 0) ?? ("id " + g.Key.BaitTid);
                    var r = Row("Наживка", bn + " — " + LocName(g.Key.Location), g.Count(), g.Count(Hit), "CONFIRMED", g.Key.Location);
                    r.Name = bn; r.BaitTid = g.Key.BaitTid; adv.Rows.Add(r);
                }
            foreach (var g in lab.Where(a => a.BaitTid.HasValue).GroupBy(a => a.BaitTid))
                if (g.Any(Hit) && Agree(g) is var (ag, di) && ag >= 2 && di == 0)
                {
                    string bn = g.Select(a => a.Bait).FirstOrDefault(b => b.Length > 0) ?? ("id " + g.Key);
                    var r = Row("Наживка", bn + $" (подтверждено на {ag} водоёмах)", g.Count(), g.Count(Hit), "CONFIRMED");
                    r.Name = bn; r.BaitTid = g.Key; adv.Rows.Add(r);
                }

            foreach (var g in lab.Where(a => a.GameHour.HasValue && a.Location.HasValue).GroupBy(a => (h: HourBucket(a.GameHour!.Value), a.Location)))
                if (g.Any(Hit))
                {
                    var r = Row("Игровое время", g.Key.h + " — " + LocName(g.Key.Location), g.Count(), g.Count(Hit), "PROBABLE (игровое время = МСК × 12)", g.Key.Location);
                    r.Name = g.Key.h; adv.Rows.Add(r);
                }
            foreach (var g in lab.Where(a => a.GameHour.HasValue).GroupBy(a => HourBucket(a.GameHour!.Value)))
                if (g.Any(Hit) && Agree(g) is var (ag2, di2) && ag2 >= 2 && di2 == 0)
                {
                    var r = Row("Игровое время", g.Key + $" (подтверждено на {ag2} водоёмах)", g.Count(), g.Count(Hit), "PROBABLE (игровое время = МСК × 12)");
                    r.Name = g.Key; adv.Rows.Add(r);
                }

            adv.Rows = adv.Rows.OrderByDescending(r => r.Advice).ThenByDescending(r => r.Lo).ThenByDescending(r => r.Successes).ToList();
            adv.HasAdvice = adv.Rows.Any(r => r.Advice);
            int linked = lab.Count(Hit);
            if (known && lab.Count < all.Count)
                adv.Notes.Add($"Учтены только забросы на водоёмах, где этот вид водится ({lab.Count} из {all.Count}): " +
                              string.Join(", ", adv.HabitatLocations.Select(l => LocName(l))) + ". Забросы на других водоёмах не считаются неудачей для этого вида (список составлен по вашим уловам, файлам игры и справочнику и может быть неполным).");
            else if (!known)
                adv.Notes.Add("Где водится этот вид, пока неизвестно (нет своих уловов и нет в списках игры/справочнике) — учтены все забросы.");
            if (!adv.HasAdvice)
                adv.Notes.Add($"Совета нет: недостаточно данных. Нужны группы минимум из {S.MinAttemptsForAdvice} забросов с известным исходом и {S.MinCatchesForAdvice} поимками этого вида.");
            if (adv.OwnCatches > linked)
                adv.Notes.Add($"{adv.OwnCatches - linked} из {adv.OwnCatches} ваших поимок не связаны с забросом (старые записи или исход неизвестен) — они учтены только как факт «вид здесь ловится».");
            if (adv.PublicSeen > 0)
                adv.Notes.Add($"Объявления других игроков ({adv.PublicSeen}) показывают только, где вид ловят другие; это не вероятность клёва.");
            if (adv.Presence.Count == 0)
                adv.Notes.Add("Этот вид ещё ни разу не встречался в ваших данных.");
            return adv;
        }

        // ------------------------------------------------------------------ формула клёва
        // Только признаки, известные ДО исхода заброса. «Время ожидания» (ExposureMin) сюда не входит:
        // оно заканчивается поклёвкой и поэтому зависит от исхода (утечка ответа; замечание ревью DeepSeek).
        private static readonly (string name, Func<Attempt, double?> get)[] Numeric =
        {
            ("глубина наживки, м", a => a.BaitDepthCm / 100.0),
            ("прикорм рядом (вносов)", a => a.GroundbaitNear),
            // COACH-01: глубина дна в точке наживки (рельеф игры). Входит, только если есть у ≥80 % забросов (ChooseFeatures).
            ("глубина дна, м", a => a.BottomDepthM),
            ("наживка над дном, м", a => a.AboveBottomM),
            // ZONES-02 / RESEARCH-01: точка прикорма (пятно ~5 м), возраст прикорма, крючок по таблице игры, кормушка, поводок
            ("до точки прикорма, м", a => a.GroundbaitDistM),
            ("прикорм работает, мин", a => a.GroundbaitAgeMin),
            ("крючок: рыба от, кг (log10)", a => a.HookMinKg is double hk && hk > 0 ? Math.Log10(hk) : null),
            ("кормушка: время прикорма, мин", a => a.FeederTimeMin),
            ("кормушка: маскировка, %", a => a.FeederCamo),
            ("поводок: толщина, мм", a => a.LeaderMm),
            ("поводок: длина", a => a.LeaderLen),
            // AUDIT-02: «chance сервера» исключён из формулы — смысл поля не подтверждён, а окно усреднения зависит от исхода
            // (медиана проверок: 5 у пойманных, 10 у «без поклёвки»; DeepSeek).
            ("температура воздуха", a => a.AirTemp),
            ("облачность", a => a.Cloud),
            ("дождь", a => a.Rain),
            ("игровой час (sin)", a => a.GameHour.HasValue ? Math.Sin(2 * Math.PI * a.GameHour.Value / 24) : null),
            ("игровой час (cos)", a => a.GameHour.HasValue ? Math.Cos(2 * Math.PI * a.GameHour.Value / 24) : null),
        };

        public static double? FeatureRaw(Attempt a, string f)
        {
            foreach (var (name, get) in Numeric) if (name == f) return get(a);
            if (f.StartsWith("водоём=", StringComparison.Ordinal)) return a.Location.HasValue && "водоём=" + a.Location.Value == f ? 1 : 0;
            if (f.StartsWith("наживка=", StringComparison.Ordinal)) return a.BaitTid.HasValue && "наживка=" + a.BaitTid.Value == f ? 1 : 0;
            return null;
        }

        public static List<string> ChooseFeatures(List<Attempt> lab, List<string>? skipped = null)
        {
            var feats = new List<string>();
            foreach (var (name, get) in Numeric)
            {
                var vals = lab.Select(get).Where(v => v.HasValue).Select(v => v!.Value).ToList();
                double cov = lab.Count == 0 ? 0 : (double)vals.Count / lab.Count;
                if (cov < 0.8) { skipped?.Add($"{name}: есть только у {cov:P0} забросов"); continue; }
                double m = vals.Average(); if (Math.Sqrt(vals.Sum(v => (v - m) * (v - m)) / vals.Count) < 1e-9) { skipped?.Add(name + ": не меняется"); continue; }
                feats.Add(name);
            }
            foreach (var (prefix, key) in new (string, Func<Attempt, long?>)[] { ("водоём=", a => a.Location), ("наживка=", a => a.BaitTid) })
            {
                var levels = lab.Where(a => key(a).HasValue).GroupBy(a => key(a)!.Value).Where(g => g.Count() >= 5).Select(g => g.Key).OrderBy(v => v).ToList();
                if (levels.Count >= 2) feats.AddRange(levels.Select(l => prefix + l));
            }
            return feats;
        }

        private static double Sig(double v) => 1.0 / (1.0 + Math.Exp(-v));
        private static double LogLoss(double p, int y) { p = Math.Clamp(p, 1e-6, 1 - 1e-6); return -(y * Math.Log(p) + (1 - y) * Math.Log(1 - p)); }

        private static (double[] means, double[] stds) Scale(List<Attempt> rows, List<string> feats)
        {
            var means = new double[feats.Count]; var stds = new double[feats.Count];
            for (int j = 0; j < feats.Count; j++)
            {
                var v = rows.Select(a => FeatureRaw(a, feats[j])).Where(x => x.HasValue).Select(x => x!.Value).ToList();
                double m = v.Count > 0 ? v.Average() : 0; means[j] = m;
                double sd = v.Count > 0 ? Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / v.Count) : 1; stds[j] = sd < 1e-9 ? 1 : sd;
            }
            return (means, stds);
        }

        private static double[] Vec(Attempt a, List<string> feats, double[] means, double[] stds)
        {
            var x = new double[feats.Count];
            for (int j = 0; j < feats.Count; j++) x[j] = ((FeatureRaw(a, feats[j]) ?? means[j]) - means[j]) / stds[j];
            return x;
        }

        private static (double[] w, double b) Train(List<double[]> X, List<int> y, double l2 = 0.02, int iters = 1500, double lr = 0.3)
        {
            int n = X.Count, d = n > 0 ? X[0].Length : 0; var w = new double[d];
            double rate = Math.Clamp(y.Average(), 1e-3, 1 - 1e-3), b = Math.Log(rate / (1 - rate));
            var g = new double[d];
            for (int it = 0; it < iters; it++)
            {
                Array.Clear(g); double gb = 0;
                for (int i = 0; i < n; i++)
                {
                    double z = b; var xi = X[i]; for (int j = 0; j < d; j++) z += w[j] * xi[j];
                    double e = Sig(z) - y[i]; gb += e; for (int j = 0; j < d; j++) g[j] += e * xi[j];
                }
                b -= lr * gb / n;
                for (int j = 0; j < d; j++) w[j] -= lr * (g[j] / n + l2 * w[j]);
            }
            return (w, b);
        }

        public double? Predict(FormulaModel m, Attempt a)
        {
            if (m.Features.Count == 0) return null;
            var x = Vec(a, m.Features, m.Means, m.Stds); double z = m.Bias;
            for (int j = 0; j < x.Length; j++) z += m.Weights[j] * x[j];
            return Sig(z);
        }

        /// <summary>Подобрать формулу на всех забросах с известным исходом (без сохранения).</summary>
        public FormulaModel Fit(int folds = 5)
        {
            var lab = Attempts.Where(x => x.Bite.HasValue).OrderBy(x => x.Ts).ThenBy(x => x.CastId).ToList();
            int pos = lab.Count(x => x.Bite == true), neg = lab.Count - pos;
            var m = new FormulaModel { CreatedAt = DateTime.Now, N = lab.Count, Pos = pos, Neg = neg, Folds = folds, BaseRate = lab.Count > 0 ? (double)pos / lab.Count : 0,
                                       TrainedUntil = lab.Count > 0 ? lab[^1].Ts : DateTime.MinValue };
            var missing = new List<string>();
            if (lab.Count < S.MinLabeledForFormula) missing.Add($"забросов с известным исходом {lab.Count} из {S.MinLabeledForFormula}");
            if (pos < S.MinPositivesForFormula) missing.Add($"поклёвок {pos} из {S.MinPositivesForFormula}");
            if (neg < S.MinNegativesForFormula) missing.Add($"забросов без поклёвки {neg} из {S.MinNegativesForFormula}");
            if (missing.Count > 0) { m.Status = "формулы нет — мало данных: " + string.Join("; ", missing); return m; }

            var skipped = new List<string>();
            m.Features = ChooseFeatures(lab, skipped);
            if (m.Features.Count == 0) { m.Status = "формулы нет — нет признаков с достаточным покрытием (" + string.Join("; ", skipped) + ")"; return m; }

            double ll = 0, llBase = 0, brier = 0; int better = 0, tested = 0, used = 0;
            // AUDIT-02: проверка «вперёд по времени»: учимся только на прошлом, проверяем на следующем отрезке.
            // Раньше обучение шло на всех остальных частях, включая будущие, — это завышало качество.
            for (int f = 1; f < folds; f++)
            {
                int lo = f * lab.Count / folds, hi = (f + 1) * lab.Count / folds;
                var train = lab.Take(lo).ToList(); var test = lab.Skip(lo).Take(hi - lo).ToList();
                if (test.Count == 0 || train.Count == 0) continue;
                var (mu, sd) = Scale(train, m.Features);
                var (w, b) = Train(train.Select(a => Vec(a, m.Features, mu, sd)).ToList(), train.Select(a => a.Bite == true ? 1 : 0).ToList());
                double rate = train.Average(a => a.Bite == true ? 1.0 : 0.0), fl = 0, fb = 0;
                foreach (var a in test)
                {
                    var x = Vec(a, m.Features, mu, sd); double z = b; for (int j = 0; j < x.Length; j++) z += w[j] * x[j];
                    double p = Sig(z); int y = a.Bite == true ? 1 : 0;
                    fl += LogLoss(p, y); fb += LogLoss(rate, y); brier += (p - y) * (p - y);
                }
                if (fl < fb) better++;
                ll += fl; llBase += fb; tested++; used += test.Count;
            }
            used = Math.Max(1, used);
            m.CvLogLoss = ll / used; m.BaselineLogLoss = llBase / used; m.CvBrier = brier / used; m.FoldsBetter = better;
            m.Improvement = m.BaselineLogLoss > 0 ? 1 - m.CvLogLoss / m.BaselineLogLoss : 0;
            int needFolds = (int)Math.Ceiling(0.8 * Math.Max(1, tested));
            folds = tested; m.Folds = tested;
            var (mean, std) = Scale(lab, m.Features);
            var (wf, bf) = Train(lab.Select(a => Vec(a, m.Features, mean, std)).ToList(), lab.Select(a => a.Bite == true ? 1 : 0).ToList());
            m.Means = mean; m.Stds = std; m.Weights = wf; m.Bias = bf;
            m.Accepted = m.Improvement >= S.MinImprovement && better >= needFolds;
            m.Status = m.Accepted
                ? $"формула найдена: на кросс-проверке лучше среднего на {m.Improvement:P1} ({better} из {folds} частей)"
                : $"формула не принята: улучшение {m.Improvement:P1} (нужно ≥ {S.MinImprovement:P0}), лучше среднего в {better} из {folds} частей (нужно ≥ {needFolds}) — связь не подтверждается";
            return m;
        }

        /// <summary>
        /// Шаг самообучения: проверка активной формулы на новых забросах, при необходимости — новый подбор.
        /// Возвращает true, если реестр изменился (его нужно сохранить).
        /// </summary>
        /// <summary>FIX-03: current feature construction (waterbody from spawn point for every cast, never from the catch row).</summary>
        public const int CurrentFeatureSchema = 2;
        public const string LeakDeactivationReason = "FIX-03: отключена — признак «водоём» был построен с утечкой исхода (у пойманных забросов брался из улова, у остальных — номер точки спавна); формула пересчитывается заново";

        public bool Learn(ModelRegistry reg, bool force = false)
        {
            bool changed = false;
            var lab = Attempts.Where(x => x.Bite.HasValue).ToList();
            // FIX-03: formulas fitted on the old (leaking) features are switched off once; history entries are kept.
            foreach (var h in reg.History.Where(h => h.Active && h.FeatureSchema < CurrentFeatureSchema))
            {
                h.Active = false; h.DeactivatedReason = LeakDeactivationReason; changed = true; force = true;
            }
            var act = reg.ActiveModel;
            if (act != null)
            {
                var fwd = lab.Where(a => a.Ts > act.TrainedUntil).ToList();
                if (fwd.Count != act.ForwardN)
                {
                    act.ForwardN = fwd.Count;
                    if (fwd.Count > 0)
                    {
                        act.ForwardLogLoss = fwd.Average(a => LogLoss(Predict(act, a) ?? act.BaseRate, a.Bite == true ? 1 : 0));
                        act.ForwardBaseline = fwd.Average(a => LogLoss(act.BaseRate, a.Bite == true ? 1 : 0));
                    }
                    changed = true;
                    if (fwd.Count >= S.ForwardTestMin && act.ForwardLogLoss > act.ForwardBaseline)
                    {
                        act.Active = false;
                        act.DeactivatedReason = $"на {fwd.Count} новых забросах формула хуже среднего ({act.ForwardLogLoss:0.000} > {act.ForwardBaseline:0.000}) — отключена";
                        reg.LastStatus = "формулы нет — " + act.DeactivatedReason;   // AUDIT-02: иначе в окне оставалось «формула найдена»
                        act = null;
                    }
                }
            }
            int lastN = reg.History.Count > 0 ? reg.History[^1].N : 0;
            bool gates = lab.Count >= S.MinLabeledForFormula && lab.Count(x => x.Bite == true) >= S.MinPositivesForFormula && lab.Count(x => x.Bite == false) >= S.MinNegativesForFormula;
            if (gates && (force || reg.History.Count == 0 || lab.Count - lastN >= S.RetrainEveryNewAttempts))
            {
                var cand = Fit();
                cand.FeatureSchema = CurrentFeatureSchema;
                cand.Version = reg.History.Count == 0 ? 1 : reg.History.Max(h => h.Version) + 1;
                var last = reg.History.Count > 0 ? reg.History[^1] : null;
                // AUDIT-02: повторный пересчёт на тех же данных не плодит одинаковые версии
                if (last != null && last.N == cand.N && last.FeatureSchema == cand.FeatureSchema && Math.Abs(last.Improvement - cand.Improvement) < 1e-9 &&
                    last.Accepted == cand.Accepted)
                {
                    if (reg.LastStatus != last.Status) { reg.LastStatus = last.Status; changed = true; }
                    reg.LastCheck = DateTime.Now;
                    return changed;
                }
                // AUDIT-02: новая версия заменяет активную, только если она лучше НЕЁ, а не только лучше среднего
                if (cand.Accepted && act != null && cand.Improvement <= act.Improvement)
                {
                    cand.Accepted = false;
                    cand.Status = $"формула не принята: {cand.Improvement:P1} лучше среднего, но не лучше активной v{act.Version} ({act.Improvement:P1})";
                }
                if (cand.Accepted)
                {
                    foreach (var h in reg.History.Where(h => h.Active)) { h.Active = false; h.DeactivatedReason = $"заменена версией v{cand.Version} (подобрана на {cand.N} забросах)"; }
                    cand.Active = true; cand.ActivatedAt = DateTime.Now;
                }
                reg.History.Add(cand);
                reg.LastStatus = cand.Status;
                changed = true;
            }
            else
            {
                string st = gates ? (act != null ? reg.LastStatus : "ожидает пересчёта") : Fit().Status;
                if (!gates && st != reg.LastStatus) { reg.LastStatus = st; changed = true; }
            }
            reg.LastCheck = DateTime.Now;
            return changed;
        }
    }
}
