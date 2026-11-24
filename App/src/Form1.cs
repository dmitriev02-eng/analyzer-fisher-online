using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using SharpPcap;
using SharpPcap.LibPcap;
using System.Runtime.CompilerServices;

namespace FisherLogger
{
    public partial class Form1 : Form
    {
        private const string SERVER_IP = "178.172.136.226";
        private const int SERVER_PORT = 9205;
        private static readonly byte[] CATCH_MARKER = new byte[] { 0xfb, 0x00, 0x00 };
        private static readonly byte[] EXP_MARKER = new byte[] { 0x30, 0x3a };
        private const int CHAT_WAIT_MS = 30000;
        private const int WEIGHT_TOLERANCE = 5;
        private const int DEEP_DEDUPE_MS = 2000;
        // NEXT-OVERLAY-02: порог записи 50 г убран; допустимость веса — CatchRules.IsRecordableWeight (0 г минимум).
        private const int LiveDedupWindowSeconds = 15;
        private static string DATA_FILE => Path.Combine(AppPaths.DataDirectory, "catch_data.json"); // NEXT-95: isolated (was D:\fishing_research\catch_data.json)
        private static string ID_MAP_FILE => Path.Combine(AppPaths.DataDirectory, "id_map.json"); // NEXT-95: isolated (was D:\fishing_research\id_map.json)


        private List<CatchData> catches = new List<CatchData>();

        public int TotalCatches => catches.Count;
        public int CatchesWithBait => catches.Count(c => !string.IsNullOrEmpty(c.bait_name));
        public int CatchesWithLocation => catches.Count(c => c.location_id.HasValue);
        public int CatchesWithEquipment => catches.Count(c =>
            c.rod_id.HasValue || c.reel_id.HasValue || c.line_id.HasValue ||
            c.hook_id.HasValue || c.float_id.HasValue || c.feeder_id.HasValue);
        private Dictionary<int, IdMapEntry> idMap = new Dictionary<int, IdMapEntry>();
        private ConcurrentDictionary<int, CatchData> waitingForChat = new ConcurrentDictionary<int, CatchData>();
        private int catchIdCounter = 0;
        private List<CatchData> pendingCatches = new List<CatchData>();
        private ConcurrentQueue<ChatCatch> chatQueue = new ConcurrentQueue<ChatCatch>();

        // ДВА НЕЗАВИСИМЫХ БУФЕРА
        private List<byte> streamBuffer = new List<byte>();
        private List<byte> chatBuffer = new List<byte>();
        // Отдельные reassembler'ы на каждое направление TCP: нельзя смешивать
        // клиентский и серверный потоки.
        private readonly TcpReassembler _serverTcpReassembler = new();
        private readonly TcpReassembler _clientTcpReassembler = new();
        private DateTime lastCatchTime = DateTime.MinValue;
        private DateTime lastChatParse = DateTime.MinValue;

        private Label? statusLabel;
        private ListBox? pendingListBox;
        private DataGridView? historyGrid;
        private TextBox? logTextBox;
        private ICaptureDevice? device;
        private PcapNgWriter? _pcapNgWriter;
        // NEXT-107: explicit capture interface (--capture-interface), link type of the opened device,
        // frames dropped because their link layer cannot be normalized to Ethernet.
        internal static string? CaptureInterfaceOverride;
        private volatile int _captureLinkType = CaptureFrameNormalizer.DltEthernet;
        private long _unsupportedFrames;
        // NEXT-107: Player.log catch block -> catch record (late model lines update the same record only).
        private readonly Dictionary<long, CatchData> _playerLogBlocks = new();
        private Thread? sniffThread;
        private System.Windows.Forms.Timer? pollTimer;
        private System.Windows.Forms.Timer? uiRefreshTimer;
        private bool uiRefreshPending;
        private int totalPackets = 0;
        private int serverPackets = 0;
        private DateTime _diagUntil = DateTime.MinValue;
        private FishLogMonitor? fishLogMonitor;
        private PacketQueue? _packetQueue;
        private CancellationTokenSource? _parserCts;
        private Task? _parserTask;
        private int? _currentBaseId;
        // NEXT-GTWC-01: смещение часов сервера (cmd 255/105) относительно ПК; погода s2c1001 для показа; метка погоды.
        private TimeSpan? _serverOffset;
        private readonly object _weatherLock = new object();
        private List<WeatherRecord> _weatherIndex = new List<WeatherRecord>();
        private Label? weatherLabel;
        private DateTime? _currentBaseSince;   // OVERLAY: when the current base was loaded (casts after it belong to this waterbody)
        private OverlayManager? _overlay;      // OVERLAY: in-game fishing radar
        private int? _currentSpawnLocId;
        private int? _currentPointId;
        // Словарь для перевода технических имен из Player.log в русские названия
        private readonly Dictionary<string, string> fishLogMapping = new Dictionary<string, string>
        {
            { "37_karasSilver", "Карась" },
            { "26_gustera", "Густера" },
            { "366", "Окунь" }, 
            { "432", "Подлещик" }
            // Сюда мы будем добавлять новые рыбы по мере их поимки!
        };

        // Глубокий парсер протокола (скопирован из PacketDecoder) и его состояние.
        private readonly PacketDeepParser _deepParser = new PacketDeepParser();
        private readonly DeepCatchSession _deepSession = new DeepCatchSession();
        private readonly object _logFileLock = new object();

        // Автоподтверждение и ручной выбор грейда.
        private bool _autoConfirm = true;
        private readonly bool _suppressPersistence;
        private readonly bool _enableCapture;
        private bool _loadingHistory;

        // SQLite и статистика текущей сессии.
        private CatchDbWriter? _db;
        private readonly List<CatchData> sessionCatches = new List<CatchData>();
        private System.Windows.Forms.Timer? baitTimeoutTimer;
        private const int BaitTimeoutSeconds = 120;

        private sealed class FishStatRow
        {
            public string Id { get; init; } = "";
            public string Fish { get; init; } = "";
            public int Count { get; init; }
            public double Percent { get; init; }
            public double AvgWeight { get; init; }
            public int MinWeight { get; init; }
            public int MaxWeight { get; init; }
            public int NoBait { get; init; }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            // Режим проверки без GUI:
            //   FisherLogger.exe --pcap "D:\...\session_001.pcapng"
            if (args != null && args.Length >= 2 &&
                string.Equals(args[0], "--pcap", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = HeadlessImport.Run(args[1]);
                return;
            }

            // Прогон того же реалтайм-кода (буферы, дедупликация, контекст) по PCAP.
            if (args != null && args.Length >= 2 &&
                string.Equals(args[0], "--replay", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = HeadlessReplay.Run(args[1]);
                return;
            }

            bool enableCapture = true; // NEXT-123: capture on by default
            string? dataDir = null;
            if (args != null)
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--capture") enableCapture = true;
                    else if (args[i] == "--no-capture") enableCapture = false; // NEXT-123
                    else if (args[i] == "--tray") StartInTray = true;            // NOTIFY-02: запуск вместе с игрой — сразу в трей
                    else if (args[i] == "--diag") DiagRequested = true;          // AUDIT-04 (G1): построчная диагностика пакетов
                    else if (args[i] == "--data-dir" && i + 1 < args.Length) dataDir = args[++i];
                    else if (args[i] == "--capture-interface" && i + 1 < args.Length) CaptureInterfaceOverride = args[++i]; // NEXT-107
                }

            if (dataDir != null)
            {
                string full = Path.GetFullPath(dataDir);
                string approved = Path.GetFullPath(@"D:\TheFisherProject\Sessions");
                string userApproved = Path.GetFullPath(AppInfo.UserDir);   // RELEASE-02: на чужом ПК — папки внутри %LOCALAPPDATA%\FisherLogger
                if (!full.StartsWith(approved, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(userApproved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    UiText.Box("--data-dir вне разрешённого staging-воркспейса: " + dataDir,
                        "Отказ запуска", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                AppPaths.BaseDirectory = full;
            }

            // AUDIT-04 (G2): программа уступает процессор игре (ниже обычного); поток приёма пакетов — наивысший внутри процесса,
            // то есть на уровне обычных программ, чтобы пакеты не терялись
            try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch { }
            // FEAT-08: интерфейс размечен в пикселях — при масштабе Windows 125–200 % пусть Windows увеличивает всё окно целиком
            // (текст остаётся чётким), а не только шрифты: иначе надписи обрезаются. При 100 % ничего не меняется.
            try { Application.SetHighDpiMode(HighDpiMode.DpiUnawareGdiScaled); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            WheelGuard.Install();   // UIFIX-02: колесо мыши — прокрутка страницы, а не смена значений списков
            if (!SingleInstance()) return;          // ui01_review3 #1: the second start only brings the running window forward
            LanguageForm.EnsureChosen(ask: true);   // RELEASE-03: первый запуск — выбор языка
            UiTranslator.Start();
            Application.Run(new Form1(startServices: true, enableCapture: enableCapture));
            // FEAT-15 (EXIT-01): всё сохранено в OnFormClosing; оставшиеся потоки (захват, сторонние библиотеки) не должны держать процесс
            try { LogFile.Flush(); } catch { }
            Environment.Exit(0);
        }

        private static System.Threading.Mutex? _single;
        private static System.Threading.EventWaitHandle? _showSignal;
        /// <summary>One FisherLogger per data folder. A second start signals the first one to show its window and exits.
        /// After an update the old copy may still be closing: wait for it a few seconds first.</summary>
        private static bool SingleInstance()
        {
            try
            {
                string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.BaseDirectory.ToUpperInvariant())))[..16];
                _single = new System.Threading.Mutex(true, @"Local\FisherLogger95." + key, out bool created);
                _showSignal = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, @"Local\FisherLogger95.Show." + key);
                if (created) return true;
                try { _showSignal.Set(); } catch { }
                try { if (_single.WaitOne(4000)) return true; }               // the other copy closed (update / restart)
                catch (System.Threading.AbandonedMutexException) { return true; }
                return false;
            }
            catch { return true; }   // no named objects (very unusual): do not block the start
        }

        /// <summary>Waits for «show yourself» from a second start (background thread).</summary>
        private void ListenSecondStart()
        {
            var ev = _showSignal; if (ev == null) return;
            var t = new Thread(() =>
            {
                while (!IsDisposed)
                {
                    try { if (!ev.WaitOne(1000)) continue; } catch { return; }
                    try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => { Log("[START] второй запуск — показываю окно"); ShowNuShell(); })); } catch { }
                }
            }) { IsBackground = true, Name = "second-start" };
            t.Start();
        }

        public Form1(bool startServices = true, bool enableCapture = false)
        {
            // В режиме --replay форма создаётся без сервисов и не должна
            // перезаписывать боевые catch_data.json / catches.csv.
            _suppressPersistence = !startServices;
            _enableCapture = enableCapture;
            AdvisorResources.Warn = Log; // ADVISOR-02: предупреждения о недоступных/битых справочных ресурсах — в лог приложения.

            this.Text = "FisherLogger — запись рыбалки и советы";
            // FEAT-08: окно не больше рабочей области экрана (1366×768, масштаб 125–150 % — иначе низ окна недоступен)
            var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1240, 900);
            this.MinimumSize = new Size(Math.Min(1100, wa.Width), Math.Min(800, wa.Height));
            this.Size = new Size(Math.Min(1240, wa.Width), Math.Min(900, wa.Height));
            if (wa.Width < 1240 || wa.Height < 900) this.WindowState = FormWindowState.Maximized;
            this.BackColor = Theme.Background;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ForeColor = Theme.Text;
            if (Theme.AppIcon != null) this.Icon = Theme.AppIcon; // DESIGN: app icon

            // DESIGN-02 (вариант A): шапка, полоса вкладок «Сейчас / Куда идти / Журнал» и «Для разработчика ▾», страница, журнал программы (свёрнут).
            var rootLayout = new TableLayoutPanel();
            _rootLayout = rootLayout;
            rootLayout.Dock = DockStyle.Fill;
            rootLayout.ColumnCount = 1;
            rootLayout.RowCount = 4;
            rootLayout.BackColor = Theme.Background;
            rootLayout.Padding = new Padding(0, 0, 0, 4);
            rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));  // шапка
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));  // вкладки
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // страница
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));   // журнал программы (свёрнут)
            this.Controls.Add(rootLayout);

            var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = new Padding(0), Padding = new Padding(20, 0, 12, 0) };
            var brand = new Label { Text = "FisherLogger", Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true, Location = new Point(20, 9), BackColor = Color.Transparent };
            var tagline = new Label { Text = "Запись рыбалки и советы для The Fisher Online", Font = UiKit.Small, ForeColor = Theme.TextMuted, AutoSize = true, Location = new Point(22, 40), BackColor = Color.Transparent };
            header.Controls.Add(brand); header.Controls.Add(tagline);
            var nav = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, BackColor = Theme.Surface, Padding = new Padding(0, 14, 0, 0) };
            Button NavButton(string text, Color bg, int w, EventHandler click)
            {
                var b = new Button { Text = text, Width = w, Height = 38, Margin = new Padding(6, 0, 0, 0) };
                Theme.StyleButton(b, bg); b.Click += click; nav.Controls.Add(b); return b;
            }
            // справа налево: В трей · Настройки · Телефон · Окно в игре · Карта (советник теперь вкладка «Куда идти»)
            // NOTIFY-02: свернуть в трей (крайняя справа) — с подсказкой, как вернуть окно
            var exitNav = NavButton("⏻", Theme.ButtonNeutral, 40, (s, e) => ExitApp());   // FEAT-09: закрыть программу совсем
            new ToolTip().SetToolTipT(exitNav, "Закрыть программу (запись остановится)");
            var trayNav = NavButton("В трей", Theme.ButtonNeutral, 86, (s, e) => { if (_tray != null) _tray.HideToTray(returnFocus: true); else WindowState = FormWindowState.Minimized; });
            new ToolTip().SetToolTipT(trayNav, "Свернуть в область уведомлений (у часов). Запись продолжается. Открыть снова — F12 (меняется в настройках) или щелчок по значку рыбки.");
            var settingsButton = NavButton("Настройки", Theme.ButtonNeutral, 112, (s, e) => OpenSettings(null));
            companionButton = NavButton("Телефон: выкл", Theme.ButtonNeutral, 134, (s, e) => OpenCompanion());
            var overlayNav = NavButton("Окно в игре", Theme.ButtonSecondary, 124, (s, e) => { if (_overlay != null) _overlay.ShowSettings(this); });
            var mapNav = NavButton("Карта", Theme.ButtonSecondary, 96, (s, e) => OpenWaterbodyMap());
            new ToolTip().SetToolTipT(mapNav, "Карта водоёма: ваши места, точки с шансом, глубины");
            new ToolTip().SetToolTipT(overlayNav, "Небольшое окно поверх игры: радар забросов и советы тренера");
            header.Controls.Add(nav);
            rootLayout.Controls.Add(header, 0, 0);

            _pages = new PageHost { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = Theme.Background };
            var nowPage = _pages.Add("Сейчас");
            _goPage = _pages.Add("Куда идти");
            var logPage = _pages.Add("Журнал");
            var nowLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Theme.Background, Margin = new Padding(0) };
            nowLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            nowLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));   // состояние + погода
            nowLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 62));    // удочки и подсказки
            nowLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 38));    // последние поимки
            nowLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));   // действие
            nowPage.Controls.Add(nowLayout);
            _nowView = new NowView();
            nowLayout.Controls.Add(SectionBox("Ваши удочки", _nowView, "что делать прямо сейчас"), 0, 1);

            var statusRow = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = new Padding(0), Padding = new Padding(20, 0, 20, 0) };
            statusLabel = new Label();
            statusLabel.Text = "Жду поимок…";
            statusLabel.Font = Theme.Status;
            statusLabel.ForeColor = Theme.Warning;
            statusLabel.BackColor = Theme.Background;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.AutoEllipsis = true;
            weatherLabel = new Label
            {
                Text = "Погода: появится после входа на водоём",
                Font = Theme.Ui, ForeColor = Theme.TextMuted, BackColor = Theme.Background,
                Dock = DockStyle.Right, Width = 520, TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true
            };
            statusRow.Controls.Add(statusLabel); statusRow.Controls.Add(weatherLabel);
            nowLayout.Controls.Add(statusRow, 0, 0);

            pendingListBox = new ListBox();
            pendingListBox.Dock = DockStyle.Fill;
            pendingListBox.BackColor = Theme.Surface;
            pendingListBox.ForeColor = Theme.Text;
            pendingListBox.Font = Theme.Ui;
            pendingListBox.BorderStyle = BorderStyle.None;
            pendingListBox.IntegralHeight = false;
            pendingListBox.DrawMode = DrawMode.OwnerDrawFixed; // DESIGN: fish icon per row
            pendingListBox.ItemHeight = 30;
            pendingListBox.DrawItem += DrawPendingItem;
            nowLayout.Controls.Add(SectionBox("Последние поимки", pendingListBox, "запись сохраняется сама"), 0, 2);

            // NEXT-SESSION-56: панель «Быстрый выбор рыбы» (ручной выбор/пропуск/грейд) удалена —
            // вид определяется автоматически; неизвестное сохраняется как unknown без ручного ввода.

            var historyFrame = new Panel();
            historyFrame.Text = "История поимок";
            historyFrame.Dock = DockStyle.Fill;
            historyFrame.ForeColor = Color.White;
            historyFrame.BackColor = Theme.Background;
            historyGrid = new DataGridView();
            historyGrid.Dock = DockStyle.Fill;
            historyGrid.AllowUserToAddRows = false;
            historyGrid.AllowUserToDeleteRows = false;
            historyGrid.ReadOnly = true;
            // AllCells: столбцы по содержимому; последний столбец («Погода») растягивается до правого края окна (FIX-01).
            historyGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
            historyGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;
            historyGrid.ScrollBars = ScrollBars.Both;
            historyGrid.AutoSize = false;
            historyGrid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            historyGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            historyGrid.BackgroundColor = Theme.Surface;
            historyGrid.ForeColor = Color.White;
            historyGrid.RowHeadersVisible = false;
            historyGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            historyGrid.EnableHeadersVisualStyles = false;
            historyGrid.ColumnHeadersDefaultCellStyle.BackColor = Theme.HeaderBar;
            historyGrid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            historyGrid.ColumnHeadersDefaultCellStyle.Font = Theme.GridHeader;
            historyGrid.DefaultCellStyle.BackColor = Theme.Surface;
            historyGrid.DefaultCellStyle.ForeColor = Color.White;
            historyGrid.DefaultCellStyle.Font = Theme.Grid;
            historyGrid.AlternatingRowsDefaultCellStyle.BackColor = Theme.SurfaceAlt;
            historyGrid.AlternatingRowsDefaultCellStyle.ForeColor = Color.White;
            // NEXT-DATA-01 / NEXT-HISTORY-UI-01 / FIX-01: the main table shows readable fields, in this order. Time, waterbody, cast and
            // weather (formerly only in the hover window) are columns now; everything else stays in the DB (catch_context_v).
            foreach (var (colName, colHeader) in HistoryColumns)
                historyGrid.Columns.Add(colName, colHeader);
            historyGrid.Columns["weather"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;   // FIX-01: the table reaches the right edge
            historyGrid.Columns["weather"]!.MinimumWidth = 180;
            // FIX-02: «Время» sorts by the real catch time (not by text; implausible dates last)
            historyGrid.SortCompare += (s, e) =>
            {
                if (e.Column.Name != "time") return;
                var a = historyGrid.Rows[e.RowIndex1].Tag as CatchData; var b = historyGrid.Rows[e.RowIndex2].Tag as CatchData;
                if (a == null || b == null) return;
                e.SortResult = HistorySortTime(a).CompareTo(HistorySortTime(b));
                e.Handled = true;
            };
            // NEXT-HISTORY-UI-01: grade = coloured badge by the raw windex (no weight-based guess); groundbait = applied (c2s 1112) on the cast
            historyGrid.Columns["grade"]!.ToolTipText = "Класс рыбы, как его сообщает игра: серый — обычная, зелёный — хорошая, синий — крупная, " +
                                                         "оранжевый со звездой — трофей. Пусто — игра класс не сообщила (по весу не угадываем).";
            historyGrid.Columns["groundbait"]!.ToolTipText = "Прикормка, внесённая на этот заброс; состав — в подсказке ячейки. «—» — не вносилась или не удалось связать с забросом.";
            historyGrid.Columns["coords"]!.ToolTipText = "Координаты как на карте в игре (NNN:NNN). Если для водоёма пересчёт не установлен — координаты сцены X/Z. " +
                                                          "«не подтв.» — точка заброса не подтверждена.";
            historyGrid.CellPainting += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex >= 0 && historyGrid.Columns[e.ColumnIndex].Name == "grade") GradeBadge.Paint(e);
            };
            // FIX-01 (по просьбе пользователя): отдельное окно подсказки строки (UI-FIX-01/02) убрано — оно могло остаться поверх
            // игры. Время, водоём, заброс и погода показаны столбцами; для грейда, прикорма и координат остаётся обычная
            // подсказка Windows, которая сама исчезает, когда FisherLogger не активен.
            historyGrid.ShowCellToolTips = true;
            historyGrid.RowTemplate.Height = 30;

            historyFrame.Controls.Add(historyGrid);
            // PRERELEASE-01: «Показывать» — сколько уловов видно в таблице (статистика всегда по всем)
            var histSection = (Panel)SectionBox("История поимок", historyFrame, "");
            _historyHead = histSection.Controls.OfType<Label>().First();
            _showBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190, Dock = DockStyle.Right, Font = Theme.Ui };
            foreach (int m in new[] { -1, 0, 1, 2 }) _showBox.Items.Add(DisplayFilter.ModeTitle(m));
            new ToolTip().SetToolTipT(_showBox, "Авто: когда за сутки больше 60 уловов, старые обычные (серые) не показываются, больше 150 — и хорошие (зелёные). Скрытые уловы учитываются в статистике, советнике и на карте.");
            _showBox.SelectedIndex = Math.Clamp(_appSettings.Display.Mode + 1, 0, 3);
            _showBox.SelectedIndexChanged += (s, e) =>
            {
                _appSettings.Display.Mode = _showBox.SelectedIndex - 1;
                try { _appSettings.Save(); } catch { }
                RebuildHistoryGrid();
            };
            Theme.DarkCombo(_showBox);
            var showCap = new Label { Text = "Показывать:", AutoSize = false, Width = 96, Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.TextMuted, Font = Theme.Ui, BackColor = Theme.Background };
            _historyHead.Controls.Add(showCap); _historyHead.Controls.Add(_showBox);   // DESIGN-02: подпись слева от списка (первый добавленный с Dock=Right стоит ближе к центру)
            _historyHead.Padding = new Padding(0, 2, 0, 2);
            logPage.Controls.Add(histSection);

            // DESIGN-02: на «Сейчас» внизу — «Завершить рыбалку»; редкие инструменты и журнал программы — в «Для разработчика ▾» у вкладок
            var actionPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = new Padding(0), Padding = new Padding(20, 8, 20, 6) };
            var endSessionBtn = new Button { Text = "Завершить рыбалку", Width = 190, Height = 38, Dock = DockStyle.Left };
            Theme.StyleButton(endSessionBtn, Theme.ButtonWarn);
            new ToolTip().SetToolTipT(endSessionBtn, "Подвести итог рыбалки и начать новую. Записанные уловы сохраняются.");
            endSessionBtn.Click += (s, e) =>
            {
                if (UiText.Box(this, "Завершить рыбалку и начать новую?\n\nВсе уловы уже сохранены. Незаконченные забросы будут закрыты.",
                        "Завершить рыбалку", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                    EndSession();
            };
            var toolsBtn = new Button { Text = "Для разработчика ▾", Width = 176, Height = 36, Dock = DockStyle.Right };
            Theme.StyleButton(toolsBtn, Theme.ButtonNeutral);
            var tools = new ContextMenuStrip { ShowImageMargin = false, BackColor = Theme.SurfaceAlt, ForeColor = Theme.Text, Font = Theme.Ui, Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()) };
            var logItem = new ToolStripMenuItem("Журнал программы") { CheckOnClick = true };
            tools.Items.Add(logItem);
            var techItem = new ToolStripMenuItem("Показывать технические данные (числа шанса, id, номера забросов)") { CheckOnClick = true, Checked = _appSettings.Display.ShowTech };
            techItem.CheckedChanged += (s, e) =>
            {
                _appSettings.Display.ShowTech = Plain.Tech = techItem.Checked;
                try { _appSettings.Save(); } catch { }
                try { historyGrid.Columns["cast"]!.Visible = Plain.Tech; } catch { }
                RefreshNow(force: true);
            };
            tools.Items.Add(techItem);
            tools.Items.Add(new ToolStripSeparator());
            tools.Items.Add("Аналитика по игровым часам", null, (s, e) => OpenAnalytics());
            tools.Items.Add("Выгрузить уловы в CSV", null, (s, e) =>
            {
                ExportForAnalysis(AppPaths.AnalysisCsv);
                if (File.Exists(AppPaths.AnalysisCsv))
                    UiText.Box("Файл сохранён:\n" + AppPaths.AnalysisCsv, "Экспорт", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            tools.Items.Add(new ToolStripSeparator());
            tools.Items.Add("Загрузить запись трафика…", null, (s, e) => ImportPcap());
            tools.Items.Add("Разобрать неизвестные предметы", null, (s, e) => AnalyzeId());
            tools.Items.Add(new ToolStripSeparator());
            tools.Items.Add("О программе и обновления…", null, (s, e) => ShowAboutAndUpdates());   // RELEASE-02
            tools.Items.Add("Язык / Language…", null, (s, e) => ChooseLanguage());   // RELEASE-03
            tools.Items.Add("Обмен данными с сервером…", null, (s, e) => ShowServerConsent());   // SERVER-01
            toolsBtn.Click += (s, e) => tools.Show(toolsBtn, new Point(0, toolsBtn.Height));
            actionPanel.Controls.Add(endSessionBtn);
            nowLayout.Controls.Add(actionPanel, 0, 3);

            // полоса вкладок: три кнопки слева, «Для разработчика ▾» справа
            var tabBar = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = new Padding(0), Padding = new Padding(20, 8, 20, 4) };
            var tabFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Background, Margin = new Padding(0) };
            string[] tabHints = { "Что делать прямо сейчас: удочки, время в игре, последние поимки", "Цель: где, на что и когда ловить нужную рыбу; советник по рыбе и водоёмам", "Все ваши поимки" };
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                var tb = new Button { Text = _pages.Title(i), Width = 150, Height = 36, Margin = new Padding(i == 0 ? 0 : 6, 0, 0, 0), Tag = idx };
                tb.Click += (s, e) => _pages.SelectedIndex = idx;
                new ToolTip().SetToolTipT(tb, tabHints[i]);
                tabFlow.Controls.Add(tb);
            }
            void MarkTabs() { foreach (Button tb in tabFlow.Controls) Theme.StyleButton(tb, (int)tb.Tag! == _pages.SelectedIndex ? Theme.ButtonPrimary : Theme.ButtonNeutral); }
            _pages.SelectedIndexChanged += (s, e) => { MarkTabs(); OnPageChanged(); };
            MarkTabs();
            tabBar.Controls.Add(tabFlow); tabBar.Controls.Add(toolsBtn);
            rootLayout.Controls.Add(tabBar, 0, 1);
            rootLayout.Controls.Add(_pages, 0, 2);

            logTextBox = new TextBox();
            logTextBox.Dock = DockStyle.Fill;
            logTextBox.Multiline = true;
            logTextBox.ScrollBars = ScrollBars.Vertical;
            logTextBox.ReadOnly = true;
            logTextBox.BackColor = Theme.Console;
            logTextBox.ForeColor = Theme.ConsoleText;
            logTextBox.Font = Theme.Mono;
            logTextBox.BorderStyle = BorderStyle.None;
            rootLayout.Controls.Add(SectionBox("Журнал программы", logTextBox, "для диагностики"), 0, 3);
            logItem.CheckedChanged += (s, e) => rootLayout.RowStyles[3].Height = logItem.Checked ? 170 : 0;
            Theme.ApplyTree(this); // DESIGN
            historyGrid.DefaultCellStyle.Font = Theme.Grid;
            StartNowPanel();   // DESIGN-02

            AppPaths.EnsureLogsDirectory();
            AppPaths.EnsureDataDirectory();
            _deepSession.FullCatchDetected += OnDeepFullCatch;
            _deepSession.CatchEnriched += OnDeepCatchEnriched;
            _deepSession.SummaryOnlyDetected += OnSummaryOnly;
            _deepSession.EarlyBiteDetected += OnEarlyBite;
            // NEXT-ANALYTICS-04: catch → cast link goes through the attempt tracker (catch event + outcome).
            _deepSession.CastCaughtWithSummary += OnCastCaught;
            _deepSession.EarlyBiteDetected += OnBitePersist; // NEXT-ANALYTICS-04: persist s2c 1120 flag=1 (notification unchanged)
            _deepSession.Diagnostics += message => Log(message);
            _deepParser.Diagnostics += message => Log(message);
            _deepParser.InventoryUpdated += OnInventoryUpdated;
            _deepParser.FeedActionDetected += OnFeedAction;
            _deepParser.CraftActionDetected += OnCraftAction;
            _deepParser.WeatherDetected += OnWeather;
            _ownWindex = new OwnWindexCoordinator(pid => _db?.AttachOwnCatchWindex(pid) ?? 0, Log);   // NEXT-ANALYTICS-14
            _deepParser.LocalPlayerIdDetected += pid => _ownWindex.ObserveLocalPlayerId(pid);       // NEXT-ANALYTICS-13/14
            _inventory.Rejected += reason => Log("[INVENTORY] " + reason);                         // NEXT-BAIT-05
            _deepParser.PublicCatchDetected += OnPublicCatch;     // NEXT-GTWC-01
            _deepParser.ChatMessageDetected += OnChatMessage;     // NEXT-GTWC-01
            _deepParser.ServerTimeDetected += OnServerTime;       // NEXT-GTWC-01
            _deepParser.CastDetected += OnCast;
            _deepParser.GroundbaitApplied += OnGroundbaitApplied; // NEXT-DATA-01
            _deepParser.BaitReloaded += OnBaitReloaded;           // NEXT-DATA-01
            _deepParser.HookDetected += OnHook;
            _deepParser.EmptyBiteDetected += OnEmptyBite;
            _deepParser.BiteInFullCatchDetected += OnBitePersist;   // LOGFIX-03 (A): поклёвка, пришедшая кадром 209/218 байт
            _deepParser.BiteInFullCatchDetected += OnEarlyBite;     // FEAT-09: и окно поклёвки (раньше для 209/218 оно не показывалось — напр. сом)
            _deepParser.ResearchMessageDetected += (t, cl, cmd, data) => { try { _db?.EnqueueResearch(t, cl, cmd, data); } catch { } };   // DATA-01
            _deepParser.WaitDetected += OnWait;
            _deepParser.ServerCheckDetected += OnServerCheck; // NEXT-BITE-IMPLEMENT-01
            _deepParser.FishTakenDetected += OnFishTaken;     // FIX-01
            CatchNotificationForm.Logger = Log;
            BiteToastManager.Log = Log; MciAudio.Log = Log;   // NOTIFY-02
            Shown += (s, e) => StartOverlay(); // OVERLAY
            Shown += (s, e) => { if (!_suppressPersistence) StartAutoUpdateCheck(); }; // RELEASE-02
            Shown += (s, e) => { if (!_suppressPersistence) TryAutoStartCompanion(); }; // NEXT-ANDROID-03
            IconLoader.Logger = Log; // DESIGN: missing icon files are logged once

            if (!_suppressPersistence)
            {
                try
                {
                    _db = new CatchDbWriter(AppPaths.CatchDb);
                    Log("SQLite: " + _db.DbPath + " (session " + _db.SessionId + ")");
                    // NEXT-ANALYTICS-04: attempt lifecycle + casts left open by a previous run (post-migration only).
                    _attempts = new CastLifecycleTracker(_db, () => _db?.SessionId ?? "", "live");
                    var att = _attempts;
                    _checks = new ServerCheckLogger(_db, () => _db?.SessionId ?? "", rod => att.Resolve(rod)); // NEXT-BITE-IMPLEMENT-01
                    _attempts.Diagnostics += message => Log(message);
                    int stale = _db.CloseStalePendingCasts(_db.SessionId, DateTime.Now);
                    if (stale > 0) Log("[ATTEMPT] незавершённых забросов прошлого запуска закрыто как unknown: " + stale);
                }
                catch (Exception ex)
                {
                    _db = null;
                    _checks = null; // NEXT-BITE-IMPLEMENT-01
                    Log("SQLite недоступен, работаем через JSON/CSV: " + ex.Message);
                }
            }

            // NEXT-GTWC-01: погода s2c1001 из БД — для колонки «Погода» у уловов без старых полей погоды.
            try { if (_db != null) lock (_weatherLock) _weatherIndex = _db.GetWeatherRecords(); }
            catch (Exception ex) { Log("[WEATHER] история погоды не загружена: " + ex.Message); }

            LoadData();
            Log("База: предметов " + _deepParser.ItemCount +
                ", наживок/приманок " + _deepParser.BaitCount +
                ", рыб " + _deepParser.FishRecordCount + " (" + _deepParser.FishCount + " ID)" +
                ", локаций " + _deepParser.LocationCount +
                ", модификаторов " + _deepParser.BaitModifierCount +
                ", сетевых ID " + _deepParser.NetworkFishCount +
                " (" + _deepParser.DataDirectory + ")");
            CpuTopology.LogTopology(Log);
            if (startServices)
            {
                // CSV с расширенными колонками доступен сразу, ещё до первой поимки.
                ExportCsv();

                // AUDIT-04 (G1): построчная диагностика каждого пакета — только по запросу (--diag): раньше она 5 минут после запуска
                // писала строку на каждый кадр игры (файл + окно журнала) и нагружала ПК во время игры
                if (DiagRequested) { _diagUntil = DateTime.Now.AddSeconds(300); Log("[DIAG] Диагностика всех пакетов включена на 300 секунд (--diag)"); }

                _packetQueue = new PacketQueue();
                _parserCts = new CancellationTokenSource();
                _parserTask = Task.Factory.StartNew(
                    () => ParserLoop(_parserCts.Token),
                    _parserCts.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);

                // Unity пишет Player.log в LocalLow, а не в Local.
                string localLowLog = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                    "AppData", "LocalLow", "R-Age", "theFisher", "Player.log");
                string localLog = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "R-Age", "theFisher", "Player.log");
                string playerLogPath = System.IO.File.Exists(localLowLog) ? localLowLog : localLog;

                fishLogMonitor = new FishLogMonitor(playerLogPath);
                fishLogMonitor.FishDetected += OnFishDetected;
                fishLogMonitor.OtherPlayerCatchDetected += OnOtherPlayerCatch;
                fishLogMonitor.BaseLoaded += OnBaseLoaded;
                fishLogMonitor.SpawnPointDetected += OnSpawnPointDetected;
                fishLogMonitor.ItemUsed += OnItemUsed;
                fishLogMonitor.EquipmentDetected += OnLogEquipment;
                fishLogMonitor.DropCourDetected += OnLogDropCour; // NEXT-ANALYTICS-04 (diagnostic only)
                fishLogMonitor.ChumDetected += OnChumDetected;
                fishLogMonitor.LocalCatchDetected += OnLocalCatchDetected;      // NEXT-107 (weight + model of the same block)
                fishLogMonitor.LocalCatchSpeciesLate += OnLocalCatchSpeciesLate; // NEXT-107
                fishLogMonitor.GearAssetLoaded += OnGearAssetLoaded;
                fishLogMonitor.Start();
                Log("Мониторинг Player.log запущен: " + playerLogPath);
                Log("Готов к работе. Парсинг чата myfish активен.");

                pollTimer = new System.Windows.Forms.Timer();
                pollTimer.Interval = 500;
                pollTimer.Tick += (s, e) => { ProcessPending(); SweepQuietCasts(); AdvisorLearnTick(); };
                pollTimer.Start();

                // UI-обновления не чаще раза в секунду: очередь/статус
                // не должны перерисовываться на каждый пакет.
                uiRefreshTimer = new System.Windows.Forms.Timer();
                uiRefreshTimer.Interval = 1000;
                uiRefreshTimer.Tick += (s, e) =>
                {
                    if (!uiRefreshPending) return;
                    uiRefreshPending = false;
                    UpdatePendingListNow();
                    UpdateStatusNow();
                };
                uiRefreshTimer.Start();

                // Если CATCH_SUMMARY не пришёл за 90 секунд — фиксируем
                // наживку как «(не определена)», чтобы «...» не висело вечно.
                baitTimeoutTimer = new System.Windows.Forms.Timer();
                baitTimeoutTimer.Interval = 5000;
                baitTimeoutTimer.Tick += (s, e) => ProcessBaitTimeouts();
                baitTimeoutTimer.Start();

                // Захват запускаем последним: Player.log/локация и UI-таймеры
                // должны работать даже при проблемах с драйвером Npcap.
                if (_enableCapture)
                    StartSniffer();
                else
                    Log("NEXT-99: захват выключен (--capture включает)");
            }
        }

        private TableLayoutPanel? _rootLayout;

        /// <summary>DESIGN-01: блок окна — заголовок (Bahnschrift) + серая подпись справа + содержимое на поверхности.</summary>
        private static Control SectionBox(string title, Control body, string hint = "")
        {
            var box = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = new Padding(0), Padding = new Padding(20, 4, 20, 4) };
            var head = new Label { Text = title, Dock = DockStyle.Top, Height = 28, Font = Theme.Heading, ForeColor = Theme.Text, BackColor = Theme.Background, TextAlign = ContentAlignment.MiddleLeft, Tag = hint };
            head.Paint += (s, e) =>
            {
                if (head.Tag is string h && h.Length > 0)
                {
                    // PRERELEASE-01: если справа есть элементы (список «Показывать»), подпись идёт сразу после заголовка
                    int right = head.Controls.Count > 0 ? head.Controls.Cast<Control>().Min(c => c.Left) - 12 : head.Width;
                    int left = UiText.MeasureText(head.Text, head.Font).Width + 16;
                    var flags = head.Controls.Count > 0 ? TextFormatFlags.Left : TextFormatFlags.Right;
                    if (right > left) UiText.DrawText(e.Graphics, h, UiKit.Small, new Rectangle(head.Controls.Count > 0 ? left : 0, 0, head.Controls.Count > 0 ? right - left : head.Width, head.Height), Theme.TextMuted, flags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
            };
            var frame = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(1) };
            frame.Paint += (s, e) => { using var pen = new Pen(Theme.Border); e.Graphics.DrawRectangle(pen, 0, 0, frame.Width - 1, frame.Height - 1); };
            body.Dock = DockStyle.Fill;
            frame.Controls.Add(body);
            box.Controls.Add(frame); box.Controls.Add(head);
            return box;
        }

        /// <summary>DESIGN-01: тёмное меню «Сервис».</summary>
        private sealed class DarkMenuColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected => Theme.Selection;
            public override Color MenuItemBorder => Theme.Selection;
            public override Color MenuBorder => Theme.Border;
            public override Color ToolStripDropDownBackground => Theme.SurfaceAlt;
            public override Color ImageMarginGradientBegin => Theme.SurfaceAlt;
            public override Color ImageMarginGradientMiddle => Theme.SurfaceAlt;
            public override Color ImageMarginGradientEnd => Theme.SurfaceAlt;
            public override Color SeparatorDark => Theme.Border;
            public override Color SeparatorLight => Theme.Border;
        }

        private void LoadData()
        {
            try
            {
                List<CatchData>? jsonCatches = null;
                if (File.Exists(DATA_FILE))
                {
                    var json = File.ReadAllText(DATA_FILE, Encoding.UTF8);
                    jsonCatches = JsonConvert.DeserializeObject<List<CatchData>>(json) ?? new List<CatchData>();
                }

                if (File.Exists(ID_MAP_FILE))
                {
                    var json = File.ReadAllText(ID_MAP_FILE, Encoding.UTF8);
                    var idMapList = JsonConvert.DeserializeObject<List<IdMapEntry>>(json) ?? new List<IdMapEntry>();
                    idMap = idMapList.ToDictionary(x => x.id, x => x);
                }

                if (_db != null)
                {
                    var dbCatches = _db.GetAllCatches();

                    // Первый запуск с SQLite: переносим старую JSON-историю.
                    if (dbCatches.Count == 0 && jsonCatches != null && jsonCatches.Count > 0)
                    {
                        foreach (var c in jsonCatches)
                            _db.SaveCatch(c, "legacy");

                        dbCatches = _db.GetAllCatches();
                        Log("В SQLite перенесено старых поимок: " + dbCatches.Count);
                    }

                    if (dbCatches.Count > 0)
                        catches = dbCatches;
                    else if (jsonCatches != null)
                        catches = jsonCatches;
                }
                else if (jsonCatches != null)
                {
                    catches = jsonCatches;
                }

                // Подтягиваем статические названия/иконки/локации для всех
                // загруженных записей (в т.ч. старых, где bait_name был пустым).
                // На старте не спамим [AUTODETECT] по уже сохранённым записям.
                _loadingHistory = true;
                try
                {
                    foreach (var c in catches)
                        EnrichCatchStatic(c);
                }
                finally
                {
                    _loadingHistory = false;
                }

                // NEXT-22: удаление записей < 50 г на старте убрано — валидные уловы
                // (в т.ч. мелкая рыба) больше не удаляются автоматически из БД и истории.

                // Старые записи с «...» (сводка так и не пришла) закрываем
                // при загрузке, чтобы статус не висел между сессиями.
                foreach (var c in catches.Where(c => c.status == "waiting_bait").ToList())
                {
                    if (c.detected_at == default ||
                        (DateTime.Now - c.detected_at).TotalSeconds >= BaitTimeoutSeconds)
                    {
                        c.status = "confirmed";
                        if (string.IsNullOrEmpty(c.bait_name))
                            c.bait_name = "(не определена)";
                        _db?.UpdateCatch(c);
                    }
                }

                if (historyGrid != null)
                {
                    // FIX-02: the 50 most recent catches by their real time (implausible dates, e.g. year 3995 from an old PCAP
                    // import, count as the oldest); rows are inserted at the top, so the newest ends up first
                    RebuildHistoryGrid();   // PRERELEASE-01: с правилом «Показывать»
                }

                Log("Загружено " + catches.Count + " поимок, " + idMap.Count + " ID в карте" +
                    (_db != null ? ", БД: " + AppPaths.CatchDb : ""));
            }
            catch (Exception ex)
            {
                Log("Ошибка загрузки: " + ex.Message);
            }
        }

        private void SaveData()
        {
            try
            {
                WriteAllTextAtomic(DATA_FILE, JsonConvert.SerializeObject(catches, Formatting.Indented), Encoding.UTF8);
                WriteAllTextAtomic(ID_MAP_FILE, JsonConvert.SerializeObject(idMap.Values.ToList(), Formatting.Indented), Encoding.UTF8);
                ExportCsv();
            }
            catch (Exception ex)
            {
                Log("Ошибка сохранения: " + ex.Message);
            }
        }

        private static void WriteAllTextAtomic(string path, string content, Encoding encoding)
        {
            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, content, encoding);
            File.Move(tempPath, path, overwrite: true);
        }

        private void ExportCsv()
        {
            try
            {
                AppPaths.EnsureLogsDirectory();
                CatchCsvWriter.Write(AppPaths.CatchCsv, catches);
            }
            catch (Exception ex)
            {
                Log("Ошибка CSV: " + ex.Message);
            }
        }

        /// <summary>Экспорт всех поимок в CSV для внешнего анализа.</summary>
        public void ExportForAnalysis(string path)
        {
            try
            {
                AppPaths.EnsureLogsDirectory();
                var sb = new StringBuilder();
                sb.AppendLine("timestamp;fish_name;weight;bait_name;location;depth;coord_x;coord_z;rod_id;reel_id;line_id;hook_id;float_id;feeder_id;quality;bait_uid;spawn_locid;source");
                foreach (var c in catches)
                {
                    sb.AppendLine(string.Join(";", new[]
                    {
                        CsvField(c.timestamp),
                        CsvField(CatchDisplay.Fish(c)),
                        c.weight.ToString(CultureInfo.InvariantCulture),
                        CsvField(CatchDisplay.Bait(c)),
                        CsvField(FormatLocation(c)),
                        c.depth_cm?.ToString("F1", CultureInfo.InvariantCulture) ?? "",
                        c.cast_x?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                        c.cast_z?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                        c.rod_id?.ToString(CultureInfo.InvariantCulture) ?? "",
                        c.reel_id?.ToString(CultureInfo.InvariantCulture) ?? "",
                        c.line_id?.ToString(CultureInfo.InvariantCulture) ?? "",
                        c.hook_id?.ToString(CultureInfo.InvariantCulture) ?? "",
                        c.float_id?.ToString(CultureInfo.InvariantCulture) ?? "",
                        c.feeder_id?.ToString(CultureInfo.InvariantCulture) ?? "",
                        (c.quality_float ?? c.quality)?.ToString("F3", CultureInfo.InvariantCulture) ?? "",
                        c.bait_uid?.ToString(CultureInfo.InvariantCulture) ?? "",
                        c.spawn_locid?.ToString(CultureInfo.InvariantCulture) ?? "",
                        CsvField(c.source)
                    }));
                }

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                Log("[ANALYSIS] Экспорт: " + path + " (" + catches.Count + " поимок)");
            }
            catch (Exception ex)
            {
                Log("Ошибка экспорта для анализа: " + ex.Message);
            }
        }

        private void StartSniffer()
        {
            if (sniffThread is { IsAlive: true }) { Log("[CAPTURE] запись уже идёт — повторный запуск пропущен"); return; }   // ui02_review5 #2
            if (!EnsureNpcap()) return;   // RELEASE-02
            var devices = CaptureDeviceList.Instance;
            if (devices.Count < 1)
            {
                Log("Адаптеры не найдены!");
                return;
            }

            // NEXT-107: the capture interface is the one Windows routes the game server through (or an explicit
            // --capture-interface), never "the first adapter with an IPv4 address". No guessing on failure.
            var candidates = new List<CaptureCandidate>();
            foreach (var dev in devices)
            {
                var ips = new List<string>();
                string? friendly = null;
                if (dev is LibPcapLiveDevice liveDev)
                {
                    friendly = liveDev.Interface?.FriendlyName;
                    if (liveDev.Addresses != null)
                        foreach (var addr in liveDev.Addresses)
                            if (addr.Addr?.ipAddress != null &&
                                addr.Addr.ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                ips.Add(addr.Addr.ipAddress.ToString());
                }
                candidates.Add(new CaptureCandidate(dev.Name, dev.Description, friendly, ips));
            }
            var route = OsRouteLookup.ForIPv4(SERVER_IP, out string? routeError);
            Log("Маршрут Windows к " + SERVER_IP + ": " + (route?.ToString() ?? ("не определён (" + routeError + ")")));
            var selection = CaptureInterfaceSelector.Select(candidates, route, CaptureInterfaceOverride);
            // RELEASE-02: маршрут не определился — берём адаптер, на котором программа уже работала на этом ПК (если он один такой)
            if ((!selection.Ok || selection.Device == null) && string.IsNullOrWhiteSpace(CaptureInterfaceOverride) &&
                CaptureMemory.Remembered(candidates, InstallInfo.LoadOrCreate().CaptureAdapterGuid) is { } remembered)
            {
                Log("Маршрут не помог (" + selection.Error + ") — использую прежний адаптер этого ПК");
                selection = new CaptureSelection { Ok = true, Device = remembered, Reason = "адаптер, запомненный при прошлом запуске" };
            }
            if (!selection.Ok || selection.Device == null)
            {
                Log("ЗАХВАТ НЕ ЗАПУЩЕН: " + selection.Error);
                return;
            }
            ICaptureDevice? bestDevice = null;
            foreach (var dev in devices)
                if (string.Equals(dev.Name, selection.Device.Name, StringComparison.Ordinal)) { bestDevice = dev; break; }
            if (bestDevice == null)
            {
                Log("ЗАХВАТ НЕ ЗАПУЩЕН: выбранный адаптер исчез из списка Npcap: " + selection.Device);
                return;
            }
            Log("Адаптер: " + selection.Device + " — " + selection.Reason);
            RememberCaptureAdapter(selection.Device);   // RELEASE-02

            device = bestDevice;
            device!.OnPacketArrival += Device_OnPacketArrival;

            sniffThread = new Thread(() =>
            {
                try
                {
                    // NEXT-107: explicit failure, no fallback to another adapter.
                    if (!CaptureOpen.TryOpen(() => device!.Open(), device!.Description, device.Name, out string openMessage))
                    {
                        Log("ЗАХВАТ НЕ ЗАПУЩЕН: " + openMessage);
                        return;
                    }
                    int linkType = (int)device.LinkType;
                    _captureLinkType = linkType;
                    Log("Канальный уровень адаптера: " + linkType + " (" + CaptureFrameNormalizer.Describe(linkType) + ")");
                    if (!CaptureFrameNormalizer.IsSupported(linkType))
                    {
                        Log("ЗАХВАТ НЕ ЗАПУЩЕН: тип канального уровня " + linkType + " не поддерживается");
                        try { device.Close(); } catch { }
                        return;
                    }
                    try
                    {
                        string capPath = System.IO.Path.Combine(AppPaths.BaseDirectory, "capture.pcapng");
                        // FIX-03: keep the previous run's capture (it used to be erased on every launch).
                        // If it cannot be moved, the writer is NOT opened over it (no capture file this run).
                        string? archived = PcapNgWriter.ArchivePrevious(capPath);
                        if (archived != null) Log("PCAPNG: прежний захват сохранён: " + archived);
                        // NEXT-107: frames are normalized to Ethernet before writing (raw IP gets a synthetic
                        // 14-byte header with zero MACs), so the file stays LINKTYPE_ETHERNET for all readers.
                        _pcapNgWriter = new PcapNgWriter(capPath, 1, background: true);   // AUDIT-04 (C1): запись на диск — в своём потоке
                _pcapNgWriter.OnError = msg => Log("PCAPNG запись прервана: " + msg);
                        Log("PCAPNG: " + capPath + (linkType == CaptureFrameNormalizer.DltEthernet ? "" : " (кадры приведены к Ethernet)"));
                    }
                    catch (Exception ex)
                    {
                        _pcapNgWriter = null;
                        Log("PCAPNG недоступен: " + ex.Message);
                    }
                    Log("Устройство открыто");

                    try
                    {
                        // TCP — текущие дампы, UDP — Photon-вариант.
                        // Не ограничиваемся только UDP, иначе сломается TCP.
                        device.Filter = GameTrafficFilter.Bpf(SERVER_IP, SERVER_PORT);
                        Log("Фильтр: " + device.Filter + " (оба направления)");
                    }
                    catch (Exception filterEx)
                    {
                        Log("BPF-фильтр не установлен: " + filterEx.Message);
                    }

                    device.StartCapture();
                    Log("Захват начат (открытие адаптера не гарантирует поступление пакетов)");
                    // NEXT-111: диагностика отсутствия трафика — если за 30 с не пришло ни одного пакета, предупредить.
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(30000);
                        if (Volatile.Read(ref totalPackets) == 0)
                            Log("[CAPTURE] за 30 с не получено ни одного пакета — проверьте маршрут к серверу, VPN-туннель и BPF-фильтр");
                    });
                }
                catch (Exception ex)
                {
                    Log("Ошибка: " + ex.Message);
                }
            });
            sniffThread.IsBackground = true;
            try { sniffThread.Priority = ThreadPriority.Highest; } catch { }   // AUDIT-04 (G2)
            sniffThread.Start();
        }

        private void ParserLoop(CancellationToken ct)
        {
            try
            {
                CpuTopology.TryPinCurrentThread(Log);
                while (!ct.IsCancellationRequested)
                {
                    var item = _packetQueue!.Take(ct);
                    if (!item.HasValue) break;
                    ProcessPayload(item.Value.Data, item.Value.FromServer,
                        item.Value.Timestamp, item.Value.IsUdp, item.Value.ConnectionEpoch, item.Value.Segment);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log("Ошибка потока парсера: " + ex.Message);
            }
        }

        private void Device_OnPacketArrival(object sender, PacketCapture e)
        {
            totalPackets++; // общий трафик, только для диагностики; в лог не пишем

            try
            {
                var packet = e.GetPacket();
                // NEXT-107: L3 adapters (WireGuard, Wintun) and loopback have no Ethernet header.
                int frameLinkType = (int)packet.LinkLayerType;
                if (!CaptureFrameNormalizer.TryToEthernet(packet.Data, frameLinkType, out byte[] raw))
                {
                    if (Interlocked.Increment(ref _unsupportedFrames) == 1)
                        Log("[CAPTURE] кадр не приведён к Ethernet (link type " + frameLinkType + "), такие кадры пропускаются");
                    return;
                }
                if (_pcapNgWriter != null)
                {
                    try
                    {
                        ulong tsMicro = (ulong)(DateTime.UtcNow.Ticks / 10);
                        _pcapNgWriter.WritePacket(raw, (uint)raw.Length, tsMicro);
                    }
                    catch (Exception ex)
                    {
                        _pcapNgWriter = null;
                        Log("PCAPNG запись прервана: " + ex.Message);
                    }
                }
                if (raw.Length < 54) return;
                if ((raw[14] >> 4) != 4) return; // IPv4

                byte b1 = raw[26], b2 = raw[27], b3 = raw[28], b4 = raw[29];
                string srcIp = b1 + "." + b2 + "." + b3 + "." + b4;
                byte d1 = raw[30], d2 = raw[31], d3 = raw[32], d4 = raw[33];
                string dstIp = d1 + "." + d2 + "." + d3 + "." + d4;

                int ipHeaderLength = (raw[14] & 0x0F) * 4;
                if (ipHeaderLength < 20) return;

                int transportStart = 14 + ipHeaderLength;
                if (transportStart + 8 > raw.Length) return;

                byte protocol = raw[23];
                bool fromServer = srcIp == SERVER_IP;
                bool toServer = dstIp == SERVER_IP;
                if (!fromServer && !toServer) return;

                int srcPort = (raw[transportStart] << 8) | raw[transportStart + 1];
                int dstPort = (raw[transportStart + 2] << 8) | raw[transportStart + 3];
                if (srcPort != SERVER_PORT && dstPort != SERVER_PORT) return;

                bool isUdp = protocol == 17;
                int payloadStart;
                long connEpoch = _gameConn.Epoch; // NEXT-ANALYTICS-14 (UDP не меняет номер соединения)
                if (isUdp)
                {
                    // UDP header: 8 байт, длина дейтаграммы в +4.
                    payloadStart = transportStart + 8;
                }
                else if (protocol == 6)
                {
                    if (transportStart + 20 > raw.Length) return;
                    int tcpHeaderLength = ((raw[transportStart + 12] >> 4) & 0x0F) * 4;
                    if (tcpHeaderLength < 20) return;
                    payloadStart = transportStart + tcpHeaderLength;
                    // NEXT-ANALYTICS-14: номер игрового соединения (до отбрасывания пустых SYN/ACK). Длина данных — по IP total length,
                    // чтобы заполнение Ethernet коротких кадров не считалось данными.
                    byte tcpFlags = raw[transportStart + 13];
                    bool clientSyn = toServer && (tcpFlags & 0x02) != 0 && (tcpFlags & 0x10) == 0;
                    int ipTotal = (raw[16] << 8) | raw[17];
                    bool tcpHasData = ipTotal - ipHeaderLength - tcpHeaderLength > 0;
                    connEpoch = _gameConn.Observe(toServer ? srcPort : dstPort, clientSyn, tcpHasData);
                }
                else
                {
                    return;
                }

                if (payloadStart >= raw.Length) return;
                int payloadLength = raw.Length - payloadStart;
                if (isUdp)
                {
                    ushort udpLength = (ushort)((raw[transportStart + 4] << 8) |
                                                raw[transportStart + 5]);
                    int declared = udpLength - 8;
                    if (declared >= 0 && declared < payloadLength)
                        payloadLength = declared;
                }

                byte[] payload = new byte[payloadLength];
                Array.Copy(raw, payloadStart, payload, 0, payloadLength);

                if (fromServer)
                    RegisterGamePacket();

                // NEXT-BAIT-05: server TCP segment with seq for the inventory channel (main path unchanged)
                TcpSegment? invSeg = fromServer && !isUdp ? TcpPayloadExtractor.ExtractSegment(raw, DateTime.Now) : null;
                if (_packetQueue != null)
                    _packetQueue.Enqueue(payload, fromServer, DateTime.Now, isUdp, connEpoch, invSeg);
                else
                    ProcessPayload(payload, fromServer, DateTime.Now, isUdp, connEpoch, invSeg);
            }
            catch
            {
                // Игнорируем
            }
        }

        /// <summary>
        /// Счётчик только игровых (серверных) пакетов. Общий трафик
        /// (DNS/HTTP/Windows) в лог не попадает.
        /// </summary>
        private void RegisterGamePacket()
        {
            Interlocked.Exchange(ref _lastGamePacketTicks, DateTime.Now.Ticks);   // FIX-01
            serverPackets++;
            if (serverPackets % 100 == 0)
                Log("Игровых пакетов: " + serverPackets);
        }

        /// <summary>
        /// Обрабатывает один TCP-payload. Вынесено из обработчика SharpPcap,
        /// чтобы тот же код можно было прогнать по PCAP (режим --replay).
        /// </summary>
        private void LogDiagnosticFrames(byte[] payload, bool fromServer, bool isUdp = false)
        {
            try
            {
                int pos = 0;
                while (pos <= payload.Length - 8)
                {
                    int marker = -1;
                    for (int i = pos; i <= payload.Length - 4; i++)
                    {
                        if (payload[i] == 0xFB && payload[i + 1] == 0 &&
                            payload[i + 2] == 0 && payload[i + 3] == 0)
                        {
                            marker = i;
                            break;
                        }
                    }
                    if (marker < 0 || marker + 9 > payload.Length) break;

                    ushort length = BitConverter.ToUInt16(payload, marker + 4);
                    bool photonA = marker + 8 < payload.Length &&
                                   payload[marker + 6] == 0x01 &&
                                   payload[marker + 7] == 0xF3;
                    bool photonB = marker + 9 < payload.Length &&
                                   payload[marker + 6] == 0x00 &&
                                   payload[marker + 7] == 0x01 &&
                                   payload[marker + 8] == 0xF3;
                    if (!photonA && !photonB)
                    {
                        int n = Math.Min(16, payload.Length - marker);
                        Log("[DIAG] Unknown header: " +
                            BitConverter.ToString(payload, marker, n));
                        pos = marker + 4;
                        continue;
                    }

                    int kindOffset = photonB ? marker + 9 : marker + 8;
                    byte kind = payload[kindOffset];
                    Log("[DIAG] " + (isUdp ? "UDP " : "TCP ") +
                        "Kind=" + kind + ", len=" + length +
                        ", fromServer=" + fromServer);

                    if (length > 500)
                    {
                        int n = Math.Min(64, payload.Length - marker);
                        Log("[DIAG] Large frame Kind=" + kind + " len=" + length +
                            " hex: " + BitConverter.ToString(payload, marker, n));
                    }

                    if (kind != 2 && kind != 3 && kind != 4 && kind != 13)
                    {
                        int n = Math.Min(32, payload.Length - marker);
                        Log("[DIAG] Unknown Kind=" + kind + " hex: " +
                            BitConverter.ToString(payload, marker, n));
                    }
                    else if (kind == 13)
                    {
                        int n = Math.Min(96, payload.Length - marker);
                        Log("[DIAG] >>> KIND 13 DETECTED! len=" + length);
                        Log("[DIAG] Hex: " + BitConverter.ToString(payload, marker, n));
                    }

                    if (length < 20 || marker + length > payload.Length) break;
                    pos = marker + length;
                }
            }
            catch
            {
                // Диагностика не должна ломать разбор.
            }
        }

        private void ProcessPayload(byte[] payload, bool fromServer, DateTime timestamp,
            bool isUdp = false, long connectionEpoch = 0, TcpSegment? inventorySegment = null)
        {
            // NEXT-ANALYTICS-14: новое игровое соединение → детекция локального ID заново (в порядке обработки пакетов,
            // поэтому пакеты прежнего соединения, ещё лежащие в очереди, не попадут в новое).
            // Недособранные кадры прежнего соединения отбрасываются, чтобы их байты не склеились с новым потоком.
            if (connectionEpoch > 0 && _ownWindex.OnConnectionEpoch(connectionEpoch))
            {
                _serverTcpReassembler.Clear();
                _clientTcpReassembler.Clear();
                _inventory.Reset(); // NEXT-BAIT-05
                lock (_castByRodUid) _castByRodUid.Clear(); // NEXT-DATA-01
            }
            // NEXT-BAIT-05: inventory messages from the seq-ordered server stream (side channel; never throws into the main path)
            if (inventorySegment != null)
            {
                try { foreach (var m in _inventory.Feed(inventorySegment)) OnInventory(m); }
                catch (Exception ex) { Log("[INVENTORY] канал: " + ex.Message); }
            }

            if (DateTime.Now < _diagUntil)
                LogDiagnosticFrames(payload, fromServer, isUdp);

            try
            {
                // Глубокий разбор нужен в ОБЕ стороны:
                // сервер присылает FULL_CATCH / CATCH_SUMMARY / WEATHER,
                // клиент — CLIENT_STATE (координаты заброса и глубина).
                if (isUdp)
                {
                    // UDP-дейтаграмма уже содержит целые кадры (или их пачку).
                    foreach (var deepEvent in _deepParser.ParseFrames(payload, timestamp))
                        _deepSession.Process(deepEvent);
                    if (fromServer)
                        ParseMyfishChat(payload);
                    return;
                }

                // TCP: кадры могут быть разрезаны между сегментами.
                var reassembler = fromServer ? _serverTcpReassembler : _clientTcpReassembler;
                foreach (var frame in reassembler.Feed(payload))
                {
                    foreach (var deepEvent in _deepParser.ParseFrames(frame, timestamp))
                    {
                        _deepSession.Process(deepEvent);
                    }
                }

                // Ниже — только прежняя серверная логика поимок и чата.
                if (!fromServer) return;

                // 1. Парсим чат myfish (НЕЗАВИСИМЫЙ БУФЕР, таймаут 240 сек)
                ParseMyfishChat(payload);

                // 2. TCP буферизация для поимок (таймаут 10 сек)
                // В реалтайме timestamp == DateTime.Now; при реплее PCAP это
                // метка пакета, поэтому паузы между пакетами работают так же.
                if ((timestamp - lastCatchTime).TotalSeconds > 10)
                {
                    streamBuffer.Clear();
                }

                streamBuffer.AddRange(payload);
                if (streamBuffer.Count > 50000)
                {
                    streamBuffer.RemoveRange(0, streamBuffer.Count - 50000);
                }

                byte[] buffer = streamBuffer.ToArray();
                if (buffer.Length < 100) return;

                int markerPos = FindMarker(buffer, CATCH_MARKER);
                if (markerPos < 0) return;

                int expPos = FindMarker(buffer, EXP_MARKER);
                if (expPos < 0 || expPos < markerPos + 30 || expPos > markerPos + 300) return;

                int weightOffset = expPos - 6;
                if (weightOffset < 0) return;

                int weight = BitConverter.ToInt32(buffer, weightOffset);
                if (!CatchRules.IsRecordableWeight(weight) || weight > 100000) return; // NEXT-OVERLAY-02: was < 10

                // Проверяем, что это действительно кадр FULL_CATCH (kind=3),
                // а не случайное совпадение маркеров в буфере. Вес по смещению
                // marker+38 должен совпасть с весом из формулы опыта.
                if (markerPos + 42 > buffer.Length) return;
                if (buffer[markerPos + 3] != 0x00) return;
                if (buffer[markerPos + 8] != 0x03) return;
                if (BitConverter.ToInt32(buffer, markerPos + 38) != weight) return;

                int offset32Pos = markerPos + 32;
                int? fishId = buffer.Length > offset32Pos + 4 ? (int?)BitConverter.ToInt32(buffer, offset32Pos) : null;

                // Глубокий парсер уже создал запись об этой поимке — не дублируем.
                if (_deepSession.WasJustDetected((uint)weight, 1, TimeSpan.FromMilliseconds(DEEP_DEDUPE_MS)))
                {
                    ResetCatchBuffer(expPos, timestamp);
                    return;
                }

                string grade = DetermineGrade(weight);

                var catchData = new CatchData();
                catchData.timestamp = timestamp.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                catchData.detected_at = timestamp;
                catchData.weight = weight;
                catchData.offset_32 = fishId;
                catchData.status = "waiting_chat";
                catchData.chatDeadline = DateTime.Now.AddMilliseconds(CHAT_WAIT_MS);
                // NEXT-DB-CLEAN-04: u32@32 — не ID рыбы (синтетическая склейка [len&0xFF][blob[0]]).
                // Вид определяется deep-парсером по fish_lang_id; realtime оставляет вид неизвестным.
                catchData.fish_name = "";
                catchData.grade = grade;
                catchData.autoDetected = false;
                catchData.source = "realtime";

                // Последние координаты/глубина/погода, даже если глубокий парсер
                // не увидел кадр целиком (например, из-за сегментации TCP).
                _deepSession.ApplyContext(catchData);

                // NEXT-SESSION-23K: локация из Player.log (Start load Level TFBase_N),
                // если её ещё нет. Иначе realtime-поимка остаётся без локации.
                if (_currentBaseId.HasValue && !catchData.location_id.HasValue)
                    catchData.location_id = (uint)_currentBaseId.Value;
                if (_currentSpawnLocId.HasValue && !catchData.spawn_locid.HasValue)
                    catchData.spawn_locid = _currentSpawnLocId;

                _deepSession.Register(catchData);

                int catchId = Interlocked.Increment(ref catchIdCounter);
                waitingForChat[catchId] = catchData;

                // NEXT-SESSION-51: если ранняя поклёвка для ЭТОГО ЖЕ события (вес+время)
                // уже показала уведомление с известным видом — не показываем второе
                // «Неизвестно (синт. …)». Запись БД при этом сохраняется отдельно.
                // NEXT-SESSION-53: НЕ подавляем уведомление по весу+времени (NEXT-52: это могло
                // скрыть РАЗНУЮ поимку). Если возможен дубль — показываем ОБА уведомления, но
                // помечаем связь как неопределённую (пометка отдельно от вида/ID).
                bool possibleDuplicate = WasEarlyBiteForSameEvent(weight, timestamp);
                if (possibleDuplicate)
                    catchData.notification_note = CatchIdentityPolicy.UncertainAssociationLabel;
                if (!_suppressPersistence && IsHandleCreated)
                {
                    try { BeginInvoke(new Action(() => ShowCatchNotification(catchData))); }
                    catch (InvalidOperationException) { }
                }
                if (possibleDuplicate)
                {
                    Log("[NOTIFY] Показано с пометкой «" + CatchIdentityPolicy.UncertainAssociationLabel +
                        "» (без подавления): " + weight + "гр");
                }

                string autoText = "";
                string deepText = catchData.depth_cm.HasValue
                    ? " глубина=" + (catchData.depth_cm.Value / 100f).ToString("F2") + "м"
                    : "";
                // NEXT-SESSION-51: offset_32 — синтетика (не fish ID), помечаем явно.
                Log("ПОИМКА: " + weight + "гр (" + grade + ")" + autoText + deepText +
                    " синтID=" + (fishId?.ToString() ?? "N/A"));

                ResetCatchBuffer(expPos, timestamp);
            }
            catch
            {
                // Повреждённый payload не должен останавливать захват.
            }
        }

        private void ResetCatchBuffer(int expPos, DateTime timestamp)
        {
            int resetPos = expPos + 100;
            if (resetPos < streamBuffer.Count)
            {
                streamBuffer.RemoveRange(0, resetPos);
            }
            else
            {
                streamBuffer.Clear();
            }
            lastCatchTime = timestamp;
        }

        /// <summary>
        /// Прогон PCAP через реалтайм-код (без живого захвата).
        /// Используется режимом FisherLogger.exe --replay.
        /// </summary>
        internal ReplayResult ReplayPcap(string path)
        {
            _deepParser.Reset();
            _deepSession.Reset();
            _ownWindex.Reset("replay"); // NEXT-ANALYTICS-14: свежая детекция ID на каждый реплей
            _inventory.Reset();         // NEXT-BAIT-05
            lock (_castByRodUid) _castByRodUid.Clear(); // NEXT-DATA-01
            var replayConn = new GameConnectionTracker(); // NEXT-ANALYTICS-14: соединения внутри файла (по порту клиента)
            _currentCastByRod.Clear(); // NEXT-89 R4: reset casts per replay
            _attempts?.ResetInMemory(); // NEXT-ANALYTICS-04
            waitingForChat.Clear();
            pendingCatches.Clear();
            chatQueue = new ConcurrentQueue<ChatCatch>();
            streamBuffer.Clear();
            chatBuffer.Clear();
            lastCatchTime = DateTime.MinValue;
            lastChatParse = DateTime.MinValue;

            var result = new ReplayResult { FilePath = path };
            var packets = CaptureFileReader.Read(File.ReadAllBytes(path));
            result.Packets = packets.Count;
            int catchesBefore = catches.Count;

            foreach (var packet in packets)
            {
                byte[]? payload = TcpPayloadExtractor.Extract(packet.Data, out int srcPort, out int dstPort);
                if (payload == null || payload.Length == 0) continue;

                result.Payloads++;
                bool fromServer = srcPort == TcpPayloadExtractor.FisherPort;
                if (fromServer) result.ServerPayloads++;

                DateTime ts = packet.Timestamp.HasValue ? packet.Timestamp.Value.DateTime : DateTime.Now;
                long replayEpoch = replayConn.Observe(fromServer ? dstPort : srcPort, clientSyn: false, hasPayload: true);
                TcpSegment? invSeg = fromServer ? TcpPayloadExtractor.ExtractSegment(packet.Data, ts) : null; // NEXT-BAIT-05
                ProcessPayload(payload, fromServer, ts, false, replayEpoch, invSeg);
            }

            // Переводим автодетектнутые поимки из ожидания в очередь.
            ProcessPending();

            var detected = new List<CatchData>();
            // Автоподтверждённые поимки попадают сразу в catches.
            foreach (var c in catches.Skip(catchesBefore)) detected.Add(c);
            detected.AddRange(pendingCatches);
            detected.AddRange(waitingForChat.Values);
            result.Catches = detected.Distinct().OrderBy(c => c.detected_at).ToList();
            result.Pending = pendingCatches.Count;
            result.Waiting = waitingForChat.Count;
            result.Report = ReplayReport.Build(result);
            return result;
        }

        // НОВАЯ ФУНКЦИЯ: Парсинг чата myfish
        private void ParseMyfishChat(byte[] payload)
        {
            try
            {
                // Таймаут буфера чата 240 секунд
                if ((DateTime.Now - lastChatParse).TotalSeconds > 240)
                {
                    chatBuffer.Clear();
                    lastChatParse = DateTime.Now;
                }

                chatBuffer.AddRange(payload);
                if (chatBuffer.Count > 50000)
                {
                    chatBuffer.RemoveRange(0, chatBuffer.Count - 50000);
                }
                lastChatParse = DateTime.Now;

                string text = Encoding.UTF8.GetString(chatBuffer.ToArray());

                // Regex для поиска "Пойман/Поймана ... гр +... exp"
                var matches = Regex.Matches(text, @"Пойма[н|на|но]\s+([А-Яа-яЁё\s]+?)\s+(?:(\d+)\s*кг\s*)?(\d+)\s*гр\s*\+\s*(\d+)\s*(?:exp|ехр)");
                
                foreach (Match m in matches)
                {
                    string fishName = m.Groups[1].Value.Trim();
                    int kg = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
                    int grams = int.Parse(m.Groups[3].Value);
                    int weight = kg * 1000 + grams;
                    int exp = int.Parse(m.Groups[4].Value);

                    if (fishName.Length > 1 && fishName.Length < 50 && weight > 0 && weight < 100000)
                    {
                        // Проверяем что такое сообщение ещё не добавляли
                        bool exists = false;
                        foreach (var c in chatQueue.ToArray())
                        {
                            if (string.Equals(c.fishName, fishName, StringComparison.OrdinalIgnoreCase) &&
                                c.weight == weight && c.exp == exp)
                            {
                                exists = true;
                                break;
                            }
                        }
                        if (!exists)
                        {
                            chatQueue.Enqueue(new ChatCatch
                            {
                                fishName = fishName,
                                weight = weight,
                                exp = exp,
                                time = DateTime.Now
                            });
                            Log("ЧАТ: " + fishName + " " + weight + "гр +" + exp + "exp");
                        }
                    }
                }

                if (matches.Count > 0)
                {
                    chatBuffer.Clear();
                }
            }
            catch
            {
                // Не текст
            }
        }

        
                private void OnFishDetected(string fishFile, string fishNameFromLog)
        {
            
            // Пытаемся найти русское название в нашем словаре
            string russianName = "Неизвестная рыба";
            if (fishLogMapping.ContainsKey(fishFile))
            {
                russianName = fishLogMapping[fishFile];
            }
            
            Log("📄 Player.log: " + fishFile + " -> " + russianName);
            
            // Если это новая рыба, можно будет добавить её в словарь вручную или автоматически
        }

        /// <summary>
        /// Поимка другого игрока из локального чата Player.log.
        /// Координаты — последний CLIENT_STATE; location_id — последняя
        /// FULL_CATCH/CATCH_SUMMARY. DistanceMeters пока null: в строке чата
        /// нет координат чужой поимки.
        /// </summary>
        private void OnOtherPlayerCatch(OtherPlayerCatch otherCatch)
        {
            try
            {
                var context = _deepSession.GetCurrentContext();
                otherCatch.MyLocationId = context.LocationId;
                otherCatch.MyCastX = context.CastX;
                otherCatch.MyCastZ = context.CastZ;
                otherCatch.DistanceMeters = null;

                _db?.SaveOtherPlayerCatch(otherCatch);

                string clan = string.IsNullOrEmpty(otherCatch.Clan) ? "" : "[" + otherCatch.Clan + "] ";
                Log("ЧУЖАЯ ПОИМКА: " + clan + otherCatch.PlayerName + " — " +
                    otherCatch.FishName + " " + otherCatch.Weight + " г" +
                    (context.LocationId.HasValue ? " (локация " + context.LocationId.Value + ")" : ""));
            }
            catch (Exception ex)
            {
                Log("Ошибка сохранения чужой поимки: " + ex.Message);
            }
        }

        private void OnInventoryUpdated(int count)
        {
            Log("[INVENTORY] Kind=13: " + count + " предметов (UID→item_id)");
        }

        /// <summary>NEXT-BAIT-05: проверенное сообщение инвентаря → текущее состояние + история uid, затем до-разрешение событий.</summary>
        private void OnInventory(InventoryMessage msg)
        {
            try
            {
                var applied = _db?.ApplyInventory(msg);
                if (msg.Cmd == InventoryCodec.CmdSnapshotPage || applied?.HistoryConflicts > 0)
                    Log("[INVENTORY] s2c " + msg.Cmd + ": " + (msg.Cmd == InventoryCodec.CmdRemove ? "uid " + msg.RemovedUid : msg.Records.Count + " зап.") +
                        (applied != null ? " → " + applied : ""));
                ResolveInventoryRefs();
            }
            catch (Exception ex) { Log("[INVENTORY] ошибка записи: " + ex.Message); }
        }

        /// <summary>NEXT-BAIT-05: заполнить известные типы предметов в FEED/CRAFT/забросах (только NULL, идемпотентно).</summary>
        private void ResolveInventoryRefs()
        {
            try
            {
                var r = _db?.ResolveInventoryItemIds();
                if (r != null && r.Total > 0) Log("[INVENTORY] тип предмета определён по uid: " + r);
            }
            catch (Exception ex) { Log("[INVENTORY] до-разрешение uid: " + ex.Message); }
        }

        private void OnFeedAction(FeedAction action)
        {
            try
            {
                _db?.SaveFeedAction(action);
                ResolveInventoryRefs(); // NEXT-BAIT-05
                Log("[FEED] bait_uid=" + action.BaitUid +
                    (action.BaitItemId.HasValue ? ", item_id=" + action.BaitItemId : "") +
                    (string.IsNullOrEmpty(action.BaitName) ? "" : ", " + action.BaitName) +
                    (action.HasBait ? "" : " (без прикормки)"));
            }
            catch (Exception ex)
            {
                Log("Ошибка сохранения feed: " + ex.Message);
            }
        }

        private readonly Dictionary<int, long> _currentCastByRod = new();

        // NEXT-ANALYTICS-04: casts and rod events are written through CastLifecycleTracker (same semantics as replay).
        private CastLifecycleTracker? _attempts;
        private ServerCheckLogger? _checks;   // NEXT-BITE-IMPLEMENT-01: c2s 1120 observations (not attempts)

        private void OnServerCheck(ServerCheck c)
        {
            try { _attempts?.NoteCheck(c.Rod, c.Timestamp == default ? DateTime.Now : c.Timestamp); }   // FIX-01
            catch (Exception ex) { Log("[ATTEMPT] проверка не учтена: " + ex.Message); }
            try { _checks?.OnCheck(c); }
            catch (Exception ex) { Log("[CHECK] c2s 1120 не сохранён: " + ex.Message); }
        }

        // FIX-01: a reeled-in rig sends nothing of its own — its server checks (every ~8 s) just stop. Close such casts after
        // QuietCloseSeconds of silence, but only while the game connection is alive (game packets in the last 15 s).
        public const int QuietCloseSeconds = 120;
        private long _lastGamePacketTicks;
        private DateTime _lastQuietSweep = DateTime.MinValue;
        private DateTime _lastWindexFill = DateTime.MinValue;

        /// <summary>FIX-02: windex was filled in the DB after the row was shown → reload windex of visible rows (grade badge).</summary>
        private void RefreshWindexInHistory()
        {
            if (_db == null || historyGrid == null || historyGrid.IsDisposed) return;
            var map = _db.GetCatchWindexByRecordId();
            foreach (DataGridViewRow row in historyGrid.Rows)
                if (row.Tag is CatchData c && !c.windex.HasValue && !string.IsNullOrEmpty(c.record_id) && map.TryGetValue(c.record_id, out int w))
                {
                    c.windex = w;
                    FillHistoryRow(row, c);
                }
        }
        private void SweepQuietCasts()
        {
            try
            {
                var now = DateTime.Now;
                if (_attempts == null || (now - _lastQuietSweep).TotalSeconds < 5) return;
                _lastQuietSweep = now;
                long last = Interlocked.Read(ref _lastGamePacketTicks);
                if (last == 0 || (now - new DateTime(last)).TotalSeconds > 15) return;   // no live game traffic → do not judge
                _attempts.CloseQuiet(now, TimeSpan.FromSeconds(QuietCloseSeconds));
                // FIX-02: the windex fill from the bite needs catches.cast_id, which is often set AFTER the catch row was saved
                // (summary → cast link); re-run it every 30 s so the last catch of a session does not stay without a grade
                if ((now - _lastWindexFill).TotalSeconds >= 30)
                {
                    _lastWindexFill = now;
                    int n = _db?.AttachWindexFromBites() ?? 0;
                    if (n > 0) { Log("[BITE] windex из поклёвки (s2c 1120) записан у уловов: " + n); RefreshWindexInHistory(); }
                }
            }
            catch (Exception ex) { Log("[ATTEMPT] проверка тишины: " + ex.Message); }
        }

        // FIX-01: c2s 1135 «Забрать» → cast_events 'keep' on the cast of that fish (uid = CATCH_SUMMARY uid)
        private readonly Dictionary<long, long> _castBySummaryUid = new();
        private void OnFishTaken(long uid, DateTime ts)
        {
            try
            {
                long castId;
                lock (_castBySummaryUid) castId = _castBySummaryUid.TryGetValue(uid, out var id) ? id : 0;
                _db?.SaveCastEvent(new CastEvent
                {
                    CastId = castId,
                    Timestamp = ts == default ? DateTime.Now : ts,
                    EventType = "keep",
                    IsClient = true,
                    RawText = "c2s 1135 «Забрать» uid=" + uid.ToString(CultureInfo.InvariantCulture),
                    MatchStatus = castId > 0 ? CastLifecycleTracker.Matched : CastLifecycleTracker.Unassociated,
                    SourceKey = "live|" + (_db?.SessionId ?? "") + "|keep|" + uid.ToString(CultureInfo.InvariantCulture)
                });
                Log("[KEEP] рыба uid=" + uid + " забрана" + (castId > 0 ? " (заброс #" + castId + ")" : " (заброс не найден)"));
            }
            catch (Exception ex) { Log("[KEEP] не сохранено: " + ex.Message); }
        }

        private void OnCast(PacketDeepParser.DeepCatchData ev)
        {
            if (ev == null) return;
            long id = 0;
            try
            {
                _checks?.OnCast(ev.RodIndex); // NEXT-BITE-IMPLEMENT-01: a pending check cannot receive a response across casts
                id = _attempts?.OnCast(ev.RodIndex, ev.Timestamp, ev.CastX, ev.CastY, ev.CastZ, ev.BaitUid) ?? 0;
                if (id > 0) ResolveInventoryRefs(); // NEXT-BAIT-05: bait_uid → casts.bait_tid from the inventory history
                if (id > 0) SnapshotTackle(id, ev);  // NEXT-DATA-01: tackle of THIS cast from the current inventory
            }
            catch (Exception ex) { Log("[ATTEMPT] заброс не сохранён: " + ex.Message); }
            if (ev.RodIndex.HasValue && id > 0 &&
                _currentCastByRod.TryGetValue(ev.RodIndex.Value, out long current) && current == id)
                return; // duplicate processing of the same cast packet: keep the existing snapshot
            if (id > 0 && ev.RodIndex.HasValue)
                _currentCastByRod[ev.RodIndex.Value] = id;
            // NEXT-115: per-cast snapshot (coordinates of THIS cast) for the catch of the same rod.
            if (ev.RodIndex.HasValue)
                _deepSession.RegisterCast(ev.RodIndex.Value, id, ev.CastX, ev.CastY, ev.CastZ, ev.Timestamp);
        }

        // NEXT-DATA-01: rod uid → its latest cast (from the cast's tackle snapshot), used only to link c2s 1112 to that cast
        private readonly Dictionary<long, (long CastId, double? X, double? Z)> _castByRodUid = new();

        private CatalogEntry? CatalogItem(long itemId)
        {
            if (itemId <= 0 || itemId > uint.MaxValue) return null;   // itemId = <tid> catalog id (never inid)
            var it = _deepParser.GetItem((uint)itemId);
            return it == null ? null : new CatalogEntry(it.Name, it.Category, it.BaseParams, it.KlevPropRaw, it.SubType, it.TypeName);
        }

        private void SnapshotTackle(long castId, PacketDeepParser.DeepCatchData ev)
        {
            try
            {
                var snap = _db?.SnapshotCastTackle(castId, ev.BaitUid, ev.Timestamp == default ? DateTime.Now : ev.Timestamp, CatalogItem);
                if (snap == null) return; // already captured (duplicate packet) or no DB
                long? rodUid = snap.Get("rod")?.Uid;
                if (rodUid.HasValue) lock (_castByRodUid) _castByRodUid[rodUid.Value] = (castId, ev.CastX, ev.CastZ);
                Log("[TACKLE] заброс #" + castId + ": " + snap.Status +
                    (snap.Get("rod") is { } rod ? ", удочка " + (rod.Name ?? rod.ItemId?.ToString() ?? "?") : "") +
                    (snap.AmbiguousSlots.Count > 0 ? ", неоднозначно: " + string.Join(",", snap.AmbiguousSlots) : "") +
                    ", прикорм " + snap.GroundbaitStatus);
            }
            catch (Exception ex) { Log("[TACKLE] снимок снасти не сохранён: " + ex.Message); }
        }

        private void OnGroundbaitApplied(GroundbaitAction a)
        {
            try
            {
                Dictionary<long, (long, double?, double?)> map;
                lock (_castByRodUid) map = new Dictionary<long, (long, double?, double?)>(_castByRodUid);
                if (_db?.SaveGroundbaitAction(a, map) == true)
                {
                    ResolveInventoryRefs();
                    Log("[GROUNDBAIT] внесение c2s 1112: прикорм uid=" + a.GroundbaitUid + (a.FeederUid > 0 ? ", кормушка uid=" + a.FeederUid : ", без кормушки") +
                        string.Format(CultureInfo.InvariantCulture, ", X {0:F1} Z {1:F1}", a.X, a.Z));
                }
            }
            catch (Exception ex) { Log("[GROUNDBAIT] не сохранено: " + ex.Message); }
        }

        private void OnBaitReloaded(BaitReload b)
        {
            try
            {
                if (_db?.SaveBaitReload(b) == true) ResolveInventoryRefs();
            }
            catch (Exception ex) { Log("[RELOAD] не сохранено: " + ex.Message); }
        }

        private long ResolveCastId(int? rodIndex)
        {
            if (rodIndex.HasValue && _currentCastByRod.TryGetValue(rodIndex.Value, out long id))
                return id;
            return 0;
        }

        private void SaveRodEvent(string type, PacketDeepParser.DeepCatchData ev, bool isClient)
        {
            try
            {
                _attempts?.OnRodEvent(type, ev.RodIndex, ev.Timestamp == default ? DateTime.Now : ev.Timestamp, isClient,
                                      ev.FishLangId, ev.Weight, ev.Windex, type == "wait" ? ev.Shance : null, ev.RawText ?? "", null, type == "bite" ? ev.Bite : null);   // FORMULA-06 (B0): шанс сервера из «wait» сохраняется
            }
            catch (Exception ex) { Log("[ATTEMPT] событие " + type + " не сохранено: " + ex.Message); }
        }

        private void OnHook(PacketDeepParser.DeepCatchData ev)
        {
            if (ev == null) return;
            SaveRodEvent("hook", ev, isClient: true);
            if (ev.RodIndex.HasValue)
                _deepSession.RegisterHook(ev.RodIndex.Value, ev.Timestamp); // NEXT-115: freeze this rod's depth
        }

        private void OnEmptyBite(PacketDeepParser.DeepCatchData ev)
        {
            if (ev == null) return;
            SaveRodEvent("empty_bite", ev, isClient: false);
        }

        private void OnWait(PacketDeepParser.DeepCatchData ev)
        {
            if (ev == null) return;
            SaveRodEvent("wait", ev, isClient: false);
            try { _checks?.OnResponse("wait", ev.RodIndex, ev.Timestamp == default ? DateTime.Now : ev.Timestamp, ev.Shance); }
            catch (Exception ex) { Log("[CHECK] ответ не сопоставлен: " + ex.Message); }
        }

        /// <summary>NEXT-ANALYTICS-04: s2c 1120 flag=1 bite → cast_events 'bite' (the notification path is separate).</summary>
        private void OnBitePersist(PacketDeepParser.DeepCatchData ev)
        {
            if (ev == null) return;
            SaveRodEvent("bite", ev, isClient: false);
            try { _checks?.OnResponse("bite", ev.RodIndex, ev.Timestamp == default ? DateTime.Now : ev.Timestamp, null); }
            catch (Exception ex) { Log("[CHECK] ответ не сопоставлен: " + ex.Message); }
        }

        /// <summary>NEXT-ANALYTICS-04: a catch summary was associated with a cast (DeepCatchSession).</summary>
        private void OnCastCaught(long castId, PacketDeepParser.DeepCatchData summary)
        {
            try
            {
                if (summary.CatchUid.HasValue) lock (_castBySummaryUid) _castBySummaryUid[summary.CatchUid.Value] = castId;   // FIX-01
                if (_attempts != null)
                    _attempts.OnCatch(castId, summary.Timestamp == default ? DateTime.Now : summary.Timestamp,
                                      summary.FishLangId, summary.Weight);
                else
                    _db?.UpdateCastOutcome(castId, "caught");
            }
            catch (Exception ex) { Log("[ATTEMPT] улов → заброс не сохранён: " + ex.Message); }
        }

        /// <summary>
        /// NEXT-ANALYTICS-04: Player.log "DROP COUR" carries no rod index and no own timestamp (time = poll time),
        /// so it is kept only as an unassociated diagnostic event (cast_id = 0), never used to close a cast.
        /// </summary>
        private void OnLogDropCour(DateTime readAt)
        {
            try
            {
                _db?.SaveCastEvent(new CastEvent
                {
                    CastId = 0,
                    Timestamp = readAt,
                    EventType = "log_drop_cour",
                    RawText = "Player.log DROP COUR (no rod index; time = log poll time, ±0.5 s)",
                    MatchStatus = CastLifecycleTracker.Unassociated
                });
            }
            catch (Exception ex) { Log("[ATTEMPT] DROP COUR не сохранён: " + ex.Message); }
        }

        private void OnWeather(PacketDeepParser.DeepCatchData w)
        {
            if (w == null) return;
            // NEXT-GTWC-01: погода для интерфейса (раньше s2c1001 только сохранялась в БД и нигде не показывалась).
            var wr = new WeatherRecord { Timestamp = w.Timestamp, Cloudly = w.Cloudly, Fog = w.Fog, Rain = w.Rain, Tempr = w.AirTemperatureC, Grom = w.Grom };
            lock (_weatherLock)
            {
                _weatherIndex.Add(wr);
                if (_weatherIndex.Count > 50000) _weatherIndex.RemoveRange(0, 10000);
            }
            try
            {
                if (weatherLabel != null && IsHandleCreated && !IsDisposed)
                    BeginInvoke(new Action(() => { if (weatherLabel != null) weatherLabel.Text = "Погода в " + w.Timestamp.ToString("HH:mm") + ": " + FormatWeather1001(wr); }));
            }
            catch (InvalidOperationException) { }
            _db?.SaveWeather(new WeatherRecord
            {
                Timestamp = w.Timestamp,
                SessionId = _db?.SessionId,
                BaseId = w.BaseId,
                LocationId = w.LocationId,
                Cloudly = w.Cloudly,
                Fog = w.Fog,
                Rain = w.Rain,
                Tempr = w.AirTemperatureC,
                Grom = w.Grom,
                UnitsStatus = "UNVERIFIED"
            });
        }

        // ------------------------------------------------------------------
        // NEXT-GTWC-01: публичные события игры (пассивный разбор уже захваченных пакетов; ничего не отправляется)
        // ------------------------------------------------------------------
        private void OnServerTime(DateTime server, DateTime received)
        {
            var off = server - received;
            if (Math.Abs(off.TotalHours) < 26) _serverOffset = off;   // разумный диапазон (часовые пояса); иначе игнор
        }

        private (DateTime? server, string? gt, int? unc) GameTimeFor(DateTime received)
        {
            var off = _serverOffset;
            var (gt, unc) = GameClock.Estimate(received, off);
            return (off.HasValue ? received + off.Value : null, gt, unc);
        }

        // NEXT-ANALYTICS-13/14: локальный player ID определяется автоматически (c2s kind=2 cmd 1006) и заново для каждого
        // игрового соединения; windex привязывается только через AttachOwnCatchWindex (точные правила NEXT-ANALYTICS-09).
        private readonly OwnWindexCoordinator _ownWindex;
        private readonly GameConnectionTracker _gameConn = new();   // поток захвата: номер игрового соединения
        private readonly InventoryChannel _inventory = new();       // NEXT-BAIT-05: инвентарь из потока, собранного по TCP seq

        private void OnPublicCatch(PacketDeepParser.PublicCatchEvent e)
        {
            try
            {
                var (server, gt, unc) = GameTimeFor(e.ReceivedAt);
                var rec = new PublicCatchRecord
                {
                    ReceivedAt = e.ReceivedAt, ServerTime = server, GameTimeEst = gt, GameTimeUncMin = unc, GameTimeModel = GameClock.ModelVersion,
                    SessionId = _db?.SessionId, ReceiverLocationId = _currentBaseId.HasValue && _currentBaseId.Value >= 0 ? (uint)_currentBaseId.Value : null,
                    SourceCmd = e.Cmd, Channel = e.Channel, PlayerId = e.PlayerId, PlayerName = e.PlayerName,
                    FishLangId = e.FishId, FishName = e.FishName, Windex = e.Windex, Weight = e.Weight,
                    PlayerLevel = e.Level, Title = e.Title, ClubId = e.ClubId, ClubTag = e.ClubTag, SourceKey = e.SourceKey
                };
                bool added = _db?.SavePublicCatch(rec) ?? false;
                if (added) _ownWindex.OnPublicCatchSaved(rec.PlayerId); // NEXT-ANALYTICS-09/14: only for the confirmed local ID
                if (added) Log("[FEED] " + (e.ClubTag.Length > 0 ? "[" + e.ClubTag + "] " : "") + e.PlayerName + ": " + e.FishName + " " + e.Weight + " г (" + e.Channel + ")");
            }
            catch (Exception ex) { Log("[FEED] ошибка записи объявления: " + ex.Message); }
        }

        private void OnChatMessage(PacketDeepParser.ChatEvent e)
        {
            try
            {
                if (e.IsPrivate)
                {
                    Log("[CHAT] личное сообщение — не сохраняется");
                    return;
                }
                // NEXT-GTWC-02: обычные сообщения чата (cmd 50) больше не сохраняются в БД и не показываются в ленте.
                Log("[CHAT] сообщение чата не сохраняется: " + e.FromName + (e.IsAction ? " (действие)" : ": " + e.Text));
            }
            catch (Exception ex) { Log("[CHAT] ошибка: " + ex.Message); }
        }

        private void OnCraftAction(CraftAction action)
        {
            try
            {
                _db?.SaveCraftAction(action);
                ResolveInventoryRefs(); // NEXT-BAIT-05
                Log("[CRAFT] main=" + action.MainWpnUid +
                    (action.MainWpnItemId.HasValue ? "→" + action.MainWpnItemId : "") +
                    (ItemName(action.MainWpnItemId) is { Length: > 0 } mn ? " (" + mn + ")" : "") +
                    ", aroma=" + action.AromaWpnUid +
                    (action.AromaWpnItemId.HasValue ? "→" + action.AromaWpnItemId : "") +
                    (ItemName(action.AromaWpnItemId) is { Length: > 0 } an ? " (" + an + ")" : "") +
                    ", add=" + action.AddWpnUid +
                    (action.AddWpnItemId.HasValue ? "→" + action.AddWpnItemId : "") +
                    (ItemName(action.AddWpnItemId) is { Length: > 0 } ad ? " (" + ad + ")" : "") +
                    ", bait=" + action.BaitUid +
                    (action.BaitItemId.HasValue ? "→" + action.BaitItemId : "") +
                    (ItemName(action.BaitItemId) is { Length: > 0 } bn ? " (" + bn + ")" : ""));
            }
            catch (Exception ex)
            {
                Log("Ошибка сохранения craft: " + ex.Message);
            }
        }

        private string ItemName(uint? itemId)
            => itemId.HasValue ? (_deepParser.GetItem(itemId)?.Name ?? "") : "";

        private void OnBaseLoaded(int baseId)
        {
            _currentBaseId = baseId;
            _currentBaseSince = DateTime.Now; // OVERLAY
            _currentSpawnLocId = null;
            _currentPointId = null;
            Log("[LOCATION] Загрузка TFBase_" + baseId);
            CloseCastsOnMove("location load TFBase_" + baseId);
        }

        /// <summary>LOGFIX-03 (C): переход/телепорт — все снасти вышли из воды, открытые забросы закрываются как перезаброс.</summary>
        private void CloseCastsOnMove(string reason)
        {
            try { int n = _attempts?.CloseEnded(DateTime.Now, reason) ?? 0; if (n > 0) Log("[ATTEMPT] смена места: закрыто забросов " + n); }
            catch (Exception ex) { Log("[ATTEMPT] закрытие при смене места: " + ex.Message); }
        }

        private void OnSpawnPointDetected(int locId, int? pointId)
        {
            bool moved = _currentSpawnLocId.HasValue && (_currentSpawnLocId != locId || _currentPointId != pointId);   // LOGFIX-03 (C)
            _currentSpawnLocId = locId;
            _currentPointId = pointId;
            if (moved) CloseCastsOnMove("spawn point LocID=" + locId + (pointId.HasValue ? " pointid=" + pointId : ""));
            Log("[LOCATION] LocID=" + locId +
                (pointId.HasValue ? ", pointid=" + pointId : "") +
                (_currentBaseId.HasValue ? " (TFBase_" + _currentBaseId + ")" : ""));
        }

        private void OnItemUsed(uint itemId)
        {
            Log("[ITEM] Использован item_id=" + itemId +
                " (" + _deepParser.GetItem(itemId)?.Name + ")");
        }

        private void OnLogEquipment(string type, uint itemId)
        {
            _deepSession.UpdateEquipment(type, itemId);
            Log("[EQUIP] " + type + "=" + itemId +
                " (" + _deepParser.GetItem(itemId)?.Name + ")");
        }

        private void OnChumDetected(double x, double z)
        {
            Log("[CHUM] Точка: X=" + x.ToString("F2", CultureInfo.InvariantCulture) +
                ", Z=" + z.ToString("F2", CultureInfo.InvariantCulture));
        }

        private void OnGearAssetLoaded(uint itemId)
        {
            Log("[ASSET] WpnInGame=" + itemId +
                " (" + _deepParser.GetItem(itemId)?.Name + ")");
        }

        // NEXT-107: weight and model of the same Player.log catch block arrive together (or the model later).
        private void OnLocalCatchDetected(LocalCatchEvent ev)
        {
            if (ev == null || !CatchRules.IsRecordableWeight(ev.Weight)) return; // NEXT-OVERLAY-02: was < 50
            if (!IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(() => HandleLocalCatchWeight(ev)));
            }
            catch (InvalidOperationException) { }
        }

        private void OnLocalCatchSpeciesLate(long blockId, string? model, bool ambiguous)
        {
            if (!IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(() => HandleLocalCatchSpeciesLate(blockId, model, ambiguous)));
            }
            catch (InvalidOperationException) { }
        }

        /// <summary>FEAT-09: окно сопоставления одной рыбы между поклёвкой и посадкой (сом 7401 г вываживали 232 с — был дубль).</summary>
        internal const int LongFightSeconds = 900;

        private void HandleLocalCatchWeight(LocalCatchEvent ev)
        {
            int weight = ev.Weight;
            DateTime when = ev.When;
            var active = _deepSession.FindActiveByWeight((uint)weight, 2, when, 180, r => r.PlayerLogBlockId == null)
                         ?? _deepSession.FindSingleActiveByWeight((uint)weight, 2, when, LongFightSeconds, r => r.PlayerLogBlockId == null);   // FEAT-09: крупную рыбу вываживают > 3 мин
            if (active != null)
            {
                RememberPlayerLogBlock(ev.BlockId, active);
                bool changed = ApplyPlayerLogSpecies(active, ev);
                if (active.status == "waiting_chat" && pendingCatches.Contains(active))
                {
                    pendingCatches.Remove(active);
                    FinalizeCatch(active, notify: true, logPrefix: "PLAYERLOG");
                }
                else if (active.status == "not_landed")
                {
                    // NEXT-117: late Player.log landing confirmation revives a not_landed record.
                    if (!catches.Contains(active))
                    {
                        catches.Add(active);
                        sessionCatches.Add(active);
                    }
                    PersistSpeciesUpdate(active, "PLAYERLOG-LATE-LANDED");
                    Log("[LANDED] " + weight + "гр — поздняя посадка, not_landed→confirmed (" + CatchDisplay.Fish(active) + ")");
                }
                else if (changed && catches.Contains(active))
                {
                    PersistSpeciesUpdate(active, "PLAYERLOG");
                }
                Log("[PLAYERLOG] Вес " + weight + "гр уже есть в сессии (" +
                    CatchDisplay.Fish(active) + ")");
                return;
            }

            // NEXT-OVERLAY-02: только записи с реальным возрастом 0..15 с (не история с датой в будущем).
            var existing = catches
                .Where(c => Math.Abs(c.weight - weight) <= 2 &&
                            CatchRules.IsWithinLiveWindow(c.detected_at, DateTime.Now, LiveDedupWindowSeconds))
                .ToList();
            if (existing.Count > 0)
            {
                // NEXT-111: backfill a realtime/full record that has no species, only if unambiguous.
                if (existing.Count == 1 && !ev.Ambiguous && ev.ModelFile != null)
                {
                    var target = existing[0];
                    if (!target.fish_lang_id.HasValue && PlayerLogSpecies.IsNameMissing(target.fish_name))
                    {
                        string outcome = PlayerLogSpecies.Apply(target, ev.ModelFile, m => _deepParser.FindFishByModel(m));
                        if (outcome == "applied")
                        {
                            RememberPlayerLogBlock(ev.BlockId, target);
                            PersistSpeciesUpdate(target, "PLAYERLOG-BACKFILL");
                            Log("[PLAYERLOG] Вес " + weight + "гр уже записан, вид дополнен (" + CatchDisplay.Fish(target) + ")");
                            return;
                        }
                    }
                }
                // NEXT-107: which existing record this is cannot be proven -> the model is not attached.
                Log("[PLAYERLOG] Вес " + weight + "гр уже записан" +
                    (ev.ModelFile != null ? " (модель " + ev.ModelFile + " не привязана: запись неоднозначна)" : ""));
                return;
            }

            var catchData = new CatchData
            {
                timestamp = when.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                detected_at = when,
                weight = weight,
                grade = DetermineGrade(weight),
                status = "waiting_bait",
                source = "playerlog",
                autodetect_source = "none"
            };

            if (_currentBaseId.HasValue)
                catchData.location_id = (uint)_currentBaseId.Value;
            catchData.spawn_locid = _currentSpawnLocId;

            // NEXT-107: species of the same block before persistence and notification.
            ApplyPlayerLogSpecies(catchData, ev);
            RememberPlayerLogBlock(ev.BlockId, catchData);

            // Регистрируем до Finalize: CATCH_SUMMARY должен найти эту запись,
            // а не создавать второй SUMMARY_ONLY.
            _deepSession.Register(catchData);
            FinalizeCatch(catchData, notify: true, logPrefix: "PLAYERLOG");
        }

        /// <summary>NEXT-107: model line arrived after the catch was reported; only the same block's record is touched.</summary>
        private void HandleLocalCatchSpeciesLate(long blockId, string? model, bool ambiguous)
        {
            if (!_playerLogBlocks.TryGetValue(blockId, out var c))
            {
                Log("[SPECIES] модель " + (model ?? "?") + " для блока Player.log #" + blockId + ": запись не найдена — не привязана");
                return;
            }
            if (ambiguous)
            {
                if (PlayerLogSpecies.ClearIfFromPlayerLog(c))
                {
                    Log("[SPECIES] AMBIGUOUS: в блоке Player.log #" + blockId + " несколько моделей — вид снят (" + c.record_id + ")");
                    PersistSpeciesUpdate(c, "PLAYERLOG-AMBIGUOUS");
                }
                else
                    Log("[SPECIES] AMBIGUOUS: в блоке Player.log #" + blockId + " несколько моделей — вид не меняется");
                return;
            }
            string outcome = PlayerLogSpecies.Apply(c, model, m => _deepParser.FindFishByModel(m));
            Log("[SPECIES] Player.log #" + blockId + " модель " + model + " (позже): " + outcome +
                (outcome == "applied" ? " -> " + c.fish_name + " fish_lang_id=" + c.fish_lang_id : ""));
            if (outcome == "applied")
                PersistSpeciesUpdate(c, "PLAYERLOG-LATE");
        }

        private bool ApplyPlayerLogSpecies(CatchData c, LocalCatchEvent ev)
        {
            if (ev.Ambiguous)
            {
                Log("[SPECIES] AMBIGUOUS: в блоке Player.log #" + ev.BlockId + " модели " + string.Join(",", ev.Models) + " — вид не присваивается");
                return false;
            }
            if (ev.ModelFile == null) return false;
            string outcome = PlayerLogSpecies.Apply(c, ev.ModelFile, m => _deepParser.FindFishByModel(m));
            var found = _deepParser.FindFishByModel(ev.ModelFile);
            Log("[SPECIES] Player.log #" + ev.BlockId + " модель " + ev.ModelFile + ": " + outcome +
                (outcome == "applied" ? " -> " + c.fish_name + " fish_lang_id=" + c.fish_lang_id
                 : outcome == "ambiguous_mapping" ? " (" + string.Join(" / ", found.Select(f => f.Id + " " + f.Name)) + ")" : ""));
            return outcome == "applied";
        }

        private void RememberPlayerLogBlock(long blockId, CatchData c)
        {
            c.PlayerLogBlockId = blockId; // NEXT-112: in-memory block identity
            _playerLogBlocks[blockId] = c;
            if (_playerLogBlocks.Count > 64)
                foreach (var k in _playerLogBlocks.Keys.OrderBy(k => k).Take(_playerLogBlocks.Count - 64).ToList())
                    _playerLogBlocks.Remove(k);
        }

        /// <summary>NEXT-107: an already persisted record got or lost its Player.log species.</summary>
        private void PersistSpeciesUpdate(CatchData c, string logPrefix)
        {
            if (IsFishNameMissing(c.fish_name) && !c.fish_lang_id.HasValue)
                c.status = "unknown";
            else if (c.status == "unknown" || c.status == "not_landed")
                c.status = string.IsNullOrEmpty(c.bait_name) ? "waiting_bait" : "confirmed";
            EnrichCatchStatic(c);
            _db?.UpdateCatch(c);
            if (!_suppressPersistence)
                SaveData();
            UpdateHistoryRow(c);
            Log("[SPECIES_UPDATE] " + logPrefix + " record_id=" + c.record_id + " status=" + c.status +
                " species=" + CatchDisplay.Fish(c) + " fish_lang_id=" + (c.fish_lang_id?.ToString() ?? "NULL"));
        }

        private void MergePlayerLogCatch(PacketDeepParser.DeepCatchData ev, CatchData existing)
        {
            // NEXT-107: a network species replaces a Player.log one completely (name and id together).
            PlayerLogSpecies.BeforeNetworkMerge(existing, ev.FishLangId, ev.FishName);
            if (ev.Weight.HasValue) existing.weight = (int)ev.Weight.Value;
            if (!string.IsNullOrWhiteSpace(ev.FishName)) existing.fish_name = ev.FishName;
            existing.fish_lang_id = ev.FishLangId ?? existing.fish_lang_id;
            existing.offset_32 = ev.FishId.HasValue ? (int)ev.FishId.Value : existing.offset_32;
            existing.baseid = ev.BaseId ?? existing.baseid;
            existing.quality = ev.Quality ?? existing.quality;
            existing.quality_float = ev.QualityFloat ?? existing.quality_float;
            existing.bait_id = ev.BaitId ?? existing.bait_id;
            if (!string.IsNullOrWhiteSpace(ev.BaitName)) existing.bait_name = ev.BaitName;
            existing.depth_cm = ev.DepthCm ?? existing.depth_cm;
            existing.cast_x = ev.CastX ?? existing.cast_x;
            existing.cast_y = ev.CastY ?? existing.cast_y;
            existing.cast_z = ev.CastZ ?? existing.cast_z;
            existing.air_temperature_c = ev.AirTemperatureC ?? existing.air_temperature_c;
            existing.wind_ms = ev.WindMs ?? existing.wind_ms;
            existing.humidity = ev.Humidity ?? existing.humidity;
            existing.experience_formula = ev.PreferenceString ?? existing.experience_formula;
            existing.bait_uid = ev.BaitUid ?? existing.bait_uid;
            existing.range_values_json = ev.RangeValues.Count == 0
                ? existing.range_values_json
                : "[" + string.Join(",", ev.RangeValues.Select(v =>
                    v.ToString("R", CultureInfo.InvariantCulture))) + "]";
            existing.source = "playerlog+deep";
            existing.autodetect_source = "full";

            // NEXT-SESSION-23K: восстановить локацию из Player.log, если её ещё нет.
            if (!existing.location_id.HasValue && _currentBaseId.HasValue)
                existing.location_id = (uint)_currentBaseId.Value;
            if (!existing.spawn_locid.HasValue && _currentSpawnLocId.HasValue)
                existing.spawn_locid = _currentSpawnLocId;

            if (ev.Equipment != null)
            {
                existing.rod_id = ev.Equipment.RodId ?? existing.rod_id;
                existing.reel_id = ev.Equipment.ReelId ?? existing.reel_id;
                existing.line_id = ev.Equipment.LineId ?? existing.line_id;
                existing.hook_id = ev.Equipment.HookId ?? existing.hook_id;
                existing.float_id = ev.Equipment.FloatId ?? existing.float_id;
                existing.feeder_id = ev.Equipment.FeederId ?? existing.feeder_id;
                existing.equipment_source = ev.Equipment.Source;
            }

            EnrichCatchStatic(existing);
            if (!catches.Contains(existing))
                FinalizeCatch(existing, notify: true, logPrefix: "PLAYERLOG+FULL");
            else
            {
                _db?.UpdateCatchAsync(existing);
                BeginInvoke(new Action(() => UpdateHistoryRow(existing)));
            }
        }

        // ------------------------------------------------------------------
        // Глубокий парсер: реалтайм
        // ------------------------------------------------------------------

        // NEXT-SESSION-23H: дедуп ранних поклёвок — по SourceIdentity (SHA-256 кадра),
        // фолбэк на вес+вид+сек, если идентичность не передана.
        // FEAT-10: кадр поклёвки не содержит времени — та же рыба (вес/вид/класс), клюнувшая снова, даёт побайтно тот же кадр
        // (бас 862 г: 13:17:59 сошёл, 13:27:25 клюнул снова → окно не показалось). Повтором считаем только кадр в пределах окна.
        private readonly Dictionary<string, DateTime> _earlyBiteSeen = new(StringComparer.Ordinal);
        internal const double EarlyBiteRepeatSeconds = 3.0;

        /// <summary>FEAT-10: true — это повтор того же кадра (ретрансляция/второй путь разбора), окно не нужно.</summary>
        internal static bool IsRepeatBite(Dictionary<string, DateTime> seen, string key, DateTime at)
        {
            lock (seen)
            {
                if (seen.Count > 2000)
                    foreach (var k in seen.Where(p => Math.Abs((at - p.Value).TotalMinutes) > 10).Select(p => p.Key).ToList()) seen.Remove(k);
                bool repeat = seen.TryGetValue(key, out var prev) && Math.Abs((at - prev).TotalSeconds) <= EarlyBiteRepeatSeconds;
                seen[key] = at;
                return repeat;
            }
        }

        // NEXT-SESSION-51: последние ранние поклёвки (вес → время) — только чтобы
        // не показывать второе «Неизвестно (синт. …)» уведомление для того же события.
        private readonly Dictionary<int, DateTime> _recentBiteNotices = new();

        private bool WasEarlyBiteForSameEvent(int weight, DateTime when)
        {
            lock (_recentBiteNotices)
            {
                foreach (var kv in _recentBiteNotices)
                {
                    if (CatchIdentityPolicy.HasSafeNotificationRelation(kv.Key, kv.Value, weight, when))
                        return true;
                }
                if (_recentBiteNotices.Count > 32)
                {
                    var old = _recentBiteNotices.Where(kv => (DateTime.Now - kv.Value).TotalSeconds > 30)
                        .Select(kv => kv.Key).ToList();
                    foreach (var k in old) _recentBiteNotices.Remove(k);
                }
            }
            return false;
        }

        private void OnEarlyBite(PacketDeepParser.DeepCatchData ev)
        {
            try
            {
                if (!ev.Weight.HasValue) return;
                string key = !string.IsNullOrEmpty(ev.SourceIdentity)
                    ? "src|" + ev.SourceIdentity
                    : $"fb|{ev.Weight}|{ev.FishLangId}|{ev.Timestamp:yyyyMMddHHmmss}";
                if (IsRepeatBite(_earlyBiteSeen, key, ev.Timestamp)) return;   // FEAT-10: по времени, а не навсегда

                // NEXT-SESSION-51: запомнить раннюю поклёвку (вес+время пакета) для подавления
                // дублирующего realtime-уведомления. Записи БД это НЕ сливает.
                lock (_recentBiteNotices)
                {
                    _recentBiteNotices[(int)ev.Weight.Value] = ev.Timestamp;
                }

                if (IsHandleCreated)
                {
                    var biteData = new CatchData
                    {
                        weight = (int)ev.Weight.Value,
                        fish_name = ev.FishName ?? "",
                        fish_lang_id = ev.FishLangId,
                        grade = "",
                        source = "early_bite",
                        status = "bite",
                        // FIX-02: class of THIS fish from the bite packet (s2c 1120 flag=1, CONFIRMED) — shown in the popup
                        windex = ev.Bite != null ? ev.Bite.Windex : ev.Windex.HasValue ? (int)ev.Windex.Value : null,
                        timestamp = ev.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                        detected_at = ev.Timestamp
                    };
                    EnrichCatchStatic(biteData);
                    _biteRods.AddOrUpdate(biteData, new StrongBox<int?>(ev.RodIndex));   // NOTIFY-02: окно этой удочки
                    try
                    {
                        BeginInvoke(new Action(() => ShowCatchNotification(biteData)));
                    }
                    catch (InvalidOperationException) { }
                }

                Log("РАННЯЯ ПОКЛЁВКА: " +
                    (string.IsNullOrEmpty(ev.FishName) ? "(вид?)" : ev.FishName) +
                    " " + ev.Weight + "гр");
            }
            catch (Exception ex)
            {
                Log("Ошибка ранней поклёвки: " + ex.Message);
            }
        }

        private void OnDeepFullCatch(PacketDeepParser.DeepCatchData ev)
        {
            try
            {
                if (!ev.Weight.HasValue) return;

                // Player.log мог создать запись раньше FULL_CATCH (или наоборот).
                var playerLogCatch = _deepSession.FindActiveByWeight(ev.Weight.Value, 2, ev.Timestamp, 180, r => r.PlayerLogBlockId.HasValue && !r.SummaryUid.HasValue)
                                      ?? _deepSession.FindSingleActiveByWeight(ev.Weight.Value, 2, ev.Timestamp, LongFightSeconds, r => r.PlayerLogBlockId.HasValue && !r.SummaryUid.HasValue);
                if (playerLogCatch != null &&
                    playerLogCatch.source.StartsWith("playerlog", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsHandleCreated)
                    {
                        try
                        {
                            BeginInvoke(new Action(() => MergePlayerLogCatch(ev, playerLogCatch)));
                        }
                        catch (InvalidOperationException) { }
                    }
                    return;
                }

                var catchData = DeepCatchSession.ToCatchData(ev);
                catchData.status = "waiting_chat";
                catchData.chatDeadline = DateTime.Now.AddMilliseconds(CHAT_WAIT_MS);
                catchData.autoDetected = !string.IsNullOrEmpty(catchData.fish_name);
                catchData.source = "deep";

                // Иконка/название/локация нужны уже для мгновенного уведомления.
                EnrichCatchStatic(catchData);

                int catchId = Interlocked.Increment(ref catchIdCounter);
                waitingForChat[catchId] = catchData;
                _deepSession.Register(catchData);

                string name = CatchDisplay.Fish(catchData);
                string autoText = catchData.autoDetected ? " [АВТО]" : "";
                string baitText = string.IsNullOrEmpty(catchData.bait_name) ? "" : " наживка=" + catchData.bait_name;
                string depthText = catchData.depth_cm.HasValue
                    ? " глубина=" + (catchData.depth_cm.Value / 100f).ToString("F2") + "м"
                    : "";
                string coordsText = FormatCoords(catchData);
                string weatherText = FormatWeather(catchData);
                Log("ПОИМКА: " + name + " " + catchData.weight + "гр (" + catchData.grade + ")" +
                    autoText + baitText + depthText +
                    (coordsText.Length > 0 ? " заброс=" + coordsText : "") +
                    (weatherText.Length > 0 ? " погода=" + weatherText : "") +
                    " ID=" + (catchData.offset_32?.ToString() ?? "N/A"));

                // Уведомление не ждёт CATCH_SUMMARY/ручного подтверждения.
                if (!_suppressPersistence && IsHandleCreated)
                {
                    try
                    {
                        BeginInvoke(new Action(() => ShowCatchNotification(catchData)));
                    }
                    catch (InvalidOperationException) { }
                }

                // Не ждём 500-мс таймер: сразу показываем поимку в таблице.
                if (_autoConfirm && catchData.autoDetected && IsHandleCreated)
                {
                    try
                    {
                        BeginInvoke(new Action(ProcessPending));
                    }
                    catch (InvalidOperationException) { }
                }
            }
            catch (Exception ex)
            {
                Log("Ошибка обработки глубокой поимки: " + ex.Message);
            }
        }

        private void OnSummaryOnly(PacketDeepParser.DeepCatchData ev)
        {
            try
            {
                var catchData = DeepCatchSession.ToCatchData(ev);
                if (!CatchRules.IsRecordableWeight(catchData.weight)) return; // NEXT-OVERLAY-02: was < 50
                catchData.status = "confirmed";
                catchData.source = "deep-summary";
                catchData.autoDetected = true;
                if (string.IsNullOrEmpty(catchData.autodetect_source))
                    catchData.autodetect_source = catchData.fish_lang_id.HasValue
                        ? "fish_lang_id" : "none";

                // Регистрируем сразу, чтобы поздний FULL_CATCH не создал дубль,
                // а DrainOldOrphans увидел уже обработанную сводку.
                _deepSession.Register(catchData);

                if (!IsHandleCreated)
                {
                    // NEXT-26: headless/реплей — добавить в catches, чтобы ReplayPcap собрал SUMMARY_ONLY.
                    if (!catches.Contains(catchData)) catches.Add(catchData);
                    return;
                }
                BeginInvoke(new Action(() =>
                {
                    // NEXT-SESSION-23U: убираем pending-запись ТОЛЬКО при уникальном
                    // совпадении по ТОЧНОМУ весу. Разные поимки с близким весом (5000/5001)
                    // или с одинаковым весом (неоднозначно) НЕ удаляются автоматически —
                    // остаются для ручного выбора.
                    var exactMatches = pendingCatches.Where(p => p.weight == catchData.weight).ToList();
                    if (exactMatches.Count == 1)
                        pendingCatches.Remove(exactMatches[0]);

                    // NEXT-112: repeat summary is suppressed by event identity (uid), not by weight alone.
                    if (catchData.SummaryUid.HasValue && catches.Any(c => c.SummaryUid == catchData.SummaryUid))
                    {
                        Log("[DEDUP] CATCH_SUMMARY uid=" + catchData.SummaryUid.Value + " уже записан — пропущен");
                        return;
                    }
                    // NEXT-OVERLAY-02: повтор по весу — только запись «только что» (возраст 0..15 с).
                    // Раньше условие (Now - detected_at) < 15 было истинно и для записей с датой в будущем
                    // (импорт PCAP с годом 3995), и независимая сводка терялась. Запись с ДРУГИМ uid сводки —
                    // другая поимка (NEXT-112: uid = идентичность события), она не считается повтором.
                    var weightDup = catches.FirstOrDefault(c => Math.Abs(c.weight - catchData.weight) <= 2 &&
                                         CatchRules.IsWithinLiveWindow(c.detected_at, DateTime.Now, LiveDedupWindowSeconds) &&
                                         !(c.SummaryUid.HasValue && catchData.SummaryUid.HasValue &&
                                           c.SummaryUid.Value != catchData.SummaryUid.Value));
                    if (weightDup != null && MergeSummaryInto(weightDup, catchData))
                    {
                        // LOGFIX-02: запись уже создал Player.log («Неизвестно»), а сводка знает вид, класс и заброс — дополняем, а не выбрасываем
                        string prev = weightDup.status;
                        if (prev is "unknown" or "manual" or "not_landed" or "waiting_bait" && CatchIdentityPolicy.CanPromoteManualToConfirmed(weightDup.fish_name, weightDup.fish_lang_id, true))
                        { weightDup.status = "confirmed"; pendingCatches.Remove(weightDup); }
                        _db?.UpdateCatchAsync(weightDup);
                        Log("[MERGE] CATCH_SUMMARY " + catchData.weight + "гр дополнила запись " + weightDup.record_id + ": " + CatchDisplay.Fish(weightDup) +
                            (weightDup.cast_id.HasValue ? ", заброс #" + weightDup.cast_id : "") + " (" + prev + "→" + weightDup.status + ")");
                        UpdateHistoryRow(weightDup); UpdatePendingList();
                        if (!_suppressPersistence) SaveData();
                        return;
                    }
                    if (weightDup != null)
                    {
                        Log("[DEDUP] CATCH_SUMMARY " + catchData.weight + "гр" +
                            (catchData.SummaryUid.HasValue ? " uid=" + catchData.SummaryUid.Value : " без uid") +
                            " — повтор записи " + weightDup.weight + "гр (" + weightDup.timestamp + ", record_id=" +
                            weightDup.record_id + ") — пропущен");
                        return;
                    }

                    FinalizeCatch(catchData, notify: true, logPrefix: "SUMMARY");
                    UpdatePendingList();
                }));
            }
            catch (Exception ex)
            {
                Log("Ошибка SUMMARY_ONLY: " + ex.Message);
            }
        }

        /// <summary>
        /// LOGFIX-02: дополнить запись-«повтор» данными сводки — только пустые поля (вид, класс, заброс, координаты, наживка, снасть).
        /// false — дополнять нечего (вид и заброс уже известны) → прежнее «повтор, пропущен».
        /// </summary>
        internal static bool MergeSummaryInto(CatchData target, CatchData src)
        {
            bool knownFish = target.fish_lang_id.HasValue;
            if (knownFish && target.cast_id.HasValue) return false;
            if (!src.fish_lang_id.HasValue && !src.cast_id.HasValue) return false;
            var skip = new HashSet<string> { nameof(CatchData.record_id), nameof(CatchData.timestamp), nameof(CatchData.detected_at), nameof(CatchData.status), nameof(CatchData.source),
                nameof(CatchData.notification_shown), nameof(CatchData.notification_note), nameof(CatchData.chatDeadline), nameof(CatchData.autoDetected), nameof(CatchData.PlayerLogBlockId),
                nameof(CatchData.weight), nameof(CatchData.exp), nameof(CatchData.grade) };
            bool changed = false;
            foreach (var p in typeof(CatchData).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite || skip.Contains(p.Name)) continue;
                object? tv = p.GetValue(target), sv = p.GetValue(src);
                if (sv == null || sv is string ss && ss.Length == 0) continue;
                bool empty = tv == null || tv is string ts && (ts.Length == 0 || (p.Name == nameof(CatchData.fish_name) && !knownFish));
                if (!empty) continue;
                p.SetValue(target, sv); changed = true;
            }
            return changed;
        }

        private void OnDeepCatchEnriched(CatchData data)
        {
            // FULL_CATCH уже сохранён, сводка дописала наживку — снимаем «...».
            if (data.status == "waiting_bait")
            {
                data.status = "confirmed";
            }
            else if ((data.status == "manual" || data.status == "unknown" || data.status == "not_landed") &&
                     CatchIdentityPolicy.CanPromoteManualToConfirmed(data.fish_name, data.fish_lang_id, true))
            {
                // NEXT-SESSION-51/54B: сводка дала надёжный вид для ЭТОЙ записи — ручной выбор не нужен.
                // Только при известном виде; одна наживка (без вида) статус НЕ меняет.
                string prevStatus = data.status;
                data.status = "confirmed";
                pendingCatches.Remove(data);
                Log("[STATE] " + prevStatus + "→confirmed (вид из сводки): " +
                    CatchDisplay.Fish(data) + " " + data.weight + "гр");
            }

            _db?.UpdateCatchAsync(data);

            try
            {
                Log("[UPDATE] Наживка: " + CatchDisplay.Fish(data) +
                    " " + data.weight + "гр → " +
                    (string.IsNullOrEmpty(data.bait_name) ? "?" : data.bait_name) +
                    (data.bait_id.HasValue ? " (ID " + data.bait_id + ")" : "") +
                    (data.quality.HasValue ? ", quality=" + data.quality : ""));
            }
            catch
            {
                // Логирование не должно ломать захват.
            }

            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(() =>
                {
                    UpdateHistoryRow(data);
                    UpdatePendingList();
                    if (!_suppressPersistence &&
                        (data.status == "confirmed" || data.status == "skipped"))
                    {
                        SaveData();
                    }
                }));
            }
            catch (InvalidOperationException) { }
        }

        private string DetermineGrade(int weight)
        {
            return CatchRules.DetermineGrade(weight);
        }

        private string DetermineGrade(int weight, string? fishName)
        {
            return CatchRules.DetermineGrade(weight, null, fishName);
        }

        /// <summary>NEXT-SESSION-56: ручной грейд (ComboBox) удалён — грейд всегда по весу.</summary>
        private string ResolveGrade(int weight)
        {
            return DetermineGrade(weight);
        }

        private int FindMarker(byte[] data, byte[] marker)
        {
            for (int i = 0; i <= data.Length - marker.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < marker.Length; j++)
                {
                    if (data[i + j] != marker[j])
                    {
                        found = false;
                        break;
                    }
                }
                if (found) return i;
            }
            return -1;
        }

        private void ProcessPending()
        {
            // 1. Сводки, для которых не пришла полная поимка (например, потерян кадр),
            // через 45 секунд становятся обычными записями.
            foreach (var orphanCatch in _deepSession.DrainOldOrphans(TimeSpan.FromSeconds(45)))
            {
                orphanCatch.status = "auto";
                orphanCatch.source = "deep-summary";

                if (!TryAutoConfirm(orphanCatch))
                {
                    pendingCatches.Add(orphanCatch);
                    Log("ПОИМКА (сводка): " +
                        CatchDisplay.Fish(orphanCatch) +
                        " " + orphanCatch.weight + "гр" +
                        (string.IsNullOrEmpty(orphanCatch.bait_name) ? "" : ", наживка " + orphanCatch.bait_name) +
                        (orphanCatch.depth_cm.HasValue ? ", глубина " + FormatDepth(orphanCatch) : ""));
                }
            }

            // 2. Чат myfish — ищем совпадения с ожидающими поимками.
            var chatItems = new List<ChatCatch>();
            while (chatQueue.TryDequeue(out var chat))
            {
                chatItems.Add(chat);
            }

            foreach (var chat in chatItems)
            {
                foreach (var kvp in waitingForChat)
                {
                    var catchData = kvp.Value;
                    if (catchData.autoDetected) continue;
                    if (catchData.status != "waiting_chat") continue;
                    if (Math.Abs(catchData.weight - chat.weight) > WEIGHT_TOLERANCE) continue;

                    catchData.fish_name = chat.fishName;
                    catchData.exp = chat.exp;
                    catchData.grade = DetermineGrade(chat.weight, chat.fishName);
                    catchData.status = "auto";
                    catchData.autoDetected = true;
                    waitingForChat.TryRemove(kvp.Key, out _);

                    if (!TryAutoConfirm(catchData))
                    {
                        pendingCatches.Add(catchData);
                        Log("АВТОДЕТЕКТ: " + chat.fishName + " " + chat.weight + "гр +" + chat.exp + "exp");
                    }
                    break;
                }
            }

            // 3. Автоопределённые поимки и таймауты ожидания чата.
            var now = DateTime.Now;
            foreach (var kvp in waitingForChat.ToList())
            {
                var catchData = kvp.Value;
                if (catchData.status != "waiting_chat") continue;

                if (catchData.autoDetected)
                {
                    catchData.status = "auto";
                    waitingForChat.TryRemove(kvp.Key, out _);

                    if (!TryAutoConfirm(catchData))
                    {
                        pendingCatches.Add(catchData);
                        Log("АВТО: " + CatchDisplay.Fish(catchData) + " " + catchData.weight + "гр");
                    }
                    continue;
                }

                if (now >= catchData.chatDeadline)
                {
                    catchData.grade = ResolveGrade(catchData.weight);
                    waitingForChat.TryRemove(kvp.Key, out _);
                    // NEXT-114: realtime bite that got no landing confirmation (no chat, no Player.log weight,
                    // no CATCH_SUMMARY) is NOT a landed catch. Mark "not_landed" and do not persist as UNKNOWN.
                    if (catchData.source == "realtime" || catchData.source == "deep")
                    {
                        catchData.status = "not_landed";
                        Log("[NOT_LANDED] " + catchData.weight + "гр — поклёвка без посадки, уловом не считается");
                        continue;
                    }
                    // NEXT-SESSION-54B: АВТО-ПЕРСИСТ без ручного выбора. Если вид неизвестен —
                    // статус "unknown" (без синтетического ID); запись сохраняется один раз
                    // (FinalizeCatch защищён catches.Contains). pending — для опционального уточнения.
                    FinalizeCatch(catchData, notify: false, logPrefix: "UNKNOWN");
                    pendingCatches.Add(catchData);
                    Log("ТАЙМАУТ: " + catchData.weight + "гр — сохранено как неизвестно (ручной выбор опционален)");
                }
            }

            // 4. Если автоподтверждение включили позже — дочищаем очередь.
            if (_autoConfirm && pendingCatches.Count > 0)
            {
                for (int i = pendingCatches.Count - 1; i >= 0; i--)
                {
                    var c = pendingCatches[i];
                    if (c.status == "manual") continue;
                    if (!IsFishKnown(c)) continue;

                    pendingCatches.RemoveAt(i);
                    FinalizeCatch(c, notify: true, logPrefix: "АВТОПОДТВЕРЖДЕНО");
                }
            }

            UpdatePendingList();
            UpdateStatus();
        }

        /// <summary>
        /// Через 90 секунд без CATCH_SUMMARY фиксируем наживку как
        /// «(не определена)» и снимаем статус waiting_bait.
        /// </summary>
        private void ProcessBaitTimeouts()
        {
            if (_suppressPersistence) return;

            var now = DateTime.Now;
            bool changed = false;

            foreach (var c in catches
                         .Where(c => sessionCatches.Contains(c) &&
                                     c.status == "waiting_bait" &&
                                     string.IsNullOrEmpty(c.bait_name))
                         .ToList())
            {
                if (c.detected_at == default) continue;
                if ((now - c.detected_at).TotalSeconds < BaitTimeoutSeconds) continue;

                c.status = "confirmed";
                c.bait_name = "(не определена)";
                _db?.UpdateCatch(c);
                UpdateHistoryRow(c);
                Log("[TIMEOUT] Наживка не пришла за " + BaitTimeoutSeconds + " сек: " +
                    CatchDisplay.Fish(c) + " " + c.weight + "гр");
                changed = true;
            }

            if (changed)
            {
                SaveData();
                UpdatePendingList();
                UpdateStatus();
            }
        }

        /// <summary>
        /// Сохраняет всё, что ещё не попало в историю: ожидание чата,
        /// ручную очередь и поимки с неполученной наживкой.
        /// </summary>
        private int SaveIncompleteCatches()
        {
            var toSave = new List<CatchData>();
            toSave.AddRange(waitingForChat.Values);
            toSave.AddRange(pendingCatches);

            // Только поимки ТЕКУЩЕЙ сессии: старые waiting_bait из истории
            // не должны попадать в статистику новой сессии.
            toSave.AddRange(catches.Where(c =>
                c.status == "waiting_bait" && sessionCatches.Contains(c)));

            int count = 0;
            foreach (var c in toSave.Distinct())
            {
                bool isNew = !catches.Contains(c);
                c.status = "incomplete";
                if (string.IsNullOrEmpty(c.bait_name) || c.bait_name == "...")
                    c.bait_name = "(не определена)";

                if (isNew)
                {
                    catches.Add(c);
                    ShowNewCatchInHistory(c);
                }
                else
                {
                    UpdateHistoryRow(c);
                }

                if (!sessionCatches.Contains(c))
                    sessionCatches.Add(c);

                _db?.SaveCatch(c);
                count++;
            }

            waitingForChat.Clear();
            pendingCatches.Clear();

            if (!_suppressPersistence)
                SaveData();

            return count;
        }

        private SessionStats BuildSessionStats()
        {
            var list = sessionCatches.Where(c => c.status != "skipped").ToList();
            var stats = new SessionStats
            {
                Total = list.Count,
                AverageWeight = list.Count > 0 ? list.Average(c => (double)c.weight) : 0
            };

            var topBait = list
                .Where(c => !string.IsNullOrEmpty(c.bait_name) &&
                            c.bait_name != "(не определена)" &&
                            c.bait_name != "...")
                .GroupBy(c => c.bait_name)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .FirstOrDefault();
            if (topBait != null)
            {
                stats.TopBait = topBait.Key;
                stats.TopBaitCount = topBait.Count();
            }

            var topFish = list
                .Where(c => IsFishKnown(c))
                .GroupBy(c => c.fish_name)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .FirstOrDefault();
            if (topFish != null)
            {
                stats.TopFish = topFish.Key;
                stats.TopFishCount = topFish.Count();
            }

            return stats;
        }

        private void ShowSessionStats(SessionStats stats, int incompleteCount)
        {
            string text =
                "Сессия завершена!\n\n" +
                "Всего поимок: " + stats.Total + "\n" +
                "Средний вес: " + stats.AverageWeight.ToString("F0", CultureInfo.InvariantCulture) + " гр\n\n" +
                "Топ наживка: " + stats.TopBait + " (" + stats.TopBaitCount + " поимок)\n" +
                "Топ рыба: " + stats.TopFish + " (" + stats.TopFishCount + " поимок)";

            if (incompleteCount > 0)
                text += "\n\nНезавершённых поимок сохранено: " + incompleteCount;

            UiText.Box(text, "Статистика сессии", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void EndSession()
        {
            if (_suppressPersistence)
            {
                Log("Завершение сессии недоступно в режиме реплея");
                return;
            }

            try
            {
                int incomplete = SaveIncompleteCatches();
                _db?.Flush();

                var stats = BuildSessionStats();
                if (_db != null)
                {
                    var dbStats = _db.GetSessionStats();
                    if (dbStats.Total >= stats.Total)
                        stats = dbStats;
                }

                ShowSessionStats(stats, incomplete);

                // MASTER-01: история поимок остаётся на экране и после завершения сессии (раньше таблица очищалась и
                // возвращалась только после перезапуска программы). Очищаются только счётчики сессии.
                RebuildHistoryGrid();
                pendingCatches.Clear();
                waitingForChat.Clear();
                chatQueue = new ConcurrentQueue<ChatCatch>();
                sessionCatches.Clear();
                streamBuffer.Clear();
                chatBuffer.Clear();
                _serverTcpReassembler.Clear();
                _clientTcpReassembler.Clear();
                lastCatchTime = DateTime.MinValue;
                lastChatParse = DateTime.MinValue;
                if (DiagRequested) _diagUntil = DateTime.Now.AddSeconds(300);   // AUDIT-04 (G1)
                catchIdCounter = 0;
                totalPackets = 0;
                serverPackets = 0;
                _deepSession.Reset();
                _deepParser.Reset();
                _ownWindex.Reset("new app session"); // NEXT-ANALYTICS-14
                try { _db?.FlushResearch(); } catch { }   // DATA-01
                try { _attempts?.CloseAll(DateTime.Now, "session ended by the user (end not observed)"); } // NEXT-ANALYTICS-04
                catch (Exception ex) { Log("[ATTEMPT] закрытие забросов: " + ex.Message); }
                _db?.StartNewSession();
            _currentCastByRod.Clear(); // NEXT-89 R4

                UpdatePendingList();
                UpdateStatus();
                Log("Сессия завершена. Данные сохранены. Начата новая сессия.");
            }
            catch (Exception ex)
            {
                Log("Ошибка завершения сессии: " + ex.Message);
            }
        }

        private static bool IsFishNameMissing(string? name)
        {
            string text = name?.Trim() ?? "";
            return text.Length == 0 ||
                   text == "???" ||
                   text == "?" ||
                   text.StartsWith("Fish_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFishKnown(CatchData c)
        {
            return !IsFishNameMissing(c.fish_name);
        }

        /// <summary>Автоподтверждение: только если включено и рыба известна.</summary>
        private bool TryAutoConfirm(CatchData catchData)
        {
            if (!_autoConfirm) return false;
            if (!IsFishKnown(catchData)) return false;

            FinalizeCatch(catchData, notify: true, logPrefix: "АВТОПОДТВЕРЖДЕНО");
            return true;
        }

        private void UpdatePendingList()
        {
            uiRefreshPending = true;
        }

        private void UpdateStatus()
        {
            uiRefreshPending = true;
        }

        private void UpdatePendingListNow()
        {
            if (pendingListBox == null) return;
            pendingListBox.Items.Clear();
            for (int i = 0; i < pendingCatches.Count; i++)
            {
                var c = pendingCatches[i];
                // NEXT-SESSION-54B: "unknown" уже сохранён; метка не подразумевает обязательный ручной выбор.
                // DESIGN-01: «Карась, 1,2 кг, на мотыль, глубина 3,0 м, +12 опыта»; без служебных меток
                var parts = new List<string> { c.status == "unknown" ? "вид не определён" : CatchDisplay.Fish(c), UiKit.Grams(c.weight) };
                if (!string.IsNullOrEmpty(CatchDisplay.Bait(c))) parts.Add("на " + CatchDisplay.Bait(c));
                if (c.depth_cm.HasValue) parts.Add(FormatDepth(c));
                if (c.exp > 0) parts.Add("+" + c.exp + " опыта");
                pendingListBox.Items.Add(new PendingItem(string.Join(", ", parts), c.fish_icon_path));
            }
        }

        private void UpdateStatusNow()
        {
            if (statusLabel == null) return;
            int waiting = waitingForChat.Count;
            int pending = pendingCatches.Count;
            if (pending > 0 && pendingCatches[0].status == "unknown")
            {
                // NEXT-SESSION-54B: запись уже сохранена; уточнение вида — опционально.
                var first = pendingCatches[0];
                statusLabel.Text = "Поймано " + UiKit.Grams(first.weight) + " — вид пока не определён, запись сохранена";
            }
            else if (pending > 0 && pendingCatches[0].status != "confirmed")
            {
                var first = pendingCatches[0];
                string autoText = first.autoDetected ? " (авто: " + CatchDisplay.Fish(first) + ")" : "";
                statusLabel.Text = "Поймано: " + (first.autoDetected ? CatchDisplay.Fish(first) + ", " : "") + UiKit.Grams(first.weight) + " — сохранено";
            }
            else if (waiting > 0)
            {
                statusLabel.Text = "Жду подтверждения поимки из чата игры: " + waiting;
            }
            else
            {
                statusLabel.Text = "Жду поимок…";
            }
        }


        private void ShowCatchNotification(CatchData catchData)
        {
            if (catchData == null || catchData.notification_shown) return;
            catchData.notification_shown = true;
            // NEXT-NOTIFY-01: only the early-bite popup is shown; the game shows its own confirmed-catch notification.
            // Every confirmed-catch path (realtime, FULL, SUMMARY, Player.log, auto-confirm) ends here, so this single
            // filter suppresses all of them. Catch processing/persistence happens before and independently of this call.
            if (!CatchNotificationPolicy.ShouldShowPopup(catchData)) return;
            // PRERELEASE-01: окно о поимке — только для выбранных классов (неизвестный класс показывается всегда)
            if (!DisplayFilter.Notify(catchData.windex, _appSettings.Display?.NotifyMinClass ?? 0)) return;
            try
            {
                DateTime? catchTime = catchData.detected_at == default
                    ? null
                    : catchData.detected_at;
                // NOTIFY-02: своё окно на каждую удочку (1 — слева, 2 — по центру, 3 — справа), звук по классу
                var ns = _appSettings.Notify ??= new NotifySettings();
                if (!ns.Enabled) return;
                int? rod = _biteRods.TryGetValue(catchData, out var rb) ? rb.Value : null;
                if (catchTime.HasValue) Log("[NOTIFY] задержка от поклёвки " + (DateTime.Now - catchTime.Value).TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) + " с");
                BiteToastManager.Show(catchData, rod, ns);
            }
            catch
            {
                // Уведомление не должно мешать сохранению поимки.
            }
        }

        /// <summary>
        /// NEXT-SESSION-54C: запись уже сохранена? По стабильному record_id
        /// (CatchData — класс без Equals, ссылочная проверка ловит только тот же экземпляр).
        /// </summary>
        private bool IsAlreadyPersisted(CatchData catchData)
        {
            if (catchData == null) return false;
            if (catches.Contains(catchData)) return true;
            if (string.IsNullOrEmpty(catchData.record_id)) return false;
            return catches.Any(c => c.record_id == catchData.record_id);
        }

        /// <summary>
        /// Единая точка сохранения поимки: JSON, CSV, таблица, уведомление.
        /// Статус waiting_bait означает, что FULL_CATCH уже показан, а
        /// CATCH_SUMMARY с наживкой ещё не пришёл (в таблице будет «...»).
        /// </summary>
        private void FinalizeCatch(CatchData catchData, bool notify, string logPrefix)
        {
            if (catchData == null) return;
            // NEXT-SESSION-54C: CatchData — класс без Equals/GetHashCode, поэтому catches.Contains
            // = ссылочное равенство. Проверяем стабильный record_id, чтобы другой экземпляр той же
            // установленной записи не добавил дубль в память/историю (БД защищена upsert по record_id).
            if (IsAlreadyPersisted(catchData)) return;

            catchData.grade = ResolveGrade(catchData.weight);
            // NEXT-SESSION-54B: неизвестный вид — явный статус "unknown" (без синтетического ID
            // и без выдуманного вида). Иначе — прежняя логика.
            if (IsFishNameMissing(catchData.fish_name) && !catchData.fish_lang_id.HasValue)
                catchData.status = "unknown";
            else
                catchData.status = string.IsNullOrEmpty(catchData.bait_name)
                    ? "waiting_bait"
                    : "confirmed";

            // Локация из Player.log: TFBase_N — база для location_map,
            // locid — внутренняя точка (например, 22 при TFBase_2).
            if (!catchData.location_id.HasValue && _currentBaseId.HasValue)
                catchData.location_id = (uint)_currentBaseId.Value;
            if (!catchData.spawn_locid.HasValue && _currentSpawnLocId.HasValue)
                catchData.spawn_locid = _currentSpawnLocId;
            catchData.location_base_id = _currentBaseId;

            EnrichCatchStatic(catchData);

            // NEXT-SESSION-56: диагностика источника вида и решения о персисте (не hot path:
            // FinalizeCatch вызывается на поимку, не на каждый пакет).
            if (!_loadingHistory)
            {
                string speciesSource = catchData.fish_lang_id.HasValue
                    ? ("fish_lang_id=" + catchData.fish_lang_id.Value)
                    : (!IsFishNameMissing(catchData.fish_name) ? ("name='" + catchData.fish_name + "'") : "UNKNOWN");
                Log("[PERSIST] record_id=" + catchData.record_id + " status=" + catchData.status +
                    " species=" + CatchDisplay.Fish(catchData) + " source=" + speciesSource);
            }

            if (string.IsNullOrWhiteSpace(catchData.autodetect_source))
            {
                if (catchData.offset_32.HasValue) catchData.autodetect_source = "full";
                else if (catchData.fish_lang_id.HasValue) catchData.autodetect_source = "fish_lang_id";
                else if (!IsFishNameMissing(catchData.fish_name)) catchData.autodetect_source = "chat";
                else catchData.autodetect_source = "none";
            }

            catches.Add(catchData);
            if (!sessionCatches.Contains(catchData))
                sessionCatches.Add(catchData);
            // Вставка новой поимки остаётся синхронной: она дешёвая и должна
            // завершиться раньше возможного UpdateCatch из CATCH_SUMMARY.
            _db?.SaveCatch(catchData);
            _ownWindex.OnCatchSaved(); // NEXT-ANALYTICS-09/14: idempotent (catch-first direction); no-op while the ID is not confirmed
            try { int n = _db?.AttachWindexFromBites() ?? 0; if (n > 0) Log("[BITE] windex из поклёвки (s2c 1120) записан у уловов: " + n); } // NEXT-BITE-IMPLEMENT-01
            catch (Exception ex) { Log("[BITE] windex из поклёвки не записан: " + ex.Message); }
            // NEXT-SESSION-FIX-11: сопоставление network_id→вид не проверено, поэтому
            // ручной выбор/финализация НЕ обновляют постоянную карту id_map.json.
            // Вид сохраняется только для текущего улова; существующие записи карты не удаляются.

            if (!_suppressPersistence)
                SaveData();

            ShowNewCatchInHistory(catchData);

            if (notify && !_suppressPersistence && IsHandleCreated)
                ShowCatchNotification(catchData);

            Log(logPrefix + ": " + CatchDisplay.Fish(catchData) +
                " " + catchData.weight + "гр (" + catchData.grade + ")" +
                (string.IsNullOrEmpty(catchData.bait_name)
                    ? " | ожидание наживки"
                    : " | " + catchData.bait_name));

            UpdatePendingList();
            UpdateStatus();
        }


        private void AnalyzeId()
        {
            var rows = BuildFishStats();
            int allCatches = catches.Count;
            int total = catches.Count(c => c.status != "skipped");
            int skipped = allCatches - total;
            var other = _db?.GetOtherPlayersSummary() ?? new OtherPlayersSummary();

            var dialog = new Form();
            dialog.Text = "Анализ ID";
            dialog.Size = new Size(1100, 700);
            dialog.MinimumSize = new Size(900, 520);   UiKit.FitToScreen(dialog);   // FEAT-08b
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.BackColor = Theme.Background;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));

            var header = new Label();
            header.Dock = DockStyle.Fill;
            header.Text = "FULL_CATCH: " + _deepSession.FullCatchCount +
                          "   |   SUMMARY_ONLY: " + _deepSession.SummaryOnlyCount +
                          "   |   Моих поимок: " + allCatches +
                          " (учтено " + total + (skipped > 0 ? ", skipped " + skipped : "") + ")" +
                          "   |   Уникальных рыб: " + rows.Count +
                          "   |   Чужих: " + other.TotalCatches +
                          "   |   Игроков: " + other.UniquePlayers;
            header.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            header.ForeColor = Theme.AccentGreen;
            header.TextAlign = ContentAlignment.MiddleLeft;
            header.Padding = new Padding(8, 0, 0, 0);

            DataGridView CreateGrid()
            {
                var g = new DataGridView();
                g.Dock = DockStyle.Fill;
                g.ReadOnly = true;
                g.AllowUserToAddRows = false;
                g.AllowUserToDeleteRows = false;
                g.RowHeadersVisible = false;
                g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
                g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
                g.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;
                g.ScrollBars = ScrollBars.Both;
                g.BackgroundColor = Theme.Surface;
                g.ForeColor = Color.White;
                g.EnableHeadersVisualStyles = false;
                g.ColumnHeadersDefaultCellStyle.BackColor = Theme.HeaderBar;
                g.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
                g.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
                g.DefaultCellStyle.BackColor = Theme.Surface;
                g.DefaultCellStyle.ForeColor = Color.White;
                g.DefaultCellStyle.Font = new Font("Consolas", 9.5f);
                g.AlternatingRowsDefaultCellStyle.BackColor = Theme.SurfaceAlt;
                g.AlternatingRowsDefaultCellStyle.ForeColor = Color.White;
                return g;
            }

            var tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Font = new Font("Segoe UI", 9f);

            var ownTab = new TabPage("Мои поимки");
            ownTab.BackColor = Theme.Background;
            ownTab.UseVisualStyleBackColor = false;

            var grid = CreateGrid();
            grid.Columns.Add("id", "ID");
            grid.Columns.Add(NewIconColumn()); // DESIGN: fish icon
            grid.Columns.Add("fish", "Рыба");
            grid.Columns.Add("count", "Кол-во");
            grid.Columns.Add("percent", "Процент (%)");
            grid.Columns.Add("avg", "Средний вес");
            grid.Columns.Add("min", "Мин вес");
            grid.Columns.Add("max", "Макс вес");
            grid.Columns.Add("nobait", "Без наживки");

            foreach (var r in rows)
            {
                grid.Rows.Add(
                    r.Id,
                    (object?)FishIconFor(r.Id, r.Fish) ?? DBNull.Value,
                    r.Fish,
                    r.Count,
                    r.Percent.ToString("F1", CultureInfo.InvariantCulture),
                    r.AvgWeight.ToString("F0", CultureInfo.InvariantCulture),
                    r.MinWeight,
                    r.MaxWeight,
                    r.NoBait);
            }

            var autoGroup = new GroupBox();
            autoGroup.Text = "Автодетект (все поимки из БД, включая прошлые сессии)";
            autoGroup.Dock = DockStyle.Fill;
            autoGroup.ForeColor = Color.White;
            autoGroup.BackColor = Theme.Background;
            autoGroup.Padding = new Padding(5);

            var autoLabel = new Label();
            autoLabel.Dock = DockStyle.Fill;
            autoLabel.Text = BuildAutoDetectText();
            autoLabel.Font = new Font("Segoe UI", 9f);
            autoLabel.ForeColor = Theme.AccentGreen;
            autoLabel.TextAlign = ContentAlignment.MiddleLeft;
            autoGroup.Controls.Add(autoLabel);

            var ownLayout = new TableLayoutPanel();
            ownLayout.Dock = DockStyle.Fill;
            ownLayout.ColumnCount = 1;
            ownLayout.RowCount = 2;
            ownLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            ownLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            ownLayout.Controls.Add(autoGroup, 0, 0);
            ownLayout.Controls.Add(grid, 0, 1);
            ownTab.Controls.Add(ownLayout);

            var otherTab = new TabPage("Поимки других игроков");
            otherTab.BackColor = Theme.Background;
            otherTab.UseVisualStyleBackColor = false;

            var otherLayout = new TableLayoutPanel();
            otherLayout.Dock = DockStyle.Fill;
            otherLayout.ColumnCount = 1;
            otherLayout.RowCount = 4;
            otherLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            otherLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
            otherLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            otherLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
            otherLayout.BackColor = Theme.Background;

            var fishTitle = new Label();
            fishTitle.Dock = DockStyle.Fill;
            fishTitle.Text = "Рыбы: частота у других, средний вес и сравнение с моими поимками";
            fishTitle.ForeColor = Theme.AccentGreen;
            fishTitle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            fishTitle.TextAlign = ContentAlignment.MiddleLeft;
            fishTitle.Padding = new Padding(6, 0, 0, 0);

            var fishGrid = CreateGrid();
            fishGrid.Columns.Add(NewIconColumn()); // DESIGN: fish icon
            fishGrid.Columns.Add("fish", "Рыба");
            fishGrid.Columns.Add("other_count", "Чужих");
            fishGrid.Columns.Add("other_avg", "Ср. чужой, г");
            fishGrid.Columns.Add("my_count", "Моих");
            fishGrid.Columns.Add("my_avg", "Ср. мой, г");
            fishGrid.Columns.Add("diff", "Разница, г");

            foreach (var s in other.FishStats)
            {
                fishGrid.Rows.Add(
                    (object?)FishIconFor(null, s.FishName) ?? DBNull.Value,
                    s.FishName,
                    s.OtherCount,
                    s.OtherAvgWeight.ToString("F0", CultureInfo.InvariantCulture),
                    s.MyCount,
                    s.MyAvgWeight.ToString("F0", CultureInfo.InvariantCulture),
                    s.AvgWeightDiff.ToString("+0;-0;0", CultureInfo.InvariantCulture));
            }

            var playerTitle = new Label();
            playerTitle.Dock = DockStyle.Fill;
            playerTitle.Text = "Игроки: топ по количеству поимок";
            playerTitle.ForeColor = Theme.AccentGreen;
            playerTitle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            playerTitle.TextAlign = ContentAlignment.MiddleLeft;
            playerTitle.Padding = new Padding(6, 0, 0, 0);

            var playerGrid = CreateGrid();
            playerGrid.Columns.Add("player", "Игрок");
            playerGrid.Columns.Add("clan", "Клан");
            playerGrid.Columns.Add("count", "Поимок");
            playerGrid.Columns.Add("avg", "Ср. вес, г");
            playerGrid.Columns.Add("last", "Последняя");

            foreach (var s in other.PlayerStats)
            {
                playerGrid.Rows.Add(
                    s.PlayerName,
                    s.Clan,
                    s.Count,
                    s.AvgWeight.ToString("F0", CultureInfo.InvariantCulture),
                    s.LastCatch);
            }

            otherLayout.Controls.Add(fishTitle, 0, 0);
            otherLayout.Controls.Add(fishGrid, 0, 1);
            otherLayout.Controls.Add(playerTitle, 0, 2);
            otherLayout.Controls.Add(playerGrid, 0, 3);
            otherTab.Controls.Add(otherLayout);

            tabs.TabPages.Add(ownTab);
            tabs.TabPages.Add(otherTab);

            var bottom = new FlowLayoutPanel();
            bottom.Dock = DockStyle.Fill;
            bottom.FlowDirection = FlowDirection.LeftToRight;
            bottom.Padding = new Padding(6, 8, 0, 0);
            bottom.BackColor = Theme.Background;

            var exportBtn = new Button();
            exportBtn.Text = "Экспорт моих";
            exportBtn.Width = 170;
            exportBtn.Height = 36;
            exportBtn.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            exportBtn.BackColor = Theme.ButtonPrimary;
            exportBtn.ForeColor = Color.White;
            exportBtn.FlatStyle = FlatStyle.Flat;
            exportBtn.Click += (s, e) => ExportFishStats(rows);
            bottom.Controls.Add(exportBtn);

            var exportOtherBtn = new Button();
            exportOtherBtn.Text = "Экспорт чужих";
            exportOtherBtn.Width = 170;
            exportOtherBtn.Height = 36;
            exportOtherBtn.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            exportOtherBtn.BackColor = Theme.ButtonSecondary;
            exportOtherBtn.ForeColor = Color.White;
            exportOtherBtn.FlatStyle = FlatStyle.Flat;
            exportOtherBtn.Click += (s, e) => ExportOtherPlayerStats(other);
            bottom.Controls.Add(exportOtherBtn);

            var closeBtn = new Button();
            closeBtn.Text = "Закрыть";
            closeBtn.Width = 120;
            closeBtn.Height = 36;
            closeBtn.Font = new Font("Segoe UI", 10f);
            closeBtn.BackColor = Theme.ButtonNeutral;
            closeBtn.ForeColor = Color.White;
            closeBtn.FlatStyle = FlatStyle.Flat;
            closeBtn.Click += (s, e) => dialog.Close();
            bottom.Controls.Add(closeBtn);

            root.Controls.Add(header, 0, 0);
            root.Controls.Add(tabs, 0, 1);
            root.Controls.Add(bottom, 0, 2);
            dialog.Controls.Add(root);
            dialog.AcceptButton = closeBtn;
            if (Theme.AppIcon != null) dialog.Icon = Theme.AppIcon; // DESIGN
            Theme.ApplyTree(dialog);
            grid.RowTemplate.Height = 30;
            fishGrid.RowTemplate.Height = 30;
            dialog.ShowDialog(this);

            Log("Анализ ID завершён: поимок " + allCatches +
                " (учтено " + total + ", skipped " + skipped + "), уникальных рыб " +
                rows.Count + ", чужих " + other.TotalCatches +
                ", FULL=" + _deepSession.FullCatchCount +
                ", SUMMARY_ONLY=" + _deepSession.SummaryOnlyCount);
        }

        private static string EffectiveAutoSource(CatchData c)
        {
            string source = c.autodetect_source ?? "";
            if (!string.IsNullOrWhiteSpace(source))
                return source;

            if (c.offset_32.HasValue) return "full";
            if (c.fish_lang_id.HasValue) return "fish_lang_id";
            if (!IsFishNameMissing(c.fish_name)) return "chat";
            return "none";
        }

        private string BuildAutoDetectText()
        {
            var list = catches.Where(c => c.status != "skipped").ToList();
            int total = list.Count;
            int full = list.Count(c => EffectiveAutoSource(c) == "full");
            int byFishLang = list.Count(c => EffectiveAutoSource(c).Contains("fish_lang_id"));
            int byHeuristic = list.Count(c => EffectiveAutoSource(c).Contains("heuristic"));
            int manual = list.Count(c => EffectiveAutoSource(c) == "manual");
            int chat = list.Count(c => EffectiveAutoSource(c) == "chat");
            int none = list.Count(c => EffectiveAutoSource(c) == "none");
            int withoutId = Math.Max(0, total - full);

            string Pct(int value) => total == 0
                ? "0%"
                : (100.0 * value / total).ToString("F0", CultureInfo.InvariantCulture) + "%";

            int allCatches = catches.Count;
            int skipped = allCatches - total;
            return "Всего поимок: " + total +
                   (skipped > 0 ? " (в БД " + allCatches + ", skipped " + skipped + ")" : "") +
                   "   |   С ID рыбы (FULL_CATCH): " + full + " (" + Pct(full) + ")" +
                   "   |   Без ID (SUMMARY/чат): " + withoutId + " (" + Pct(withoutId) + ")\n" +
                   "Определено по fish_lang_id: " + byFishLang +
                   "   |   по эвристике network_id: " + byHeuristic +
                   "   |   вручную: " + manual +
                   "   |   из чата: " + chat +
                   "   |   без определения: " + none;
        }

        private List<FishStatRow> BuildFishStats()
        {
            var source = catches.Where(c => c.status != "skipped").ToList();
            int total = source.Count;
            var rows = new List<FishStatRow>();

            foreach (var group in source.GroupBy(GetStatId))
            {
                var list = group.ToList();
                string fish = list
                    .GroupBy(c => CatchDisplay.Fish(c))
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key)
                    .Select(g => g.Key)
                    .FirstOrDefault() ?? "Неизвестно";

                rows.Add(new FishStatRow
                {
                    Id = group.Key,
                    Fish = fish,
                    Count = list.Count,
                    Percent = total > 0 ? list.Count * 100.0 / total : 0,
                    AvgWeight = list.Average(c => (double)c.weight),
                    MinWeight = list.Min(c => c.weight),
                    MaxWeight = list.Max(c => c.weight),
                    NoBait = list.Count(c =>
                        string.IsNullOrEmpty(c.bait_name) ||
                        c.bait_name == "..." ||
                        c.bait_name == "(не определена)")
                });
            }

            return rows
                .OrderByDescending(r => r.Count)
                .ThenBy(r => r.Id, StringComparer.Ordinal)
                .ToList();
        }

        private static string GetStatId(CatchData c)
        {
            if (c.offset_32.HasValue)
                return c.offset_32.Value.ToString(CultureInfo.InvariantCulture);
            if (c.fish_lang_id.HasValue)
                return "lang:" + c.fish_lang_id.Value.ToString(CultureInfo.InvariantCulture);
            return "—";
        }

        private void ExportFishStats(List<FishStatRow> rows)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("ID;Рыба;Кол-во;Процент;Средний вес;Мин вес;Макс вес;Без наживки");
                foreach (var r in rows)
                {
                    sb.AppendLine(string.Join(";", new[]
                    {
                        CsvField(r.Id),
                        CsvField(r.Fish),
                        r.Count.ToString(CultureInfo.InvariantCulture),
                        r.Percent.ToString("F1", CultureInfo.InvariantCulture),
                        r.AvgWeight.ToString("F0", CultureInfo.InvariantCulture),
                        r.MinWeight.ToString(CultureInfo.InvariantCulture),
                        r.MaxWeight.ToString(CultureInfo.InvariantCulture),
                        r.NoBait.ToString(CultureInfo.InvariantCulture)
                    }));
                }

                AppPaths.EnsureLogsDirectory();
                File.WriteAllText(AppPaths.FishStatsCsv, sb.ToString(), new UTF8Encoding(true));
                Log("Экспорт статистики: " + AppPaths.FishStatsCsv + " (" + rows.Count + " строк)");
                UiText.Box("Статистика сохранена:\n" + AppPaths.FishStatsCsv,
                    "Экспорт статистики", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("Ошибка экспорта статистики: " + ex.Message);
                UiText.Box("Не удалось сохранить статистику:\n" + ex.Message,
                    "Экспорт статистики", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ExportOtherPlayerStats(OtherPlayersSummary summary)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Рыба;Чужих;Ср. чужой, г;Моих;Ср. мой, г;Разница, г");
                foreach (var s in summary.FishStats)
                {
                    sb.AppendLine(string.Join(";", new[]
                    {
                        CsvField(s.FishName),
                        s.OtherCount.ToString(CultureInfo.InvariantCulture),
                        s.OtherAvgWeight.ToString("F0", CultureInfo.InvariantCulture),
                        s.MyCount.ToString(CultureInfo.InvariantCulture),
                        s.MyAvgWeight.ToString("F0", CultureInfo.InvariantCulture),
                        s.AvgWeightDiff.ToString("+0;-0;0", CultureInfo.InvariantCulture)
                    }));
                }

                sb.AppendLine();
                sb.AppendLine("Игрок;Клан;Поимок;Ср. вес, г;Последняя");
                foreach (var s in summary.PlayerStats)
                {
                    sb.AppendLine(string.Join(";", new[]
                    {
                        CsvField(s.PlayerName),
                        CsvField(s.Clan),
                        s.Count.ToString(CultureInfo.InvariantCulture),
                        s.AvgWeight.ToString("F0", CultureInfo.InvariantCulture),
                        CsvField(s.LastCatch)
                    }));
                }

                AppPaths.EnsureLogsDirectory();
                File.WriteAllText(AppPaths.OtherPlayersStatsCsv, sb.ToString(), new UTF8Encoding(true));
                Log("Экспорт чужих поимок: " + AppPaths.OtherPlayersStatsCsv);
                UiText.Box("Статистика чужих поимок сохранена:\n" + AppPaths.OtherPlayersStatsCsv,
                    "Экспорт чужих поимок", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("Ошибка экспорта чужих поимок: " + ex.Message);
                UiText.Box("Не удалось сохранить статистику:\n" + ex.Message,
                    "Экспорт чужих поимок", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string CsvField(string? value)
        {
            string text = value ?? "";
            if (text.IndexOf(';') < 0 && text.IndexOf('"') < 0 &&
                text.IndexOf('\r') < 0 && text.IndexOf('\n') < 0)
                return text;

            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        // ------------------------------------------------------------------
        // Импорт PCAP
        // ------------------------------------------------------------------

        private void ImportPcap()
        {
            using var dialog = new OpenFileDialog();
            dialog.Title = "Импорт PCAP/PCAPNG";
            dialog.Filter = "Захваты (*.pcap;*.pcapng)|*.pcap;*.pcapng|Все файлы (*.*)|*.*";
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            string path = dialog.FileName;
            Log("Импорт PCAP: " + path);

            Task.Run(() =>
            {
                try
                {
                    var result = PcapImportRunner.Run(path);
                    if (IsDisposed) return;
                    BeginInvoke(new Action(() => ApplyImportResult(result)));
                }
                catch (Exception ex)
                {
                    if (IsDisposed) return;
                    try
                    {
                        BeginInvoke(new Action(() => Log("Ошибка импорта PCAP: " + ex.Message)));
                    }
                    catch (InvalidOperationException) { }
                }
            });
        }

        private void ApplyImportResult(PcapImportResult result)
        {
            int added = 0;

            foreach (var c in result.Catches)
            {
                bool exists = catches.Any(x =>
                    x.timestamp == c.timestamp &&
                    x.weight == c.weight &&
                    x.fish_name == c.fish_name);
                if (exists) continue;

                c.status = "confirmed";
                c.source = "pcap";
                c.autoDetected = true;
                catches.Add(c);
                _db?.SaveCatch(c, "import");
                AddHistoryRow(c);   // импорт записи: сразу все, по правилу — после импорта

                if (c.offset_32.HasValue && !string.IsNullOrEmpty(c.fish_name) &&
                    !idMap.ContainsKey(c.offset_32.Value))
                {
                    idMap[c.offset_32.Value] = new IdMapEntry
                    {
                        id = c.offset_32.Value,
                        fish_name = c.fish_name,
                        count = 1
                    };
                }

                added++;
            }

            SaveData();

            string summary = "Импорт завершён: пакетов " + result.Packets +
                             ", событий " + result.Events +
                             ", поимок " + result.Catches.Count +
                             ", добавлено " + added;
            Log(summary);
            RebuildHistoryGrid();   // PRERELEASE-01
            if (statusLabel != null) statusLabel.Text = "Из записи трафика добавлено поимок: " + added;

            try
            {
                AppPaths.EnsureLogsDirectory();
                string reportPath = Path.Combine(AppPaths.LogsDirectory,
                    "import_" + Path.GetFileNameWithoutExtension(result.FilePath) + ".txt");
                File.WriteAllText(reportPath, result.Report, new UTF8Encoding(false));
                Log("Отчёт импорта: " + reportPath);
            }
            catch (Exception ex)
            {
                Log("Отчёт импорта не сохранён: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Таблица истории
        // ------------------------------------------------------------------

        /// <summary>FIX-02: time used to order the table: the catch timestamp if plausible (2020 … next year), else detection time,
        /// else the oldest possible (implausible dates such as 3995 go to the bottom).</summary>
        internal static DateTime HistorySortTime(CatchData c)
        {
            static bool Ok(DateTime t) => t.Year >= 2020 && t <= DateTime.Now.AddDays(1);
            if (DateTime.TryParse(c.timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) && Ok(t)) return t;
            if (c.detected_at != default && Ok(c.detected_at)) return c.detected_at;
            return DateTime.MinValue;
        }

        private void AddHistoryRow(CatchData c) => AddHistoryRow(c, resort: true);

        private Label? _historyHead;
        private ComboBox? _showBox;
        /// <summary>PRERELEASE-01: последний результат правила показа (для подписи и тестов).</summary>
        public DisplayResult<CatchData>? HistoryDisplay { get; private set; }

        /// <summary>MASTER-01 / PRERELEASE-01: заново показать уловы по правилу «Показывать» (по умолчанию — авто, до 200 строк).
        /// Скрытые уловы остаются в базе, статистике, советнике и формуле.</summary>
        private void RebuildHistoryGrid()
        {
            if (historyGrid == null || historyGrid.IsDisposed) return;
            var ds = _appSettings.Display ?? new DisplaySettings();
            var r = DisplayFilter.Apply(catches.Where(c => c.status != "skipped").OrderByDescending(HistorySortTime),
                                        c => c.windex, HistorySortTime, DateTime.Now, ds.Mode, ds.MaxRows);
            HistoryDisplay = r;
            historyGrid.SuspendLayout();
            // AUDIT-04 (B2): ширина колонок считается один раз после заполнения, а не на каждой строке
            var autoCols = historyGrid.AutoSizeColumnsMode; var autoRows = historyGrid.AutoSizeRowsMode;
            historyGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None; historyGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            try
            {
                historyGrid.Rows.Clear();
                foreach (var c in Enumerable.Reverse(r.Shown)) AddHistoryRow(c, resort: false);
                historyGrid.Sort(historyGrid.Columns["time"]!, System.ComponentModel.ListSortDirection.Descending);
            }
            finally { historyGrid.AutoSizeColumnsMode = autoCols; historyGrid.AutoSizeRowsMode = autoRows; }
            historyGrid.ResumeLayout();
            if (_historyHead != null)
            {
                string auto = ds.Mode == -1 && r.Effective > 0 ? "уловов много — показаны " + DisplayFilter.ModeName(r.Effective) + "; " : "";
                string hid = DisplayFilter.HiddenText(r);
                _historyHead.Tag = hid.Length > 0 ? auto + hid : "наведите на ячейку — подробности";
                _historyHead.Invalidate();
            }
        }

        /// <summary>PRERELEASE-01: новый улов — в таблицу по правилу показа (при «все» и без лимита — просто сверху).</summary>
        private void ShowNewCatchInHistory(CatchData c)
        {
            if (historyGrid == null || historyGrid.IsDisposed) return;
            var ds = _appSettings.Display ?? new DisplaySettings();
            if (ds.Mode == 0 && historyGrid.Rows.Count < ds.MaxRows) AddHistoryRow(c);
            else RebuildHistoryGrid();
        }

        private void AddHistoryRow(CatchData c, bool resort)
        {
            if (historyGrid == null || historyGrid.IsDisposed) return;

            EnrichCatchStatic(c);
            historyGrid.Rows.Insert(0);
            var row = historyGrid.Rows[0];
            row.Tag = c;
            row.DefaultCellStyle.BackColor = Theme.Surface;
            row.DefaultCellStyle.ForeColor = Color.White;
            FillHistoryRow(row, c);
            // FIX-02: keep the current order (by default: time, newest first) also for rows added later (e.g. PCAP import)
            if (resort && historyGrid.SortedColumn != null)
                historyGrid.Sort(historyGrid.SortedColumn, historyGrid.SortOrder == SortOrder.Ascending
                    ? System.ComponentModel.ListSortDirection.Ascending : System.ComponentModel.ListSortDirection.Descending);
        }

        private void UpdateHistoryRow(CatchData c)
        {
            if (historyGrid == null || historyGrid.IsDisposed) return;

            EnrichCatchStatic(c);
            foreach (DataGridViewRow row in historyGrid.Rows)
            {
                if (ReferenceEquals(row.Tag, c))
                {
                    FillHistoryRow(row, c);
                    return;
                }
            }
        }

        /// <summary>NEXT-HISTORY-UI-01 / FIX-01: the visible columns of the main catch table, in the required order.</summary>
        internal static readonly (string Name, string Header)[] HistoryColumns =
        {
            ("time", "Время"), ("fish", "Рыба"), ("weight", "Вес"), ("grade", "Класс"), ("bait", "Наживка"), ("groundbait", "Прикорм"),
            ("coords", "Координаты"), ("depth", "Глубина"), ("location", "Водоём"), ("cast", "Заброс"), ("weather", "Погода")
        };

        /// <summary>NEXT-DATA-01 / FIX-01: readable values of the columns (no IDs, packet fields or raw coordinates).</summary>
        internal static object?[] HistoryValues(CatchData c, string groundbait, string weather) => new object?[]
        {
            string.IsNullOrWhiteSpace(c.timestamp) ? "—" : c.timestamp,
            CatchDisplay.Fish(c),
            c.weight > 0 ? c.weight.ToString(CultureInfo.InvariantCulture) + " г" : "—",
            c.windex,                                   // NEXT-HISTORY-UI-01: raw windex (painted as a badge; null = unknown)
            FormatBait(c) is { Length: > 0 } b ? b : "—",
            groundbait,
            MapGridCoordinates.Format(c),               // NEXT-MAP-COORDINATES-02: grid for verified waterbodies, raw X/Z otherwise
            FormatDepth(c) is { Length: > 0 } d ? d : "—",
            FormatLocation(c),
            c.cast_id.HasValue ? "#" + c.cast_id.Value.ToString(CultureInfo.InvariantCulture) : "—",
            string.IsNullOrEmpty(weather) ? "—" : weather
        };

        private readonly Dictionary<long, CatchDbWriter.AppliedGroundbait> _gbMemo = new();

        private void FillHistoryRow(DataGridViewRow row, CatchData c)
        {
            CatchDbWriter.AppliedGroundbait? gb = null;
            // AUDIT-04 (B2): прикорм заброса не меняется после заброса — запоминаем найденный, без запроса к БД на каждую строку при каждом обновлении
            if (c.cast_id is long cid && _gbMemo.TryGetValue(cid, out var memo)) gb = memo;
            else
            {
                try { gb = _db?.GetAppliedGroundbait(c.cast_id); } catch (Exception ex) { Log("[HISTORY] прикорм не прочитан: " + ex.Message); }
                if (gb != null && c.cast_id is long cid2) _gbMemo[cid2] = gb;
            }
            var v = HistoryValues(c, GroundbaitDisplay.Cell(gb, ItemName), FormatWeatherWithFallback(c));
            for (int i = 0; i < HistoryColumns.Length; i++) row.Cells[HistoryColumns[i].Name].Value = v[i];
            // FIX-01: time / waterbody / cast / weather are columns now; only cell-specific details stay as ordinary tooltips
            row.Cells["grade"].ToolTipText = GradeBadge.Describe(c.windex);
            row.Cells["groundbait"].ToolTipText = GroundbaitDisplay.Tooltip(gb, ItemName, c.cast_id);
            row.Cells["coords"].ToolTipText = MapGridCoordinates.Tooltip(c);
        }

        // NEXT-HISTORY-UI-01: catalog name by catalog id (<tid>)
        private string? ItemName(long id) => id > 0 && id <= uint.MaxValue ? _deepParser.GetItem((uint)id)?.Name is { Length: > 0 } n ? n : null : null;

        // ---------------- DESIGN: fish icons in lists, map window ----------------

        private sealed class PendingItem
        {
            public PendingItem(string text, string? iconPath) { Text = text; IconPath = iconPath; }
            public string Text { get; }
            public string? IconPath { get; }
            public override string ToString() => Text;
        }

        private void DrawPendingItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || sender is not ListBox lb) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(selected ? Theme.Selection : (e.Index % 2 == 0 ? Theme.Surface : Theme.SurfaceAlt)))
                e.Graphics.FillRectangle(bg, e.Bounds);
            object item = lb.Items[e.Index];
            string text = item.ToString() ?? "";
            int x = e.Bounds.Left + 6;
            var icon = item is PendingItem pi ? IconLoader.Load(pi.IconPath) : null;
            if (icon != null)
            {
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(icon, new Rectangle(x, e.Bounds.Top + 2, e.Bounds.Height - 4, e.Bounds.Height - 4));
            }
            x += e.Bounds.Height + 4;
            UiText.DrawText(e.Graphics, text, lb.Font, new Rectangle(x, e.Bounds.Top, e.Bounds.Width - x, e.Bounds.Height),
                Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private static DataGridViewImageColumn NewIconColumn()
        {
            var col = new DataGridViewImageColumn();
            col.Name = "FishIcon";
            col.HeaderText = "";
            col.Width = 36;
            col.ImageLayout = DataGridViewImageCellLayout.Zoom;
            col.DefaultCellStyle.NullValue = null; // no "missing image" cross when the icon is absent
            return col;
        }

        /// <summary>
        /// Иконка по виду: только по fish_lang_id ("lang:N") или по имени вида; null, если нет.
        /// Числовой ID статистики — это network offset_32 (синтетика), его НЕЛЬЗЯ трактовать как вид.
        /// </summary>
        private Image? FishIconFor(string? id, string? name)
        {
            PacketDeepParser.FishInfo? fish = null;
            if (id != null && id.StartsWith("lang:", StringComparison.Ordinal) &&
                uint.TryParse(id.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint fid))
                fish = _deepParser.GetFish(fid);
            fish ??= _deepParser.GetFishByName(name);
            return fish == null ? null : IconLoader.Load(fish.IconPath);
        }

        private WaterbodyMapForm? _mapForm;
        private AnalyticsForm? _analyticsForm; // NEXT-ANALYTICS-01
        private AdvisorForm? _advisorForm;     // ADVISOR-01 (the assistant of the new window)
        private AdvisorForm? _goAdvisor;       // ui01_review3 #3: the old «Куда идти» tab of the service window has its own
        private SettingsForm? _settingsForm;   // ADVISOR-01
        private AppSettings _appSettings = AppSettings.Load();
        private DateTime _lastAdvisorLearn = DateTime.MinValue;
        private int _advisorLearnBusy;
        internal AppSettings AppSettingsForTest => _appSettings;

        // NEXT-ANDROID-01: Android-компаньон
        private Button? companionButton;
        private CompanionServer? _companion;
        private CompanionDialog? _companionDialog;

        /// <summary>
        /// NEXT-ANDROID-03: включить сервис телефона при запуске, только если пользователь оставил его включённым
        /// (CompanionSettings.StartOnLaunch) и есть сопряжённые устройства. Иначе ничего не делает.
        /// </summary>
        private void TryAutoStartCompanion()
        {
            try
            {
                var saved = CompanionSettings.Load();
                if (!saved.StartOnLaunch || saved.Devices.Count == 0) return;
                EnsureCompanion();
                if (_companion == null || _companion.IsRunning) return;
                _companion.Start(_companion.Settings.Port);
                Log("[COMPANION] Сервис включён автоматически (был включён при прошлом запуске), порт " + _companion.Settings.Port);
            }
            catch (Exception ex)
            {
                Log("[COMPANION] Автовключение не удалось: " + ex.Message);
            }
        }

        private void OpenCompanion()
        {
            try
            {
                EnsureCompanion();
                if (_companionDialog != null && !_companionDialog.IsDisposed) { _companionDialog.Activate(); return; }
                _companionDialog = new CompanionDialog(_companion!);
                _companionDialog.FormClosed += (s, e) => _companionDialog = null;
                _companionDialog.Show(this);
            }
            catch (Exception ex)
            {
                Log("[COMPANION] Ошибка: " + ex.Message);
            }
        }

        private void EnsureCompanion()
        {
            {
                if (_companion == null)
                {
                    _companion = new CompanionServer(() => new CompanionContext
                    {
                        CurrentDb = AppPaths.CatchDb,
                        SessionDir = AppPaths.BaseDirectory,
                        CurrentLocationId = _currentBaseId.HasValue && _currentBaseId.Value >= 0 ? (uint)_currentBaseId.Value : null,
                        ServerOffset = _serverOffset
                    }, _deepParser, Log);
                    _companion.StatusChanged += () =>
                    {
                        try
                        {
                            if (companionButton == null || companionButton.IsDisposed) return;
                            BeginInvoke(new Action(() => companionButton.Text = _companion?.IsRunning == true ? "Телефон: вкл" : "Телефон: выкл"));
                        }
                        catch { }
                    };
                }
            }
        }

        // ADVISOR-01 ---------------------------------------------------------------------------------
        private string AdvisorFishName(uint id) { try { return _deepParser.GetFish(id)?.Name ?? ""; } catch { return ""; } }
        private string AdvisorLocName(uint? id) { try { return id.HasValue ? _deepParser.GetLocation(id.Value)?.Name ?? "" : ""; } catch { return ""; } }

        private string AdvisorBaitName(long id) { try { return id >= 0 && id <= uint.MaxValue ? _deepParser.GetBait((uint)id)?.Name ?? _deepParser.GetItem((uint)id)?.Name ?? "" : ""; } catch { return ""; } }

        private void OpenAdvisor()
        {
            try
            {
                // DESIGN-02: советник живёт во вкладке «Куда идти»
                if (_pages != null && _pages.SelectedIndex != 1) { _pages.SelectedIndex = 1; return; }   // создаст советника при первом показе
                if (_goAdvisor != null && !_goAdvisor.IsDisposed) { _goAdvisor.ReloadData(learn: true); return; }
                _goAdvisor = new AdvisorForm(AppPaths.CatchDb, () => _appSettings, AdvisorFishName, AdvisorLocName,
                    loc => { OpenWaterbodyMap(); if (loc is uint l) _mapForm?.SelectWaterbody(l); }, () => OpenSettings("ИИ-агент"), Log)
                {
                    BaitName = AdvisorBaitName,
                    // BRIDGE-FB-01: «Другие игроки» → карта водоёма с их точками поверх
                    OpenReferencePoints = (loc, fish, pts) =>
                    {
                        OpenWaterbodyMap();
                        if (_mapForm != null && !_mapForm.ShowReferencePoints(loc, fish, pts)) Log("[MAP] водоём " + loc + " не найден в списке карты");
                    }
                };
                var ga = _goAdvisor;
                ga.FormClosed += (s, e) => { if (ReferenceEquals(_goAdvisor, ga)) _goAdvisor = null; };
                EmbedAdvisor(ga);
                Log("[ADVISOR] Окно советника открыто");
            }
            catch (Exception ex) { Log("[ADVISOR] Ошибка открытия советника: " + ex.Message); }
        }

        private void OpenSettings(string? section)
        {
            // ui01_review3 #18: one settings window — the one inside the new window (the old dialog would overwrite its changes)
            if (_nuShell != null && !_nuShell.IsDisposed) { ShowNuShell(); if (_nuShell.Mode != "settings") _nuShell.Press("settings"); return; }
            try
            {
                if (_settingsForm != null && !_settingsForm.IsDisposed) { _settingsForm.Activate(); return; }
                _settingsForm = new SettingsForm(_appSettings, s =>
                    {
                        _appSettings = s.Clone();
                        Log("[SETTINGS] Настройки сохранены: " + AppSettings.FilePath + (s.Agent.Enabled ? " · ИИ-агент включён (" + s.Agent.Model + ")" : ""));
                        try { if (_advisorForm != null && !_advisorForm.IsDisposed) _advisorForm.ReloadData(learn: false); } catch { }
                        try { if (_goAdvisor != null && !_goAdvisor.IsDisposed) _goAdvisor.ReloadData(learn: false); } catch { }
                        // PRERELEASE-01: правило показа истории
                        try { _tray?.ApplyHotkey(); } catch { }   // NOTIFY-02
                        try { if (_showBox != null) _showBox.SelectedIndex = Math.Clamp(_appSettings.Display.Mode + 1, 0, 3); RebuildHistoryGrid(); } catch { }
                    },
                    OverlaySummary, () => { if (_overlay != null) _overlay.ShowSettings(_settingsForm); },
                    PhoneSummary, OpenCompanion, section);
                _settingsForm.FormClosed += (s, e) => _settingsForm = null;
                _settingsForm.ShowDialog(this);
            }
            catch (Exception ex) { Log("[SETTINGS] Ошибка окна настроек: " + ex.Message); }
        }

        private string OverlaySummary()
        {
            if (_overlay == null) return "Оверлей не запущен.";
            var o = _overlay.Settings;
            return $"Сейчас: {(o.Enabled ? "включён" : "выключен")}, горячая клавиша {(o.HotkeyEnabled ? OverlaySettings.HotkeyText(o.HotkeyKey, o.HotkeyModifiers) : "отключена")}, " +
                   $"прозрачность {o.Opacity:P0}, радиус {o.RadiusMeters:0} м, {(_overlay.IsVisible ? "сейчас показан" : "сейчас скрыт")}.";
        }

        private string PhoneSummary()
        {
            EnsureCompanion();
            var c = _companion!;
            return $"Сейчас: сервис {(c.IsRunning ? "включён, порт " + c.Port : "выключен")}, сопряжённых устройств: {c.Settings.Devices.Count}, " +
                   $"запуск вместе с программой: {(c.Settings.StartOnLaunch ? "да" : "нет")}.";
        }

        /// <summary>Самообучение советника в фоне: раз в 10 минут, только чтение базы, запись — только analytics_models.json.</summary>
        private void AdvisorLearnTick()
        {
            try
            {
                if (!_appSettings.Analytics.AutoRetrain || (DateTime.Now - _lastAdvisorLearn).TotalMinutes < 10) return;
                if (Interlocked.Exchange(ref _advisorLearnBusy, 1) == 1) return;
                _lastAdvisorLearn = DateTime.Now;
                var settings = _appSettings.Analytics;
                Task.Run(() =>
                {
                    try
                    {
                        var an = BiteAnalytics.Load(AppPaths.CatchDb, settings, AdvisorFishName, AdvisorLocName, AdvisorBaitName);
                        var reg = ModelRegistry.Load(ModelRegistry.DefaultPath);
                        string before = reg.LastStatus; int activeBefore = reg.ActiveModel?.Version ?? 0;
                        if (an.Learn(reg))
                        {
                            reg.Save(ModelRegistry.DefaultPath);
                            int activeAfter = reg.ActiveModel?.Version ?? 0;
                            if (reg.LastStatus != before || activeAfter != activeBefore)
                                BeginInvoke(new Action(() => Log("[ADVISOR] " + (activeAfter != activeBefore ? (activeAfter > 0 ? "активна формула v" + activeAfter + ": " : "формула отключена: ") : "") + reg.LastStatus)));
                        }
                    }
                    catch (Exception ex) { try { BeginInvoke(new Action(() => Log("[ADVISOR] самообучение: " + ex.Message))); } catch { } }
                    finally { Interlocked.Exchange(ref _advisorLearnBusy, 0); }
                });
            }
            catch (Exception ex) { Log("[ADVISOR] самообучение: " + ex.Message); Interlocked.Exchange(ref _advisorLearnBusy, 0); }
        }

        private void OpenAnalytics()
        {
            try
            {
                if (_analyticsForm != null && !_analyticsForm.IsDisposed) { _analyticsForm.Activate(); _analyticsForm.LoadData(); return; }
                _analyticsForm = new AnalyticsForm(_deepParser, AppPaths.CatchDb, AppPaths.BaseDirectory);
                _analyticsForm.FormClosed += (s, e) => _analyticsForm = null;
                _analyticsForm.Show(this);
                Log("[ANALYTICS] Окно аналитики открыто");
            }
            catch (Exception ex)
            {
                Log("[ANALYTICS] Ошибка открытия аналитики: " + ex.Message);
            }
        }

        // ---------------- GAMEMAP-01: карта водоёма на весь экран поверх игры ----------------
        private WaterbodyMapForm? _gameMap;
        private IntPtr _gameMapPrev;
        private ReferenceProvider? _gameRefs;
        private readonly Dictionary<string, List<(double X, double Z, string G)>> _othersCache = new();

        /// <summary>Точки других игроков на водоёме: выбранный вид или все виды справочника (кэш на запуск).</summary>
        private List<(double X, double Z, string G)> OthersPoints(uint loc, uint? fish, string name)
        {
            string key = loc + "|" + (fish?.ToString() ?? "*");
            if (_othersCache.TryGetValue(key, out var c)) return c;
            var res = new List<(double X, double Z, string G)>();
            try
            {
                _gameRefs ??= new ReferenceProvider();
                IEnumerable<ReferenceHints?> hints = fish.HasValue ? new[] { _gameRefs.GetHintsForSpecies(fish, name) }
                                                                   : _gameRefs.ReferenceSpecies().Select(sp => _gameRefs.GetHintsForSpecies(sp.Id, sp.Name));
                foreach (var h in hints)
                    if (h != null)
                        foreach (var l in h.Locations.Where(l => l.LocationId == (int)loc && l.Points.Count > 0))
                            res.AddRange(AdvisorForm.RefWorldPoints(l.LocationId, l.Points));
                res = res.GroupBy(p => p.G).Select(g => g.First()).ToList();
            }
            catch (Exception ex) { Log("[MAP] точки других игроков: " + ex.Message); }
            _othersCache[key] = res;
            return res;
        }

        private readonly Dictionary<uint, Dictionary<string, List<(string fish, List<string> baits)>>> _othersInfo = new();

        /// <summary>GAMEMAP-02: точка другого игрока — какая рыба там поймана и на что эту рыбу обычно ловят (справочник; на что именно в этой точке — неизвестно).</summary>
        private string? OthersInfoAt(uint loc, string grid)
        {
            try
            {
                if (!_othersInfo.TryGetValue(loc, out var map))
                {
                    map = new Dictionary<string, List<(string fish, List<string> baits)>>();
                    _gameRefs ??= new ReferenceProvider();
                    foreach (var sp in _gameRefs.ReferenceSpecies())
                    {
                        var h = _gameRefs.GetHintsForSpecies(sp.Id, sp.Name);
                        if (h == null) continue;
                        var baits = h.Baits.OrderByDescending(b => b.Count).Take(3).Select(b => b.Name).Concat(h.Lures.OrderByDescending(b => b.Count).Take(1).Select(b => b.Name)).ToList();
                        foreach (var l in h.Locations.Where(l => l.LocationId == (int)loc))
                            foreach (var g in l.Points)
                            {
                                if (!map.TryGetValue(g, out var lst)) map[g] = lst = new();
                                if (!lst.Any(x => x.fish == sp.Name)) lst.Add((sp.Name, baits));
                            }
                    }
                    _othersInfo[loc] = map;
                }
                if (!map.TryGetValue(grid, out var here) || here.Count == 0) return null;
                var sb = new System.Text.StringBuilder();
                sb.Append("Здесь другие игроки ловили: " + string.Join(", ", here.Select(x => x.fish)) + ".");
                foreach (var (fish, baits) in here.Take(4))
                    if (baits.Count > 0) sb.Append("\r\n• " + fish + " — обычно ловят на: " + string.Join(", ", baits));
                sb.Append("\r\n(справочник игроков: на что именно в этой точке — не записано)");
                return sb.ToString();
            }
            catch (Exception ex) { Log("[MAP] точка других игроков: " + ex.Message); return null; }
        }

        private void ToggleGameMap()
        {
            try
            {
                if (_gameMap != null && !_gameMap.IsDisposed && _gameMap.Visible) { _gameMap.HideGame(); return; }
                var fg = GetForegroundWindowFg();
                if (fg != IntPtr.Zero && (_gameMap == null || fg != _gameMap.Handle)) _gameMapPrev = fg;
                if (_gameMap == null || _gameMap.IsDisposed)
                {
                    _gameMap = new WaterbodyMapForm(_deepParser, AppPaths.CatchDb, AppPaths.BaseDirectory, gameMode: true) { OthersProvider = OthersPoints, OthersInfo = OthersInfoAt, SpotsProvider = SpotsFor };
                    _gameMap.ReturnFocus = () => { if (_gameMapPrev != IntPtr.Zero) TrayController.ForceForeground(_gameMapPrev); };
                }
                var scr = _gameMapPrev != IntPtr.Zero ? Screen.FromHandle(_gameMapPrev) : Screen.FromPoint(Cursor.Position);
                _gameMap.Bounds = scr.Bounds;
                if (_currentBaseId is int cb && cb > 0) _gameMap.SelectWaterbody((uint)cb);   // водоём, где вы сейчас
                if (!_gameMap.Visible) _gameMap.Show();
                else _gameMap.LoadData(keepView: true);
                _gameMap.TopMost = true; _gameMap.TopMost = false;
                _gameMap.Activate();
                TrayController.ForceForeground(_gameMap.Handle);
                Log("[MAP] Карта на весь экран открыта");
            }
            catch (Exception ex) { Log("[MAP] Ошибка карты на весь экран: " + ex.Message); }
        }

        /// <summary>SPOTS-02: пятна шанса этой рыбалки на водоёме (для карты F12), только чтение.</summary>
        private List<ChanceSpots.Spot> SpotsFor(uint water)
        {
            try
            {
                using var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + AppPaths.CatchDb + ";Mode=ReadOnly;Pooling=False");
                c.Open();
                return ChanceSpots.Read(c, _db?.SessionId ?? "", water, DateTime.Now);
            }
            catch { return new List<ChanceSpots.Spot>(); }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern IntPtr GetForegroundWindowFg();

        private void OpenWaterbodyMap()
        {
            try
            {
                if (_mapForm != null && !_mapForm.IsDisposed) { _mapForm.Activate(); _mapForm.LoadData(keepView: true); return; }
                _mapForm = new WaterbodyMapForm(_deepParser, AppPaths.CatchDb, AppPaths.BaseDirectory);
                _mapForm.FormClosed += (s, e) => _mapForm = null;
                // FIX-05: первый раз — водоём, где вы сейчас (иначе форма сама выберет водоём последнего улова)
                if (_currentBaseId is int cb && cb > 0) _mapForm.SelectWaterbody((uint)cb);
                _mapForm.Show(this);
                Log("[MAP] Окно карты открыто");
            }
            catch (Exception ex)
            {
                Log("[MAP] Ошибка открытия карты: " + ex.Message);
            }
        }

        /// <summary>
        /// Дополняет запись статическими данными из fish_full/items_full/location_map.
        /// Идемпотентно; вызывается перед сохранением, таблицей и уведомлением.
        /// </summary>
        private void EnrichCatchStatic(CatchData c)
        {
            if (c == null) return;

            var fish = _deepParser.GetFish(c.fish_lang_id)
                       ?? _deepParser.GetFishByName(c.fish_name);
            if (fish != null)
            {
                bool fishMissing = IsFishNameMissing(c.fish_name);
                if (fishMissing && !string.IsNullOrWhiteSpace(fish.Name))
                {
                    c.fish_name = fish.Name;
                    if (string.IsNullOrEmpty(c.autodetect_source) ||
                        c.autodetect_source == "none")
                        c.autodetect_source = "fish_lang_id";
                    if (!_loadingHistory)
                    {
                        Log("[AUTODETECT] Рыба определена по fish_lang_id=" +
                            (c.fish_lang_id?.ToString() ?? "?") + ": " + fish.Name);
                    }
                }
                if (!c.fish_lang_id.HasValue && fish.Id > 0)
                    c.fish_lang_id = fish.Id;
                c.fish_icon_path = fish.IconPath;
                c.fish_min_weight = fish.HasWeight ? fish.MinWeight : null;
                c.fish_max_weight = fish.HasWeight ? fish.MaxWeight : null;
                c.fish_is_mutant = fish.IsMutant;
                c.fish_is_aggression = fish.IsAggression;
            }

            if (string.IsNullOrEmpty(c.autodetect_source) &&
                !c.offset_32.HasValue && c.fish_lang_id.HasValue)
                c.autodetect_source = "fish_lang_id";

            var bait = _deepParser.GetItem(c.bait_id);
            if (bait != null)
            {
                c.bait_icon_path = bait.IconPath;
                if (string.IsNullOrWhiteSpace(c.bait_name) ||
                    c.bait_name == "..." ||
                    c.bait_name == "(не определена)")
                    c.bait_name = bait.Name;
            }

            var location = _deepParser.GetLocation(c.location_id);
            if (location != null)
                c.location_name = location.Name;

            // NEXT-04B (A4): эвристика GuessNetworkId отключена.
            // network_id — синтетика из длины кадра; в новые записи он попадать не должен.
            // Исторические значения offset_32 в БД/CSV не переписываются.
        }

        private static string FormatBait(CatchData c)
        {
            return CatchDisplay.Bait(c);
        }

        private static string FormatGear(CatchData c)
        {
            var parts = new List<string>();
            if (c.rod_id.HasValue) parts.Add("У:" + c.rod_id.Value);
            if (c.reel_id.HasValue) parts.Add("К:" + c.reel_id.Value);
            if (c.line_id.HasValue) parts.Add("Л:" + c.line_id.Value);
            if (c.hook_id.HasValue) parts.Add("Кр:" + c.hook_id.Value);
            if (c.float_id.HasValue) parts.Add("П:" + c.float_id.Value);
            if (c.feeder_id.HasValue) parts.Add("Ф:" + c.feeder_id.Value);
            return string.Join(" ", parts);
        }

        private static string FormatDepth(CatchData c)
        {
            return c.depth_cm.HasValue
                ? (c.depth_cm.Value / 100f).ToString("F2", CultureInfo.InvariantCulture) + " м"
                : "";
        }

        private static string FormatCoords(CatchData c)
        {
            if (!c.cast_x.HasValue || !c.cast_y.HasValue || !c.cast_z.HasValue) return "";
            return string.Format(CultureInfo.InvariantCulture,
                "X={0:F1} Y={1:F1} Z={2:F1}", c.cast_x.Value, c.cast_y.Value, c.cast_z.Value);
        }

        private static string FormatLocation(CatchData c)
        {
            if (!string.IsNullOrWhiteSpace(c.location_name))
                return c.location_name;

            return c.location_id.HasValue
                ? "Неизвестная локация (ID " + c.location_id.Value + ")"
                : "Н/Д";
        }

        /// <summary>NEXT-GTWC-01: показ s2c1001 без единиц (единицы полей не подтверждены).</summary>
        private static string FormatWeather1001(WeatherRecord w)
        {
            string F(float? v, string fmt) => v.HasValue ? v.Value.ToString(fmt, CultureInfo.InvariantCulture) : "—";
            return "темп " + F(w.Tempr, "F1") + " · облачн " + F(w.Cloudly, "F2") + " · туман " + F(w.Fog, "F2") +
                   " · дождь " + F(w.Rain, "F2") + " · гроза " + F(w.Grom, "F2");
        }

        /// <summary>
        /// NEXT-GTWC-01: старые поля погоды улова (air_temp/wind/humidity — из устаревшего кадра kind=4 len 124), иначе
        /// ближайшая запись s2c1001 той же БД в пределах 2 минут от времени улова. Записи уловов не изменяются.
        /// </summary>
        private string FormatWeatherWithFallback(CatchData c)
        {
            string legacy = FormatWeather(c);
            if (legacy.Length > 0) return legacy;
            DateTime t = c.detected_at;
            if (t == default && !DateTime.TryParse(c.timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return "";
            WeatherRecord? best = null;
            lock (_weatherLock)
            {
                int lo = 0, hi = _weatherIndex.Count - 1;
                while (lo <= hi) { int mid = (lo + hi) / 2; if (_weatherIndex[mid].Timestamp < t) lo = mid + 1; else hi = mid - 1; }
                foreach (int i in new[] { lo - 1, lo })
                    if (i >= 0 && i < _weatherIndex.Count && Math.Abs((_weatherIndex[i].Timestamp - t).TotalSeconds) <= 120 &&
                        (best == null || Math.Abs((_weatherIndex[i].Timestamp - t).TotalSeconds) < Math.Abs((best.Timestamp - t).TotalSeconds)))
                        best = _weatherIndex[i];
            }
            return best == null ? "" : FormatWeather1001(best);
        }

        private static string FormatWeather(CatchData c)
        {
            var parts = new List<string>();
            if (c.air_temperature_c.HasValue)
                parts.Add(c.air_temperature_c.Value.ToString("F1", CultureInfo.InvariantCulture) + "°C");
            if (c.wind_ms.HasValue)
                parts.Add(c.wind_ms.Value.ToString("F1", CultureInfo.InvariantCulture) + " м/с");
            if (c.humidity.HasValue)
                parts.Add(c.humidity.Value.ToString("F0", CultureInfo.InvariantCulture) + "%");
            return string.Join(", ", parts);
        }

        private void Log(string message)
        {
            var line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + "\r\n";

            // Лог-файл пишем всегда, даже если окно уже закрывается.
            try
            {
                // AUDIT-04 (C3): буферизованная запись (раз в секунду); ошибки и предупреждения — сразу
                bool urgent = message.Contains("ошибк", StringComparison.OrdinalIgnoreCase) || message.Contains("error", StringComparison.OrdinalIgnoreCase) || message.Contains("[CRASH");
                lock (_logFileLock) LogFile.Append(AppPaths.SessionLog(DateTime.Now), line, urgent);
            }
            catch
            {
                // Ошибка файла не должна мешать захвату.
            }

            if (logTextBox == null || logTextBox.IsDisposed) return;
            // AUDIT-04 (G1): строки в окно журнала — пачкой раз в 300 мс из потока окна; потоки захвата/разбора больше не ждут окно (раньше Invoke)
            _uiLog.Enqueue(line);
            if (Interlocked.Exchange(ref _uiLogArmed, 1) == 0)
            {
                try { if (IsHandleCreated) BeginInvoke(new Action(ArmUiLogTimer)); else _uiLogArmed = 0; } catch { _uiLogArmed = 0; }
            }
        }

        private readonly ConcurrentQueue<string> _uiLog = new();
        private int _uiLogArmed;
        private System.Windows.Forms.Timer? _uiLogTimer;
        public const int UiLogMaxChars = 300_000;

        private void ArmUiLogTimer()
        {
            if (_uiLogTimer == null)
            {
                _uiLogTimer = new System.Windows.Forms.Timer { Interval = 300 };
                _uiLogTimer.Tick += (s, e) => FlushUiLog();
            }
            _uiLogTimer.Start();
        }

        private void FlushUiLog()
        {
            _uiLogTimer?.Stop();
            Interlocked.Exchange(ref _uiLogArmed, 0);
            if (logTextBox == null || logTextBox.IsDisposed) { while (_uiLog.TryDequeue(out _)) { } return; }
            var sb = new StringBuilder();
            while (_uiLog.TryDequeue(out var l)) sb.Append(l);
            if (sb.Length == 0) return;
            try
            {
                if (logTextBox.TextLength + sb.Length > UiLogMaxChars)   // окно журнала держит последние ~300 тыс. символов (файл — полный)
                {
                    string all = logTextBox.Text + sb;
                    logTextBox.Text = all.Substring(Math.Max(0, all.Length - UiLogMaxChars / 2));
                }
                else logTextBox.AppendText(sb.ToString());
                logTextBox.SelectionStart = logTextBox.TextLength;
                logTextBox.ScrollToCaret();
            }
            catch (InvalidOperationException) { }
            if (!_uiLog.IsEmpty && Interlocked.Exchange(ref _uiLogArmed, 1) == 0) ArmUiLogTimer();
        }

        // OVERLAY: overlay window + global hotkey; data are read from the session DB in read-only mode.
        // NOTIFY-02: трей, клавиша главного окна, номер удочки поклёвки для её окна уведомления
        public static bool StartInTray;
        /// <summary>AUDIT-04 (G1): построчная диагностика пакетов (ключ --diag).</summary>
        public static bool DiagRequested;
        private TrayController? _tray;
        private readonly ConditionalWeakTable<CatchData, StrongBox<int?>> _biteRods = new();

        private void StartTray()
        {
            try
            {
                SoundBank.EnsureDefaults();
                GameLaunch.Ensure();
                _tray = new TrayController(this, () => _appSettings.Window ??= new WindowSettings(), Log, () => _overlay?.Toggle(), () => { try { _appSettings.Save(); } catch { } });
                _tray.Exit = ExitApp;               // FEAT-09: «Выход» в меню значка закрывает программу
                _tray.ToggleMap = ToggleGameMap;   // GAMEMAP-01: F12 — карта на весь экран
                _tray.Start(StartInTray);
                NuStartup();                       // FEAT-11: новый интерфейс (бета)
            }
            catch (Exception ex) { Log("[TRAY] не запущен: " + ex.Message); }
        }

        private void StartOverlay()
        {
            try
            {
                _overlay = new OverlayManager(_deepParser, () => AppPaths.CatchDb, () => AppPaths.BaseDirectory,
                                              () => (_currentBaseId, _currentBaseSince), Log,
                                              () => _appSettings.Agent, () => _appSettings.Analytics, AdvisorLocName);   // COACH-01: вкладка «Советы»
                NuMiniHook();                      // FEAT-14: новая мини-карта
                _overlay.RefreshSync = false;   // OVERLAY-04: чтение БД окна в игре — в фоне
                _overlay.Start();
            }
            catch (Exception ex)
            {
                Log("[OVERLAY] не запущен: " + ex.Message);
            }
            StartTray();   // NOTIFY-02: трей и клавиша работают и без окна в игре
        }

        /// <summary>FEAT-09: ✕ окна сворачивает к часам (запись идёт); выход — кнопка ⏻ или «Выход» у значка.</summary>
        private bool _exitRequested;
        private bool _trayHintShown;

        public void ExitApp()
        {
            _exitRequested = true;
            Log("[EXIT] выход по кнопке");
            try { _nuShell?.Hide(); } catch { }
            try { _nuMini?.Hide(); } catch { }
            ExitWatchdog();
            Close();
        }

        /// <summary>FEAT-15 (EXIT-01): если выход завис (захват, драйвер), через 20 с процесс завершается сам — не висит в диспетчере.</summary>
        private static int _watchdog;
        internal void ExitWatchdog()
        {
            if (Interlocked.Exchange(ref _watchdog, 1) == 1) return;
            var t = new Thread(() => { Thread.Sleep(20000); try { Log("[EXIT] выход не закончился за 20 с — процесс завершён принудительно"); LogFile.Flush(); } catch { } Environment.Exit(1); }) { IsBackground = true, Name = "exit-watchdog" };
            t.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !_exitRequested && _tray != null)
            {
                e.Cancel = true;
                _tray.HideToTray(returnFocus: true);
                if (!_trayHintShown)
                {
                    _trayHintShown = true;
                    try { _tray.Icon.ShowBalloonTip(4000, "FisherLogger работает", "Окно свёрнуто к часам, запись продолжается. Закрыть совсем — кнопка ⏻ или «Выход» в меню значка.", ToolTipIcon.Info); } catch { }
                }
                return;
            }
            ExitWatchdog();   // FEAT-15 (EXIT-01)
            try { _companion?.Dispose(); } catch { } // NEXT-ANDROID-01
            _companion = null;
            try { _overlay?.Dispose(); } catch { } // OVERLAY
            try { _tray?.Dispose(); } catch { } _tray = null;   // NOTIFY-02
            try { BiteToastManager.CloseAll(); } catch { }
            _overlay = null;
            fishLogMonitor?.Stop();
            pollTimer?.Stop();
            pollTimer?.Dispose();
            uiRefreshTimer?.Stop();
            uiRefreshTimer?.Dispose();
            baitTimeoutTimer?.Stop();
            baitTimeoutTimer?.Dispose();

            Log("[EXIT] остановка захвата");
            var dev = device;
            var stop = System.Threading.Tasks.Task.Run(() => { try { dev?.StopCapture(); } catch { } try { dev?.Close(); } catch { } });
            if (!stop.Wait(3000)) Log("[EXIT] захват не остановился за 3 с — выход продолжается");
            try { _pcapNgWriter?.Dispose(); } catch { }

            try
            {
                _parserCts?.Cancel();
                _parserTask?.Wait(2000);
            }
            catch { }
            if (_packetQueue != null && _packetQueue.Dropped > 0)
                Log("[QUEUE] Потеряно пакетов при переполнении: " + _packetQueue.Dropped);
            try { _packetQueue?.Dispose(); } catch { }

            if (!_suppressPersistence)
            {
                try
                {
                    // Не теряем поимки, которые ещё ждали чат/сводку.
                    SaveIncompleteCatches();
                }
                catch (Exception ex)
                {
                    Log("Ошибка сохранения незавершённых поимок: " + ex.Message);
                }

                try
                {
                    SaveData();
                }
                catch (Exception ex)
                {
                    Log("Ошибка финального сохранения: " + ex.Message);
                }

                try { _db?.FlushResearch(); } catch { }   // DATA-01
                try { _attempts?.CloseAll(DateTime.Now, "application closed (end not observed)"); } // NEXT-ANALYTICS-04
                catch (Exception ex) { Log("[ATTEMPT] закрытие забросов: " + ex.Message); }

                try
                {
                    _db?.Flush();
                    _db?.Dispose();
                    _db = null;
                    _checks = null; // NEXT-BITE-IMPLEMENT-01
                }
                catch (Exception ex)
                {
                    Log("Ошибка закрытия SQLite: " + ex.Message);
                }
            }

            Log("[EXIT] данные сохранены");

            try { LogFile.Flush(); } catch { }   // AUDIT-04 (C3)
            base.OnFormClosing(e);
        }
    }
    /// <summary>
    /// NEXT-NOTIFY-01: which FisherLogger popups are shown. Only the early-bite notification
    /// (Form1.OnEarlyBite: source = "early_bite") is allowed; the post-catch popup is disabled.
    /// </summary>
    public static class CatchNotificationPolicy
    {
        public const string EarlyBiteSource = "early_bite";

        public static bool ShouldShowPopup(CatchData? data)
            => data != null && string.Equals(data.source, EarlyBiteSource, StringComparison.Ordinal);
    }
}



