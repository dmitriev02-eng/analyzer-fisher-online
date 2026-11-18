using System;
using System.Collections.Generic;

namespace FisherLogger
{
    /// <summary>
    /// Запись о поимке. Поля с маленькими буквами сохранены для обратной
    /// совместимости с D:\fishing_research\catch_data.json.
    /// Все новые поля nullable/со значениями по умолчанию — старые записи
    /// читаются без ошибок.
    /// </summary>
    public class CatchData
    {
        // --- существовавшие поля ---
        public string timestamp { get; set; } = "";
        public int weight { get; set; }
        public int exp { get; set; }
        public int? offset_32 { get; set; }
        public string status { get; set; } = "";
        public string fish_name { get; set; } = "";
        public string grade { get; set; } = "";
        public DateTime chatDeadline { get; set; }
        public bool autoDetected { get; set; }

        // --- служебные поля ---
        public string record_id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime detected_at { get; set; }
        public string source { get; set; } = "realtime";

        // --- расширенные данные PacketDeepParser ---
        public uint? fish_lang_id { get; set; }
        public uint? bait_id { get; set; }
        public string bait_name { get; set; } = "";
        public string bait_category { get; set; } = "";
        public float? depth_cm { get; set; }
        public float? wind_kmh { get; set; }
        public double? cast_x { get; set; }
        public double? cast_y { get; set; }
        public double? cast_z { get; set; }
        public float? air_temperature_c { get; set; }
        public float? wind_ms { get; set; }
        public float? humidity { get; set; }
        public int? quality { get; set; }
        public uint? location_id { get; set; }
    public uint? baseid { get; set; }
        public string experience_formula { get; set; } = "";

        // --- статические данные из fish_full_database/items_full_database ---
        public string fish_icon_path { get; set; } = "";
        public string bait_icon_path { get; set; } = "";
        public string location_name { get; set; } = "";
        public double? fish_min_weight { get; set; }
        public double? fish_max_weight { get; set; }
        public bool fish_is_mutant { get; set; }
        public bool fish_is_aggression { get; set; }

        // full | fish_lang_id | fish_lang_id+heuristic | network_heuristic | chat | manual | none
        public string autodetect_source { get; set; } = "";
        public uint? SummaryUid { get; set; }   // NEXT-112: CATCH_SUMMARY blob+4 (in-memory identity)
        public long? PlayerLogBlockId { get; set; }   // NEXT-112: Player.log block id (in-memory identity)

        // --- новые поля FULL_CATCH / Player.log ---
        public double? quality_float { get; set; }
        public uint? bait_uid { get; set; }
        public string range_values_json { get; set; } = "";
        public int? spawn_locid { get; set; }
        public int? location_base_id { get; set; }
        public bool notification_shown { get; set; }

        // NEXT-SESSION-53: пометка неопределённой связи между источниками (НЕ имя рыбы, НЕ ID).
        // Пустая строка = нет пометки. Показывается отдельно от вида/ID.
        public string notification_note { get; set; } = "";

        // --- снасти из ITEM_ACTION / Player.log ---
        public uint? rod_id { get; set; }
        public uint? reel_id { get; set; }
        public uint? line_id { get; set; }
        public uint? hook_id { get; set; }
        public uint? float_id { get; set; }
        public uint? feeder_id { get; set; }
        public string equipment_source { get; set; } = "";

        // NEXT-ANALYTICS-04: direct link to casts.id, set ONLY when the catch summary was associated with the
        // current cast of its own rod (DeepCatchSession.ResolveCastForSummary). null = association not proven.
        public long? cast_id { get; set; }

        // NEXT-ANALYTICS-09: raw size class (0-3), set ONLY from a unique own-catch cmd=6 announcement.
        // null = association not proven. Distinct from grade (weight-derived label).
        public int? windex { get; set; }

        // NEXT-DATA-01: coordinates equal a recorded cast (tolerance GameCoordinates.CastMatchTolerance); set when read
        // from the DB. null = not checked. Live catches are verified through cast_id instead.
        public bool? coord_verified { get; set; }
    }

    public class EquipmentState
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public uint? RodId { get; set; }
        public uint? ReelId { get; set; }
        public uint? LineId { get; set; }
        public uint? HookId { get; set; }
        public uint? FloatId { get; set; }
        public uint? FeederId { get; set; }
        public string Source { get; set; } = "ITEM_ACTION";
    }

    public class LocationChange
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public uint LocationId { get; set; }
        public string LocationName { get; set; } = "";
        public uint SpawnLocId { get; set; }
    }

    public class FeedAction
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public uint BaitUid { get; set; }
        public uint? BaitItemId { get; set; }
        public string BaitName { get; set; } = "";
        public bool HasBait => BaitUid > 0;
        // NEXT-DATA-01: c2s 192 decoded structurally (int64 rod uid, string 'feed', string value) — LOADING groundbait
        // into the rod (sets the rod's props/feed). null = frame did not decode with this layout.
        public long? RodUid { get; set; }
        public string? PropName { get; set; }
    }

    /// <summary>NEXT-DATA-01: c2s 1112 — APPLICATION (throwing in) of groundbait. Coordinates are game world X/Y/Z.</summary>
    public class GroundbaitAction
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public long GroundbaitUid { get; set; }
        public long FeederUid { get; set; }        // 0 = no feeder (thrown by hand)
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public string SourceKey { get; set; } = "";
    }

    /// <summary>NEXT-DATA-01: c2s 200 — bait reload: stack uid → bag/cell of the rod rod_uid.</summary>
    public class BaitReload
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public long StackUid { get; set; }
        public int Count { get; set; }
        public int Bag { get; set; }
        public int Cell { get; set; }
        public long RodUid { get; set; }
        public string SourceKey { get; set; } = "";
    }

    public class CraftAction
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public uint MainWpnUid { get; set; }
        public uint? MainWpnItemId { get; set; }
        public uint AromaWpnUid { get; set; }
        public uint? AromaWpnItemId { get; set; }
        public uint AddWpnUid { get; set; }
        public uint? AddWpnItemId { get; set; }
        public uint BaitUid { get; set; }
        public uint? BaitItemId { get; set; }
    }

    public class IdMapEntry
    {
        public int id { get; set; }
        public string fish_name { get; set; } = "";
        public int count { get; set; }
    }

    public class ChatCatch
    {
        public string fishName { get; set; } = "";
        public int weight { get; set; }
        public int exp { get; set; }
        public DateTime time { get; set; }
    }

    /// <summary>
    /// Поимка другого игрока, распознанная в локальном чате Player.log.
    /// Координаты — мои на момент события (из последнего CLIENT_STATE).
    /// </summary>
    public class OtherPlayerCatch
    {
        public long id { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string PlayerName { get; set; } = "";
        public string Clan { get; set; } = "";
        public string FishName { get; set; } = "";
        public int Weight { get; set; }
        public uint? MyLocationId { get; set; }
        public double? MyCastX { get; set; }
        public double? MyCastZ { get; set; }
        public double? DistanceMeters { get; set; }
    }

    public class OtherPlayerFishStat
    {
        public string FishName { get; set; } = "";
        public int OtherCount { get; set; }
        public double OtherAvgWeight { get; set; }
        public int MyCount { get; set; }
        public double MyAvgWeight { get; set; }

        public double AvgWeightDiff => OtherAvgWeight - MyAvgWeight;
    }

    public class OtherPlayerStat
    {
        public string PlayerName { get; set; } = "";
        public string Clan { get; set; } = "";
        public int Count { get; set; }
        public double AvgWeight { get; set; }
        public string LastCatch { get; set; } = "";
    }

    public class OtherPlayersSummary
    {
        public int TotalCatches { get; set; }
        public int UniquePlayers { get; set; }
        public int UniqueFish { get; set; }
        public List<OtherPlayerFishStat> FishStats { get; set; } = new();
        public List<OtherPlayerStat> PlayerStats { get; set; } = new();
    }

    public class BaitModifier
    {
        public int ItemId { get; set; }
        public string Name { get; set; } = "";
        public string KlevpropRaw { get; set; } = "";
        public List<ModifierEntry> Modifiers { get; set; } = new();
    }

    public class ModifierEntry
    {
        public int Kind { get; set; }
        public int FishId { get; set; }
        public string FishName { get; set; } = "";
        public double Modifier { get; set; }
        public string Meaning { get; set; } = "";
    }

    /// <summary>
    /// Правила определения весового грейда. Вынесены из Form1, чтобы
    /// импорт PCAP и реалтайм использовали одну и ту же шкалу.
    /// </summary>
    public sealed class WeatherRecord
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string? SessionId { get; set; }
    public uint? BaseId { get; set; }
    public uint? LocationId { get; set; }
    public float? Cloudly { get; set; }
    public float? Fog { get; set; }
    public float? Rain { get; set; }
    public float? Tempr { get; set; }
    public float? Grom { get; set; }
    public string UnitsStatus { get; set; } = "UNVERIFIED";
    public string? SourceKey { get; set; }
}

/// <summary>NEXT-GTWC-01: объявление об улове игрока на локации (Photon событие cmd 6 / cmd 5 клуб).</summary>
public sealed class PublicCatchRecord
{
    public DateTime ReceivedAt { get; set; }            // время ПК при приёме пакета
    public DateTime? ServerTime { get; set; }           // ReceivedAt + смещение часов сервера (cmd 255), если известно
    public string? GameTimeEst { get; set; }            // «HH:mm», оценка (GameClock), может отсутствовать
    public int? GameTimeUncMin { get; set; }
    public string? GameTimeModel { get; set; }
    public string? SessionId { get; set; }
    public uint? ReceiverLocationId { get; set; }        // водоём, где находился НАШ игрок при приёме (не место чужого улова)
    public int SourceCmd { get; set; }                   // 6 — локация, 5 — клуб
    public string Channel { get; set; } = "";            // fish / rcrd / clubfish / clubtrof (как в клиенте)
    public long PlayerId { get; set; }
    public string PlayerName { get; set; } = "";
    public int FishLangId { get; set; }
    public string FishName { get; set; } = "";
    public int Windex { get; set; }
    public int Weight { get; set; }
    public int PlayerLevel { get; set; }
    public int Title { get; set; }
    public long ClubId { get; set; }
    public string ClubTag { get; set; } = "";
    public string SourceKey { get; set; } = "";
}

/// <summary>NEXT-GTWC-01: публичное сообщение чата или действие игрока (Photon событие cmd 50). Личные не сохраняются.</summary>
public sealed class ChatMessageRecord
{
    public DateTime ReceivedAt { get; set; }
    public DateTime? ServerTime { get; set; }
    public string? GameTimeEst { get; set; }
    public int? GameTimeUncMin { get; set; }
    public string? GameTimeModel { get; set; }
    public string? SessionId { get; set; }
    public int RoomId { get; set; }
    public string Channel { get; set; } = "";
    public string MessageType { get; set; } = "text";    // text / action
    public string? ActionName { get; set; }
    public string? ActionValue { get; set; }
    public long FromId { get; set; }
    public string FromName { get; set; } = "";
    public string Text { get; set; } = "";
    public int? FromLevel { get; set; }
    public int? FromTitle { get; set; }
    public long? FromClubId { get; set; }
    public string? FromClubTag { get; set; }
    public string SourceKey { get; set; } = "";
}

public sealed class CastRecord
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string? SessionId { get; set; }
    public int RodIndex { get; set; }
    public double? CastX { get; set; }
    public double? CastY { get; set; }
    public double? CastZ { get; set; }
    public long? BaitUid { get; set; }
    public long? BaitTid { get; set; }
    public string? BaitName { get; set; }
    public string Outcome { get; set; } = "pending";
    public string? SourceKey { get; set; }
}

public sealed class CastEvent
{
    public long Id { get; set; }
    public long CastId { get; set; }
    public DateTime Timestamp { get; set; }
    // wait/bite/empty_bite/hook/catch/retrieve/cast_end_unresolved/log_drop_cour (NEXT-ANALYTICS-04: see CastLifecycleTracker)
    public string EventType { get; set; } = "";
    public bool IsClient { get; set; }
    public uint? FishLangId { get; set; }
    public uint? Weight { get; set; }
    public uint? Windex { get; set; }
    public float? Chance { get; set; }
    public string RawText { get; set; } = "";
    public string MatchStatus { get; set; } = "UNVERIFIED";
    public string? SourceKey { get; set; }
    public BiteMetadata? Bite { get; set; }   // NEXT-BITE-IMPLEMENT-01: only for event_type 'bite' from s2c 1120 flag=1
}

public sealed class TcpSegment
{
    public string ConnKey { get; set; } = "";
    public bool FromServer { get; set; }
    public uint Seq { get; set; }
    public uint Ack { get; set; }
    public byte Flags { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public DateTime Timestamp { get; set; }
    public bool IsSyn => (Flags & 0x02) != 0;
    public bool IsFin => (Flags & 0x01) != 0;
    public bool IsRst => (Flags & 0x04) != 0;
}

    /// <summary>NEXT-ANALYTICS-13: локальный player ID — Unknown / Confirmed / Ambiguous.
    /// Подтверждается двумя и более согласованными наблюдениями; конфликт → Ambiguous (ассоциация запрещена).
    /// NEXT-ANALYTICS-14: сбрасывается на каждое новое игровое соединение (OwnWindexCoordinator.OnConnectionEpoch).</summary>
    public sealed class LocalPlayerIdTracker
    {
        private readonly object _lock = new();
        private uint? _detected;
        private uint _candidate;
        private int _count;
        private bool _ambiguous;

        public uint? DetectedId { get { lock (_lock) return _detected; } }
        public bool IsAmbiguous { get { lock (_lock) return _ambiguous; } }

        /// <summary>Наблюдение кандидата ID из c2s. Возвращает true, если ID только что подтверждён.</summary>
        public bool Observe(uint pid)
        {
            if (pid == 0 || pid > 100_000_000) return false;
            lock (_lock)
            {
                if (_ambiguous) return false;
                if (_detected.HasValue)
                {
                    if (_detected.Value != pid) { _ambiguous = true; _detected = null; }
                    return false;
                }
                if (_count == 0) { _candidate = pid; _count = 1; return false; }
                if (_candidate == pid)
                {
                    _count++;
                    if (_count >= 2) { _detected = pid; return true; }
                    return false;
                }
                _ambiguous = true;
                return false;
            }
        }

        public void Reset()
        {
            lock (_lock) { _detected = null; _candidate = 0; _count = 0; _ambiguous = false; }
        }
    }

    /// <summary>
    /// NEXT-ANALYTICS-14: границы игрового TCP-соединения с сервером (порт 9205) по метаданным захвата.
    /// Номер соединения (epoch) растёт, когда начинается НОВОЕ соединение:
    ///  * другой локальный порт клиента, чем у текущего соединения (в захватах игра держит одно соединение за раз);
    ///  * SYN клиента (без ACK) на тот же порт после того, как по текущему соединению уже шли данные
    ///    (повтор SYN до данных — ретрансляция, не новое соединение).
    /// Обычные пакеты того же соединения номер не меняют. UDP номер не меняет.
    /// </summary>
    public sealed class GameConnectionTracker
    {
        private readonly object _lock = new();
        private long _epoch;
        private int _port = -1;
        private bool _dataSeen;

        public long Epoch { get { lock (_lock) return _epoch; } }

        /// <summary>Учесть TCP-пакет игрового соединения. Возвращает номер соединения, к которому относится пакет.</summary>
        public long Observe(int clientPort, bool clientSyn, bool hasPayload)
        {
            lock (_lock)
            {
                if (clientPort <= 0 || clientPort > 65535) return _epoch;
                if (_epoch == 0) { _epoch = 1; _port = clientPort; _dataSeen = hasPayload; return _epoch; }
                if (clientPort != _port || (clientSyn && _dataSeen))
                {
                    _epoch++; _port = clientPort; _dataSeen = false;
                }
                if (hasPayload) _dataSeen = true;
                return _epoch;
            }
        }

        public void Reset()
        {
            lock (_lock) { _epoch = 0; _port = -1; _dataSeen = false; }
        }
    }

    /// <summary>
    /// NEXT-ANALYTICS-14: связывает автоопределение локального player ID с привязкой windex к своим уловам.
    /// Сама привязка — только существующим методом CatchDbWriter.AttachOwnCatchWindex (точные правила: player_id, вид,
    /// вес, окно [улов, улов+30 с], уникальность 1:1; только NULL → значение; неоднозначность остаётся NULL).
    /// Вызывается: (1) в момент подтверждения ID — для уловов и объявлений, пришедших раньше подтверждения;
    /// (2) при новом объявлении с подтверждённым ID; (3) при новом улове. Неизвестный/неоднозначный ID → привязки нет.
    /// Новое игровое соединение (другой epoch) или новая сессия → детекция ID начинается заново.
    /// </summary>
    public sealed class OwnWindexCoordinator
    {
        private readonly LocalPlayerIdTracker _tracker = new();
        private readonly Func<long, int> _attach;
        private readonly Action<string>? _log;
        private readonly object _lock = new();
        private long _epoch;
        private int _attachCalls;

        public OwnWindexCoordinator(Func<long, int> attach, Action<string>? log = null)
        {
            _attach = attach ?? throw new ArgumentNullException(nameof(attach));
            _log = log;
        }

        public uint? LocalPlayerId => _tracker.DetectedId;
        public bool IsAmbiguous => _tracker.IsAmbiguous;
        public long ConnectionEpoch { get { lock (_lock) return _epoch; } }
        public int AttachCalls => System.Threading.Volatile.Read(ref _attachCalls);

        /// <summary>Номер игрового соединения текущего пакета (вызывается в порядке обработки пакетов).
        /// true — началось новое соединение и детекция ID сброшена.</summary>
        public bool OnConnectionEpoch(long epoch)
        {
            if (epoch <= 0) return false;
            bool reset = false;
            lock (_lock)
            {
                if (_epoch != epoch)
                {
                    reset = _epoch != 0;
                    _epoch = epoch;
                    if (reset) _tracker.Reset();
                }
            }
            if (reset) _log?.Invoke("[PLAYERID] новое игровое соединение — локальный ID определяется заново");
            return reset;
        }

        /// <summary>Сброс детекции (новый реплей / новая сессия приложения).</summary>
        public void Reset(string reason)
        {
            lock (_lock) { _epoch = 0; _tracker.Reset(); }
            _log?.Invoke("[PLAYERID] сброс определения ID: " + reason);
        }

        /// <summary>Наблюдение ID из c2s cmd 1006. При подтверждении — отложенная привязка уже сохранённых данных.</summary>
        public int ObserveLocalPlayerId(uint pid)
        {
            bool wasAmbiguous = _tracker.IsAmbiguous;
            if (_tracker.Observe(pid))
            {
                _log?.Invoke("[PLAYERID] локальный ID подтверждён: " + pid);
                return Attach(pid);
            }
            if (!wasAmbiguous && _tracker.IsAmbiguous)
                _log?.Invoke("[PLAYERID] конфликт ID в одном соединении — идентичность неоднозначна, windex не присваивается");
            return 0;
        }

        /// <summary>Объявление cmd 6/5 сохранено (новая строка).</summary>
        public int OnPublicCatchSaved(long playerId)
        {
            var id = _tracker.DetectedId;
            return id.HasValue && playerId == id.Value ? Attach(id.Value) : 0;
        }

        /// <summary>Свой улов сохранён.</summary>
        public int OnCatchSaved()
        {
            var id = _tracker.DetectedId;
            return id.HasValue ? Attach(id.Value) : 0;
        }

        private int Attach(long id)
        {
            System.Threading.Interlocked.Increment(ref _attachCalls);
            try { return _attach(id); }
            catch (Exception ex) { _log?.Invoke("[PLAYERID] привязка windex: " + ex.Message); return 0; }
        }
    }

public static class CatchRules
    {
        /// <summary>ADVISOR-02: видоспецифичная оценка по трофейным порогам справочника (large/unique).</summary>
        public static string DetermineGrade(int weight, uint? fishLangId = null, string? fishName = null)
        {
            var t = FishWeightsCatalog.Resolve(fishLangId, fishName);
            if (t != null)
            {
                if (weight >= t.Value.UniqueG) return "УНИКАЛЬНЫЙ";
                if (weight >= t.Value.LargeG) return "ТРОФЕЙНЫЙ";
                return "ОБЫЧНЫЙ";
            }
            return DetermineGrade(weight);
        }

        /// <summary>Старый фиксированный fallback (для видов без порога или без привязки вида).</summary>
        public static string DetermineGrade(int weight)
        {
            if (weight >= 10000) return "РЕЛИКТ";
            if (weight >= 5000) return "ЗОЛОТОЙ";
            if (weight >= 2000) return "ТРОФЕЙНЫЙ";
            if (weight >= 1000) return "КРУПНЫЙ";
            return "ОБЫЧНЫЙ";
        }

        // NEXT-OVERLAY-02: запись поимок не ограничена весом (минимум записи 0 г).
        // Отбрасываются только физически невозможные значения (0 г и меньше, больше 200 кг).
        // Фильтр по весу допускается только при ОТОБРАЖЕНИИ (оверлей), но не при записи.
        public const int MinRecordWeightG = 0;
        public const int MaxRecordWeightG = 200_000;

        public static bool IsRecordableWeight(long weight)
            => weight > 0 && weight >= MinRecordWeightG && weight <= MaxRecordWeightG;

        /// <summary>Допустимое опережение времени записи относительно часов ПК (с).</summary>
        public const double LiveClockSkewSeconds = 5;

        /// <summary>
        /// NEXT-OVERLAY-02: запись относится к окну «только что» (0 ≤ возраст &lt; window, с допуском на расхождение часов).
        /// Записи с датой в будущем (например, импорт PCAP с годом 3995) и без даты в окно не попадают.
        /// </summary>
        public static bool IsWithinLiveWindow(DateTime detectedAt, DateTime now, double windowSeconds)
        {
            if (detectedAt == default) return false;
            double age = (now - detectedAt).TotalSeconds;
            return age >= -LiveClockSkewSeconds && age < windowSeconds;
        }
    }

    /// <summary>
    /// NEXT-SESSION-51: чистая политика идентичности поимки.
    /// offset_32 — синтетическая склейка [len&0xFF][blob[0]], НЕ fish_lang_id.
    /// </summary>
    public static class CatchIdentityPolicy
    {
        /// <summary>Допуск веса для безопасной связи уведомлений (не слияние записей).</summary>
        public const int NotificationWeightToleranceG = 2;

        /// <summary>Окно времени для безопасной связи уведомлений (не слияние записей).</summary>
        public const int NotificationTimeWindowMs = 3000;

        /// <summary>
        /// NEXT-SESSION-53: текстовая пометка неопределённой связи источников.
        /// Показывается ОТДЕЛЬНО от вида/ID; НЕ означает «одна поимка».
        /// </summary>
        public const string UncertainAssociationLabel = "Возможно, та же поимка";

        /// <summary>
        /// Безопасная связь двух событий (вес+время) — ТОЛЬКО для пометки неопределённости,
        /// НЕ для подавления уведомлений и НЕ для слияния записей.
        /// </summary>
        public static bool HasSafeNotificationRelation(int weightA, DateTime timeA, int weightB, DateTime timeB)
        {
            if (Math.Abs(weightA - weightB) > NotificationWeightToleranceG) return false;
            return Math.Abs((timeB - timeA).TotalMilliseconds) <= NotificationTimeWindowMs;
        }

        /// <summary>
        /// NEXT-SESSION-53: пометка для UI. Возвращает текст пометки, если связь возможна,
        /// иначе пустую строку. Уведомление при этом НЕ подавляется — показываются оба.
        /// </summary>
        public static string UncertaintyNoteFor(int weightA, DateTime timeA, int weightB, DateTime timeB)
        {
            return HasSafeNotificationRelation(weightA, timeA, weightB, timeB)
                ? UncertainAssociationLabel
                : "";
        }

        /// <summary>fish_lang_id 110 — валидный канонический вид; offset_32=110 — нет.</summary>
        public static bool LooksLikeSyntheticOffset(int? offset32) => offset32.HasValue;

        /// <summary>
        /// Перевод manual→confirmed допустим только при известном виде И надёжной связи
        /// «обогащение ↔ эта запись». Иначе manual остаётся для ручного выбора.
        /// </summary>
        public static bool CanPromoteManualToConfirmed(string? fishName, uint? fishLangId, bool associationReliable)
        {
            if (!associationReliable) return false;
            bool nameKnown = !string.IsNullOrWhiteSpace(fishName) && fishName != "???" && fishName != "?";
            return nameKnown || fishLangId.HasValue;
        }

        /// <summary>Каноническая рыба известна (имя или fish_lang_id).</summary>
        public static bool HasVerifiedFishIdentity(string? fishName, uint? fishLangId)
        {
            bool nameKnown = !string.IsNullOrWhiteSpace(fishName) && fishName != "???" && fishName != "?";
            return nameKnown || fishLangId.HasValue;
        }
    }

    /// <summary>
    /// Человекочитаемые названия для UI. Неизвестный вид показывается БЕЗ подмены
    /// синтетического offset_32 каноническим fish ID.
    /// </summary>
    public static class CatchDisplay
    {
        public static string Fish(CatchData c)
        {
            string name = c.fish_name?.Trim() ?? "";
            if (name.StartsWith("Fish_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Item_", StringComparison.OrdinalIgnoreCase))
                name = "";

            if (name.Length > 0 && name != "???" && name != "?")
                return name;

            // NEXT-SESSION-51: offset_32 — синтетика, не «ID» рыбы.
            if (c.offset_32.HasValue)
                return "Неизвестно (синт. " + c.offset_32.Value + ")";

            // fish_lang_id — канонический namespace, помечаем явно.
            if (c.fish_lang_id.HasValue)
                return "Неизвестно (fishLang " + c.fish_lang_id.Value + ")";

            return "Неизвестно";
        }

        public static string Bait(CatchData c)
        {
            if (!string.IsNullOrEmpty(c.bait_name))
                return c.bait_name;

            if (c.bait_id.HasValue)
                return "Неизвестно (ID " + c.bait_id.Value + ")";

            if (c.status == "waiting_bait")
                return "...";

            return "";
        }
    }
}
