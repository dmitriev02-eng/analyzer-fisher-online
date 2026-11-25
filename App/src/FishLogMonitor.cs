using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace FisherLogger
{
    /// <summary>
    /// Хвостовое чтение Unity Player.log.
    /// Кроме загрузки моделей рыб умеет разбирать локальный чат поимок
    /// других игроков: "[КЛАН] Ник Рыба 6 кг 778 гр" / "Ник Рыба 900 гр".
    /// </summary>
    public class FishLogMonitor
    {
        private readonly string logPath;
        private long lastPosition = 0;
        private System.Threading.Timer? pollTimer;

        private readonly Dictionary<string, string> fishNameMap = new Dictionary<string, string>
        {
            { "37_karasSilver", "Карась" },
            { "26_gustera", "Густера" },
            // Пополняется вручную/из fish_full_database.json по мере необходимости.
        };

        // [КЛАН] Ник Рыба 6 кг 778 гр  ИЛИ  Ник Рыба 900 гр
        private static readonly Regex OtherPlayerCatchRegex = new Regex(
            @"^\s*(?:\[(?<clan>[^\]]{1,40})\]\s*)?(?<player>\S{1,40})\s+(?<fish>\S.*?)\s+(?:(?<kg>\d{1,4})\s*(?:кг|kg)\s*)?(?<grams>\d{1,6})\s*(?:гр|г|g)\.?\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex LevelRegex = new Regex(
            @"Start load Level TFBase_(\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex SpawnPointRegex = new Regex(
            @"getSpawnPoint locid=(\d+)(?:\s+pointid=(\d+))?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex UseItemRegex = new Regex(
            @"(?:Using item|UseItem.*?id=)\s*(\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex EquipRegex = new Regex(
            @"Equipped\s+(rod|reel|line)\s+(\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex ChumRegex = new Regex(
            @"Drop chum at\s+(-?\d+(?:[.,]\d+)?)\s*[,;]\s*(-?\d+(?:[.,]\d+)?)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex NewFishRegex = new Regex(
            @"NEW FISH!", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex WgtCatchRegex = new Regex(
            @"stat:\s*wgtcatch\s+value=(\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex WpnAssetRegex = new Regex(
            @"Successfully loaded:\s*WpnInGame/(\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private bool _awaitingNewFishWeight;

        // NEXT-107: one local catch block of Player.log = fishGet / NEW FISH! ... stat: wgtcatch ... newFish end
        // [Successfully loaded: FishInGame/<model>]; it ends at the next cast ("DROP COUR"), the next fishGet /
        // NEW FISH! or after SpeciesWindow. A model load is attached only to the block that is open at that moment.
        // The game writes the model line only when that model is loaded for the first time, so most catches have none.
        private sealed class CatchBlock
        {
            public long Id;
            public int? Weight;
            public DateTime When;
            public readonly List<string> Models = new List<string>();
            public bool Emitted;
        }

        private CatchBlock? _block;
        private long _blockSeq;

        /// <summary>NEXT-107: maximal distance between the weight line and a model line of the same block.</summary>
        public static readonly TimeSpan SpeciesWindow = TimeSpan.FromSeconds(10);

        /// <summary>NEXT-107: injectable clock (tests).</summary>
        public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

        /// <summary>NEXT-107: model loads that could not be attached to an open catch block.</summary>
        public int UnassignedModelLoads { get; private set; }

        public string LastDetectedFish { get; private set; } = "";
        public DateTime LastDetectionTime { get; private set; } = DateTime.MinValue;

        /// <summary>ID файла модели, русское название.</summary>
        public event Action<string, string>? FishDetected;

        /// <summary>Поимка другого игрока из локального чата.</summary>
        public event Action<OtherPlayerCatch>? OtherPlayerCatchDetected;

        /// <summary>Текущая база TFBase_N (1..32) из Player.log.</summary>
        public int? CurrentBaseId { get; private set; }
        /// <summary>Текущий getSpawnPoint locid (внутренний ID точки).</summary>
        public int? CurrentSpawnLocId { get; private set; }
        public int? CurrentPointId { get; private set; }

        public event Action<int>? BaseLoaded;
        public event Action<int, int?>? SpawnPointDetected;
        public event Action<uint>? ItemUsed;
        public event Action<string, uint>? EquipmentDetected;

        /// <summary>NEXT-ANALYTICS-04: a "DROP COUR" line was read (poll time; no rod index) — diagnostic only.</summary>
        public event Action<DateTime>? DropCourDetected;
        public event Action<double, double>? ChumDetected;

        /// <summary>Вес своей поимки из Player.log: NEW FISH! + stat: wgtcatch.</summary>
        public event Action<int, DateTime>? LocalCatchWeightDetected;

        /// <summary>
        /// NEXT-107: own catch from Player.log with the model of the same block, if one was seen in the same read
        /// batch (ModelFile null = unknown; Ambiguous = several different models in the block).
        /// </summary>
        public event Action<LocalCatchEvent>? LocalCatchDetected;

        /// <summary>NEXT-107: a model line of an already reported block arrived later: (blockId, model or null, ambiguous).</summary>
        public event Action<long, string?, bool>? LocalCatchSpeciesLate;
        public event Action<uint>? GearAssetLoaded;

        public FishLogMonitor(string logPath)
        {
            this.logPath = logPath;
        }

        public string LogPath => logPath;

        public void Start()
        {
            // Не перечитываем весь старый Player.log: иначе старые поимки
            // других игроков при каждом запуске снова попадали бы в SQLite.
            try
            {
                if (File.Exists(logPath))
                {
                    // Последняя локация могла быть загружена до запуска логгера.
                    PrimeLocationFromTail();
                    lastPosition = new FileInfo(logPath).Length;
                }
            }
            catch
            {
                lastPosition = 0;
            }

            // Polling каждые 500 мс надёжнее FileSystemWatcher для Unity-логов.
            pollTimer = new System.Threading.Timer(CheckLog, null, 0, 500);
        }

        public void Stop()
        {
            pollTimer?.Dispose();
            pollTimer = null;
        }

        private void CheckLog(object? state)
        {
            try
            {
                if (!File.Exists(logPath)) return;

                var fileInfo = new FileInfo(logPath);
                if (fileInfo.Length < lastPosition)
                {
                    // Файл очищен или пересоздан.
                    lastPosition = 0;
                }

                if (fileInfo.Length == lastPosition) return;

                using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(lastPosition, SeekOrigin.Begin);

                using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    ProcessLine(line);
                }
                FlushBlock(); // NEXT-107: report the catch of this batch (with its model, if already seen)

                // stream.Position, а не fileInfo.Length: файл мог дописаться
                // во время чтения, и тогда часть строк прочиталась бы повторно.
                lastPosition = stream.Position;
            }
            catch
            {
                // Игнорируем ошибки доступа: игра может держать файл.
            }
        }

        private void PrimeLocationFromTail()
        {
            try
            {
                using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                // MAPS-FIX-01: читаем ВЕСЬ лог только для строк локации/спавна.
                // Раньше смотрели последние 200 КБ — если сцена загружена давно (лог успел вырасти), «Start load Level» терялся.
                using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
                string? line;
                while ((line = reader.ReadLine()) != null)
                    ProcessLocationLine(line);
            }
            catch
            {
                // Локация не критична для старта.
            }
        }

        private bool ProcessLocationLine(string line)
        {
            var level = LevelRegex.Match(line);
            if (level.Success && int.TryParse(level.Groups[1].Value, out int baseId))
            {
                if (CurrentBaseId != baseId)
                {
                    CurrentBaseId = baseId;
                    CurrentSpawnLocId = null;
                    CurrentPointId = null;
                    BaseLoaded?.Invoke(baseId);
                }
                return true;
            }

            var spawn = SpawnPointRegex.Match(line);
            if (spawn.Success && int.TryParse(spawn.Groups[1].Value, out int locId))
            {
                int? pointId = spawn.Groups[2].Success &&
                               int.TryParse(spawn.Groups[2].Value, out int p)
                    ? p
                    : null;

                if (CurrentSpawnLocId != locId || CurrentPointId != pointId)
                {
                    CurrentSpawnLocId = locId;
                    CurrentPointId = pointId;
                    SpawnPointDetected?.Invoke(locId, pointId);
                }
                return true;
            }

            return false;
        }

        private void ProcessLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            // NEXT-107: catch-block boundaries.
            string trimmed = line.Trim();
            if (trimmed == "fishGet")
            {
                CloseBlock();
                _block = new CatchBlock { Id = ++_blockSeq };
                return;
            }
            if (trimmed.IndexOf("DROP COUR", StringComparison.Ordinal) >= 0)
            {
                CloseBlock(); // a new cast: nothing after this belongs to the previous catch
                DropCourDetected?.Invoke(DateTime.Now); // NEXT-ANALYTICS-04
                return;
            }

            // 0. Своя поимка: NEW FISH! + stat: wgtcatch value=N.
            if (NewFishRegex.IsMatch(line))
            {
                if (_block == null || _block.Weight.HasValue)
                {
                    CloseBlock();
                    _block = new CatchBlock { Id = ++_blockSeq };
                }
                _awaitingNewFishWeight = true;
                return;
            }

            if (_awaitingNewFishWeight)
            {
                var wgt = WgtCatchRegex.Match(line);
                if (wgt.Success && int.TryParse(wgt.Groups[1].Value, out int caughtWeight))
                {
                    _awaitingNewFishWeight = false;
                    if (caughtWeight >= 1 && caughtWeight <= 200_000)
                    {
                        // NEXT-107: reported at the end of the read batch (FlushBlock), together with the model
                        // line of the same block if the game already wrote it.
                        _block ??= new CatchBlock { Id = ++_blockSeq };
                        _block.Weight = caughtWeight;
                        _block.When = Clock();
                    }
                    return;
                }
            }

            // 0b. Загрузка ассета снасти (индикатор, не факт экипировки).
            var wpnAsset = WpnAssetRegex.Match(line);
            if (wpnAsset.Success && uint.TryParse(wpnAsset.Groups[1].Value, out uint wpnId))
            {
                GearAssetLoaded?.Invoke(wpnId);
                return;
            }

            // 0c. Локация из Player.log.
            if (ProcessLocationLine(line))
                return;

            // 1. Дополнительные события Player.log.
            var use = UseItemRegex.Match(line);
            if (use.Success && uint.TryParse(use.Groups[1].Value, out uint usedId))
            {
                ItemUsed?.Invoke(usedId);
                return;
            }

            var equip = EquipRegex.Match(line);
            if (equip.Success && uint.TryParse(equip.Groups[2].Value, out uint equipId))
            {
                EquipmentDetected?.Invoke(equip.Groups[1].Value.ToLowerInvariant(), equipId);
                return;
            }

            var chum = ChumRegex.Match(line);
            if (chum.Success &&
                double.TryParse(chum.Groups[1].Value.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double chumX) &&
                double.TryParse(chum.Groups[2].Value.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double chumZ))
            {
                ChumDetected?.Invoke(chumX, chumZ);
                return;
            }

            // 2. Поимка другого игрока в локальном чате.
            var catchMatch = OtherPlayerCatchRegex.Match(line);
            if (catchMatch.Success)
            {
                string clan = catchMatch.Groups["clan"].Success
                    ? catchMatch.Groups["clan"].Value.Trim()
                    : "";
                string player = catchMatch.Groups["player"].Value.Trim();
                string fish = catchMatch.Groups["fish"].Value.Trim();

                int kg = catchMatch.Groups["kg"].Success
                    ? ParseIntSafe(catchMatch.Groups["kg"].Value)
                    : 0;
                int grams = ParseIntSafe(catchMatch.Groups["grams"].Value);
                int weight = kg * 1000 + grams;

                if (!string.IsNullOrEmpty(player) &&
                    !string.IsNullOrEmpty(fish) &&
                    weight > 0 && weight <= 200_000)
                {
                    OtherPlayerCatchDetected?.Invoke(new OtherPlayerCatch
                    {
                        Timestamp = DateTime.Now,
                        PlayerName = player,
                        Clan = clan,
                        FishName = fish,
                        Weight = weight
                    });
                }

                return;
            }

            // 2. Прежняя логика: Successfully loaded: FishInGame/XXX_name
            var match = Regex.Match(line, @"Successfully loaded: FishInGame/(\w+)");
            if (match.Success)
            {
                string fishFile = match.Groups[1].Value;
                LastDetectedFish = fishFile;
                LastDetectionTime = DateTime.Now;
                OnModelLoaded(fishFile); // NEXT-107

                if (fishNameMap.TryGetValue(fishFile, out string? fishName))
                {
                    FishDetected?.Invoke(fishFile, fishName);
                }
            }
        }

        // ------------------------------------------------------------------ NEXT-107 correlation

        private void OnModelLoaded(string model)
        {
            var b = _block;
            if (b == null)
            {
                UnassignedModelLoads++;
                return;
            }
            if (b.Weight.HasValue && Clock() - b.When > SpeciesWindow)
            {
                CloseBlock();
                UnassignedModelLoads++;
                return;
            }
            bool isNew = true;
            foreach (var m in b.Models)
                if (string.Equals(m, model, StringComparison.OrdinalIgnoreCase)) { isNew = false; break; }
            if (!isNew) return; // duplicate line of the same model: no change
            b.Models.Add(model);
            if (b.Emitted)
            {
                bool ambiguous = b.Models.Count > 1;
                LocalCatchSpeciesLate?.Invoke(b.Id, ambiguous ? null : b.Models[0], ambiguous);
            }
        }

        private void FlushBlock()
        {
            var b = _block;
            if (b == null || b.Emitted || !b.Weight.HasValue) return;
            b.Emitted = true;
            bool ambiguous = b.Models.Count > 1;
            LocalCatchDetected?.Invoke(new LocalCatchEvent
            {
                BlockId = b.Id,
                Weight = b.Weight.Value,
                When = b.When,
                ModelFile = ambiguous || b.Models.Count == 0 ? null : b.Models[0],
                Ambiguous = ambiguous,
                Models = b.Models.ToArray()
            });
            LocalCatchWeightDetected?.Invoke(b.Weight.Value, b.When);
        }

        private void CloseBlock()
        {
            FlushBlock();
            _block = null;
        }

        /// <summary>NEXT-107 tests: process lines as one read batch (same path as CheckLog), without a file.</summary>
        public void ProcessLinesForTest(IEnumerable<string> lines)
        {
            foreach (var l in lines) ProcessLine(l);
            FlushBlock();
        }

        private static int ParseIntSafe(string value)
        {
            return int.TryParse(value, out int result) ? result : 0;
        }

        public void AddFishMapping(string fishFile, string fishName)
        {
            if (!fishNameMap.ContainsKey(fishFile))
                fishNameMap[fishFile] = fishName;
        }

        public string GetFishName(string fishFile)
        {
            return fishNameMap.TryGetValue(fishFile, out string? name) ? name : "";
        }
    }

    /// <summary>NEXT-107: own catch from Player.log.</summary>
    public sealed class LocalCatchEvent
    {
        public long BlockId { get; init; }
        public int Weight { get; init; }
        public DateTime When { get; init; }
        /// <summary>Model of the same block ("37_karasSilver"), null if none or ambiguous.</summary>
        public string? ModelFile { get; init; }
        public bool Ambiguous { get; init; }
        public string[] Models { get; init; } = Array.Empty<string>();
    }
}
