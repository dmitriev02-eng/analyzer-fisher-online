using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FisherLogger
{
    /// <summary>
    /// Управление оверлеем: глобальная горячая клавиша (RegisterHotKey — штатный механизм Windows, без хуков),
    /// показ/скрытие без активации, обновление данных (только чтение БД), сквозные щелчки и Ctrl, сохранение настроек.
    /// </summary>
    public sealed class OverlayManager : IDisposable
    {
        private const int WM_HOTKEY = 0x312, HotkeyId = 0xB135;
        private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
        private const int VK_CONTROL = 0x11;
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

        private sealed class HotkeyWindow : NativeWindow
        {
            public Action? Pressed;
            public HotkeyWindow() => CreateHandle(new CreateParams { Parent = new IntPtr(-3) });   // message-only window
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId) { Pressed?.Invoke(); return; }
                base.WndProc(ref m);
            }
        }

        public OverlaySettings Settings { get; }
        public OverlayForm Form { get; }
        public OverlayDataSource Data { get; }
        public bool HotkeyRegistered { get; private set; }
        public string HotkeyStatus { get; private set; } = "";
        public IntPtr HotkeyHandle => _hk.Handle;
        public int HotkeyId_ => HotkeyId;

        private readonly HotkeyWindow _hk = new();
        private readonly Func<(int? baseId, DateTime? since)> _location;
        private readonly Action<string>? _log;
        private readonly System.Windows.Forms.Timer _dataTimer = new() { Interval = 2000 };
        private readonly System.Windows.Forms.Timer _fastTimer = new() { Interval = 120 };
        private int _fastTicks;
        private bool _disposed;

        // COACH-01: вкладка «Советы»
        private readonly Func<string> _currentDb;
        private readonly Func<AgentSettings>? _agent;
        private readonly Func<AnalyticsSettings>? _analytics;
        private readonly Func<uint?, string>? _locName;
        private SoakCurve? _curve; private DateTime _curveAt = DateTime.MinValue; private bool _curveLoading;
        private DateTime _coachAt = DateTime.MinValue; private string _lastAutoFp = "";
        public CoachSnapshot? Coach => Form.Coach;

        public OverlayManager(PacketDeepParser? parser, Func<string> currentDb, Func<string> sessionDir,
                              Func<(int? baseId, DateTime? since)> location, Action<string>? log,
                              Func<AgentSettings>? agent = null, Func<AnalyticsSettings>? analytics = null, Func<uint?, string>? locName = null)
        {
            _location = location; _log = log;
            _currentDb = currentDb; _agent = agent; _analytics = analytics; _locName = locName;
            Settings = OverlaySettings.Load(log);
            Data = new OverlayDataSource(parser, currentDb, sessionDir);
            Form = new OverlayForm(Settings);
            if (parser != null) Form.PoseSource = () => parser.Track.Last;   // OVERLAY-03: живая позиция игрока (c2s 1006)
            Form.SettingsChanged += () => { Settings.Save(_log); _fastTicks = 0; };
            Form.HideRequested += Hide;
            Form.SettingsDialogRequested += () => ShowSettings(null);
            Form.RefreshRequested += () => RefreshData(force: true);
            Form.CoachAskRequested += () => _ = AskCoach(manual: true);
            Form.CoachShowRequested += () => { if (Form.CoachAiText.Length > 0) CoachPopup.ShowNear(Form, Form.CoachAiText, Form.CoachAiNote); };
            OverlayForm.SlowLog = log;
            _hk.Pressed += Toggle;
            _dataTimer.Tick += (s, e) => RefreshData(false);
            _fastTimer.Tick += (s, e) => FastTick();
        }

        /// <summary>Зарегистрировать горячую клавишу и восстановить окно по настройкам.</summary>
        public void Start()
        {
            Form.RestorePlacement();
            ApplyHotkey();
            if (Settings.Enabled && Settings.VisibleOnStart) Show();
        }

        public bool IsVisible => Form.Visible;

        public void Toggle() { if (Form.Visible) Hide(); else Show(); }

        public void Show()
        {
            if (!Settings.Enabled || _disposed) return;
            if (!Form.Visible)
            {
                Form.Show();                // ShowWithoutActivation = true -> SW_SHOWNOACTIVATE
                Form.KeepOnTop();
            }
            RefreshData(force: true);
            _dataTimer.Start(); _fastTimer.Start();
            Settings.VisibleOnStart = true; Settings.Save(_log);
        }

        public void Hide()
        {
            if (Form.Visible) Form.Hide();
            _dataTimer.Stop(); _fastTimer.Stop();
            Form.SetClickThroughActive(false);
            Settings.VisibleOnStart = false; Settings.Save(_log);
        }

        /// <summary>OVERLAY-04: чтение БД — в фоне (не в потоке окна), применение — в потоке окна. Тесты: RefreshSync = true.</summary>
        public bool RefreshSync { get; set; } = true;   // FisherLogger ставит false
        private bool _refreshing, _refreshAgain;

        public void RefreshData(bool force)
        {
            if (RefreshSync || !Form.IsHandleCreated)
            {
                try
                {
                    var (baseId, since) = _location();
                    uint? loc = baseId.HasValue && baseId.Value >= 0 ? (uint)baseId.Value : null;
                    Form.SetData(Data.Refresh(loc, since, force));
                }
                catch (Exception ex) { _log?.Invoke("[OVERLAY] ошибка обновления данных: " + ex.Message); }
                if (Settings.LogTab == 2) RefreshCoach(force);
                return;
            }
            if (_refreshing) { _refreshAgain |= force; return; }
            _refreshing = true;
            var (b, sn) = _location();
            uint? l = b.HasValue && b.Value >= 0 ? (uint)b.Value : null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Task.Run(() => Data.Refresh(l, sn, force)).ContinueWith(t =>
            {
                _refreshing = false;
                if (t.Exception != null) _log?.Invoke("[OVERLAY] ошибка обновления данных: " + t.Exception.InnerException?.Message);
                else if (!Form.IsDisposed) Form.SetData(t.Result);
                if (sw.ElapsedMilliseconds > 500) _log?.Invoke("[OVERLAY] обновление данных заняло " + sw.ElapsedMilliseconds + " мс (в фоне)");
                if (Settings.LogTab == 2) RefreshCoach(force);
                if (_refreshAgain) { _refreshAgain = false; RefreshData(true); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>COACH-01: снимок рыбалки (раз в 5 с; кривая по минуте в воде — в фоне раз в 10 мин), автосовет ИИ при изменениях.</summary>
        public void RefreshCoach(bool force)
        {
            try
            {
                if (!force && (DateTime.Now - _coachAt).TotalSeconds < 5) return;
                _coachAt = DateTime.Now;
                string db = _currentDb();
                if (!_curveLoading && (_curve == null || (DateTime.Now - _curveAt).TotalMinutes > 10))
                {
                    _curveLoading = true;
                    var aset = _analytics?.Invoke() ?? new AnalyticsSettings();
                    Task.Run(() => { try { return BiteAnalytics.Load(db, aset).Soak; } catch { return null; } })
                        .ContinueWith(t => { _curve = t.Result ?? _curve; _curveAt = DateTime.Now; _curveLoading = false; }, TaskScheduler.FromCurrentSynchronizationContext());
                }
                if (RefreshSync || !Form.IsHandleCreated) { ApplyCoach(SessionCoach.Build(db, _curve, DateTime.Now, _locName)); return; }
                if (_coachBuilding) return;
                _coachBuilding = true;   // OVERLAY-04: БД — в фоне
                var curve = _curve;
                Task.Run(() => SessionCoach.Build(db, curve, DateTime.Now, _locName)).ContinueWith(t =>
                {
                    _coachBuilding = false;
                    if (t.Exception != null) { _log?.Invoke("[COACH] " + t.Exception.InnerException?.Message); return; }
                    if (!Form.IsDisposed) ApplyCoach(t.Result);
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            catch (Exception ex) { _log?.Invoke("[COACH] " + ex.Message); }
        }

        private bool _coachBuilding;

        private void ApplyCoach(CoachSnapshot snap)
        {
            Form.Coach = snap;
            Form.Invalidate();
            var a = _agent?.Invoke();
            if (a != null && a.CoachAuto && snap.HasData && snap.Fingerprint != _lastAutoFp && !Form.CoachBusy)
            {
                _lastAutoFp = snap.Fingerprint;
                _ = AskCoach(manual: false);
            }
        }

        public async Task AskCoach(bool manual)
        {
            var a = _agent?.Invoke();
            if (a == null) { Form.CoachAiNote = "ИИ-агент недоступен"; Form.Invalidate(); return; }
            if (Form.CoachBusy) return;
            if (manual || Form.Coach == null) { try { Form.Coach = SessionCoach.Build(_currentDb(), _curve, DateTime.Now, _locName); } catch { } }
            var snap = Form.Coach;
            if (snap == null) return;
            Form.CoachBusy = true; Form.Invalidate();
            try
            {
                var r = await CoachAi.AskAsync(a, snap, _curve, manual);
                if (r.Ok) { Form.CoachAiText = r.Text; if (!Form.IsDisposed && Form.Visible) CoachPopup.ShowNear(Form, r.Text, r.Note); }   // OVERLAY-04: ответ — во всплывающем окне
                Form.CoachAiNote = r.Note;
                if (r.Ok && !r.FromCache) _log?.Invoke("[COACH] совет ИИ: " + r.Note);
            }
            catch (Exception ex) { Form.CoachAiNote = "Ошибка: " + ex.Message; }
            finally { Form.CoachBusy = false; if (!Form.IsDisposed) Form.Invalidate(); }
        }

        private void FastTick()
        {
            if (!Form.Visible) return;
            bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;   // state query only; the key is not intercepted
            Form.SetClickThroughActive(Settings.ClickThrough && !ctrl);
            if (_fastTicks % 4 == 0) Form.OnPoseTick();                          // OVERLAY-03: новая поза → следовать за игроком
            if (++_fastTicks % 16 == 0) { Form.KeepOnTop(); Form.Invalidate(); }   // ~2 s: stay on top (only if something is above), refresh "minutes ago" (map layer cached)
        }

        /// <summary>(Пере)регистрировать глобальную горячую клавишу по настройкам.</summary>
        public void ApplyHotkey()
        {
            UnregisterHotKey(_hk.Handle, HotkeyId);
            HotkeyRegistered = false;
            if (!Settings.Enabled || !Settings.HotkeyEnabled || Settings.HotkeyKey == 0)
            {
                HotkeyStatus = "горячая клавиша выключена";
                return;
            }
            uint mods = MOD_NOREPEAT;
            if ((Settings.HotkeyModifiers & (int)Keys.Control) != 0) mods |= MOD_CONTROL;
            if ((Settings.HotkeyModifiers & (int)Keys.Shift) != 0) mods |= MOD_SHIFT;
            if ((Settings.HotkeyModifiers & (int)Keys.Alt) != 0) mods |= MOD_ALT;
            HotkeyRegistered = RegisterHotKey(_hk.Handle, HotkeyId, mods, (uint)Settings.HotkeyKey);
            string text = OverlaySettings.HotkeyText(Settings.HotkeyKey, Settings.HotkeyModifiers);
            HotkeyStatus = HotkeyRegistered ? "горячая клавиша " + text + " активна"
                                            : "клавиша " + text + " занята другой программой (код " + Marshal.GetLastWin32Error() + ")";
            _log?.Invoke("[OVERLAY] " + HotkeyStatus);
        }

        /// <summary>Применить изменённые настройки (окно настроек).</summary>
        public void ApplySettings()
        {
            Settings.Normalize();
            Form.ApplyOpacity();
            Form.Size = new Size(Settings.Width, Settings.Height);
            if (Settings.Preset != OverlayPreset.Custom) Form.ApplyPreset(Settings.Preset);
            ApplyHotkey();
            if (!Settings.Enabled) Hide();
            Form.Invalidate();
            Settings.Save(_log);
        }

        public void ShowSettings(IWin32Window? owner)
        {
            using var dlg = new OverlaySettingsForm(Settings.Clone(), this);
            if ((owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog()) == DialogResult.OK)
            {
                var n = dlg.Result;
                Settings.Enabled = n.Enabled; Settings.HotkeyEnabled = n.HotkeyEnabled; Settings.HotkeyKey = n.HotkeyKey; Settings.HotkeyModifiers = n.HotkeyModifiers;
                Settings.Opacity = n.Opacity; Settings.Preset = n.Preset; Settings.Mode = n.Mode; Settings.RadiusMeters = n.RadiusMeters;
                Settings.ClickThrough = n.ClickThrough; Settings.RecentHours = n.RecentHours;
                Settings.MinDisplayWeightG = n.MinDisplayWeightG;   // NEXT-OVERLAY-02
                ApplySettings();
                if (dlg.ShowAfterClose) Show();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (Form.Visible) { Settings.X = Form.Left; Settings.Y = Form.Top; Settings.Width = Form.Width; Settings.Height = Form.Height; }
                Settings.Save(_log);
            }
            catch { }
            _dataTimer.Stop(); _fastTimer.Stop(); _dataTimer.Dispose(); _fastTimer.Dispose();
            UnregisterHotKey(_hk.Handle, HotkeyId);
            _hk.DestroyHandle();
            Form.Dispose();
        }
    }

    /// <summary>Компактное окно настроек оверлея (открывается из FisherLogger).</summary>
    public sealed class OverlaySettingsForm : Form
    {
        public OverlaySettings Result { get; }
        public bool ShowAfterClose { get; private set; }
        private readonly TextBox _hotkey = new() { ReadOnly = true, Width = 150 };

        public OverlaySettingsForm(OverlaySettings s, OverlayManager mgr)
        {
            Result = s;
            Text = "Окно в игре — настройки";
            Shown += (s0, e0) => Theme.ApplyInputs(this);   // DESIGN-01
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Background; ForeColor = Theme.Text; Font = Theme.Ui;
            ClientSize = new Size(440, 540);
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;

            var p = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12), AutoSize = false };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170)); p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Label L(string t) => new() { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.Text, Margin = new Padding(0, 7, 0, 0) };
            ComboBox C(params string[] items) { var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, BackColor = Theme.SurfaceAlt, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat }; c.Items.AddRange(items); return c; }

            var enabled = new CheckBox { Text = "Оверлей включён", Checked = s.Enabled, AutoSize = true };
            var hkOn = new CheckBox { Text = "Глобальная горячая клавиша", Checked = s.HotkeyEnabled, AutoSize = true };
            int key = s.HotkeyKey, mods = s.HotkeyModifiers;
            _hotkey.Text = OverlaySettings.HotkeyText(key, mods);
            _hotkey.KeyDown += (o, e) =>
            {
                e.SuppressKeyPress = true; e.Handled = true;
                if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;
                key = (int)e.KeyCode; mods = (int)(e.Modifiers & (Keys.Control | Keys.Shift | Keys.Alt));
                _hotkey.Text = OverlaySettings.HotkeyText(key, mods);
            };
            var hkDefault = new Button { Text = "«\\» по умолч.", Width = 110, Height = 28 };
            Theme.StyleButton(hkDefault, Theme.ButtonNeutral);
            hkDefault.Click += (o, e) => { key = (int)Keys.OemPipe; mods = 0; _hotkey.Text = OverlaySettings.HotkeyText(key, mods); };
            var hkRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) }; hkRow.Controls.Add(_hotkey); hkRow.Controls.Add(hkDefault);

            var op = new TrackBar { Minimum = 30, Maximum = 100, TickFrequency = 10, Value = (int)Math.Round(s.Opacity * 100), Width = 200 };
            var opL = new Label { AutoSize = true, Text = op.Value + " %", Margin = new Padding(0, 7, 0, 0) };
            op.ValueChanged += (o, e) => opL.Text = op.Value + " %";
            var opRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) }; opRow.Controls.Add(op); opRow.Controls.Add(opL);
            var preset = C("Как сейчас (свободно)", "Слева вверху", "Справа вверху", "Слева внизу", "Справа внизу"); preset.SelectedIndex = (int)s.Preset;
            var mode = C("Активность (уловы)", "Поклёвки (события)"); mode.SelectedIndex = (int)s.Mode;
            var radius = C("50 м", "100 м", "200 м"); radius.SelectedIndex = s.RadiusMeters <= 75 ? 0 : s.RadiusMeters <= 150 ? 1 : 2;
            var ct = new CheckBox { Text = "Сквозные щелчки (удерживайте Ctrl, чтобы управлять окном)", Checked = s.ClickThrough, AutoSize = true, MaximumSize = new Size(250, 0) };
            var period = C("Всё время", "30 дней", "7 дней", "24 часа", "1 час"); int[] hours = { 0, 720, 168, 24, 1 };
            period.SelectedIndex = Math.Max(0, Array.IndexOf(hours, s.RecentHours));
            // NEXT-OVERLAY-02: минимальный вес для показа (на запись уловов не влияет)
            var minW = C("Все (0 г)", "от 10 г", "от 25 г", "от 50 г", "от 100 г", "Своё значение");
            var minWNum = new NumericUpDown { Minimum = 0, Maximum = CatchRules.MaxRecordWeightG, Width = 80, BackColor = Theme.SurfaceAlt, ForeColor = Theme.Text,
                Value = Math.Clamp(s.MinDisplayWeightG, 0, CatchRules.MaxRecordWeightG) };
            int presetIdx = Array.IndexOf(OverlaySettings.DisplayWeightPresets, s.MinDisplayWeightG);
            minW.SelectedIndex = presetIdx >= 0 ? presetIdx : OverlaySettings.DisplayWeightPresets.Length;
            minW.Width = 120;
            minWNum.Enabled = minW.SelectedIndex == OverlaySettings.DisplayWeightPresets.Length;
            minW.SelectedIndexChanged += (o, e) =>
            {
                bool custom = minW.SelectedIndex == OverlaySettings.DisplayWeightPresets.Length;
                minWNum.Enabled = custom;
                if (!custom) minWNum.Value = OverlaySettings.DisplayWeightPresets[minW.SelectedIndex];
            };
            var minWRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) }; minWRow.Controls.Add(minW); minWRow.Controls.Add(minWNum);
            minWRow.Controls.Add(new Label { AutoSize = true, Text = "г", Margin = new Padding(0, 7, 0, 0) });
            var status = new Label { AutoSize = true, MaximumSize = new Size(400, 0), ForeColor = mgr.HotkeyRegistered ? Theme.TextMuted : Theme.Warning,
                Text = "Состояние: " + mgr.HotkeyStatus + ".\r\nКлавиша, пока активна, перехватывается системой и не вводится в другие программы.\r\n" +
                       "Поверх игры в эксклюзивном полноэкранном режиме окно может не отображаться — используйте оконный или «оконный без рамки»." };

            p.Controls.Add(enabled, 0, 0); p.SetColumnSpan(enabled, 2);
            p.Controls.Add(hkOn, 0, 1); p.SetColumnSpan(hkOn, 2);
            p.Controls.Add(L("Горячая клавиша"), 0, 2); p.Controls.Add(hkRow, 1, 2);
            p.Controls.Add(L("Прозрачность"), 0, 3); p.Controls.Add(opRow, 1, 3);
            p.Controls.Add(L("Положение"), 0, 4); p.Controls.Add(preset, 1, 4);
            p.Controls.Add(L("Режим карты"), 0, 5); p.Controls.Add(mode, 1, 5);
            p.Controls.Add(L("Радиус карты"), 0, 6); p.Controls.Add(radius, 1, 6);
            p.Controls.Add(L("Период активности"), 0, 7); p.Controls.Add(period, 1, 7);
            p.Controls.Add(L("Мин. вес (только показ)"), 0, 8); p.Controls.Add(minWRow, 1, 8);
            p.Controls.Add(ct, 0, 9); p.SetColumnSpan(ct, 2);
            p.Controls.Add(status, 0, 10); p.SetColumnSpan(status, 2);

            var ok = new Button { Text = "Сохранить", Width = 110, Height = 32, DialogResult = DialogResult.OK };
            var okShow = new Button { Text = "Сохранить и показать", Width = 170, Height = 32, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Отмена", Width = 90, Height = 32, DialogResult = DialogResult.Cancel };
            Theme.StyleButton(ok, Theme.ButtonPrimary); Theme.StyleButton(okShow, Theme.ButtonSecondary); Theme.StyleButton(cancel, Theme.ButtonNeutral);
            okShow.Click += (o, e) => ShowAfterClose = true;
            var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 46, Padding = new Padding(8) };
            btns.Controls.Add(cancel); btns.Controls.Add(ok); btns.Controls.Add(okShow);
            Controls.Add(p); Controls.Add(btns);
            AcceptButton = ok; CancelButton = cancel;

            FormClosing += (o, e) =>
            {
                if (DialogResult != DialogResult.OK) return;
                Result.Enabled = enabled.Checked; Result.HotkeyEnabled = hkOn.Checked; Result.HotkeyKey = key; Result.HotkeyModifiers = mods;
                Result.Opacity = op.Value / 100.0; Result.Preset = (OverlayPreset)preset.SelectedIndex; Result.Mode = (OverlayMapMode)mode.SelectedIndex;
                Result.RadiusMeters = new[] { 50.0, 100, 200 }[radius.SelectedIndex]; Result.ClickThrough = ct.Checked; Result.RecentHours = hours[period.SelectedIndex];
                Result.MinDisplayWeightG = minW.SelectedIndex < OverlaySettings.DisplayWeightPresets.Length
                    ? OverlaySettings.DisplayWeightPresets[minW.SelectedIndex] : (int)minWNum.Value;
            };
        }
    }
}
