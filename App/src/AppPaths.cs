using System;
using System.IO;

namespace FisherLogger
{
    /// <summary>Единые пути к рабочим файлам логгера.</summary>
    public static class AppPaths
    {
        public static string BaseDirectory { get; set; } = AppInfo.DefaultSessionDir(); // RELEASE-02: на ПК разработчика прежняя папка, у остальных %LOCALAPPDATA%\FisherLogger\session // NEXT-95: isolated staging path (was D:\FisherLogger)

        public static string LogsDirectory => Path.Combine(BaseDirectory, "logs");
        public static string DataDirectory => Path.Combine(BaseDirectory, "data");
        public static string CatchCsv => Path.Combine(LogsDirectory, "catches.csv");
        public static string FishStatsCsv => Path.Combine(LogsDirectory, "fish_stats.csv");
        public static string OtherPlayersStatsCsv => Path.Combine(LogsDirectory, "other_players_stats.csv");
        public static string AnalysisCsv => Path.Combine(LogsDirectory, "analysis_export.csv");
        public static string CatchDb => Path.Combine(DataDirectory, "catches.db");

        // Новые полные базы из декомпиляции.
        public static string FishFullDatabasePath => Path.Combine(DataDirectory, "fish_full_database.json");
        public static string ItemsFullDatabasePath => Path.Combine(DataDirectory, "items_full_database.json");
        public static string LocationMapPath => Path.Combine(DataDirectory, "location_map.json");
        public static string BaitModifiersPath => Path.Combine(DataDirectory, "bait_modifiers.json");

        // DESIGN: shared, read-only assets (fish_icons, bait_icons, location_images, maps).
        // Resolved independently of the session (--data-dir) and of the working directory:
        //   1) FISHER_ASSETS_DIR; 2) <dictionary dir>\assets (D:\TheFisherProject\Data\assets, or data\ next to the exe);
        //   3) <session>\assets (legacy per-session copy).
        private static string? _assetsDirectory;
        public static string AssetsDirectory
        {
            get
            {
                if (_assetsDirectory != null) return _assetsDirectory;
                string? env = Environment.GetEnvironmentVariable("FISHER_ASSETS_DIR");
                if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return _assetsDirectory = env;
                string shared = Path.Combine(PacketDeepParser.ResolveDefaultDataDir(), "assets");
                if (Directory.Exists(shared)) return _assetsDirectory = shared;
                return Path.Combine(BaseDirectory, "assets"); // not cached: follows --data-dir
            }
            set => _assetsDirectory = value;
        }

        public static string SessionLog(DateTime time)
            => Path.Combine(LogsDirectory, "session_" + time.ToString("yyyy-MM-dd") + ".log");

        public static void EnsureLogsDirectory()
        {
            Directory.CreateDirectory(LogsDirectory);
        }

        public static void EnsureDataDirectory()
        {
            Directory.CreateDirectory(DataDirectory);
        }
    }
}
