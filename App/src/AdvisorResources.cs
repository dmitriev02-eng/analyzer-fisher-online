using System;
using System.IO;
using System.Text;

namespace FisherLogger
{
    // ADVISOR-02 (R4): единый ленивый загрузчик справочных ресурсов «Советника».
    // Порядок для каждого ресурса:
    //   1) <FISHER_RESOURCES_DIR>/<file> — переопределение из каталога (QA/тесты/горячая правка);
    //   2) embedded-ресурс сборки (LogicalName "FisherLogger.resources.*");
    //   3) для fish_full_database.json — runtime-файл словарей (он не встраивается). FIX-03: сначала каталог словарей
    //      приложения (PacketDeepParser.ResolveDefaultDataDir() = D:\TheFisherProject\Data — там файл лежит на самом деле),
    //      затем AppPaths.FishFullDatabasePath (Sessions\default\data — там файла нет; без п. 3а сопоставление по id не работало).
    // Исключения наружу НЕ бросаются: нет файла / битый JSON → null + один WARN (через Warn).
    public static class AdvisorResources
    {
        public const string ReferenceResource = "FisherLogger.resources.fishing_reference.json";
        public const string WeightsResource = "FisherLogger.resources.fh_fish_weights.json";

        /// <summary>Необязательный приёмник предупреждений (иначе деградация молчаливая).</summary>
        public static Action<string>? Warn;

        private static readonly Lazy<string?> _reference = new(() => Load("fishing_reference.json", ReferenceResource, useAppPath: false));
        private static readonly Lazy<string?> _weights = new(() => Load("fh_fish_weights.json", WeightsResource, useAppPath: false));
        private static readonly Lazy<string?> _fishFull = new(() => Load("fish_full_database.json", embeddedName: null, useAppPath: true));

        public static string? LoadReference() => _reference.Value;
        public static string? LoadWeights() => _weights.Value;
        public static string? LoadFishFullDatabase() => _fishFull.Value;

        private static string? Load(string fileName, string? embeddedName, bool useAppPath)
        {
            string? dir = Environment.GetEnvironmentVariable("FISHER_RESOURCES_DIR");
            if (!string.IsNullOrWhiteSpace(dir))
            {
                string disk = Path.Combine(dir, fileName);
                if (File.Exists(disk))
                {
                    try { return File.ReadAllText(disk, Encoding.UTF8); }
                    catch (Exception ex) { WarnOnce(fileName + " (override): " + ex.Message); return null; }
                }
            }

            if (!string.IsNullOrEmpty(embeddedName))
            {
                try
                {
                    using Stream? s = typeof(AdvisorResources).Assembly.GetManifestResourceStream(embeddedName);
                    if (s != null) using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
                }
                catch (Exception ex) { WarnOnce(embeddedName + " (embedded): " + ex.Message); return null; }
            }

            if (useAppPath)
            {
                string dict;
                try { dict = Path.Combine(PacketDeepParser.ResolveDefaultDataDir(), fileName); } catch { dict = ""; }
                foreach (string p in new[] { dict, AppPaths.FishFullDatabasePath })
                {
                    if (p.Length == 0 || !File.Exists(p)) continue;
                    try { return File.ReadAllText(p, Encoding.UTF8); }
                    catch (Exception ex) { WarnOnce(p + ": " + ex.Message); return null; }
                }
            }

            WarnOnce(fileName + ": ресурс недоступен");
            return null;
        }

        private static readonly System.Collections.Generic.HashSet<string> _warned = new();
        private static void WarnOnce(string msg)
        {
            bool first;
            lock (_warned) { first = _warned.Add(msg); }
            if (first) Warn?.Invoke("[ADVISOR-02] " + msg);
        }
    }
}
