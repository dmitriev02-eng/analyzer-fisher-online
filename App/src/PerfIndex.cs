using System;
using System.Collections.Generic;

namespace FisherLogger
{
    // =====================================================================================
    // PRERELEASE-01: быстрые поиски вместо перебора «каждый с каждым» (аудит DeepSeek T20261113-1920-scale).
    // Результаты те же, что у прежнего перебора: порядок исходного списка сохраняется, совпадения проверяются тем же условием.
    // =====================================================================================

    /// <summary>Точки на плоскости X/Z, разложенные по клеткам размером tol. Поиск «первой в исходном порядке точки,
    /// у которой |dx| &lt; tol и |dz| &lt; tol» — как List.FirstOrDefault, но без перебора всего списка.</summary>
    public sealed class PointGrid<T>
    {
        private readonly double _cell;
        private readonly Dictionary<(long, long), List<(int idx, double x, double z, T item)>> _b = new();
        public PointGrid(IEnumerable<T> items, Func<T, double> x, Func<T, double> z, double tol)
        {
            _cell = tol > 0 ? tol : 1;
            int i = 0;
            foreach (var it in items)
            {
                double px = x(it), pz = z(it);
                var k = Key(px, pz);
                if (!_b.TryGetValue(k, out var l)) _b[k] = l = new();
                l.Add((i++, px, pz, it));
            }
        }
        private (long, long) Key(double x, double z) => ((long)Math.Floor(x / _cell), (long)Math.Floor(z / _cell));

        /// <summary>Первая (по исходному порядку) точка с |dx| &lt; tol и |dz| &lt; tol; default — нет такой.</summary>
        public T? First(double x, double z, double tol)
        {
            var (bx, bz) = Key(x, z);
            int best = int.MaxValue; T? res = default;
            for (long dx = -1; dx <= 1; dx++)
                for (long dz = -1; dz <= 1; dz++)
                    if (_b.TryGetValue((bx + dx, bz + dz), out var l))
                        foreach (var p in l)
                            if (p.idx < best && Math.Abs(p.x - x) < tol && Math.Abs(p.z - z) < tol) { best = p.idx; res = p.item; }
            return res;
        }
        public bool Any(double x, double z, double tol) => FirstIndex(x, z, tol) >= 0;
        private int FirstIndex(double x, double z, double tol)
        {
            var (bx, bz) = Key(x, z);
            int best = -1;
            for (long dx = -1; dx <= 1; dx++)
                for (long dz = -1; dz <= 1; dz++)
                    if (_b.TryGetValue((bx + dx, bz + dz), out var l))
                        foreach (var p in l)
                            if ((best < 0 || p.idx < best) && Math.Abs(p.x - x) < tol && Math.Abs(p.z - z) < tol) best = p.idx;
            return best;
        }
    }

    /// <summary>Поиск по времени в списке, отсортированном по возрастанию времени.</summary>
    public static class TimeSearch
    {
        /// <summary>Индекс первого элемента с t ≥ from (или Count).</summary>
        public static int LowerBound<T>(IReadOnlyList<T> sorted, Func<T, DateTime> t, DateTime from)
        {
            int lo = 0, hi = sorted.Count;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (t(sorted[mid]) < from) lo = mid + 1; else hi = mid; }
            return lo;
        }
        /// <summary>Индекс первого элемента с t &gt; to (или Count).</summary>
        public static int UpperBound<T>(IReadOnlyList<T> sorted, Func<T, DateTime> t, DateTime to)
        {
            int lo = 0, hi = sorted.Count;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (t(sorted[mid]) <= to) lo = mid + 1; else hi = mid; }
            return lo;
        }
    }
}
