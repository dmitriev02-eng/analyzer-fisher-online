using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace FisherLogger
{
    /// <summary>
    /// Определение P/E-ядер через GetLogicalProcessorInformationEx.
    /// Привязка фонового потока включается только переменной
    /// FISHER_PIN_E_CORES=1, чтобы не испортить поведение на не-гибридных CPU.
    /// </summary>
    public static class CpuTopology
    {
        private const int RelationProcessorCore = 0;
        private const int ErrorInsufficientBuffer = 122;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(
            int relationshipType, IntPtr buffer, ref uint returnedLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr dwThreadAffinityMask);

        public static IReadOnlyList<int> EfficiencyCores => Detect().EfficiencyCores;
        public static int PerformanceCoreCount => Detect().PerformanceCores;

        private sealed class Topology
        {
            public List<int> EfficiencyCores { get; } = new();
            public int PerformanceCores { get; set; }
        }

        private static Topology? _cached;

        private static Topology Detect()
        {
            if (_cached != null) return _cached;
            var result = new Topology();
            var byClass = new Dictionary<byte, List<int>>();

            try
            {
                uint length = 0;
                GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
                if (length == 0) return _cached = result;

                IntPtr buffer = Marshal.AllocHGlobal((int)length);
                try
                {
                    if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                        return _cached = result;

                    int offset = 0;
                    while (offset + 8 <= (int)length)
                    {
                        int relationship = Marshal.ReadInt32(buffer, offset);
                        int size = Marshal.ReadInt32(buffer, offset + 4);
                        if (size <= 0 || offset + size > (int)length) break;

                        if (relationship == RelationProcessorCore && size >= 48)
                        {
                            byte efficiency = Marshal.ReadByte(buffer, offset + 9);
                            ushort groupCount = (ushort)Marshal.ReadInt16(buffer, offset + 30);

                            int groupOffset = offset + 32;
                            for (int g = 0; g < Math.Max(1, (int)groupCount); g++)
                            {
                                if (groupOffset + 16 > offset + size) break;
                                ulong mask = (ulong)Marshal.ReadInt64(buffer, groupOffset);
                                short group = Marshal.ReadInt16(buffer, groupOffset + 8);
                                for (int bit = 0; bit < 64; bit++)
                                {
                                    if ((mask & (1UL << bit)) == 0) continue;
                                    int logical = group * 64 + bit;
                                    if (!byClass.TryGetValue(efficiency, out var list))
                                    {
                                        list = new List<int>();
                                        byClass[efficiency] = list;
                                    }
                                    list.Add(logical);
                                }
                                groupOffset += 16;
                            }
                        }

                        offset += size;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch
            {
                // Не гибридный CPU или API недоступен.
            }

            if (byClass.Count == 0)
            {
                // API не дал данных.
            }
            else if (byClass.Count == 1)
            {
                result.PerformanceCores = byClass.Values.First().Count;
            }
            else
            {
                // На Intel 12th+ P-ядра обычно имеют больше логических
                // процессоров (Hyper-Threading), E-ядра — меньше.
                var ordered = byClass
                    .OrderByDescending(kv => kv.Value.Count)
                    .ThenBy(kv => kv.Key)
                    .ToList();
                result.PerformanceCores = ordered[0].Value.Count;
                foreach (var group in ordered.Skip(1))
                    result.EfficiencyCores.AddRange(group.Value);
            }

            result.EfficiencyCores.Sort();
            return _cached = result;
        }

        /// <summary>Логирует топологию; возвращает true, если E-ядра найдены.</summary>
        public static bool LogTopology(Action<string> log)
        {
            var t = Detect();
            log("[CPU] P-логических: " + t.PerformanceCores +
                ", E-логических: " + t.EfficiencyCores.Count +
                (t.EfficiencyCores.Count > 0
                    ? " (" + string.Join(",", t.EfficiencyCores) + ")"
                    : ""));
            return t.EfficiencyCores.Count > 0;
        }

        /// <summary>Опциональная привязка текущего потока к первому E-ядру.</summary>
        public static bool TryPinCurrentThread(Action<string> log)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("FISHER_PIN_E_CORES"),
                    "1", StringComparison.OrdinalIgnoreCase))
                return false;

            var cores = Detect().EfficiencyCores;
            if (cores.Count == 0)
            {
                log("[CPU] E-ядра не найдены, привязка пропущена");
                return false;
            }

            int core = cores[0];
            if (core >= 64) return false;
            UIntPtr mask = (UIntPtr)(1UL << core);
            UIntPtr previous = SetThreadAffinityMask(GetCurrentThread(), mask);
            bool ok = previous != UIntPtr.Zero;
            log(ok
                ? "[CPU] Поток парсера привязан к E-core " + core
                : "[CPU] Не удалось привязать поток к E-core " + core);
            return ok;
        }
    }
}
