using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace FisherLogger
{
    /// <summary>
    /// Пассивный разбор протокола The Fisher Online (TCP 9205).
    /// Все смещения подтверждены на реальных дампах session_001/session_002
    /// (см. отчёт: D:\fishing_research\REPORT.md). Код только читает байты, ничего не отправляет.
    ///
    /// Адаптация для FisherLogger:
    ///  - ParseFrames потокобезопасен (может вызываться из потока захвата и из GUI);
    ///  - Reset() сбрасывает состояние между сессиями;
    ///  - диагностика отдаётся через событие Diagnostics, а не в Console.
    /// </summary>
    public sealed class PacketDeepParser
    {
        // ------------------------------------------------------------------
        // Константы формата
        // ------------------------------------------------------------------
        private static readonly byte[] FrameMagic = { 0xFB };  // NEXT-89 R1
        private const byte KindClientState = 0x02;
        private const byte KindServerCatch = 0x03;
        private const byte KindServerEvent = 0x04;

        // NEXT-OVERLAY-02: порог 50 г для записи убран — FULL_CATCH и CATCH_SUMMARY принимаются
        // при любом допустимом весе (CatchRules.IsRecordableWeight: 1 г … 200 кг); кадры по-прежнему
        // проверяются по длине, формуле опыта, blob и fish_lang_id.
        // Отключённая эвристика TryParseCatchSummary85 (NEXT-04B D) сохраняет прежний порог.
        private const int LegacySummary85MinWeight = 50;

        // Формула опыта: 4 float32 сразу после ASCII-строки, БЕЗ выравнивания.
        private const int FormulaOffset = 44;
        private const float ExpectedFloat03 = 0.3f;
        private const float DefaultWaterLevelCm = 500f;

        // ------------------------------------------------------------------
        // Базы (JSON из D:\fishing_research)
        // ------------------------------------------------------------------
        private readonly Dictionary<uint, BaitInfo> _baits = new();
        private readonly Dictionary<uint, BaitInfo> _items = new();
        private readonly Dictionary<uint, FishInfo> _fish = new();
        private readonly Dictionary<string, FishInfo> _fishByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, LocationInfo> _locations = new();
        private readonly Dictionary<uint, uint> _locationAliases = new();
        private readonly List<BaitModifier> _baitModifiers = new();
        private readonly Dictionary<uint, NetworkFishInfo> _networkFish = new();
        private readonly Dictionary<uint, List<uint>> _reverseNetwork = new();
        private readonly Dictionary<uint, uint> _uidToItemId = new();
        private long _heartbeatCount;
        private readonly HashSet<string> _processedFrames = new(StringComparer.Ordinal);
        private readonly string _dataDir;
        private int _fishRecordCount;
        private readonly object _sync = new();

        /// <summary>Диагностические сообщения (загрузка баз, предупреждения).</summary>
        public event Action<string>? Diagnostics;

        /// <summary>Инвентарь Kind=13: UID → item_id.</summary>
        public IReadOnlyDictionary<uint, uint> UidToItemId => _uidToItemId;
        public int InventoryCount => _uidToItemId.Count;

        public event Action<int>? InventoryUpdated;
        public event Action<FeedAction>? FeedActionDetected;
        public event Action<CraftAction>? CraftActionDetected;
    public event Action<DeepCatchData>? CastDetected;
    public event Action<GroundbaitAction>? GroundbaitApplied;   // NEXT-DATA-01: c2s 1112
    public event Action<BaitReload>? BaitReloaded;              // NEXT-DATA-01: c2s 200
    public event Action<DeepCatchData>? EmptyBiteDetected;
    /// <summary>
    /// LOGFIX-03 (A): s2c 1120 flag=1 (поклёвка) длиной ровно 209/218 байт разбирается как FULL_CATCH и раньше НЕ сохранялась как
    /// поклёвка — у такого улова не было windex и данных поклёвки (сом 1857 г 15.11). Теперь та же поклёвка дополнительно
    /// отдаётся здесь (только запись события 'bite'; уведомление и запись улова идут по-старому через FULL_CATCH).
    /// </summary>
    public event Action<DeepCatchData>? BiteInFullCatchDetected;

    /// <summary>DATA-01: сообщения, смысл которых ещё не разобран, — отдаются как есть (время, от клиента?, номер, байты данных) для записи.</summary>
    public event Action<DateTime, bool, int, byte[]>? ResearchMessageDetected;
    public static readonly HashSet<int> ResearchServerCmds = new() { 186, 162, 1000, 15, 255 };
    public static readonly HashSet<int> ResearchClientCmds = new() { 182 };
    public event Action<DeepCatchData>? HookDetected;
    /// <summary>FIX-01: c2s 1135 (8 B) — «Забрать» for a landed fish; payload = int64 uid of that fish (= CATCH_SUMMARY uid,
    /// CONTROLS-01: 9/9 matched s2c 1130). The «Отпустить» button was not checked.</summary>
    public event Action<long, DateTime>? FishTakenDetected;
    public event Action<DeepCatchData>? WaitDetected;
    public event Action<ServerCheck>? ServerCheckDetected;      // NEXT-BITE-IMPLEMENT-01: c2s 1120 (URod.KLUpd, 112 B)
    public event Action<DeepCatchData>? WeatherDetected;
    // NEXT-GTWC-01: объявления об уловах игроков (cmd 6 / 5), сообщения чата (cmd 50), время сервера (cmd 255 / 105).
    public event Action<PublicCatchEvent>? PublicCatchDetected;
    public event Action<ChatEvent>? ChatMessageDetected;
    public event Action<DateTime, DateTime>? ServerTimeDetected;   // (время сервера, время приёма ПК)
    /// <summary>NEXT-ANALYTICS-13: локальный server-side player ID, извлечённый из c2s kind=2 cmd 1006 (byte[] param 3, offset 0).</summary>
    public event Action<uint>? LocalPlayerIdDetected;
    /// <summary>OVERLAY-03: живая поза игрока (c2s 1006: X,Y,Z +9, поворот +33). Track — потокобезопасная история.</summary>
    public event Action<PlayerPose>? PlayerPoseDetected;
    public PlayerTrack Track { get; } = new PlayerTrack();

        public int BaitCount => _baits.Count;
        public int ItemCount => _items.Count;
        public int FishCount => _fish.Count;
        public int FishRecordCount => _fishRecordCount;
        public int LocationCount => _locations.Count;
        public int BaitModifierCount => _baitModifiers.Count;
        public int NetworkFishCount => _networkFish.Count;
        public string DataDirectory => _dataDir;
        public IReadOnlyList<BaitModifier> BaitModifiers => _baitModifiers;

        public sealed class BaitInfo
        {
            public string Name { get; init; } = "";
            public string NameDe { get; init; } = "";
            public string Category { get; init; } = "";
            public int? TypeId { get; init; }
            public List<string> Fish { get; init; } = new();
            public List<int> FishIds { get; init; } = new();
            public string IconPath { get; init; } = "";
            public double MinWeight { get; init; }
            public double MaxWeight { get; init; }
            // NEXT-DATA-01: raw catalog characteristics (semantics UNKNOWN; stored as-is, never interpreted)
            public string? BaseParams { get; init; }
            public string? KlevPropRaw { get; init; }
            public string? SubType { get; init; }
            public string? TypeName { get; init; }
        }

        public sealed class FishInfo
        {
            public uint Id { get; init; }
            public string Name { get; init; } = "";
            public string NameSmall { get; init; } = "";
            public string Latin { get; init; } = "";
            public string ModelId { get; init; } = "";
            public string IconPath { get; init; } = "";
            public double MinWeight { get; init; }
            public double MaxWeight { get; init; }
            public bool IsMutant { get; init; }
            public bool IsAggression { get; init; }
            public bool HasWeight => MaxWeight > 0;
        }

        public sealed class LocationInfo
        {
            public uint Id { get; init; }
            public string Name { get; init; } = "";
            public int? MaxDeepCm { get; init; }
            public double? FlowForce { get; init; }
            public List<int> FishIds { get; init; } = new();
            public string ImagePath { get; init; } = "";
        }

        public sealed class NetworkFishInfo
        {
            public string Name { get; init; } = "";
            public uint? FishLangId { get; init; }
            public string Confidence { get; init; } = "";
        }

        public sealed class GearItem
        {
            public uint Id { get; set; }
            public uint Quantity { get; set; }
            public uint State { get; set; }
            public string? Name { get; set; }
        }

        /// <summary>Разобранная формула опыта вида "0:0+50,2+20/1:0+100...".</summary>
        public sealed class ExperienceFormula
        {
            public Dictionary<int, Dictionary<int, int>> Groups { get; } = new();

            public static ExperienceFormula? Parse(string? text)
            {
                if (string.IsNullOrWhiteSpace(text)) return null;
                var formula = new ExperienceFormula();

                foreach (string groupPart in text.Split('/'))
                {
                    int colon = groupPart.IndexOf(':');
                    if (colon <= 0) continue;
                    if (!int.TryParse(groupPart.AsSpan(0, colon), out int group)) continue;

                    var items = new Dictionary<int, int>();
                    foreach (string itemPart in groupPart.AsSpan(colon + 1).ToString().Split(','))
                    {
                        int plus = itemPart.IndexOf('+');
                        if (plus <= 0) continue;
                        if (int.TryParse(itemPart.AsSpan(0, plus), out int item) &&
                            int.TryParse(itemPart.AsSpan(plus + 1), out int value))
                        {
                            items[item] = value;
                        }
                    }
                    if (items.Count > 0) formula.Groups[group] = items;
                }

                return formula.Groups.Count > 0 ? formula : null;
            }

            /// <summary>Сумма всех наград в группе. Группа 0 — награда за поимку (наблюдение).</summary>
            public int SumGroup(int group)
            {
                return Groups.TryGetValue(group, out var items) ? items.Values.Sum() : 0;
            }

            public int CatchExperience => SumGroup(0);

            public string Describe()
            {
                var sb = new StringBuilder();
                foreach (var kv in Groups.OrderBy(k => k.Key))
                {
                    sb.Append(kv.Key).Append(": ");
                    sb.Append(string.Join(", ", kv.Value.OrderBy(i => i.Key).Select(i => $"{i.Key}+{i.Value}")));
                    sb.Append("  ");
                }
                return sb.ToString().TrimEnd();
            }
        }

        public sealed class DeepCatchData
        {
            public DateTime Timestamp { get; set; }
            public int PacketLength { get; set; }
            public string MessageType { get; set; } = "";
            public bool IsClientPacket { get; set; }

            // NEXT-SESSION-23H: SHA-256 собранного кадра (источник события).
            public string? SourceIdentity { get; set; }

            // --- серверная поимка (FULL) ---
            public uint? FishId { get; set; }              // сетевой ID (offset 32)
            public uint? FishLangId { get; set; }          // ID из fishLang (SUMMARY)
            public string? FishName { get; set; }
            public uint? Weight { get; set; }              // граммы
            public string? Grade { get; set; }
            public ushort? FormulaLength { get; set; }
            public string? PreferenceString { get; set; }
            public List<float> RangeValues { get; } = new();
            public float? Float03 { get; set; }
            public float? QualityFloat { get; set; }
            public uint? BaitUid { get; set; }
            public uint? LocationId { get; set; }
            public uint? BaseId { get; set; }   // NEXT-85: SUMMARY blob+28 = baseid/location (was mislabeled 'quality')
            public int? RodIndex { get; set; }   // NEXT-88: rod number for cast/event linking
            public uint? CatchUid { get; set; }   // NEXT-112: CATCH_SUMMARY blob+4 = event identity
            public long? CastId { get; set; }     // NEXT-ANALYTICS-04: casts.id proven for this summary (else null)
            public uint? Windex { get; set; }   // NEXT-88: size class 0-3
            public BiteMetadata? Bite { get; set; }   // NEXT-BITE-IMPLEMENT-01: full s2c 1120 flag=1 payload (strict parse)
            public float? Shance { get; set; }        // NEXT-BITE-IMPLEMENT-01: s2c 1120 flag=0 raw float (semantics UNVERIFIED)
            public float? CoordZ { get; set; }
            public uint? TailFlag { get; set; }
            public uint? HashValue { get; set; }
            public ExperienceFormula? Experience { get; set; }

            // --- SUMMARY ---
            public int? FishCount { get; set; }
            public int? Quality { get; set; }
            public uint? BaitId { get; set; }
            public string? BaitName { get; set; }
            public string? BaitCategory { get; set; }
            public List<string> BaitFish { get; } = new();
            public string? BaitSource { get; set; }

            // --- клиентское состояние ---
            public double? PlayerX { get; set; }
            public double? PlayerY { get; set; }
            public double? PlayerZ { get; set; }
            public double? CastX { get; set; }             // точка поплавка/заброса
            public double? CastY { get; set; }
            public double? CastZ { get; set; }
            public double? GridX { get; set; }             // квантование 5 м
            public double? GridY { get; set; }
            public double? GridZ { get; set; }
            public float? DepthCm { get; set; }            // глубина в САНТИМЕТРАХ
            public float? WaterLevelCm { get; set; }
            public float? WindKmh { get; set; }            // кандидат: км/ч, требует проверки

            // --- погода (kind=4, len=124, subtype=14) ---
            public float? AirTemperatureC { get; set; }
            public float? WindMs { get; set; }
            public float? Humidity { get; set; }
            public float? Cloudly { get; set; }     // s2c 1001 (NEXT-85)
            public float? Fog { get; set; }          // s2c 1001
            public float? Rain { get; set; }         // s2c 1001
            public float? Grom { get; set; }         // s2c 1001 (thunder)

            // --- снасти (wpn=...) ---
            public List<GearItem> GearItems { get; } = new();
            public EquipmentState? Equipment { get; set; }
            public string? RawText { get; set; }
        }

        public PacketDeepParser(string? dataDir = null)
        {
            _dataDir = dataDir
                       ?? Environment.GetEnvironmentVariable("FISHER_DATA_DIR")
                       ?? ResolveDefaultDataDir();

            LoadJsonDatabases();
        }

        /// <summary>
        /// Каталог с JSON-базами: сначала data рядом с приложением,
        /// затем data проекта FisherLogger, затем общая папка исследований.
        /// </summary>
        public static string ResolveDefaultDataDir()
        {
            string appData = Path.Combine(AppContext.BaseDirectory, "data");
            if (File.Exists(Path.Combine(appData, "baits_map.json"))) return appData;

            const string projectData = @"D:\TheFisherProject\Data"; // NEXT-101: isolated dictionary dir
            if (File.Exists(Path.Combine(projectData, "baits_map.json"))) return projectData;

            return projectData; // NEXT-95: no fallback outside NEXT95 (was D:\fishing_research)
        }

        // ------------------------------------------------------------------
        // Публичный API
        // ------------------------------------------------------------------

        /// <summary>
        /// Сбрасывает состояние между сессиями (дедупликация кадров).
        /// Полезно при старте нового захвата или перед импортом другого PCAP.
        /// </summary>
        public void Reset()
        {
            lock (_sync)
            {
                _processedFrames.Clear();
            }
        }

        /// <summary>
        /// Разбирает все кадры FB 00 00 00 в одном TCP-payload (кадры могут быть склеены).
        /// Метод потокобезопасен: результат материализуется под блокировкой,
        /// чтобы HashSet дедупликации не повредился при параллельных вызовах.
        /// </summary>
        /// <summary>
        /// Поддерживает оба варианта Photon-заголовка:
        ///   01 F3 &lt;kind&gt;       (текущие TCP-дампы, kind на +8)
        ///   00 01 F3 &lt;kind&gt;    (UDP/новый вариант, kind на +9)
        /// Возвращает kind или 0.
        /// </summary>
        public byte GetPacketKind(byte[] payload)
        {
            if (payload == null || payload.Length < 10) return 0;
            if (payload[0] != 0xFB || payload[1] != 0x00 ||
                payload[2] != 0x00 || payload[3] != 0x00) return 0;

            if (payload[6] == 0x00 && payload[7] == 0x01 && payload[8] == 0xF3)
                return payload[9];
            if (payload[6] == 0x01 && payload[7] == 0xF3)
                return payload[8];
            return 0;
        }

        private static byte[] NormalizeFrameHeader(byte[] frame)
        {
            if (frame.Length >= 10 && frame[6] == 0x00 && frame[7] == 0x01 &&
                frame[8] == 0xF3)
            {
                ushort oldLength = BitConverter.ToUInt16(frame, 4);
                if (oldLength < 21 || oldLength > frame.Length) return frame;

                // Убираем лишний 00, чтобы Kind снова оказался на +8,
                // а все проверенные смещенияpayload остались прежними.
                var normalized = new byte[frame.Length - 1];
                Array.Copy(frame, 0, normalized, 0, 6);
                Array.Copy(frame, 7, normalized, 6, frame.Length - 7);
                ushort newLength = (ushort)(oldLength - 1);
                normalized[4] = (byte)(newLength & 0xFF);
                normalized[5] = (byte)(newLength >> 8);
                return normalized;
            }
            return frame;
        }

        public IReadOnlyList<DeepCatchData> ParseFrames(byte[] payload, DateTime timestamp)
        {
            if (payload == null || payload.Length < 12) return Array.Empty<DeepCatchData>();

            lock (_sync)
            {
                var result = new List<DeepCatchData>();
                int pos = 0;
                while (pos <= payload.Length - 8)
                {
                    int marker = IndexOf(payload, FrameMagic, pos);
                    if (marker < 0) break;

                    if (marker + 8 > payload.Length) break;
                    uint frameLength = ((uint)payload[marker + 1] << 24) | ((uint)payload[marker + 2] << 16) | ((uint)payload[marker + 3] << 8) | payload[marker + 4];
                    if (payload[marker + 7] != 0xF3 || frameLength < 20 || frameLength > 10_000 || marker + (int)frameLength > payload.Length)
                    {
                        pos = marker + 1;
                        continue;
                    }

                    var frame = new byte[(int)frameLength];
                    Array.Copy(payload, marker, frame, 0, (int)frameLength);

                    // NEXT-89 R2: no SHA-256 dedup — real repeated s2c events (1120/1122/1001) must be kept.
                    // AUDIT-04 (C2): отпечаток считается только для разобранных кадров (раньше — для каждого кадра потока)
                    var parsed = ParseSingleFrame(frame, timestamp);
                    if (parsed != null)
                    {
                        parsed.SourceIdentity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(frame));
                        result.Add(parsed);
                    }

                    pos = marker + (int)frameLength;
                }

                return result;
            }
        }

        /// <summary>
        /// Обёртка для реалтайма: принимает TCP-payload и отдаёт все события.
        /// Идентична ParseFrames, оставлена как явная точка входа для сниффера.
        /// </summary>
        public IEnumerable<DeepCatchData> ParsePayload(byte[] payload, DateTime timestamp)
            => ParseFrames(payload, timestamp);

        /// <summary>Совместимость со старым API: первый разобранный кадр.</summary>
        public DeepCatchData? ParseCatchPacket(byte[] payload, DateTime timestamp)
            => ParseFrames(payload, timestamp).FirstOrDefault();

        // ------------------------------------------------------------------
        // Диспетчер кадров
        // ------------------------------------------------------------------
        private DeepCatchData? ParseSingleFrame(byte[] frame, DateTime timestamp)
        {
            if (frame.Length < 20) return null;
            if (frame.Length < 1 || frame[0] != 0xFB) return null;  // NEXT-89 R1

            byte kind = frame[8];
            TryCaptureResearch(frame, timestamp, kind);   // DATA-01: только побочная запись, разбор ниже не меняется
            switch (kind)
            {
                case KindServerCatch:
                    // NEXT-04B (D): эвристика TryParseCatchSummary85 отключена —
                    // 85-байтные кадры не являются сводкой (0 срабатываний на 242 кадрах).
                    if (frame.Length == 209 || frame.Length == 218)   // LOGFIX-03 (A): это может быть поклёвка 1120 flag=1
                    {
                        var fb = TryParseCmd1120Bite(frame, timestamp);
                        if (fb != null)
                        {
                            if (TryParseFullCatch(frame, timestamp) is not { } fc) return fb;   // как раньше: дальше по цепочке её нашёл бы TryParseCmd1120Bite
                            BiteInFullCatchDetected?.Invoke(fb);
                            return fc;
                        }
                    }
                    return TryParseFullCatch(frame, timestamp)
                           ?? TryParseCatchSummary(frame, timestamp)
                           ?? TryParseCmd1120Bite(frame, timestamp)
                           ?? TryParseCmd1122EmptyBite(frame, timestamp)
                           ?? TryParseCmd1120Wait(frame, timestamp)
                           ?? TryParseCmd1001Weather(frame, timestamp)
                           ?? TryParseServerTime(frame, timestamp);

                case KindServerEvent:
                    // NEXT-04B (E): в kind=4 не разбираем ни сводку, ни 85 —
                    // это давало ложные поимки из чат-кадров. FULL оставлен,
                    // но ограничен whitelist 209/218 внутри TryParseFullCatch.
                    // NEXT-GTWC-01: события игры cmd 6/5 (уловы на локации / в клубе) и cmd 50 (чат) — первыми,
                    // чтобы их байты не принимались эвристиками ниже (CATCH_NOTIFY len 75 и т.п.).
                    return TryParsePublicEvent(frame, timestamp)
                           ?? TryParseFullCatch(frame, timestamp)
                           ?? TryParseWeather(frame, timestamp)
                           ?? TryParseItemAction(frame, timestamp)
                           ?? TryParseCatchNotify(frame, timestamp);

                case 13:
                    // По новым данным Kind=13 — heartbeat/синхронизация,
                    // а не инвентарь. Инвентарь остаётся доступен только
                    // через явный ParseInventory() для диагностики.
                    _heartbeatCount++;
                    if (_heartbeatCount % 100 == 0)
                        Report("[HEARTBEAT] Kind=13 пакет #" + _heartbeatCount);
                    return null;

                case KindClientState:
                    TryParseLocalPlayerId(frame, timestamp); // NEXT-ANALYTICS-13, OVERLAY-03
                    // feed/craft — тоже kind=2, но с особым subtype.
                    if (TryParseFeedFrame(frame, timestamp, out var feed))
                    {
                        FeedActionDetected?.Invoke(feed!);
                        return null;
                    }
                    if (TryParseCraftFrame(frame, timestamp, out var craft))
                    {
                        CraftActionDetected?.Invoke(craft!);
                        return null;
                    }
                    // NEXT-DATA-01: c2s 1112 / c2s 200 are recorded as a side effect only; the existing chain below is unchanged.
                    TryParseCmd1112Groundbait(frame, timestamp);
                    TryParseCmd200Reload(frame, timestamp);
                    TryParseCmd1120ServerCheck(frame, timestamp); // NEXT-BITE-IMPLEMENT-01: side effect only
                    TryParseCmd1135Take(frame, timestamp);        // FIX-01: side effect only
                    return TryParseCmd1110Cast(frame, timestamp)
                           ?? TryParseCmd1125Hook(frame, timestamp)
                           ?? TryParseClientState(frame, timestamp);
            }

            return null;
        }

        // ------------------------------------------------------------------
        // NEXT-SESSION-23E: cmd=1120 flag=1 — ранняя поклёвка (вес + вид)
        // Port Photon-параметров из Workbench EventPipeline.ParsePhoton.
        // ------------------------------------------------------------------
        private DeepCatchData? TryParseCmd1120Bite(byte[] frame, DateTime timestamp)
        {
            if (frame.Length < 13 || frame[8] != KindServerCatch) return null;
            try
            {
                int p = 9;
                p++;                 // operation code
                p += 2;              // return code
                byte debugType = frame[p++];
                if (debugType != 0x2A)
                {
                    if (!SkipGpValue(frame, ref p, debugType)) return null;
                }

                int n = (frame[p] << 8) | frame[p + 1];
                p += 2;
                int? cmd = null;
                byte[]? data = null;
                for (int i = 0; i < n; i++)
                {
                    int key = frame[p++];
                    byte type = frame[p++];
                    object? val = ReadGpValue(frame, ref p, type);
                    if (val == null) return null;
                    if (key == 1 && val is int ci) cmd = ci;
                    else if (key == 3 && val is byte[] b) data = b;
                }

                if (cmd != 1120 || data == null || data.Length < 11 || data[4] != 1) return null;

                // NEXT-ANALYTICS-04: same cmd 1120 layout as the wait frame (rod int32 at data+0, flag at data+4).
                int rod = BitConverter.ToInt32(data, 0);
                int weight = BitConverter.ToInt32(data, 5);
                int slen = BitConverter.ToUInt16(data, 9);
                if (slen < 0 || 11 + slen + 20 + 4 > data.Length) return null;
                int fishId = BitConverter.ToInt32(data, 11 + slen + 20);

                if (weight <= 0 || weight > 200_000) return null;

                // NEXT-BITE-IMPLEMENT-01: the rest of the confirmed payload; attached only if it parses strictly and agrees
                // with the species/weight read above (those two values are not changed by this step).
                var meta = BiteMetadata.Parse(data);
                if (meta != null && (meta.FishId != fishId || meta.Weight != weight || meta.Rod != rod)) meta = null;
                if (meta == null) Report("[BITE] s2c 1120 flag=1: хвост пакета не разобран (длина " + data.Length + ") — метаданные не сохранены");

                return new DeepCatchData
                {
                    Timestamp = timestamp,
                    PacketLength = frame.Length,
                    MessageType = "EARLY_BITE_1120",
                    IsClientPacket = false,
                    RodIndex = rod >= 0 && rod <= 15 ? rod : null, // NEXT-ANALYTICS-04: out-of-range = unknown rod
                    RawText = $"rod={rod} fid={fishId} weight={weight}" + (meta != null ? $" windex={meta.Windex} wkf={meta.Wkf.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}" : ""),
                    Weight = (uint)weight,
                    FishLangId = (uint)fishId,
                    FishName = ResolveFishName(null, (uint)fishId),
                    Windex = meta != null ? (uint)meta.Windex : null,
                    Bite = meta
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>DATA-01: номер сообщения (параметр 1) и данные (параметр 3) кадра kind 2/3/4; совпало со списком — событие.</summary>
        private void TryCaptureResearch(byte[] f, DateTime ts, byte kind)
        {
            if (ResearchMessageDetected == null) return;
            try
            {
                int p = 9;
                if (kind == KindClientState || kind == KindServerEvent) p += 1;                 // код операции / события
                else if (kind == KindServerCatch)
                {
                    p += 3;                                                                      // код операции + код возврата
                    byte dt = f[p++];
                    if (dt != 0x2A && !SkipGpValue(f, ref p, dt)) return;                       // отладочное сообщение
                }
                else return;
                if (p + 2 > f.Length) return;
                int n = (f[p] << 8) | f[p + 1]; p += 2;
                int? cmd = null; byte[]? data = null;
                for (int i = 0; i < n && p + 2 <= f.Length; i++)
                {
                    int key = f[p++]; byte type = f[p++];
                    if (key == 1 && type == 0x69) { if (ReadGpValue(f, ref p, type) is int ci) cmd = ci; else return; }
                    else if (key == 3 && type == 0x78) { data = ReadGpValue(f, ref p, type) as byte[]; if (data == null) return; }
                    else if (!SkipGpValue(f, ref p, type)) return;
                }
                if (cmd is not int c || data == null) return;
                bool client = kind == KindClientState;
                if (!(client ? ResearchClientCmds : ResearchServerCmds).Contains(c)) return;
                ResearchMessageDetected?.Invoke(ts == default ? DateTime.Now : ts, client, c, data);
            }
            catch { }
        }

        private static bool SkipGpValue(byte[] b, ref int p, byte type)
        {
            switch (type)
            {
                case 0x2A: return true;
                case 0x62: case 0x6F: p++; return true;
                case 0x6B: p += 2; return true;
                case 0x69: case 0x66: p += 4; return true;
                case 0x6C: case 0x64: p += 8; return true;
                case 0x73: { if (p + 2 > b.Length) return false; int len = (b[p] << 8) | b[p + 1]; p += 2 + len; return p <= b.Length; }
                case 0x78: { if (p + 4 > b.Length) return false; int len = (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]; p += 4 + len; return p <= b.Length; }
                default: return false;
            }
        }

        private static object? ReadGpValue(byte[] b, ref int p, byte type)
        {
            switch (type)
            {
                case 0x2A: return null;
                case 0x62: { if (p >= b.Length) return null; return b[p++]; }
                case 0x6F: { if (p >= b.Length) return null; return b[p++] != 0; }
                case 0x6B: { if (p + 2 > b.Length) return null; int v = (short)((b[p] << 8) | b[p + 1]); p += 2; return v; }
                case 0x69: { if (p + 4 > b.Length) return null; int v = (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]; p += 4; return v; }
                case 0x66: { if (p + 4 > b.Length) return null; int v = (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]; p += 4; return BitConverter.Int32BitsToSingle(v); }
                case 0x6C: { if (p + 8 > b.Length) return null; long v = ((long)(uint)((b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]) << 32) | (uint)((b[p + 4] << 24) | (b[p + 5] << 16) | (b[p + 6] << 8) | b[p + 7]); p += 8; return v; }
                case 0x64: { if (p + 8 > b.Length) return null; long v = ((long)(uint)((b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]) << 32) | (uint)((b[p + 4] << 24) | (b[p + 5] << 16) | (b[p + 6] << 8) | b[p + 7]); p += 8; return BitConverter.Int64BitsToDouble(v); }
                case 0x73: { if (p + 2 > b.Length) return null; int len = (b[p] << 8) | b[p + 1]; p += 2; if (p + len > b.Length) return null; string s = System.Text.Encoding.UTF8.GetString(b, p, len); p += len; return s; }
                case 0x78: { if (p + 4 > b.Length) return null; int len = (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]; p += 4; if (len < 0 || p + len > b.Length) return null; byte[] arr = new byte[len]; Array.Copy(b, p, arr, 0, len); p += len; return arr; }
                default: return null;
            }
        }

        // ------------------------------------------------------------------
        // Kind=13: инвентарь/профиль (UID → item_id)
        // ------------------------------------------------------------------
        public int ParseInventory(byte[] payload)
        {
            if (!TryExtractFrame(payload, 13, out byte[] frame)) return _uidToItemId.Count;
            return ParseInventoryFrame(frame, DateTime.Now);
        }

        private int ParseInventoryFrame(byte[] frame, DateTime timestamp)
        {
            if (frame.Length < 12 || frame[8] != 13) return _uidToItemId.Count;

            // Точная структура Kind=13 в доступных дампах отсутствует, поэтому
            // проверяем несколько правдоподобных вариантов и принимаем только
            // плотный массив валидных записей.
            var best = new Dictionary<uint, uint>();
            int bestStart = 0, bestStride = 0;
            foreach (int start in new[] { 8, 12, 16, 20, 24 })
            {
                foreach (int stride in new[] { 8, 12, 16, 20 })
                {
                    var candidate = TryReadInventoryRecords(frame, start, stride);
                    if (candidate.Count > best.Count)
                    {
                        best = candidate;
                        bestStart = start;
                        bestStride = stride;
                    }
                }
            }

            if (best.Count < 3) return _uidToItemId.Count;

            _uidToItemId.Clear();
            foreach (var pair in best) _uidToItemId[pair.Key] = pair.Value;

            Report($"[INVENTORY] Kind=13: {_uidToItemId.Count} предметов " +
                   $"(UID→item_id, start={bestStart}, record={bestStride})");
            InventoryUpdated?.Invoke(_uidToItemId.Count);
            return _uidToItemId.Count;
        }

        private static Dictionary<uint, uint> TryReadInventoryRecords(byte[] frame, int start, int stride)
        {
            var result = new Dictionary<uint, uint>();
            if (start < 0 || start + stride > frame.Length) return result;

            int possible = 0;
            for (int offset = start; offset + 12 <= frame.Length; offset += stride)
            {
                possible++;
                uint uid = BitConverter.ToUInt32(frame, offset);
                uint itemId = BitConverter.ToUInt32(frame, offset + 4);
                uint quantity = BitConverter.ToUInt32(frame, offset + 8);

                // UID в игре — крупное число; item_id — небольшой ID предмета.
                if (uid < 1000 || uid > 4_000_000_000u) continue;
                if (itemId == 0 || itemId > 100_000) continue;
                if (quantity > 1_000_000) continue;
                result[uid] = itemId;
            }

            // Отсекаем случайные совпадения: записи должны занимать заметную
            // часть проверяемого диапазона.
            if (possible < 3 || result.Count < 3) return new Dictionary<uint, uint>();
            if (result.Count * 2 < possible) return new Dictionary<uint, uint>();
            return result;
        }

        // ------------------------------------------------------------------
        // Kind=2, subtype 0xC000: действие прикормки
        // ------------------------------------------------------------------
        public FeedAction? ParseFeedAction(byte[] payload, DateTime timestamp)
        {
            if (!TryExtractFrame(payload, 2, out byte[] frame)) return null;
            return TryParseFeedFrame(frame, timestamp, out var action) ? action : null;
        }

        private bool TryParseFeedFrame(byte[] frame, DateTime timestamp, out FeedAction? action)
        {
            action = null;
            if (frame.Length < 50 || frame.Length > 80) return false;
            if (frame[8] != 2) return false;
            if (frame.Length < 18) return false;

            ushort subtype = BitConverter.ToUInt16(frame, 16);
            if (subtype != 0xC000) return false;

            string text = Encoding.ASCII.GetString(frame, Math.Min(20, frame.Length),
                Math.Max(0, frame.Length - 20));
            uint uid = ExtractFirstLongNumber(text);
            uint? itemId = uid > 0 && _uidToItemId.TryGetValue(uid, out uint id) ? id : null;

            action = new FeedAction
            {
                Timestamp = timestamp,
                BaitUid = uid,
                BaitItemId = itemId,
                BaitName = itemId.HasValue && _items.TryGetValue(itemId.Value, out var item)
                    ? item.Name
                    : ""
            };
            // NEXT-DATA-01: the same frame decoded structurally = c2s 192 (int64 rod uid, int16-len string name, int16-len string value).
            // Only adds the rod uid; the existing heuristic fields above are unchanged.
            var pc = DecodePhotonCmd(frame);
            if (pc != null && pc.Value.cmd == 192 && pc.Value.data.Length >= 12)
            {
                var d = pc.Value.data;
                long rodUid = BitConverter.ToInt64(d, 0);
                int nlen = BitConverter.ToInt16(d, 8);
                if (rodUid > 0 && nlen > 0 && 10 + nlen <= d.Length)
                {
                    action.RodUid = rodUid;
                    action.PropName = Encoding.ASCII.GetString(d, 10, nlen);
                }
            }
            return true;
        }

        // ------------------------------------------------------------------
        // Kind=2, subtype 0xAB06: крафт
        // ------------------------------------------------------------------
        public CraftAction? ParseCraftAction(byte[] payload, DateTime timestamp)
        {
            if (!TryExtractFrame(payload, 2, out byte[] frame)) return null;
            return TryParseCraftFrame(frame, timestamp, out var action) ? action : null;
        }

        private bool TryParseCraftFrame(byte[] frame, DateTime timestamp, out CraftAction? action)
        {
            action = null;
            if (frame.Length != 88 && frame.Length != 147) return false;
            if (frame[8] != 2) return false;
            if (BitConverter.ToUInt16(frame, 4) != frame.Length) return false;
            if (frame.Length < 18 || BitConverter.ToUInt16(frame, 16) != 0xAB06)
                return false;
            if (frame.Length < 72) return false;

            uint mainUid = BitConverter.ToUInt32(frame, 44);
            uint aromaUid = BitConverter.ToUInt32(frame, 52);
            uint addUid = BitConverter.ToUInt32(frame, 60);
            uint baitUid = BitConverter.ToUInt32(frame, 68);

            action = new CraftAction
            {
                Timestamp = timestamp,
                MainWpnUid = mainUid,
                MainWpnItemId = LookupUid(mainUid),
                AromaWpnUid = aromaUid,
                AromaWpnItemId = LookupUid(aromaUid),
                AddWpnUid = addUid,
                AddWpnItemId = LookupUid(addUid),
                BaitUid = baitUid,
                BaitItemId = LookupUid(baitUid)
            };
            return true;
        }

        private uint? LookupUid(uint uid)
            => uid > 0 && _uidToItemId.TryGetValue(uid, out uint itemId) ? itemId : null;

        private static uint ExtractFirstLongNumber(string text)
        {
            int start = -1;
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsDigit(text[i]))
                {
                    if (start < 0) start = i;
                }
                else if (start >= 0)
                {
                    int length = i - start;
                    if (length >= 6 &&
                        uint.TryParse(text.AsSpan(start, length), out uint value))
                        return value;
                    start = -1;
                }
            }
            if (start >= 0 && text.Length - start >= 6 &&
                uint.TryParse(text.AsSpan(start), out uint tail))
                return tail;
            return 0;
        }

        private static bool TryExtractFrame(byte[] payload, byte expectedKind, out byte[] frame)
        {
            frame = Array.Empty<byte>();
            if (payload == null || payload.Length < 12) return false;
            int marker = IndexOf(payload, FrameMagic, 0);
            if (marker < 0 || marker + 9 > payload.Length) return false;
            if (!TryU16(payload, marker + 4, out ushort length)) return false;
            if (length < 20 || marker + length > payload.Length) return false;
            int kindOffset = marker + 8;
            if (marker + 9 < payload.Length &&
                payload[marker + 6] == 0x00 &&
                payload[marker + 7] == 0x01 &&
                payload[marker + 8] == 0xF3)
                kindOffset = marker + 9;
            if (payload[kindOffset] != expectedKind) return false;

            frame = new byte[length];
            Array.Copy(payload, marker, frame, 0, length);
            frame = NormalizeFrameHeader(frame);
            return true;
        }

        // ------------------------------------------------------------------
        // kind=3: полная поимка (len 209/218)
        // ------------------------------------------------------------------
        private DeepCatchData? TryParseFullCatch(byte[] frame, DateTime timestamp)
        {
            // NEXT-04B (A1): FULL_CATCH принимаем только по подтверждённым длинам 209/218.
            // Иначе XML-кадры (215) доходят до чтения полей.
            if (frame.Length != 209 && frame.Length != 218) return null;

            // NEXT-04B (A2): u32@32 — не ID рыбы, а склейка [len&0xFF][blob[0]].
            // Поле не читаем; вид определяется по fish_lang_id (+20).
            if (!TryU32(frame, 38, out uint weight)) return null;
            if (!TryU16(frame, 42, out ushort formulaLength)) return null;

            if (!CatchRules.IsRecordableWeight(weight))
            {
                Report($"[FILTER] Пропуск FULL_CATCH {weight}гр — недопустимый вес");
                return null;
            }

            int formulaStart = FormulaOffset;
            if (formulaLength == 0 || formulaLength > 512) return null;
            if (formulaStart + formulaLength + 16 > frame.Length) return null;

            string formulaText = Encoding.ASCII.GetString(frame, formulaStart, formulaLength);
            if (!formulaText.Contains(':') || !formulaText.Contains('+')) return null;

            // ВАЖНО: никакого padding. 4 float32 начинаются сразу после строки.
            int rangesStart = formulaStart + formulaLength;
            if (rangesStart + 32 > frame.Length) return null;

            var ranges = new float[4];
            for (int i = 0; i < 4; i++)
            {
                ranges[i] = BitConverter.ToSingle(frame, rangesStart + i * 4);
                if (float.IsNaN(ranges[i]) || float.IsInfinity(ranges[i]) ||
                    Math.Abs(ranges[i]) > 100_000f)
                {
                    return null;
                }
            }

            int cursor = rangesStart + 16;

            // NEXT-04B (B): подтверждены только +0 = 0.3f и +4 = fish_lang_id.
            // +8 коррелирует с весом (w-80)/3920, но семантика не доказана.
            // location_id в этом кадре отсутствует — его даёт Player.log (locid).
            float quality = BitConverter.ToSingle(frame, cursor);
            uint fishLangId = BitConverter.ToUInt32(frame, cursor + 4);
            uint tailValue = cursor + 12 <= frame.Length - 4
                ? BitConverter.ToUInt32(frame, cursor + 12)
                : 0;

            var data = new DeepCatchData
            {
                Timestamp = timestamp,
                PacketLength = frame.Length,
                MessageType = "FULL_CATCH",
                IsClientPacket = false,
                // NEXT-04B (A2): FishId намеренно не заполняется — u32@32 синтетический.
                FishLangId = fishLangId is > 0 and < 100_000 ? fishLangId : null,
                Weight = weight,
                FormulaLength = formulaLength,
                PreferenceString = formulaText,
                QualityFloat = !float.IsNaN(quality) && quality is >= 0 and <= 100 ? quality : null,
                Float03 = Math.Abs(quality - ExpectedFloat03) < 0.01f ? quality : null,
                BaitUid = tailValue > 1_000_000 ? tailValue : null,
                Grade = DetermineGrade(weight),
                Experience = ExperienceFormula.Parse(formulaText)
            };

            foreach (float r in ranges) data.RangeValues.Add(r);

            if (cursor + 12 <= frame.Length - 4)
                data.TailFlag = tailValue;
            if (frame.Length >= 4)
                data.HashValue = BitConverter.ToUInt32(frame, frame.Length - 4);

            data.FishName = ResolveFishName(null, data.FishLangId);
            return data;
        }

        // ------------------------------------------------------------------
        // kind=3: сводка поимки (len 108/110) — здесь есть fishLang ID и наживка
        // ------------------------------------------------------------------
        /// <summary>
        /// Компактная сводка len=85: вес хранится float32, а не uint.
        /// Точная структура в доступных дампах отсутствует, поэтому ищем
        /// выровненную пару fish_lang_id + float weight и требуем, чтобы
        /// fish_lang_id был известен базе.
        /// </summary>
        private DeepCatchData? TryParseCatchSummary85(byte[] frame, DateTime timestamp)
        {
            if (frame.Length != 85 || (frame[8] != 3 && frame[8] != 4)) return null;

            for (int pos = 12; pos + 4 <= frame.Length; pos += 4)
            {
                float weightF = BitConverter.ToSingle(frame, pos);
                if (float.IsNaN(weightF) || float.IsInfinity(weightF) ||
                    weightF < LegacySummary85MinWeight || weightF > 200_000)
                    continue;

                foreach (int fishPos in new[] { pos - 4, pos - 8, pos + 4 })
                {
                    if (fishPos < 8 || fishPos + 4 > frame.Length) continue;
                    uint fishLangId = BitConverter.ToUInt32(frame, fishPos);
                    if (fishLangId == 0 || fishLangId > 100_000) continue;
                    if (!_fish.ContainsKey(fishLangId)) continue;

                    uint? baitId = null;
                    string baitName = "";
                    string baitCategory = "";
                    foreach (int baitPos in new[] { pos + 4, pos + 8, pos + 12 })
                    {
                        if (baitPos + 4 > frame.Length) continue;
                        uint candidate = BitConverter.ToUInt32(frame, baitPos);
                        if (candidate > 0 && candidate < 100_000 &&
                            _baits.TryGetValue(candidate, out var bait))
                        {
                            baitId = candidate;
                            baitName = bait.Name;
                            baitCategory = bait.Category;
                            break;
                        }
                    }

                    float qualityF = 0;
                    if (pos + 16 <= frame.Length)
                        qualityF = BitConverter.ToSingle(frame, pos + 16);
                    int? quality = !float.IsNaN(qualityF) && qualityF is >= 0 and < 1000
                        ? (int)Math.Round(qualityF)
                        : null;

                    var data = new DeepCatchData
                    {
                        Timestamp = timestamp,
                        PacketLength = frame.Length,
                        MessageType = "CATCH_SUMMARY",
                        IsClientPacket = false,
                        FishLangId = fishLangId,
                        Weight = (uint)Math.Round(weightF),
                        Quality = quality,
                        BaitId = baitId,
                        BaitName = baitName,
                        BaitCategory = baitCategory,
                        BaitSource = "summary85_float",
                        Grade = DetermineGrade((uint)Math.Round(weightF))
                    };
                    data.FishName = ResolveFishName(null, fishLangId);
                    Report($"[SUMMARY85] fish_lang_id={fishLangId}, weight_float={weightF:F1}");
                    return data;
                }
            }

            return null;
        }

        private DeepCatchData? TryParseCatchSummary(byte[] frame, DateTime timestamp)
        {
            // NEXT-04B (C): длина зависит от строки достижений (102/108/110),
            // поэтому проверяем структуру блоба, а не только длину кадра.
            if (frame.Length is < 96 or > 130) return null;
            if (frame[8] != KindServerCatch) return null;
            if (!TryFindBlob(frame, out int blob, out int blobLength)) return null;
            if (blobLength is < 60 or > 130) return null;
            // Варианты сводки имеют subtype 0/1/2; 14 — погода, дальше — прочее.
            int rodIndex = frame[blob]; // NEXT-89 R3: blob[0] = rod_index (was misread as subtype)
            if (rodIndex > 15) return null;

            uint fishLangId = BitConverter.ToUInt32(frame, blob + 12);
            uint weight = BitConverter.ToUInt32(frame, blob + 16);
            uint count = BitConverter.ToUInt32(frame, blob + 20);
            uint baitId = BitConverter.ToUInt32(frame, blob + 24);
            uint baseId = BitConverter.ToUInt32(frame, blob + 28); // NEXT-85: actually baseid/location

            if (!CatchRules.IsRecordableWeight(weight))
            {
                if (weight > 0)
                    Report($"[FILTER] Пропуск CATCH_SUMMARY {weight}гр — недопустимый вес");
                return null;
            }
            if (fishLangId == 0 || fishLangId > 100_000) return null;

            var data = new DeepCatchData
            {
                Timestamp = timestamp,
                PacketLength = frame.Length,
                MessageType = "CATCH_SUMMARY",
                IsClientPacket = false,
                FishLangId = fishLangId,
                Weight = weight,
                FishCount = count is > 0 and < 1000 ? (int)count : null,
                BaseId = baseId,
                RodIndex = rodIndex,
                CatchUid = BitConverter.ToUInt32(frame, blob + 4), // NEXT-112: summary event identity
                Quality = null, // NEXT-85: real quality is quality_float (FULL); int 'quality' was location
                BaitId = baitId is > 0 and < 100_000 ? baitId : null,
                BaitSource = "server_summary(blob+24)",
                Grade = DetermineGrade(weight)
            };

            if (data.BaitId.HasValue && _baits.TryGetValue(data.BaitId.Value, out var bait))
            {
                data.BaitName = bait.Name;
                data.BaitCategory = bait.Category;
                data.BaitFish.AddRange(bait.Fish);
            }

            data.FishName = ResolveFishName(null, fishLangId);
            return data;
        }

        // ------------------------------------------------------------------
        // s2c 1001: погода (28 байт payload, 5×float32) — NEXT-81/85
        // ------------------------------------------------------------------
        private DeepCatchData? TryParseCmd1001Weather(byte[] frame, DateTime timestamp)
        {
            if (frame.Length < 20 || frame[8] != KindServerCatch) return null;
            try
            {
                int p = 9;
                p++;                 // operation code
                if (frame[8] == KindServerCatch)
                {
                    p += 2;          // return code (server only)
                    byte debugType = frame[p++];
                    if (debugType != 0x2A)
                    {
                        if (!SkipGpValue(frame, ref p, debugType)) return null;
                    }
                }
                int n = (frame[p] << 8) | frame[p + 1];
                p += 2;
                int? cmd = null;
                byte[]? data = null;
                for (int i = 0; i < n; i++)
                {
                    int key = frame[p++];
                    byte type = frame[p++];
                    object? val = ReadGpValue(frame, ref p, type);
                    if (val == null) return null;
                    if (key == 1 && val is int ci) cmd = ci;
                    else if (key == 3 && val is byte[] b) data = b;
                }
                if (cmd != 1001 || data == null || data.Length != 28) return null;

                float cloudly = BitConverter.ToSingle(data, 0);
                float fog = BitConverter.ToSingle(data, 4);
                float rain = BitConverter.ToSingle(data, 8);
                float tempr = BitConverter.ToSingle(data, 12);
                float grom = BitConverter.ToSingle(data, 16);

                var wd = new DeepCatchData
                {
                    Timestamp = timestamp,
                    PacketLength = frame.Length,
                    MessageType = "WEATHER_1001",
                    IsClientPacket = false,
                    Cloudly = cloudly,
                    Fog = fog,
                    Rain = rain,
                    AirTemperatureC = tempr,
                    Grom = grom,
                    RawText = $"s2c1001: cloudly={cloudly:F2} fog={fog:F2} rain={rain:F2} tempr={tempr:F2} grom={grom:F2}"
                };
                WeatherDetected?.Invoke(wd);
                return wd;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------
        // NEXT-ANALYTICS-13: c2s kind=2 cmd 1006 несёт локальный player ID как UInt32 LE в начале byte[] (param 3).
        // Только структурный разбор Photon-параметров (DecodePhotonCmd): поиск байтов по кадру не выполняется.
        // ------------------------------------------------------------------
        private void TryParseLocalPlayerId(byte[] frame, DateTime timestamp)
        {
            if (frame.Length < 20 || frame[8] != KindClientState) return;
            var r = DecodePhotonCmd(frame);
            if (r == null || r.Value.cmd != 1006 || r.Value.data.Length < 4) return;
            uint pid = BitConverter.ToUInt32(r.Value.data, 0);
            if (pid == 0 || pid > 100_000_000) return;
            LocalPlayerIdDetected?.Invoke(pid);
            // OVERLAY-03: та же команда несёт позицию и поворот игрока
            var pose = PlayerTrack.Parse(r.Value.data, timestamp);
            if (pose != null && Track.Add(pose)) PlayerPoseDetected?.Invoke(pose);
        }

        // ------------------------------------------------------------------
        // NEXT-87: события процесса рыбалки (Photon cmd-слой)
        // ------------------------------------------------------------------
        /// <summary>NEXT-BAIT-05: тот же структурный разбор Photon-команды для канала инвентаря (Inventory.cs).</summary>
        internal static (int cmd, byte[] data)? DecodeCommand(byte[] frame) => DecodePhotonCmd(frame);

        private static (int cmd, byte[] data)? DecodePhotonCmd(byte[] frame)
        {
            if (frame.Length < 20 || (frame[8] != KindServerCatch && frame[8] != KindClientState && frame[8] != KindServerEvent)) return null;
            try
            {
                int p = 9;
                p++;                 // operation code
                if (frame[8] == KindServerCatch)
                {
                    p += 2;          // return code (server only)
                    byte debugType = frame[p++];
                    if (debugType != 0x2A)
                    {
                        if (!SkipGpValue(frame, ref p, debugType)) return null;
                    }
                }
                int n = (frame[p] << 8) | frame[p + 1];
                p += 2;
                int? cmd = null;
                byte[]? data = null;
                for (int i = 0; i < n; i++)
                {
                    int key = frame[p++];
                    byte type = frame[p++];
                    object? val = ReadGpValue(frame, ref p, type);
                    if (val == null) return null;
                    if (key == 1 && val is int ci) cmd = ci;
                    else if (key == 3 && val is byte[] b) data = b;
                }
                return cmd.HasValue ? (cmd.Value, data ?? Array.Empty<byte>()) : null;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------
        // NEXT-GTWC-01: публичные события (Photon событие, параметры 1=cmd, 2=packid, 3=byte[]).
        // Структуры подтверждены дизассемблированием клиента (NEXT-CHAT-SOURCE-01):
        //   cmd 6/5 → ActorMgr.fishMsgDin: long playerId, string name, int fishId, int windex, int weight,
        //             int level, int title, long clubId, string clubTag;
        //   cmd 50  → GameChat.ChatMessage(DataIn): int roomid, long fromId, string fromName, long toPrivate,
        //             string extra, string text, string channel, [int level, int title, long clubId, string clubTag].
        // DataIn: little-endian; string = Int16 длина + UTF-8.
        // ------------------------------------------------------------------
        public sealed class PublicCatchEvent
        {
            public DateTime ReceivedAt;
            public int Cmd;
            public long PlayerId;
            public string PlayerName = "";
            public int FishId;
            public string FishName = "";
            public int Windex;
            public int Weight;
            public int Level;
            public int Title;
            public long ClubId;
            public string ClubTag = "";
            public string SourceKey = "";
            /// <summary>Канал, как его выбирает клиент: windex &gt; 2 → rcrd; клуб: clubfish / clubtrof (windex == 3).</summary>
            public string Channel => Cmd == 5 ? (Windex == 3 ? "clubtrof" : "clubfish") : (Windex > 2 ? "rcrd" : "fish");
        }

        public sealed class ChatEvent
        {
            public DateTime ReceivedAt;
            public int RoomId;
            public long FromId;
            public string FromName = "";
            public long ToPrivate;
            public string Extra = "";
            public string Text = "";
            public string Channel = "";
            public int? Level;
            public int? Title;
            public long? ClubId;
            public string? ClubTag;
            public string SourceKey = "";
            public bool IsPrivate => ToPrivate > 0;
            public bool IsAction => Text.StartsWith("▸cmd[", StringComparison.Ordinal);
        }

        private sealed class LeReader
        {
            private readonly byte[] _b; private int _p;
            public LeReader(byte[] b) { _b = b; }
            public bool Eof => _p >= _b.Length;
            public int Remaining => _b.Length - _p;
            private void Need(int n) { if (n < 0 || _p + n > _b.Length) throw new FormatException("short data"); }
            public short I16() { Need(2); short v = BitConverter.ToInt16(_b, _p); _p += 2; return v; }
            public int I32() { Need(4); int v = BitConverter.ToInt32(_b, _p); _p += 4; return v; }
            public long I64() { Need(8); long v = BitConverter.ToInt64(_b, _p); _p += 8; return v; }
            public string Str()
            {
                short n = I16();
                if (n <= 0) return "";
                Need(n);
                string s = Encoding.UTF8.GetString(_b, _p, n); _p += n; return s;
            }
        }

        private static string EventKey(int cmd, byte[] data, DateTime ts)
            => cmd + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).Substring(0, 32) + ":" + ts.ToString("yyyyMMddHHmmss");

        private DeepCatchData? TryParsePublicEvent(byte[] frame, DateTime timestamp)
        {
            if (frame.Length < 20 || frame[8] != KindServerEvent) return null;
            var r = DecodePhotonCmd(frame);
            if (r == null) return null;
            int cmd = r.Value.cmd; byte[] data = r.Value.data;
            if (cmd != 6 && cmd != 5 && cmd != 50) return null;
            try
            {
                var rd = new LeReader(data);
                if (cmd == 50)
                {
                    var ce = new ChatEvent
                    {
                        ReceivedAt = timestamp,
                        RoomId = rd.I32(), FromId = rd.I64(), FromName = rd.Str(), ToPrivate = rd.I64(),
                        Extra = rd.Str(), Text = rd.Str(), Channel = rd.Str()
                    };
                    if (rd.Remaining >= 4 + 4 + 8 + 2)
                    {
                        ce.Level = rd.I32(); ce.Title = rd.I32(); ce.ClubId = rd.I64(); ce.ClubTag = rd.Str();
                    }
                    ce.SourceKey = EventKey(cmd, data, timestamp);
                    ChatMessageDetected?.Invoke(ce);
                    return new DeepCatchData
                    {
                        Timestamp = timestamp, PacketLength = frame.Length, MessageType = "CHAT_MESSAGE", IsClientPacket = false,
                        RawText = ce.IsPrivate ? "cmd50 private (content not logged)" : "cmd50 room=" + ce.RoomId + " " + ce.Channel
                    };
                }
                var pc = new PublicCatchEvent
                {
                    ReceivedAt = timestamp, Cmd = cmd,
                    PlayerId = rd.I64(), PlayerName = rd.Str(), FishId = rd.I32(), Windex = rd.I32(), Weight = rd.I32(),
                    Level = rd.I32(), Title = rd.I32(), ClubId = rd.I64(), ClubTag = rd.Str()
                };
                if (pc.FishId <= 0 || pc.Weight <= 0 || pc.Weight > 1_000_000 || pc.PlayerName.Length == 0)
                    throw new FormatException("implausible values");
                pc.FishName = ResolveFishName(null, (uint)pc.FishId);
                pc.SourceKey = EventKey(cmd, data, timestamp);
                PublicCatchDetected?.Invoke(pc);
                return new DeepCatchData
                {
                    Timestamp = timestamp, PacketLength = frame.Length, MessageType = "PUBLIC_CATCH", IsClientPacket = false,
                    FishLangId = (uint)pc.FishId, FishName = pc.FishName, Weight = (uint)pc.Weight,
                    RawText = "cmd" + cmd + " " + pc.PlayerName + " fish=" + pc.FishId + " w=" + pc.Weight + " windex=" + pc.Windex
                };
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                Report("[FEED] повреждённое событие cmd " + cmd + " (" + data.Length + " байт) пропущено: " + ex.Message);
                return new DeepCatchData { Timestamp = timestamp, PacketLength = frame.Length, MessageType = "PUBLIC_EVENT_MALFORMED", IsClientPacket = false };
            }
        }

        // s2c cmd 255 (ping, ~10 с) и cmd 105 (вход): время сервера — DataIn.readDateTime = 6×Int16 (год, месяц, день, час, мин, сек).
        private DeepCatchData? TryParseServerTime(byte[] frame, DateTime timestamp)
        {
            if (frame[8] != KindServerCatch) return null;
            var r = DecodePhotonCmd(frame);
            if (r == null) return null;
            int off = r.Value.cmd == 255 ? 4 : r.Value.cmd == 105 ? 16 : -1;
            var d = r.Value.data;
            if (off < 0 || d.Length < off + 12) return null;
            int y = BitConverter.ToInt16(d, off), mo = BitConverter.ToInt16(d, off + 2), dd = BitConverter.ToInt16(d, off + 4),
                h = BitConverter.ToInt16(d, off + 6), mi = BitConverter.ToInt16(d, off + 8), s = BitConverter.ToInt16(d, off + 10);
            if (y < 2000 || y > 2100 || mo is < 1 or > 12 || dd < 1 || dd > DateTime.DaysInMonth(y, mo) || h is < 0 or > 23 || mi is < 0 or > 59 || s is < 0 or > 59)
                return null;
            var server = new DateTime(y, mo, dd, h, mi, s);
            ServerTimeDetected?.Invoke(server, timestamp);
            return new DeepCatchData { Timestamp = timestamp, PacketLength = frame.Length, MessageType = "SERVER_TIME", IsClientPacket = false,
                                       RawText = "cmd" + r.Value.cmd + " server=" + server.ToString("yyyy-MM-dd HH:mm:ss") };
        }

        // c2s 1110: заброс (rod int32, xyz 3×double, bait_uid int64) — 36B payload
        private DeepCatchData? TryParseCmd1110Cast(byte[] frame, DateTime timestamp)
        {
            if (frame[8] != KindClientState) return null;
            var r = DecodePhotonCmd(frame);
            if (r == null || r.Value.cmd != 1110 || r.Value.data.Length != 36) return null;
            var d = r.Value.data;
            int rod = BitConverter.ToInt32(d, 0);
            double x = BitConverter.ToDouble(d, 4);
            double y = BitConverter.ToDouble(d, 12);
            double z = BitConverter.ToDouble(d, 20);
            long baitUid = BitConverter.ToInt64(d, 28);
            var ev = new DeepCatchData { Timestamp = timestamp, PacketLength = frame.Length, MessageType = "CAST_1110", IsClientPacket = true, CastX = x, CastY = y, CastZ = z, BaitUid = baitUid > 0 ? (uint)baitUid : null, RodIndex = rod, RawText = $"rod={rod} xyz={x:F2},{y:F2},{z:F2} baitUid={baitUid}" };
            CastDetected?.Invoke(ev);
            return ev;
        }

        // NEXT-DATA-01: c2s 1112 — внесение прикорма: int64 uid прикорма, int64 uid кормушки (0 = без кормушки), 3×double xyz — ровно 40 Б.
        // Проверено: 71 кадр в 12 захватах; с кормушкой xyz = точка предыдущего заброса (71/71).
        private void TryParseCmd1112Groundbait(byte[] frame, DateTime timestamp)
        {
            var r = DecodePhotonCmd(frame);
            if (r == null || r.Value.cmd != 1112 || r.Value.data.Length != 40) return;
            var d = r.Value.data;
            long gb = BitConverter.ToInt64(d, 0), feeder = BitConverter.ToInt64(d, 8);
            double x = BitConverter.ToDouble(d, 16), y = BitConverter.ToDouble(d, 24), z = BitConverter.ToDouble(d, 32);
            if (gb <= 0 || feeder < 0 || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return;
            GroundbaitApplied?.Invoke(new GroundbaitAction
            {
                Timestamp = timestamp, GroundbaitUid = gb, FeederUid = feeder, X = x, Y = y, Z = z,
                SourceKey = "c2s1112|" + gb + "|" + feeder + "|" + timestamp.Ticks
            });
        }

        // NEXT-DATA-01: c2s 200 — перезарядка наживки: int64 uid пачки, int32 count, int32 bag (71+удочка), int32 cell (20), int64 uid удочки — ровно 28 Б.
        private void TryParseCmd200Reload(byte[] frame, DateTime timestamp)
        {
            var r = DecodePhotonCmd(frame);
            if (r == null || r.Value.cmd != 200 || r.Value.data.Length != 28) return;
            var d = r.Value.data;
            long stack = BitConverter.ToInt64(d, 0), rod = BitConverter.ToInt64(d, 20);
            int count = BitConverter.ToInt32(d, 8), bag = BitConverter.ToInt32(d, 12), cell = BitConverter.ToInt32(d, 16);
            if (stack <= 0 || rod <= 0 || count < 0) return;
            BaitReloaded?.Invoke(new BaitReload
            {
                Timestamp = timestamp, StackUid = stack, Count = count, Bag = bag, Cell = cell, RodUid = rod,
                SourceKey = "c2s200|" + stack + "|" + rod + "|" + timestamp.Ticks
            });
        }

        // FIX-01: c2s 1135 «Забрать» — int64 uid of the landed fish (8 B)
        private void TryParseCmd1135Take(byte[] frame, DateTime timestamp)
        {
            if (frame[8] != KindClientState) return;
            var r = DecodePhotonCmd(frame);
            if (r == null || r.Value.cmd != 1135 || r.Value.data.Length != 8) return;
            long uid = BitConverter.ToInt64(r.Value.data, 0);
            if (uid > 0) FishTakenDetected?.Invoke(uid, timestamp);
        }

        // c2s 1125: подсечка (rod int32) — 4B payload
        private DeepCatchData? TryParseCmd1125Hook(byte[] frame, DateTime timestamp)
        {
            if (frame[8] != KindClientState) return null;
            var r = DecodePhotonCmd(frame);
            if (r == null || r.Value.cmd != 1125 || r.Value.data.Length < 4) return null;
            int rod = BitConverter.ToInt32(r.Value.data, 0);
            var ev = new DeepCatchData { Timestamp = timestamp, PacketLength = frame.Length, MessageType = "HOOK_1125", IsClientPacket = true, RodIndex = rod, RawText = $"rod={rod}" };
            HookDetected?.Invoke(ev);
            return ev;
        }

        // s2c 1122: пустая поклёвка (rod, fish_id, windex, weight — 4×int32) — 16B payload
        private DeepCatchData? TryParseCmd1122EmptyBite(byte[] frame, DateTime timestamp)
        {
            if (frame[8] != KindServerCatch) return null;
            var r = DecodePhotonCmd(frame);
            if (r == null || r.Value.cmd != 1122 || r.Value.data.Length < 16) return null;
            var d = r.Value.data;
            int rod = BitConverter.ToInt32(d, 0);
            uint fid = (uint)BitConverter.ToInt32(d, 4);
            uint windex = (uint)BitConverter.ToInt32(d, 8);
            uint weight = (uint)BitConverter.ToInt32(d, 12);
            var ev = new DeepCatchData { Timestamp = timestamp, PacketLength = frame.Length, MessageType = "EMPTY_BITE_1122", IsClientPacket = false, FishLangId = fid, Weight = weight, RodIndex = rod, Windex = windex, RawText = $"rod={rod} fid={fid} windex={windex} weight={weight}" };
            EmptyBiteDetected?.Invoke(ev);
            return ev;
        }

        // NEXT-BITE-IMPLEMENT-01: c2s 1120 with a 112-byte payload = client server check (URod.KLUpd); raised as a side effect.
        private void TryParseCmd1120ServerCheck(byte[] frame, DateTime timestamp)
        {
            if (ServerCheckDetected == null || frame.Length < 20 || frame[8] != KindClientState) return;
            var r = DecodePhotonCmd(frame);
            if (r == null || r.Value.cmd != 1120) return;
            var c = ServerCheck.Parse(r.Value.data, timestamp);
            if (c != null) ServerCheckDetected.Invoke(c);
        }

        // s2c 1120 flag=0: ожидание/chance (rod, flag=0, chance float32, long, tail) — 25B payload
        private DeepCatchData? TryParseCmd1120Wait(byte[] frame, DateTime timestamp)
        {
            if (frame[8] != KindServerCatch) return null;
            var r = DecodePhotonCmd(frame);
            if (r == null || r.Value.cmd != 1120 || r.Value.data.Length < 25 || r.Value.data[4] != 0) return null;
            var d = r.Value.data;
            int rod = BitConverter.ToInt32(d, 0);
            float chance = BitConverter.ToSingle(d, 5);
            var ev = new DeepCatchData { Timestamp = timestamp, PacketLength = frame.Length, MessageType = "WAIT_1120", IsClientPacket = false, RodIndex = rod, Shance = chance, RawText = $"rod={rod} chance={chance:F2} (semantics UNVERIFIED)" };
            WaitDetected?.Invoke(ev);
            return ev;
        }

        // ------------------------------------------------------------------
        // kind=4: погода (len 124, subtype 14)
        // ------------------------------------------------------------------
        private DeepCatchData? TryParseWeather(byte[] frame, DateTime timestamp)
        {
            if (frame.Length != 124) return null;
            if (!TryFindBlob(frame, out int blob, out int blobLength)) return null;
            if (blobLength < 28) return null;

            uint subtype = BitConverter.ToUInt32(frame, blob);
            if (subtype != 14) return null;

            double temperature = BitConverter.ToDouble(frame, blob + 4);
            double wind = BitConverter.ToDouble(frame, blob + 12);
            double humidity = BitConverter.ToDouble(frame, blob + 20);

            if (double.IsNaN(temperature) || temperature is < -60 or > 70) return null;
            if (double.IsNaN(wind) || wind is < 0 or > 100) return null;
            if (double.IsNaN(humidity) || humidity is < 0 or > 100) return null;

            return new DeepCatchData
            {
                Timestamp = timestamp,
                PacketLength = frame.Length,
                MessageType = "WEATHER",
                IsClientPacket = false,
                AirTemperatureC = (float)temperature,
                WindMs = (float)wind,
                Humidity = (float)humidity,
                RawText = $"subtype=14; temp={temperature:F2}°C; wind={wind:F2} м/с; humidity={humidity:F1}%"
            };
        }

        // ------------------------------------------------------------------
        // kind=4: строки wpn=<id>/<qty>/<state>;
        // ------------------------------------------------------------------
        private DeepCatchData? TryParseItemAction(byte[] frame, DateTime timestamp)
        {
            string text = Encoding.ASCII.GetString(frame);
            int idx = text.IndexOf("wpn=", StringComparison.Ordinal);
            if (idx < 0) return null;

            var data = new DeepCatchData
            {
                Timestamp = timestamp,
                PacketLength = frame.Length,
                MessageType = "ITEM_ACTION",
                IsClientPacket = false,
                RawText = text
            };

            var equipment = new EquipmentState { Timestamp = timestamp };

            int p = idx;
            while (p < text.Length)
            {
                int start = text.IndexOf("wpn=", p, StringComparison.Ordinal);
                if (start < 0) break;
                int end = text.IndexOf(';', start);
                if (end < 0) break;

                string payload = text.Substring(start + 4, end - start - 4);
                string[] parts = payload.Split('/');
                if (parts.Length >= 1 && uint.TryParse(parts[0], out uint id))
                {
                    var gear = new GearItem
                    {
                        Id = id,
                        Quantity = parts.Length > 1 && uint.TryParse(parts[1], out uint q) ? q : 0,
                        State = parts.Length > 2 && uint.TryParse(parts[2], out uint s) ? s : 0
                    };
                    gear.Name = ResolveItemName(id);
                    data.GearItems.Add(gear);

                    string category = _items.TryGetValue(id, out var item) ? item.Category : "";
                    switch (category)
                    {
                        case "rod": equipment.RodId = id; break;
                        case "reel": equipment.ReelId = id; break;
                        case "line": equipment.LineId = id; break;
                        case "hook": equipment.HookId = id; break;
                        case "float": equipment.FloatId = id; break;
                        case "feeder": equipment.FeederId = id; break;
                    }
                }

                p = end + 1;
            }

            if (equipment.RodId.HasValue || equipment.ReelId.HasValue ||
                equipment.LineId.HasValue || equipment.HookId.HasValue ||
                equipment.FloatId.HasValue || equipment.FeederId.HasValue)
                data.Equipment = equipment;

            return data.GearItems.Count > 0 ? data : null;
        }

        // ------------------------------------------------------------------
        // kind=4: уведомление о поимке (len 75)
        // ------------------------------------------------------------------
        private DeepCatchData? TryParseCatchNotify(byte[] frame, DateTime timestamp)
        {
            if (frame.Length != 75) return null;
            if (!TryU32(frame, 53, out uint weight)) return null;
            if (weight is 0 or > 200_000) return null;

            return new DeepCatchData
            {
                Timestamp = timestamp,
                PacketLength = frame.Length,
                MessageType = "CATCH_NOTIFY",
                IsClientPacket = false,
                Weight = weight,
                Grade = DetermineGrade(weight)
            };
        }

        // ------------------------------------------------------------------
        // kind=2: состояние игрока/поплавка
        // ------------------------------------------------------------------
        private DeepCatchData? TryParseClientState(byte[] frame, DateTime timestamp)
        {
            if (!TryFindBlob(frame, out int blob, out int blobLength)) return null;
            if (blobLength < 24) return null;

            var runs = FindQuantizedDoubleRuns(frame, blob, blobLength);
            if (runs.Count == 0) return null;

            var data = new DeepCatchData
            {
                Timestamp = timestamp,
                PacketLength = frame.Length,
                MessageType = "CLIENT_STATE",
                IsClientPacket = true
            };

            // NEXT-115: c2s 1120, 148-byte frame = per-rod state; rod index at blob+4 (int32), depth at blob+64.
            // Verified on session_03 (236 frames, rod 0/1/2 match the 1110 cast coordinates of the same rod).
            if (frame.Length == 148 && blob + 8 <= frame.Length && DecodePhotonCmd(frame)?.cmd == 1120)
            {
                int rod = BitConverter.ToInt32(frame, blob + 4);
                if (rod is >= 0 and <= 15) data.RodIndex = rod;
            }

            // Первый длинный run — позиция игрока (если runs >= 2).
            if (runs.Count >= 2 && runs[0].Count >= 3)
            {
                data.PlayerX = runs[0][0].Value;
                data.PlayerY = runs[0][1].Value;
                data.PlayerZ = runs[0][2].Value;
            }

            var last = runs[^1];
            double[] values = last.Select(x => x.Value).ToArray();

            // NEXT-04B (F, консервативно): для 147/192/243 координаты заброса не выдаём —
            // там позиция игрока/углы, а не поплавок. 72/76/179 и 148 — без изменений.
            bool suppressCast = frame.Length is 147 or 192 or 243;

            double[]? cast = null;
            if (!suppressCast)
            {
                if (values.Length >= 6)
                {
                    double[] a = { values[0], values[1], values[2] };
                    double[] b = { values[3], values[4], values[5] };

                    if (IsGridTriple(b))
                    {
                        cast = a;
                        data.GridX = b[0];
                        data.GridY = b[1];
                        data.GridZ = b[2];
                    }
                    else if (IsGridTriple(a))
                    {
                        cast = b;
                        data.GridX = a[0];
                        data.GridY = a[1];
                        data.GridZ = a[2];
                    }
                    else
                    {
                        double water = data.GridY ?? DefaultWaterLevelCm / 100.0;
                        cast = Math.Abs(a[1] - water) <= Math.Abs(b[1] - water) ? a : b;
                    }
                }
                else
                {
                    cast = new[] { values[0], values[1], values[2] };
                }
            }

            if (cast != null)
            {
                data.CastX = cast[0];
                data.CastY = cast[1];
                data.CastZ = cast[2];
            }

            // Глубина: подтверждённое поле blob+64 (см) только для кадра 148 б.
            if (frame.Length == 148 && blob + 68 <= frame.Length)
            {
                float depth = BitConverter.ToSingle(frame, blob + 64);
                if (!float.IsNaN(depth) && depth is >= 0 and <= 10_000)
                    data.DepthCm = depth;

                float waterLevel = BitConverter.ToSingle(frame, blob + 68);
                if (!float.IsNaN(waterLevel) && waterLevel is > 0 and <= 10_000)
                    data.WaterLevelCm = waterLevel;

                if (blob + 107 <= frame.Length)
                {
                    float wind = BitConverter.ToSingle(frame, blob + 103);
                    if (!float.IsNaN(wind) && wind is >= 0 and <= 300)
                        data.WindKmh = wind;
                }
            }

            if (!data.DepthCm.HasValue && data.GridY.HasValue && data.CastY.HasValue)
            {
                double depth = (data.GridY.Value - data.CastY.Value) * 100.0;
                if (depth is >= 0 and <= 10_000)
                    data.DepthCm = (float)depth;
            }

            // Наживка в клиентском кадре: не подтверждена, но ищем известные ID.
            if (data.BaitId == null)
            {
                for (int o = blob; o + 4 <= blob + blobLength; o++)
                {
                    uint candidate = BitConverter.ToUInt32(frame, o);
                    if (_baits.TryGetValue(candidate, out var bait))
                    {
                        data.BaitId = candidate;
                        data.BaitName = bait.Name;
                        data.BaitCategory = bait.Category;
                        data.BaitSource = "client_scan(эвристика, требует проверки)";
                        data.BaitFish.AddRange(bait.Fish);
                        break;
                    }
                }
            }

            return data;
        }

        // ------------------------------------------------------------------
        // Поиск и классификация 8-байтовых double с обнулёнными младшими 3 байтами
        // ------------------------------------------------------------------
        private readonly struct QDouble
        {
            public QDouble(int offset, double value) { Offset = offset; Value = value; }
            public int Offset { get; }
            public double Value { get; }
        }

        private static List<List<QDouble>> FindQuantizedDoubleRuns(byte[] frame, int blob, int blobLength)
        {
            var candidates = new SortedDictionary<int, double>();
            int end = blob + blobLength - 8;

            for (int o = blob; o <= end; o++)
            {
                // Квантование: младшие 3 байта double равны нулю.
                if (frame[o] != 0 || frame[o + 1] != 0 || frame[o + 2] != 0) continue;

                double v = BitConverter.ToDouble(frame, o);
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                if (Math.Abs(v) < 1e-4 || Math.Abs(v) > 1e6) continue;

                candidates[o] = v;
            }

            var runs = new List<List<QDouble>>();
            foreach (var kv in candidates)
            {
                if (runs.Count > 0 && kv.Key == runs[^1][^1].Offset + 8)
                    runs[^1].Add(new QDouble(kv.Key, kv.Value));
                else
                    runs.Add(new List<QDouble> { new(kv.Key, kv.Value) });
            }

            return runs.Where(r => r.Count >= 3).ToList();
        }

        private static bool IsGridTriple(double[] v)
        {
            if (v.Length < 3) return false;
            bool near5(double x) => Math.Abs(x - Math.Round(x / 5.0) * 5.0) < 0.01;
            return near5(v[0]) && near5(v[1]) && near5(v[2]) &&
                   v[1] is > -100 and < 1000;
        }

        // ------------------------------------------------------------------
        // Вспомогательные методы чтения
        // ------------------------------------------------------------------
        private static bool TryU16(byte[] b, int o, out ushort v)
        {
            if (o < 0 || o + 2 > b.Length) { v = 0; return false; }
            v = BitConverter.ToUInt16(b, o);
            return true;
        }

        private static bool TryU32(byte[] b, int o, out uint v)
        {
            if (o < 0 || o + 4 > b.Length) { v = 0; return false; }
            v = BitConverter.ToUInt32(b, o);
            return true;
        }

        private static bool TryFindBlob(byte[] frame, out int blob, out int blobLength)
        {
            blob = 0;
            blobLength = 0;

            for (int i = 8; i + 6 <= frame.Length; i++)
            {
                if (frame[i] != 0x03 || frame[i + 1] != 0x78) continue;

                int len = (frame[i + 2] << 24) | (frame[i + 3] << 16) |
                          (frame[i + 4] << 8) | frame[i + 5];

                if (len > 0 && len < 1_000_000 && i + 6 + len == frame.Length)
                {
                    blob = i + 6;
                    blobLength = len;
                    return true;
                }
            }

            return false;
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start)
        {
            for (int i = start; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        private static string DetermineGrade(uint weight)
        {
            if (weight < 100) return "СЕРЫЙ";
            if (weight < 300) return "ЗЕЛЁНЫЙ";
            if (weight < 600) return "СИНИЙ";
            if (weight < 1000) return "ФИОЛЕТОВЫЙ";
            return "ЗОЛОТОЙ";
        }

        private string ResolveFishName(uint? networkId, uint? fishLangId)
        {
            if (networkId.HasValue && _networkFish.TryGetValue(networkId.Value, out var nf))
                return nf.Name;

            if (fishLangId.HasValue && _fish.TryGetValue(fishLangId.Value, out var f))
                return string.IsNullOrEmpty(f.Name) ? "Fish_" + fishLangId.Value : f.Name;

            if (networkId.HasValue)
                return "Fish_" + networkId.Value;

            return "?";
        }

        private string ResolveItemName(uint id)
        {
            if (_items.TryGetValue(id, out var item)) return item.Name;
            if (_baits.TryGetValue(id, out var bait)) return bait.Name;
            return "Item_" + id;
        }

        /// <summary>
        /// NEXT-107: all fish whose fish_full_database.json model_id equals the Player.log model file
        /// (e.g. "37_karasSilver"). Several results = the model is shared (ambiguous); none = unknown.
        /// The numeric prefix of the model name is NOT used: it differs from the fish id for several models.
        /// </summary>
        public IReadOnlyList<FishInfo> FindFishByModel(string? modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId)) return Array.Empty<FishInfo>();
            var list = new List<FishInfo>();
            foreach (var f in _fish.Values)
                if (!string.IsNullOrEmpty(f.ModelId) && string.Equals(f.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
                    list.Add(f);
            return list;
        }

        public FishInfo? GetFish(uint? fishLangId)
        {
            if (!fishLangId.HasValue) return null;
            return _fish.TryGetValue(fishLangId.Value, out var fish) ? fish : null;
        }

        public FishInfo? GetFishByName(string? fishName)
        {
            if (string.IsNullOrWhiteSpace(fishName)) return null;
            return _fishByName.TryGetValue(fishName.Trim(), out var fish) ? fish : null;
        }

        public BaitInfo? GetItem(uint? itemId)
        {
            if (!itemId.HasValue) return null;
            if (_items.TryGetValue(itemId.Value, out var item)) return item;
            return _baits.TryGetValue(itemId.Value, out var bait) ? bait : null;
        }

        public BaitInfo? GetBait(uint? itemId)
        {
            if (!itemId.HasValue) return null;
            return _baits.TryGetValue(itemId.Value, out var bait) ? bait : null;
        }

        public LocationInfo? GetLocation(uint? locationId)
        {
            if (!locationId.HasValue) return null;
            uint id = locationId.Value;
            if (_locations.TryGetValue(id, out var location)) return location;
            if (_locationAliases.TryGetValue(id, out uint alias) &&
                _locations.TryGetValue(alias, out location))
                return location;
            return null;
        }

        /// <summary>
        /// Эвристика только для SUMMARY-only: подбирает типичный network_id
        /// по fish_lang_id и весу. Это не подтверждённый ID, поэтому в
        /// CatchData он помечается autodetect_source = network_heuristic.
        /// </summary>
        public uint? GuessNetworkId(uint? fishLangId, uint weight)
        {
            if (!fishLangId.HasValue) return null;
            if (!_reverseNetwork.TryGetValue(fishLangId.Value, out var candidates) ||
                candidates.Count == 0)
                return null;

            if (fishLangId.Value == 52)
            {
                if (weight >= 800 && candidates.Contains(697u)) return 697u;
                if (candidates.Contains(688u)) return 688u;
            }
            else if (fishLangId.Value == 66)
            {
                if (candidates.Contains(110u)) return 110u;
            }

            return candidates[0];
        }

        // ------------------------------------------------------------------
        // Загрузка JSON
        // ------------------------------------------------------------------
        private void Report(string message)
        {
            Diagnostics?.Invoke(message);
        }

        private void LoadJsonDatabases()
        {
            LoadItemsFullDatabase(Path.Combine(_dataDir, "items_full_database.json"));
            if (_items.Count == 0)
            {
                // Совместимость: если новой базы нет, работаем по старой.
                LoadBaits(Path.Combine(_dataDir, "baits_map.json"));
            }

            LoadFishFullDatabase(Path.Combine(_dataDir, "fish_full_database.json"));
            if (_fish.Count == 0)
            {
                LoadFishCatalog(Path.Combine(_dataDir, "fish_catalog.json"));
            }

            LoadLocationMap(Path.Combine(_dataDir, "location_map.json"));
            LoadBaitModifiers(Path.Combine(_dataDir, "bait_modifiers.json"));
            LoadNetworkMap(Path.Combine(_dataDir, "network_fish_map.json"));
        }

        private static JsonElement GetArrayRoot(JsonDocument doc, string propertyName)
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return doc.RootElement;
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(propertyName, out var array) &&
                array.ValueKind == JsonValueKind.Array)
                return array;
            return default;
        }

        /// <summary>NEXT-DATA-01: catalog value as raw text (string or number); missing/null/empty → null.</summary>
        private static string? RawText(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var v)) return null;
            string? t = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null
            };
            return string.IsNullOrWhiteSpace(t) ? null : t.Trim();
        }

        private static string GetString(JsonElement element, params string[] names)
        {
            if (element.ValueKind != JsonValueKind.Object) return "";
            foreach (string name in names)
            {
                if (element.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? "";
            }
            return "";
        }

        private static double GetDouble(JsonElement element, string name, double fallback = 0)
        {
            if (element.ValueKind != JsonValueKind.Object) return fallback;
            if (!element.TryGetProperty(name, out var value)) return fallback;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
                return number;
            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out number))
                return number;
            return fallback;
        }

        private static int? GetInt(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            if (!element.TryGetProperty(name, out var value)) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
                return number;
            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), out number))
                return number;
            return null;
        }

        private static bool GetBool(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object) return false;
            if (!element.TryGetProperty(name, out var value)) return false;
            return value.ValueKind == JsonValueKind.True;
        }

        private void LoadItemsFullDatabase(string path)
        {
            if (!File.Exists(path))
            {
                Report("[WARN] Не найден items_full_database.json: " + path);
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                JsonElement items = GetArrayRoot(doc, "items");
                if (items.ValueKind != JsonValueKind.Array)
                {
                    Report("[WARN] items_full_database.json: нет массива items");
                    return;
                }

                foreach (var e in items.EnumerateArray())
                {
                    int? idValue = GetInt(e, "id");
                    if (!idValue.HasValue || idValue.Value <= 0) continue;
                    uint id = (uint)idValue.Value;
                    int? wtype = GetInt(e, "wtype");
                    string category = GetString(e, "category");

                    var info = new BaitInfo
                    {
                        Name = GetString(e, "name_ru", "name"),
                        NameDe = GetString(e, "name_de"),
                        Category = category,
                        TypeId = wtype,
                        IconPath = GetString(e, "icon"),
                        MinWeight = GetDouble(e, "min_bait_weight"),
                        MaxWeight = GetDouble(e, "max_bait_weight"),
                        BaseParams = RawText(e, "base_params"),
                        KlevPropRaw = RawText(e, "klevprop_raw"),
                        SubType = RawText(e, "subtype"),
                        TypeName = RawText(e, "type_name")
                    };

                    if (e.TryGetProperty("fish", out var fishArray) &&
                        fishArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var fish in fishArray.EnumerateArray())
                        {
                            if (fish.ValueKind == JsonValueKind.String)
                            {
                                string? name = fish.GetString();
                                if (!string.IsNullOrWhiteSpace(name)) info.Fish.Add(name);
                            }
                        }
                    }

                    if (e.TryGetProperty("fish_ids", out var fishIds) &&
                        fishIds.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var fishId in fishIds.EnumerateArray())
                        {
                            if (fishId.ValueKind == JsonValueKind.Number &&
                                fishId.TryGetInt32(out int value))
                                info.FishIds.Add(value);
                        }
                    }

                    _items[id] = info;
                    if (IsBaitType(wtype, category))
                        _baits[id] = info;
                }

                Report($"[OK] Предметов: {_items.Count}, наживок/приманок: {_baits.Count}");
            }
            catch (Exception ex)
            {
                Report("[WARN] Ошибка items_full_database.json: " + ex.Message);
            }
        }

        private static bool IsBaitType(int? wtype, string category)
        {
            if (wtype is 10 or 11 or 12 or 37 or 38 or 39 or 40 or 49)
                return true;
            return category is "bait" or "lure" or "fly" or "groundbait" or "dip";
        }

        private void LoadFishFullDatabase(string path)
        {
            if (!File.Exists(path))
            {
                Report("[WARN] Не найден fish_full_database.json: " + path);
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                JsonElement fishArray = GetArrayRoot(doc, "fish");
                if (fishArray.ValueKind != JsonValueKind.Array)
                {
                    Report("[WARN] fish_full_database.json: нет массива fish");
                    return;
                }

                foreach (var e in fishArray.EnumerateArray())
                {
                    int? idValue = GetInt(e, "id");
                    if (!idValue.HasValue) continue;
                    _fishRecordCount++;
                    // -2/-1 — служебные записи «Ледяные рыбы»/«Любой мутант»;
                    // в uint-словарь их не кладём, но в счётчике базы учитываем.
                    if (idValue.Value < 0) continue;
                    uint id = (uint)idValue.Value;
                    string name = GetString(e, "name_ru", "name");

                    var info = new FishInfo
                    {
                        Id = id,
                        Name = name,
                        NameSmall = GetString(e, "name_small"),
                        Latin = GetString(e, "latin"),
                        ModelId = GetString(e, "model_id"),
                        IconPath = GetString(e, "icon"),
                        MinWeight = GetDouble(e, "min_weight_g"),
                        MaxWeight = GetDouble(e, "max_weight_g"),
                        IsMutant = GetBool(e, "is_mutant"),
                        IsAggression = GetBool(e, "is_aggression")
                    };

                    _fish[id] = info;
                    if (!string.IsNullOrWhiteSpace(name))
                        _fishByName[name] = info;
                }

                Report($"[OK] Рыб: {_fishRecordCount} записей ({_fish.Count} ID)");
            }
            catch (Exception ex)
            {
                Report("[WARN] Ошибка fish_full_database.json: " + ex.Message);
            }
        }

        private void LoadLocationMap(string path)
        {
            if (!File.Exists(path))
            {
                Report("[WARN] Не найден location_map.json: " + path);
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                JsonElement locations = GetArrayRoot(doc, "locations");
                if (locations.ValueKind != JsonValueKind.Array)
                {
                    Report("[WARN] location_map.json: нет массива locations");
                    return;
                }

                foreach (var e in locations.EnumerateArray())
                {
                    int? idValue = GetInt(e, "id");
                    if (!idValue.HasValue || idValue.Value <= 0) continue;
                    uint id = (uint)idValue.Value;

                    var info = new LocationInfo
                    {
                        Id = id,
                        Name = GetString(e, "name"),
                        MaxDeepCm = GetInt(e, "max_deep_cm"),
                        FlowForce = GetDouble(e, "flow_force", double.NaN) is double f && !double.IsNaN(f) ? f : null,
                        ImagePath = GetString(e, "image")
                    };

                    if (e.TryGetProperty("fish_ids", out var fishIds) &&
                        fishIds.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var fishId in fishIds.EnumerateArray())
                        {
                            if (fishId.ValueKind == JsonValueKind.Number &&
                                fishId.TryGetInt32(out int value))
                                info.FishIds.Add(value);
                        }
                    }

                    _locations[id] = info;
                }

                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("aliases", out var aliases) &&
                    aliases.ValueKind == JsonValueKind.Object)
                {
                    foreach (var alias in aliases.EnumerateObject())
                    {
                        if (uint.TryParse(alias.Name, out uint from) &&
                            alias.Value.ValueKind == JsonValueKind.Number &&
                            alias.Value.TryGetUInt32(out uint to))
                            _locationAliases[from] = to;
                    }
                }

                Report($"[OK] Локаций: {_locations.Count}" +
                       (_locationAliases.Count > 0 ? $", алиасов: {_locationAliases.Count}" : ""));
            }
            catch (Exception ex)
            {
                Report("[WARN] Ошибка location_map.json: " + ex.Message);
            }
        }

        private void LoadBaitModifiers(string path)
        {
            if (!File.Exists(path))
            {
                Report("[WARN] Не найден bait_modifiers.json: " + path);
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                JsonElement items = GetArrayRoot(doc, "items");
                if (items.ValueKind != JsonValueKind.Array)
                {
                    Report("[WARN] bait_modifiers.json: нет массива items");
                    return;
                }

                foreach (var e in items.EnumerateArray())
                {
                    int? itemId = GetInt(e, "item_id");
                    if (!itemId.HasValue) continue;

                    var modifier = new BaitModifier
                    {
                        ItemId = itemId.Value,
                        Name = GetString(e, "name"),
                        KlevpropRaw = GetString(e, "klevprop_raw")
                    };

                    if (e.TryGetProperty("modifiers", out var entries) &&
                        entries.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in entries.EnumerateArray())
                        {
                            int kind = GetInt(entry, "kind") ?? 0;
                            string meaning = GetString(entry, "meaning");
                            if (string.IsNullOrEmpty(meaning))
                            {
                                meaning = kind switch
                                {
                                    1 => "базовое влияние",
                                    2 => "целевое привлечение",
                                    0 => "редкое специфическое",
                                    _ => ""
                                };
                            }

                            modifier.Modifiers.Add(new ModifierEntry
                            {
                                Kind = kind,
                                FishId = GetInt(entry, "fish_id") ?? 0,
                                FishName = GetString(entry, "fish_name"),
                                Modifier = GetDouble(entry, "modifier"),
                                Meaning = meaning
                            });
                        }
                    }

                    _baitModifiers.Add(modifier);
                }

                int totalEntries = _baitModifiers.Sum(m => m.Modifiers.Count);
                Report($"[OK] Модификаторов клева: {_baitModifiers.Count} предметов, {totalEntries} записей");
            }
            catch (Exception ex)
            {
                Report("[WARN] Ошибка bait_modifiers.json: " + ex.Message);
            }
        }

        private void LoadBaits(string path)
        {
            if (!File.Exists(path))
            {
                Report("[WARN] Не найден файл наживок: " + path);
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

                if (!doc.RootElement.TryGetProperty("baits", out var baits)) return;

                foreach (var prop in baits.EnumerateObject())
                {
                    if (!uint.TryParse(prop.Name, out uint id)) continue;
                    var e = prop.Value;

                    var info = new BaitInfo
                    {
                        Name = e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() ?? "" : (e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""),
                        Category = e.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "",
                        TypeId = e.TryGetProperty("type_id", out var t) && t.ValueKind == JsonValueKind.Number
                            ? t.GetInt32()
                            : null
                    };

                    if (e.TryGetProperty("fish", out var fish) && fish.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var f in fish.EnumerateArray())
                        {
                            string? s = f.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) info.Fish.Add(s);
                        }
                    }

                    _baits[id] = info;
                }

                Report($"[OK] Наживок загружено: {_baits.Count}");
            }
            catch (Exception ex)
            {
                Report("[WARN] Ошибка baits_map.json: " + ex.Message);
            }
        }

                private void LoadFishCatalog(string path)
        {
            if (!File.Exists(path))
            {
                Report("[WARN] Не найден каталог рыб: " + path);
                return;
            }

            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                using var doc = JsonDocument.Parse(json);
                
                // Пробуем формат {"fish": {"52": {"name": "Лещ"}}}
                if (doc.RootElement.TryGetProperty("fish", out var fish))
                {
                    foreach (var prop in fish.EnumerateObject())
                    {
                        if (!uint.TryParse(prop.Name, out uint id)) continue;
                        var e = prop.Value;

                        _fish[id] = new FishInfo
                        {
                            Id = id,
                            Name = e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : 
                                   (e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""),
                            NameSmall = e.TryGetProperty("name_small", out var ns) ? ns.GetString() ?? "" : "",
                            Latin = e.TryGetProperty("latin", out var l) ? l.GetString() ?? "" : ""
                        };
                    }
                }
                else
                {
                    // Пробуем простой формат {"52": "Лещ"}
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (!uint.TryParse(prop.Name, out uint id)) continue;
                        _fish[id] = new FishInfo
                        {
                            Id = id,
                            Name = prop.Value.GetString() ?? ""
                        };
                    }
                }

                _fishRecordCount = _fish.Count;
                Report($"[OK] Рыб в каталоге: {_fishRecordCount}");
            }
            catch (Exception ex)
            {
                Report("[WARN] Ошибка fish_catalog.json: " + ex.Message);
            }
        }
private void LoadNetworkMap(string path)
        {
            if (!File.Exists(path))
            {
                Report("[WARN] Не найден маппинг сетевых ID: " + path);
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                if (!doc.RootElement.TryGetProperty("network_ids", out var map)) return;

                foreach (var prop in map.EnumerateObject())
                {
                    if (!uint.TryParse(prop.Name, out uint id)) continue;
                    var e = prop.Value;

                    _networkFish[id] = new NetworkFishInfo
                    {
                        Name = e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() ?? "" : (e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""),
                        FishLangId = e.TryGetProperty("fishlang_id", out var fl) && fl.ValueKind == JsonValueKind.Number
                            ? fl.GetUInt32()
                            : null,
                        Confidence = e.TryGetProperty("confidence", out var c) ? c.GetString() ?? "" : ""
                    };
                }

                if (doc.RootElement.TryGetProperty("reverse_mapping", out var reverse) &&
                    reverse.ValueKind == JsonValueKind.Object)
                {
                    foreach (var group in reverse.EnumerateObject())
                    {
                        if (!uint.TryParse(group.Name, out uint fishLangId) ||
                            group.Value.ValueKind != JsonValueKind.Array)
                            continue;

                        var list = new List<uint>();
                        foreach (var netId in group.Value.EnumerateArray())
                        {
                            if (netId.ValueKind == JsonValueKind.Number &&
                                netId.TryGetUInt32(out uint id))
                                list.Add(id);
                        }
                        if (list.Count > 0)
                            _reverseNetwork[fishLangId] = list;
                    }
                }

                Report($"[OK] Сетевых ID рыб: {_networkFish.Count}" +
                       (_reverseNetwork.Count > 0 ? $", reverse-групп: {_reverseNetwork.Count}" : ""));
            }
            catch (Exception ex)
            {
                Report("[WARN] Ошибка network_fish_map.json: " + ex.Message);
            }
        }
    }
}
