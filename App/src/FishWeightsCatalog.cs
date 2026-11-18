using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FisherLogger
{
    // ADVISOR-02 (R3/R4): трофейные пороги (large/unique) из fh_fish_weights.json.
    // Мост идентификаторов: fh.id ≠ fish_lang_id. Сопоставление через fish_full_database.json:
    //   fish_lang_id / name_ru(/name_en) → latin (первичный ключ) / name_en (вторичный) → запись fh.
    // Валидность строго: large>0 && unique>0 && large<unique (граммы).
    // Неоднозначность (несколько fh-записей по ключу) → null (fallback), не «первая запись».
    public static class FishWeightsCatalog
    {
        private static readonly Lazy<Catalog> _cat = new(() => Catalog.Load());

        /// <summary>Возвращает (LargeG, UniqueG) в граммах, либо null, если порог недостоверен/вид не найден.</summary>
        public static (int LargeG, int UniqueG)? Resolve(uint? fishLangId, string? fishName)
            => _cat.Value.Resolve(fishLangId, fishName);

        private sealed class Catalog
        {
            private readonly Dictionary<string, List<WeightEntry>> _byLatin = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<WeightEntry>> _byNameEn = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<uint, FishDbEntry> _ffdById = new();
            private readonly Dictionary<string, List<FishDbEntry>> _ffdByName = new(StringComparer.OrdinalIgnoreCase);

            public static Catalog Load()
            {
                var c = new Catalog();
                c.LoadWeights(AdvisorResources.LoadWeights());
                c.LoadFishFull(AdvisorResources.LoadFishFullDatabase());
                return c;
            }

            private void LoadWeights(string? json)
            {
                if (string.IsNullOrWhiteSpace(json)) return;
                try
                {
                    var opts = new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };
                    var list = JsonSerializer.Deserialize<List<WeightDto>>(json, opts);
                    if (list == null) return;

                    foreach (var w in list)
                    {
                        if (w == null) continue;
                        int? large = ParseGrams(w.large);
                        int? unique = ParseGrams(w.unique);
                        if (!large.HasValue || !unique.HasValue || large.Value <= 0 || unique.Value <= 0 || large.Value >= unique.Value)
                            continue;

                        var e = new WeightEntry { LargeG = large.Value, UniqueG = unique.Value };
                        string latin = (w.latin ?? "").Trim();
                        string name = (w.name ?? "").Trim();
                        if (latin.Length > 0)
                        {
                            if (!_byLatin.TryGetValue(latin, out var ll)) { ll = new List<WeightEntry>(); _byLatin[latin] = ll; }
                            ll.Add(e);
                        }
                        if (name.Length > 0)
                        {
                            if (!_byNameEn.TryGetValue(name, out var nn)) { nn = new List<WeightEntry>(); _byNameEn[name] = nn; }
                            nn.Add(e);
                        }
                    }
                }
                catch (Exception ex) { AdvisorResources.Warn?.Invoke("[ADVISOR-02] fh_fish_weights.json: " + ex.Message); }
            }

            private void LoadFishFull(string? json)
            {
                if (string.IsNullOrWhiteSpace(json)) return;
                try
                {
                    var opts = new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };
                    var root = JsonSerializer.Deserialize<FishFullRoot>(json, opts);
                    if (root?.fish == null) return;

                    foreach (var f in root.fish)
                    {
                        if (f == null || !f.id.HasValue || f.id.Value < 0) continue;
                        var e = new FishDbEntry
                        {
                            Latin = (f.latin ?? "").Trim(),
                            NameEn = (f.name_en ?? "").Trim(),
                            NameRu = (f.name_ru ?? "").Trim()
                        };
                        _ffdById[(uint)f.id.Value] = e;
                        if (e.NameRu.Length > 0)
                        {
                            if (!_ffdByName.TryGetValue(e.NameRu, out var ruList)) { ruList = new List<FishDbEntry>(); _ffdByName[e.NameRu] = ruList; }
                            ruList.Add(e);
                        }
                        if (e.NameEn.Length > 0)
                        {
                            if (!_ffdByName.TryGetValue(e.NameEn, out var enList)) { enList = new List<FishDbEntry>(); _ffdByName[e.NameEn] = enList; }
                            enList.Add(e);
                        }
                    }
                }
                catch (Exception ex) { AdvisorResources.Warn?.Invoke("[ADVISOR-02] fish_full_database.json: " + ex.Message); }
            }

            public (int LargeG, int UniqueG)? Resolve(uint? fishLangId, string? fishName)
            {
                string? latin = null;
                string? nameEn = null;

                if (fishLangId.HasValue && _ffdById.TryGetValue(fishLangId.Value, out var byId))
                {
                    latin = byId.Latin;
                    nameEn = byId.NameEn;
                }
                else if (!string.IsNullOrWhiteSpace(fishName) && _ffdByName.TryGetValue(fishName.Trim(), out var byNameList) && byNameList.Count == 1)
                {
                    var byName = byNameList[0];
                    latin = byName.Latin;
                    nameEn = byName.NameEn;
                }

                if (!string.IsNullOrEmpty(latin) && _byLatin.TryGetValue(latin, out var lm) && lm.Count == 1)
                    return (lm[0].LargeG, lm[0].UniqueG);
                if (!string.IsNullOrEmpty(nameEn) && _byNameEn.TryGetValue(nameEn, out var nm) && nm.Count == 1)
                    return (nm[0].LargeG, nm[0].UniqueG);
                return null;
            }
        }

        private sealed class WeightEntry { public int LargeG; public int UniqueG; }
        private sealed class FishDbEntry { public string Latin = ""; public string NameEn = ""; public string NameRu = ""; }

        private sealed class WeightDto { public string? name { get; set; } public string? latin { get; set; } public string? large { get; set; } public string? unique { get; set; } }
        private sealed class FishFullRoot { public List<FishFullDto>? fish { get; set; } }
        private sealed class FishFullDto { public int? id { get; set; } public string? name_ru { get; set; } public string? name_en { get; set; } public string? latin { get; set; } }

        // "~1.25 kg" → 1250; "~0.017 kg" → 17; "~0 kg" / null / непарсится → null.
        private static readonly Regex _grams = new(@"^\s*(?<num>[0-9]+(?:\.[0-9]+)?)\s*(?<unit>kg|g)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static int? ParseGrams(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            string t = s.Trim().TrimStart('~').Trim();
            var m = _grams.Match(t);
            if (!m.Success) return null;
            if (!double.TryParse(m.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
            double grams = string.Equals(m.Groups["unit"].Value, "g", StringComparison.OrdinalIgnoreCase) ? v : v * 1000.0;
            if (grams <= 0 || grams > int.MaxValue) return null;
            return (int)Math.Round(grams);
        }
    }
}
