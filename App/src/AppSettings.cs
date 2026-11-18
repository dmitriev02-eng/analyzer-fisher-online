using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FisherLogger
{
    // =====================================================================================
    // ADVISOR-01: общие настройки программы (аналитика-советник и ИИ-агент) в одном файле
    // %LOCALAPPDATA%\FisherLogger\app_settings.json. Настройки оверлея и телефона остаются в своих
    // файлах (overlay_settings.json, companion_settings.json) и открываются из окна «Настройки».
    // API-ключ агента хранится только зашифрованным DPAPI (CurrentUser) — открытым текстом не пишется.
    // =====================================================================================

    public sealed class AnalyticsSettings
    {
        /// <summary>Минимум забросов с известным исходом, чтобы давать совет по месту/наживке/времени.</summary>
        public int MinAttemptsForAdvice { get; set; } = 15;
        /// <summary>Минимум поимок вида в группе, чтобы давать совет.</summary>
        public int MinCatchesForAdvice { get; set; } = 3;
        /// <summary>Формула клёва: минимум забросов с известным исходом, поклёвок и забросов без поклёвки.</summary>
        public int MinLabeledForFormula { get; set; } = 200;
        public int MinPositivesForFormula { get; set; } = 30;
        public int MinNegativesForFormula { get; set; } = 30;
        /// <summary>Насколько формула должна быть лучше «среднего по всем забросам» (доля улучшения log-loss).</summary>
        public double MinImprovement { get; set; } = 0.02;
        /// <summary>Сколько новых забросов нужно для проверки формулы на данных, которых она не видела.</summary>
        public int ForwardTestMin { get; set; } = 30;
        public bool AutoRetrain { get; set; } = true;
        public int RetrainEveryNewAttempts { get; set; } = 20;
        public double ZoneCellMeters { get; set; } = 20;
        public double GroundbaitRadiusM { get; set; } = 5;
        /// <summary>ZONES-02: радиус «места» в советнике (круг вокруг точки наживки), м. 5 м — пятно прикорма игры.</summary>
        public double PlaceRadiusM { get; set; } = 5;
        public int GroundbaitWindowMin { get; set; } = 30;

        public void Normalize()
        {
            MinAttemptsForAdvice = Math.Clamp(MinAttemptsForAdvice, 3, 1000);
            MinCatchesForAdvice = Math.Clamp(MinCatchesForAdvice, 1, 1000);
            MinLabeledForFormula = Math.Clamp(MinLabeledForFormula, 50, 100000);
            MinPositivesForFormula = Math.Clamp(MinPositivesForFormula, 10, 100000);
            MinNegativesForFormula = Math.Clamp(MinNegativesForFormula, 10, 100000);
            if (double.IsNaN(MinImprovement)) MinImprovement = 0.02;
            MinImprovement = Math.Clamp(MinImprovement, 0.005, 0.5);
            ForwardTestMin = Math.Clamp(ForwardTestMin, 10, 10000);
            RetrainEveryNewAttempts = Math.Clamp(RetrainEveryNewAttempts, 5, 10000);
            if (double.IsNaN(ZoneCellMeters)) ZoneCellMeters = 20;
            ZoneCellMeters = Math.Clamp(ZoneCellMeters, 5, 200);
            if (double.IsNaN(GroundbaitRadiusM)) GroundbaitRadiusM = 5;
            GroundbaitRadiusM = Math.Clamp(GroundbaitRadiusM, 1, 50);
            GroundbaitWindowMin = Math.Clamp(GroundbaitWindowMin, 1, 240);
            if (double.IsNaN(PlaceRadiusM) || PlaceRadiusM <= 0) PlaceRadiusM = 5;
            PlaceRadiusM = Math.Clamp(PlaceRadiusM, 2, 50);
        }
    }

    public enum AgentProvider { OpenAiCompatible = 0, Anthropic = 1 }

    public sealed class AgentSettings
    {
        public bool Enabled { get; set; }
        public AgentProvider Provider { get; set; } = AgentProvider.OpenAiCompatible;
        public string BaseUrl { get; set; } = "https://api.deepseek.com";
        public string Model { get; set; } = "deepseek-chat";
        /// <summary>API-ключ, зашифрованный DPAPI (CurrentUser), base64. Пусто — ключа нет.</summary>
        public string ApiKeyDpapi { get; set; } = "";
        public int MaxToolCalls { get; set; } = 6;
        public int TimeoutSeconds { get; set; } = 60;
        public double Temperature { get; set; } = 0.2;
        // COACH-01: экономия токенов и разбор сессии
        /// <summary>Сколько токенов в сутки можно потратить на все запросы агента (0 — без ограничения не допускается, минимум 2000).</summary>
        public int DailyTokenBudget { get; set; } = 50000;
        /// <summary>Предел длины ответа агента в чате, токенов.</summary>
        public int MaxOutputTokens { get; set; } = 700;
        /// <summary>Разбор сессии сам по себе (в оверлее «Советы»), не чаще чем раз в CoachIntervalMin и только когда что-то изменилось.</summary>
        public bool CoachAuto { get; set; } = false;
        public int CoachIntervalMin { get; set; } = 15;
        public int CoachMaxTokens { get; set; } = 350;

        [JsonIgnore] public bool HasKey => !string.IsNullOrEmpty(ApiKeyDpapi);
        public void SetApiKey(string? key) => ApiKeyDpapi = string.IsNullOrWhiteSpace(key) ? "" : Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes(key.Trim())));   // Dpapi — CompanionServer.cs
        public string? GetApiKey() { try { return HasKey ? Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(ApiKeyDpapi))) : null; } catch { return null; } }

        public void Normalize()
        {
            BaseUrl = (BaseUrl ?? "").Trim();
            Model = (Model ?? "").Trim();
            MaxToolCalls = Math.Clamp(MaxToolCalls, 1, 20);
            TimeoutSeconds = Math.Clamp(TimeoutSeconds, 10, 300);
            if (double.IsNaN(Temperature)) Temperature = 0.2;
            Temperature = Math.Clamp(Temperature, 0, 1.5);
            DailyTokenBudget = Math.Clamp(DailyTokenBudget, 2000, 2_000_000);
            MaxOutputTokens = Math.Clamp(MaxOutputTokens, 200, 4000);
            CoachIntervalMin = Math.Clamp(CoachIntervalMin, 5, 240);
            CoachMaxTokens = Math.Clamp(CoachMaxTokens, 100, 2000);
        }

        public static (string url, string model) Preset(string name) => name switch
        {
            "deepseek" => ("https://api.deepseek.com", "deepseek-chat"),
            "openai" => ("https://api.openai.com/v1", ""),
            "anthropic" => ("https://api.anthropic.com", "claude-sonnet-5-5"),
            "openrouter" => ("https://openrouter.ai/api/v1", ""),
            "local" => ("http://127.0.0.1:11434/v1", ""),
            _ => ("", "")
        };
    }

    /// <summary>MASTER-01: выбранная цель Советника (какую рыбу, какого класса и где хочет поймать игрок).</summary>
    public sealed class GoalSettings
    {
        public uint Fish { get; set; }
        public int MinClass { get; set; } = 2;
        public uint? Location { get; set; }
    }

    public sealed class AppSettings
    {
        public int Version { get; set; } = 1;
        public AnalyticsSettings Analytics { get; set; } = new();
        public AgentSettings Agent { get; set; } = new();
        public GoalSettings Goal { get; set; } = new();
        /// <summary>PRERELEASE-01: что показывать в списках, когда уловов много (только вид, не статистика).</summary>
        public DisplaySettings Display { get; set; } = new();
        /// <summary>NOTIFY-02: окна о поклёвке по удочкам, звуки.</summary>
        public NotifySettings Notify { get; set; } = new();
        /// <summary>NOTIFY-02: трей и клавиша главного окна.</summary>
        public WindowSettings Window { get; set; } = new();
        /// <summary>MASTER-01: простой вид — без вкладок и подписей для аналитика.</summary>
        public bool SimpleMode { get; set; } = true;

        /// <summary>Только для тестов: другой путь файла.</summary>
        public static string? FilePathOverride { get; set; }
        public static string FilePath => FilePathOverride ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FisherLogger", "app_settings.json");

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        public static AppSettings Load(Action<string>? log = null)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
                    if (s != null) { s.Analytics ??= new(); s.Agent ??= new(); s.Goal ??= new(); s.Display ??= new(); s.Notify ??= new(); s.Window ??= new(); s.Notify.Normalize(); s.Window.Normalize(); s.Analytics.Normalize(); s.Agent.Normalize(); s.Display.Normalize(); return s; }
                }
            }
            catch (Exception ex) { log?.Invoke("[SETTINGS] Не удалось прочитать " + FilePath + ": " + ex.Message + " — используются значения по умолчанию"); }
            return new AppSettings();
        }

        public void Save(Action<string>? log = null)
        {
            try
            {
                Analytics.Normalize(); Agent.Normalize(); (Display ??= new()).Normalize(); (Notify ??= new()).Normalize(); (Window ??= new()).Normalize();
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
                File.Move(tmp, FilePath, true);
            }
            catch (Exception ex) { log?.Invoke("[SETTINGS] Не удалось сохранить настройки: " + ex.Message); throw; }
        }

        public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json), Json)!;
    }
}
