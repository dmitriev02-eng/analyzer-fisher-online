using System;
using System.Collections.Generic;
using System.Linq;

namespace FisherLogger
{
    // =====================================================================================
    // AUDIT-02: «какая рыба водится на каком водоёме». В игре на водоёме ловится только свой набор рыбы, но ПОЛНОГО списка
    // у нас нет (проверено: location_map.json из старых LocInfo — только 8 водоёмов из 36 и неполно, например нет Карася
    // золотого на р. Пра при 51 своей поимке; справочник fisher-hub — 1241 чужой улов). Поэтому список — объединение
    // свидетельств с пометкой источника:
    //   own  — ваши подтверждённые уловы на этом водоёме (CONFIRMED);
    //   game — список рыбы водоёма из файлов игры (location_map.json, PROBABLE: может быть неполным/старым);
    //   ref  — справочник fisher-hub (чужие уловы с водоёмом, PROBABLE).
    // Объявления других игроков сюда НЕ входят: в них нет места поимки (receiver_location — где были вы).
    // «Нет в списке» значит «не подтверждено», а не «не водится».
    // =====================================================================================
    public sealed class HabitatIndex
    {
        private readonly Dictionary<uint, Dictionary<uint, HashSet<string>>> _byFish = new();   // fish → loc → sources

        public void Add(uint fish, uint loc, string source)
        {
            if (!_byFish.TryGetValue(fish, out var m)) _byFish[fish] = m = new();
            if (!m.TryGetValue(loc, out var s)) m[loc] = s = new();
            s.Add(source);
        }

        /// <summary>Есть ли сведения о виде хоть на одном водоёме (иначе фильтровать по водоёмам нельзя).</summary>
        public bool Known(uint fish) => _byFish.ContainsKey(fish);
        public bool Lives(uint fish, uint? loc) => loc.HasValue && _byFish.TryGetValue(fish, out var m) && m.ContainsKey(loc.Value);
        public IReadOnlyCollection<uint> LocationsOf(uint fish) => _byFish.TryGetValue(fish, out var m) ? m.Keys : Array.Empty<uint>();
        public IEnumerable<string> Sources(uint fish, uint loc) => _byFish.TryGetValue(fish, out var m) && m.TryGetValue(loc, out var s) ? s : Enumerable.Empty<string>();
        public IEnumerable<uint> SpeciesAt(uint loc) => _byFish.Where(kv => kv.Value.ContainsKey(loc)).Select(kv => kv.Key);
        public int Count => _byFish.Count;

        /// <summary>Учитывать ли забросы на водоёме для вида: да, если вид там подтверждён, или о виде нет сведений вообще.</summary>
        public bool Counts(uint fish, uint? loc) => !Known(fish) || Lives(fish, loc);

        public static string SourceText(IEnumerable<string> src)
        {
            var s = src.ToHashSet();
            var parts = new List<string>();
            if (s.Contains("own")) parts.Add("ваши уловы");
            if (s.Contains("game")) parts.Add("список игры");
            if (s.Contains("ref")) parts.Add("справочник");
            return string.Join(", ", parts);
        }
    }

    public static class Habitat
    {
        /// <summary>Списки рыбы водоёмов из файлов игры (Data\location_map.json, поле fish_ids). Пусто, если файла нет.</summary>
        public static Dictionary<uint, List<int>> GameLists(string? path = null)
        {
            var res = new Dictionary<uint, List<int>>();
            try
            {
                path ??= AppPaths.LocationMapPath;
                if (!System.IO.File.Exists(path)) return res;
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("locations", out var locs)) return res;
                foreach (var e in locs.EnumerateArray())
                {
                    if (!e.TryGetProperty("id", out var idEl) || !idEl.TryGetUInt32(out uint id)) continue;
                    if (!e.TryGetProperty("fish_ids", out var f) || f.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                    var list = f.EnumerateArray().Where(x => x.ValueKind == System.Text.Json.JsonValueKind.Number && x.TryGetInt32(out _)).Select(x => x.GetInt32()).ToList();
                    if (list.Count > 0) res[id] = list;
                }
            }
            catch { }
            return res;
        }

        /// <summary>Свидетельства: свои подтверждённые уловы + список игры (fish_ids водоёма) + справочник fisher-hub.</summary>
        public static HabitatIndex Build(IEnumerable<(uint fish, uint? loc)> own, Func<uint, IEnumerable<int>>? gameFishOfLocation,
                                         IEnumerable<uint>? gameLocations, ReferenceProvider? refs, Func<uint, string>? fishName)
        {
            var h = new HabitatIndex();
            foreach (var (f, l) in own) if (l.HasValue && l.Value > 0) h.Add(f, l.Value, "own");
            if (gameFishOfLocation != null && gameLocations != null)
                foreach (var loc in gameLocations)
                    foreach (var f in gameFishOfLocation(loc)) if (f > 0) h.Add((uint)f, loc, "game");
            if (refs != null && fishName != null)
            {
                IReadOnlyList<(uint Id, string Name)> rs;
                try { rs = refs.ReferenceSpecies(); } catch { rs = Array.Empty<(uint, string)>(); }
                var ids = rs.Select(x => x.Id).Distinct().ToList();
                foreach (var id in ids)
                {
                    ReferenceHints? hint = null;
                    try { hint = refs.GetHintsForSpecies(id, fishName(id)); } catch { }
                    if (hint == null) continue;
                    foreach (var l in hint.Locations) if (l.LocationId > 0) h.Add(id, (uint)l.LocationId, "ref");
                }
            }
            return h;
        }
    }
}
