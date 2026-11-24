// UI-01 / FEAT-11: new interface (beta) — window shell: own title bar, right rail with pressed buttons, exit button, dark theme.
// Steps 0–1 of claude/UI-01_NEW_INTERFACE.md. Content areas are placeholders until steps 2–4.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FisherLogger
{
    /// <summary>Colors and fonts of the new interface (UI-01 «тёмная вода»).</summary>
    public static class NuTheme
    {
        public static readonly Color Ground = Color.FromArgb(0x0F, 0x1A, 0x20);
        public static readonly Color Bar = Color.FromArgb(0x0B, 0x14, 0x18);
        public static readonly Color Panel = Color.FromArgb(0x13, 0x22, 0x2A);
        public static readonly Color Raised = Color.FromArgb(0x16, 0x25, 0x2D);
        public static readonly Color Line = Color.FromArgb(0x1F, 0x31, 0x3A);
        public static readonly Color Line2 = Color.FromArgb(0x2A, 0x42, 0x4E);
        public static readonly Color Text = Color.FromArgb(0xE8, 0xEF, 0xEC);
        public static readonly Color Muted = Color.FromArgb(0x93, 0xA9, 0xA6);
        public static readonly Color Soft = Color.FromArgb(0xB9, 0xC8, 0xC5);
        public static readonly Color Accent = Color.FromArgb(0xFF, 0x8A, 0x3D);
        public static readonly Color AccentText = Color.FromArgb(0xFF, 0xB2, 0x7A);
        public static readonly Color AccentDeep = Color.FromArgb(0x2A, 0x1C, 0x10);
        public static readonly Color PressedBg = Color.FromArgb(0x1A, 0x12, 0x08);
        public static readonly Color Ok = Color.FromArgb(0x6F, 0xCF, 0x8E);
        public static readonly Color Danger = Color.FromArgb(0xE5, 0x9A, 0x8C);
        public static readonly Color Water = Color.FromArgb(0x27, 0x6E, 0x86);

        private static string? _family;
        /// <summary>Golos Text if installed, else Segoe UI.</summary>
        public static string Family
        {
            get
            {
                if (_family != null) return _family;
                try { using var f = new Font("Golos Text", 10f); _family = f.Name == "Golos Text" ? "Golos Text" : "Segoe UI"; }
                catch { _family = "Segoe UI"; }
                return _family;
            }
        }
        public static Font F(float size, FontStyle st = FontStyle.Regular) => new(Family, size, st, GraphicsUnit.Point);

        public static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); int d = rad * 2;
            if (rad <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        /// <summary>Moscow time → game clock (game hour = MSK hours × 12 mod 24, MECH-02 §2) and the bite window now.</summary>
        public static (string game, string? window, TimeSpan left) GameClock(DateTime msk)
        {
            double h = msk.Hour + msk.Minute / 60.0 + msk.Second / 3600.0;
            double g = (h * 12) % 24; int gh = (int)g, gm = (int)((g - gh) * 60);
            string game = gh.ToString("00") + ":" + gm.ToString("00");
            int sec = msk.Minute * 60 + msk.Second; bool even = msk.Hour % 2 == 0;
            if (even && sec >= 22 * 60 + 30 && sec < 36 * 60 + 30) return (game, "Утренний клёв", TimeSpan.FromSeconds(36 * 60 + 30 - sec));
            if (!even && sec >= 37 * 60 + 30 && sec < 47 * 60 + 30) return (game, "Вечерний клёв", TimeSpan.FromSeconds(47 * 60 + 30 - sec));
            return (game, null, TimeSpan.Zero);
        }

        public static DateTime MoscowNow()
        {
            try { return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Russian Standard Time"); }
            catch { return DateTime.UtcNow.AddHours(3); }
        }
    }

    /// <summary>Square rail button that stays pressed («вжимается»).</summary>
    public sealed class NuRailButton : Control
    {
        public string Id { get; }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public bool Pressed { get => _pressed; set { _pressed = value; AccessibleDescription = value ? "нажата" : ""; Invalidate(); } }
        private bool _pressed, _hover;
        private readonly Action<Graphics, Rectangle, Color> _icon;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public Color IdleColor { get; set; } = NuTheme.Soft;
        private static readonly ToolTip Tips = new();
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public bool Danger { get; set; }

        public NuRailButton(string id, string label, Action<Graphics, Rectangle, Color> icon)
        {
            Id = id; _icon = icon; Size = new Size(44, 44); Margin = new Padding(10, 3, 10, 3);
            AccessibleName = label; AccessibleRole = AccessibleRole.PushButton; Cursor = Cursors.Hand; TabStop = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Tips.SetToolTip(this, label);
        }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty); base.OnKeyDown(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(NuTheme.Bar);
            var r = new Rectangle(0, _pressed ? 1 : 0, Width - 1, Height - 2);
            using (var path = NuTheme.Round(r, 11))
            {
                using var bg = new SolidBrush(_pressed ? NuTheme.PressedBg : _hover ? NuTheme.Raised : NuTheme.Panel);
                g.FillPath(bg, path);
                if (_pressed)
                {   // inner shadow: darker band along the top edge
                    using var sh = new LinearGradientBrush(new Rectangle(r.X, r.Y, r.Width, 10), Color.FromArgb(150, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), LinearGradientMode.Vertical);
                    var st = g.Save(); g.SetClip(path); g.FillRectangle(sh, r.X, r.Y, r.Width, 10); g.Restore(st);
                }
                using var pen = new Pen(Danger ? Color.FromArgb(0x3A, 0x2A, 0x2A) : _pressed ? Color.FromArgb(0x3F, 0x2A, 0x18) : NuTheme.Line, 1f);
                g.DrawPath(pen, path);
            }
            if (Focused) { using var f = new Pen(NuTheme.Accent, 2f); using var fp = NuTheme.Round(new Rectangle(1, 1, Width - 3, Height - 3), 11); g.DrawPath(f, fp); }
            var col = Danger ? NuTheme.Danger : _pressed ? NuTheme.Accent : IdleColor;
            _icon(g, new Rectangle(r.X + (r.Width - 22) / 2, r.Y + (r.Height - 22) / 2, 22, 22), col);
        }
    }

    /// <summary>Stroke icons for the rail (24-unit grid scaled into the rect).</summary>
    public static class NuIcons
    {
        private static Pen P(Color c, Rectangle r) => new(c, 1.7f * r.Width / 24f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        private static PointF Pt(Rectangle r, float x, float y) => new(r.X + x * r.Width / 24f, r.Y + y * r.Height / 24f);
        private static RectangleF Rc(Rectangle r, float x, float y, float w, float h) => new(r.X + x * r.Width / 24f, r.Y + y * r.Height / 24f, w * r.Width / 24f, h * r.Height / 24f);

        public static void Fish(Graphics g, Rectangle r, Color c)
        {
            using var p = P(c, r); using var path = new GraphicsPath();
            path.AddBezier(Pt(r, 3, 12), Pt(r, 7, 6), Pt(r, 13, 6), Pt(r, 18, 12));
            path.AddBezier(Pt(r, 18, 12), Pt(r, 13, 18), Pt(r, 7, 18), Pt(r, 3, 12));
            g.DrawPath(p, path);
            g.DrawLines(p, new[] { Pt(r, 18, 12), Pt(r, 21, 9), Pt(r, 21, 15), Pt(r, 18, 12) });
            using var b = new SolidBrush(c); g.FillEllipse(b, Rc(r, 7, 10.5f, 2, 2));
        }
        public static void Journal(Graphics g, Rectangle r, Color c)
        {
            using var p = P(c, r);
            using (var path = NuTheme.Round(Rectangle.Round(Rc(r, 5, 3, 14, 18)), (int)(2 * r.Width / 24f))) g.DrawPath(p, path);
            g.DrawLine(p, Pt(r, 9, 8), Pt(r, 15, 8)); g.DrawLine(p, Pt(r, 9, 12), Pt(r, 15, 12)); g.DrawLine(p, Pt(r, 9, 16), Pt(r, 13, 16));
        }
        public static void Spark(Graphics g, Rectangle r, Color c)
        {
            using var p = P(c, r);
            g.DrawPolygon(p, new[] { Pt(r, 12, 3), Pt(r, 13.8f, 7.7f), Pt(r, 18.5f, 9.5f), Pt(r, 13.8f, 11.3f), Pt(r, 12, 16), Pt(r, 10.2f, 11.3f), Pt(r, 5.5f, 9.5f), Pt(r, 10.2f, 7.7f) });
            g.DrawPolygon(p, new[] { Pt(r, 18, 15), Pt(r, 18.8f, 17.2f), Pt(r, 21, 18), Pt(r, 18.8f, 18.8f), Pt(r, 18, 21), Pt(r, 17.2f, 18.8f), Pt(r, 15, 18), Pt(r, 17.2f, 17.2f) });
        }
        public static void Phone(Graphics g, Rectangle r, Color c)
        {
            using var p = P(c, r);
            using (var path = NuTheme.Round(Rectangle.Round(Rc(r, 7, 2.5f, 10, 19)), (int)(2.5f * r.Width / 24f))) g.DrawPath(p, path);
            g.DrawLine(p, Pt(r, 11, 18.5f), Pt(r, 13, 18.5f));
        }
        public static void Clock(Graphics g, Rectangle r, Color c)
        {
            using var p = P(c, r);
            g.DrawEllipse(p, Rc(r, 3.5f, 3.5f, 17, 17));
            g.DrawLines(p, new[] { Pt(r, 12, 7), Pt(r, 12, 12), Pt(r, 15.5f, 14) });
        }
        public static void Stats(Graphics g, Rectangle r, Color c)
        {
            using var p = P(c, r);
            g.DrawLine(p, Pt(r, 4, 20), Pt(r, 4, 10)); g.DrawLine(p, Pt(r, 10, 20), Pt(r, 10, 4)); g.DrawLine(p, Pt(r, 16, 20), Pt(r, 16, 13)); g.DrawLine(p, Pt(r, 22, 20), Pt(r, 2, 20));
        }
        public static void Gear(Graphics g, Rectangle r, Color c)
        {
            using var p = P(c, r);
            g.DrawEllipse(p, Rc(r, 9, 9, 6, 6));
            var pts = new List<PointF>();
            for (int i = 0; i < 16; i++)
            {
                double a = i * Math.PI / 8; float rad = i % 2 == 0 ? 9.2f : 7.0f;
                pts.Add(Pt(r, 12 + (float)(Math.Cos(a) * rad), 12 + (float)(Math.Sin(a) * rad)));
            }
            g.DrawPolygon(p, pts.ToArray());
        }
        /// <summary>UI-02-6: иконка «карта» — сложенная карта с линией-стыком.</summary>
        public static void Map(Graphics g, Rectangle r, Color c)
        {
            using var p = P(c, r);
            using var path = new GraphicsPath();
            path.AddLine(Pt(r, 2.5f, 6), Pt(r, 8.5f, 3.5f));
            path.AddLine(Pt(r, 8.5f, 3.5f), Pt(r, 15.5f, 6));
            path.AddLine(Pt(r, 15.5f, 6), Pt(r, 21.5f, 3.5f));
            path.AddLine(Pt(r, 21.5f, 3.5f), Pt(r, 21.5f, 18));
            path.AddLine(Pt(r, 21.5f, 18), Pt(r, 15.5f, 20.5f));
            path.AddLine(Pt(r, 15.5f, 20.5f), Pt(r, 8.5f, 18));
            path.AddLine(Pt(r, 8.5f, 18), Pt(r, 2.5f, 20.5f));
            path.AddLine(Pt(r, 2.5f, 20.5f), Pt(r, 2.5f, 6));
            g.DrawPath(p, path);
            g.DrawLine(p, Pt(r, 8.5f, 3.5f), Pt(r, 8.5f, 18));
            g.DrawLine(p, Pt(r, 15.5f, 6), Pt(r, 15.5f, 20.5f));
        }
        public static void Power(Graphics g, Rectangle r, Color c)
        {
            using var p = P(c, r);
            g.DrawLine(p, Pt(r, 12, 3), Pt(r, 12, 11));
            g.DrawArc(p, Rc(r, 4, 5, 16, 16), -50, 280);
        }
        public static void Radar(Graphics g, Rectangle r, Color c)
        {
            using var p = P(c, r);
            g.DrawEllipse(p, Rc(r, 3, 3, 18, 18)); g.DrawEllipse(p, Rc(r, 7.5f, 7.5f, 9, 9)); g.DrawLine(p, Pt(r, 12, 12), Pt(r, 18, 6));
        }
    }

    /// <summary>The new main window (beta). Owns no logic: Form1 feeds it through callbacks.</summary>
    public sealed class NuShellForm : Form
    {
        public const int TitleH = 44, RailW = 64, SideW = 340, RodsH = 92;

        public Func<string>? WaterName;              // «Франция: Пруды Перруше»
        public Func<bool>? Recording;                 // capture is running
        public Func<bool>? MiniMapShown;              // in-game minimap visible
        public Action? ToggleMiniMap;                 // «\»
        public Action? ExitApp;                       // ⏻
        public Action? HideToTray;                    // ✕
        public Func<bool>? MinimizeToTray;
        public Func<string>? MiniKeyText;             // the in-game minimap key from its settings            // «Сворачивать в трей» from the settings
        public Action<string>? OpenLegacy;            // temporary bridge to the old windows (step 0–1): "settings", "phone", "journal", "stats", "advisor"

        private readonly Panel _title = new() { Dock = DockStyle.Top, Height = TitleH };
        private readonly FlowLayoutPanel _rail = new() { Dock = DockStyle.Right, Width = RailW, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(0, 10, 0, 0) };
        private readonly Panel _side = new() { Dock = DockStyle.Right, Width = SideW };
        private readonly Panel _work = new() { Dock = DockStyle.Fill };
        private readonly Panel _rods = new() { Dock = DockStyle.Bottom, Height = RodsH };
        private readonly Panel _map = new() { Dock = DockStyle.Fill };
        private readonly Panel _ai = new() { Visible = false, Size = new Size(400, 300) };
        // UI-02-6: «Карта любого водоёма» — выбор водоёма + карта с уловами (все сессии).
        private readonly Panel _waterStrip = new() { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 6, 8, 6), Visible = false };
        private readonly ComboBox _waterPick = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280, FlatStyle = FlatStyle.Flat };
        private readonly Label _waterLabel = new() { Text = "Водоём:", AutoSize = true, Margin = new Padding(0, 6, 8, 0) };
        /// <summary>UI-02-6: выбран водоём в режиме «карта любого водоёма» (событие = переписать карту).</summary>
        public event Action? MapPickChanged;
        public uint? MapPickLoc;
        /// <summary>Step 2 (FEAT-12): the depth map of the current water.</summary>
        public NuMapView MapView { get; } = new() { Dock = DockStyle.Fill };
        /// <summary>FEAT-13: journal and statistics.</summary>
        public NuStatsView Stats { get; } = new() { Visible = false };
        /// <summary>FEAT-15: the rest of the window.</summary>
        public NuRodsStrip RodsStrip { get; } = new() { Dock = DockStyle.Fill };
        public NuCatchList Catches { get; } = new() { Dock = DockStyle.Fill };
        public NuTipsPanel TipsPanel { get; } = new() { Dock = DockStyle.Fill, Visible = false };
        public NuHoursPanel Hours { get; } = new() { Dock = DockStyle.Fill, Visible = false };
        public NuFormHost PhoneHost { get; } = new() { Visible = false, EmptyText = "Телефон недоступен" };
        public NuFormHost SettingsHost { get; } = new() { Visible = false, EmptyText = "Настройки недоступны" };
        public NuFormHost AiHost { get; } = new() { EmptyText = "Помощник недоступен" };
        private readonly NuRailButton _exit;
        private readonly List<NuRailButton> _buttons = new();
        private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
        private Rectangle _btnMin, _btnMax, _btnClose, _btnMini;
        private int _hoverBtn = -1;
        private bool _ai1;

        /// <summary>Which content is open: "catches" (default), "tips", "phone", "hours" — side panel; "journal", "stats", "settings" — instead of the map.</summary>
        public string Mode { get; private set; } = "catches";
        public bool AssistantOpen => _ai1;

        public NuShellForm()
        {
            Text = "Анализатор Fisher Online";
            FormBorderStyle = FormBorderStyle.None;   // own title bar; WS_CAPTION|WS_THICKFRAME are put back in CreateParams for snap and resize
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = NuTheme.Ground; ForeColor = NuTheme.Text; Font = NuTheme.F(10f);
            MinimumSize = new Size(960, 600);
            var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1440, 900);
            Size = new Size(Math.Min(1440, wa.Width - 40), Math.Min(900, wa.Height - 40));
            try { if (Theme.AppIcon != null) Icon = Theme.AppIcon; } catch { }
            DoubleBuffered = true; KeyPreview = true;

            foreach (var p in new Control[] { _title, _rail, _side, _work, _rods, _map, _ai })
                p.GetType().GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(p, true);

            _title.BackColor = NuTheme.Bar; _rail.BackColor = NuTheme.Bar; _side.BackColor = NuTheme.Panel; _work.BackColor = NuTheme.Ground;
            _title.Paint += PaintTitle; _title.MouseMove += TitleMove; _title.MouseLeave += (s, e) => { _hoverBtn = -1; _title.Invalidate(); };
            _title.MouseDown += TitleDown; _title.MouseUp += TitleUp; _title.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left && HitButton(e.Location) < 0) ToggleMax(); };
            _map.Paint += PaintMap; _ai.Paint += PaintAi;
            _side.Controls.Add(Catches); _side.Controls.Add(TipsPanel); _side.Controls.Add(Hours); _side.Controls.Add(PhoneHost);
            _rods.Controls.Add(RodsStrip);
            _ai.Padding = new Padding(1, 44, 1, 1); _ai.Controls.Add(AiHost); _ai.BackColor = NuTheme.Raised;
            _map.Resize += (s, e) => { _map.Invalidate(); PlaceAi(); };
            Padding = new Padding(5);   // the 5-px frame belongs to the form itself: resizing by the edges works (WM_NCHITTEST)
            _ai.MouseClick += (s, e) => { if (new Rectangle(_ai.Width - 36, 8, 28, 28).Contains(e.Location)) { _ai1 = false; Sync(); } };

            _work.Controls.Add(_map); _work.Controls.Add(_rods); _map.Controls.Add(MapView); _map.Controls.Add(Stats); _map.Controls.Add(SettingsHost); _map.Controls.Add(_ai); _ai.BringToFront();
            // UI-02-6: полоска выбора водоёма над картой (появляется только в режиме «карта любого водоёма»).
            _waterLabel.ForeColor = NuTheme.Text; _waterPick.BackColor = NuTheme.Raised; _waterPick.ForeColor = NuTheme.Text;
            _waterStrip.Controls.Add(_waterLabel); _waterStrip.Controls.Add(_waterPick);
            _waterPick.SelectedIndexChanged += (s, e) => { if (_waterPick.SelectedItem is KeyValuePair<uint, string> kv) { MapPickLoc = kv.Key; MapPickChanged?.Invoke(); } };
            _work.Controls.Add(_waterStrip);   // UI-02-6: полоска над картой (не поверх неё)

            void Btn(string id, string label, Action<Graphics, Rectangle, Color> icon)
            {
                var b = new NuRailButton(id, label, icon); b.Click += (s, e) => Press(id); _buttons.Add(b); _rail.Controls.Add(b);
            }
            Btn("tips", "Подсказки", NuIcons.Fish);
            Btn("journal", "Журнал поимок", NuIcons.Journal);
            Btn("ai", "Помощник: куда идти и ИИ", NuIcons.Spark);
            Btn("phone", "Телефон", NuIcons.Phone);
            Btn("hours", "Клёв по часам", NuIcons.Clock);
            Btn("stats", "Статистика", NuIcons.Stats);
            Btn("settings", "Настройки", NuIcons.Gear);
            // UI-02-6: «Карта любого водоёма» — карта выбранного водоёма со всеми уловами (все сессии).
            Btn("map", "Карта любого водоёма (со всеми уловами)", NuIcons.Map);
            _exit = new NuRailButton("exit", "Закрыть программу полностью (запись остановится)", NuIcons.Power) { Danger = true };
            _exit.Click += (s, e) => ExitApp?.Invoke();
            _rail.Controls.Add(_exit);
            _rail.Resize += (s, e) => PlaceExit(); _rail.Layout += (s, e) => PlaceExit();
            _rail.Paint += (s, e) => { using var pen = new Pen(NuTheme.Line); e.Graphics.DrawLine(pen, 0, 0, 0, _rail.Height); };

            Controls.Add(_work); Controls.Add(_side); Controls.Add(_rail); Controls.Add(_title);
            _clock.Tick += (s, e) => { _title.Invalidate(); if (MapView.Visible && Visible) MapView.Invalidate(); }; _clock.Start();
            FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing && HideToTray != null) { e.Cancel = true; HideToTray(); } };
            Sync();
        }

        private void PlaceExit()
        {
            // ⏻ sits at the very bottom of the rail
            if (_exit == null || _buttons.Count == 0) return;
            var last = _buttons[^1];
            int top = Math.Max(last.Bottom + 12, _rail.ClientSize.Height - _exit.Height - 12);
            _exit.Margin = new Padding(10, Math.Max(3, top - last.Bottom - last.Margin.Bottom), 10, 3);
        }

        /// <summary>Rail press: toggles; pressing again returns to the default view.</summary>
        public void Press(string id)
        {
            if (id == "ai") _ai1 = !_ai1;
            else
            {
                Mode = Mode == id ? "catches" : id;
                if (Mode is "settings" or "journal" or "stats") _ai1 = false;   // ui01_review3 #17: the assistant must not cover these pages
            }
            Sync();
        }

        /// <summary>Back to the map (e.g. «show on map» from the journal).</summary>
        public void ShowMap() { Mode = "catches"; Sync(); }

        /// <summary>UI-02-6: заполнить список водоёмов для режима «карта любого водоёма».</summary>
        public void SetWaterList(IEnumerable<(uint id, string name)> items)
        {
            _waterPick.BeginUpdate();
            _waterPick.Items.Clear();
            foreach (var it in items) _waterPick.Items.Add(new KeyValuePair<uint, string>(it.id, it.name));
            _waterPick.EndUpdate();
            if (_waterPick.Items.Count > 0 && _waterPick.SelectedIndex < 0) _waterPick.SelectedIndex = 0;
        }

        private bool Wide => Mode is "journal" or "stats" or "settings";

        private void Sync()
        {
            foreach (var b in _buttons) b.Pressed = b.Id == "ai" ? _ai1 : b.Id == Mode;
            _side.Visible = !Wide;
            _rods.Visible = !Wide;
            MapView.Visible = !Wide;
            bool st = Mode is "journal" or "stats";
            if (st) { Stats.SetMode(Mode == "journal"); Stats.Visible = true; Stats.BringToFront(); _ai.BringToFront(); }
            else Stats.Visible = false;
            if (Mode == "settings") { SettingsHost.Visible = true; SettingsHost.Ensure(); SettingsHost.BringToFront(); _ai.BringToFront(); }
            else SettingsHost.Visible = false;
            // side panel
            _side.Width = Mode == "phone" ? 560 : SideW;
            Catches.Visible = Mode == "catches"; TipsPanel.Visible = Mode == "tips"; Hours.Visible = Mode == "hours";
            PhoneHost.Visible = Mode == "phone"; if (Mode == "phone") PhoneHost.Ensure();
            _waterStrip.Visible = Mode == "map";   // UI-02-6: выбор водоёма только в режиме «карта любого водоёма»
            _ai.Visible = _ai1; if (_ai1) AiHost.Ensure(); PlaceAi();
            _side.Invalidate(); _map.Invalidate(); RodsStrip.Invalidate();
        }

        private void PlaceAi()
        {
            _ai.Size = new Size(Math.Max(360, Math.Min(760, _map.Width - 40)), Math.Max(300, _map.Height - 40));
            _ai.Location = new Point(Math.Max(8, _map.Width - _ai.Width - 20), 20); _ai.BringToFront();
        }

        // ------------------------------------------------------------------ title bar
        private void PaintTitle(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int h = _title.Height, w = _title.Width;
            using (var pen = new Pen(NuTheme.Line)) g.DrawLine(pen, 0, h - 1, w, h - 1);

            // «\» minimap toggle
            bool mini = MiniMapShown?.Invoke() ?? false;
            _btnMini = new Rectangle(8, 7, 52, 30);
            using (var path = NuTheme.Round(_btnMini, 8))
            {
                using var bg = new SolidBrush(mini ? NuTheme.PressedBg : (_hoverBtn == 3 ? NuTheme.Raised : NuTheme.Panel)); g.FillPath(bg, path);
                using var pen = new Pen(mini ? Color.FromArgb(0x3F, 0x2A, 0x18) : NuTheme.Line); g.DrawPath(pen, path);
            }
            var mc = mini ? NuTheme.Accent : NuTheme.Soft;
            NuIcons.Radar(g, new Rectangle(_btnMini.X + 8, _btnMini.Y + 6, 18, 18), mc);
            using (var f = NuTheme.F(9f, FontStyle.Bold)) { string mk = MiniKeyText?.Invoke() ?? "\\"; if (mk.Length <= 3) TextRenderer.DrawText(g, mk, f, new Point(_btnMini.X + 32, _btnMini.Y + 6), mc); }   // ui01_review3 #27

            int x = _btnMini.Right + 14;
            string water = WaterName?.Invoke() ?? ""; if (string.IsNullOrWhiteSpace(water)) water = "Водоём ещё не определён";
            var msk = NuTheme.MoscowNow(); var (game, window, left) = NuTheme.GameClock(msk);
            using (var f = NuTheme.F(10.5f, FontStyle.Bold))
            {
                TextRenderer.DrawText(g, water, f, new Point(x, 12), NuTheme.Text, TextFormatFlags.NoPadding);
                x += TextRenderer.MeasureText(g, water, f, Size.Empty, TextFormatFlags.NoPadding).Width + 12;
            }
            using (var f = NuTheme.F(9f))
            {
                string t = "в игре " + game + " · МСК " + msk.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                TextRenderer.DrawText(g, t, f, new Point(x, 14), NuTheme.Muted, TextFormatFlags.NoPadding);
                x += TextRenderer.MeasureText(g, t, f, Size.Empty, TextFormatFlags.NoPadding).Width + 14;
            }
            if (window != null)
            {
                string t = window + " · ещё " + (int)left.TotalMinutes + " мин";
                using var f = NuTheme.F(9f, FontStyle.Bold);
                int tw = TextRenderer.MeasureText(g, t, f, Size.Empty, TextFormatFlags.NoPadding).Width;
                var pill = new Rectangle(x, 10, tw + 34, 24);
                using (var path = NuTheme.Round(pill, 12)) { using var bg = new SolidBrush(NuTheme.AccentDeep); g.FillPath(bg, path); }
                using (var dot = new SolidBrush(NuTheme.Accent)) g.FillEllipse(dot, pill.X + 11, pill.Y + 9, 7, 7);
                TextRenderer.DrawText(g, t, f, new Point(pill.X + 24, pill.Y + 5), NuTheme.AccentText, TextFormatFlags.NoPadding);
            }

            // window buttons
            _btnClose = new Rectangle(w - 48, 0, 48, h); _btnMax = new Rectangle(w - 96, 0, 48, h); _btnMin = new Rectangle(w - 144, 0, 48, h);
            void Hover(Rectangle r, int i) { if (_hoverBtn == i) { using var b = new SolidBrush(i == 2 ? Color.FromArgb(0x5A, 0x22, 0x1E) : NuTheme.Raised); g.FillRectangle(b, r); } }
            Hover(_btnMin, 0); Hover(_btnMax, 1); Hover(_btnClose, 2);
            using (var pen = new Pen(NuTheme.Muted, 1.4f))
            {
                int cy = h / 2;
                g.DrawLine(pen, _btnMin.X + 18, cy, _btnMin.X + 30, cy);
                if (WindowState == FormWindowState.Maximized) { g.DrawRectangle(pen, _btnMax.X + 20, cy - 4, 9, 9); g.DrawLine(pen, _btnMax.X + 22, cy - 7, _btnMax.X + 32, cy - 7); g.DrawLine(pen, _btnMax.X + 32, cy - 7, _btnMax.X + 32, cy + 3); }
                else g.DrawRectangle(pen, _btnMax.X + 19, cy - 5, 10, 10);
                g.DrawLine(pen, _btnClose.X + 19, cy - 5, _btnClose.X + 29, cy + 5); g.DrawLine(pen, _btnClose.X + 29, cy - 5, _btnClose.X + 19, cy + 5);
            }

            // recording state, left of the window buttons
            bool rec = Recording?.Invoke() ?? false;
            using (var f = NuTheme.F(9f))
            {
                string t = rec ? "Запись идёт" : "Нет данных от игры";
                int tw = TextRenderer.MeasureText(g, t, f, Size.Empty, TextFormatFlags.NoPadding).Width;
                int rx = _btnMin.X - 16 - tw;
                using (var dot = new SolidBrush(rec ? NuTheme.Ok : NuTheme.Muted)) g.FillEllipse(dot, rx - 13, h / 2 - 3, 7, 7);
                TextRenderer.DrawText(g, t, f, new Point(rx, h / 2 - 8), NuTheme.Muted, TextFormatFlags.NoPadding);
            }
        }

        private int HitButton(Point p) => _btnMin.Contains(p) ? 0 : _btnMax.Contains(p) ? 1 : _btnClose.Contains(p) ? 2 : _btnMini.Contains(p) ? 3 : -1;
        private void TitleMove(object? s, MouseEventArgs e) { int i = HitButton(e.Location); if (i != _hoverBtn) { _hoverBtn = i; _title.Invalidate(); } _title.Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; }
        private void TitleDown(object? s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || HitButton(e.Location) >= 0) return;
            // drag by the title bar through the system: gives Aero Snap (edges, halves, maximize at the top)
            ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }
        private void TitleUp(object? s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            switch (HitButton(e.Location))
            {
                case 0: if (MinimizeToTray?.Invoke() == true && HideToTray != null) HideToTray(); else WindowState = FormWindowState.Minimized; break;   // ui01_review3 #15
                case 1: ToggleMax(); break;
                case 2: if (HideToTray != null) HideToTray(); else Close(); break;
                case 3: ToggleMiniMap?.Invoke(); _title.Invalidate(); break;
            }
        }
        private void ToggleMax() { WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; _title.Invalidate(); }

        // ------------------------------------------------------------------ placeholders (steps 2–4 fill these)
        private void PaintMap(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = _map.ClientRectangle;
            if (Wide)
            {
                g.Clear(NuTheme.Ground);
                string title = Mode == "journal" ? "Журнал поимок" : Mode == "stats" ? "Статистика" : "Настройки";
                string step = Mode == "settings" ? "Шаг 4. Пока настройки открываются в прежнем окне." : "Шаг 4. Пока открывается в прежнем окне.";
                DrawPlaceholder(g, r, title, step, true);
                return;
            }
            g.Clear(NuTheme.Ground);
        }

        private readonly Dictionary<string, Rectangle> _legacyLinks = new();
        private void DrawPlaceholder(Graphics g, Rectangle r, string title, string sub, bool withLink)
        {
            using var f1 = NuTheme.F(15f, FontStyle.Bold); using var f2 = NuTheme.F(10f);
            var s1 = TextRenderer.MeasureText(g, title, f1); var s2 = TextRenderer.MeasureText(g, sub, f2);
            int cy = r.Height / 2 - 30;
            TextRenderer.DrawText(g, title, f1, new Point(r.X + (r.Width - s1.Width) / 2, cy), NuTheme.Text);
            TextRenderer.DrawText(g, sub, f2, new Point(r.X + (r.Width - s2.Width) / 2, cy + s1.Height + 6), NuTheme.Soft);
            if (withLink && OpenLegacy != null)
            {
                string t = "Открыть в прежнем окне";
                var ts = TextRenderer.MeasureText(g, t, f2);
                var br = new Rectangle(r.X + (r.Width - ts.Width - 32) / 2, cy + s1.Height + s2.Height + 22, ts.Width + 32, 36);
                using (var path = NuTheme.Round(br, 10)) { using var pen = new Pen(NuTheme.Line2); using var bg = new SolidBrush(NuTheme.Raised); g.FillPath(bg, path); g.DrawPath(pen, path); }
                TextRenderer.DrawText(g, t, f2, br, NuTheme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                _legacyLinks[Mode] = br;
            }
        }
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _map.MouseClick += (s, ev) => { if (Wide && _legacyLinks.TryGetValue(Mode, out var br) && br.Contains(ev.Location)) OpenLegacy?.Invoke(Mode); };
            _side.MouseClick += (s, ev) => { if (_legacyLinks.TryGetValue("side:" + Mode, out var br) && br.Contains(ev.Location)) OpenLegacy?.Invoke(Mode); };
            PlaceExit();
        }

        private void PaintSide(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(NuTheme.Panel);
            using (var pen = new Pen(NuTheme.Line)) g.DrawLine(pen, 0, 0, 0, _side.Height);
            string title = Mode switch { "tips" => "Подсказки по рыбе", "phone" => "Телефон", "hours" => "Клёв по часам", _ => "Последние уловы" };
            string sub = Mode switch { "tips" => "шаг 4", "phone" => "шаг 4 · пока — прежнее окно телефона", "hours" => "шаг 4", _ => "на этом водоёме · шаг 3" };
            using var f1 = NuTheme.F(12f, FontStyle.Bold); using var f2 = NuTheme.F(9f);
            TextRenderer.DrawText(g, title, f1, new Point(16, 16), NuTheme.Text);
            TextRenderer.DrawText(g, sub, f2, new Point(16, 44), NuTheme.Muted);
            if (Mode == "hours")
            {   // already real: next bite windows by Moscow time
                var msk = NuTheme.MoscowNow(); var (_, window, left) = NuTheme.GameClock(msk);
                int y = 80;
                var box = new Rectangle(16, y, _side.Width - 32, 78);
                using (var path = NuTheme.Round(box, 12)) { using var bg = new SolidBrush(window != null ? NuTheme.AccentDeep : NuTheme.Raised); g.FillPath(bg, path); }
                using var fb = NuTheme.F(16f, FontStyle.Bold);
                TextRenderer.DrawText(g, window ?? "Сейчас обычный клёв", f2, new Point(30, y + 12), window != null ? NuTheme.AccentText : NuTheme.Muted);
                TextRenderer.DrawText(g, window != null ? "ещё " + left.ToString(@"mm\:ss") : "окна: чётный час :22:30, нечётный :37:30", window != null ? fb : f2, new Point(30, y + 34), NuTheme.Text);
            }
            if (Mode == "phone" && OpenLegacy != null)
            {
                var br = new Rectangle(16, 80, _side.Width - 32, 40);
                using (var path = NuTheme.Round(br, 10)) { using var pen = new Pen(NuTheme.Line2); using var bg = new SolidBrush(NuTheme.Raised); g.FillPath(bg, path); g.DrawPath(pen, path); }
                TextRenderer.DrawText(g, "Открыть окно телефона", f2, br, NuTheme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                _legacyLinks["side:phone"] = br;
            }
        }

        private void PaintRods(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(NuTheme.Line);
            int n = 3, gap = 1, w = (_rods.Width - gap * (n - 1)) / n;
            using var f1 = NuTheme.F(9f); using var f2 = NuTheme.F(10f, FontStyle.Bold);
            for (int i = 0; i < n; i++)
            {
                var r = new Rectangle(i * (w + gap), 1, w, _rods.Height - 1);
                using (var b = new SolidBrush(NuTheme.Panel)) g.FillRectangle(b, r);
                TextRenderer.DrawText(g, "Удочка " + (i + 1), f1, new Point(r.X + 16, r.Y + 14), NuTheme.Muted);
                TextRenderer.DrawText(g, "шаг 3: наживка, в воде, шанс, совет", f2, new Point(r.X + 16, r.Y + 36), NuTheme.Soft);
            }
        }

        private void PaintAi(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(NuTheme.Raised);
            using (var pen = new Pen(NuTheme.Line2)) g.DrawRectangle(pen, 0, 0, _ai.Width - 1, _ai.Height - 1);
            using (var b = new SolidBrush(NuTheme.Panel)) g.FillRectangle(b, 1, 1, _ai.Width - 2, 42);
            using var f1 = NuTheme.F(10.5f, FontStyle.Bold); using var f2 = NuTheme.F(9f);
            TextRenderer.DrawText(g, "Помощник — куда идти и ИИ", f1, new Point(14, 12), NuTheme.Text);
            using (var pen = new Pen(NuTheme.Muted, 1.5f)) { int cx = _ai.Width - 22, cy = 22; g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5); g.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5); }

        }

        // ------------------------------------------------------------------ borderless window with system snap/resize
        private const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_NCLBUTTONDOWN = 0x00A1, HTCLIENT = 1, HTCAPTION = 2,
            HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        private const int WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000, WS_SYSMENU = 0x00080000;
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.Style |= WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU; return cp; }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                // the whole window is client area; when maximized keep it inside the work area (Windows overhangs by the frame size)
                if (WindowState == FormWindowState.Maximized)
                {
                    var rc = Marshal.PtrToStructure<RECT>(m.LParam);
                    int fx = GetSystemMetrics(32) + GetSystemMetrics(92), fy = GetSystemMetrics(33) + GetSystemMetrics(92);   // SM_CXSIZEFRAME + SM_CXPADDEDBORDER
                    rc.L += fx; rc.R -= fx; rc.T += fy; rc.B -= fy;
                    Marshal.StructureToPtr(rc, m.LParam, false);
                }
                m.Result = IntPtr.Zero; return;
            }
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if (WindowState == FormWindowState.Maximized) { if ((int)m.Result != HTCLIENT) m.Result = (IntPtr)HTCLIENT; return; }
                var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
                const int B = 6; bool l = p.X < B, r = p.X >= ClientSize.Width - B, t = p.Y < B, b = p.Y >= ClientSize.Height - B;
                int hit = t && l ? HTTOPLEFT : t && r ? HTTOPRIGHT : b && l ? HTBOTTOMLEFT : b && r ? HTBOTTOMRIGHT : l ? HTLEFT : r ? HTRIGHT : t ? HTTOP : b ? HTBOTTOM : HTCLIENT;
                m.Result = (IntPtr)hit; return;
            }
            base.WndProc(ref m);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            var pad = WindowState == FormWindowState.Maximized ? new Padding(0) : new Padding(5);
            if (Padding != pad) Padding = pad;
            _title?.Invalidate(); Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (WindowState != FormWindowState.Maximized) { using var pen = new Pen(Color.FromArgb(0x24, 0x36, 0x3F)); e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1); }
        }
        protected override void Dispose(bool disposing) { if (disposing) _clock.Dispose(); base.Dispose(disposing); }
    }
}
