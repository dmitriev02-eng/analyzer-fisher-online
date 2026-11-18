using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FisherLogger
{
    public sealed class PcapImportResult
    {
        public string FilePath { get; init; } = "";
        public int Packets { get; set; }
        public int TcpPayloads { get; set; }
        public int Events { get; set; }
        public TimeSpan Duration { get; set; }
        public List<CatchData> Catches { get; set; } = new();
        public Dictionary<string, int> EventCounts { get; } = new(StringComparer.Ordinal);
        public string Report { get; set; } = "";
        public List<string> Warnings { get; } = new();
    }

    /// <summary>
    /// Импорт записанного PCAP/PCAPNG через те же читалки, что и в PacketDecoder.
    /// События подаются в DeepCatchSession в хронологическом порядке, поэтому
    /// поимка получает координаты/глубину/погоду на момент вылова, а наживку —
    /// из сводки, пришедшей позже.
    /// </summary>
    public static class PcapImportRunner
    {
        public static PcapImportResult Run(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Путь к файлу не задан.", nameof(path));

            var result = new PcapImportResult { FilePath = path };
            var sw = Stopwatch.StartNew();

            byte[] fileBytes = File.ReadAllBytes(path);
            var packets = CaptureFileReader.Read(fileBytes);
            result.Packets = packets.Count;

            var parser = new PacketDeepParser();
            var session = new DeepCatchSession();
            session.Diagnostics += msg =>
            {
                if (msg.StartsWith("[DUP?", StringComparison.Ordinal)) result.Warnings.Add(msg);
            };
            var catches = new List<CatchData>();
            var serverReassembler = new TcpReassembler();
            var clientReassembler = new TcpReassembler();

            session.FullCatchDetected += ev =>
            {
                var data = DeepCatchSession.ToCatchData(ev);
                data.status = "confirmed";
                data.source = "pcap";
                data.autoDetected = !string.IsNullOrEmpty(data.fish_name);
                catches.Add(data);
                session.Register(data);
            };

            foreach (var packet in packets)
            {
                byte[]? payload = TcpPayloadExtractor.Extract(packet.Data, out int srcPort, out _);
                if (payload == null || payload.Length == 0) continue;

                result.TcpPayloads++;
                DateTime ts = packet.Timestamp.HasValue ? packet.Timestamp.Value.DateTime : DateTime.MinValue;
                bool fromServer = srcPort == TcpPayloadExtractor.FisherPort;
                var reassembler = fromServer ? serverReassembler : clientReassembler;

                foreach (var frame in reassembler.Feed(payload))
                {
                    foreach (var ev in parser.ParseFrames(frame, ts))
                    {
                        result.Events++;
                        result.EventCounts.TryGetValue(ev.MessageType, out int count);
                        result.EventCounts[ev.MessageType] = count + 1;
                        session.Process(ev);
                    }
                }
            }

            // Сводки, для которых не нашлось полной поимки, не теряются.
            var orphanCatches = session.TakeOrphanCatches();
            catches.AddRange(orphanCatches);

            catches.Sort((a, b) =>
            {
                int byTime = a.detected_at.CompareTo(b.detected_at);
                return byTime != 0 ? byTime : string.CompareOrdinal(a.timestamp, b.timestamp);
            });

            result.Catches = catches;
            sw.Stop();
            result.Duration = sw.Elapsed;
            result.Report = BuildReport(result);

            return result;
        }

        private static string BuildReport(PcapImportResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Импорт PCAP в FisherLogger ===");
            sb.AppendLine("Файл: " + result.FilePath);
            sb.AppendLine("Пакетов прочитано: " + result.Packets);
            sb.AppendLine("TCP-payload порта " + TcpPayloadExtractor.FisherPort + ": " + result.TcpPayloads);
            sb.AppendLine("Событий разобрано: " + result.Events);

            if (result.EventCounts.Count > 0)
            {
                sb.AppendLine("По типам:");
                foreach (var kv in result.EventCounts.OrderByDescending(x => x.Value).ThenBy(x => x.Key))
                    sb.AppendLine("  " + kv.Key + ": " + kv.Value);
            }

            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("Предупреждения:");
                foreach (var w in result.Warnings) sb.AppendLine("  " + w);
            }

            sb.AppendLine("Поимок: " + result.Catches.Count);
            sb.AppendLine("Время разбора: " + result.Duration.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " мс");
            sb.AppendLine();

            int index = 0;
            foreach (var c in result.Catches)
            {
                index++;
                sb.AppendLine("[" + index + "] " + c.timestamp + "  " + CatchDisplay.Fish(c) +
                              "  " + c.weight + " г  (" + c.grade + ")" +
                              (c.exp > 0 ? "  +" + c.exp + " exp" : ""));

                if (!string.IsNullOrEmpty(c.bait_name) || c.bait_id.HasValue)
                    sb.AppendLine("    наживка: " + CatchDisplay.Bait(c) +
                                  (c.bait_id.HasValue ? " (ID " + c.bait_id + ")" : "") +
                                  (string.IsNullOrEmpty(c.bait_category) ? "" : ", " + c.bait_category));

                if (c.depth_cm.HasValue)
                    sb.AppendLine("    глубина: " + (c.depth_cm.Value / 100f).ToString("F2", CultureInfo.InvariantCulture) + " м (" +
                                  c.depth_cm.Value.ToString("F1", CultureInfo.InvariantCulture) + " см)");

                if (c.cast_x.HasValue && c.cast_y.HasValue && c.cast_z.HasValue)
                    sb.AppendLine("    заброс: X=" + c.cast_x.Value.ToString("F2", CultureInfo.InvariantCulture) +
                                  ", Y=" + c.cast_y.Value.ToString("F2", CultureInfo.InvariantCulture) +
                                  ", Z=" + c.cast_z.Value.ToString("F2", CultureInfo.InvariantCulture));

                if (c.air_temperature_c.HasValue || c.wind_ms.HasValue || c.humidity.HasValue)
                    sb.AppendLine("    погода: " +
                                  (c.air_temperature_c.HasValue ? "t=" + c.air_temperature_c.Value.ToString("F1", CultureInfo.InvariantCulture) + "°C " : "") +
                                  (c.wind_ms.HasValue ? "ветер=" + c.wind_ms.Value.ToString("F1", CultureInfo.InvariantCulture) + " м/с " : "") +
                                  (c.humidity.HasValue ? "влажность=" + c.humidity.Value.ToString("F0", CultureInfo.InvariantCulture) + "%" : ""));

                sb.AppendLine("    сетевой ID: " + (c.offset_32?.ToString() ?? "N/A") +
                              ", lang ID: " + (c.fish_lang_id?.ToString() ?? "N/A") +
                              ", quality: " + (c.quality?.ToString() ?? "N/A"));
            }

            return sb.ToString();
        }
    }
}
