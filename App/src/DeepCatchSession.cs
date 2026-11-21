using System;
using System.Collections.Generic;
using System.Linq;

namespace FisherLogger
{
    /// <summary>Последний известный контекст игрока для привязки чужих поимок.</summary>
    public sealed class DeepSessionContext
    {
        public uint? LocationId { get; set; }
        public double? CastX { get; set; }
        public double? CastZ { get; set; }
        public EquipmentState? Equipment { get; set; }
    }

    /// <summary>
    /// Связывает события PacketDeepParser с записями CatchData.
    ///
    /// Реалтайм: полноценная поимка (FULL_CATCH) сразу превращается в CatchData,
    /// к ней добавляются последние координаты/глубина/погода. Сводка
    /// (CATCH_SUMMARY), которая приходит через 20–30 секунд, дописывает наживку
    /// в уже созданную запись (событие CatchEnriched).
    ///
    /// Импорт PCAP: те же события подаются в хронологическом порядке, поэтому
    /// контекст на момент поимки восстанавливается корректно.
    ///
    /// Класс потокобезопасен. События вызываются вне блокировки.
    /// </summary>
    public sealed class DeepCatchSession
    {
        private sealed class ActiveCatch
        {
            public CatchData Data = null!;
            public uint Weight;
            public DateTime DetectedAt;
        }

        private sealed class OrphanSummary
        {
            public PacketDeepParser.DeepCatchData Event = null!;
            public DateTime SeenAt;
        }

        private const int ClientContextMaxAgeSeconds = 180;
        // NEXT-SESSION-23K: сводка приходит через ~20–30 с, но наживка может
        // задержаться до ~2 минут (TIMEOUT наживки = 120 с). Окно расширено до
        // 180 с, чтобы сводка после таймаута наживки всё равно обогащала ту же
        // поимку, а не создавала дубль SUMMARY_ONLY.
        private const int SummaryWindowSeconds = 180;
        private const int SummaryWeightTolerance = 2;
        private const int MaxActiveRecords = 500;
        private const int MaxRecentDetections = 500;

        private readonly object _sync = new();
        private readonly List<ActiveCatch> _active = new();
        private readonly List<OrphanSummary> _orphans = new();
        private readonly List<(DateTime At, uint Weight)> _recent = new();
        private readonly HashSet<uint> _seenSummaryUids = new(); // NEXT-112: repeat-summary suppression by event identity

        // NEXT-115: per-rod state (c2s 1120/148: rod at +4, depth at +64) and per-cast snapshots (c2s 1110).
        private sealed class RodState
        {
            public float? DepthCm;
            public DateTime At;
        }

        public sealed class CastSnapshot
        {
            public long CastId { get; init; }
            public int Rod { get; init; }
            public double? X { get; init; }
            public double? Y { get; init; }
            public double? Z { get; init; }
            public DateTime CastAt { get; init; }
            public DateTime? HookAt { get; internal set; }
            public float? DepthAtHook { get; internal set; }
            public uint? ClaimedByUid { get; internal set; }
            public bool Claimed { get; internal set; }
        }

        private readonly Dictionary<int, RodState> _rodState = new();
        private readonly Dictionary<int, CastSnapshot> _castByRod = new();
        // NEXT-ANALYTICS-04: the cast that preceded the current one on each rod (recast-before-summary guard).
        private readonly Dictionary<int, CastSnapshot> _prevCastByRod = new();

        /// <summary>NEXT-115: a catch summary was associated with this cast id (live path: set outcome = caught).</summary>
        public event Action<long>? CastCaught;

        /// <summary>NEXT-ANALYTICS-04: same as CastCaught, with the summary event (timestamp, species, weight).</summary>
        public event Action<long, PacketDeepParser.DeepCatchData>? CastCaughtWithSummary;

        private PacketDeepParser.DeepCatchData? _client;
        private DateTime _clientAt = DateTime.MinValue;
        private uint? _lastLocationId;
        private EquipmentState? _equipment;

        // Последняя известная погода сессии. Не сбрасываем по возрасту:
        // сервер присылает WEATHER редко, а привязывать её к поимкам нужно.
        private float? _currentTemp;
        private float? _currentWind;
        private float? _currentHumidity;

        private int _fullCatchCount;
        private int _summaryOnlyCount;

        public int FullCatchCount { get { lock (_sync) return _fullCatchCount; } }
        public int SummaryOnlyCount { get { lock (_sync) return _summaryOnlyCount; } }

        /// <summary>
        /// Координаты последнего CLIENT_STATE и location_id последней полной
        /// поимки/сводки. Используется для other_players_catches.
        /// </summary>
        public DeepSessionContext GetCurrentContext()
        {
            lock (_sync)
            {
                return new DeepSessionContext
                {
                    LocationId = _lastLocationId,
                    CastX = _client?.CastX,
                    CastZ = _client?.CastZ,
                    Equipment = _equipment
                };
            }
        }

        /// <summary>Диагностические сообщения: [STAT], [FILTER], [SESSION].</summary>
        public event Action<string>? Diagnostics;

        /// <summary>Новая полная поимка. Обработчик должен создать CatchData и вызвать Register().</summary>
        public event Action<PacketDeepParser.DeepCatchData>? FullCatchDetected;

        /// <summary>В уже известную поимку добавились новые данные (обычно наживка из сводки).</summary>
        public event Action<CatchData>? CatchEnriched;

        /// <summary>Сводка без FULL_CATCH: можно сразу создать поимку.</summary>
        public event Action<PacketDeepParser.DeepCatchData>? SummaryOnlyDetected;

        /// <summary>NEXT-SESSION-23F: ранняя поклёвка (cmd=1120 flag=1), вес+вид.</summary>
        public event Action<PacketDeepParser.DeepCatchData>? EarlyBiteDetected;

        public void Reset()
        {
            lock (_sync)
            {
                _active.Clear();
                _orphans.Clear();
                _recent.Clear();
                _client = null;
                _clientAt = DateTime.MinValue;
                _lastLocationId = null;
                _equipment = null;
                _currentTemp = null;
                _currentWind = null;
                _currentHumidity = null;
                _fullCatchCount = 0;
                _summaryOnlyCount = 0;
                _rodState.Clear();   // NEXT-115
                _castByRod.Clear();  // NEXT-115
                _prevCastByRod.Clear(); // NEXT-ANALYTICS-04
            }
        }

        // ------------------------------------------------------------------
        // NEXT-115: per-cast snapshot and per-rod depth
        // ------------------------------------------------------------------

        /// <summary>c2s 1110: new cast on a rod. Replaces the previous (finished) cast of the same rod.</summary>
        public void RegisterCast(int rod, long castId, double? x, double? y, double? z, DateTime at)
        {
            lock (_sync)
            {
                if (_castByRod.TryGetValue(rod, out var previous)) _prevCastByRod[rod] = previous; // NEXT-ANALYTICS-04
                _castByRod[rod] = new CastSnapshot { CastId = castId, Rod = rod, X = x, Y = y, Z = z, CastAt = at };
            }
        }

        /// <summary>c2s 1125: hook on a rod. Freezes the depth of THIS rod measured after the cast.</summary>
        public void RegisterHook(int rod, DateTime at)
        {
            lock (_sync)
            {
                if (!_castByRod.TryGetValue(rod, out var cast) || cast.Claimed || at < cast.CastAt) return;
                cast.HookAt = at;
                if (_rodState.TryGetValue(rod, out var st) && st.DepthCm.HasValue && st.At >= cast.CastAt && st.At <= at)
                    cast.DepthAtHook = st.DepthCm;
            }
        }

        /// <summary>Snapshot of the current cast of a rod (tests/diagnostics).</summary>
        public CastSnapshot? GetCurrentCast(int rod)
        {
            lock (_sync) return _castByRod.TryGetValue(rod, out var c) ? c : null;
        }

        /// <summary>Latest depth of one rod (tests/diagnostics).</summary>
        public float? GetRodDepth(int rod)
        {
            lock (_sync) return _rodState.TryGetValue(rod, out var s) ? s.DepthCm : null;
        }

        /// <summary>
        /// Resolves the cast of the rod named in the summary. Only the current cast of THAT rod is a candidate;
        /// it must start before the summary and must not be claimed by another summary uid. Never uses another rod.
        /// Caller holds _sync. Returns null (and logs why) when the association cannot be established.
        /// </summary>
        private CastSnapshot? ResolveCastForSummary(PacketDeepParser.DeepCatchData summary, out float? depth)
        {
            depth = null;
            if (!summary.RodIndex.HasValue)
            {
                Report("[CAST] UNRESOLVED: сводка " + summary.Weight + "гр без номера удочки — координаты/глубина не присваиваются");
                return null;
            }
            int rod = summary.RodIndex.Value;
            var at = Timestamp(summary);
            if (!_castByRod.TryGetValue(rod, out var cast))
            {
                Report("[CAST] UNRESOLVED: нет заброса для удочки " + rod + " (сводка " + summary.Weight + "гр)");
                return null;
            }
            if (cast.CastAt > at)
            {
                Report("[CAST] AMBIGUOUS: заброс #" + cast.CastId + " удочки " + rod + " позже сводки " + summary.Weight + "гр — не привязано");
                return null;
            }
            if (cast.Claimed && !(summary.CatchUid.HasValue && cast.ClaimedByUid == summary.CatchUid))
            {
                Report("[CAST] AMBIGUOUS: заброс #" + cast.CastId + " удочки " + rod + " уже привязан к uid=" + cast.ClaimedByUid +
                       " — сводка " + summary.Weight + "гр uid=" + summary.CatchUid + " не привязана");
                return null;
            }

            // NEXT-ANALYTICS-04: the player may recast the rod before the summary of the previous fish arrives.
            // If the current cast has no hook but the previous cast of this rod was hooked recently and never
            // claimed, the summary cannot be attributed to either cast with certainty → no association.
            if (!cast.HookAt.HasValue && _prevCastByRod.TryGetValue(rod, out var prev) && prev.HookAt.HasValue &&
                !prev.Claimed && (at - prev.HookAt.Value).TotalSeconds <= SummaryWindowSeconds)
            {
                Report("[CAST] AMBIGUOUS: сводка " + summary.Weight + "гр удочки " + rod + " — текущий заброс #" + cast.CastId +
                       " без подсечки, предыдущий #" + prev.CastId + " с подсечкой не привязан — не привязано");
                return null;
            }

            depth = cast.DepthAtHook;
            if (!depth.HasValue && _rodState.TryGetValue(rod, out var st) && st.DepthCm.HasValue &&
                st.At >= cast.CastAt && st.At <= (cast.HookAt ?? at))
                depth = st.DepthCm;
            return cast;
        }

        /// <summary>Claims the cast for this summary. Caller holds _sync. Returns the cast id to report as caught.</summary>
        private long? ClaimCast(CastSnapshot cast, PacketDeepParser.DeepCatchData summary, float? depth)
        {
            bool first = !cast.Claimed;
            cast.Claimed = true;
            cast.ClaimedByUid = summary.CatchUid;
            Report("[CAST] сводка " + summary.Weight + "гр uid=" + summary.CatchUid + " → заброс #" + cast.CastId +
                   " удочка " + cast.Rod + " (" + Fmt(cast.X) + "; " + Fmt(cast.Y) + "; " + Fmt(cast.Z) + ") глубина=" +
                   (depth.HasValue ? depth.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "см" : "нет"));
            return first && cast.CastId > 0 ? cast.CastId : null;
        }

        private static string Fmt(double? v) => v.HasValue ? v.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "?";

        /// <summary>Обновляет последнюю погоду сессии (вызывается на WEATHER).</summary>
        public CatchData? FindActiveByWeight(uint weight, int tolerance = 2)
        {
            lock (_sync)
            {
                return _active.LastOrDefault(a =>
                    a.Data.status != "skipped" &&
                    Math.Abs((long)a.Weight - weight) <= tolerance)?.Data;
            }
        }

        /// <summary>NEXT-112: weight + bounded time window + free-record predicate (one-to-one cross-source matching only).</summary>
        public CatchData? FindActiveByWeight(uint weight, int tolerance, DateTime at, int maxAgeSeconds, Func<CatchData, bool>? isFree = null)
        {
            lock (_sync)
            {
                return _active.LastOrDefault(a =>
                    a.Data.status != "skipped" &&
                    Math.Abs((long)a.Weight - weight) <= tolerance &&
                    (at - a.DetectedAt).TotalSeconds >= -5 &&
                    (at - a.DetectedAt).TotalSeconds <= maxAgeSeconds &&
                    (isFree == null || isFree(a.Data)))?.Data;
            }
        }

        // ------------------------------------------------------------------
        // NEXT-04S (D1): связывание FULL_CATCH со сводкой, пришедшей раньше
        // ------------------------------------------------------------------

        /// <summary>
        /// Переносит в FULL-событие данные единственной подходящей сводки-сироты.
        /// При 0 или ≥2 кандидатах ничего не меняет (неоднозначность).
        /// </summary>
        private void MergeSingleOrphanSummary(PacketDeepParser.DeepCatchData ev)
        {
            if (!ev.Weight.HasValue) return;

            var candidates = _orphans
                .Where(o => o.Event.Weight.HasValue)
                .Where(o => Math.Abs((long)o.Event.Weight.GetValueOrDefault() - ev.Weight.GetValueOrDefault()) <= SummaryWeightTolerance)
                .Where(o => FishLangCompatible(o.Event.FishLangId, ev.FishLangId))
                .Where(o => IsFullAfterSummary(ev, o.SeenAt))
                .ToList();

            if (candidates.Count != 1) return;

            var orphan = candidates[0];
            ev.BaitId ??= orphan.Event.BaitId;
            ev.BaitName ??= orphan.Event.BaitName;
            ev.BaitCategory ??= orphan.Event.BaitCategory;
            ev.Quality ??= orphan.Event.Quality;
            ev.FishLangId ??= orphan.Event.FishLangId;
            ev.CastId ??= orphan.Event.CastId; // NEXT-ANALYTICS-04

            _orphans.Remove(orphan);
            Report("[DUP] FULL дополнен сводкой (вес " + ev.Weight.Value + "г)");
        }

        /// <summary>Единственная активная запись, созданная из сводки и ещё не обогащённая FULL.</summary>
        private CatchData? FindSingleSummaryOnlyRecord(PacketDeepParser.DeepCatchData ev)
        {
            if (!ev.Weight.HasValue) return null;

            var candidates = _active
                .Select(a => a.Data)
                .Where(d => d.source.StartsWith("deep-summary", StringComparison.OrdinalIgnoreCase))
                .Where(d => !d.source.Contains("+full", StringComparison.OrdinalIgnoreCase))
                .Where(d => Math.Abs((long)d.weight - ev.Weight.Value) <= SummaryWeightTolerance)
                .Where(d => FishLangCompatible(d.fish_lang_id, ev.FishLangId))
                .Where(d => IsFullAfterSummary(ev, d.detected_at))
                .ToList();

            return candidates.Count == 1 ? candidates[0] : null;
        }

        /// <summary>Дополняет существующую запись данными FULL, не затирая заполненное.</summary>
        private static bool TryMergeFullIntoData(PacketDeepParser.DeepCatchData ev, CatchData data)
        {
            if (data == null || !ev.Weight.HasValue) return false;
            if (Math.Abs((long)data.weight - ev.Weight.Value) > SummaryWeightTolerance) return false;

            data.fish_lang_id = ev.FishLangId ?? data.fish_lang_id;
            data.quality = ev.Quality ?? data.quality;
            data.quality_float = ev.QualityFloat ?? data.quality_float;
            data.bait_id = ev.BaitId ?? data.bait_id;
            if (!string.IsNullOrWhiteSpace(ev.BaitName)) data.bait_name = ev.BaitName!;
            if (!string.IsNullOrWhiteSpace(ev.BaitCategory)) data.bait_category = ev.BaitCategory!;
            data.bait_uid = ev.BaitUid ?? data.bait_uid;

            int xp = ev.Experience?.CatchExperience ?? 0;
            if (xp > 0) data.exp = xp;
            if (!string.IsNullOrEmpty(ev.PreferenceString)) data.experience_formula = ev.PreferenceString!;
            if (ev.RangeValues.Count > 0)
            {
                data.range_values_json = "[" + string.Join(",", ev.RangeValues.Select(v =>
                    v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]";
            }

            data.depth_cm ??= ev.DepthCm;
            data.cast_x ??= ev.CastX;
            data.cast_y ??= ev.CastY;
            data.cast_z ??= ev.CastZ;
            data.air_temperature_c ??= ev.AirTemperatureC;
            data.wind_ms ??= ev.WindMs;
            data.humidity ??= ev.Humidity;

            data.source = "deep-summary+full";
            data.autodetect_source = "full";
            data.status = "confirmed";
            return true;
        }

        private static bool FishLangCompatible(uint? summaryId, uint? fullId)
        {
            if (!summaryId.HasValue || !fullId.HasValue) return true;
            return summaryId.Value == fullId.Value;
        }

        private static bool IsFullAfterSummary(PacketDeepParser.DeepCatchData ev, DateTime summaryAt)
        {
            var delta = Timestamp(ev) - summaryAt;
            return delta >= TimeSpan.FromSeconds(-5) && delta <= TimeSpan.FromSeconds(SummaryWindowSeconds);
        }

        public void UpdateEquipment(string type, uint itemId)
        {
            lock (_sync)
            {
                _equipment ??= new EquipmentState { Source = "Player.log" };
                switch (type.ToLowerInvariant())
                {
                    case "rod": _equipment.RodId = itemId; break;
                    case "reel": _equipment.ReelId = itemId; break;
                    case "line": _equipment.LineId = itemId; break;
                    case "hook": _equipment.HookId = itemId; break;
                    case "float": _equipment.FloatId = itemId; break;
                    case "feeder": _equipment.FeederId = itemId; break;
                }
                _equipment.Timestamp = DateTime.Now;
                _equipment.Source = "Player.log";
            }
        }

        public void UpdateWeather(float temp, float wind, float humidity)
        {
            lock (_sync)
            {
                _currentTemp = temp;
                _currentWind = wind;
                _currentHumidity = humidity;
            }

            Report($"[SESSION] Погода обновлена: {temp:F1}C, {wind:F1}м/с, {humidity:F1}%");
        }

        private void Report(string message)
        {
            Diagnostics?.Invoke(message);
        }

        /// <summary>Обрабатывает одно событие парсера (потокобезопасно).</summary>
        public void Process(PacketDeepParser.DeepCatchData ev)
        {
            if (ev == null) return;

            // Погоду обновляем вне lock: UpdateWeather сам логирует событие.
            if (ev.MessageType == "WEATHER")
            {
                if (ev.AirTemperatureC.HasValue && ev.WindMs.HasValue && ev.Humidity.HasValue)
                {
                    UpdateWeather(ev.AirTemperatureC.Value, ev.WindMs.Value, ev.Humidity.Value);
                }
                return;
            }

            PacketDeepParser.DeepCatchData? detected = null;
            PacketDeepParser.DeepCatchData? summaryOnly = null;
            PacketDeepParser.DeepCatchData? earlyBite = null;
            CatchData? enriched = null;
            string? statMessage = null;
            long? caughtCast = null; // NEXT-115

            lock (_sync)
            {
                switch (ev.MessageType)
                {
                    case "CLIENT_STATE":
                        MergeClientState(ev);
                        _clientAt = Timestamp(ev);
                        break;

                    case "ITEM_ACTION":
                        if (ev.Equipment != null)
                            _equipment = ev.Equipment;
                        break;

                    case "FULL_CATCH":
                        if (ev.LocationId.HasValue) _lastLocationId = ev.LocationId;
                        if (!ev.Weight.HasValue) break;
                        _fullCatchCount++;
                        statMessage = "[STAT] FULL_CATCH: " + _fullCatchCount;
                        ApplyContextToEvent(ev, Timestamp(ev));
                        // Сводки, пришедшие ДО полной поимки, к ней не относятся:
                        // TCP сохраняет порядок, а сводка идёт через 20–30 секунд
                        // ПОСЛЕ поимки. Такие «сироты» станут отдельными записями.
                        // Дедупликация базового детектора работает по «настенным»
                        // часам: при реплее PCAP метка события может быть старой.
                        AddRecent(DateTime.Now, ev.Weight.Value);

                        // NEXT-04S (D1): сводка могла прийти РАНЬШЕ полной поимки.
                        // 1) подтягиваем в событие данные единственной сводки-сироты
                        //    (нужно для импорта PCAP, где Form1 отсутствует);
                        MergeSingleOrphanSummary(ev);

                        // 2) если в live уже создана запись deep-summary — дополняем её,
                        //    сохраняя record_id и наживку, вместо второй записи.
                        var summaryRecord = FindSingleSummaryOnlyRecord(ev);
                        if (summaryRecord != null && TryMergeFullIntoData(ev, summaryRecord))
                        {
                            statMessage = "[STAT] FULL_CATCH merged into deep-summary";
                            enriched = summaryRecord;   // уйдёт в CatchEnriched
                        }
                        else
                        {
                            detected = ev;
                        }
                        break;

                    case "EARLY_BITE_1120":
                        // NEXT-SESSION-23F: ранняя поклёвка — не поимка, только уведомление.
                        earlyBite = ev;
                        break;

                    case "CATCH_SUMMARY":
                        if (ev.LocationId.HasValue) _lastLocationId = ev.LocationId;
                        enriched = AttachSummary(ev, ref caughtCast);
                        if (enriched == null)
                        {
                            _summaryOnlyCount++;
                            summaryOnly = ev;
                            statMessage = "[STAT] SUMMARY_ONLY: " + _summaryOnlyCount;
                        }
                        break;
                }
            }

            if (statMessage != null) Report(statMessage);

            // Обработчики вызываются вне lock: они могут обращаться к UI и к этому же классу.
            if (detected != null) FullCatchDetected?.Invoke(detected);
            if (enriched != null) CatchEnriched?.Invoke(enriched);
            if (summaryOnly != null) SummaryOnlyDetected?.Invoke(summaryOnly);
            if (earlyBite != null) EarlyBiteDetected?.Invoke(earlyBite);
            if (caughtCast.HasValue)
            {
                CastCaught?.Invoke(caughtCast.Value); // NEXT-115
                CastCaughtWithSummary?.Invoke(caughtCast.Value, ev); // NEXT-ANALYTICS-04
            }
        }

        /// <summary>Регистрирует созданную поимку для последующего обогащения из сводки.</summary>
        public void Register(CatchData data)
        {
            if (data == null) return;

            lock (_sync)
            {
                _active.RemoveAll(a => ReferenceEquals(a.Data, data));
                _active.Add(new ActiveCatch
                {
                    Data = data,
                    Weight = data.weight > 0 ? (uint)data.weight : 0u,
                    DetectedAt = data.detected_at != default ? data.detected_at : DateTime.Now
                });
                if (_active.Count > MaxActiveRecords)
                    _active.RemoveRange(0, _active.Count - MaxActiveRecords);
            }
        }

        /// <summary>
        /// Дописывает в поимку последние известные координаты/глубину/погоду.
        /// Используется старым (базовым) путём детекции, если глубокий парсер
        /// не увидел кадр целиком.
        /// </summary>
        public void ApplyContext(CatchData data)
        {
            if (data == null) return;

            lock (_sync)
            {
                var now = DateTime.Now;
                ApplyClientContextToData(data, now);
                ApplyWeatherContextToData(data);
            }
        }

        /// <summary>
        /// Был ли только что разобран FULL_CATCH с таким весом.
        /// Нужен, чтобы старый детектор не создал вторую запись о той же поимке.
        /// </summary>
        public bool WasJustDetected(uint weight, int tolerance, TimeSpan window)
        {
            var now = DateTime.Now;

            lock (_sync)
            {
                _recent.RemoveAll(r => now - r.At > TimeSpan.FromMinutes(2));
                return _recent.Any(r =>
                    Math.Abs((long)r.Weight - weight) <= tolerance &&
                    now - r.At <= window);
            }
        }

        /// <summary>
        /// Реалтайм: сводки, которые «повисли» без полной поимки (например,
        /// приложение запустилось позже), через некоторое время сами становятся
        /// записями, чтобы не терять данные.
        /// </summary>
        public List<CatchData> DrainOldOrphans(TimeSpan minAge)
        {
            var result = new List<CatchData>();
            var now = DateTime.Now;

            lock (_sync)
            {
                var ready = _orphans.Where(o => now - o.SeenAt >= minAge).ToList();
                foreach (var orphan in ready)
                {
                    _orphans.Remove(orphan);
                    if (!orphan.Event.Weight.HasValue) continue;

                    // Сводка идёт ПОСЛЕ своей поимки. Подавляем «сироту»,
                    // только если поимка того же веса уже была раньше
                    // (полный кадр разбился на сегменты и его поймал
                    // базовый детектор). Более поздняя поимка — другая рыба.
                    bool alreadyHasCatch = _active.Any(a =>
                    {
                        if (a.Weight != orphan.Event.Weight.Value) return false;
                        double age = (orphan.SeenAt - a.DetectedAt).TotalSeconds;
                        return age >= -5 && age <= SummaryWindowSeconds;
                    });
                    if (alreadyHasCatch) continue;

                    var data = ToCatchData(orphan.Event);
                    data.autoDetected = !string.IsNullOrEmpty(data.fish_name);
                    result.Add(data);
                }
            }

            return result;
        }

        /// <summary>
        /// Для импорта PCAP: каждая сводка, к которой не нашлась полная поимка,
        /// становится самостоятельной записью. В импорте события идут строго
        /// по порядку, поэтому «лишняя» сводка — это реальная потерянная поимка,
        /// даже если такая же по весу рыба была поймана рядом по времени.
        /// </summary>
        public List<CatchData> TakeOrphanCatches()
        {
            var result = new List<CatchData>();
            List<string> warnings;

            lock (_sync)
            {
                // NEXT-04T (D2): только диагностика подозрительных пар, без слияния.
                warnings = FindSuspiciousOrphans(_orphans);

                foreach (var orphan in _orphans)
                {
                    if (!orphan.Event.Weight.HasValue) continue;

                    // Защита от повторной сводки той же поимки: поимка того же
                    // веса уже была РАНЬШЕ этой сводки (в пределах окна).
                    bool alreadyHasCatch = _active.Any(a =>
                    {
                        if (a.Weight != orphan.Event.Weight.Value) return false;
                        double age = (orphan.SeenAt - a.DetectedAt).TotalSeconds;
                        return age >= -5 && age <= SummaryWindowSeconds;
                    });
                    if (alreadyHasCatch) continue;

                    var data = ToCatchData(orphan.Event);
                    data.status = "confirmed";
                    data.source = "pcap";
                    data.autoDetected = true;
                    result.Add(data);
                }

                _orphans.Clear();
            }

            foreach (var warning in warnings) Report(warning);

            return result;
        }

        /// <summary>
        /// NEXT-04T: ищет сирот с совпадением fishLang + вес + bait.
        /// Совпадение НЕ считается доказательством дубля и ничего не меняет —
        /// это могут быть две настоящие рыбы. Временное окно не применяется.
        /// </summary>
        private static List<string> FindSuspiciousOrphans(IEnumerable<OrphanSummary> orphans)
        {
            var warnings = new List<string>();

            var groups = orphans
                .Where(o => o.Event.Weight.HasValue && o.Event.FishLangId.HasValue)
                .GroupBy(o => new
                {
                    Lang = o.Event.FishLangId.GetValueOrDefault(),
                    Weight = o.Event.Weight.GetValueOrDefault(),
                    Bait = o.Event.BaitId
                })
                .Where(g => g.Count() > 1);

            foreach (var group in groups)
            {
                string times = string.Join(", ", group.Select(o => o.SeenAt.ToString("HH:mm:ss")));
                string bait = group.Key.Bait.HasValue
                    ? group.Key.Bait.Value.ToString()
                    : "—";
                warnings.Add(
                    "[DUP?] orphan SUMMARY: fishLang=" + group.Key.Lang +
                    ", вес=" + group.Key.Weight + "г, bait=" + bait +
                    ", совпадений=" + group.Count() + " (" + times + ")" +
                    " — записи сохранены, объединения нет");
            }

            return warnings;
        }

        // ------------------------------------------------------------------
        // Создание CatchData
        // ------------------------------------------------------------------

        public static CatchData ToCatchData(PacketDeepParser.DeepCatchData ev)
        {
            DateTime ts = ev.Timestamp == default ? DateTime.Now : ev.Timestamp;
            int weight = ev.Weight.HasValue
                ? (int)Math.Min(ev.Weight.Value, (uint)int.MaxValue)
                : 0;

            var data = new CatchData
            {
                timestamp = ts.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                detected_at = ts,
                weight = weight,
                exp = ev.Experience?.CatchExperience ?? 0,
                offset_32 = ev.FishId.HasValue && ev.FishId.Value <= int.MaxValue
                    ? (int)ev.FishId.Value
                    : null,
                fish_name = NormalizeFishName(ev.FishName),
                grade = CatchRules.DetermineGrade(weight, ev.FishLangId, NormalizeFishName(ev.FishName)),
                fish_lang_id = ev.FishLangId,
                bait_id = ev.BaitId,
                bait_name = ev.BaitName ?? "",
                bait_category = ev.BaitCategory ?? "",
                depth_cm = ev.DepthCm,
                wind_kmh = ev.WindKmh,
                cast_x = ev.CastX,
                cast_y = ev.CastY,
                cast_z = ev.CastZ,
                air_temperature_c = ev.AirTemperatureC,
                wind_ms = ev.WindMs,
                humidity = ev.Humidity,
                quality = ev.Quality,
                quality_float = ev.QualityFloat,
                baseid = ev.BaseId, // NEXT-95: persist SUMMARY blob+28 baseid (was dropped for packet-only catches)
                SummaryUid = ev.CatchUid, // NEXT-112: summary event identity
                bait_uid = ev.BaitUid,
                range_values_json = ev.RangeValues.Count == 0
                    ? ""
                    : "[" + string.Join(",", ev.RangeValues.Select(v =>
                        v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]",
                location_id = ev.LocationId,
                rod_id = ev.Equipment?.RodId,
                reel_id = ev.Equipment?.ReelId,
                line_id = ev.Equipment?.LineId,
                hook_id = ev.Equipment?.HookId,
                float_id = ev.Equipment?.FloatId,
                feeder_id = ev.Equipment?.FeederId,
                equipment_source = ev.Equipment?.Source ?? "",
                cast_id = ev.CastId, // NEXT-ANALYTICS-04: only a proven summary→cast association
                experience_formula = !string.IsNullOrEmpty(ev.PreferenceString)
                    ? ev.PreferenceString!
                    : (ev.Experience?.Describe() ?? ""),
                source = "deep",
                autodetect_source = ev.FishId.HasValue
                    ? "full"
                    : (ev.FishLangId.HasValue ? "fish_lang_id" : "none")
            };

            return data;
        }

        /// <summary>Отбрасывает технические заглушки парсера ("?", "Fish_123").</summary>
        public static string NormalizeFishName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string text = name.Trim();
            if (text == "?" || text.StartsWith("Fish_", StringComparison.OrdinalIgnoreCase)) return "";
            return text;
        }

        // ------------------------------------------------------------------
        // Контекст
        // ------------------------------------------------------------------

        private static DateTime Timestamp(PacketDeepParser.DeepCatchData ev)
            => ev.Timestamp == default ? DateTime.Now : ev.Timestamp;

        private static bool IsFresh(DateTime at, DateTime now, int maxAgeSeconds)
        {
            double age = (now - at).TotalSeconds;
            return age >= -2 && age <= maxAgeSeconds;
        }

        private void MergeClientState(PacketDeepParser.DeepCatchData ev)
        {
            _client ??= new PacketDeepParser.DeepCatchData
            {
                MessageType = "CLIENT_STATE",
                Timestamp = ev.Timestamp
            };

            _client.Timestamp = ev.Timestamp;

            if (ev.RodIndex.HasValue)
            {
                // NEXT-115: rod-specific frame — float position/depth belong to THIS rod only and must not
                // overwrite the shared state (another rod's catch would inherit them).
                if (ev.DepthCm.HasValue)
                    _rodState[ev.RodIndex.Value] = new RodState { DepthCm = ev.DepthCm, At = Timestamp(ev) };
            }
            else
            {
                if (ev.CastX.HasValue && ev.CastY.HasValue && ev.CastZ.HasValue)
                {
                    _client.CastX = ev.CastX;
                    _client.CastY = ev.CastY;
                    _client.CastZ = ev.CastZ;
                }

                if (ev.DepthCm.HasValue) _client.DepthCm = ev.DepthCm;
            }
            if (ev.WaterLevelCm.HasValue) _client.WaterLevelCm = ev.WaterLevelCm;
            if (ev.WindKmh.HasValue) _client.WindKmh = ev.WindKmh;

            if (ev.PlayerX.HasValue) _client.PlayerX = ev.PlayerX;
            if (ev.PlayerY.HasValue) _client.PlayerY = ev.PlayerY;
            if (ev.PlayerZ.HasValue) _client.PlayerZ = ev.PlayerZ;

            if (ev.GridX.HasValue) _client.GridX = ev.GridX;
            if (ev.GridY.HasValue) _client.GridY = ev.GridY;
            if (ev.GridZ.HasValue) _client.GridZ = ev.GridZ;
        }

        private void ApplyContextToEvent(PacketDeepParser.DeepCatchData ev, DateTime at, bool rodSpecific = true)
        {
            if (_client != null && IsFresh(_clientAt, at, ClientContextMaxAgeSeconds))
            {
                // NEXT-115: rodSpecific=false — the event names its rod; shared cast/depth would be another rod's.
                if (rodSpecific)
                {
                    ev.DepthCm ??= _client.DepthCm;
                    ev.CastX ??= _client.CastX;
                    ev.CastY ??= _client.CastY;
                    ev.CastZ ??= _client.CastZ;
                }
                ev.WaterLevelCm ??= _client.WaterLevelCm;
                ev.WindKmh ??= _client.WindKmh;
                ev.PlayerX ??= _client.PlayerX;
                ev.PlayerY ??= _client.PlayerY;
                ev.PlayerZ ??= _client.PlayerZ;
                ev.GridX ??= _client.GridX;
                ev.GridY ??= _client.GridY;
                ev.GridZ ??= _client.GridZ;
            }

            // Погода сессии не имеет срока давности: WEATHER приходит редко,
            // а последняя известная погода всё равно полезнее NULL.
            ev.AirTemperatureC ??= _currentTemp;
            ev.WindMs ??= _currentWind;
            ev.Humidity ??= _currentHumidity;
            ev.Equipment ??= _equipment;
        }

        private static void ApplyEquipmentToData(CatchData data, EquipmentState? equipment)
        {
            if (equipment == null) return;
            data.rod_id ??= equipment.RodId;
            data.reel_id ??= equipment.ReelId;
            data.line_id ??= equipment.LineId;
            data.hook_id ??= equipment.HookId;
            data.float_id ??= equipment.FloatId;
            data.feeder_id ??= equipment.FeederId;
            if (string.IsNullOrEmpty(data.equipment_source))
                data.equipment_source = equipment.Source;
        }

        private void ApplyClientContextToData(CatchData data, DateTime at)
        {
            ApplyEquipmentToData(data, _equipment);
            if (_client == null || !IsFresh(_clientAt, at, ClientContextMaxAgeSeconds)) return;

            data.depth_cm ??= _client.DepthCm;
            data.wind_kmh ??= _client.WindKmh;
            data.cast_x ??= _client.CastX;
            data.cast_y ??= _client.CastY;
            data.cast_z ??= _client.CastZ;
        }

        private void ApplyWeatherContextToData(CatchData data)
        {
            data.air_temperature_c ??= _currentTemp;
            data.wind_ms ??= _currentWind;
            data.humidity ??= _currentHumidity;
        }

        // ------------------------------------------------------------------
        // Сводка (наживка)
        // ------------------------------------------------------------------

        private CatchData? AttachSummary(PacketDeepParser.DeepCatchData ev, ref long? caughtCast)
        {
            if (!ev.Weight.HasValue) return null;

            // NEXT-112: repeat summary (same uid) is not a new catch.
            if (ev.CatchUid.HasValue && !_seenSummaryUids.Add(ev.CatchUid.Value))
            {
                Report("[DEDUP] повтор CATCH_SUMMARY uid=" + ev.CatchUid.Value + " — пропущен");
                return null;
            }

            var target = _active
                .Where(a => a.Data.status != "skipped")
                .Where(a => Math.Abs((long)a.Weight - ev.Weight.Value) <= SummaryWeightTolerance)
                .Where(a => IsSummaryNear(a.DetectedAt, Timestamp(ev)))
                .Where(a => string.IsNullOrEmpty(a.Data.bait_name) ||
                            a.Data.bait_name == "(не определена)" ||
                            a.Data.bait_name == "...")
                .Where(a => !a.Data.SummaryUid.HasValue || !ev.CatchUid.HasValue || a.Data.SummaryUid == ev.CatchUid)
                .OrderByDescending(a => a.DetectedAt)
                .FirstOrDefault();

            // NEXT-115: cast of the SAME rod (both event orders). No fallback to another rod.
            var cast = ResolveCastForSummary(ev, out float? rodDepth);

            if (target == null)
            {
                var seen = Timestamp(ev);
                if (cast != null)
                {
                    ev.CastX = cast.X;
                    ev.CastY = cast.Y;
                    ev.CastZ = cast.Z;
                    ev.DepthCm = rodDepth;
                    caughtCast = ClaimCast(cast, ev, rodDepth);
                    ev.CastId = cast.CastId > 0 ? cast.CastId : null; // NEXT-ANALYTICS-04
                }
                // Для сводки без полной поимки сохраняем ещё и контекст:
                // иначе такая запись останется без глубины и координат.
                // NEXT-115: a summary takes cast/depth only from its own rod's cast; the shared (any-rod)
                // cast/depth is never used for it — unknown rod means no coordinates, not another rod's.
                ApplyContextToEvent(ev, seen, rodSpecific: false);
                _orphans.Add(new OrphanSummary { Event = ev, SeenAt = seen });

                // Ограничиваем только количеством: при импорте длинного PCAP
                // сводка может ждать своей очереди минутами.
                if (_orphans.Count > MaxActiveRecords)
                    _orphans.RemoveRange(0, _orphans.Count - MaxActiveRecords);
                return null;
            }

            ApplySummaryToData(target.Data, ev);
            if (cast != null)
            {
                // Rod-correct values replace any shared-context values the record may carry.
                target.Data.cast_x = cast.X;
                target.Data.cast_y = cast.Y;
                target.Data.cast_z = cast.Z;
                if (rodDepth.HasValue) target.Data.depth_cm = rodDepth;
                caughtCast = ClaimCast(cast, ev, rodDepth);
                if (cast.CastId > 0) target.Data.cast_id = cast.CastId; // NEXT-ANALYTICS-04
            }
            return target.Data;
        }

        private static bool IsSummaryNear(DateTime catchAt, DateTime summaryAt)
        {
            return summaryAt >= catchAt.AddSeconds(-5) &&
                   summaryAt <= catchAt.AddSeconds(SummaryWindowSeconds);
        }

        private void ApplySummaryToData(CatchData data, PacketDeepParser.DeepCatchData summary)
        {
            if (summary.CatchUid.HasValue) data.SummaryUid = summary.CatchUid; // NEXT-112
            if (summary.BaitId.HasValue) data.bait_id = summary.BaitId;
            if (summary.BaseId.HasValue) data.baseid = summary.BaseId; // NEXT-95
            if (!string.IsNullOrEmpty(summary.BaitName)) data.bait_name = summary.BaitName!;
            if (!string.IsNullOrEmpty(summary.BaitCategory)) data.bait_category = summary.BaitCategory!;
            if (summary.FishLangId.HasValue) data.fish_lang_id = summary.FishLangId;
            if (summary.Quality.HasValue) data.quality = summary.Quality;
            if (summary.LocationId.HasValue) data.location_id = summary.LocationId;

            if (string.IsNullOrEmpty(data.fish_name))
                data.fish_name = NormalizeFishName(summary.FishName);

            // Если у FULL_CATCH погоды не было, сводка может её донести.
            data.air_temperature_c ??= _currentTemp;
            data.wind_ms ??= _currentWind;
            data.humidity ??= _currentHumidity;

            // Сводка — достоверный источник названия: если рыба была неизвестна,
            // теперь её можно сразу отдать в очередь, не дожидаясь чата.
            if (!string.IsNullOrEmpty(data.fish_name))
                data.autoDetected = true;
        }

        private void AddRecent(DateTime at, uint weight)
        {
            _recent.Add((at, weight));
            if (_recent.Count > MaxRecentDetections)
                _recent.RemoveRange(0, _recent.Count - MaxRecentDetections);
        }
    }
}
