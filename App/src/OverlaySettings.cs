using System;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

namespace FisherLogger
{
    public enum OverlayMapMode { Activity = 0, Bite = 1 }
    public enum OverlayPreset { Custom = 0, TopLeft = 1, TopRight = 2, BottomLeft = 3, BottomRight = 4 }

    /// <summary>
    /// Настройки игрового оверлея. Хранятся отдельно от данных сессий:
    /// %LOCALAPPDATA%\FisherLogger\overlay_settings.json (путь можно переопределить FISHER_OVERLAY_SETTINGS — для тестов).
    /// </summary>
    public sealed class OverlaySettings
    {
        public bool Enabled { get; set; } = true;
        public bool HotkeyEnabled { get; set; } = true;
        public int HotkeyKey { get; set; } = (int)Keys.OemPipe;          // VK_OEM_5: клавиша «\» (тот же физический ключ в русской раскладке)
        public int HotkeyModifiers { get; set; } = 0;                     // Keys.Control | Keys.Shift | Keys.Alt
        public double Opacity { get; set; } = 0.85;
        public int X { get; set; } = -1;                                  // -1: по пресету
        public int Y { get; set; } = -1;
        public int Width { get; set; } = 340;
        public int Height { get; set; } = 480;
        public OverlayPreset Preset { get; set; } = OverlayPreset.TopRight;
        public OverlayMapMode Mode { get; set; } = OverlayMapMode.Activity;
        public double RadiusMeters { get; set; } = 100;
        public bool ClickThrough { get; set; } = false;
        public int RecentHours { get; set; } = 0;                         // 0 = всё время
        public bool VisibleOnStart { get; set; } = false;
        /// <summary>NEXT-OVERLAY-02: минимальный вес улова для ПОКАЗА в оверлее (карта и журнал), г. 0 = все. На запись не влияет.</summary>
        public int MinDisplayWeightG { get; set; } = 0;
        /// <summary>NEXT-GTWC-01: нижний список — 0 «Мои уловы», 1 «Лента» (уловы игроков и публичный чат).</summary>
        public int LogTab { get; set; } = 0;
        /// <summary>Строк ленты (вкладка «Лента» выше обычного журнала).</summary>
        public int FeedRows { get; set; } = 8;
        /// <summary>COACH-01: строк во вкладке «Советы».</summary>
        public int CoachRows { get; set; } = 11;
        /// <summary>Показывать оценку игрового времени («≈HH:mm», модель GameClock, вероятная) рядом со временем ПК.</summary>
        public bool FeedShowGameTime { get; set; } = true;
        /// <summary>OVERLAY-03: круглая карта вокруг игрока с полосой координат и панелью поимки (false — прежняя прямоугольная).</summary>
        public bool RoundMap { get; set; } = true;

        public static readonly int[] DisplayWeightPresets = { 0, 10, 25, 50, 100 };
        public const int MinWidth = 260, MaxWidth = 900, MinHeight = 340, MaxHeight = 1200;

        public static string FilePath
        {
            get
            {
                string? env = Environment.GetEnvironmentVariable("FISHER_OVERLAY_SETTINGS");
                if (!string.IsNullOrWhiteSpace(env)) return env;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FisherLogger", "overlay_settings.json");
            }
        }

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public static OverlaySettings Load(Action<string>? log = null)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(FilePath), Json);
                    if (s != null) { s.Normalize(); return s; }
                }
            }
            catch (Exception ex) { log?.Invoke("[OVERLAY] настройки не читаются, используются значения по умолчанию: " + ex.Message); }
            return new OverlaySettings();
        }

        public void Save(Action<string>? log = null)
        {
            try
            {
                Normalize();
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
                File.Move(tmp, FilePath, true);
            }
            catch (Exception ex) { log?.Invoke("[OVERLAY] настройки не сохранены: " + ex.Message); }
        }

        public void Normalize()
        {
            Opacity = Math.Clamp(double.IsFinite(Opacity) ? Opacity : 0.85, 0.3, 1.0);
            Width = Math.Clamp(Width, MinWidth, MaxWidth);
            Height = Math.Clamp(Height, MinHeight, MaxHeight);
            MinDisplayWeightG = Math.Clamp(MinDisplayWeightG, 0, CatchRules.MaxRecordWeightG);
            LogTab = LogTab is 1 or 2 ? LogTab : 0;   // COACH-01: 2 — «Советы»
            FeedRows = Math.Clamp(FeedRows, 3, 20);
            CoachRows = Math.Clamp(CoachRows, 6, 24);
            RadiusMeters = Math.Clamp(double.IsFinite(RadiusMeters) ? RadiusMeters : 100, 10, 2000);
            if (!Enum.IsDefined(Mode)) Mode = OverlayMapMode.Activity;
            if (!Enum.IsDefined(Preset)) Preset = OverlayPreset.TopRight;
            if (RecentHours < 0) RecentHours = 0;
            HotkeyModifiers &= (int)(Keys.Control | Keys.Shift | Keys.Alt);
        }

        public OverlaySettings Clone() => (OverlaySettings)MemberwiseClone();

        public static string HotkeyText(int key, int mods)
        {
            if (key == 0) return "не задана";
            string s = "";
            if ((mods & (int)Keys.Control) != 0) s += "Ctrl+";
            if ((mods & (int)Keys.Shift) != 0) s += "Shift+";
            if ((mods & (int)Keys.Alt) != 0) s += "Alt+";
            return s + ((Keys)key == Keys.OemPipe ? "\\" : ((Keys)key).ToString());
        }
    }
}
