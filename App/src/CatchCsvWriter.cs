using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FisherLogger
{
    /// <summary>
    /// Экспорт поимок в CSV. Файл всегда перезаписывается целиком (записей
    /// немного), кодировка — UTF-8 с BOM, разделитель «;» (удобно для Excel
    /// с русской локалью). Все текстовые поля экранируются.
    /// </summary>
    public static class CatchCsvWriter
    {
        public const char Separator = ';';

        private const string Header =
            "record_id;timestamp;fish;weight_g;grade;exp;network_id;fish_lang_id;" +
            "bait_id;bait_name;bait_category;depth_m;depth_cm;cast_x;cast_y;cast_z;" +
            "air_temp_c;wind_ms;humidity_pct;quality;location_id;status;source;experience_formula";

        public static void Write(string path, IEnumerable<CatchData> catches)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Путь к CSV не задан.", nameof(path));

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine(Header);

            foreach (var c in catches
                         .OrderBy(c => c.timestamp, StringComparer.Ordinal)
                         .ThenBy(c => c.record_id, StringComparer.Ordinal))
            {
                sb.AppendLine(string.Join(Separator.ToString(), new[]
                {
                    Esc(c.record_id),
                    Esc(c.timestamp),
                    Esc(c.fish_name),
                    Int(c.weight),
                    Esc(c.grade),
                    Int(c.exp),
                    Int(c.offset_32),
                    UInt(c.fish_lang_id),
                    UInt(c.bait_id),
                    Esc(c.bait_name),
                    Esc(c.bait_category),
                    DepthMeters(c.depth_cm),
                    Float(c.depth_cm, "F1"),
                    Double(c.cast_x, "F2"),
                    Double(c.cast_y, "F2"),
                    Double(c.cast_z, "F2"),
                    Float(c.air_temperature_c, "F1"),
                    Float(c.wind_ms, "F1"),
                    Float(c.humidity, "F1"),
                    Int(c.quality),
                    UInt(c.location_id),
                    Esc(c.status),
                    Esc(c.source),
                    Esc(c.experience_formula)
                }));
            }

            // Атомарная замена: не оставляем полузаписанный CSV при сбое.
            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, sb.ToString(), new UTF8Encoding(true));
            File.Move(tempPath, path, overwrite: true);
        }

        private static string DepthMeters(float? depthCm)
            => depthCm.HasValue
                ? (depthCm.Value / 100f).ToString("F2", CultureInfo.InvariantCulture)
                : "";

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Int(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
        private static string UInt(uint? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

        private static string Float(float? value, string format)
            => value?.ToString(format, CultureInfo.InvariantCulture) ?? "";

        private static string Double(double? value, string format)
            => value?.ToString(format, CultureInfo.InvariantCulture) ?? "";

        private static string Esc(string? value)
        {
            string text = value ?? "";
            if (!text.Contains('"') && !text.Contains(Separator) && !text.Contains('\r') && !text.Contains('\n'))
                return text;

            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
    }
}
