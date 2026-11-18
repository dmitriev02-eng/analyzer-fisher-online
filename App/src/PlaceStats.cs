using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FisherLogger
{
    /// <summary>
    /// MAPCARD-01: сводка уловов места по наживке и по виду — не только «сколько», но и «какие»: средний и лучший вес, классы (0–3).
    /// Только описание записанных уловов (не вероятность клёва). Подсказка «реже, но крупнее» — с пометкой, когда уловов мало.
    /// </summary>
    public static class PlaceStats
    {
        public sealed class Group
        {
            public string Name = "";
            public string Fish = "";          // 1–2 самых частых вида (для наживки)
            public int N;
            public int AvgG, MaxG;
            public int[] Classes = new int[4]; // уловы по классу 0…3 (неизвестный класс — не входит)
            public int Best = -1;              // лучший известный класс
            public string IconPath = "";
            public DateTime? Last;
        }

        public const int SolidN = 3;           // меньше — «мало уловов»

        public static List<Group> ByBait(IEnumerable<MapCatch> cs) =>
            cs.Where(c => !string.IsNullOrWhiteSpace(c.Bait)).GroupBy(c => c.Bait.Trim()).Select(g => Make(g.Key, g, true))
              .OrderByDescending(x => x.N).ThenByDescending(x => x.AvgG).ToList();

        public static List<Group> ByFish(IEnumerable<MapCatch> cs) =>
            cs.GroupBy(c => c.Fish).Select(g => Make(g.Key, g, false)).OrderByDescending(x => x.N).ThenByDescending(x => x.MaxG).ToList();

        private static Group Make(string name, IEnumerable<MapCatch> src, bool withFish)
        {
            var l = src.ToList();
            var w = l.Where(c => c.Weight > 0).Select(c => c.Weight).ToList();
            var g = new Group
            {
                Name = name, N = l.Count, AvgG = w.Count > 0 ? (int)Math.Round(w.Average()) : 0, MaxG = w.Count > 0 ? w.Max() : 0,
                IconPath = l.First().IconPath,
                Last = l.Select(c => OverlayDataSource.PlausibleTime(c.TimeText)).Where(t => t.HasValue).Select(t => t!.Value).DefaultIfEmpty().Max() is DateTime d && d != default ? d : null,
                Fish = withFish ? string.Join(", ", l.GroupBy(c => c.Fish).OrderByDescending(x => x.Count()).Take(2).Select(x => x.Key)) : ""
            };
            foreach (var c in l) if (c.Windex is int k && k >= 0 && k <= 3) g.Classes[k]++;
            for (int k = 3; k >= 0; k--) if (g.Classes[k] > 0) { g.Best = k; break; }
            return g;
        }

        /// <summary>Подпись строки: «Линь · ср. 1,9 кг · лучший 2,6 кг» (+ «★ трофей» / «мало уловов»).</summary>
        public static string Subtitle(Group g, bool withFish)
        {
            // вес и класс — первыми (их не должно съедать многоточие), виды — в конце
            var parts = new List<string>();
            if (g.N >= 2 && g.AvgG > 0) parts.Add("ср. " + UiKit.Grams(g.AvgG));
            if (g.MaxG > 0) parts.Add((g.N >= 2 ? "лучший " : "") + UiKit.Grams(g.MaxG));
            if (g.Best == 3) parts.Add("★ трофей");
            else if (g.Best >= 0) parts.Add(GradeBadge.ShortName(g.Best));
            if (withFish && g.Fish.Length > 0) parts.Add(g.Fish);
            return string.Join(" · ", parts);
        }

        /// <summary>
        /// «Реже, но крупнее»: наживка с заметно большим средним весом (≥1,4×) или с трофеем, когда у самой частой его нет.
        /// Пусто — подсказывать нечего. При малом числе уловов прямо говорит, что это только повод проверить.
        /// </summary>
        public static string Insight(List<Group> baits)
        {
            if (baits.Count < 2) return "";
            var top = baits[0];
            var heavy = baits.Skip(1).Where(b => b.AvgG > 0).OrderByDescending(b => b.Best == 3 && top.Best < 3 ? 1 : 0).ThenByDescending(b => b.AvgG).FirstOrDefault();
            if (heavy == null || top.AvgG <= 0) return "";
            bool trophy = heavy.Best == 3 && top.Best < 3;
            bool bigger = heavy.AvgG >= top.AvgG * 1.4;
            if (!trophy && !bigger) return "";
            string what = trophy
                ? $"Реже, но крупнее: «{heavy.Name}» — {heavy.N} {Plural(heavy.N)}, есть трофей {UiKit.Grams(heavy.MaxG)}; «{top.Name}» — {top.N} {Plural(top.N)}, ср. {UiKit.Grams(top.AvgG)}"
                : $"Реже, но крупнее: «{heavy.Name}» — {heavy.N} {Plural(heavy.N)}, ср. {UiKit.Grams(heavy.AvgG)}; «{top.Name}» — ср. {UiKit.Grams(top.AvgG)}";
            return what + (heavy.N < SolidN ? ". Уловов мало — повод проверить, не вывод." : ".");
        }

        public static string Plural(int n) { int a = n % 100, b = n % 10; return a is >= 11 and <= 14 ? "уловов" : b == 1 ? "улов" : b is >= 2 and <= 4 ? "улова" : "уловов"; }
    }
}
