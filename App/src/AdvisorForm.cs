using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FisherLogger
{
    /// <summary>ADVISOR-01: окно «Советник» — советы по виду, карта видов, формула клёва, вопросы ИИ-агенту.</summary>
    public sealed class AdvisorForm : Form
    {
        private readonly string _dbPath;
        private readonly Func<AppSettings> _settings;
        private readonly Func<uint, string> _fishName;
        private readonly Func<uint?, string> _locName;
        private readonly Action<uint?>? _openMap;
        private readonly Action? _openSettings;
        private readonly Action<string>? _log;
        private readonly string _registryPath;

        private BiteAnalytics _an;
        private ModelRegistry _reg;
        private readonly ReferenceProvider _refProvider;

        private readonly Label _overview = new() { Dock = DockStyle.Top, Height = 64, Padding = new Padding(10, 8, 10, 4), ForeColor = Theme.Text, Font = Theme.Ui };
        private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
        private readonly FlowLayoutPanel _nav = new() { Dock = DockStyle.Top, Height = 84, Padding = new Padding(12, 4, 12, 6), BackColor = Theme.Background, WrapContents = true };
        // вкладка «Где и на что»
        internal readonly ComboBox SpeciesBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        internal readonly DataGridView AdviceGrid = new() { Dock = DockStyle.Fill };
        // FIX-03: текстовое поле с прокруткой — сюда помещается и справочный блок, когда своих данных мало
        internal readonly TextBox AdviceNotes = new() { Dock = DockStyle.Bottom, Height = 190, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, ForeColor = Theme.TextMuted };
        // вкладка «Карта видов»
        internal readonly ComboBox WaterBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        internal readonly DataGridView PresenceGrid = new() { Dock = DockStyle.Fill };
        // вкладка «Формула клёва»
        internal readonly TextBox FormulaText = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono };
        // вкладка «Спросить ИИ»
        internal readonly TextBox ChatLog = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        internal readonly TextBox ChatInput = new() { Dock = DockStyle.Fill, Multiline = true };
        internal readonly Button ChatSend = new() { Text = "Спросить", Width = 120, Dock = DockStyle.Right };
        internal readonly Label ChatState = new() { Dock = DockStyle.Top, Height = 40, ForeColor = Theme.TextMuted, Padding = new Padding(4) };
        // ADVISOR-UX-01: вкладка «Рыба» — поиск вида и карточка ответа «где / на что / когда»
        internal readonly SpeciesPicker Picker = new() { Dock = DockStyle.Left, Width = 300 };
        internal readonly FlowLayoutPanel Card = UiKit.Stack(new Padding(18, 12, 18, 12));
        private readonly PictureBox _cardIcon = new() { Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(0, 4) };
        private readonly Label _cardName = new() { AutoSize = false, Location = new Point(76, 2), Height = 34, Font = Theme.Title, ForeColor = Theme.Text };
        private readonly Label _cardSub = new() { AutoSize = false, Location = new Point(78, 40), Height = 24, ForeColor = Theme.TextMuted };
        internal readonly Label CardStatus = UiKit.Pill("", Theme.AccentGreen);
        internal readonly StatList Places = new() { EmptyText = "Своих поимок этого вида с координатами пока нет" };
        internal readonly StatList Waters = new() { EmptyText = "Нет данных по водоёмам" };
        internal readonly StatList Baits = new() { EmptyText = "Своих данных о наживке пока нет" };
        /// <summary>UIFIX-02: рецепт прикормки прямо в карточке рыбы (подробнее — «Цель», раздел 3).</summary>
        internal readonly StatList Recipes = new() { EmptyText = "Рецепт рассчитать не из чего" };
        private readonly Label _recipeNote = UiKit.Note("");
        private readonly Label _hookNote = UiKit.Note("");   // HINTS-01: крючок под границу крупная/трофейная
        internal readonly StatList Times = new() { EmptyText = "Нет данных о времени" };   // не показывается (BRIDGE-FB-01), оставлен для тестов
        // BRIDGE-FB-01: «Когда» — схема по 24 игровым часам, свои и чужие уловы
        internal readonly WhenChart When = new();
        internal readonly StatList RefPlaces = new() { EmptyText = "В справочнике этого вида нет" };
        internal readonly StatList RefBaits = new() { EmptyText = "—" };
        private readonly Label _refNote = UiKit.Note("");
        // HOURS-01: вкладка «По часам» — водоём, рыба, период; следует за выбранной рыбой
        internal HourView Hours { get; private set; } = null!;
        // MASTER-01: вкладка «Цель» — план «как поймать нужную рыбу» и подсказка во время рыбалки
        internal GoalView GoalTab { get; private set; } = null!;
        private readonly List<Button> _expertButtons = new();
        private readonly List<AgentTurn> _history = new();
        private CancellationTokenSource? _cts;

        /// <summary>Для тестов: подменить транспорт агента.</summary>
        internal System.Net.Http.HttpMessageHandler? AgentHandler = null;
        internal Task? LastAsk { get; private set; }

        public AdvisorForm(string dbPath, Func<AppSettings> settings, Func<uint, string> fishName, Func<uint?, string> locName,
                           Action<uint?>? openMap = null, Action? openSettings = null, Action<string>? log = null, string? registryPath = null)
        {
            _dbPath = dbPath; _settings = settings; _fishName = fishName; _locName = locName; _openMap = openMap; _openSettings = openSettings; _log = log;
            _registryPath = registryPath ?? ModelRegistry.DefaultPath;
            Text = "Советник — что, где и на что клюёт (FisherLogger)";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1100, 720); MinimumSize = new Size(820, 520);   UiKit.FitToScreen(this);   // FEAT-08b
            Shown += (s0, e0) => Theme.ApplyInputs(this);   // DESIGN-01: тёмные поля ввода
            BackColor = Theme.Background; ForeColor = Theme.Text; Font = Theme.Ui;
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;

            _an = new BiteAnalytics(settings().Analytics); _reg = new ModelRegistry();
            _refProvider = new ReferenceProvider();
            BuildUi();
            Controls.Add(_tabs); Controls.Add(_nav); Controls.Add(_overview);
            Theme.ApplyTree(this);
            // ADVISOR-UX-01: данные грузятся в OnLoad — после инициализатора объекта (BaitName = …). Раньше первая загрузка
            // шла в конструкторе без BaitName, поэтому наживки показывались как «id 1010».
        }

        private bool _loaded;
        private string _stamp = "";
        private readonly System.Windows.Forms.Timer _watch = new() { Interval = 15000 };
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (!_loaded)
            {
                _loaded = true; ReloadData(learn: true);
                // MASTER-01: окно обновляется само, когда в базе появляются новые забросы и уловы (подсказка «Сейчас»)
                _stamp = LiveWatch.Stamp(_dbPath);
                _watch.Tick += (s, e2) =>
                {
                    try
                    {
                        string st = LiveWatch.Stamp(_dbPath);
                        if (st.Length > 0 && st != _stamp) { _stamp = st; ReloadInBackground(); }   // AUDIT-04 (B1): БД и расчёт — в фоне
                        else GoalTab.Recalc(false);          // время идёт: «сейчас вы не ловите» появляется без новых данных
                    }
                    catch (Exception ex) { _log?.Invoke("[ADVISOR] автообновление: " + ex.Message); }
                };
                _watch.Start();
            }
        }

        private static Button Btn(string t, int w, Color c) { var b = new Button { Text = t, Width = w, Height = 32, Margin = new Padding(6, 2, 0, 2) }; Theme.StyleButton(b, c); return b; }
        private static FlowLayoutPanel Bar() => new() { Dock = DockStyle.Top, Height = 80, Padding = new Padding(6, 4, 6, 2), BackColor = Theme.Surface, WrapContents = true };
        private static void Grid(DataGridView g)
        {
            g.ReadOnly = true; g.AllowUserToAddRows = false; g.AllowUserToDeleteRows = false; g.RowHeadersVisible = false;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect; g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            Theme.StyleGrid(g);
        }

        private void BuildUi()
        {
            var t0 = new TabPage("Рыба") { BackColor = Theme.Surface };
            BuildFishCard();
            t0.Controls.Add(Card); t0.Controls.Add(Picker);
            Card.Dock = DockStyle.Fill;
            Picker.Selected += id => SelectFish(id);

            var t1 = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };
            var b1 = Bar(); b1.Controls.Add(new Label { Text = "Вид:", AutoSize = true, Margin = new Padding(0, 9, 4, 0) }); b1.Controls.Add(SpeciesBox);
            var mapBtn = Btn("Показать на карте", 170, Theme.ButtonMap);
            mapBtn.Click += (s, e) => _openMap?.Invoke((AdviceGrid.CurrentRow?.Tag as EvidenceRow)?.LocationId);
            b1.Controls.Add(mapBtn);
            Grid(AdviceGrid);
            foreach (var (n, h, w) in new[] { ("group", "Что", 90), ("item", "Вариант", 300), ("n", "Забросов", 70), ("k", "Поимок", 60), ("verdict", "Вывод", 300), ("level", "Надёжность", 180) })
                AdviceGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = n, HeaderText = h, FillWeight = w });
            t1.Controls.Add(AdviceGrid); t1.Controls.Add(AdviceNotes); t1.Controls.Add(b1);
            SpeciesBox.SelectedIndexChanged += (s, e) =>
            {
                ShowAdvice();
                var cur = SpeciesBox.SelectedItem as SpeciesItem;
                ShowCard(cur?.Id);
                if (cur != null) Picker.SelectId(cur.Id);
                if (_loaded) Hours.SetFish(cur?.Id);   // HOURS-01: «По часам» показывает выбранную рыбу
            };

            var t2 = new TabPage("Водоёмы") { BackColor = Theme.Background };
            var b2 = Bar(); b2.Controls.Add(new Label { Text = "Водоём:", AutoSize = true, Margin = new Padding(0, 9, 4, 0) }); b2.Controls.Add(WaterBox);
            var map2 = Btn("Открыть карту водоёма", 200, Theme.ButtonMap);
            map2.Click += (s, e) => _openMap?.Invoke((WaterBox.SelectedItem as WaterItem)?.Id);
            b2.Controls.Add(map2);
            Grid(PresenceGrid);
            foreach (var (n, h, w) in new[] { ("water", "Водоём", 220), ("fish", "Вид", 180), ("own", "Мои поимки", 90), ("pub", "Объявления других", 120), ("cls", "Макс. класс", 90), ("zones", "Участков с поимками", 120), ("hab", "Вид здесь водится (по каким данным)", 200) })
                PresenceGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = n, HeaderText = h, FillWeight = w });
            var note2 = new Label { Dock = DockStyle.Bottom, Height = 44, ForeColor = Theme.TextMuted, Padding = new Padding(6),
                Text = "«Мои поимки» — подтверждённые уловы. «Объявления других» — сколько раз игра объявляла поимку вида, пока вы были на водоёме: это активность, а не вероятность клёва." };
            t2.Controls.Add(PresenceGrid); t2.Controls.Add(note2); t2.Controls.Add(b2);
            WaterBox.SelectedIndexChanged += (s, e) => ShowPresence();

            var t3 = new TabPage("Подробно") { BackColor = Theme.Background };
            var b3 = Bar(); var re = Btn("Пересчитать формулу", 190, Theme.ButtonPrimary);
            re.Click += (s, e) => { ReloadData(learn: true, force: true); _tabs.SelectedIndex = 2; };
            b3.Controls.Add(re);
            b3.Controls.Add(new Label { AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(10, 9, 0, 0), Text = "Для аналитика: все группы, интервалы и состояние формулы клёва" });
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 330, BackColor = Theme.Border };
            split.Panel1.Controls.Add(t1);
            var formulaHost = new Panel { Dock = DockStyle.Fill };
            formulaHost.Controls.Add(FormulaText);
            split.Panel2.Controls.Add(formulaHost);
            t3.Controls.Add(split); t3.Controls.Add(b3);

            var t4 = new TabPage("Спросить ИИ-агента") { BackColor = Theme.Background };
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 80, Padding = new Padding(6) };
            bottom.Controls.Add(ChatInput); bottom.Controls.Add(ChatSend);
            Theme.StyleButton(ChatSend, Theme.ButtonPrimary);
            var b4 = Bar(); var setBtn = Btn("Настройки агента…", 170, Theme.ButtonNeutral); setBtn.Click += (s, e) => { _openSettings?.Invoke(); UpdateChatState(); };
            var clr = Btn("Очистить диалог", 150, Theme.ButtonNeutral); clr.Click += (s, e) => { _history.Clear(); ChatLog.Clear(); };
            b4.Controls.Add(setBtn); b4.Controls.Add(clr);
            ChatLog.BackColor = Theme.Console; ChatLog.ForeColor = Theme.Text;
            t4.Controls.Add(ChatLog); t4.Controls.Add(ChatState); t4.Controls.Add(bottom); t4.Controls.Add(b4);
            ChatSend.Click += (s, e) => LastAsk = SendAsync();
            ChatInput.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; LastAsk = SendAsync(); } };
            _tabs.SelectedIndexChanged += (s, e) => UpdateChatState();

            var t5 = new TabPage("По часам") { BackColor = Theme.Background };
            Hours = new HourView(_fishName, _locName);
            t5.Controls.Add(Hours);
            var t6 = new TabPage("Цель") { BackColor = Theme.Surface };
            GoalTab = new GoalView((id, name) => SafeHints(id, name), loc => _openMap?.Invoke(loc), (loc, fish, pts) => OpenReferencePoints?.Invoke(loc, fish, pts));   // FIX-05
            GoalTab.GoalChanged += g =>
            {
                try
                {
                    var st = _settings();
                    st.Goal.Fish = g.Fish; st.Goal.MinClass = g.MinClass; st.Goal.Location = g.Location;
                    st.Save(_log);
                }
                catch (Exception ex) { _log?.Invoke("[ADVISOR] цель не сохранена: " + ex.Message); }
            };
            t6.Controls.Add(GoalTab);
            _tabs.TabPages.AddRange(new[] { t0, t2, t3, t4, t5, t6 });
            // ADVISOR-UX-01: тёмное меню-переключатель вместо светлых системных вкладок (TabControl остаётся моделью страниц)
            _tabs.Appearance = TabAppearance.FlatButtons; _tabs.ItemSize = new Size(0, 1); _tabs.SizeMode = TabSizeMode.Fixed;
            // HOURS-01: «По часам» — вторая кнопка (страница добавлена в конец, номера прежних страниц не меняются)
            // MASTER-01: «Цель» — первая; «Водоёмы» и «Подробно» (таблицы для аналитика) в простом виде скрыты за кнопкой «Ещё»
            var names = new[] { ("Цель", 5), ("Рыба", 0), ("По часам", 4), ("Спросить ИИ", 3), ("Водоёмы", 1), ("Подробно", 2) };
            for (int i = 0; i < names.Length; i++)
            {
                int idx = names[i].Item2;
                var b = new Button { Text = names[i].Item1, Width = 110, Height = 36, Margin = new Padding(i == 0 ? 0 : 6, 0, 0, 0), Tag = idx };
                Theme.StyleButton(b, Theme.ButtonNeutral);
                b.Click += (s, e) => _tabs.SelectedIndex = idx;
                _nav.Controls.Add(b);
                if (idx == 1 || idx == 2) _expertButtons.Add(b);
            }
            var more = new Button { Text = "Ещё ▾", Width = 90, Height = 36, Margin = new Padding(6, 0, 0, 0), Tag = -1 };
            Theme.StyleButton(more, Theme.ButtonNeutral);
            bool simple = true; try { simple = _settings().SimpleMode; } catch { }
            foreach (var b in _expertButtons) b.Visible = !simple;
            more.Visible = simple;
            // UX-FIX: «Ещё» — выпадающее меню (раньше раскрывало кнопки за краем панели)
            var moreMenu = new ContextMenuStrip { ShowImageMargin = false, Font = Theme.Ui };
            foreach (var (txt, idx) in new[] { ("Водоёмы", 1), ("Подробно", 2) })
            {
                var mi = new ToolStripMenuItem(txt);
                int ii = idx; mi.Click += (s, e) => { _tabs.SelectedIndex = ii; };
                moreMenu.Items.Add(mi);
            }
            more.Click += (s, e) => moreMenu.Show(more, new Point(0, more.Height));
            _nav.Controls.Add(more);
            void Mark() { foreach (Button b in _nav.Controls) Theme.StyleButton(b, (int)b.Tag! == _tabs.SelectedIndex ? Theme.ButtonPrimary : Theme.ButtonNeutral); }
            _tabs.SelectedIndexChanged += (s, e) => Mark();
            _tabs.SelectedIndex = 5;
            Mark();
        }

        private sealed class SpeciesItem { public uint Id; public string Text = ""; public string Name = ""; public int Own, Pub; public bool RefOnly; public override string ToString() => Text; }
        private sealed class WaterItem { public uint? Id; public string Text = ""; public bool All; public override string ToString() => Text; }

        private bool _reloading;

        /// <summary>AUDIT-04 (B1): автообновление — чтение БД и расчёт аналитики в фоне, применение к окну — в потоке окна.</summary>
        private void ReloadInBackground()
        {
            if (_reloading || IsDisposed) return;
            _reloading = true;
            var s = _settings();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            System.Threading.Tasks.Task.Run(() => (BiteAnalytics.Load(_dbPath, s.Analytics, _fishName, _locName, BaitName), ModelRegistry.Load(_registryPath)))
                .ContinueWith(t =>
                {
                    _reloading = false;
                    if (IsDisposed) return;
                    if (t.Exception != null) { _log?.Invoke("[ADVISOR] автообновление: " + t.Exception.InnerException?.Message); return; }
                    (_an, _reg) = t.Result;
                    ApplyData();
                    if (sw.ElapsedMilliseconds > 1000) _log?.Invoke("[ADVISOR] автообновление заняло " + sw.ElapsedMilliseconds + " мс (в фоне)");
                }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        public void ReloadData(bool learn, bool force = false)
        {
            try
            {
                var s = _settings();
                _an = BiteAnalytics.Load(_dbPath, s.Analytics, _fishName, _locName, BaitName);
                _reg = ModelRegistry.Load(_registryPath);
                if (learn && (s.Analytics.AutoRetrain || force) && _an.Learn(_reg, force))
                {
                    try { _reg.Save(_registryPath); } catch (Exception ex) { _log?.Invoke("[ADVISOR] Не удалось сохранить формулы: " + ex.Message); }
                }
            }
            catch (Exception ex) { _log?.Invoke("[ADVISOR] Ошибка загрузки данных: " + ex.Message); }
            ApplyData();
        }

        private void ApplyData()
        {
            var o = _an.Overview(_reg);
            _overview.Text = $"Ваши данные: {o.Casts} забросов · {o.OwnCatches} уловов · {o.Species} видов · {o.PublicAnnouncements} объявлений других игроков";
            _overview.Height = 34;

            uint? prevFish = (SpeciesBox.SelectedItem as SpeciesItem)?.Id;
            SpeciesBox.Items.Clear();
            var known = new HashSet<uint>();
            foreach (var sp in _an.KnownSpecies()) { known.Add(sp.id); SpeciesBox.Items.Add(new SpeciesItem { Id = sp.id, Name = sp.name, Own = sp.own, Pub = sp.pub, Text = $"{sp.name} — мои {sp.own}, у других {sp.pub}" }); }
            // FIX-03: виды, которых нет в своих данных, но есть в справочнике — чтобы и для них был ориентир «с чего начать»
            try
            {
                foreach (var (rid, rname) in _refProvider.ReferenceSpecies().Where(x => !known.Contains(x.Id)).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                    SpeciesBox.Items.Add(new SpeciesItem { Id = rid, Name = rname, RefOnly = true, Text = $"{rname} — только справочник" });
            }
            catch (Exception ex) { _log?.Invoke("[ADVISOR] справочник видов: " + ex.Message); }
            FillPicker(prevFish);
            if (SpeciesBox.Items.Count > 0)
            {
                int idx = prevFish.HasValue ? SpeciesBox.Items.Cast<SpeciesItem>().ToList().FindIndex(x => x.Id == prevFish) : -1;
                SpeciesBox.SelectedIndex = idx >= 0 ? idx : 0;
            }
            else { ShowAdvice(); ShowCard(null); }

            WaterBox.Items.Clear();
            // UI-02/3: records without a water are not a water to choose — they stay only in «Все водоёмы», with a note
            var (nwOwn, nwPub) = _an.NoWaterCount; int nw = nwOwn + nwPub;
            WaterBox.Items.Add(new WaterItem { All = true, Text = "Все водоёмы" + (nw > 0 ? $"  (и ещё {nw} {(nw % 100 is >= 11 and <= 14 ? "старых записей" : (nw % 10) switch { 1 => "старая запись", 2 or 3 or 4 => "старые записи", _ => "старых записей" })} без водоёма)" : "") });
            foreach (var w in _an.Presence().Where(p => p.LocationId.HasValue).GroupBy(p => p.LocationId).Select(g => new WaterItem { Id = g.Key, Text = $"{g.First().Waterbody} — видов {g.Count()}" }).OrderBy(w => w.Text))
                WaterBox.Items.Add(w);
            WaterBox.SelectedIndex = 0;
            try { Hours.Reload(_dbPath, _an.Attempts, _an.Habitat); Hours.SetFish((SpeciesBox.SelectedItem as SpeciesItem)?.Id); } catch (Exception ex) { _log?.Invoke("[ADVISOR] по часам: " + ex.Message); }
            RefreshWhen((SpeciesBox.SelectedItem as SpeciesItem)?.Id);   // BRIDGE-FB-01: данные часов готовы только теперь
            try
            {
                GoalSpec? keep = GoalTab.Spec;
                if (keep == null) { var gs = _settings().Goal; if (gs.Fish > 0) keep = new GoalSpec { Fish = gs.Fish, MinClass = gs.MinClass, Location = gs.Location }; }
                GoalTab.SetData(_an, SpeciesBox.Items.Cast<SpeciesItem>().Select(x => (x.Id, x.Name)), BaitName, keep);
            }
            catch (Exception ex) { _log?.Invoke("[ADVISOR] цель: " + ex.Message); }
            ShowFormula();
            UpdateChatState();
        }

        /// <summary>Имя наживки по id предмета (задаётся окном программы).</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Func<long, string>? BaitName { get; init; }
        /// <summary>BRIDGE-FB-01: открыть карту водоёма с точками других игроков (водоём, рыба, мировые X/Z и «NNN:NNN»).</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Action<uint, string, IReadOnlyList<(double X, double Z, string G)>>? OpenReferencePoints { get; init; }
        public BiteAnalytics Analytics => _an;
        public ModelRegistry Registry => _reg;

        // ------------------------------------------------------------------ ADVISOR-UX-01: карточка вида
        private void BuildFishCard()
        {
            var head = new Panel { Height = 74, Margin = new Padding(0, 0, 0, 2), BackColor = Theme.Surface };
            head.Controls.Add(_cardIcon); head.Controls.Add(_cardName); head.Controls.Add(_cardSub);
            head.Resize += (s, e) => { _cardName.Width = head.Width - 80; _cardSub.Width = head.Width - 80; };
            _cardSub.Font = Theme.Ui;
            var mapBtn = Btn("Открыть карту водоёма", 220, Theme.ButtonMap);
            mapBtn.Margin = new Padding(0, 0, 0, 4);
            mapBtn.Click += (s, e) => _openMap?.Invoke((Places.Rows.FirstOrDefault()?.Tag as EvidenceRow)?.LocationId);
            foreach (var l in new[] { Places, Waters, Baits, Times, RefPlaces, RefBaits })
            {
                l.BackColor = Theme.SurfaceAlt;
                l.RowClicked += r =>
                {
                    // BRIDGE-FB-01: строка «Другие игроки» с точками — карта с этими точками поверх
                    if (r.Tag is RefPlaceTag rt && rt.Points.Count > 0 && OpenReferencePoints != null) { OpenReferencePoints(rt.Loc, rt.Fish, rt.Points); return; }
                    if (r.Tag is EvidenceRow er && er.LocationId.HasValue) _openMap?.Invoke(er.LocationId);
                    else if (r.Tag is RefPlaceTag rt2) _openMap?.Invoke(rt2.Loc);
                };
            }
            RefPlaces.BackColor = RefBaits.BackColor = Theme.Lighten(Theme.Surface, 0.03);
            Recipes.BackColor = Theme.SurfaceAlt; _recipeNote.Height = 34;
            When.Click += (s, e) => _tabs.SelectedIndex = 4;   // «По часам» (рыба там уже выбрана)
            Card.SuspendLayout();
            Card.Controls.AddRange(new Control[]
            {
                head, CardStatus,
                UiKit.Section("Где ловить", "ваши уловы, места радиусом 5 м"), Places,
                UiKit.Section("Водоёмы", "доля поимок этого вида на ваших забросах"), Waters,
                UiKit.Section("На что", "наживка · все водоёмы"), Baits, _hookNote,
                UiKit.Section("Чем прикормить", "готовый рецепт · подробнее во вкладке «Цель»"), Recipes, _recipeNote,
                UiKit.Section("Когда", "игровое время · доля поимок по часам · нажмите — вкладка «По часам»"), When,
                UiKit.Section("Другие игроки", "справочник чужих уловов · не ваша статистика"), RefPlaces, RefBaits, _refNote,
                UiKit.Note("«Когда»: высота — доля записей рыбы в часе (свои и чужие отдельно); «чаще всего» — с поправкой на забросы / время наблюдения, поэтому может не совпадать с самым высоким столбцом. Не вероятность клёва."),
                UiKit.Note("«5 из 26» — сколько раз этот вид попался на 26 ваших забросах в этом месте. Это частота по вашим данным, а не вероятность клёва. " +
                           "Зелёным отмечено, когда данных достаточно для совета. Подробная таблица и формула — во вкладке «Подробно»."),
                mapBtn
            });
            Card.ResumeLayout();
        }

        private void FillPicker(uint? keep)
        {
            var items = new List<SpeciesPicker.Item>();
            foreach (SpeciesItem it in SpeciesBox.Items)
            {
                string section = it.RefOnly ? "Справочник" : it.Own > 0 ? "Ловили вы" : "Ловят другие игроки";
                string sub = it.RefOnly ? "только справочник" : it.Own > 0 ? $"мои {it.Own} · у других {it.Pub}" : $"у других {it.Pub}";
                items.Add(new SpeciesPicker.Item { Id = it.Id, Name = it.Name, Subtitle = sub, Section = section });
            }
            var order = new[] { "Ловили вы", "Ловят другие игроки", "Справочник" };
            Picker.SetItems(items.OrderBy(i => Array.IndexOf(order, i.Section)).ThenBy(i => i.Section == "Справочник" ? i.Name : "").ThenByDescending(i => i.Section == "Справочник" ? 0 : 1), keep);
        }

        internal void SelectFish(uint id)
        {
            for (int i = 0; i < SpeciesBox.Items.Count; i++)
                if (SpeciesBox.Items[i] is SpeciesItem it && it.Id == id) { if (SpeciesBox.SelectedIndex != i) SpeciesBox.SelectedIndex = i; return; }
        }

        private static string ClassName(int w) => w >= 0 ? GradeBadge.ShortName(w) : "";

        internal void ShowCard(uint? fishId)
        {
            Card.SuspendLayout();
            try
            {
                if (fishId is not uint id)
                {
                    _cardIcon.Image = null; _cardName.Text = "Выберите рыбу слева"; _cardSub.Text = "или начните вводить название в поиске";
                    UiKit.SetPill(CardStatus, "Пока нет ни одного вида в данных", Theme.ButtonNeutral);
                    foreach (var l in new[] { Places, Waters, Baits, Times, RefPlaces, RefBaits }) l.SetRows(Array.Empty<StatRow>());
                    When.SetData(null, null);
                    _hookNote.Text = ""; _hookNote.Visible = false;
                    _refNote.Text = "";
                    return;
                }
                var adv = _an.Advise(id);
                _cardIcon.Image = UiKit.FishIcon(id);
                _cardName.Text = adv.Fish;
                int best = _an.MaxWindex(id), maxW = _an.MaxWeight(id);
                var subs = new List<string> { $"ваших поимок: {adv.OwnCatches}", $"у других: {adv.PublicSeen}" };
                if (best >= 0) subs.Add("лучший класс: " + ClassName(best));
                if (maxW > 0) subs.Add("рекорд: " + UiKit.Grams(maxW));
                _cardSub.Text = string.Join("  ·  ", subs);
                var hints = adv.HasAdvice ? null : SafeHints(id, adv.Fish);
                if (adv.HasAdvice) UiKit.SetPill(CardStatus, "✓  Есть совет по вашим уловам", Theme.AccentGreen);
                else if (hints != null) UiKit.SetPill(CardStatus, "Своих данных мало — ниже ориентир по уловам других игроков", Theme.Warning);
                else UiKit.SetPill(CardStatus, "Данных пока нет — ни ваших, ни в справочнике", Theme.ButtonNeutral);

                // полоса — только там, где данных хватает для совета; длина — относительно лучшей такой строки блока
                // (строки легче сравнить); у строк «мало данных» полосы нет, чтобы «1 из 1» не выглядело лучшим местом
                static double Rate(EvidenceRow r) => r.Attempts > 0 ? (double)r.Successes / r.Attempts : 0;
                double BestIn(string g) => Math.Max(1e-9, adv.Rows.Where(r => r.Group == g && r.Advice).Select(Rate).DefaultIfEmpty(0).Max());
                StatRow Ev(EvidenceRow r, string title, string sub, Image? icon) => new()
                {
                    Icon = icon, Title = title, Subtitle = r.Advice ? sub : (sub.Length > 0 ? sub + " · " : "") + "мало данных",
                    Value = $"{r.Successes} из {r.Attempts}",
                    Fraction = r.Advice ? Math.Min(1, Rate(r) / BestIn(r.Group)) : null, Muted = !r.Advice,
                    Accent = r.Advice ? Theme.AccentGreen : Theme.AccentTeal, Tag = r,
                    Tooltip = r.Verdict + (r.Level.Length > 0 ? "\n" + r.Level : "")
                };
                IEnumerable<EvidenceRow> Top(string g, int n) => adv.Rows.Where(r => r.Group == g).OrderByDescending(r => r.Advice).ThenByDescending(r => r.Lo).ThenByDescending(r => r.Successes).Take(n);

                Places.SetRows(Top("Участок", 5).Select(r =>
                {
                    var sub = new List<string>();
                    if (r.X.HasValue && r.Z.HasValue)
                    {
                        if (MapGridCoordinates.ToGrid(r.LocationId, r.X.Value, r.Z.Value) is { } g) sub.Add("координаты " + MapGridCoordinates.FormatGrid(g));
                        else sub.Add(FormattableString.Invariant($"X {r.X.Value:0}, Z {r.Z.Value:0}"));
                        if (Bathymetry.DepthAt(r.LocationId, r.X.Value, r.Z.Value) is double d) sub.Add(FormattableString.Invariant($"глубина {d:0.0} м"));
                    }
                    sub.Add(UiKit.Casts(r.Attempts));
                    return Ev(r, r.Name.Length > 0 ? r.Name : r.Item, string.Join(" · ", sub), null);
                }));
                Waters.SetRows(Top("Водоём", 4).Select(r => Ev(r, r.Name.Length > 0 ? r.Name : r.Item, "", null)));
                Baits.SetRows(Top("Наживка", 5).Select(r => Ev(r, r.Name.Length > 0 ? r.Name : r.Item, "", UiKit.BaitIcon(r.BaitTid))));
                try { _hookNote.Text = GameKnowledge.TrophyHookHint(id, adv.Fish) ?? ""; } catch { _hookNote.Text = ""; }
                _hookNote.Visible = _hookNote.Text.Length > 0;
                try
                {
                    var gbr = GroundbaitCalc.For(id, _an.DbPath);
                    Recipes.SetRows(gbr.Recipes.Take(3).Select(rc => new StatRow
                    {
                        Title = rc.Title, Subtitle = rc.Summary, Value = rc.Ready && rc.Cost == 0 ? "" : GroundbaitCalc.Money(rc.Cost),
                        Accent = rc.FromGame ? Theme.Warning : Theme.AccentGreen,
                        Tooltip = string.Join("\n", rc.Parts.Select(p => $"{p.Slot}: {p.Name} — {p.Source}")) + "\n" + rc.Why
                    }));
                    _recipeNote.Text = gbr.Note.Length > 0 ? gbr.Note : gbr.Recipes.Count > 0 ? "Цена — за набор (≈10 порций). Наведите на строку — откуда каждый ингредиент." : "";
                }
                catch { Recipes.SetRows(Array.Empty<StatRow>()); _recipeNote.Text = ""; }
                Times.SetRows(Top("Игровое время", 4).Select(r => Ev(r, r.Name, "", null)));
                RefreshWhen(id);

                var h = hints ?? SafeHints(id, adv.Fish);
                if (h == null)
                {
                    RefPlaces.SetRows(Array.Empty<StatRow>()); RefBaits.SetRows(Array.Empty<StatRow>()); _refNote.Text = "";
                }
                else
                {
                    int maxL = Math.Max(1, h.Locations.Select(l => l.Count).DefaultIfEmpty(1).Max());
                    RefPlaces.SetRows(h.Locations.OrderByDescending(l => l.Count).Take(4).Select(l =>
                    {
                        var pts = RefWorldPoints(l.LocationId, l.Points);
                        return new StatRow
                        {
                            Title = l.Name, Subtitle = (l.Points.Count > 0 ? "точки " + string.Join(", ", l.Points.Take(4)) : "") + (pts.Count > 0 ? " · щёлкните — на карте" : ""),
                            Value = l.Count + " " + UiKit.Catches(l.Count), Fraction = (double)l.Count / maxL, Accent = Theme.Warning, Muted = adv.HasAdvice,
                            Tooltip = ReferenceProvider.SourceNote,
                            Tag = l.LocationId > 0 ? new RefPlaceTag((uint)l.LocationId, adv.Fish, pts) : null
                        };
                    }));
                    int maxB = Math.Max(1, h.Baits.Select(b => b.Count).DefaultIfEmpty(1).Max());
                    RefBaits.SetRows(h.Baits.OrderByDescending(b => b.Count).Take(4).Select(b => new StatRow
                    {
                        Title = b.Name, Value = b.Count + " " + UiKit.Catches(b.Count), Fraction = (double)b.Count / maxB, Accent = Theme.Warning, Muted = adv.HasAdvice, Tooltip = ReferenceProvider.SourceNote
                    }));
                    var extra = new List<string>();
                    if (h.Periods.Count > 0) extra.Add("время: " + string.Join(", ", h.Periods.OrderByDescending(x => x.Value).Take(3).Select(x => Ru(x.Key))));
                    if (h.Weather.Count > 0) extra.Add("погода: " + string.Join(", ", h.Weather.OrderByDescending(x => x.Value).Take(2).Select(x => Ru(x.Key))));
                    _refNote.Text = $"Всего {h.Count} чужих поимок. " + string.Join(" · ", extra);
                }
            }
            finally { Card.ResumeLayout(true); Card.PerformLayout(); }
        }

        internal sealed record RefPlaceTag(uint Loc, string Fish, IReadOnlyList<(double X, double Z, string G)> Points);

        /// <summary>BRIDGE-FB-01: точки справочника «NNN:NNN» → мировые X/Z (тот же пересчёт, что для телефона).</summary>
        internal static List<(double X, double Z, string G)> RefWorldPoints(int loc, IEnumerable<string> points)
        {
            var res = new List<(double, double, string)>();
            var t = loc > 0 ? MapGridCoordinates.For((uint)loc) : null;
            if (t == null) return res;
            foreach (var p in points.Distinct(StringComparer.Ordinal))
                if (CompanionAdvisor.GridToWorld(t, p) is { } w) res.Add((w.X, w.Z, p));
            return res;
        }

        private void RefreshWhen(uint? id)
        {
            try { if (id is uint f && Hours != null) { var (m, p) = Hours.ForFish(f); When.SetData(m, p); } else When.SetData(null, null); }
            catch (Exception ex) { When.SetData(null, null); _log?.Invoke("[ADVISOR] «Когда»: " + ex.Message); }
        }

        private ReferenceHints? SafeHints(uint id, string name)
        {
            try { return _refProvider.GetHintsForSpecies(id, name); } catch { return null; }
        }

        private void ShowAdvice()
        {
            AdviceGrid.Rows.Clear();
            if (SpeciesBox.SelectedItem is not SpeciesItem it) { AdviceNotes.Text = "Пока нет ни одного вида в данных."; return; }
            var adv = _an.Advise(it.Id);
            foreach (var r in adv.Rows)
            {
                int i = AdviceGrid.Rows.Add(r.Group, r.Item, r.Attempts, r.Successes, r.Verdict, r.Level);
                var row = AdviceGrid.Rows[i]; row.Tag = r;
                row.DefaultCellStyle.ForeColor = r.Advice ? Theme.AccentGreen : Theme.TextMuted;
            }
            var sb = new StringBuilder();
            sb.AppendLine(adv.HasAdvice ? "Зелёным — группы с достаточными данными (сортировка по нижней границе 80%-интервала)." : "СОВЕТА НЕТ — данных недостаточно, программа не угадывает.");
            foreach (var n in adv.Notes) sb.AppendLine("• " + n);
            if (adv.Presence.Count > 0) sb.AppendLine("Встречается: " + string.Join(", ", adv.Presence.Take(6).Select(p => $"{p.Waterbody} (мои {p.OwnCatches}, у других {p.PublicSeen})")));
            if (!adv.HasAdvice) sb.Append(ReferenceText(it.Id, adv.Fish));   // FIX-03
            AdviceNotes.Text = sb.ToString().Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        }

        private static readonly Dictionary<string, string> RefWords = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Night"] = "ночь", ["Morning"] = "утро", ["Noon"] = "полдень", ["Afternoon"] = "день", ["Evening"] = "вечер",
            ["Clear"] = "ясно", ["Cloudy"] = "облачно", ["Rainy"] = "дождь", ["Foggy"] = "туман"
        };
        private static string Ru(string k) => RefWords.TryGetValue(k, out var v) ? v : k;

        /// <summary>FIX-03: справочный ориентир по чужим уловам (fisher-hub) — только когда своего совета нет.</summary>
        internal string ReferenceText(uint fishId, string fishName)
        {
            ReferenceHints? h;
            try { h = _refProvider.GetHintsForSpecies(fishId, fishName); }
            catch (Exception ex) { return "СПРАВОЧНО: справочник недоступен (" + ex.Message + ")"; }
            if (h == null) return "СПРАВОЧНО: этого вида нет в справочнике чужих уловов.";
            var sb = new StringBuilder();
            sb.AppendLine($"СПРАВОЧНО — чужие уловы (справочник, {h.Count} поимок), {ReferenceProvider.SourceNote}; ориентир «с чего начать», не вероятность клёва:");
            if (h.Locations.Count > 0)
                sb.AppendLine("• Где: " + string.Join("; ", h.Locations.OrderByDescending(l => l.Count).Take(5)
                    .Select(l => $"{l.Name} — {l.Count}" + (l.Points.Count > 0 ? " (точки " + string.Join(", ", l.Points.Take(4)) + ")" : ""))));
            if (h.Baits.Count > 0) sb.AppendLine("• Наживки: " + string.Join(", ", h.Baits.OrderByDescending(b => b.Count).Take(6).Select(b => $"{b.Name} — {b.Count}")));
            if (h.Lures.Count > 0) sb.AppendLine("• Приманки: " + string.Join(", ", h.Lures.OrderByDescending(b => b.Count).Take(4).Select(b => $"{b.Name} — {b.Count}")));
            if (h.Periods.Count > 0) sb.AppendLine("• Время: " + string.Join(", ", h.Periods.OrderByDescending(x => x.Value).Take(4).Select(x => $"{Ru(x.Key)} — {x.Value}")));
            if (h.Weather.Count > 0) sb.AppendLine("• Погода: " + string.Join(", ", h.Weather.OrderByDescending(x => x.Value).Take(4).Select(x => $"{Ru(x.Key)} — {x.Value}")));
            if (h.Depth.N > 0) sb.AppendLine($"• Глубина по справочнику (единицы не проверены): {h.Depth.Min:0.#}…{h.Depth.Max:0.#}, в среднем {h.Depth.Avg:0.#} (n={h.Depth.N})");
            return sb.ToString();
        }

        private void ShowPresence()
        {
            PresenceGrid.Rows.Clear();
            var w = WaterBox.SelectedItem as WaterItem;
            foreach (var p in _an.Presence().Where(p => w == null || w.All || p.LocationId == w.Id).OrderBy(p => p.LocationId.HasValue ? 0 : 1))   // records without a water at the end
            {
                // AUDIT-02: объявления места поимки не доказывают — показываем, подтверждён ли вид на этом водоёме
                string hab = p.LocationId.HasValue && _an.Habitat.Lives(p.FishId, p.LocationId) ? HabitatIndex.SourceText(_an.Habitat.Sources(p.FishId, p.LocationId.Value))
                           : _an.Habitat.Known(p.FishId) ? "не подтверждено (только объявления)" : "нет сведений";
                int i = PresenceGrid.Rows.Add(p.Waterbody, p.Fish, p.OwnCatches, p.PublicSeen, p.MaxWindex >= 0 ? GradeBadge.ShortName(p.MaxWindex) : "—", p.Zones, hab);
                if (!(p.LocationId.HasValue && _an.Habitat.Lives(p.FishId, p.LocationId))) PresenceGrid.Rows[i].DefaultCellStyle.ForeColor = Theme.TextMuted;
                if (p.MaxWindex >= 0 && GradeBadge.ColorOf(p.MaxWindex) is Color c) PresenceGrid.Rows[i].Cells["cls"].Style.ForeColor = c;
            }
        }

        private void ShowFormula()
        {
            var sb = new StringBuilder();
            var o = _an.Overview(_reg); var s = _an.S;
            sb.AppendLine("ФОРМУЛА КЛЁВА — оценка, насколько активно клюёт на заброс (не вероятность; логистическая регрессия)");
            sb.AppendLine("Состояние: " + o.FormulaStatus);
            sb.AppendLine();
            sb.AppendLine($"Условия подбора: ≥ {s.MinLabeledForFormula} забросов с известным исходом, ≥ {s.MinPositivesForFormula} поклёвок, ≥ {s.MinNegativesForFormula} без поклёвки;");
            sb.AppendLine($"принимается, только если на проверке «вперёд по времени» (учится на прошлом, проверяется на следующих 4 отрезках) лучше среднего на ≥ {s.MinImprovement:P0} минимум в 4 отрезках из 4;");
            sb.AppendLine($"после принятия проверяется на новых забросах (от {s.ForwardTestMin}); если там хуже среднего — отключается.");
            sb.AppendLine($"Сейчас: исход известен у {o.Labeled} из {o.Casts} забросов (поклёвок {o.Bites}, без поклёвки {o.NoBites}); у {o.UnknownOutcome} исход неизвестен — они не используются.");
            var act = _reg.ActiveModel;
            if (act != null)
            {
                sb.AppendLine();
                sb.AppendLine($"Активная формула v{act.Version} (создана {act.CreatedAt:yyyy-MM-dd HH:mm}, {act.N} забросов, средняя доля поклёвок {act.BaseRate:P1})");
                sb.AppendLine($"  кросс-проверка: log-loss {act.CvLogLoss:0.0000} против среднего {act.BaselineLogLoss:0.0000} (лучше на {act.Improvement:P1}), Brier {act.CvBrier:0.0000}");
                sb.AppendLine("  влияние признаков (вес на 1 стандартное отклонение; + повышает шанс поклёвки):");
                foreach (var (f, w) in act.Features.Zip(act.Weights).OrderByDescending(t => Math.Abs(t.Second)))
                    sb.AppendLine($"    {(w >= 0 ? "+" : "−")}{Math.Abs(w):0.000}  {f}");
                sb.AppendLine(act.ForwardN > 0 ? $"  на новых забросах: {act.ForwardN}, log-loss {act.ForwardLogLoss:0.0000} против {act.ForwardBaseline:0.0000}" : "  на новых забросах: пока нет данных");
                sb.AppendLine("  Связь ≠ причина: формула описывает ваши данные, а не точный алгоритм игры.");
            }
            if (_reg.History.Count > 0)
            {
                sb.AppendLine(); sb.AppendLine("История подборов:");
                foreach (var h in _reg.History.AsEnumerable().Reverse().Take(15))
                    sb.AppendLine($"  v{h.Version} {h.CreatedAt:yyyy-MM-dd HH:mm} · {h.N} забросов · {(h.Active ? "АКТИВНА" : h.Accepted ? "была принята" : "не принята")} · {h.Status}" + (h.DeactivatedReason.Length > 0 ? " · " + h.DeactivatedReason : ""));
            }
            FormulaText.Text = sb.ToString().Replace("\n", Environment.NewLine).Replace("\r\r", "\r");
        }

        private void UpdateChatState()
        {
            var a = _settings().Agent;
            bool ready = a.Enabled && a.HasKey && a.BaseUrl.Length > 0 && a.Model.Length > 0;
            ChatSend.Enabled = ready && _cts == null;
            ChatState.Text = ready
                ? $"Агент: {(a.Provider == AgentProvider.Anthropic ? "Anthropic" : "OpenAI-совместимый")} · {a.Model}. В запрос уходят только сводки ваших данных; агент ничего не меняет."
                : "Агент не настроен: откройте «Настройки агента…», включите агента, укажите адрес, модель и API-ключ.";
        }

        private async Task SendAsync()
        {
            string q = ChatInput.Text.Trim();
            var a = _settings().Agent;
            if (q.Length == 0 || _cts != null) return;
            string? key = a.GetApiKey();
            if (!a.Enabled || key == null) { UpdateChatState(); return; }
            // COACH-01: суточный лимит токенов
            var usage = AgentUsage.Load();
            if (!usage.CanSpend(a, CoachAi.ExpectedTokens)) { Append("Суточный лимит токенов исчерпан — " + usage.Text(a) + ". Изменить: Настройки → ИИ-агент."); return; }
            ChatInput.Clear();
            Append("Вы: " + q);
            _cts = new CancellationTokenSource(); UpdateChatState(); ChatState.Text = "Агент думает…";
            try
            {
                var client = new AgentClient(a, key, AgentHandler);
                var tools = AdvisorTools.Build(() => _an, () => _reg, () => _refProvider, SessionText);
                var reply = await client.AskAsync(_history.ToList(), q, tools, _cts.Token);
                var u2 = AgentUsage.Load(); u2.Add(client.LastTokens); u2.Save();
                Append("   (" + client.LastTokens + " токенов · " + u2.Text(a) + ")");
                if (reply.ToolCalls.Count > 0) Append("   (агент запросил у программы: " + string.Join("; ", reply.ToolCalls) + ")");
                if (reply.Ok)
                {
                    Append("Агент: " + reply.Text);
                    _history.Add(new AgentTurn { Role = "user", Text = q }); _history.Add(new AgentTurn { Role = "assistant", Text = reply.Text });
                    while (_history.Count > 6) _history.RemoveAt(0);   // COACH-01: короче история — дешевле каждый запрос
                }
                else Append("Ошибка: " + reply.Error);
            }
            finally { _cts?.Dispose(); _cts = null; if (!IsDisposed) UpdateChatState(); }
        }

        /// <summary>COACH-01: сводка текущей рыбалки для инструмента analyze_session.</summary>
        private string SessionText()
        {
            try
            {
                var snap = SessionCoach.Build(_dbPath, _an.Soak, DateTime.Now, _locName);
                return snap.HasData ? SessionCoach.Summary(snap, _an.Soak) : "Сейчас нет активной рыбалки (нет тактов сервера за последние 45 мин). " + (_an.Soak != null ? "Кривая по минуте в воде (фидер): " + _an.Soak.Describe(1) : "");
            }
            catch (Exception ex) { return "Сводка недоступна: " + ex.Message; }
        }

        private void Append(string s) { if (IsDisposed) return; ChatLog.AppendText(s.Replace("\n", Environment.NewLine) + Environment.NewLine + Environment.NewLine); }

        protected override void OnFormClosed(FormClosedEventArgs e) { try { _watch.Stop(); _watch.Dispose(); } catch { } try { _cts?.Cancel(); } catch { } base.OnFormClosed(e); }
    }
}
