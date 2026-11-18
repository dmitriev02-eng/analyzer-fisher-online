using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FisherLogger
{
    public sealed class ReplayResult
    {
        public string FilePath { get; init; } = "";
        public int Packets { get; set; }
        public int Payloads { get; set; }
        public int ServerPayloads { get; set; }
        public int Pending { get; set; }
        public int Waiting { get; set; }
        public List<CatchData> Catches { get; set; } = new();
        public string Report { get; set; } = "";
    }

    /// <summary>
    /// Прогоняет PCAP через тот же код, что работает в реалтайме
    /// (буферизация, дедупликация глубокого парсера и базового детектора).
    /// Нужен для проверки интеграции без запуска захвата и без GUI.
    /// </summary>
    public static class HeadlessReplay
    {
        public static int Run(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    Console.WriteLine("Файл не найден: " + path);
                    return 2;
                }

                using var form = new Form1(startServices: false);
                var result = form.ReplayPcap(path);

                AppPaths.EnsureLogsDirectory();
                string baseName = Path.GetFileNameWithoutExtension(path);
                string csvPath = Path.Combine(AppPaths.LogsDirectory, "replay_" + baseName + ".csv");
                string reportPath = Path.Combine(AppPaths.LogsDirectory, "replay_" + baseName + ".txt");

                CatchCsvWriter.Write(csvPath, result.Catches);
                File.WriteAllText(reportPath, result.Report, new UTF8Encoding(false));

                Console.WriteLine(result.Report);
                Console.WriteLine("CSV: " + csvPath);
                Console.WriteLine("Отчёт: " + reportPath);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                try
                {
                    AppPaths.EnsureLogsDirectory();
                    File.WriteAllText(Path.Combine(AppPaths.LogsDirectory, "replay_error.txt"), ex.ToString(),
                        new UTF8Encoding(false));
                }
                catch { }
                return 1;
            }
        }
    }

    internal static class ReplayReport
    {
        public static string Build(ReplayResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Реалтайм-реплей PCAP (Form1.ProcessPayload) ===");
            sb.AppendLine("Файл: " + result.FilePath);
            sb.AppendLine("Пакетов: " + result.Packets);
            sb.AppendLine("TCP-payload 9205: " + result.Payloads + " (от сервера: " + result.ServerPayloads + ")");
            sb.AppendLine("Поимок найдено: " + result.Catches.Count +
                          " (в очереди: " + result.Pending + ", ожидают: " + result.Waiting + ")");
            sb.AppendLine();

            int index = 0;
            foreach (var c in result.Catches)
            {
                index++;
                sb.AppendLine("[" + index + "] " + c.timestamp + "  " +
                              CatchDisplay.Fish(c) +
                              "  " + c.weight + " г  (" + c.grade + ")" +
                              (c.exp > 0 ? "  +" + c.exp + " exp" : ""));

                if (!string.IsNullOrEmpty(c.bait_name) || c.bait_id.HasValue)
                    sb.AppendLine("    наживка: " + CatchDisplay.Bait(c) +
                                  (c.bait_id.HasValue ? " (ID " + c.bait_id + ")" : ""));

                if (c.depth_cm.HasValue)
                    sb.AppendLine("    глубина: " + (c.depth_cm.Value / 100f).ToString("F2", CultureInfo.InvariantCulture) + " м");

                if (c.cast_x.HasValue && c.cast_y.HasValue && c.cast_z.HasValue)
                    sb.AppendLine("    заброс: X=" + c.cast_x.Value.ToString("F2", CultureInfo.InvariantCulture) +
                                  ", Y=" + c.cast_y.Value.ToString("F2", CultureInfo.InvariantCulture) +
                                  ", Z=" + c.cast_z.Value.ToString("F2", CultureInfo.InvariantCulture));

                sb.AppendLine("    источник: " + c.source + ", статус: " + c.status +
                              ", auto: " + c.autoDetected);
            }

            return sb.ToString();
        }
    }
}
