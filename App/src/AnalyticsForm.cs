using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;

namespace FisherLogger
{
    // =====================================================================================
    // NEXT-ANALYTICS-01: окно «Аналитика» — распределение уловов по 24 игровым часам.
    //
    // Честность данных:
    //  * Две серии никогда не складываются: «мои уловы» (таблица catches) и «публичные объявления»
    //    (таблица public_catches, события cmd 6/5). Каждая считается и рисуется отдельно.
    //  * Время своих уловов: catches.timestamp — локальное время ПК в момент приёма пакета
    //    (Form1: _packetQueue.Enqueue(..., DateTime.Now, ...)) или чтения строки Player.log
    //    (FishLogMonitor, опрос 500 мс). Игровое время считается при чтении функцией GameClock
    //    без смещения сервера (Estimate(t, null) → погрешность Base+NoServer = 3 игровые минуты).
    //    Записи с неправдоподобной датой (год < 2000 или в будущем — например, 3995 из импорта PCAPNG)
    //    в график не попадают и считаются как «без достоверного времени». Хранимые данные не меняются.
    //  * Время публичных объявлений: хранимые game_time_est / game_time_unc_min / game_time_model
    //    (рассчитаны при приёме по времени сервера). Если оценки нет, но есть server_time — та же
    //    GameClock.GameTimeOfDay; если нет ни того, ни другого — «без времени».
    //  * Водоём публичного объявления — receiver_location_id: где были ВЫ при приёме (в пакете места нет).
    //  * Вероятность клёва, процент успеха и CPUE не считаются: знаменателей (попыток/экспозиции) нет.
    //    Полоса «наблюдение» — только сколько реального времени шла запись пакетов (по таблице weather).
    // =====================================================================================

    public sealed class AnalyticsRecord
    {
        public bool IsMine;
        public string Session = "";
        public string Key = "";
        public string RealText = "";
        public DateTime? Real;            // правдоподобное реальное время (локальное), иначе null
        public DateTime? Server;
        public int? GameMinute;           // 0..1439, null — игровое время недоступно
        public string GameText = "";
        public int UncMin;
        public string Model = "";
        public string TimeIssue = "";     // причина, если игровое время недоступно
        public uint? FishId;
        public string Fish = "";
        public int Weight;
        public uint? LocationId;
        public long? PlayerId;
        public string Player = "";
        public bool MatchedOwn;           // публичное объявление совпало с моим уловом (вид+вес, ±120 с)

        public int? Hour => GameMinute.HasValue ? GameMinute.Value / 60 : null;
        public string SpeciesKey => FishId.HasValue ? "id:" + FishId.Value : "n:" + Fish.Trim().ToLowerInvariant();

        /// <summary>Оценка находится в пределах погрешности от границы игрового часа (может относиться к соседнему).</summary>
        public bool NearHourBoundary
        {
            get
            {
                if (!GameMinute.HasValue) return false;
                int m = GameMinute.Value % 60;
                return m < UncMin || 60 - m <= UncMin;
            }
        }
    }

    public sealed class AnalyticsDataset
    {
        public List<AnalyticsRecord> Mine = new();
        public List<AnalyticsRecord> Public = new();
        /// <summary>Отрезки записи пакетов: (начало, длительность, с) по соседним строкам weather (интервал ≤ 30 с).</summary>
        public List<(DateTime start, double seconds)> Coverage = new();
        public List<string> Errors = new();
        public List<string> SourcesRead = new();
        public int ExcludedStatus;        // skipped / not_landed — не успешные поимки
        public int DuplicateMine;
        public int DuplicatePublic;
        public string LocationName(uint? id, PacketDeepParser? parser)
        {
            if (!id.HasValue) return "водоём не записан";
            var reg = MapRegistry.Get(id);
            string n = reg?.Name ?? parser?.GetLocation(id)?.Name ?? "";
            return (n.Length > 0 ? n : "loc") + " (loc " + id.Value + ")";
        }
    }

    public static class AnalyticsLoader
    {
        public const double OwnMatchSeconds = 120;
        public const double CoverageMaxGapSeconds = 30;

        public static AnalyticsDataset Load(IEnumerable<(string session, string dbPath)> sources, PacketDeepParser? parser)
        {
            var ds = new AnalyticsDataset();
            var seenMine = new HashSet<string>(StringComparer.Ordinal);
            var seenPub = new HashSet<string>(StringComparer.Ordinal);
            var weatherTimes = new SortedSet<DateTime>();

            foreach (var (session, dbPath) in sources)
            {
                if (!File.Exists(dbPath)) continue;
                try
                {
                    using var con = new SqliteConnection("Data Source=" + dbPath + ";Mode=ReadOnly;Pooling=False");
                    con.Open();
                    ds.SourcesRead.Add(session);

                    if (TableExists(con, "catches"))
                    {
                        using var cmd = new SqliteCommand("SELECT * FROM catches", con);
                        using var r = cmd.ExecuteReader();
                        while (r.Read())
                        {
                            string status = S(r, "status");
                            if (status == "skipped" || status == "not_landed") { ds.ExcludedStatus++; continue; }
                            string rid = S(r, "record_id");
                            string key = rid.Length > 0 ? rid : session + "#" + (L(r, "id") ?? 0);
                            if (!seenMine.Add(key)) { ds.DuplicateMine++; continue; }

                            var a = new AnalyticsRecord
                            {
                                IsMine = true, Session = session, Key = key,
                                RealText = S(r, "timestamp"),
                                FishId = U(r, "fish_lang_id"), Fish = S(r, "fish_name"),
                                Weight = (int)(L(r, "weight") ?? 0),
                                LocationId = U(r, "location_id") ?? U(r, "baseid")
                            };
                            ResolveFish(a, parser);
                            a.Real = OverlayDataSource.PlausibleTime(a.RealText);
                            if (a.Real.HasValue)
                            {
                                var (text, unc) = GameClock.Estimate(a.Real.Value, null);
                                a.GameMinute = (int)Math.Floor(GameClock.GameTimeOfDay(a.Real.Value).TotalMinutes) % 1440;
                                a.GameText = text;
                                a.UncMin = unc;
                                a.Model = GameClock.ModelVersion;
                            }
                            else a.TimeIssue = string.IsNullOrWhiteSpace(a.RealText) ? "нет времени" : "неправдоподобная дата «" + a.RealText + "»";
                            ds.Mine.Add(a);
                        }
                    }

                    if (TableExists(con, "public_catches"))
                    {
                        using var cmd = new SqliteCommand("SELECT * FROM public_catches", con);
                        using var r = cmd.ExecuteReader();
                        while (r.Read())
                        {
                            string sk = S(r, "source_key");
                            string key = sk.Length > 0 ? sk : session + "#" + (L(r, "id") ?? 0);
                            if (!seenPub.Add(key)) { ds.DuplicatePublic++; continue; }

                            var a = new AnalyticsRecord
                            {
                                IsMine = false, Session = session, Key = key,
                                RealText = S(r, "received_at"),
                                FishId = U(r, "fish_lang_id"), Fish = S(r, "fish_name"),
                                Weight = (int)(L(r, "weight") ?? 0),
                                LocationId = U(r, "receiver_location_id"),
                                PlayerId = L(r, "player_id"), Player = S(r, "player_name"),
                                Server = OverlayDataSource.ParseTime(S(r, "server_time"))
                            };
                            ResolveFish(a, parser);
                            a.Real = OverlayDataSource.PlausibleTime(a.RealText);
                            string est = S(r, "game_time_est");
                            long? unc = L(r, "game_time_unc_min");
                            if (!a.Real.HasValue)
                                a.TimeIssue = "неправдоподобная дата приёма «" + a.RealText + "»";
                            else if (TryParseHm(est, out int gm))
                            {
                                a.GameMinute = gm;
                                a.GameText = est.Trim();
                                a.UncMin = (int)(unc ?? GameClock.BaseUncertaintyMinutes);
                                a.Model = S(r, "game_time_model");
                            }
                            else if (a.Server.HasValue)
                            {
                                var t = GameClock.GameTimeOfDay(a.Server.Value);
                                a.GameMinute = (int)Math.Floor(t.TotalMinutes) % 1440;
                                a.GameText = GameClock.Format(t);
                                a.UncMin = GameClock.BaseUncertaintyMinutes;
                                a.Model = GameClock.ModelVersion;
                            }
                            else a.TimeIssue = "нет оценки игрового времени и времени сервера";
                            ds.Public.Add(a);
                        }
                    }

                    if (TableExists(con, "weather"))
                    {
                        using var cmd = new SqliteCommand("SELECT timestamp FROM weather", con);
                        using var r = cmd.ExecuteReader();
                        while (r.Read())
                        {
                            var t = OverlayDataSource.PlausibleTime(r.IsDBNull(0) ? null : Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture));
                            if (t.HasValue) weatherTimes.Add(t.Value); // одинаковые строки из копий сессий схлопываются
                        }
                    }
                }
                catch (Exception ex)
                {
                    ds.Errors.Add(session + ": " + ex.Message);
                }
            }

            DateTime? prev = null;
            foreach (var t in weatherTimes)
            {
                if (prev.HasValue)
                {
                    double gap = (t - prev.Value).TotalSeconds;
                    if (gap > 0 && gap <= CoverageMaxGapSeconds) ds.Coverage.Add((prev.Value, gap));
                }
                prev = t;
            }

            MatchOwn(ds);
            return ds;
        }

        /// <summary>
        /// Сервер рассылает объявление и о моём улове. Совпадение: тот же вид (fish_lang_id или имя),
        /// тот же вес, |Δt| ≤ 120 с между приёмом объявления и записью улова. Только пометка, данные не меняются.
        /// </summary>
        public static void MatchOwn(AnalyticsDataset ds)
        {
            // PRERELEASE-01: свои уловы разложены по (вес, вид) — прежде на каждое объявление перебирались ВСЕ свои уловы
            // (при 300 тыс. объявлений × 30 тыс. уловов — 9·10⁹ сравнений). Результат тот же: внутри корзины сохраняется порядок
            // ds.Mine, выбирается ближайший по времени, при равенстве — первый.
            var used = new HashSet<AnalyticsRecord>();
            var byKey = new Dictionary<(int, string), List<AnalyticsRecord>>();
            foreach (var c in ds.Mine)
                if (c.Real.HasValue)
                {
                    var k = (c.Weight, c.SpeciesKey);
                    if (!byKey.TryGetValue(k, out var l)) byKey[k] = l = new();
                    l.Add(c);
                }
            foreach (var p in ds.Public.Where(p => p.Real.HasValue).OrderBy(p => p.Real))
            {
                if (!(p.Weight > 0) || !byKey.TryGetValue((p.Weight, p.SpeciesKey), out var cand)) continue;
                AnalyticsRecord? m = null; double bd = double.MaxValue;
                foreach (var c in cand)
                {
                    if (used.Contains(c)) continue;
                    double d = Math.Abs((c.Real!.Value - p.Real!.Value).TotalSeconds);
                    if (d <= OwnMatchSeconds && d < bd) { bd = d; m = c; }
                }
                if (m != null) { used.Add(m); p.MatchedOwn = true; }
            }
        }

        private static void ResolveFish(AnalyticsRecord a, PacketDeepParser? parser)
        {
            var fish = parser?.GetFish(a.FishId) ?? (string.IsNullOrWhiteSpace(a.Fish) ? null : parser?.GetFishByName(a.Fish));
            if (fish != null)
            {
                a.FishId ??= fish.Id;
                if (string.IsNullOrWhiteSpace(a.Fish)) a.Fish = fish.Name;
            }
            if (string.IsNullOrWhiteSpace(a.Fish)) a.Fish = a.FishId.HasValue ? "Неизвестно (fishLang " + a.FishId.Value + ")" : "Неизвестно";
        }

        public static bool TryParseHm(string? s, out int minuteOfDay)
        {
            minuteOfDay = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var p = s.Trim().Split(':');
            if (p.Length < 2 || !int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) ||
                !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) || h < 0 || h > 23 || m < 0 || m > 59)
                return false;
            minuteOfDay = h * 60 + m;
            return true;
        }

        private static bool TableExists(SqliteConnection con, string name)
        {
            using var cmd = new SqliteCommand("SELECT 1 FROM sqlite_master WHERE type='table' AND name=@n", con);
            cmd.Parameters.AddWithValue("@n", name);
            return cmd.ExecuteScalar() != null;
        }
        private static int Ord(SqliteDataReader r, string n) { for (int i = 0; i < r.FieldCount; i++) if (string.Equals(r.GetName(i), n, StringComparison.OrdinalIgnoreCase)) return i; return -1; }
        private static string S(SqliteDataReader r, string n) { int i = Ord(r, n); return i < 0 || r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? ""; }
        private static long? L(SqliteDataReader r, string n)
        {
            int i = Ord(r, n);
            if (i < 0 || r.IsDBNull(i)) return null;
            try { return Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture); } catch { return null; }
        }
        private static uint? U(SqliteDataReader r, string n) { var v = L(r, n); return v.HasValue && v.Value >= 0 && v.Value <= uint.MaxValue ? (uint)v.Value : null; }
    }

    public sealed class AnalyticsFilter
    {
        public string? SpeciesKey;       // null — все виды
        public string SpeciesName = "все виды";
        public uint? LocationId;         // null — все водоёмы
        public bool AnyLocation = true;
        public string LocationName = "все водоёмы";
        public DateTime? From;           // реальная дата, включительно
        public DateTime? To;             // реальная дата, включительно
        public bool ShowMine = true;
        public bool ShowPublic = true;
        public bool ExcludeOwnFromPublic = true;
    }

    public sealed class AnalyticsSeries
    {
        public int[] Hours = new int[24];
        public int Total;                 // в графике
        public int MissingTime;           // подходят под фильтр, но без достоверного игрового времени (вне графика)
        public int NearBoundary;          // в пределах погрешности от границы часа
        public int OwnExcluded;           // только публичные: исключены как мои
        public int Species;
        public List<string> Models = new();
        public List<(int hour, int count)> Top(int n) =>
            Enumerable.Range(0, 24).Where(h => Hours[h] > 0).Select(h => (h, Hours[h]))
                      .OrderByDescending(x => x.Item2).ThenBy(x => x.Item1).Take(n).ToList();
    }

    public sealed class AnalyticsResult
    {
        public AnalyticsSeries Mine = new();
        public AnalyticsSeries Public = new();
        public double[] CoverageMinutes = new double[24];
        public int Species;
        public AnalyticsFilter Filter = new();
    }

    public static class AnalyticsEngine
    {
        public static bool PassesCommon(AnalyticsRecord a, AnalyticsFilter f)
        {
            if (f.SpeciesKey != null && a.SpeciesKey != f.SpeciesKey) return false;
            if (!f.AnyLocation && a.LocationId != f.LocationId) return false;
            return true;
        }

        public static bool PassesDate(DateTime? real, AnalyticsFilter f)
        {
            if (!f.From.HasValue && !f.To.HasValue) return true;
            if (!real.HasValue) return false;
            if (f.From.HasValue && real.Value.Date < f.From.Value.Date) return false;
            if (f.To.HasValue && real.Value.Date > f.To.Value.Date) return false;
            return true;
        }

        public static AnalyticsResult Compute(AnalyticsDataset ds, AnalyticsFilter f)
        {
            var res = new AnalyticsResult { Filter = f };
            var species = new HashSet<string>();
            if (f.ShowMine) Fill(res.Mine, ds.Mine, f, species, false);
            if (f.ShowPublic) Fill(res.Public, ds.Public, f, species, f.ExcludeOwnFromPublic);
            res.Species = species.Count;

            foreach (var (start, sec) in ds.Coverage)
            {
                if (!PassesDate(start, f)) continue;
                int h = (int)(GameClock.GameTimeOfDay(start).TotalHours) % 24;
                res.CoverageMinutes[h] += sec / 60.0;
            }
            return res;
        }

        private static void Fill(AnalyticsSeries s, List<AnalyticsRecord> src, AnalyticsFilter f, HashSet<string> allSpecies, bool excludeOwn)
        {
            var sp = new HashSet<string>();
            foreach (var a in src)
            {
                if (!PassesCommon(a, f)) continue;
                if (!a.Real.HasValue || !a.GameMinute.HasValue)
                {
                    // Без достоверного времени нельзя проверить и дату: считаются только при «весь период».
                    if (!f.From.HasValue && !f.To.HasValue) s.MissingTime++;
                    continue;
                }
                if (!PassesDate(a.Real, f)) continue;
                if (excludeOwn && a.MatchedOwn) { s.OwnExcluded++; continue; }
                s.Hours[a.Hour!.Value]++;
                s.Total++;
                if (a.NearHourBoundary) s.NearBoundary++;
                sp.Add(a.SpeciesKey);
                allSpecies.Add(a.SpeciesKey);
                if (a.Model.Length > 0 && !s.Models.Contains(a.Model)) s.Models.Add(a.Model);
            }
            s.Species = sp.Count;
        }

        public static string SampleNote(int n) =>
            n == 0 ? "нет данных" :
            n < 30 ? "выборка очень мала — это описание наблюдений, а не закономерность" :
            n < 100 ? "выборка мала — вывод предварительный" :
                      "выборка умеренная — проверяйте на разных днях и при разном покрытии";

        public static string Summary(AnalyticsDataset ds, AnalyticsResult r)
        {
            var f = r.Filter;
            var sb = new StringBuilder();
            string period = !f.From.HasValue && !f.To.HasValue ? "весь период"
                : (f.From?.ToString("dd.MM.yyyy") ?? "…") + " – " + (f.To?.ToString("dd.MM.yyyy") ?? "…") + " (реальные даты)";
            string series = f.ShowMine && f.ShowPublic ? "обе" : f.ShowMine ? "только мои уловы" : "только публичные объявления";
            sb.AppendLine("Период: " + period + " · Вид: " + f.SpeciesName + " · Водоём: " + f.LocationName + " · Серии: " + series);
            sb.AppendLine("Прочитано баз: " + ds.SourcesRead.Count + " · моих записей " + ds.Mine.Count + ", публичных " + ds.Public.Count +
                          (ds.ExcludedStatus > 0 ? " · не поимки (skipped/not_landed) исключены: " + ds.ExcludedStatus : "") +
                          (ds.Errors.Count > 0 ? " · ошибок чтения: " + ds.Errors.Count : ""));
            if (f.From.HasValue || f.To.HasValue)
                sb.AppendLine("(записи без достоверного времени при выборе дат не считаются: их дату проверить нельзя)");
            sb.AppendLine();
            if (f.ShowMine)
                sb.AppendLine("Мои уловы: " + r.Mine.Total + " в графике · без достоверного времени: " + r.Mine.MissingTime +
                              " · у границы часа (±погрешность): " + r.Mine.NearBoundary);
            if (f.ShowPublic)
                sb.AppendLine("Публичные объявления: " + r.Public.Total + " в графике · без времени: " + r.Public.MissingTime +
                              " · у границы часа: " + r.Public.NearBoundary +
                              (f.ExcludeOwnFromPublic ? " · исключено как мои (вид+вес, ±120 с): " + r.Public.OwnExcluded : " · мои уловы НЕ исключены"));
            sb.AppendLine("Видов рыбы в выборке: " + r.Species +
                          (f.ShowMine && f.ShowPublic ? " (мои " + r.Mine.Species + ", публичные " + r.Public.Species + ")" : ""));
            sb.AppendLine("Самые частые игровые часы (число записей):");
            if (f.ShowMine) sb.AppendLine("  мои:       " + TopText(r.Mine) + "   [n=" + r.Mine.Total + ": " + SampleNote(r.Mine.Total) + "]");
            if (f.ShowPublic) sb.AppendLine("  публичные: " + TopText(r.Public) + "   [n=" + r.Public.Total + ": " + SampleNote(r.Public.Total) + "]");
            int covered = r.CoverageMinutes.Count(m => m > 0);
            double totalCov = r.CoverageMinutes.Sum();
            sb.AppendLine("Наблюдение (запись пакетов, по таблице weather, все водоёмы): " + covered + " из 24 игровых часов, всего " +
                          totalCov.ToString("0", CultureInfo.InvariantCulture) + " мин реального времени" +
                          (covered > 0 ? "; минимум " + r.CoverageMinutes.Where(m => m > 0).Min().ToString("0.0", CultureInfo.InvariantCulture) +
                                         ", максимум " + r.CoverageMinutes.Max().ToString("0.0", CultureInfo.InvariantCulture) + " мин на час" : ""));
            var models = r.Mine.Models.Concat(r.Public.Models).Distinct().ToList();
            sb.AppendLine("Игровое время — оценка: модель " + (models.Count > 0 ? string.Join(", ", models) : GameClock.ModelVersion) +
                          "; погрешность мои ±" + (GameClock.BaseUncertaintyMinutes + GameClock.NoServerTimeExtraMinutes) +
                          ", публичные ±" + GameClock.BaseUncertaintyMinutes + " игровые мин.");
            return sb.ToString();
        }

        private static string TopText(AnalyticsSeries s)
        {
            var top = s.Top(3);
            return top.Count == 0 ? "—" : string.Join(" · ", top.Select(t => t.hour.ToString("00") + ":00 — " + t.count));
        }
    }

    /// <summary>Гистограмма по 24 игровым часам: две серии рядом + полоса покрытия наблюдением.</summary>
    public sealed class HourChart : Control
    {
        public static readonly Color MineColor = Theme.AccentGreen;
        public static readonly Color PublicColor = Theme.Others;
        public AnalyticsResult? Result { get; private set; }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Percent { get; set; }
        private readonly ToolTip _tip = new() { InitialDelay = 150, ReshowDelay = 50 };
        private int _hoverHour = -1;
        private Rectangle _plot;

        public HourChart()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Font = Theme.Ui;
            MinimumSize = new Size(420, 260);
        }

        public void SetResult(AnalyticsResult r) { Result = r; Invalidate(); }

        private double Value(AnalyticsSeries s, int h) =>
            Percent ? (s.Total == 0 ? 0 : 100.0 * s.Hours[h] / s.Total) : s.Hours[h];

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = -1;
            if (_plot.Width > 0 && e.X >= _plot.Left && e.X < _plot.Right && e.Y >= _plot.Top && e.Y <= Height)
                h = Math.Clamp((int)((e.X - _plot.Left) * 24.0 / _plot.Width), 0, 23);
            if (h == _hoverHour) return;
            _hoverHour = h;
            Invalidate();
            if (h < 0 || Result == null) { _tip.Hide(this); return; }
            var r = Result;
            string t = "Игровой час " + h.ToString("00") + ":00–" + h.ToString("00") + ":59";
            if (r.Filter.ShowMine) t += "\nМои уловы: " + r.Mine.Hours[h] + (r.Mine.Total > 0 ? " (" + (100.0 * r.Mine.Hours[h] / r.Mine.Total).ToString("0", CultureInfo.InvariantCulture) + "% серии)" : "");
            if (r.Filter.ShowPublic) t += "\nПубличные объявления: " + r.Public.Hours[h] + (r.Public.Total > 0 ? " (" + (100.0 * r.Public.Hours[h] / r.Public.Total).ToString("0", CultureInfo.InvariantCulture) + "% серии)" : "");
            t += "\nНаблюдение: " + r.CoverageMinutes[h].ToString("0.0", CultureInfo.InvariantCulture) + " мин реального времени";
            _tip.Show(t, this, e.X + 14, e.Y + 14, 8000);
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hoverHour = -1; _tip.Hide(this); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);
            using var small = new Font("Segoe UI", 8f);
            using var muted = new SolidBrush(Theme.TextMuted);
            using var text = new SolidBrush(Theme.Text);

            var r = Result;
            bool mine = r?.Filter.ShowMine ?? true, pub = r?.Filter.ShowPublic ?? true;

            // легенда
            int lx = 12, ly = 8;
            void Legend(Color c, string s)
            {
                using var b = new SolidBrush(c);
                g.FillRectangle(b, lx, ly + 4, 12, 12);
                g.DrawStringT(s, Font, text, lx + 16, ly);
                lx += 22 + (int)g.MeasureStringT(s, Font).Width;
            }
            if (mine) Legend(MineColor, "Мои уловы" + (r != null ? " (n=" + r.Mine.Total + ")" : ""));
            if (pub) Legend(PublicColor, "Публичные объявления" + (r != null ? " (n=" + r.Public.Total + ")" : ""));
            Legend(Theme.AccentTeal, "Наблюдение, мин (полоса под осью)");
            string yTitle = Percent ? "% от своей серии" : "число записей";
            g.DrawStringT("Ось Y: " + yTitle + " · ось X: игровой час (оценка)", small, muted, 12, ly + 22);

            int left = 46, right = 12, top = 52, bottom = 62;
            _plot = new Rectangle(left, top, Math.Max(10, Width - left - right), Math.Max(10, Height - top - bottom));
            using var gridPen = new Pen(Theme.Border);
            using var axisPen = new Pen(Theme.TextMuted);

            double max = 0;
            if (r != null)
                for (int h = 0; h < 24; h++)
                {
                    if (mine) max = Math.Max(max, Value(r.Mine, h));
                    if (pub) max = Math.Max(max, Value(r.Public, h));
                }

            bool empty = r == null || ((!mine || r.Mine.Total == 0) && (!pub || r.Public.Total == 0));
            double step = NiceStep(Math.Max(max, Percent ? 10 : 4));
            double yMax = Math.Max(step, Math.Ceiling(max / step) * step);
            for (double v = 0; v <= yMax + 1e-9; v += step)
            {
                int y = _plot.Bottom - (int)(v / yMax * _plot.Height);
                g.DrawLine(gridPen, _plot.Left, y, _plot.Right, y);
                string lab = Percent ? v.ToString("0", CultureInfo.InvariantCulture) + "%" : v.ToString("0", CultureInfo.InvariantCulture);
                var sz = g.MeasureStringT(lab, small);
                g.DrawStringT(lab, small, muted, _plot.Left - sz.Width - 4, y - sz.Height / 2);
            }
            g.DrawLine(axisPen, _plot.Left, _plot.Bottom, _plot.Right, _plot.Bottom);

            double cw = _plot.Width / 24.0;
            if (_hoverHour >= 0)
            {
                using var hb = new SolidBrush(Color.FromArgb(40, Theme.Accent));
                g.FillRectangle(hb, (float)(_plot.Left + _hoverHour * cw), _plot.Top, (float)cw, _plot.Height);
            }

            for (int h = 0; h < 24; h++)
            {
                float x0 = (float)(_plot.Left + h * cw);
                string hl = h.ToString("00");
                var hs = g.MeasureStringT(hl, small);
                g.DrawStringT(hl, small, muted, x0 + (float)cw / 2 - hs.Width / 2, _plot.Bottom + 3);
                if (r == null || empty) continue;

                int n = (mine ? 1 : 0) + (pub ? 1 : 0);
                float gap = (float)Math.Max(1, cw * 0.12);
                float bw = (float)((cw - 2 * gap) / Math.Max(1, n));
                int idx = 0;
                void Bar(AnalyticsSeries s, Color c)
                {
                    double v = Value(s, h);
                    float bx = x0 + gap + idx * bw;
                    idx++;
                    if (s.Hours[h] == 0) return;
                    float bh = (float)(v / yMax * _plot.Height);
                    using var b = new SolidBrush(c);
                    g.FillRectangle(b, bx, _plot.Bottom - bh, Math.Max(1, bw - 1), bh);
                    string cnt = s.Hours[h].ToString(CultureInfo.InvariantCulture);
                    var cs = g.MeasureStringT(cnt, small);
                    if (bw >= cs.Width - 6)
                        g.DrawStringT(cnt, small, text, bx + bw / 2 - cs.Width / 2, _plot.Bottom - bh - cs.Height);
                }
                if (mine) Bar(r.Mine, MineColor);
                if (pub) Bar(r.Public, PublicColor);
            }

            // полоса покрытия
            int sy = _plot.Bottom + 22, sh = 16;
            double cmax = r == null ? 0 : r.CoverageMinutes.Max();
            for (int h = 0; h < 24; h++)
            {
                float x0 = (float)(_plot.Left + h * cw);
                double m = r?.CoverageMinutes[h] ?? 0;
                int alpha = cmax <= 0 || m <= 0 ? 0 : 40 + (int)(170 * m / cmax);
                using (var b = new SolidBrush(m > 0 ? Color.FromArgb(alpha, Theme.AccentTeal) : Theme.Background))
                    g.FillRectangle(b, x0 + 1, sy, (float)cw - 2, sh);
                string ml = m <= 0 ? "0" : m < 10 ? m.ToString("0.0", CultureInfo.InvariantCulture) : m.ToString("0", CultureInfo.InvariantCulture);
                var ms = g.MeasureStringT(ml, small);
                if (ms.Width <= cw) g.DrawStringT(ml, small, m > 0 ? text : muted, x0 + (float)cw / 2 - ms.Width / 2, sy + 1);
            }
            g.DrawStringT("мин реального времени записи пакетов в каждом игровом часе (все водоёмы)", small, muted, _plot.Left, sy + sh + 2);

            if (empty)
            {
                string msg = r == null ? "Загрузка…" : "Недостаточно данных для выбранных фильтров";
                using var big = new Font("Segoe UI Semibold", 12f);
                var ms = g.MeasureStringT(msg, big);
                g.DrawStringT(msg, big, muted, _plot.Left + _plot.Width / 2 - ms.Width / 2, _plot.Top + _plot.Height / 2 - ms.Height / 2);
            }
        }

        private static double NiceStep(double max)
        {
            double raw = max / 5;
            double p = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-9))));
            foreach (double m in new[] { 1, 2, 5, 10 })
                if (raw <= m * p) return Math.Max(1, m * p);
            return Math.Max(1, 10 * p);
        }
    }

    public sealed class AnalyticsForm : Form
    {
        private readonly PacketDeepParser? _parser;
        private readonly string _currentDb;
        private readonly string _sessionDir;
        private AnalyticsDataset _ds = new();

        private readonly RadioButton _dbAll = new() { Text = "Все сессии (история)", AutoSize = true, Checked = true };
        private readonly RadioButton _dbCurrent = new() { Text = "Текущая сессия", AutoSize = true };
        private readonly RadioButton _serBoth = new() { Text = "Обе серии (рядом)", AutoSize = true, Checked = true };
        private readonly RadioButton _serMine = new() { Text = "Только мои уловы", AutoSize = true };
        private readonly RadioButton _serPub = new() { Text = "Только публичные объявления", AutoSize = true };
        private readonly ComboBox _species = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 262 };
        private readonly ComboBox _water = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 262 };
        private readonly CheckBox _allDates = new() { Text = "Весь период", AutoSize = true, Checked = true };
        private readonly DateTimePicker _from = new() { Format = DateTimePickerFormat.Short, Width = 125 };
        private readonly DateTimePicker _to = new() { Format = DateTimePickerFormat.Short, Width = 125 };
        private readonly CheckBox _excludeOwn = new() { Text = "Не учитывать мои уловы среди\nпубличных объявлений", AutoSize = true, Checked = true };
        private readonly CheckBox _percent = new() { Text = "Доли, % от своей серии", AutoSize = true };
        public HourChart Chart { get; } = new() { Dock = DockStyle.Fill };
        private readonly TextBox _summary = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, WordWrap = true };
        private bool _suspend;

        public const string InterpretationText =
            "Как читать эти данные\n\n" +
            "• Публичные объявления — индекс активности, а не вероятность поклёвки. Сервер объявляет не каждый улов: часть ваших уловов 07.11 в объявлениях не появилась.\n\n" +
            "• Мои уловы — только успешные поимки. Число попыток (забросов, ожидания) здесь не учитывается.\n\n" +
            "• Ни один источник сам по себе не даёт обоснованной вероятности клёва. Вероятность, процент успеха и CPUE не рассчитываются.\n\n" +
            "• Разное время наблюдения по часам искажает сравнение: час, в который вы чаще играли, получит больше записей. Смотрите полосу «наблюдение»: она учитывает только время записи пакетов; уловы из Player.log могли записываться и без неё.\n\n" +
            "• Игровое время — оценка по модели gt1 (ВЕРОЯТНО): ±3 игровые мин для моих уловов, ±2 для объявлений. Записи у границы часа могут относиться к соседнему часу.\n\n" +
            "• Водоём объявления — где находились вы при приёме: в пакете места нет (ВЕРОЯТНО, сервер рассылает объявления в пределах локации).\n\n" +
            "• Уловы с неправдоподобной датой (например, 3995 г. из старого импорта PCAP) не попадают в график и считаются как «без достоверного времени».";

        public AnalyticsForm(PacketDeepParser? parser, string currentDbPath, string sessionDir)
        {
            _parser = parser;
            _currentDb = currentDbPath;
            _sessionDir = sessionDir;

            Text = "Аналитика по игровым часам — FisherLogger";
            Size = new Size(1320, 860);
            MinimumSize = new Size(1000, 640);   UiKit.FitToScreen(this);   // FEAT-08b
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = Theme.Ui;
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;

            var left = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 300, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                                             AutoScroll = true, Padding = new Padding(12, 10, 8, 10), BackColor = Theme.Surface };
            Label H(string t) => new() { Text = t, AutoSize = true, ForeColor = Theme.TextMuted, Font = Theme.UiBold, Margin = new Padding(0, 14, 0, 4) };
            foreach (var cb in new[] { _species, _water }) { cb.BackColor = Theme.SurfaceAlt; cb.ForeColor = Theme.Text; cb.FlatStyle = FlatStyle.Flat; }
            foreach (var c in new Control[] { _dbAll, _dbCurrent, _serBoth, _serMine, _serPub, _allDates, _excludeOwn, _percent })
            { c.ForeColor = Theme.Text; c.Margin = new Padding(2, 2, 0, 2); }

            left.Controls.Add(H("Базы данных"));
            left.Controls.Add(_dbAll);
            left.Controls.Add(_dbCurrent);
            left.Controls.Add(H("Серии"));
            left.Controls.Add(_serBoth);
            left.Controls.Add(_serMine);
            left.Controls.Add(_serPub);
            left.Controls.Add(H("Вид рыбы"));
            left.Controls.Add(_species);
            left.Controls.Add(H("Водоём"));
            left.Controls.Add(_water);
            left.Controls.Add(H("Период (реальные даты)"));
            left.Controls.Add(_allDates);
            var dates = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
            dates.Controls.Add(_from);
            dates.Controls.Add(_to);
            left.Controls.Add(dates);
            left.Controls.Add(H("Вид графика"));
            left.Controls.Add(_excludeOwn);
            left.Controls.Add(_percent);
            var reloadBtn = new Button { Text = "Обновить из базы", Width = 262, Height = 34, Margin = new Padding(0, 12, 0, 6) };
            Theme.StyleButton(reloadBtn, Theme.ButtonNeutral);
            left.Controls.Add(reloadBtn);
            left.Controls.Add(new Label
            {
                Text = InterpretationText, AutoSize = true, MaximumSize = new Size(262, 0), ForeColor = Theme.TextMuted,
                Font = new Font("Segoe UI", 8.75f), Margin = new Padding(0, 12, 0, 6)
            });

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10), BackColor = Theme.Background };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
            right.Controls.Add(Chart, 0, 0);
            right.Controls.Add(new Label { Text = "Сводка", AutoSize = true, ForeColor = Theme.Accent, Font = Theme.UiBold, Margin = new Padding(0, 6, 0, 0) }, 0, 1);
            _summary.BackColor = Theme.Console;
            _summary.ForeColor = Theme.Text;
            _summary.BorderStyle = BorderStyle.FixedSingle;
            _summary.Font = Theme.Mono;
            right.Controls.Add(_summary, 0, 2);

            Controls.Add(right);
            Controls.Add(left);

            _dbAll.CheckedChanged += (s, e) => { if (_dbAll.Checked) LoadData(); };
            _dbCurrent.CheckedChanged += (s, e) => { if (_dbCurrent.Checked) LoadData(); };
            foreach (var rb in new[] { _serBoth, _serMine, _serPub }) rb.CheckedChanged += (s, e) => { if (((RadioButton)s!).Checked) ApplyFilters(); };
            _species.SelectedIndexChanged += (s, e) => ApplyFilters();
            _water.SelectedIndexChanged += (s, e) => ApplyFilters();
            _allDates.CheckedChanged += (s, e) => { _from.Enabled = _to.Enabled = !_allDates.Checked; ApplyFilters(); };
            // AUDIT-04 (B3): календарь шлёт ValueChanged на каждый щелчок — пересчёт через 300 мс после последнего изменения
            var dateDebounce = new System.Windows.Forms.Timer { Interval = 300 };
            dateDebounce.Tick += (s, e) => { dateDebounce.Stop(); ApplyFilters(); };
            _from.ValueChanged += (s, e) => { dateDebounce.Stop(); dateDebounce.Start(); };
            _to.ValueChanged += (s, e) => { dateDebounce.Stop(); dateDebounce.Start(); };
            FormClosed += (s, e) => dateDebounce.Dispose();
            _excludeOwn.CheckedChanged += (s, e) => ApplyFilters();
            _percent.CheckedChanged += (s, e) => { Chart.Percent = _percent.Checked; Chart.Invalidate(); };
            reloadBtn.Click += (s, e) => LoadData();
            _from.Enabled = _to.Enabled = false;

            LoadData();
        }

        private sealed class Item
        {
            public string? Key; public uint? Loc; public bool Any; public string Name = ""; public string Text = "";
            public override string ToString() => Text;
        }

        public List<(string session, string db)> Sources()
        {
            var sources = new List<(string, string)> { (Path.GetFileName(_sessionDir.TrimEnd('\\', '/')), _currentDb) };
            if (_dbAll.Checked)
            {
                string? root = Directory.GetParent(_sessionDir.TrimEnd('\\', '/'))?.FullName;
                if (root != null && Directory.Exists(root))
                    foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    {
                        string db = Path.Combine(dir, "data", "catches.db");
                        if (File.Exists(db) && !string.Equals(Path.GetFullPath(db), Path.GetFullPath(_currentDb), StringComparison.OrdinalIgnoreCase))
                            sources.Add((Path.GetFileName(dir), db));
                    }
            }
            return sources;
        }

        public void LoadData()
        {
            _ds = AnalyticsLoader.Load(Sources(), _parser);
            _suspend = true;
            try
            {
                string? prevSp = (_species.SelectedItem as Item)?.Key;
                var prevW = _water.SelectedItem as Item;
                _species.Items.Clear();
                _species.Items.Add(new Item { Key = null, Name = "все виды", Text = "Все виды" });
                foreach (var g in _ds.Mine.Concat(_ds.Public).GroupBy(a => a.SpeciesKey)
                                     .OrderByDescending(g => g.Count()).ThenBy(g => g.First().Fish, StringComparer.CurrentCulture))
                {
                    string name = g.First().Fish;
                    _species.Items.Add(new Item { Key = g.Key, Name = name,
                        Text = name + " — мои " + g.Count(a => a.IsMine) + ", публ. " + g.Count(a => !a.IsMine) });
                }
                int si = prevSp == null ? 0 : _species.Items.Cast<Item>().ToList().FindIndex(i => i.Key == prevSp);
                _species.SelectedIndex = Math.Max(0, si);

                _water.Items.Clear();
                _water.Items.Add(new Item { Any = true, Name = "все водоёмы", Text = "Все водоёмы" });
                foreach (var g in _ds.Mine.Concat(_ds.Public).GroupBy(a => a.LocationId).OrderByDescending(g => g.Count()))
                {
                    string name = _ds.LocationName(g.Key, _parser);
                    _water.Items.Add(new Item { Loc = g.Key, Name = name,
                        Text = name + " — мои " + g.Count(a => a.IsMine) + ", публ. " + g.Count(a => !a.IsMine) });
                }
                int wi = prevW == null || prevW.Any ? 0 : _water.Items.Cast<Item>().ToList().FindIndex(i => !i.Any && i.Loc == prevW.Loc);
                _water.SelectedIndex = Math.Max(0, wi);

                var dates = _ds.Mine.Concat(_ds.Public).Where(a => a.Real.HasValue).Select(a => a.Real!.Value.Date).ToList();
                if (dates.Count > 0 && _allDates.Checked)
                {
                    _from.Value = dates.Min();
                    _to.Value = dates.Max();
                }
            }
            finally { _suspend = false; }
            ApplyFilters();
        }

        public AnalyticsFilter CurrentFilter()
        {
            var sp = _species.SelectedItem as Item;
            var w = _water.SelectedItem as Item;
            return new AnalyticsFilter
            {
                SpeciesKey = sp?.Key, SpeciesName = sp?.Name ?? "все виды",
                AnyLocation = w == null || w.Any, LocationId = w?.Loc, LocationName = w?.Name ?? "все водоёмы",
                From = _allDates.Checked ? null : _from.Value.Date,
                To = _allDates.Checked ? null : _to.Value.Date,
                ShowMine = !_serPub.Checked, ShowPublic = !_serMine.Checked,
                ExcludeOwnFromPublic = _excludeOwn.Checked
            };
        }

        public void ApplyFilters()
        {
            if (_suspend) return;
            var r = AnalyticsEngine.Compute(_ds, CurrentFilter());
            Chart.SetResult(r);
            string s = AnalyticsEngine.Summary(_ds, r);
            if (_ds.Errors.Count > 0) s += Environment.NewLine + "Ошибки чтения: " + string.Join("; ", _ds.Errors);
            _summary.Text = s;
        }

        // --- для автотестов ---
        public void SelectSeries(bool mine, bool pub) { if (mine && pub) _serBoth.Checked = true; else if (mine) _serMine.Checked = true; else _serPub.Checked = true; }
        public void SelectSpeciesByName(string name)
        {
            int i = _species.Items.Cast<Item>().ToList().FindIndex(x => x.Name == name);
            if (i >= 0) _species.SelectedIndex = i;
        }
        public string SummaryText => _summary.Text;
        public void SetPercent(bool on) => _percent.Checked = on;
    }
}
