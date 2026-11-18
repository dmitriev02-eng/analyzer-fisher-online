using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace FisherLogger
{
    /// <summary>
    /// ANDROID-UX-01: данные «Советника» для телефона — те же строки, что карточка вида в Windows (ADVISOR-UX-01),
    /// одним пакетом, чтобы телефон мог показывать их без связи с ПК. Только чтение; статистика — BiteAnalytics.Advise
    /// без изменений. Частота поимок ≠ вероятность клёва; справочник fisher-hub — чужие уловы, не ваша статистика.
    /// </summary>
    public static class CompanionAdvisor
    {
        private static readonly Dictionary<string, string> RefWords = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Night"] = "ночь", ["Morning"] = "утро", ["Noon"] = "полдень", ["Afternoon"] = "день", ["Evening"] = "вечер",
            ["Clear"] = "ясно", ["Cloudy"] = "облачно", ["Rainy"] = "дождь", ["Foggy"] = "туман"
        };
        private static string Ru(string k) => RefWords.TryGetValue(k, out var v) ? v : k;

        public static string Casts(int n)
        {
            int m10 = n % 10, m100 = n % 100;
            string w = m10 == 1 && m100 != 11 ? "заброс" : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? "заброса" : "забросов";
            return n.ToString(CultureInfo.InvariantCulture) + " " + w;
        }

        public static string Grams(int g) => g >= 1000 ? (g / 1000.0).ToString("0.##", CultureInfo.GetCultureInfo("ru-RU")) + " кг" : g + " г";

        public static string? Grid(uint? loc, double x, double z) =>
            MapGridCoordinates.ToGrid(loc, x, z) is { } g ? MapGridCoordinates.FormatGrid(g) : null;

        /// <summary>Пакет для телефона: обзор, список видов (разделы как в Windows) и карточки всех видов.</summary>
        public static object Build(BiteAnalytics an, ReferenceProvider? refs)
        {
            var o = an.Overview(null);
            var species = new List<object>();
            var cards = new Dictionary<string, object>();
            var known = new HashSet<uint>();
            foreach (var sp in an.KnownSpecies())
            {
                known.Add(sp.id);
                species.Add(new { id = sp.id, name = sp.name, own = sp.own, pub = sp.pub, refOnly = false,
                                  section = sp.own > 0 ? "Ловили вы" : "Ловят другие игроки" });
                cards[sp.id.ToString(CultureInfo.InvariantCulture)] = Card(an, refs, sp.id);
            }
            if (refs != null)
            {
                IReadOnlyList<(uint Id, string Name)> rs;
                try { rs = refs.ReferenceSpecies(); } catch { rs = Array.Empty<(uint, string)>(); }
                foreach (var (rid, rname) in rs.Where(x => !known.Contains(x.Id)).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    species.Add(new { id = rid, name = rname, own = 0, pub = 0, refOnly = true, section = "Справочник" });
                    cards[rid.ToString(CultureInfo.InvariantCulture)] = RefOnlyCard(refs, rid, rname);
                }
            }
            return new
            {
                overview = new { casts = o.Casts, catches = o.OwnCatches, species = o.Species, publicAnnouncements = o.PublicAnnouncements },
                generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                species, cards,
                note = "Частота поимок на ваших забросах — не вероятность клёва. «Другие игроки» — справочник чужих уловов, не ваша статистика."
            };
        }

        private static object? RefBlock(ReferenceProvider? refs, uint id, string name, bool muted)
        {
            ReferenceHints? h = null;
            try { h = refs?.GetHintsForSpecies(id, name); } catch { }
            if (h == null) return null;
            int maxL = Math.Max(1, h.Locations.Select(l => l.Count).DefaultIfEmpty(1).Max());
            int maxB = Math.Max(1, h.Baits.Select(b => b.Count).DefaultIfEmpty(1).Max());
            return new
            {
                count = h.Count, muted,
                places = h.Locations.OrderByDescending(l => l.Count).Take(4).Select(l => new
                {
                    title = l.Name, sub = l.Points.Count > 0 ? "точки " + string.Join(", ", l.Points.Take(4)) : "",
                    value = l.Count + " ул.", frac = (double)l.Count / maxL, locationId = l.LocationId > 0 ? l.LocationId : (int?)null,
                    // BRIDGE-FB-01: все точки справочника в мировых X/Z (центр клетки карты игры) — для показа поверх карты
                    pts = RefPoints(l.LocationId, l.Points)
                }),
                baits = h.Baits.OrderByDescending(b => b.Count).Take(4).Select(b => new { title = b.Name, value = b.Count + " ул.", frac = (double)b.Count / maxB }),
                periods = h.Periods.OrderByDescending(x => x.Value).Take(3).Select(x => Ru(x.Key)),
                weather = h.Weather.OrderByDescending(x => x.Value).Take(2).Select(x => Ru(x.Key)),
                source = ReferenceProvider.SourceNote
            };
        }

        /// <summary>BRIDGE-FB-01: «NNN:NNN» справочника → мировые X/Z центра клетки (обратное к MapGridCoordinates.Convert:
        /// клетка g = ceil((мир − начало)/5) ⇒ мир ∈ (начало + 5(g−1), начало + 5g], центр = начало + 5(g − 0,5)).
        /// Только для водоёмов с проверенным пересчётом и точек внутри террейна; остальные — без x/z (не угадываем).</summary>
        public static List<object> RefPoints(int locationId, IEnumerable<string> points)
        {
            var res = new List<object>();
            var t = locationId > 0 ? MapGridCoordinates.For((uint)locationId) : null;
            foreach (var p in points.Distinct(StringComparer.Ordinal))
            {
                var w = t != null ? GridToWorld(t, p) : null;
                res.Add(new { g = p, x = w.HasValue ? Math.Round(w.Value.X, 2) : (double?)null, z = w.HasValue ? Math.Round(w.Value.Z, 2) : (double?)null });
            }
            return res;
        }

        /// <summary>«NNN:NNN» → центр клетки в мировых X/Z; null — неверный текст или клетка вне террейна.</summary>
        public static (double X, double Z)? GridToWorld(MapGridCoordinates.Transform t, string grid)
        {
            var a = grid.Split(':');
            if (!t.HasTransform || a.Length != 2 || !int.TryParse(a[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int gx)
                || !int.TryParse(a[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int gy)
                || gx < 1 || gy < 1 || gx > t.MaxCellX || gy > t.MaxCellY) return null;
            // последняя клетка у края может быть неполной (4096 / 5 = 819,2): берём середину её части внутри террейна
            double ox = t.OriginX!.Value, oz = t.OriginZ!.Value;
            double x0 = ox + t.Cell * (gx - 1), x1 = Math.Min(ox + t.Cell * gx, ox + t.SizeX!.Value);
            double z0 = oz + t.Cell * (gy - 1), z1 = Math.Min(oz + t.Cell * gy, oz + t.SizeZ!.Value);
            return ((x0 + x1) / 2, (z0 + z1) / 2);
        }

        private static object RefOnlyCard(ReferenceProvider refs, uint id, string name)
        {
            var rb = RefBlock(refs, id, name, false);
            return new
            {
                id, name, own = 0, pub = 0, bestClass = -1, recordG = 0,
                status = rb != null ? "reference" : "none",
                statusText = rb != null ? "Своих данных нет — ниже ориентир по уловам других игроков" : "Данных пока нет — ни ваших, ни в справочнике",
                places = Array.Empty<object>(), waters = Array.Empty<object>(), baits = Array.Empty<object>(), times = Array.Empty<object>(),
                reference = rb, notes = Array.Empty<string>()
            };
        }

        public static object Card(BiteAnalytics an, ReferenceProvider? refs, uint id)
        {
            var adv = an.Advise(id);
            int best = an.MaxWindex(id), maxW = an.MaxWeight(id);
            var rb = RefBlock(refs, id, adv.Fish, adv.HasAdvice);
            string status = adv.HasAdvice ? "advice" : rb != null ? "reference" : "none";
            string statusText = adv.HasAdvice ? "Есть совет по вашим уловам"
                : rb != null ? "Своих данных мало — ниже ориентир по уловам других игроков" : "Данных пока нет — ни ваших, ни в справочнике";

            static double Rate(EvidenceRow r) => r.Attempts > 0 ? (double)r.Successes / r.Attempts : 0;
            double BestIn(string g) => Math.Max(1e-9, adv.Rows.Where(r => r.Group == g && r.Advice).Select(Rate).DefaultIfEmpty(0).Max());
            IEnumerable<EvidenceRow> Top(string g, int n) => adv.Rows.Where(r => r.Group == g).OrderByDescending(r => r.Advice).ThenByDescending(r => r.Lo).ThenByDescending(r => r.Successes).Take(n);
            object Row(EvidenceRow r, string sub, string? grid = null, double? depth = null) => new
            {
                title = r.Name.Length > 0 ? r.Name : r.Item,
                sub = r.Advice ? sub : (sub.Length > 0 ? sub + " · " : "") + "мало данных",
                value = $"{r.Successes} из {r.Attempts}", successes = r.Successes, attempts = r.Attempts,
                frac = r.Advice ? Math.Min(1, Rate(r) / BestIn(r.Group)) : (double?)null, advice = r.Advice,
                verdict = r.Verdict, level = r.Level,
                locationId = r.LocationId, x = r.X, z = r.Z, grid, depthM = depth, baitTid = r.BaitTid
            };

            var places = Top("Участок", 5).Select(r =>
            {
                var sub = new List<string>();
                string? grid = null; double? depth = null;
                if (r.X.HasValue && r.Z.HasValue)
                {
                    grid = Grid(r.LocationId, r.X.Value, r.Z.Value);
                    sub.Add(grid != null ? "координаты " + grid : FormattableString.Invariant($"X {r.X.Value:0}, Z {r.Z.Value:0}"));
                    depth = Bathymetry.DepthAt(r.LocationId, r.X.Value, r.Z.Value);
                    if (depth is double d) sub.Add(FormattableString.Invariant($"глубина {d:0.0} м"));
                }
                sub.Add(Casts(r.Attempts));
                return Row(r, string.Join(" · ", sub), grid, depth);
            }).ToList();

            return new
            {
                id, name = adv.Fish, own = adv.OwnCatches, pub = adv.PublicSeen, bestClass = best, recordG = maxW,
                bestClassText = best >= 0 ? GradeBadge.ShortName(best) : "", recordText = maxW > 0 ? Grams(maxW) : "",
                status, statusText,
                places,
                waters = Top("Водоём", 4).Select(r => Row(r, "")).ToList(),
                baits = Top("Наживка", 5).Select(r => Row(r, "")).ToList(),
                times = Top("Игровое время", 4).Select(r => Row(r, "")).ToList(),
                reference = rb,
                notes = adv.Notes
            };
        }

        /// <summary>Иконки видов и наживок (PNG в base64) для показа без связи. Только существующие файлы ресурсов.</summary>
        public static object Icons(IEnumerable<uint> fishIds, IEnumerable<long> baitTids)
        {
            static string? B64(string rel)
            {
                try
                {
                    string p = IconLoader.ResolvePath(rel);
                    if (!File.Exists(p)) return null;
                    var fi = new FileInfo(p);
                    return fi.Length > 0 && fi.Length < 256 * 1024 ? Convert.ToBase64String(File.ReadAllBytes(p)) : null;
                }
                catch { return null; }
            }
            var fish = new Dictionary<string, string>();
            foreach (var f in fishIds.Distinct())
                if (B64("assets/fish_icons/fish_" + f.ToString(CultureInfo.InvariantCulture) + ".png") is string s) fish[f.ToString(CultureInfo.InvariantCulture)] = s;
            var bait = new Dictionary<string, string>();
            foreach (var b in baitTids.Where(t => t > 0).Distinct())
                if (B64("assets/bait_icons/item_" + b.ToString(CultureInfo.InvariantCulture) + ".png") is string s) bait[b.ToString(CultureInfo.InvariantCulture)] = s;
            return new { fish, bait };
        }
    }
}
