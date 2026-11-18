using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FisherLogger
{
    /// <summary>
    /// ADVISOR-01: единое окно «Настройки» — разделы слева, содержимое справа.
    /// Аналитика и ИИ-агент редактируются здесь (app_settings.json). Оверлей и телефон сохраняют свои
    /// проверенные окна настроек (горячая клавиша, сопряжение) — они открываются кнопкой из своего раздела.
    /// </summary>
    public sealed class SettingsForm : Form
    {
        private readonly AppSettings _work;
        private readonly Action<AppSettings> _apply;
        private readonly Func<string> _overlaySummary, _phoneSummary;
        private readonly Action _openOverlay, _openPhone;
        private readonly Dictionary<string, Control> _pages = new();
        internal readonly ListBox Sections = new() { Dock = DockStyle.Left, Width = 210, IntegralHeight = false, BorderStyle = BorderStyle.None, Font = Theme.UiBold, ItemHeight = 30, DrawMode = DrawMode.OwnerDrawFixed };
        private readonly Panel _host = new() { Dock = DockStyle.Fill, Padding = new Padding(16, 10, 16, 10), AutoScroll = true };

        // аналитика
        internal readonly NumericUpDown MinAttempts = Num(3, 1000), MinCatches = Num(1, 1000), MinLabeled = Num(50, 100000), MinPos = Num(10, 100000), MinNeg = Num(10, 100000),
                                        MinImprovePct = Num(1, 50), ForwardMin = Num(10, 10000), RetrainEvery = Num(5, 10000), CellM = Num(2, 50), GbRadius = Num(1, 50), GbWindow = Num(1, 240);
        internal readonly CheckBox AutoRetrain = new() { Text = "Самообучение: автоматически пересчитывать формулу при появлении новых данных", AutoSize = true };
        // агент
        internal readonly CheckBox AgentEnabled = new() { Text = "Включить ИИ-агента", AutoSize = true };
        internal readonly ComboBox Provider = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
        internal readonly TextBox BaseUrl = new() { Width = 340 }, Model = new() { Width = 260 }, ApiKey = new() { Width = 340, UseSystemPasswordChar = true };
        internal readonly Label KeyState = new() { AutoSize = true, ForeColor = Theme.TextMuted };
        internal readonly NumericUpDown MaxTools = Num(1, 20), Timeout = Num(10, 300);
        // COACH-01: экономия токенов и разбор сессии
        internal readonly NumericUpDown Budget = Num(2000, 2000000), MaxOut = Num(200, 4000), CoachInterval = Num(5, 240), CoachTokens = Num(100, 2000);
        internal readonly CheckBox CoachAuto = new() { Text = "Разбирать текущую рыбалку самому (в окне в игре, вкладка «Советы»), не чаще чем раз в N минут и только при изменениях", AutoSize = true, MaximumSize = new Size(600, 0) };
        internal readonly Label Usage = new() { AutoSize = true, ForeColor = Theme.TextMuted };
        internal readonly Label TestResult = new() { AutoSize = true, MaximumSize = new Size(580, 0), ForeColor = Theme.TextMuted };
        private bool _clearKey;

        internal System.Net.Http.HttpMessageHandler? AgentHandler = null;
        internal Task? LastTest { get; private set; }
        public AppSettings Result => _work;

        public SettingsForm(AppSettings current, Action<AppSettings> apply, Func<string> overlaySummary, Action openOverlay, Func<string> phoneSummary, Action openPhone, string? startSection = null)
        {
            _work = current.Clone(); _apply = apply;
            _overlaySummary = overlaySummary; _openOverlay = openOverlay; _phoneSummary = phoneSummary; _openPhone = openPhone;
            Text = "Настройки — FisherLogger";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(900, 640); MinimumSize = new Size(760, 520);
            Shown += (s0, e0) => Theme.ApplyInputs(this);   // DESIGN-01: тёмные поля ввода
            BackColor = Theme.Background; ForeColor = Theme.Text; Font = Theme.Ui;
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;

            _pages["Советник и аналитика"] = AnalyticsPage();
            _pages["Уведомления о поклёвке"] = NotifyPage();   // NOTIFY-02
            _pages["Окно программы и трей"] = WindowPage();     // NOTIFY-02
            _pages["ИИ-агент"] = AgentPage();
            _pages["Окно в игре"] = ExternalPage("Окно в игре: радар и советы", "Горячая клавиша, прозрачность, положение, режим карты, радиус.", overlaySummary, openOverlay, "Открыть настройки окна…");
            _pages["Телефон (Android)"] = ExternalPage("Android-компаньон", "Сервис только для чтения в домашней сети: включение, порт, сопряжение и отзыв устройств.", phoneSummary, openPhone, "Открыть настройки телефона…");
            _pages["Файлы и папки"] = PathsPage();

            Sections.BackColor = Theme.Surface; Sections.ForeColor = Theme.Text;
            Sections.Items.AddRange(_pages.Keys.ToArray<object>());
            Sections.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using var bg = new SolidBrush(sel ? Theme.Selection : Theme.Surface); e.Graphics.FillRectangle(bg, e.Bounds);
                UiText.DrawText(e.Graphics, Sections.Items[e.Index].ToString(), Theme.UiBold, new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            };
            Sections.SelectedIndexChanged += (s, e) => ShowPage(Sections.SelectedItem?.ToString());

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8), BackColor = Theme.Surface };
            var cancel = new Button { Text = "Отмена", Width = 110, Height = 34, DialogResult = DialogResult.Cancel };
            var save = new Button { Text = "Сохранить", Width = 130, Height = 34 };
            Theme.StyleButton(cancel, Theme.ButtonNeutral); Theme.StyleButton(save, Theme.ButtonPrimary);
            save.Click += (s, e) => { if (SaveAll()) { DialogResult = DialogResult.OK; Close(); } };
            buttons.Controls.Add(cancel); buttons.Controls.Add(save);
            CancelButton = cancel;

            Controls.Add(_host); Controls.Add(Sections); Controls.Add(buttons);
            LoadValues();
            int idx = startSection != null ? Sections.Items.IndexOf(startSection) : 0;
            Sections.SelectedIndex = idx >= 0 ? idx : 0;
        }

        // PRERELEASE-01: что показывать, когда уловов много
        internal readonly ComboBox ShowMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        internal readonly ComboBox NotifyClass = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        internal readonly NumericUpDown MaxRows = Num(50, 2000);
        private static NumericUpDown Num(int min, int max) => new() { Minimum = min, Maximum = max, Width = 100 };
        private static Label H(string t) => new() { Text = t, AutoSize = true, Font = Theme.Title, ForeColor = Theme.Text, Margin = new Padding(0, 0, 0, 6) };
        private static Label Muted(string t) => new() { Text = t, AutoSize = true, MaximumSize = new Size(600, 0), ForeColor = Theme.TextMuted, Margin = new Padding(0, 2, 0, 10) };

        private static TableLayoutPanel Table()
        {
            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 4, 0, 4) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250)); t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return t;
        }
        private static void RowTo(TableLayoutPanel t, string label, Control c)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 8, 0), MaximumSize = new Size(240, 0) });
            c.Margin = new Padding(0, 3, 0, 3); t.Controls.Add(c);
        }
        private static FlowLayoutPanel Page() => new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Top };

        private Control AnalyticsPage()
        {
            var p = Page();
            p.Controls.Add(H("Советник и аналитика"));
            p.Controls.Add(Muted("Советник даёт совет только при достаточных данных; иначе пишет «мало данных». Чем выше пороги — тем надёжнее и тем позже появятся советы."));
            var t = Table();
            RowTo(t, "Совет: минимум забросов с известным исходом в группе", MinAttempts);
            RowTo(t, "Совет: минимум поимок вида в группе", MinCatches);
            RowTo(t, "Радиус места (советник), м", CellM);
            RowTo(t, "Прикорм «рядом»: радиус, м", GbRadius);
            RowTo(t, "Прикорм «рядом»: не раньше чем за, мин", GbWindow);
            p.Controls.Add(t);
            p.Controls.Add(H("Когда уловов много"));
            p.Controls.Add(Muted("Скрываются только из списков. В статистике, советнике, формуле и местах на карте учитываются все уловы."));
            if (ShowMode.Items.Count == 0)
            {
                foreach (int m in new[] { -1, 0, 1, 2 }) ShowMode.Items.Add(DisplayFilter.ModeTitle(m) + (m == -1 ? " (меньше, когда уловов много)" : ""));
                foreach (var n in new[] { "о каждой поклёвке", "хорошие и крупнее", "крупные и трофеи", "только трофеи" }) NotifyClass.Items.Add(n);
            }
            var d = Table();
            RowTo(d, "История поимок: показывать", ShowMode);
            RowTo(d, "История поимок: не больше строк", MaxRows);
            p.Controls.Add(d);
            p.Controls.Add(H("Формула клёва"));
            p.Controls.Add(Muted("Формула подбирается на ваших забросах и принимается, только если на проверке предсказывает лучше простого среднего. Затем она проверяется на новых забросах и отключается, если перестала работать."));
            var f = Table();
            RowTo(f, "Минимум забросов с известным исходом", MinLabeled);
            RowTo(f, "Минимум поклёвок", MinPos);
            RowTo(f, "Минимум забросов без поклёвки", MinNeg);
            RowTo(f, "Должна быть лучше среднего минимум на, %", MinImprovePct);
            RowTo(f, "Проверка на новых данных: минимум забросов", ForwardMin);
            RowTo(f, "Пересчитывать после каждых N новых забросов", RetrainEvery);
            p.Controls.Add(f);
            p.Controls.Add(AutoRetrain);
            var reset = new Button { Text = "Вернуть значения по умолчанию", Width = 260, Height = 32, Margin = new Padding(0, 12, 0, 0) };
            Theme.StyleButton(reset, Theme.ButtonNeutral);
            reset.Click += (s, e) => { _work.Analytics = new AnalyticsSettings(); LoadValues(); };
            p.Controls.Add(reset);
            return p;
        }

        // ---------------- NOTIFY-02: уведомления о поклёвке ----------------
        internal readonly CheckBox NEnabled = new() { Text = "Показывать окно при поклёвке", AutoSize = true };
        internal readonly ComboBox NStyle = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        internal readonly NumericUpDown NDuration = Num(2, 60), NTrophyExtra = Num(0, 60), NOpacity = Num(30, 100), NScale = Num(70, 160), NOffset = Num(0, 600), NMaxSound = Num(1, 15);
        internal readonly ComboBox NPlacement = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        internal readonly CheckBox NGold = new() { Text = "Трофей: золотая рамка и свечение", AutoSize = true };
        internal readonly CheckBox NSound = new() { Text = "Звук при поклёвке", AutoSize = true };
        internal readonly TrackBar NVolume = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, Width = 300, AutoSize = false, Height = 32 };
        internal readonly ComboBox[] NSounds = Enumerable.Range(0, 4).Select(_ => new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 }).ToArray();
        internal readonly Label NArrangeState = new() { AutoSize = true, ForeColor = Theme.TextMuted };
        private const string SndDefault = "По умолчанию", SndNone = "Без звука";

        private void FillSounds(int cls, string? select)
        {
            var c = NSounds[cls]; c.Items.Clear();
            c.Items.Add(SndDefault + " — " + SoundBank.DefaultName(cls)); c.Items.Add(SndNone);
            foreach (var f in SoundBank.List()) c.Items.Add(f);
            int idx = select == null || select == "default" ? 0 : select == "none" ? 1 : c.Items.IndexOf(select);
            c.SelectedIndex = idx < 0 ? 0 : idx;
        }
        private static string SoundValue(ComboBox c) => c.SelectedIndex <= 0 ? "default" : c.SelectedIndex == 1 ? "none" : c.SelectedItem?.ToString() ?? "default";

        private Control NotifyPage()
        {
            var p = Page();
            p.Controls.Add(H("Уведомления о поклёвке"));
            p.Controls.Add(Muted("У каждой удочки своё окно: удочка 1 — слева, 2 — по центру, 3 — справа. Окна не забирают фокус у игры; щелчок по окну закрывает его. Новая поклёвка на той же удочке заменяет её окно."));
            p.Controls.Add(NEnabled);
            if (NStyle.Items.Count == 0)
            {
                NStyle.Items.AddRange(new object[] { "Овал (номер, рыба, название, вес)", "Капсула (низкая и широкая)", "Медальон (рыба в круге)" });
                NPlacement.Items.AddRange(new object[] { "Вверху экрана, в ряд", "Внизу экрана, в ряд", "Где расставил сам" });
            }
            if (NotifyClass.Items.Count == 0) foreach (var n in new[] { "о каждой поклёвке", "хорошие и крупнее", "крупные и трофеи", "только трофеи" }) NotifyClass.Items.Add(n);
            var t = Table();
            RowTo(t, "Вид окна", NStyle);
            RowTo(t, "Показывать", NotifyClass);
            RowTo(t, "Время показа, с", NDuration);
            RowTo(t, "Трофей — дольше на, с", NTrophyExtra);
            RowTo(t, "Непрозрачность, %", NOpacity);
            RowTo(t, "Размер, %", NScale);
            RowTo(t, "Где показывать", NPlacement);
            RowTo(t, "Отступ от края экрана, точек", NOffset);
            var arr = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            var bArr = new Button { Text = "Расставить окна мышью…", Width = 220, Height = 30 }; Theme.StyleButton(bArr, Theme.ButtonSecondary);
            bArr.Click += (s, e) =>
            {
                CollectNotify();
                BiteToastManager.Arrange(_work.Notify, () => { NPlacement.SelectedIndex = 2; NArrangeState.Text = "положение запомнено — нажмите «Сохранить»"; });
                NArrangeState.Text = "перетащите три окна на экране, затем «Сохранить»";
            };
            arr.Controls.Add(bArr); arr.Controls.Add(NArrangeState);
            RowTo(t, "", arr);
            p.Controls.Add(t);
            p.Controls.Add(NGold);
            p.Controls.Add(H("Звук"));
            p.Controls.Add(NSound);
            var s2 = Table();
            RowTo(s2, "Громкость", NVolume);
            RowTo(s2, "Звук не длиннее, с", NMaxSound);
            for (int i = 0; i < 4; i++)
            {
                int cls = i;
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
                var play = new Button { Text = "▶", Width = 36, Height = 28, Margin = new Padding(6, 0, 0, 0) }; Theme.StyleButton(play, Theme.ButtonNeutral);
                play.Click += (s, e) => MciAudio.Play(SoundBank.Resolve(cls, SoundValue(NSounds[cls])), NVolume.Value, (int)NMaxSound.Value * 1000);
                var load = new Button { Text = "Загрузить…", Width = 104, Height = 28, Margin = new Padding(6, 0, 0, 0) }; Theme.StyleButton(load, Theme.ButtonNeutral);
                load.Click += (s, e) =>
                {
                    using var dlg = new OpenFileDialog { Title = "Звук для класса «" + SoundBank.ClassNames[cls] + "»", Filter = "Звуки (*.wav;*.mp3;*.wma;*.m4a)|*.wav;*.mp3;*.wma;*.m4a" };
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    try
                    {
                        string name = SoundBank.Import(dlg.FileName);
                        var keep = Enumerable.Range(0, 4).Select(k => SoundValue(NSounds[k])).ToArray(); keep[cls] = name;
                        for (int k = 0; k < 4; k++) FillSounds(k, keep[k]);
                    }
                    catch (Exception ex) { NArrangeState.Text = "Не удалось загрузить: " + ex.Message; }
                };
                row.Controls.Add(NSounds[i]); row.Controls.Add(play); row.Controls.Add(load);
                RowTo(s2, "Класс «" + SoundBank.ClassNames[i] + "»", row);
            }
            p.Controls.Add(s2);
            p.Controls.Add(Muted("Звуки хранятся в папке " + SoundBank.Dir + ". «По умолчанию»: для обычного, хорошего и крупного — короткий «буль-дзинь», для трофея — ваш бобёр (если его нет — фанфары)."));
            var prev = new Button { Text = "Показать пример", Width = 200, Height = 34, Margin = new Padding(0, 8, 0, 0) }; Theme.StyleButton(prev, Theme.ButtonPrimary);
            prev.Click += (s, e) => { CollectNotify(); BiteToastManager.Preview(_work.Notify); };
            p.Controls.Add(prev);
            return p;
        }

        private void LoadNotify()
        {
            var n = _work.Notify ??= new NotifySettings(); n.Normalize();
            NEnabled.Checked = n.Enabled; NStyle.SelectedIndex = (int)n.Style; NDuration.Value = n.DurationSec; NTrophyExtra.Value = n.TrophyExtraSec;
            NOpacity.Value = (decimal)Math.Round(n.Opacity * 100); NScale.Value = (decimal)Math.Round(n.Scale * 100); NPlacement.SelectedIndex = (int)n.Placement;
            NOffset.Value = n.OffsetY; NGold.Checked = n.TrophyGold; NSound.Checked = n.SoundEnabled; NVolume.Value = n.Volume; NMaxSound.Value = n.MaxSoundSec;
            for (int i = 0; i < 4; i++) FillSounds(i, n.Sounds[i]);
        }

        private void CollectNotify()
        {
            var n = _work.Notify ??= new NotifySettings();
            n.Enabled = NEnabled.Checked; n.Style = (ToastStyle)Math.Max(0, NStyle.SelectedIndex); n.DurationSec = (int)NDuration.Value; n.TrophyExtraSec = (int)NTrophyExtra.Value;
            n.Opacity = (double)NOpacity.Value / 100.0; n.Scale = (double)NScale.Value / 100.0; n.Placement = (ToastPlacement)Math.Max(0, NPlacement.SelectedIndex);
            n.OffsetY = (int)NOffset.Value; n.TrophyGold = NGold.Checked; n.SoundEnabled = NSound.Checked; n.Volume = NVolume.Value; n.MaxSoundSec = (int)NMaxSound.Value;
            for (int i = 0; i < 4; i++) n.Sounds[i] = SoundValue(NSounds[i]);
            n.Normalize();
        }

        // ---------------- NOTIFY-02: окно программы, трей, запуск с игрой ----------------
        internal readonly CheckBox WTray = new() { Text = "Сворачивать в трей (значок у часов), запись продолжается", AutoSize = true };
        internal readonly CheckBox WHotkeyOn = new() { Text = "Клавиша открыть/спрятать окно программы", AutoSize = true };
        internal readonly ComboBox WKey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        internal readonly CheckBox WCtrl = new() { Text = "Ctrl", AutoSize = true }, WAlt = new() { Text = "Alt", AutoSize = true }, WShift = new() { Text = "Shift", AutoSize = true };
        internal readonly Label WKeyState = new() { AutoSize = true, ForeColor = Theme.TextMuted, MaximumSize = new Size(560, 0) };
        // GAMEMAP-01: клавиша карты водоёма на весь экран
        internal readonly CheckBox WMapOn = new() { Text = "Клавиша карты водоёма на весь экран (поверх игры)", AutoSize = true };
        internal readonly ComboBox WMapKey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        private static readonly Keys[] KeyChoices = { Keys.F12, Keys.F11, Keys.F10, Keys.F9, Keys.F8, Keys.F7, Keys.F6, Keys.F5, Keys.Oemplus, Keys.OemMinus, Keys.Pause, Keys.Scroll, Keys.Insert, Keys.Home, Keys.End, Keys.PageUp, Keys.PageDown };

        private Control WindowPage()
        {
            var p = Page();
            p.Controls.Add(H("Окно программы и трей"));
            p.Controls.Add(Muted("Клавиша показывает окно программы по центру экрана и делает его активным. Нажмите ещё раз — окно уйдёт в трей, а активной снова станет игра (игра не сворачивается). Клавиша окна в игре (радар) — отдельная, в её настройках."));
            p.Controls.Add(WTray);
            p.Controls.Add(WHotkeyOn);
            if (WKey.Items.Count == 0) foreach (var k in KeyChoices) WKey.Items.Add(TrayController.HotkeyText((int)k, 0));
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            row.Controls.Add(WKey); row.Controls.Add(WCtrl); row.Controls.Add(WAlt); row.Controls.Add(WShift);
            var t = Table(); RowTo(t, "Клавиша", row); p.Controls.Add(t);
            p.Controls.Add(WKeyState);
            p.Controls.Add(WMapOn);
            if (WMapKey.Items.Count == 0) foreach (var k in KeyChoices) WMapKey.Items.Add(TrayController.HotkeyText((int)k, 0));
            var tm = Table(); RowTo(tm, "Клавиша карты", WMapKey); p.Controls.Add(tm);
            p.Controls.Add(Muted("Карта откроется на весь экран поверх игры: ваши места с рыбой, глубины, точки других игроков. Нажмите на место — увидите, что там клюёт и на что; нажмите в воду — координаты и глубину. Закрыть — та же клавиша или Esc, игра снова станет активной."));
            p.Controls.Add(Muted("F12 Windows не даёт занять одной программе, поэтому FisherLogger только замечает нажатие: F12 дойдёт и до Steam, и Steam тоже сделает снимок экрана. Если это мешает — выберите другую клавишу (например, F11 или Pause) или смените клавишу снимков в Steam."));
            p.Controls.Add(H("Запуск вместе с игрой"));
            p.Controls.Add(Muted("Без автозагрузки Windows: Steam сам запустит FisherLogger (сразу в трей) при старте игры. Steam → Библиотека → theFisher Online → Свойства → Параметры запуска — вставьте строку:"));
            var line = new TextBox { Text = GameLaunch.SteamLine, ReadOnly = true, Width = 520, BorderStyle = BorderStyle.FixedSingle };
            var copy = new Button { Text = "Копировать строку", Width = 180, Height = 30, Margin = new Padding(0, 6, 0, 0) }; Theme.StyleButton(copy, Theme.ButtonSecondary);
            copy.Click += (s, e) => { try { GameLaunch.Ensure(); Clipboard.SetText(line.Text); copy.Text = "Скопировано"; } catch (Exception ex) { copy.Text = "Ошибка: " + ex.Message; } };
            p.Controls.Add(line); p.Controls.Add(copy);
            p.Controls.Add(Muted("Если FisherLogger уже запущен, второй раз он не запускается. Закрыть программу — правый щелчок по значку у часов → «Выход»."));
            p.VisibleChanged += (s, e) => { if (p.Visible) WKeyState.Text = (TrayController.Current?.HotkeyStatus ?? "") + (TrayController.Current is { } tc ? "; " + tc.MapHotkeyStatus : ""); };
            return p;
        }

        private void LoadWindow()
        {
            var w = _work.Window ??= new WindowSettings(); w.Normalize();
            WTray.Checked = w.MinimizeToTray; WHotkeyOn.Checked = w.HotkeyEnabled;
            int i = Array.IndexOf(KeyChoices, (Keys)w.HotkeyKey);
            if (i < 0) { WKey.Items.Add(TrayController.HotkeyText(w.HotkeyKey, 0)); i = WKey.Items.Count - 1; }
            WKey.SelectedIndex = i;
            WCtrl.Checked = (w.HotkeyModifiers & (int)Keys.Control) != 0; WAlt.Checked = (w.HotkeyModifiers & (int)Keys.Alt) != 0; WShift.Checked = (w.HotkeyModifiers & (int)Keys.Shift) != 0;
            WMapOn.Checked = w.MapHotkeyEnabled;
            int mi = Array.IndexOf(KeyChoices, (Keys)w.MapHotkeyKey);
            if (mi < 0) { WMapKey.Items.Add(TrayController.HotkeyText(w.MapHotkeyKey, 0)); mi = WMapKey.Items.Count - 1; }
            WMapKey.SelectedIndex = mi;
        }

        private void CollectWindow()
        {
            var w = _work.Window ??= new WindowSettings();
            w.MinimizeToTray = WTray.Checked; w.HotkeyEnabled = WHotkeyOn.Checked;
            if (WKey.SelectedIndex >= 0 && WKey.SelectedIndex < KeyChoices.Length) w.HotkeyKey = (int)KeyChoices[WKey.SelectedIndex];
            w.HotkeyModifiers = (WCtrl.Checked ? (int)Keys.Control : 0) | (WAlt.Checked ? (int)Keys.Alt : 0) | (WShift.Checked ? (int)Keys.Shift : 0);
            w.MapHotkeyEnabled = WMapOn.Checked;
            if (WMapKey.SelectedIndex >= 0 && WMapKey.SelectedIndex < KeyChoices.Length) w.MapHotkeyKey = (int)KeyChoices[WMapKey.SelectedIndex];
            w.Normalize();
        }

        private Control AgentPage()
        {
            var p = Page();
            p.Controls.Add(H("ИИ-агент"));
            p.Controls.Add(Muted("Агент отвечает на вопросы в окне «Советник» (вкладка «Спросить ИИ-агента»). Он вызывает функции программы только для чтения: сводка, советы по виду, виды водоёма, формула, последние забросы. " +
                                 "В запрос уходят только эти сводки — без логина и пароля игры, сырых пакетов и путей к файлам. Агент не управляет игрой и ничего не меняет. Запросы оплачиваются по тарифу выбранного сервиса."));
            p.Controls.Add(AgentEnabled);
            Provider.Items.AddRange(new object[] { "OpenAI-совместимый (DeepSeek и др.)", "Anthropic (Claude)" });
            var t = Table();
            RowTo(t, "Формат API", Provider);
            var presets = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(350, 0) };
            foreach (var (name, text) in new[] { ("deepseek", "DeepSeek"), ("openai", "OpenAI"), ("anthropic", "Anthropic"), ("openrouter", "OpenRouter"), ("local", "Локальный (Ollama)") })
            {
                var b = new Button { Text = text, AutoSize = true, Height = 28, Margin = new Padding(0, 0, 6, 4) };
                Theme.StyleButton(b, Theme.ButtonNeutral);
                b.Click += (s, e) =>
                {
                    var (url, model) = AgentSettings.Preset(name);
                    BaseUrl.Text = url; Model.Text = model;
                    Provider.SelectedIndex = name == "anthropic" ? 1 : 0;
                    if (model.Length == 0) Model.Focus();
                };
                presets.Controls.Add(b);
            }
            RowTo(t, "Заполнить для сервиса", presets);
            RowTo(t, "Адрес API", BaseUrl);
            RowTo(t, "Модель", Model);
            RowTo(t, "API-ключ (пусто — оставить прежний)", ApiKey);
            var keyRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            var del = new Button { Text = "Удалить ключ", Width = 130, Height = 28 }; Theme.StyleButton(del, Theme.ButtonWarn);
            del.Click += (s, e) => { _clearKey = true; ApiKey.Clear(); KeyState.Text = "ключ будет удалён при сохранении"; };
            keyRow.Controls.Add(del); keyRow.Controls.Add(KeyState);
            RowTo(t, "", keyRow);
            RowTo(t, "Максимум обращений агента к программе за вопрос", MaxTools);
            RowTo(t, "Ожидание ответа, с", Timeout);
            Budget.Increment = 5000; MaxOut.Increment = 100; CoachTokens.Increment = 50;
            RowTo(t, "Лимит токенов в сутки (все запросы)", Budget);
            RowTo(t, "Длина ответа в чате, токенов", MaxOut);
            RowTo(t, "Длина совета тренера, токенов", CoachTokens);
            RowTo(t, "Автосовет не чаще, мин", CoachInterval);
            p.Controls.Add(t);
            p.Controls.Add(CoachAuto);
            p.Controls.Add(Usage);
            p.VisibleChanged += (s, e) => { if (p.Visible) try { Usage.Text = AgentUsage.Load().Text(_work.Agent) + ". Совет тренера — один короткий запрос (≈1–2 тыс. токенов); повтор без изменений — из памяти, бесплатно."; } catch { } };
            p.Controls.Add(Muted("Ключ хранится только на этом компьютере в зашифрованном виде (Windows DPAPI, ваша учётная запись). Получить ключ можно в личном кабинете сервиса."));
            var test = new Button { Text = "Проверить подключение", Width = 220, Height = 34 };
            Theme.StyleButton(test, Theme.ButtonPrimary);
            test.Click += (s, e) => LastTest = TestAsync(test);
            p.Controls.Add(test); p.Controls.Add(TestResult);
            return p;
        }

        private Control ExternalPage(string title, string what, Func<string> summary, Action open, string btnText)
        {
            var p = Page();
            p.Controls.Add(H(title));
            p.Controls.Add(Muted(what));
            var sum = new Label { AutoSize = true, MaximumSize = new Size(600, 0), ForeColor = Theme.Text, Margin = new Padding(0, 4, 0, 12) };
            p.Controls.Add(sum);
            var b = new Button { Text = btnText, Width = 260, Height = 34 };
            Theme.StyleButton(b, Theme.ButtonSecondary);
            b.Click += (s, e) => { try { open(); } catch (Exception ex) { sum.Text = "Ошибка: " + ex.Message; return; } try { sum.Text = summary(); } catch { } };
            p.Controls.Add(b);
            p.VisibleChanged += (s, e) => { if (p.Visible) try { sum.Text = summary(); } catch (Exception ex) { sum.Text = "нет данных: " + ex.Message; } };
            return p;
        }

        private Control PathsPage()
        {
            var p = Page();
            p.Controls.Add(H("Файлы и папки"));
            p.Controls.Add(Muted("Только для справки. Пути задаются при установке программы."));
            var t = Table();
            foreach (var (n, v) in new[]
            {
                ("База уловов", AppPaths.CatchDb), ("Папка сессии", AppPaths.BaseDirectory), ("Журналы", AppPaths.LogsDirectory),
                ("Формулы советника", ModelRegistry.DefaultPath), ("Настройки (советник, агент)", AppSettings.FilePath),
                ("Настройки окна в игре", OverlaySettings.FilePath)
            })
                RowTo(t, n, new TextBox { Text = v, ReadOnly = true, Width = 340, BorderStyle = BorderStyle.FixedSingle });
            p.Controls.Add(t);
            return p;
        }

        private void ShowPage(string? name)
        {
            _host.Controls.Clear();
            if (name != null && _pages.TryGetValue(name, out var c)) { _host.Controls.Add(c); c.Visible = false; c.Visible = true; }
        }

        private void LoadValues()
        {
            var a = _work.Analytics; a.Normalize();
            MinAttempts.Value = a.MinAttemptsForAdvice; MinCatches.Value = a.MinCatchesForAdvice; MinLabeled.Value = a.MinLabeledForFormula;
            MinPos.Value = a.MinPositivesForFormula; MinNeg.Value = a.MinNegativesForFormula; MinImprovePct.Value = (decimal)Math.Round(a.MinImprovement * 100);
            ForwardMin.Value = a.ForwardTestMin; RetrainEvery.Value = a.RetrainEveryNewAttempts; CellM.Value = (decimal)Math.Round(a.PlaceRadiusM);
            GbRadius.Value = (decimal)Math.Round(a.GroundbaitRadiusM); GbWindow.Value = a.GroundbaitWindowMin; AutoRetrain.Checked = a.AutoRetrain;
            var ds = _work.Display ??= new DisplaySettings(); ds.Normalize();
            if (ShowMode.Items.Count > 0) { ShowMode.SelectedIndex = ds.Mode + 1; NotifyClass.SelectedIndex = ds.NotifyMinClass; }
            MaxRows.Value = ds.MaxRows;
            LoadNotify(); LoadWindow();   // NOTIFY-02
            var g = _work.Agent; g.Normalize();
            AgentEnabled.Checked = g.Enabled; Provider.SelectedIndex = g.Provider == AgentProvider.Anthropic ? 1 : 0;
            BaseUrl.Text = g.BaseUrl; Model.Text = g.Model; ApiKey.Clear();
            KeyState.Text = g.HasKey ? "ключ сохранён (зашифрован)" : "ключ не задан";
            MaxTools.Value = g.MaxToolCalls; Timeout.Value = g.TimeoutSeconds;
            Budget.Value = g.DailyTokenBudget; MaxOut.Value = g.MaxOutputTokens; CoachInterval.Value = g.CoachIntervalMin; CoachTokens.Value = g.CoachMaxTokens; CoachAuto.Checked = g.CoachAuto;
        }

        /// <summary>Собирает значения из полей в _work (без сохранения в файл).</summary>
        internal void Collect()
        {
            var a = _work.Analytics;
            a.MinAttemptsForAdvice = (int)MinAttempts.Value; a.MinCatchesForAdvice = (int)MinCatches.Value; a.MinLabeledForFormula = (int)MinLabeled.Value;
            a.MinPositivesForFormula = (int)MinPos.Value; a.MinNegativesForFormula = (int)MinNeg.Value; a.MinImprovement = (double)MinImprovePct.Value / 100.0;
            a.ForwardTestMin = (int)ForwardMin.Value; a.RetrainEveryNewAttempts = (int)RetrainEvery.Value; a.PlaceRadiusM = (double)CellM.Value;
            a.GroundbaitRadiusM = (double)GbRadius.Value; a.GroundbaitWindowMin = (int)GbWindow.Value; a.AutoRetrain = AutoRetrain.Checked;
            a.Normalize();
            var ds = _work.Display ??= new DisplaySettings();
            if (ShowMode.SelectedIndex >= 0) ds.Mode = ShowMode.SelectedIndex - 1;
            if (NotifyClass.SelectedIndex >= 0) ds.NotifyMinClass = NotifyClass.SelectedIndex;
            ds.MaxRows = (int)MaxRows.Value; ds.Normalize();
            CollectNotify(); CollectWindow();   // NOTIFY-02
            var g = _work.Agent;
            g.Enabled = AgentEnabled.Checked; g.Provider = Provider.SelectedIndex == 1 ? AgentProvider.Anthropic : AgentProvider.OpenAiCompatible;
            g.BaseUrl = BaseUrl.Text.Trim(); g.Model = Model.Text.Trim(); g.MaxToolCalls = (int)MaxTools.Value; g.TimeoutSeconds = (int)Timeout.Value;
            g.DailyTokenBudget = (int)Budget.Value; g.MaxOutputTokens = (int)MaxOut.Value; g.CoachIntervalMin = (int)CoachInterval.Value; g.CoachMaxTokens = (int)CoachTokens.Value; g.CoachAuto = CoachAuto.Checked;
            if (ApiKey.Text.Trim().Length > 0) { g.SetApiKey(ApiKey.Text); _clearKey = false; }
            else if (_clearKey) g.SetApiKey(null);
            g.Normalize();
        }

        internal bool SaveAll()
        {
            Collect();
            var g = _work.Agent;
            Uri? u = null;
            if (g.Enabled && (!Uri.TryCreate(g.BaseUrl, UriKind.Absolute, out u) || (u.Scheme != "https" && u.Scheme != "http")))
            {
                Sections.SelectedItem = "ИИ-агент"; TestResult.ForeColor = Theme.Danger; TestResult.Text = "Укажите адрес API, например https://api.deepseek.com";
                return false;
            }
            if (g.Enabled && u!.Scheme == "http" && !u.IsLoopback)
            {
                Sections.SelectedItem = "ИИ-агент"; TestResult.ForeColor = Theme.Danger; TestResult.Text = "Без шифрования (http://) разрешён только локальный сервер на этом компьютере — иначе ключ уйдёт открытым текстом.";
                return false;
            }
            try { _work.Save(); _apply(_work); ApiKey.Clear(); return true; }
            catch (Exception ex) { TestResult.ForeColor = Theme.Danger; TestResult.Text = "Не удалось сохранить: " + ex.Message; Sections.SelectedItem = "ИИ-агент"; return false; }
        }

        private async Task TestAsync(Button btn)
        {
            Collect();
            var g = _work.Agent; string? key = g.GetApiKey();
            if (key == null) { TestResult.ForeColor = Theme.Warning; TestResult.Text = "Введите API-ключ."; return; }
            btn.Enabled = false; TestResult.ForeColor = Theme.TextMuted; TestResult.Text = "Проверка…";
            try
            {
                var r = await new AgentClient(g, key, AgentHandler).AskAsync(Array.Empty<AgentTurn>(), "Проверка связи. Ответь одним словом: готов.", Array.Empty<AgentTool>());
                TestResult.ForeColor = r.Ok ? Theme.AccentGreen : Theme.Danger;
                TestResult.Text = r.Ok ? "Подключение работает. Ответ модели: " + (r.Text.Length > 120 ? r.Text[..120] + "…" : r.Text) : r.Error;
            }
            finally { if (!IsDisposed) btn.Enabled = true; }
        }
    }
}
