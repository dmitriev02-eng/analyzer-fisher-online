using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FisherLogger
{
    // =====================================================================================
    // Игровой оверлей «Радар рыбалки»: обычное внешнее окно Windows поверх других окон.
    //  * Не активируется (WS_EX_NOACTIVATE, ShowWithoutActivation, WM_MOUSEACTIVATE -> MA_NOACTIVATE):
    //    показ/скрытие и щелчки не забирают фокус клавиатуры у игры.
    //  * Сквозные щелчки: WS_EX_TRANSPARENT (+ WS_EX_LAYERED); удержание Ctrl временно отключает (OverlayManager).
    //  * Никакого внедрения в игру, хуков или чтения памяти — только данные FisherLogger (БД, Player.log).
    //  * Карта: та же подложка и то же преобразование X/Z, что в окне карты (MapRenderer, MapRegistry).
    //  * OVERLAY-03: живая позиция игрока — из c2s 1006 (раз в ~10 с, PlayerTrack); круглая карта вокруг игрока,
    //    забросы текущей рыбалки меняют цвет по исходу (поклёвка/подсечка/улов), под картой — последняя поимка.
    //    Без позы (нет пакета, другой водоём) — прежнее поведение: последний заброс с пометкой «не позиция игрока».
    // =====================================================================================
    public sealed class OverlayForm : Form
    {
        private const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
        private const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3, WM_NCHITTEST = 0x84, HTCLIENT = 1, HTCAPTION = 2,
                          HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17,
                          WM_NCLBUTTONDOWN = 0xA1, WM_EXITSIZEMOVE = 0x232, GWL_EXSTYLE = -20;
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_NOOWNERZORDER = 0x200;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        /// <summary>OVERLAY-04: журнал медленных мест (перерисовка дольше 40 мс).</summary>
        public static Action<string>? SlowLog;
        private DateTime _slowAt = DateTime.MinValue;
        // OVERLAY-04: подложка карты рисуется в кэш и пересобирается только при сдвиге/масштабе/размере
        private Bitmap? _mapLayer;
        private (object? img, double cx, double cz, double sc, int w, int h) _mapKey;
        private (double x, double z, double yaw) _poseDrawn = (double.NaN, double.NaN, double.NaN);
        public int MapLayerBuilds { get; private set; }

        public OverlaySettings Settings { get; }
        public OverlaySnapshot? Data { get; private set; }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public double CenterX { get; set; }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public double CenterZ { get; set; }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Follow { get; set; } = true;
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SpeciesFilter { get; set; }
        public bool ClickThroughActive { get; private set; }
        public Func<DateTime> Clock = () => DateTime.Now;          // tests may replace
        public const double StaleCastMinutes = 15;
        /// <summary>OVERLAY-03: источник живой позы игрока (PlayerTrack.Last). null — позиции нет.</summary>
        public Func<PlayerPose?>? PoseSource;
        public const double LivePoseSeconds = 25, RecentPoseMinutes = 30;
        private DateTime _poseSeen = DateTime.MinValue;

        public event Action? SettingsChanged;        // persist
        public event Action? HideRequested;
        public event Action? SettingsDialogRequested;
        public event Action? RefreshRequested;
        /// <summary>COACH-01: «Спросить ИИ» во вкладке «Советы».</summary>
        public event Action? CoachAskRequested;
        /// <summary>OVERLAY-04: показать последний ответ ИИ во всплывающем окне.</summary>
        public event Action? CoachShowRequested;
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public CoachSnapshot? Coach { get; set; }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string CoachAiText { get; set; } = "";
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string CoachAiNote { get; set; } = "";
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool CoachBusy { get; set; }

        // layout
        public Rectangle HeaderRect { get; private set; }
        public Rectangle MapRect { get; private set; }
        public Rectangle LegendRect { get; private set; }
        public Rectangle LogRect { get; private set; }
        public Rectangle InfoRect { get; private set; }      // OVERLAY-03: «Вы» и «Поимка» над картой
        public Rectangle CatchRect { get; private set; }     // OVERLAY-03: панель последней поимки под картой
        public bool Round => Settings.RoundMap;
        private const int HeaderH = 24, LegendH = 18, RowH = 19, LogRows = 5, InfoH = 40, CatchH = 36, NeckMin = 8;
        private readonly List<(Rectangle r, string id, string text)> _buttons = new();
        private readonly List<(Rectangle r, MapCatch c)> _logHits = new();
        private int _logOffset;
        private Point _panLast; private bool _panning;

        private static readonly Color Bg = Theme.Background, HeaderBg = Theme.HeaderBar, MapBg = Color.FromArgb(10, 15, 22);
        // OVERLAY-03: игрок и забросы по исходу
        public static readonly Color PlayerColor = Color.FromArgb(255, 90, 95), CastWaitColor = Color.FromArgb(90, 169, 230),
                                     CastBiteColor = Color.FromArgb(242, 192, 87), CastHookColor = Color.FromArgb(255, 138, 61),
                                     CastCatchColor = Color.FromArgb(108, 204, 140), CastDoneColor = Color.FromArgb(120, 138, 150);
        public static Color CastStateColor(string state) => state switch
        {
            "bite" => CastBiteColor, "hook" => CastHookColor, "catch" => CastCatchColor, "done" => CastDoneColor, _ => CastWaitColor
        };
        private static readonly Font FCoord = new("Bahnschrift SemiBold", 11.5f), FCoordSmall = new("Bahnschrift", 8f), FPanel = new("Segoe UI Semibold", 9f);
        private static readonly Color CastColor = Color.FromArgb(41, 182, 230), HookColor = Color.FromArgb(242, 192, 87),
                                      EmptyColor = Color.FromArgb(150, 170, 192), LandedColor = Color.FromArgb(108, 204, 140), HeatColor = Color.FromArgb(230, 150, 60);
        private static readonly Font FTitle = new("Segoe UI Semibold", 9f), FSmall = new("Segoe UI", 8f), FLog = new("Segoe UI", 8.5f), FBtn = new("Segoe UI", 8f);

        public OverlayForm(OverlaySettings settings)
        {
            Settings = settings;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            // OVERLAY-04: поверх других окон — через WS_EX_TOPMOST (CreateParams). Свойство TopMost при показе делало окно активным,
            // если у программы было право на фокус (сразу после нажатия клавиши окна), — игра теряла управление.
            Text = "FisherLogger — окно в игре";
            BackColor = Bg;
            ForeColor = Theme.Text;
            MinimumSize = new Size(OverlaySettings.MinWidth, OverlaySettings.MinHeight);
            MaximumSize = new Size(OverlaySettings.MaxWidth, OverlaySettings.MaxHeight);   // NEXT-OVERLAY-02: same limits as the saved settings
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(settings.Width, settings.Height);
            Opacity = Math.Min(settings.Opacity, 0.99);   // < 1 keeps the window layered (required for click-through)
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_LAYERED;
                if (ClickThroughActive) cp.ExStyle |= WS_EX_TRANSPARENT;
                return cp;
            }
        }

        public long ExStyle => IsHandleCreated ? GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64() : 0;

        /// <summary>Сквозные щелчки: окно не перехватывает мышь (WS_EX_TRANSPARENT).</summary>
        public void SetClickThroughActive(bool on)
        {
            if (on == ClickThroughActive) return;
            ClickThroughActive = on;
            if (!IsHandleCreated) return;
            long ex = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
            ex = on ? (ex | WS_EX_TRANSPARENT | WS_EX_LAYERED) : (ex & ~WS_EX_TRANSPARENT);
            SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(ex));
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_NOOWNERZORDER);
            Invalidate();
        }

        /// <summary>Поднять окно поверх остальных без активации (вызывается периодически, пока оверлей виден).</summary>
        public void KeepOnTop()
        {
            // OVERLAY-04: не дёргать порядок окон, если над нами и так никого нет (каждые 2 с — лишняя работа композитора над игрой)
            if (IsHandleCreated && Visible && GetWindow(Handle, 3 /* GW_HWNDPREV */) != IntPtr.Zero)
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        public void ApplyOpacity() => Opacity = Math.Min(Settings.Opacity, 0.99);

        public void ApplyPreset(OverlayPreset p)
        {
            if (p == OverlayPreset.Custom) return;
            var wa = Screen.FromRectangle(Bounds).WorkingArea; const int m = 12;
            int x = p is OverlayPreset.TopLeft or OverlayPreset.BottomLeft ? wa.Left + m : wa.Right - Width - m;
            int y = p is OverlayPreset.TopLeft or OverlayPreset.TopRight ? wa.Top + m : wa.Bottom - Height - m;
            Location = new Point(x, y);
            Settings.Preset = p; Settings.X = x; Settings.Y = y;
            SettingsChanged?.Invoke();
        }

        /// <summary>Положение из настроек: сохранённые координаты (если окно видно на каком-либо экране) или пресет.</summary>
        public void RestorePlacement()
        {
            Size = new Size(Settings.Width, Settings.Height);
            var r = new Rectangle(Settings.X, Settings.Y, Settings.Width, Settings.Height);
            bool onScreen = Settings.X != -1 && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(r.X, r.Y, Math.Min(r.Width, 120), 40)));
            if (onScreen && Settings.Preset == OverlayPreset.Custom) Location = new Point(Settings.X, Settings.Y);
            else
            {
                Location = Screen.PrimaryScreen!.WorkingArea.Location;
                ApplyPreset(Settings.Preset == OverlayPreset.Custom ? OverlayPreset.TopRight : Settings.Preset);
            }
        }

        public void SetData(OverlaySnapshot s)
        {
            bool locChanged = Data?.LocationId != s.LocationId;
            Data = s;
            if (Follow || locChanged) Recenter();
            Invalidate();
        }

        /// <summary>Центр вида: последний заброс → последний улов водоёма → центр карты.</summary>
        public void Recenter()
        {
            var d = Data;
            var pose = ShownPose;
            // OVERLAY-04: после заброса — на забросы в воде (их центр); нет забросов — на игрока
            var act = d?.Casts.Where(k => k.Active).ToList();
            if (Round && act != null && act.Count > 0) { CenterX = act.Average(k => k.X); CenterZ = act.Average(k => k.Z); }
            else if (pose != null) { CenterX = pose.X; CenterZ = pose.Z; }
            else if (d?.LastCast != null && CastShownOnMap) { CenterX = d.LastCast.X; CenterZ = d.LastCast.Z; }
            else if (d != null && d.Catches.Count > 0)
            {
                var c = d.Catches.OrderByDescending(OverlayDataSource.SortTime).ThenByDescending(k => k.TimeText, StringComparer.Ordinal).First();
                CenterX = c.X!.Value; CenterZ = c.Z!.Value;
            }
            else if (d?.Map != null) { CenterX = (d.Map.MinX + d.Map.MaxX) / 2; CenterZ = (d.Map.MinZ + d.Map.MaxZ) / 2; }
            Invalidate();
        }

        // ---------------- geometry ----------------
        public double ViewScale => Math.Min(MapRect.Width, MapRect.Height) / (2.0 * Settings.RadiusMeters);   // px per metre

        public PointF WorldToScreen(double x, double z) =>
            new((float)(MapRect.X + MapRect.Width / 2.0 + (x - CenterX) * ViewScale), (float)(MapRect.Y + MapRect.Height / 2.0 - (z - CenterZ) * ViewScale));

        public (double x, double z) ScreenToWorld(PointF p) =>
            (CenterX + (p.X - MapRect.X - MapRect.Width / 2.0) / ViewScale, CenterZ - (p.Y - MapRect.Y - MapRect.Height / 2.0) / ViewScale);

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int w = ClientSize.Width, h = ClientSize.Height;
            int logH = (Settings.LogTab == 1 ? Settings.FeedRows : Settings.LogTab == 2 ? Settings.CoachRows : LogRows) * RowH + 4;
            logH = Math.Min(logH, Math.Max(RowH * 4, h - HeaderH - TabH - LegendH - 60));   // карта остаётся видна хотя бы полоской
            if (Round)
            {
                // OVERLAY-03: круг не меньше ~190 px (или ширины окна) — журнал/лента уступают строки, но не меньше двух
                int fixedH = HeaderH + InfoH + 4 + NeckMin + CatchH + 3 + LegendH + TabH;
                int want = Math.Min(w - 14, 190);
                if (h - fixedH - logH < want) logH = Math.Max(RowH * 2 + 4, h - fixedH - want);
            }
            HeaderRect = new Rectangle(0, 0, w, HeaderH);
            LogRect = new Rectangle(0, h - logH, w, logH);
            TabRect = new Rectangle(0, LogRect.Top - TabH, w, TabH);          // NEXT-GTWC-01: вкладки «Мои уловы / Лента»
            LegendRect = new Rectangle(0, TabRect.Top - LegendH, w, LegendH);
            if (!Round)
            {
                InfoRect = Rectangle.Empty; CatchRect = Rectangle.Empty;
                MapRect = new Rectangle(4, HeaderH + 2, w - 8, Math.Max(40, LegendRect.Top - HeaderH - 4));
                return;
            }
            // OVERLAY-03: полоса координат → круг → «ножка» → панель поимки
            InfoRect = new Rectangle(0, HeaderH, w, InfoH);
            CatchRect = new Rectangle(10, LegendRect.Top - CatchH - 3, w - 20, CatchH);
            int top = InfoRect.Bottom + 4, bottom = CatchRect.Top - NeckMin;
            int d = Math.Max(40, Math.Min(w - 14, bottom - top));
            int y = Math.Max(top, top + (bottom - top - d) / 2);
            MapRect = new Rectangle((w - d) / 2, y, d, d);
        }

        // ---------------- filters ----------------
        public Rectangle TabRect { get; private set; }
        private const int TabH = 17;
        private readonly List<(Rectangle r, int tab)> _tabHits = new();
        private int _feedOffset;

        public void SetLogTab(int tab)
        {
            Settings.LogTab = tab is 1 or 2 ? tab : 0;
            _logOffset = 0; _feedOffset = 0;
            SettingsChanged?.Invoke(); PerformLayout(); Invalidate();
        }

        /// <summary>Лента с тем же фильтром веса для объявлений об уловах.</summary>
        public List<FeedItem> VisibleFeed() => (Data?.Feed ?? new List<FeedItem>())
            .Where(f => f.Kind is "text" or "action" || Settings.MinDisplayWeightG <= 0 || f.Weight >= Settings.MinDisplayWeightG).ToList();

        private bool InPeriod(DateTime? t) => Settings.RecentHours <= 0 || (t.HasValue && t.Value >= Clock().AddHours(-Settings.RecentHours));

        /// <summary>NEXT-OVERLAY-02: фильтр веса только для показа (карта и журнал); данные БД не меняются.</summary>
        public bool PassesWeight(MapCatch c) => Settings.MinDisplayWeightG <= 0 || c.Weight >= Settings.MinDisplayWeightG;

        public List<MapCatch> FilteredCatches() =>
            (Data?.Catches ?? new List<MapCatch>())
            .Where(c => InPeriod(OverlayDataSource.PlausibleTime(c.TimeText)))
            .Where(PassesWeight)
            .Where(c => SpeciesFilter == null || c.Fish == SpeciesFilter).ToList();

        /// <summary>Журнал уловов с тем же фильтром веса, что и карта.</summary>
        public List<MapCatch> VisibleLog() => (Data?.RecentLog ?? new List<MapCatch>()).Where(PassesWeight).ToList();

        public void SetMinDisplayWeight(int grams)
        {
            Settings.MinDisplayWeightG = Math.Clamp(grams, 0, CatchRules.MaxRecordWeightG);
            _logOffset = 0;
            SettingsChanged?.Invoke(); Invalidate();
        }

        public List<OverlayEvent> FilteredEvents() => (Data?.Events ?? new List<OverlayEvent>()).Where(e => InPeriod(e.Time)).ToList();

        public static double NiceStep(double raw)
        {
            if (raw <= 0 || double.IsNaN(raw)) return 1;
            double exp = Math.Pow(10, Math.Floor(Math.Log10(raw))); double f = raw / exp;
            return (f < 1.5 ? 1 : f < 3.5 ? 2 : f < 7.5 ? 5 : 10) * exp;
        }
        public double DensityCell => NiceStep(Settings.RadiusMeters / 5.0);

        // ---------------- painting ----------------
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var swPaint = System.Diagnostics.Stopwatch.StartNew();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Bg);
            DrawHeader(g);
            if (Round) { DrawInfo(g); DrawCatchPanel(g); }   // панель до карты: «ножка» уходит под край круга
            DrawMapArea(g);
            DrawLegend(g);
            DrawTabs(g);
            if (Settings.LogTab == 1) DrawFeed(g); else if (Settings.LogTab == 2) DrawCoach(g); else DrawLog(g);
            using var pen = new Pen(Color.FromArgb(60, 80, 104));
            g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            // resize grip
            for (int i = 0; i < 3; i++) g.DrawLine(pen, ClientSize.Width - 4 - i * 4, ClientSize.Height - 2, ClientSize.Width - 2, ClientSize.Height - 4 - i * 4);
            if (swPaint.ElapsedMilliseconds > 40 && (DateTime.Now - _slowAt).TotalSeconds > 30) { _slowAt = DateTime.Now; SlowLog?.Invoke("[OVERLAY] медленная перерисовка: " + swPaint.ElapsedMilliseconds + " мс (" + ClientSize.Width + "×" + ClientSize.Height + ")"); }
        }

        private void DrawHeader(Graphics g)
        {
            using (var b = new SolidBrush(HeaderBg)) g.FillRectangle(b, HeaderRect);
            _buttons.Clear();
            int x = HeaderRect.Right - 4;
            void Btn(string id, string text, int w)
            {
                var r = new Rectangle(x - w, 3, w, HeaderH - 6); x -= w + 3;
                _buttons.Add((r, id, text));
                using var b = new SolidBrush(Color.FromArgb(38, 54, 74)); g.FillRectangle(b, r);
                UiText.DrawText(g, text, FBtn, r, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            Btn("hide", "✕", 20);
            Btn("settings", "⚙", 20);
            Btn("follow", Follow ? "⌖" : "⌖?", 24);
            Btn("radius", Settings.RadiusMeters.ToString("0", CultureInfo.InvariantCulture) + " м", 46);
            Btn("mode", Settings.Mode == OverlayMapMode.Activity ? "Улов" : "Клёв", 38);
            string title = Data == null ? "Радар" : (Data.LocationName.Length > 0 ? Data.LocationName : Data.LocationId.HasValue ? "loc " + Data.LocationId : "водоём не определён");
            if (ClickThroughActive) title = "⇵ " + title;
            UiText.DrawText(g, title, FTitle, new Rectangle(6, 0, Math.Max(10, x - 6), HeaderH), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void DrawMapArea(Graphics g)
        {
            var mr = MapRect;
            var state = g.Save();
            using var circle = new GraphicsPath();
            circle.AddEllipse(mr);
            if (Round) g.SetClip(circle); else g.SetClip(mr);
            using (var b = new SolidBrush(MapBg)) g.FillRectangle(b, mr);
            var d = Data;
            if (d?.Map != null && d.MapImage != null)
            {
                var key = ((object?)d.MapImage, CenterX, CenterZ, ViewScale, mr.Width, mr.Height);
                if (_mapLayer == null || !key.Equals(_mapKey))
                {
                    _mapLayer?.Dispose();
                    _mapLayer = new Bitmap(Math.Max(1, mr.Width), Math.Max(1, mr.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                    using (var lg = Graphics.FromImage(_mapLayer))
                    {
                        lg.Clear(MapBg);
                        MapRenderer.DrawBackground(lg, d.Map, d.MapImage, CenterX, CenterZ, ViewScale, mr.Width, mr.Height);
                    }
                    _mapKey = key; MapLayerBuilds++;
                }
                var im = g.InterpolationMode; g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImageUnscaled(_mapLayer, mr.X, mr.Y);
                g.InterpolationMode = im;
            }
            else DrawNeutralGrid(g);

            if (Settings.Mode == OverlayMapMode.Activity) DrawActivity(g); else DrawBites(g);
            if (Round)
            {
                DrawRangeRings(g);
                DrawCastMarks(g);
                if ((Data?.Casts.Count ?? 0) == 0) DrawCast(g);   // нет забросов этой рыбалки — прежний крестик последнего
                DrawPlayer(g);
                g.Restore(state);
                DrawRing(g);
                return;
            }
            DrawCast(g);
            DrawScale(g);
            g.Restore(state);
            using var pen = new Pen(Color.FromArgb(70, 92, 118)); g.DrawRectangle(pen, mr.X, mr.Y, mr.Width - 1, mr.Height - 1);
        }

        private void DrawNeutralGrid(Graphics g)
        {
            double step = NiceStep(Settings.RadiusMeters / 4);
            var (x0, z1) = ScreenToWorld(new PointF(MapRect.Left, MapRect.Top));
            var (x1, z0) = ScreenToWorld(new PointF(MapRect.Right, MapRect.Bottom));
            using var pen = new Pen(Color.FromArgb(32, 48, 66));
            for (double x = Math.Floor(x0 / step) * step; x <= x1; x += step) { float sx = WorldToScreen(x, 0).X; g.DrawLine(pen, sx, MapRect.Top, sx, MapRect.Bottom); }
            for (double z = Math.Floor(z0 / step) * step; z <= z1; z += step) { float sy = WorldToScreen(0, z).Y; g.DrawLine(pen, MapRect.Left, sy, MapRect.Right, sy); }
            UiText.DrawText(g, Data?.LocationId == null ? "Водоём не определён — сетка X/Z" : "Нет проверенной карты — сетка X/Z", FSmall,
                new Point(MapRect.Left + 4, MapRect.Top + 21), Theme.TextMuted);
        }

        private void DrawActivity(Graphics g)
        {
            var pts = FilteredCatches();
            if (pts.Count == 0) return;
            double cell = DensityCell;
            var bins = pts.GroupBy(p => ((long)Math.Floor(p.X!.Value / cell), (long)Math.Floor(p.Z!.Value / cell))).Select(k => (k.Key, n: k.Count())).ToList();
            int max = bins.Max(b => b.n);
            foreach (var (key, n) in bins)
            {
                var a = WorldToScreen(key.Item1 * cell, (key.Item2 + 1) * cell); var b = WorldToScreen((key.Item1 + 1) * cell, key.Item2 * cell);
                var r = RectangleF.FromLTRB(a.X, a.Y, b.X, b.Y);
                if (!r.IntersectsWith(MapRect)) continue;
                double t = max == 1 ? 1 : (n - 1) / (double)(max - 1);
                using var br = new SolidBrush(Color.FromArgb(55 + (int)(120 * t), HeatColor));
                g.FillRectangle(br, r);
                if (r.Width >= 18 && r.Height >= 14)
                    UiText.DrawText(g, n.ToString(), FSmall, Rectangle.Round(r), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            DateTime now = Clock();
            foreach (var p in pts)
            {
                var s = WorldToScreen(p.X!.Value, p.Z!.Value);
                if (!MapRect.Contains(Point.Round(s))) continue;
                var t = OverlayDataSource.PlausibleTime(p.TimeText);
                bool recent = t.HasValue && (now - t.Value).TotalMinutes <= 60;
                using var br = new SolidBrush(recent ? Color.FromArgb(255, 236, 170) : Color.FromArgb(220, 255, 210, 140));
                g.FillEllipse(br, s.X - 2.5f, s.Y - 2.5f, 5, 5);
                if (recent) using (var pen = new Pen(Color.White, 1.2f)) g.DrawEllipse(pen, s.X - 5, s.Y - 5, 10, 10);
            }
        }

        private void DrawBites(Graphics g)
        {
            foreach (var e in FilteredEvents())
            {
                var s = WorldToScreen(e.X, e.Z);
                if (!MapRect.Contains(Point.Round(s))) continue;
                if (e.Type == "hook")
                {
                    using var br = new SolidBrush(Color.FromArgb(220, HookColor));
                    g.FillPolygon(br, new[] { new PointF(s.X, s.Y - 5), new PointF(s.X + 4.5f, s.Y + 3.5f), new PointF(s.X - 4.5f, s.Y + 3.5f) });
                }
                else if (e.Type == "empty_bite")
                {
                    using var pen = new Pen(EmptyColor, 1.4f);
                    g.DrawPolygon(pen, new[] { new PointF(s.X, s.Y - 4.5f), new PointF(s.X + 4.5f, s.Y), new PointF(s.X, s.Y + 4.5f), new PointF(s.X - 4.5f, s.Y) });
                }
            }
            foreach (var c in FilteredCatches())
            {
                var s = WorldToScreen(c.X!.Value, c.Z!.Value);
                if (!MapRect.Contains(Point.Round(s))) continue;
                using var br = new SolidBrush(LandedColor);
                g.FillEllipse(br, s.X - 3.5f, s.Y - 3.5f, 7, 7);
            }
        }

        public bool LastCastStale => Data?.LastCast == null || (Clock() - Data.LastCast.Time).TotalMinutes > StaleCastMinutes;

        /// <summary>
        /// Заброс можно показывать на этой карте: он сделан после загрузки текущей базы (Player.log),
        /// либо водоём определён не по Player.log (тогда показывается с пометкой «водоём не подтверждён»).
        /// </summary>
        public bool CastShownOnMap => Data?.LastCast != null && (Data.LastCast.LocationConfirmed || Data.LocationSource != "Player.log");

        /// <summary>Строка статуса позиции (живой позиции игрока в данных нет).</summary>
        public string PositionText
        {
            get
            {
                var pose = ShownPose;
                if (pose != null) return "Позиция игрока " + CoordText(pose.X, pose.Z) + (PoseLive ? " (сейчас)" : ", " + AgeText(Clock() - pose.Time) + " назад");
                var c = Data?.LastCast;
                if (c == null) return "Позиция игрока: нет данных; забросов нет";
                if (!CastShownOnMap) return "Позиция игрока: нет данных; заброс на этом водоёме ещё не записан";
                double min = (Clock() - c.Time).TotalMinutes;
                string age = min < 1 ? "<1 мин" : min < 120 ? ((int)min) + " мин" : min < 2880 ? ((int)(min / 60)) + " ч" : ((int)(min / 1440)) + " дн";
                return "Заброс " + c.Time.ToString("HH:mm") + ", " + age + " — не позиция игрока" +
                       (LastCastStale ? "; устарел" : "") + (c.LocationConfirmed ? "" : "; водоём не подтверждён");
            }
        }

        private void DrawCast(Graphics g)
        {
            var c = Data?.LastCast;
            if (c != null && CastShownOnMap)
            {
                var s = WorldToScreen(c.X, c.Z);
                bool stale = LastCastStale;
                using var pen = new Pen(stale ? Color.FromArgb(150, CastColor) : CastColor, 2f) { DashStyle = stale ? DashStyle.Dash : DashStyle.Solid };
                g.DrawEllipse(pen, s.X - 7, s.Y - 7, 14, 14);
                g.DrawLine(pen, s.X - 11, s.Y, s.X - 4, s.Y); g.DrawLine(pen, s.X + 4, s.Y, s.X + 11, s.Y);
                g.DrawLine(pen, s.X, s.Y - 11, s.X, s.Y - 4); g.DrawLine(pen, s.X, s.Y + 4, s.X, s.Y + 11);
            }
            if (Round) return;   // OVERLAY-03: статус и север — в полосе над картой и на кольце
            var r = new Rectangle(MapRect.Left + 3, MapRect.Top + 3, MapRect.Width - 26, 15);
            using (var b = new SolidBrush(Color.FromArgb(170, 8, 12, 18))) g.FillRectangle(b, r);
            UiText.DrawText(g, PositionText, FSmall, r, LastCastStale ? Theme.Warning : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            // north
            var n = new Rectangle(MapRect.Right - 20, MapRect.Top + 3, 17, 15);
            using (var b = new SolidBrush(Color.FromArgb(170, 8, 12, 18))) g.FillRectangle(b, n);
            UiText.DrawText(g, "С", FSmall, n, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawScale(Graphics g)
        {
            double unit = NiceStep(Settings.RadiusMeters / 3);
            float px = (float)(unit * ViewScale);
            float x = MapRect.Left + 8, y = MapRect.Bottom - 10;
            using var pen = new Pen(Color.FromArgb(220, 230, 240), 2f);
            g.DrawLine(pen, x, y, x + px, y); g.DrawLine(pen, x, y - 4, x, y + 3); g.DrawLine(pen, x + px, y - 4, x + px, y + 3);
            UiText.DrawText(g, unit.ToString("0", CultureInfo.InvariantCulture) + " м", FSmall, new Point((int)(x + px + 4), (int)y - 8), Color.White);
        }

        private void DrawLegend(Graphics g)
        {
            var r = LegendRect; int x = r.Left + 6, cy = r.Top + r.Height / 2;
            void Txt(string t, Color c) { var sz = UiText.MeasureText(t, FSmall); UiText.DrawText(g, t, FSmall, new Point(x, r.Top + 2), c); x += sz.Width + 2; }
            if (Round)
            {
                void Dot(Color c, string t) { using (var b = new SolidBrush(c)) g.FillEllipse(b, x, cy - 3, 7, 7); x += 10; Txt(t, Theme.TextMuted); x += 2; }
                Dot(PlayerColor, "вы"); Dot(CastWaitColor, "в воде"); Dot(CastBiteColor, "поклёвка"); Dot(CastHookColor, "подсечка"); Dot(CastCatchColor, "улов");
                if (Settings.MinDisplayWeightG > 0) Txt("от " + Settings.MinDisplayWeightG + " г", Theme.Warning);
                if (GameKnowledge.LeaderColor(Data?.LocationId) is string lc0) Txt("· поводок " + lc0, Theme.TextMuted);   // HINTS-01
                return;
            }
            using (var pen = new Pen(CastColor, 1.6f)) g.DrawEllipse(pen, x, cy - 4, 8, 8); x += 11; Txt("заброс", Theme.TextMuted);
            if (Settings.Mode == OverlayMapMode.Activity)
            {
                using (var b = new SolidBrush(Color.FromArgb(150, HeatColor))) g.FillRectangle(b, x, cy - 4, 10, 8); x += 13;
                Txt("плотность уловов, шаг " + DensityCell.ToString("0", CultureInfo.InvariantCulture) + " м", Theme.TextMuted);
            }
            if (Settings.MinDisplayWeightG > 0) Txt("· от " + Settings.MinDisplayWeightG + " г", Theme.Warning);   // NEXT-OVERLAY-02
            else
            {
                using (var b = new SolidBrush(HookColor)) g.FillPolygon(b, new[] { new Point(x + 4, cy - 4), new Point(x + 8, cy + 3), new Point(x, cy + 3) }); x += 11; Txt("подсечка", Theme.TextMuted);
                using (var p = new Pen(EmptyColor)) g.DrawPolygon(p, new[] { new Point(x + 4, cy - 4), new Point(x + 8, cy), new Point(x + 4, cy + 4), new Point(x, cy) }); x += 11; Txt("пуст.", Theme.TextMuted);
                using (var b = new SolidBrush(LandedColor)) g.FillEllipse(b, x, cy - 3, 7, 7); x += 10; Txt("улов", Theme.TextMuted);
            }
            if (GameKnowledge.LeaderColor(Data?.LocationId) is string lc) Txt("· поводок " + lc, Theme.TextMuted);   // HINTS-01: только если игра называет цвет
        }

        // ---------------- NEXT-GTWC-01: вкладки и лента ----------------
        private static readonly Color FeedCatch = Color.FromArgb(173, 216, 230), FeedRcrd = Color.FromArgb(255, 160, 32), FeedClub = Color.FromArgb(80, 240, 255),
                                      FeedText = Color.FromArgb(220, 224, 230), FeedNickCatch = Color.FromArgb(194, 236, 147), FeedNickChat = Color.FromArgb(255, 221, 64);

        private void DrawTabs(Graphics g)
        {
            _tabHits.Clear();
            using (var b = new SolidBrush(HeaderBg)) g.FillRectangle(b, TabRect);
            int x = TabRect.Left + 4;
            foreach (var (tab, text) in new[] { (0, "Мои уловы"), (1, "Лента"), (2, "Советы") })
            {
                var sz = UiText.MeasureText(text, FSmall);
                var r = new Rectangle(x, TabRect.Top + 1, sz.Width + 10, TabRect.Height - 2);
                bool on = Settings.LogTab == tab;
                using (var b = new SolidBrush(on ? Color.FromArgb(52, 76, 104) : Color.FromArgb(30, 42, 58))) g.FillRectangle(b, r);
                UiText.DrawText(g, text, FSmall, r, on ? Theme.Text : Theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                _tabHits.Add((r, tab));
                x = r.Right + 3;
            }
        }

        // ---------------- COACH-01: вкладка «Советы» ----------------
        private static readonly Color CoachOk = Color.FromArgb(108, 204, 140), CoachSoon = Color.FromArgb(242, 192, 87), CoachRecast = Color.FromArgb(222, 104, 88),
                                      CoachBite = Color.FromArgb(41, 182, 230), CoachIdle = Color.FromArgb(110, 125, 145);

        private void DrawCoach(Graphics g)
        {
            _logHits.Clear();
            using (var b = new SolidBrush(Color.FromArgb(14, 20, 28))) g.FillRectangle(b, LogRect);
            var c = Coach;
            int x0 = LogRect.Left + 6, w = LogRect.Width - 12, y = LogRect.Top + 3, bottom = LogRect.Bottom - 2;
            int Line(string text, Color col, Font f, int indent = 0)
            {
                if (y >= bottom) return 0;
                var r = new Rectangle(x0 + indent, y, w - indent, bottom - y);
                var sz = UiText.MeasureText(g, text, f, new Size(r.Width, 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                int hh = Math.Min(sz.Height, bottom - y);
                UiText.DrawText(g, text, f, new Rectangle(r.X, y, r.Width, hh), col, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                y += hh + 2; return hh;
            }
            if (c == null || !c.HasData)
            {
                Line("Сейчас вы не рыбачите (нет проверок сервера за последние 45 мин). Советы появятся после заброса.", Theme.TextMuted, FLog);
            }
            else
            {
                foreach (var r in c.Rods)
                {
                    if (y >= bottom) break;
                    Color col = r.Level switch { 0 => CoachOk, 1 => CoachSoon, 2 => CoachRecast, 3 => CoachBite, _ => CoachIdle };
                    using (var b = new SolidBrush(col)) g.FillEllipse(b, x0, y + 5, 9, 9);
                    // коротко: «Уд.1 фидер · 13 мин — перезабросьте · пок 0 · пуст 3»; полные данные — во вкладке ИИ и в Советнике
                    string line = "Удочка " + (r.Slot + 1) + ", " + SoakCurve.RodName(r.RodCode) + ": " + r.Status + "; поклёвок " + r.Bites + ", пустых " + r.Empty;
                    Line(line, r.Level == 2 ? CoachRecast : Theme.Text, FLog, 14);
                }
                foreach (var h in c.Hints) Line("• " + h, CoachSoon, FSmall);
                Line(c.CurveNote, Theme.TextMuted, FSmall);
            }
            // ИИ: кнопка и последний ответ
            if (y < bottom - 18)
            {
                string bt = CoachBusy ? "ИИ думает…" : "Спросить ИИ о рыбалке";
                var sz = UiText.MeasureText(bt, FBtn);
                var br = new Rectangle(x0, y + 1, sz.Width + 14, 18);
                using (var b = new SolidBrush(CoachBusy ? Color.FromArgb(40, 56, 76) : Color.FromArgb(18, 92, 122))) g.FillRectangle(b, br);
                UiText.DrawText(g, bt, FBtn, br, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (!CoachBusy) _buttons.Add((br, "coach_ask", bt));
                if (CoachAiNote.Length > 0)
                    UiText.DrawText(g, CoachAiNote, FSmall, new Rectangle(br.Right + 6, br.Top, Math.Max(10, LogRect.Right - br.Right - 12), 18), Theme.TextMuted,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                y = br.Bottom + 4;
                // OVERLAY-04: полный ответ — во всплывающем окне; здесь одна строка, щелчок — показать снова
                if (CoachAiText.Length > 0 && y < bottom - 14)
                {
                    var ar = new Rectangle(x0, y, w, 16);
                    string first = CoachAiText.Replace("\r", " ").Replace("\n", " ");
                    UiText.DrawText(g, "Ответ ИИ: " + first, FSmall, ar, Color.FromArgb(200, 225, 245), TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                    _buttons.Add((ar, "coach_show", "ответ"));
                }
            }
        }

        private void DrawFeed(Graphics g)
        {
            _logHits.Clear();
            using (var b = new SolidBrush(Color.FromArgb(14, 20, 28))) g.FillRectangle(b, LogRect);
            var list = VisibleFeed();
            int rows = Settings.FeedRows;
            if (list.Count == 0)
            {
                UiText.DrawText(g, "Ленты пока нет: объявления об уловах и чат появятся при игре с включённым захватом",
                    FSmall, new Rectangle(LogRect.Left + 8, LogRect.Top + 4, LogRect.Width - 16, LogRect.Height - 8), Theme.TextMuted, TextFormatFlags.WordBreak);
                return;
            }
            _feedOffset = Math.Clamp(_feedOffset, 0, Math.Max(0, list.Count - rows));
            int y = LogRect.Top + 2;
            DateTime today = Clock().Date;
            foreach (var f in list.Skip(_feedOffset).Take(rows))
            {
                string time = f.At.ToString(f.At.Date == today ? "HH:mm" : "dd.MM HH:mm");
                if (Settings.FeedShowGameTime && f.GameTime != null) time += " ≈" + f.GameTime;
                var tsz = UiText.MeasureText(time, FSmall);
                UiText.DrawText(g, time, FSmall, new Point(LogRect.Left + 4, y + 3), Theme.TextMuted);
                int x = LogRect.Left + 8 + tsz.Width;
                bool isCatch = f.Kind is "catch" or "rcrd" or "club";
                string name = f.Name + ":";
                int maxName = Math.Max(40, (LogRect.Right - x) * 2 / 5);
                var nsz = UiText.MeasureText(name, FLog);
                var nr = new Rectangle(x, y, Math.Min(nsz.Width, maxName), RowH);
                UiText.DrawText(g, name, FLog, nr, isCatch ? FeedNickCatch : FeedNickChat, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
                Color tc = f.Kind switch { "rcrd" => FeedRcrd, "club" => FeedClub, "catch" => FeedCatch, "action" => Theme.TextMuted, _ => FeedText };
                string text = f.Kind == "rcrd" ? "★ " + f.Text : f.Text;
                UiText.DrawText(g, text, FLog, new Rectangle(nr.Right + 4, y, Math.Max(10, LogRect.Right - nr.Right - 8), RowH), tc,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
                y += RowH;
            }
            if (list.Count > rows)
            {
                string pos = (_feedOffset + 1) + "–" + Math.Min(list.Count, _feedOffset + rows) + " из " + list.Count;
                var psz = UiText.MeasureText(pos, FSmall);
                UiText.DrawText(g, pos, FSmall, new Point(TabRect.Right - psz.Width - 6, TabRect.Top + 2), Theme.TextMuted);
            }
        }

        private void DrawLog(Graphics g)
        {
            _logHits.Clear();
            using (var b = new SolidBrush(Color.FromArgb(14, 20, 28))) g.FillRectangle(b, LogRect);
            var list = VisibleLog();
            if (list.Count == 0)
            {
                bool hidden = Settings.MinDisplayWeightG > 0 && (Data?.RecentLog.Count ?? 0) > 0;
                UiText.DrawText(g, hidden ? "Нет уловов от " + Settings.MinDisplayWeightG + " г (фильтр показа)" : "Уловов пока нет",
                    FLog, new Point(LogRect.Left + 8, LogRect.Top + 4), Theme.TextMuted);
                return;
            }
            _logOffset = Math.Clamp(_logOffset, 0, Math.Max(0, list.Count - LogRows));
            int y = LogRect.Top + 2;
            var ru = CultureInfo.GetCultureInfo("ru-RU");
            const TextFormatFlags L = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            foreach (var c in list.Skip(_logOffset).Take(LogRows))
            {
                var row = new Rectangle(LogRect.Left, y, LogRect.Width, RowH);
                _logHits.Add((row, c));
                bool onMap = OnThisMap(c);
                // OVERLAY-03: полоска класса, рыба цветом класса как в игре, вес, координаты поимки, картинка наживки, время
                var gcol = GradeBadge.ColorOf(c.Windex);
                if (gcol.HasValue) using (var gb = new SolidBrush(gcol.Value)) g.FillRectangle(gb, row.Left + 2, y + 3, 3, RowH - 6);
                var icon = IconLoader.Load(c.IconPath);
                if (icon != null) g.DrawImage(icon, new Rectangle(row.Left + 8, y + 1, RowH - 2, RowH - 2));
                int left = row.Left + RowH + 10, right = row.Right - 6;
                var t = OverlayDataSource.PlausibleTime(c.TimeText);
                string time = t.HasValue ? t.Value.ToString(t.Value.Date == Clock().Date ? "HH:mm" : "dd.MM HH:mm") : "дата?";
                var tsz = UiText.MeasureText(time, FSmall, Size.Empty, L);
                right -= tsz.Width;
                UiText.DrawText(g, time, FSmall, new Rectangle(right, y, tsz.Width + 2, RowH), Theme.TextMuted, L);
                right -= 5;
                string w = c.Weight.ToString("#,0", ru) + " г";
                int nameW = UiText.MeasureText(c.Fish, FLog, Size.Empty, L).Width, wW = UiText.MeasureText(w, FLog, Size.Empty, L).Width;
                int minLeft = left + Math.Min(nameW, 70) + 6 + wW + 6;   // рыба (хотя бы начало) и вес видны всегда
                var bait = BaitImage(c);
                if (bait != null && right - (RowH - 2) >= minLeft) { right -= RowH - 2; g.DrawImage(bait, new Rectangle(right, y + 1, RowH - 2, RowH - 2)); right -= 5; }
                string coord = CatchCoord(c);
                if (coord.Length > 0)
                {
                    int cw = UiText.MeasureText(coord, FCoordSmall, Size.Empty, L).Width;
                    if (right - cw >= minLeft)
                    {
                        right -= cw;
                        UiText.DrawText(g, coord, FCoordSmall, new Rectangle(right, y, cw + 2, RowH), onMap ? Theme.Accent : Theme.TextMuted, L);
                        right -= 6;
                    }
                }
                int nameMax = Math.Max(20, right - left - wW - 6);
                var nameCol = GradeNameColor(c.Windex);
                if (!onMap && c.Windex == null) nameCol = Theme.TextMuted;
                UiText.DrawText(g, c.Fish, FLog, new Rectangle(left, y, nameMax, RowH), nameCol, L | TextFormatFlags.EndEllipsis);
                int nx = left + Math.Min(nameW, nameMax) + 6;
                UiText.DrawText(g, w, FLog, new Rectangle(nx, y, wW + 2, RowH), Theme.Text, L);
                y += RowH;
            }
        }

        // ---------------- OVERLAY-03: игрок, забросы, круглая карта ----------------
        /// <summary>Поза для показа: не старше 30 мин и снята на текущем водоёме (после загрузки базы Player.log).</summary>
        public PlayerPose? ShownPose
        {
            get
            {
                var p = PoseSource?.Invoke();
                if (p == null) return null;
                if ((Clock() - p.Time).TotalMinutes > RecentPoseMinutes) return null;
                var since = Data?.LocationSince;
                if (since.HasValue && p.Time < since.Value.AddSeconds(-2)) return null;
                return p;
            }
        }

        public bool PoseLive => ShownPose is { } p && (Clock() - p.Time).TotalSeconds <= LivePoseSeconds;

        /// <summary>Вызывается часто (OverlayManager): новая поза → следовать за игроком.</summary>
        public void OnPoseTick()
        {
            var p = PoseSource?.Invoke();
            DateTime t = p?.Time ?? DateTime.MinValue;
            if (t == _poseSeen) return;
            _poseSeen = t;
            // OVERLAY-04: игрок стоит на месте и не повернулся — ничего не перерисовываем
            if (p != null && !double.IsNaN(_poseDrawn.x) && Math.Abs(p.X - _poseDrawn.x) < 0.3 && Math.Abs(p.Z - _poseDrawn.z) < 0.3 &&
                Math.Abs(((p.Yaw ?? 0) - _poseDrawn.yaw + 540) % 360 - 180) < 3) return;
            _poseDrawn = p == null ? (double.NaN, double.NaN, double.NaN) : (p.X, p.Z, p.Yaw ?? 0);
            if (Follow) Recenter(); else Invalidate();
        }

        /// <summary>Последний улов (текущая рыбалка, затем история) с учётом фильтра веса.</summary>
        public MapCatch? LastCatch => VisibleLog().FirstOrDefault();

        public bool OnThisMap(MapCatch c) => c.Verified && c.X.HasValue && c.Z.HasValue && Data?.LocationId != null && c.LocationId == Data.LocationId;

        /// <summary>Игровые координаты «X:Y» (сетка игры) или мировые X/Z, если сетки этого водоёма нет.</summary>
        public string CoordText(double x, double z) => CoordText(Data?.LocationId, x, z);

        public static string CoordText(uint? loc, double x, double z)
        {
            var gr = MapGridCoordinates.ToGrid(loc, x, z);
            if (gr != null) return gr.Value.X.ToString(CultureInfo.InvariantCulture) + " : " + gr.Value.Y.ToString(CultureInfo.InvariantCulture);
            return "X " + Math.Round(x).ToString("0", CultureInfo.InvariantCulture) + "  Z " + Math.Round(z).ToString("0", CultureInfo.InvariantCulture);
        }

        /// <summary>Цвет имени рыбы как в игре (FishTemplate.getColorFullName, по windex): 0 светлый, 1 зелёный, 2 синий, 3 оранжевый.</summary>
        public static Color GradeNameColor(int? windex) => windex switch
        {
            0 => Color.FromArgb(255, 255, 199), 1 => Color.FromArgb(128, 255, 96), 2 => Color.FromArgb(120, 150, 255), 3 => Color.FromArgb(255, 160, 32),
            _ => Theme.Text
        };

        /// <summary>Картинка наживки улова (из справочника игры), если есть.</summary>
        public static Image? BaitImage(MapCatch c) =>
            c.BaitIconPath.Length > 0 ? IconLoader.Load(c.BaitIconPath) : c.BaitId.HasValue ? UiKit.BaitIcon(c.BaitId) : null;

        /// <summary>Координаты улова в сетке его водоёма (или X/Z).</summary>
        public static string CatchCoord(MapCatch c) =>
            c.X.HasValue && c.Z.HasValue ? CoordText(c.LocationId, c.X.Value, c.Z.Value).Replace(" : ", ":") : "";

        public static string AgeText(TimeSpan a) =>
            a.TotalSeconds < 60 ? Math.Max(0, (int)a.TotalSeconds) + " с" : a.TotalMinutes < 120 ? (int)a.TotalMinutes + " мин" :
            a.TotalHours < 48 ? (int)a.TotalHours + " ч" : (int)a.TotalDays + " дн";

        private static string Compass(double yaw)
        {
            string[] n = { "С", "СВ", "В", "ЮВ", "Ю", "ЮЗ", "З", "СЗ" };
            return n[(int)Math.Round(yaw / 45.0) % 8];
        }

        /// <summary>Строка «Вы»: координаты и свежесть позиции.</summary>
        public (string value, string sub, Color subColor) PlayerInfo()
        {
            var p = ShownPose;
            if (p == null)
            {
                var raw = PoseSource?.Invoke();
                string why = raw == null ? "позиция ещё не пришла (раз в 10 с)"
                           : Data?.LocationSince is { } s && raw.Time < s ? "позиция с прошлого водоёма"
                           : "позиция устарела";
                return ("— : —", why, Theme.TextMuted);
            }
            var age = Clock() - p.Time;
            string dir = p.Yaw.HasValue ? " · взгляд " + Compass(p.Yaw.Value) : "";
            if (age.TotalSeconds <= LivePoseSeconds) return (CoordText(p.X, p.Z), "сейчас" + dir, Theme.TextMuted);
            return (CoordText(p.X, p.Z), AgeText(age) + " назад" + dir, Theme.Warning);
        }

        /// <summary>Строка «Поимка»: координаты и глубина последнего улова.</summary>
        public (string value, string sub, Color subColor) CatchInfo()
        {
            var c = LastCatch;
            if (c == null) return ("— : —", "уловов пока нет", Theme.TextMuted);
            string depth = c.DepthCm.HasValue && c.DepthCm.Value > 0 ? "глубина " + (c.DepthCm.Value / 100.0).ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")) + " м" : "глубина ?";
            if (!(c.X.HasValue && c.Z.HasValue)) return ("— : —", depth + " · без координат", Theme.TextMuted);
            return (CoordText(c.X.Value, c.Z.Value), depth + (OnThisMap(c) ? "" : " · другой водоём"), Theme.TextMuted);
        }

        private void DrawInfo(Graphics g)
        {
            var r = InfoRect;
            using (var b = new SolidBrush(Theme.Surface)) g.FillRectangle(b, r);
            int half = r.Width / 2;
            void Col(int x0, int w, Color dot, string label, (string value, string sub, Color subColor) v)
            {
                using (var b = new SolidBrush(dot)) g.FillEllipse(b, x0 + 8, r.Top + 8, 7, 7);
                UiText.DrawText(g, label, FSmall, new Point(x0 + 18, r.Top + 4), Theme.TextMuted, TextFormatFlags.NoPadding);
                var lsz = UiText.MeasureText(label, FSmall, Size.Empty, TextFormatFlags.NoPadding);
                UiText.DrawText(g, v.value, FCoord, new Rectangle(x0 + 22 + lsz.Width, r.Top + 1, w - lsz.Width - 26, 20), Theme.Text,
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
                UiText.DrawText(g, v.sub, FSmall, new Rectangle(x0 + 8, r.Top + 22, w - 12, 15), v.subColor,
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
            Col(0, half, PlayerColor, "Вы", PlayerInfo());
            Col(half, r.Width - half, CastCatchColor, "Поимка", CatchInfo());
            using var pen = new Pen(Theme.Border);
            g.DrawLine(pen, half, r.Top + 6, half, r.Bottom - 6);
            g.DrawLine(pen, 0, r.Bottom - 1, r.Width, r.Bottom - 1);
        }

        private void DrawCatchPanel(Graphics g)
        {
            var r = CatchRect;
            float cx = MapRect.Left + MapRect.Width / 2f;
            // «ножка» от низа круга к панели
            using (var neck = new GraphicsPath())
            {
                float yTop = MapRect.Bottom - 6, yBot = r.Top + 1;
                neck.AddPolygon(new[] { new PointF(cx - 7, yTop), new PointF(cx + 7, yTop), new PointF(cx + 15, yBot), new PointF(cx - 15, yBot) });
                using var b = new SolidBrush(Theme.SurfaceAlt); g.FillPath(b, neck);
                using var pn = new Pen(Theme.Border); g.DrawLine(pn, cx - 7, yTop, cx - 15, yBot); g.DrawLine(pn, cx + 7, yTop, cx + 15, yBot);
            }
            using (var path = RoundRect(r, 8))
            {
                using var b = new SolidBrush(Theme.SurfaceAlt); g.FillPath(b, path);
                using var pn = new Pen(Theme.Border); g.DrawPath(pn, path);
            }
            var c = LastCatch;
            if (c == null)
            {
                UiText.DrawText(g, "Последняя поимка появится здесь", FSmall, r, Theme.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }
            using (var b = new SolidBrush(GradeBadge.ColorOf(c.Windex) ?? CastCatchColor)) g.FillRectangle(b, r.Left + 1, r.Top + 7, 3, r.Height - 14);
            int x = r.Left + 10;
            var icon = IconLoader.Load(c.IconPath);
            if (icon != null) { g.DrawImage(icon, new Rectangle(x, r.Top + 4, r.Height - 8, r.Height - 8)); x += r.Height - 4; }
            var t = OverlayDataSource.PlausibleTime(c.TimeText);
            string when = t.HasValue ? AgeText(Clock() - t.Value) + " назад" : "";
            var wsz = UiText.MeasureText(when, FSmall, Size.Empty, TextFormatFlags.NoPadding);
            UiText.DrawText(g, when, FSmall, new Point(r.Right - wsz.Width - 8, r.Top + 4), Theme.TextMuted, TextFormatFlags.NoPadding);
            string w = c.Weight.ToString("#,0", CultureInfo.GetCultureInfo("ru-RU")) + " г";
            const TextFormatFlags PF = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            int avail = r.Right - x - wsz.Width - 14, wwP = UiText.MeasureText(w, FPanel, Size.Empty, PF).Width;
            int nmax = Math.Max(30, avail - wwP - 6), nw = Math.Min(nmax, UiText.MeasureText(c.Fish, FPanel, Size.Empty, PF).Width);
            UiText.DrawText(g, c.Fish, FPanel, new Rectangle(x, r.Top + 2, nmax, 17), GradeNameColor(c.Windex), PF | TextFormatFlags.EndEllipsis);
            UiText.DrawText(g, w, FPanel, new Rectangle(x + nw + 6, r.Top + 2, wwP + 2, 17), Theme.Text, PF);
            string line2 = c.X.HasValue && c.Z.HasValue ? CoordText(c.X.Value, c.Z.Value) : "без координат";
            var pose = ShownPose;
            if (pose != null && OnThisMap(c)) line2 += " · " + Math.Round(Math.Sqrt(Math.Pow(c.X!.Value - pose.X, 2) + Math.Pow(c.Z!.Value - pose.Z, 2))).ToString("0") + " м от вас";
            int x2 = x;
            var bimg = BaitImage(c);
            if (bimg != null) { g.DrawImage(bimg, new Rectangle(x2, r.Top + 19, 14, 14)); x2 += 17; }
            if (c.Bait.Length > 0) line2 += " · " + c.Bait;
            UiText.DrawText(g, line2, FSmall, new Rectangle(x2, r.Top + 19, r.Right - x2 - 8, 15), Theme.TextMuted,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        private static GraphicsPath RoundRect(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); int d = rad * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d - 1, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d - 1, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        private PointF CircleCenter => new(MapRect.Left + MapRect.Width / 2f, MapRect.Top + MapRect.Height / 2f);
        private float CircleR => MapRect.Width / 2f;

        private void DrawRangeRings(Graphics g)
        {
            var c = CircleCenter; float r = CircleR / 2;
            using var pen = new Pen(Color.FromArgb(60, Theme.Accent), 1f) { DashStyle = DashStyle.Dash };
            g.DrawEllipse(pen, c.X - r, c.Y - r, 2 * r, 2 * r);
            string half = (Settings.RadiusMeters / 2).ToString("0", CultureInfo.InvariantCulture);
            UiText.DrawText(g, half, FCoordSmall, new Point((int)(c.X + r * 0.71f) + 2, (int)(c.Y + r * 0.71f) - 2), Color.FromArgb(150, Theme.Accent), TextFormatFlags.NoPadding);
        }

        private void DrawRing(Graphics g)
        {
            var c = CircleCenter; float R = CircleR;
            using (var pen = new Pen(Color.FromArgb(150, Theme.Accent), 2f)) g.DrawEllipse(pen, MapRect.X + 1, MapRect.Y + 1, MapRect.Width - 2, MapRect.Height - 2);
            using (var tick = new Pen(Color.FromArgb(170, Theme.Accent), 1.4f))
                for (int a = 0; a < 360; a += 30)
                {
                    double rad = (a - 90) * Math.PI / 180; float len = a % 90 == 0 ? 8 : 4;
                    g.DrawLine(tick, c.X + (float)Math.Cos(rad) * (R - 1), c.Y + (float)Math.Sin(rad) * (R - 1),
                                     c.X + (float)Math.Cos(rad) * (R - 1 - len), c.Y + (float)Math.Sin(rad) * (R - 1 - len));
                }
            // север — табличка на кольце
            var n = new Rectangle((int)c.X - 9, MapRect.Top - 6, 18, 14);
            using (var p = RoundRect(n, 6)) { using var b = new SolidBrush(Theme.Accent); g.FillPath(b, p); }
            UiText.DrawText(g, "С", FCoordSmall, n, Theme.TextOnAccent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            // радиус — внизу справа на кольце
            string rt = Settings.RadiusMeters.ToString("0", CultureInfo.InvariantCulture) + " м";
            var sz = UiText.MeasureText(rt, FCoordSmall, Size.Empty, TextFormatFlags.NoPadding);
            var rr = new Rectangle((int)(c.X + R * 0.71f) - sz.Width / 2 - 4, (int)(c.Y + R * 0.71f) - 7, sz.Width + 8, 14);
            using (var p = RoundRect(rr, 6)) { using var b = new SolidBrush(Theme.Surface); g.FillPath(b, p); using var pn = new Pen(Color.FromArgb(150, Theme.Accent)); g.DrawPath(pn, p); }
            UiText.DrawText(g, rt, FCoordSmall, rr, Theme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            // игрок за краем круга — стрелка на кольце
            var pose = ShownPose;
            if (pose != null)
            {
                var s = WorldToScreen(pose.X, pose.Z);
                double dx = s.X - c.X, dy = s.Y - c.Y, dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > R - 4 && dist > 0)
                {
                    float ux = (float)(dx / dist), uy = (float)(dy / dist);
                    var tip = new PointF(c.X + ux * (R - 3), c.Y + uy * (R - 3));
                    var bse = new PointF(c.X + ux * (R - 15), c.Y + uy * (R - 15));
                    using var b = new SolidBrush(PlayerColor);
                    g.FillPolygon(b, new[] { tip, new PointF(bse.X - uy * 6, bse.Y + ux * 6), new PointF(bse.X + uy * 6, bse.Y - ux * 6) });
                }
            }
        }

        /// <summary>TROPHY-01 (C): свежие пятна шанса (≤15 мин, круг ≈12 м) и стрелка «сдвиньтесь» к пятну той же снасти в 25–50 м.</summary>
        private void DrawSpots(Graphics g)
        {
            var co = Coach; if (co == null) return;
            var placed = new List<Rectangle>();
            foreach (var sp in co.Spots.Where(s => s.Ticks >= ChanceSpots.MinTicks).OrderByDescending(s => s.Median))
            {
                var s = WorldToScreen(sp.X, sp.Z); var e = WorldToScreen(sp.X + ChanceSpots.MergeM, sp.Z);
                float rad = Math.Max(6, Math.Abs(e.X - s.X));
                int alpha = (int)Math.Round(110 * Math.Clamp(1 - sp.AgeMin / ChanceSpots.MaxAgeMin, 0.45, 1));
                Color col = sp.Level == 2 ? Theme.AccentGreen : sp.Level == 1 ? Theme.Warning : Theme.TextMuted;   // SPOTS-05
                using (var b = new SolidBrush(Color.FromArgb(alpha / 2, col))) g.FillEllipse(b, s.X - rad, s.Y - rad, 2 * rad, 2 * rad);
                using (var pn = new Pen(Color.FromArgb(alpha + 60, col), 1f) { DashStyle = DashStyle.Dot }) g.DrawEllipse(pn, s.X - rad, s.Y - rad, 2 * rad, 2 * rad);
                // SPOTS-02: цифра чёрная жирная на белой подложке — на голубой воде зелёный/оранжевый текст не читался
                string t = sp.Median.ToString(sp.Median < 10 ? "0.0" : "0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
                SpotLabel(g, t, s, placed);
            }
            foreach (var r in co.Rods.Where(r => r.MoveX.HasValue && r.Setup.X.HasValue))
            {
                var a = WorldToScreen(r.Setup.X!.Value, r.Setup.Z!.Value); var b2 = WorldToScreen(r.MoveX!.Value, r.MoveZ!.Value);
                using var pn = new Pen(Color.Black, 2f) { DashStyle = DashStyle.Dash, CustomEndCap = new AdjustableArrowCap(4, 5) };
                g.DrawLine(pn, a, b2);
            }
        }

        private static readonly Font FSpot = new("Bahnschrift SemiBold", 9.5f);
        /// <summary>SPOTS-02: подпись пятна — чёрный текст по центру круга, без подложки (подложка закрывала карту).</summary>
        internal static void SpotLabel(Graphics g, string t, PointF c, List<Rectangle>? placed = null)
        {
            var sz = UiText.MeasureText(g, t, FSpot, Size.Empty, TextFormatFlags.NoPadding);
            var r = new Rectangle((int)(c.X - sz.Width / 2f) - 3, (int)(c.Y - sz.Height / 2f) - 1, sz.Width + 6, sz.Height + 2);
            if (placed != null) { if (placed.Any(q => q.IntersectsWith(r))) return; placed.Add(r); }   // подписи не налезают: остаётся более высокий шанс
            UiText.DrawText(g, t, FSpot, r, Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawCastMarks(Graphics g)
        {
            DrawSpots(g);
            var list = Data?.Casts;
            if (list == null || list.Count == 0) return;
            foreach (var m in list.OrderBy(k => k.Active).ThenBy(k => k.Time))
            {
                var s = WorldToScreen(m.X, m.Z);
                var col = CastStateColor(m.State);
                if (m.Active)
                {
                    if (m.PlayerX.HasValue && m.PlayerZ.HasValue)
                    {
                        var from = WorldToScreen(m.PlayerX.Value, m.PlayerZ.Value);
                        using var ln = new Pen(Color.FromArgb(150, col), 1.3f) { DashStyle = DashStyle.Dash };
                        g.DrawLine(ln, from, s);
                        double dist = Math.Sqrt(Math.Pow(m.X - m.PlayerX.Value, 2) + Math.Pow(m.Z - m.PlayerZ.Value, 2));
                        string dt = Math.Round(dist).ToString("0") + " м";
                        var dsz = UiText.MeasureText(dt, FCoordSmall, Size.Empty, TextFormatFlags.NoPadding);
                        var lr = new Rectangle((int)s.X + 11, (int)s.Y - 7, dsz.Width + 8, 14);
                        using (var lp = RoundRect(lr, 6)) { using var lb = new SolidBrush(Color.FromArgb(215, Theme.Surface)); g.FillPath(lb, lp); }
                        UiText.DrawText(g, dt, FCoordSmall, lr, col, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    }
                    if (m.State is "bite" or "hook")
                        using (var halo = new Pen(Color.FromArgb(200, col), 2f)) g.DrawEllipse(halo, s.X - 9, s.Y - 9, 18, 18);
                    using (var b = new SolidBrush(col)) g.FillEllipse(b, s.X - 5, s.Y - 5, 10, 10);
                    using (var pn = new Pen(Color.White, 1.2f)) g.DrawEllipse(pn, s.X - 5, s.Y - 5, 10, 10);
                }
                else
                {
                    using var b = new SolidBrush(Color.FromArgb(m.State == "done" ? 140 : 200, col));
                    g.FillEllipse(b, s.X - 3, s.Y - 3, 6, 6);
                }
            }
        }

        private void DrawPlayer(Graphics g)
        {
            var p = ShownPose;
            if (p == null) return;
            var s = WorldToScreen(p.X, p.Z);
            bool live = PoseLive;
            if (p.Yaw.HasValue)
            {
                float rad = Math.Max(26, Math.Min(46, CircleR * 0.3f));
                using var path = new GraphicsPath();
                path.AddPie(s.X - rad, s.Y - rad, 2 * rad, 2 * rad, (float)p.Yaw.Value - 90 - 22, 44);
                using var br = new PathGradientBrush(path) { CenterPoint = s, CenterColor = Color.FromArgb(live ? 110 : 60, PlayerColor), SurroundColors = new[] { Color.FromArgb(0, PlayerColor) } };
                g.FillPath(br, path);
            }
            using (var halo = new SolidBrush(Color.FromArgb(live ? 55 : 30, PlayerColor))) g.FillEllipse(halo, s.X - 11, s.Y - 11, 22, 22);
            if (live)
            {
                using var b = new SolidBrush(PlayerColor); g.FillEllipse(b, s.X - 5.5f, s.Y - 5.5f, 11, 11);
                using var pn = new Pen(Color.White, 1.6f); g.DrawEllipse(pn, s.X - 5.5f, s.Y - 5.5f, 11, 11);
            }
            else
            {
                using var pn = new Pen(PlayerColor, 2f) { DashStyle = DashStyle.Dot }; g.DrawEllipse(pn, s.X - 5.5f, s.Y - 5.5f, 11, 11);
            }
        }

        // ---------------- interaction ----------------
        public void ToggleMode() { Settings.Mode = Settings.Mode == OverlayMapMode.Activity ? OverlayMapMode.Bite : OverlayMapMode.Activity; SettingsChanged?.Invoke(); Invalidate(); }

        public void CycleRadius()
        {
            double[] r = { 50, 100, 200 };
            int i = Array.FindIndex(r, v => v > Settings.RadiusMeters + 0.5);
            SetRadius(i < 0 ? r[0] : r[i]);
        }

        public void SetRadius(double m) { Settings.RadiusMeters = Math.Clamp(m, 10, 2000); SettingsChanged?.Invoke(); Invalidate(); }

        public bool CenterOnCatch(MapCatch c)
        {
            if (!(c.Verified && c.X.HasValue && c.Z.HasValue && Data?.LocationId != null && c.LocationId == Data.LocationId)) return false;
            Follow = false; CenterX = c.X.Value; CenterZ = c.Z.Value; Invalidate(); return true;
        }

        public string? HitButton(Point p) => _buttons.FirstOrDefault(b => b.r.Contains(p)).id;

        public void PressButton(string id)
        {
            switch (id)
            {
                case "hide": HideRequested?.Invoke(); break;
                case "settings": SettingsDialogRequested?.Invoke(); break;
                case "follow": Follow = true; Recenter(); break;
                case "radius": CycleRadius(); break;
                case "mode": ToggleMode(); break;
                case "coach_ask": CoachAskRequested?.Invoke(); break;
                case "coach_show": CoachShowRequested?.Invoke(); break;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) { BuildMenu().Show(this, e.Location); return; }
            if (e.Button != MouseButtons.Left) return;
            var id = HitButton(e.Location);
            if (id != null) { PressButton(id); return; }
            if (HeaderRect.Contains(e.Location))
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);   // window drag (no activation)
                return;
            }
            var tabHit = _tabHits.FirstOrDefault(h => h.r.Contains(e.Location));
            if (tabHit.r.Width > 0) { SetLogTab(tabHit.tab); return; }
            if (Settings.LogTab is 1 or 2 && LogRect.Contains(e.Location)) return;
            var hit = _logHits.FirstOrDefault(h => h.r.Contains(e.Location));
            if (hit.c != null) { CenterOnCatch(hit.c); return; }
            if (Round && CatchRect.Contains(e.Location)) { if (LastCatch != null) CenterOnCatch(LastCatch); return; }
            if (MapRect.Contains(e.Location)) { _panning = true; _panLast = e.Location; Capture = true; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_panning && e.Button == MouseButtons.Left)
            {
                int dx = e.X - _panLast.X, dy = e.Y - _panLast.Y;
                if (dx != 0 || dy != 0) { Follow = false; CenterX -= dx / ViewScale; CenterZ += dy / ViewScale; _panLast = e.Location; Invalidate(); }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _panning = false; Capture = false; }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (LogRect.Contains(e.Location))
            {
                if (Settings.LogTab == 1) _feedOffset += e.Delta > 0 ? -1 : 1; else _logOffset += e.Delta > 0 ? -1 : 1;
                Invalidate(); return;
            }
            if (MapRect.Contains(e.Location)) SetRadius(Settings.RadiusMeters * (e.Delta > 0 ? 0.8 : 1.25));
        }

        private ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip { ShowImageMargin = false };
            ToolStripMenuItem Item(string text, bool chk, Action a) { var i = new ToolStripMenuItem(text) { Checked = chk }; i.Click += (s, e) => a(); return i; }
            var mode = new ToolStripMenuItem("Режим");
            mode.DropDownItems.Add(Item("Активность (уловы)", Settings.Mode == OverlayMapMode.Activity, () => { Settings.Mode = OverlayMapMode.Activity; SettingsChanged?.Invoke(); Invalidate(); }));
            mode.DropDownItems.Add(Item("Поклёвки (события)", Settings.Mode == OverlayMapMode.Bite, () => { Settings.Mode = OverlayMapMode.Bite; SettingsChanged?.Invoke(); Invalidate(); }));
            var rad = new ToolStripMenuItem("Радиус");
            foreach (var r in new[] { 50.0, 100, 200 }) rad.DropDownItems.Add(Item(r + " м", Math.Abs(Settings.RadiusMeters - r) < 0.5, () => SetRadius(r)));
            var per = new ToolStripMenuItem("Период");
            foreach (var (h, t) in new[] { (0, "Всё время"), (720, "30 дней"), (168, "7 дней"), (24, "24 часа"), (1, "1 час") })
                per.DropDownItems.Add(Item(t, Settings.RecentHours == h, () => { Settings.RecentHours = h; SettingsChanged?.Invoke(); Invalidate(); }));
            var sp = new ToolStripMenuItem("Вид рыбы");
            sp.DropDownItems.Add(Item("Все виды", SpeciesFilter == null, () => { SpeciesFilter = null; Invalidate(); }));
            foreach (var f in (Data?.Catches ?? new List<MapCatch>()).Where(PassesWeight).GroupBy(c => c.Fish).OrderByDescending(k => k.Count()).Take(20))
            { string name = f.Key; sp.DropDownItems.Add(Item(name + " (" + f.Count() + ")", SpeciesFilter == name, () => { SpeciesFilter = name; Invalidate(); })); }
            var minW = new ToolStripMenuItem("Мин. вес (показ)");
            foreach (var g in OverlaySettings.DisplayWeightPresets)
                minW.DropDownItems.Add(Item(g == 0 ? "Все (0 г)" : "от " + g + " г", Settings.MinDisplayWeightG == g, () => SetMinDisplayWeight(g)));
            bool custom = Array.IndexOf(OverlaySettings.DisplayWeightPresets, Settings.MinDisplayWeightG) < 0;
            minW.DropDownItems.Add(Item(custom ? "Своё: " + Settings.MinDisplayWeightG + " г…" : "Своё значение…", custom, () => SettingsDialogRequested?.Invoke()));
            var feed = new ToolStripMenuItem("Лента");   // NEXT-GTWC-01
            feed.DropDownItems.Add(Item("Показать ленту", Settings.LogTab == 1, () => SetLogTab(Settings.LogTab == 1 ? 0 : 1)));
            feed.DropDownItems.Add(Item("Показать советы", Settings.LogTab == 2, () => SetLogTab(Settings.LogTab == 2 ? 0 : 2)));
            feed.DropDownItems.Add(Item("Игровое время ≈ (оценка)", Settings.FeedShowGameTime, () => { Settings.FeedShowGameTime = !Settings.FeedShowGameTime; SettingsChanged?.Invoke(); Invalidate(); }));
            foreach (int n in new[] { 5, 8, 12, 16 })
                feed.DropDownItems.Add(Item(n + " строк", Settings.FeedRows == n, () => { Settings.FeedRows = n; SettingsChanged?.Invoke(); PerformLayout(); Invalidate(); }));
            var pos = new ToolStripMenuItem("Положение");
            foreach (var (p, t) in new[] { (OverlayPreset.TopLeft, "Слева вверху"), (OverlayPreset.TopRight, "Справа вверху"), (OverlayPreset.BottomLeft, "Слева внизу"), (OverlayPreset.BottomRight, "Справа внизу") })
                pos.DropDownItems.Add(Item(t, Settings.Preset == p, () => ApplyPreset(p)));
            var op = new ToolStripMenuItem("Прозрачность");
            foreach (var v in new[] { 0.4, 0.55, 0.7, 0.85, 1.0 })
                op.DropDownItems.Add(Item((int)(v * 100) + " %", Math.Abs(Settings.Opacity - v) < 0.01, () => { Settings.Opacity = v; ApplyOpacity(); SettingsChanged?.Invoke(); }));
            m.Items.AddRange(new ToolStripItem[] { mode, rad, per, sp, minW, feed, new ToolStripSeparator(), pos, op,
                Item("Сквозные щелчки (Ctrl — управлять)", Settings.ClickThrough, () => { Settings.ClickThrough = !Settings.ClickThrough; SettingsChanged?.Invoke(); }),
                Item("Круглая карта вокруг игрока", Settings.RoundMap, () => { Settings.RoundMap = !Settings.RoundMap; SettingsChanged?.Invoke(); PerformLayout(); Recenter(); }),
                Item(ShownPose != null ? "Следовать за игроком" : "Центрировать на последнем забросе", Follow, () => { Follow = true; Recenter(); }),
                Item("Обновить", false, () => RefreshRequested?.Invoke()),
                new ToolStripSeparator(),
                Item("Настройки…", false, () => SettingsDialogRequested?.Invoke()),
                Item("Скрыть", false, () => HideRequested?.Invoke()) });
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            return m;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = new IntPtr(MA_NOACTIVATE); return; }
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
                int ht = ResizeHitTest(ClientSize, p, HitButton(p) != null);
                if (ht != HTCLIENT) m.Result = new IntPtr(ht);
                return;
            }
            if (m.Msg == WM_EXITSIZEMOVE)
            {
                base.WndProc(ref m);
                Settings.Preset = OverlayPreset.Custom; Settings.X = Left; Settings.Y = Top; Settings.Width = Width; Settings.Height = Height;
                SettingsChanged?.Invoke();
                return;
            }
            base.WndProc(ref m);
        }

        // NEXT-OVERLAY-02: изменение размера за все 4 края и 4 угла (системный цикл WM_NCHITTEST → HT*).
        // Полоса края — ResizeBorder px; угол — ResizeCorner px вдоль соседнего края. Кнопки заголовка имеют приоритет.
        public const int ResizeBorder = 5, ResizeCorner = 16;

        /// <summary>Код WM_NCHITTEST для точки клиентской области: HTLEFT…HTBOTTOMRIGHT или HTCLIENT (1).</summary>
        public static int ResizeHitTest(Size client, Point p, bool onButton = false)
        {
            if (onButton) return HTCLIENT;
            int w = client.Width, h = client.Height;
            if (p.X < 0 || p.Y < 0 || p.X >= w || p.Y >= h) return HTCLIENT;
            bool l = p.X < ResizeBorder, r = p.X >= w - ResizeBorder, t = p.Y < ResizeBorder, b = p.Y >= h - ResizeBorder;
            bool lc = p.X < ResizeCorner, rc = p.X >= w - ResizeCorner, tc = p.Y < ResizeCorner, bc = p.Y >= h - ResizeCorner;
            if ((t && lc) || (l && tc)) return HTTOPLEFT;
            if ((t && rc) || (r && tc)) return HTTOPRIGHT;
            if ((b && lc) || (l && bc)) return HTBOTTOMLEFT;
            if ((b && rc) || (r && bc)) return HTBOTTOMRIGHT;
            if (l) return HTLEFT;
            if (r) return HTRIGHT;
            if (t) return HTTOP;
            if (b) return HTBOTTOM;
            return HTCLIENT;
        }

        /// <summary>Отрисовать окно в растровое изображение (для тестов; без показа на экране).</summary>
        public Bitmap RenderToBitmap()
        {
            PerformLayout();
            var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
            using var g = Graphics.FromImage(bmp);
            OnPaint(new PaintEventArgs(g, new Rectangle(Point.Empty, ClientSize)));
            return bmp;
        }
    }
}
