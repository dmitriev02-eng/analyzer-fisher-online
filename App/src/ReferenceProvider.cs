using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FisherLogger
{
    // ADVISOR-02 (R1): справочник агрегированных чужих уловов (fishing_reference.json).
    // Fallback для «Советника», когда своей статистики мало. Это НЕ вероятность клёва.
    public sealed class ReferenceProvider
    {
        public const string SourceNote = "справочно, не ваша статистика";

        private readonly Lazy<Catalog> _cat;
        private readonly Lazy<GameNames> _game;

        public ReferenceProvider() : this((string?)null) { }

        /// <summary>Для тестов: загрузить из явного JSON вместо встроенного ресурса.</summary>
        public ReferenceProvider(string? json) : this(json, null) { }

        /// <summary>FIX-03: справочник + словарь видов игры (fish_full_database.json: id, name_ru, name_en) для сопоставления.</summary>
        public ReferenceProvider(string? json, string? fishDbJson)
        {
            _cat = new Lazy<Catalog>(() => Catalog.Load(json ?? AdvisorResources.LoadReference()));
            _game = new Lazy<GameNames>(() => GameNames.Load(fishDbJson ?? AdvisorResources.LoadFishFullDatabase()));
        }

        /// <summary>
        /// Подсказки по названию вида. FIX-03: сначала точное совпадение (регистр, «ё/е» и пробелы не важны); если нет —
        /// название ищется в словаре игры и сопоставляется через его английское имя (русские имена справочника совпадают
        /// с игровыми лишь у ~30 % видов, английские — у всех). Неоднозначность (одно имя → разные записи справочника) → null.
        /// </summary>
        public ReferenceHints? GetHints(string fishName)
        {
            var direct = _cat.Value.GetHints(fishName);
            if (direct != null || string.IsNullOrWhiteSpace(fishName)) return direct;
            return Unique(_game.Value.IdsByName(fishName).SelectMany(id => _game.Value.Names(id)).Select(n => _cat.Value.GetHints(n)));
        }

        /// <summary>FIX-03: подсказки по fish_lang_id (имя — запасной путь): английское, затем русское имя вида из словаря игры.</summary>
        public ReferenceHints? GetHintsForSpecies(uint? fishLangId, string? fishName)
        {
            if (fishLangId is uint id && id > 0)
            {
                var byId = Unique(_game.Value.Names(id).Select(n => _cat.Value.GetHints(n)));
                if (byId != null) return byId;
            }
            return string.IsNullOrWhiteSpace(fishName) ? null : GetHints(fishName);
        }

        /// <summary>FIX-03: имя для показа: русское из справочника, если оно русское; иначе из словаря игры; иначе английское.</summary>
        public string DisplayName(ReferenceHints h, uint? fishLangId = null)
        {
            if (h.FishRu.Any(ch => ch >= 'А' && ch <= 'я' || ch == 'ё' || ch == 'Ё')) return h.FishRu;
            if (fishLangId is uint id && _game.Value.RuName(id) is { Length: > 0 } ru) return ru;
            var ids = _game.Value.IdsByName(h.FishEn);
            if (ids.Count == 1 && _game.Value.RuName(ids[0]) is { Length: > 0 } ru2) return ru2;
            return h.FishEn.Length > 0 ? h.FishEn : h.FishRu;
        }

        /// <summary>FIX-03: виды справочника, однозначно сопоставленные с fish_lang_id игры (для списка видов в окне).</summary>
        public IReadOnlyList<(uint Id, string Name)> ReferenceSpecies()
        {
            var res = new List<(uint, string)>();
            foreach (var (ru, en) in _cat.Value.AllNames())
            {
                var ids = _game.Value.IdsByName(en).Count > 0 ? _game.Value.IdsByName(en) : _game.Value.IdsByName(ru);
                if (ids.Count != 1) continue;
                var h = GetHintsForSpecies(ids[0], null);
                if (h != null) res.Add((ids[0], DisplayName(h, ids[0])));
            }
            return res.GroupBy(x => x.Item1).Select(g => g.First()).ToList();
        }

        private static ReferenceHints? Unique(IEnumerable<ReferenceHints?> found)
        {
            var d = found.Where(h => h != null).Distinct().ToList();
            return d.Count == 1 ? d[0] : null;
        }

        internal static string Norm(string? s) => string.Join(" ", (s ?? "").Trim().ToLowerInvariant().Replace('ё', 'е')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        /// <summary>FIX-03: словарь видов игры: id ↔ name_ru / name_en.</summary>
        private sealed class GameNames
        {
            private readonly Dictionary<uint, (string Ru, string En)> _byId = new();
            private readonly Dictionary<string, List<uint>> _byName = new();

            public static GameNames Load(string? json)
            {
                var g = new GameNames();
                if (string.IsNullOrWhiteSpace(json)) return g;
                try
                {
                    using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("fish", out var arr) || arr.ValueKind != JsonValueKind.Array) return g;
                    foreach (var e in arr.EnumerateArray())
                    {
                        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out long id) || id <= 0 || id > uint.MaxValue) continue;
                        string ru = e.TryGetProperty("name_ru", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()!.Trim() : "";
                        string en = e.TryGetProperty("name_en", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : "";
                        g._byId[(uint)id] = (ru, en);
                        foreach (var name in new[] { ru, en })
                        {
                            string k = Norm(name); if (k.Length == 0) continue;
                            if (!g._byName.TryGetValue(k, out var l)) g._byName[k] = l = new List<uint>();
                            if (!l.Contains((uint)id)) l.Add((uint)id);
                        }
                    }
                }
                catch (Exception ex) { AdvisorResources.Warn?.Invoke("[FIX-03] fish_full_database.json: " + ex.Message); }
                return g;
            }

            public IReadOnlyList<uint> IdsByName(string? name) => _byName.TryGetValue(Norm(name), out var l) ? l : Array.Empty<uint>();
            public IEnumerable<string> Names(uint id)
            {
                if (!_byId.TryGetValue(id, out var t)) yield break;
                if (t.En.Length > 0) yield return t.En;
                if (t.Ru.Length > 0) yield return t.Ru;
            }
            public string? RuName(uint id) => _byId.TryGetValue(id, out var t) ? t.Ru : null;
        }

        /// <summary>Все пары (русское имя, английское имя).</summary>
        public IReadOnlyList<(string Ru, string En)> AllNames() => _cat.Value.AllNames();

        /// <summary>Топ-n похожих названий (по-русски) для ответа «вид не найден».</summary>
        public IReadOnlyList<string> ClosestNames(string q, int n) => _cat.Value.ClosestNames(q, n);

        private sealed class Catalog
        {
            private readonly Dictionary<string, ReferenceHints> _index = new(StringComparer.Ordinal);   // ключ — Norm(имя)
            private readonly List<(string Ru, string En)> _names = new();

            public static Catalog Load(string? json)
            {
                var c = new Catalog();
                if (string.IsNullOrWhiteSpace(json)) return c;
                try
                {
                    var opts = new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };
                    var root = JsonSerializer.Deserialize<RefRoot>(json, opts);
                    if (root?.species == null) return c;

                    foreach (var s in root.species)
                    {
                        if (s == null) continue;
                        string ru = (s.fish_ru ?? "").Trim();
                        string en = (s.fish ?? "").Trim();
                        if (ru.Length == 0 && en.Length == 0) continue;

                        var hints = new ReferenceHints
                        {
                            FishRu = ru,
                            FishEn = en,
                            Count = s.count,
                            Locations = (s.locations ?? new List<LocDto>()).Select(l => new RefLocation
                            {
                                Name = string.IsNullOrWhiteSpace(l?.map_ru) ? (l?.map ?? "") : l.map_ru,
                                LocationId = l?.location_id ?? 0,
                                Count = l?.count ?? 0,
                                Points = l?.points ?? new List<string>()
                            }).ToList(),
                            Baits = (s.baits ?? new List<BaitDto>()).Select(b => new RefBait
                            {
                                Name = string.IsNullOrWhiteSpace(b?.bait_ru) ? (b?.bait ?? "") : b.bait_ru,
                                Count = b?.count ?? 0
                            }).ToList(),
                            Lures = (s.lures ?? new List<LureDto>()).Select(b => new RefBait
                            {
                                Name = string.IsNullOrWhiteSpace(b?.lure_ru) ? (b?.lure ?? "") : b.lure_ru,
                                Count = b?.count ?? 0
                            }).ToList(),
                            Periods = s.periods ?? new Dictionary<string, int>(),
                            Weather = s.weather ?? new Dictionary<string, int>(),
                            Hooks = s.hooks ?? new Dictionary<string, int>(),
                            Depth = new RefDepth
                            {
                                Min = s.depth?.min ?? 0,
                                Avg = s.depth?.avg ?? 0,
                                Max = s.depth?.max ?? 0,
                                N = s.depth?.n ?? 0
                            }
                        };

                        string kr = Norm(ru), ke = Norm(en);
                        if (kr.Length > 0 && !c._index.ContainsKey(kr)) c._index[kr] = hints;
                        if (ke.Length > 0 && !c._index.ContainsKey(ke)) c._index[ke] = hints;
                        c._names.Add((ru, en));
                    }
                }
                catch (Exception ex) { AdvisorResources.Warn?.Invoke("[ADVISOR-02] fishing_reference.json: " + ex.Message); }
                return c;
            }

            public ReferenceHints? GetHints(string fishName)
            {
                if (string.IsNullOrWhiteSpace(fishName)) return null;
                return _index.TryGetValue(Norm(fishName), out var h) ? h : null;
            }

            public IReadOnlyList<(string Ru, string En)> AllNames() => _names;

            public IReadOnlyList<string> ClosestNames(string q, int n)
            {
                if (string.IsNullOrWhiteSpace(q) || n <= 0) return Array.Empty<string>();
                string needle = q.Trim();

                static int Score((string Ru, string En) t, string needle)
                {
                    if (string.Equals(t.Ru, needle, StringComparison.OrdinalIgnoreCase)) return 0;
                    if (string.Equals(t.En, needle, StringComparison.OrdinalIgnoreCase)) return 0;
                    if (t.Ru.StartsWith(needle, StringComparison.OrdinalIgnoreCase)) return 1;
                    if (t.En.StartsWith(needle, StringComparison.OrdinalIgnoreCase)) return 1;
                    if (t.Ru.Contains(needle, StringComparison.OrdinalIgnoreCase)) return 2;
                    if (t.En.Contains(needle, StringComparison.OrdinalIgnoreCase)) return 2;
                    return 3;
                }

                return _names
                    .Select(t => (t, s: Score(t, needle)))
                    .Where(x => x.s <= 2)
                    .OrderBy(x => x.s).ThenBy(x => x.t.Ru, StringComparer.OrdinalIgnoreCase)
                    .Take(n)
                    .Select(x => x.t.Ru.Length > 0 ? x.t.Ru : x.t.En)
                    .ToList();
            }
        }

        private sealed class RefRoot { public List<SpeciesDto>? species { get; set; } }

        private sealed class SpeciesDto
        {
            public string? fish { get; set; }
            public string? fish_ru { get; set; }
            public int count { get; set; }
            public List<LocDto>? locations { get; set; }
            public List<BaitDto>? baits { get; set; }
            public List<LureDto>? lures { get; set; }
            public Dictionary<string, int>? periods { get; set; }
            public Dictionary<string, int>? weather { get; set; }
            public Dictionary<string, int>? hooks { get; set; }
            public DepthDto? depth { get; set; }
        }

        private sealed class LocDto { public string? map { get; set; } public string? map_ru { get; set; } public int location_id { get; set; } public int count { get; set; } public List<string>? points { get; set; } }
        private sealed class BaitDto { public string? bait { get; set; } public string? bait_ru { get; set; } public int count { get; set; } }
        private sealed class LureDto { public string? lure { get; set; } public string? lure_ru { get; set; } public int count { get; set; } }
        private sealed class DepthDto { public double? min { get; set; } public double? avg { get; set; } public double? max { get; set; } public int? n { get; set; } }
    }

    // R1: результат GetHints.
    public sealed class ReferenceHints
    {
        public string FishRu { get; set; } = "";
        public string FishEn { get; set; } = "";
        public int Count { get; set; }
        public List<RefLocation> Locations { get; set; } = new();
        public List<RefBait> Baits { get; set; } = new();
        public List<RefBait> Lures { get; set; } = new();
        public Dictionary<string, int> Periods { get; set; } = new();
        public Dictionary<string, int> Weather { get; set; } = new();
        public Dictionary<string, int> Hooks { get; set; } = new();
        public RefDepth Depth { get; set; } = new();
    }

    public sealed class RefLocation { public string Name { get; set; } = ""; public int LocationId { get; set; } public int Count { get; set; } public List<string> Points { get; set; } = new(); }
    public sealed class RefBait { public string Name { get; set; } = ""; public int Count { get; set; } }
    public sealed class RefDepth { public double Min { get; set; } public double Avg { get; set; } public double Max { get; set; } public int N { get; set; } }
}
