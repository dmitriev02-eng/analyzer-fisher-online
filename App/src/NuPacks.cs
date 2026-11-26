using System.Globalization;

namespace FisherLogger;

/// <summary>
/// PACKS-01: вкладка «Торговые паки» — цены из трафика (sale_limit), калькулятор себестоимости
/// и уведомления о моменте продажи (пик / предмаксимум).
/// </summary>
public sealed class NuPacks : Panel
{
    // ------------------------------------------------------------- настройки уведомлений
    public sealed class PacksSettings
    {
        public bool Notify { get; set; } = true;
        public int Mode { get; set; } = 1;          // 0 — максимальная цена (пик), 1 — предмаксимальная
        public int PeakPct { get; set; } = 120;      // % средней = «пик»
        public int PrePct { get; set; } = 105;       // % средней = «предмаксимум»
        public int MinProfitPct { get; set; } = 10;  // минимальная прибыль, %
        public bool ProfitOnly { get; set; } = true;
        public int Packaging { get; set; } = 5;
        public long PackagingRub { get; set; } = 500;
        public long WorkRub { get; set; } = 200;
        public long CordRub { get; set; } = 200;
        /// <summary>PACKS-03: id паков, по которым присылать уведомления (пусто — все).</summary>
        public List<long> NotifyPacks { get; set; } = new();
        /// <summary>PACKS-03: показывать калькулятор (кнопка вкл/выкл).</summary>
        public bool CalcShown { get; set; } = true;
        public static string? PathOverride { get; set; }
        public static string FilePath => PathOverride ?? Path.Combine(AppInfo.UserDir, "packs.json");
        public static PacksSettings Load() => JsonFile.Load<PacksSettings>(FilePath);
        public void Save() => JsonFile.Save(FilePath, this);
        /// <summary>Порог сегодняшней цены в % средней для выбранного режима.</summary>
        public int ThresholdPct => Mode == 0 ? PeakPct : PrePct;
        public string ModeText => Mode == 0 ? "максимальная цена (пик)" : "предмаксимальная (" + PrePct + "% средней)";
    }

    public PacksSettings Settings = PacksSettings.Load();
    public Func<string, (uint water, string name)?>? WaterOf;
    public Action<string>? Log;
    public Action<string>? NotifyTest;

    private readonly Label _title = new() { AutoSize = true, Font = NuTheme.F(15f, FontStyle.Bold), ForeColor = NuTheme.Text, Location = new Point(24, 14) };
    private readonly Label _source = new() { AutoSize = true, Font = NuTheme.F(9f), ForeColor = NuTheme.Muted, Location = new Point(24, 48) };
    private readonly Button _reload = Btn("Обновить из трафика", NuTheme.Accent, 210);
    private readonly Label _hint = new() { AutoSize = true, Font = NuTheme.F(9f), ForeColor = NuTheme.Muted, Location = new Point(24, 78), MaximumSize = new Size(1600, 0) };
    private readonly DataGridView _grid = NuGrid.Make();
    private readonly Label _calc = new() { Dock = DockStyle.Bottom, Height = 96, ForeColor = NuTheme.Text, Font = NuTheme.F(9.5f), Padding = new Padding(24, 8, 24, 4) };
    private readonly CheckBox _notify = new() { Text = "Уведомлять о лучшем моменте продажи", AutoSize = true, Checked = true, ForeColor = NuTheme.Text, Font = NuTheme.F(9.5f) };
    private readonly ComboBox _mode = new() { Width = 340, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = NuTheme.Raised, ForeColor = NuTheme.Text, Font = NuTheme.F(9.5f) };
    private readonly NumericUpDown _minProfit = new() { Width = 90, Minimum = -100, Maximum = 500, Value = 10, BackColor = NuTheme.Raised, ForeColor = NuTheme.Text, BorderStyle = BorderStyle.FixedSingle, Font = NuTheme.F(9.5f) };
    private readonly CheckBox _profitOnly = new() { Text = "Только прибыльные", AutoSize = true, Checked = true, ForeColor = NuTheme.Text, Font = NuTheme.F(9.5f) };
    private readonly Button _test = Btn("Проверить уведомление", NuTheme.Line2, 190);
    private readonly FlowLayoutPanel _packChecks = new() { Dock = DockStyle.Bottom, Height = 38, BackColor = NuTheme.Panel, Padding = new Padding(20, 4, 20, 4), FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = true };
    private readonly FlowLayoutPanel _foot = new() { Dock = DockStyle.Bottom, Height = 150, BackColor = NuTheme.Ground, Padding = new Padding(20, 4, 20, 4), FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = true };
    private readonly CheckBox _calcToggle = new() { Text = "Калькулятор", AutoSize = true, Checked = true, ForeColor = NuTheme.Text, Font = NuTheme.F(9.5f) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 8000 };
    private readonly List<PackRow> _rows = new();
    private Dictionary<long, PackPrice> _prices = new();
    private Dictionary<long, int> _inv = new();
    private string _lastNotified = "";
    private string _capturePath = "";
    private DateTime _lastSeen = DateTime.MinValue;
    private bool _loading;

    public NuPacks()
    {
        DoubleBuffered = true; BackColor = NuTheme.Ground; Dock = DockStyle.Fill; Padding = new Padding(0);
        _title.Text = "Торговые паки";
        _source.Text = "Цены — из трафика игры (меню «Продам паки»). Пока трафика нет — себестоимость по каталогу.";
        _hint.Text = "Как считается: себестоимость = 20 × цена ресурса + 5 × Упаковка + 1 × Корд + работа. " +
                     "Прибыль = цена «сегодня» − себестоимость. Лимит продажи — 10 паков в сутки.";
        _reload.Location = new Point(0, 0);   // разместим в top
        _reload.Click += (s, e) => Reload(force: true);

        _grid.Columns.Add(NuGrid.Col("Груз", 130));
        _grid.Columns.Add(NuGrid.Col("Ресурс", 120));
        _grid.Columns.Add(NuGrid.Col("Цена ресурса", 110, true));
        _grid.Columns.Add(NuGrid.Col("Себестоимость", 110, true));
        _grid.Columns.Add(NuGrid.Col("Сегодня", 95, true));
        _grid.Columns.Add(NuGrid.Col("Средняя", 95, true));
        _grid.Columns.Add(NuGrid.Col("Прибыль", 95, true));
        _grid.Columns.Add(NuGrid.Col("Сигнал", 115));
        _grid.Columns.Add(NuGrid.Col("В наличии", 95, true));
        _grid.CellFormatting += (s, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
            var r = _grid.Rows[e.RowIndex].Tag as PackRow;
            if (r == null) return;
            if (e.ColumnIndex == 6)   // прибыль
            {
                e.CellStyle.ForeColor = r.Profit > 0 ? NuTheme.Ok : r.Profit < 0 ? NuTheme.Danger : NuTheme.Muted;
                e.CellStyle.Font = new Font(NuTheme.Family, 9.5f, FontStyle.Bold);
            }
            if (e.ColumnIndex == 7) e.CellStyle.ForeColor = SignalColor(r);
        };
        _grid.SelectionChanged += (s, e) => ShowCalc();

        _calc.Paint += PaintCalc;
        _calc.BackColor = NuTheme.Ground;

        _mode.Items.AddRange(new object[] { "Максимальная цена (пик)", "Предмаксимальная цена" });
        _mode.SelectedIndex = Math.Clamp(Settings.Mode, 0, 1);
        _mode.SelectedIndexChanged += (s, e) => SaveSettings();
        _notify.Checked = Settings.Notify; _notify.CheckedChanged += (s, e) => SaveSettings();
        _minProfit.Value = Math.Clamp(Settings.MinProfitPct, -100, 500); _minProfit.ValueChanged += (s, e) => { SaveSettings(); SyncRows(); };
        _profitOnly.Checked = Settings.ProfitOnly; _profitOnly.CheckedChanged += (s, e) => SaveSettings();
        _test.Click += (s, e) => { string t = NotifyText(); NotifyTest?.Invoke(t.Length > 0 ? t : "Порог не достигнут — уведомление придёт, когда цена дойдёт до " + Settings.ThresholdPct + "% средней"); };
        _calcToggle.Checked = Settings.CalcShown;
        _calcToggle.CheckedChanged += (s, e) => { Settings.CalcShown = _calcToggle.Checked; try { Settings.Save(); } catch { } ApplyCalc(); };

        BuildLayout();
        _timer.Tick += (s, e) => { Reload(force: false); CheckNotify(); };
        _timer.Start();
        Reload(force: true);
    }

    private static Button Btn(string text, Color accent, int w) => new()
    {
        Text = text, Width = w, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = accent, ForeColor = NuTheme.Text,
        Font = NuTheme.F(9.5f), Cursor = Cursors.Hand, UseVisualStyleBackColor = false
    };

    private void BuildLayout()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 118, BackColor = NuTheme.Ground };
        top.Controls.Add(_title); top.Controls.Add(_source); top.Controls.Add(_hint);
        top.Paint += (s, e) => { using var p = new Pen(NuTheme.Line); e.Graphics.DrawLine(p, 0, top.Height - 1, top.Width, top.Height - 1); };

        var rightTop = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 250, Padding = new Padding(0, 14, 16, 0), FlowDirection = FlowDirection.TopDown };
        _reload.Location = new Point(0, 0);
        rightTop.Controls.Add(_reload);
        top.Controls.Add(rightTop);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 190, BackColor = NuTheme.Panel, Padding = new Padding(0) };
        var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 10, 20, 8), FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        settings.Controls.Add(_notify); settings.Controls.Add(new Label { Text = "   Режим:", AutoSize = true, ForeColor = NuTheme.Muted, Font = NuTheme.F(9.5f), Padding = new Padding(0, 4, 0, 0) });
        settings.Controls.Add(_mode);
        settings.Controls.Add(new Label { Text = "   мин. прибыль, %:", AutoSize = true, ForeColor = NuTheme.Muted, Font = NuTheme.F(9.5f), Padding = new Padding(0, 4, 0, 0) });
        settings.Controls.Add(_minProfit);
        settings.Controls.Add(_profitOnly);
        settings.Controls.Add(_test); settings.Controls.Add(_calcToggle);
        _notify.ForeColor = NuTheme.Text; _profitOnly.ForeColor = NuTheme.Text;
        var mt = new Label { AutoSize = true, ForeColor = NuTheme.Muted, Font = NuTheme.F(9f), MaximumSize = new Size(1500, 0), Padding = new Padding(0, 8, 0, 0) };
        mt.Text = "Уведомление приходит в трей, когда сегодняшняя цена пака достигает выбранного порога, а прибыль выше минимума. " +
                  "«Пик» = цена выросла до максимума за день (≥120% средней); «предмаксимум» — 105% средней (продавать раньше, безопаснее).";
        settings.Controls.Add(mt);
        bottom.Controls.Add(settings);
        bottom.Paint += (s, e) => { using var p = new Pen(NuTheme.Line); e.Graphics.DrawLine(p, 0, 0, bottom.Width, 0); };

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 6, 24, 6) };
        body.Controls.Add(_grid);
        Controls.Add(body);
        Controls.Add(bottom);
        Controls.Add(_foot);
        Controls.Add(_packChecks);
        Controls.Add(top);
    }

    // ------------------------------------------------------------- данные
    public void Reload(bool force)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            if (force || _capturePath.Length == 0 || !File.Exists(_capturePath)) _capturePath = LatestCapture();
            if (_capturePath.Length == 0) { _source.Text = "Захват не найден — трафик ещё не записан. Себестоимость по каталогу."; }
            else
            {
                var fi = new FileInfo(_capturePath);
                if (force || fi.LastWriteTimeUtc != _lastSeen)
                {
                    _lastSeen = fi.LastWriteTimeUtc;
                    _prices = PacksData.ParseSaleLimitFile(_capturePath).ToDictionary(p => p.ItemId);
                    _inv = PacksData.ParseInventoryFile(_capturePath);
                    _source.Text = "Цены из " + Path.GetFileName(_capturePath) + " · " + _prices.Count + " предметов · " + fi.LastWriteTime.ToString("dd.MM HH:mm");
                }
            }
            _rows.Clear();
            _rows.AddRange(PacksData.Rows(_prices, null, Settings.Packaging, Settings.PackagingRub, Settings.WorkRub, _inv));
            SyncRows();
        }
        catch (Exception ex) { Log?.Invoke("[PACKS] " + ex.Message); }
        finally { _loading = false; }
    }

    private static string LatestCapture()
    {
        try
        {
            string dir = Path.Combine(AppPaths.BaseDirectory, "captures");
            var files = new List<string>();
            string live = Path.Combine(AppPaths.BaseDirectory, "capture.pcapng");
            if (File.Exists(live) && new FileInfo(live).Length > 2000) files.Add(live);
            if (Directory.Exists(dir)) files.AddRange(Directory.GetFiles(dir, "*.pcapng"));
            return files.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? "";
        }
        catch { return ""; }
    }

    private void SyncRows()
    {
        _grid.Rows.Clear();
        foreach (var r in _rows)
        {
            if (Settings.ProfitOnly && !r.Profitable) continue;
            if (r.Pct < Settings.MinProfitPct) continue;
            var idx = _grid.Rows.Add(
                r.Cargo.Name, r.IngredientName, r.IngredientRub > 0 ? r.IngredientRub + " ₽" : "—",
                r.CostRub + " ₽", r.TodayRub > 0 ? r.TodayRub + " ₽" : "—", r.BaseRub > 0 ? r.BaseRub + " ₽" : "—",
                (r.TodayRub > 0 ? (r.Profit >= 0 ? "+" : "") + r.Profit + " ₽" : "—"), SignalText(r),
                (r.HavePacks > 0 ? r.HavePacks.ToString() : "0"));
            _grid.Rows[idx].Tag = r;
        }
        ShowCalc();
        RefreshFoot();
        FillPackChecks();
        ApplyCalc();
    }

    private static string SignalText(PackRow r)
    {
        if (r.TodayRub <= 0) return "нет цены";
        if (r.BaseRub <= 0) return "только сегодня";
        double pct = r.TodayRub * 100.0 / r.BaseRub;
        if (pct >= 120) return "↑↑ пик (" + (int)pct + "%)";
        if (pct >= 105) return "↑ выше (" + (int)pct + "%)";
        if (pct <= 80) return "↓↓ дно (" + (int)pct + "%)";
        if (pct <= 95) return "↓ ниже (" + (int)pct + "%)";
        return "= обычно";
    }
    private static Color SignalColor(PackRow r)
    {
        if (r.TodayRub <= 0 || r.BaseRub <= 0) return NuTheme.Muted;
        double pct = r.TodayRub * 100.0 / r.BaseRub;
        return pct >= 120 ? NuTheme.Ok : pct >= 105 ? NuTheme.AccentText : pct <= 80 ? NuTheme.Danger : NuTheme.Muted;
    }

    private PackRow? Selected() => _grid.CurrentRow?.Tag as PackRow;
    private PackRow? Best() => _rows.Where(r => r.TodayRub > 0 && r.Profitable).OrderByDescending(r => r.Profit).FirstOrDefault();

    private void ShowCalc()
    {
        var r = Selected();
        string text;
        if (r == null) text = "Выберите груз в таблице — покажу расчёт: ресурс × 20 + упаковка + корд + работа = себестоимость; прибыль = цена − себестоимость.";
        else
        {
            string ing = r.IngredientRub > 0 ? r.IngredientRub + " ₽ (" + r.IngredientSource + ")" : "нет данных";
            text = r.Cargo.Name + "  →  " + r.Cargo.Count + " × " + (r.IngredientName.Length > 0 ? r.IngredientName : "ресурс") + " = " + ing +
                   "  ·  упаковка " + r.PackagingOverride + "×" + r.PackagingPriceOverride + " ₽  ·  корд " + r.Cargo.Cord + "×" + r.Cargo.CordRub + " ₽  ·  работа " + r.WorkOverride + " ₽" +
                   "\nВ наличии: паков " + r.HavePacks + ", ресурса " + r.HaveIngredient + " (на " + (r.Cargo.Count > 0 ? r.HaveIngredient / r.Cargo.Count : 0) + " паков)   ·   можно продать сегодня: " + CanSell(r) +
                   "\nСебестоимость: " + r.CostRub + " ₽   →   сегодня: " + (r.TodayRub > 0 ? r.TodayRub + " ₽" : "нет цены") +
                   (r.BaseRub > 0 ? " (средняя " + r.BaseRub + " ₽)" : "") + "   →   прибыль: " + (r.TodayRub > 0 ? (r.Profit >= 0 ? "+" : "") + r.Profit + " ₽ (" + r.Pct + "%)" : "—");
        }
        _calc.Text = text; _calc.Tag = text;
        _calc.Invalidate();
    }

    private void PaintCalc(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(NuTheme.Ground);
        using var p = new Pen(NuTheme.Line); g.DrawLine(p, 0, 0, _calc.Width, 0);
        string t = _calc.Text;
        using var f = NuTheme.F(9.5f); using var fb = NuTheme.F(10f, FontStyle.Bold);
        TextRenderer.DrawText(g, t, f, new Rectangle(24, 8, _calc.Width - 48, _calc.Height - 12), NuTheme.Soft,
            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
    }

    /// <summary>Сколько паков этого груза реально можно продать сегодня (инвентарь + лимит 10/сутки).</summary>
    public static int CanSell(PackRow r)
    {
        int left = PacksData.PackLimitPerDay - r.SoldToday;
        return Math.Max(0, Math.Min(Math.Min(r.HavePacks, left), PacksData.PackLimitPerDay));
    }

    /// <summary>Текст уведомления о лучшем моменте продажи; пусто — порог не достигнут.</summary>
    public string NotifyText()
    {
        var best = _rows.Where(r => r.TodayRub > 0 && r.BaseRub > 0 && r.Profitable && CanSell(r) > 0)
                        .Where(r => Settings.NotifyPacks.Count == 0 || Settings.NotifyPacks.Contains(r.Cargo.Id))
                        .OrderByDescending(r => r.TodayRub * 100.0 / r.BaseRub).FirstOrDefault();
        if (best == null) return "";
        long pct = best.TodayRub * 100 / best.BaseRub;
        if (pct < Settings.ThresholdPct) return "";
        int can = CanSell(best);
        return best.Cargo.Name + ": цена " + best.TodayRub + " ₽ (" + pct + "% средней — " + Settings.ModeText +
               ") — можно продать " + can + " шт, прибыль " + best.Profit + " ₽/пак";
    }

    private void CheckNotify()
    {
        if (!Settings.Notify) return;
        string t = NotifyText();
        if (t.Length == 0 || t == _lastNotified) return;
        _lastNotified = t;
        try { NotifyTest?.Invoke(t); } catch { }
    }

    /// <summary>PACKS-03: сноска «на 1 единицу». В ней только цена продажи пака; нет цены — подсказка, что открыть в игре.</summary>
    private void RefreshFoot()
    {
        if (_foot.IsDisposed) return;
        _foot.SuspendLayout();
        foreach (Control c in _foot.Controls) c.Dispose();
        _foot.Controls.Clear();
        foreach (var r in _rows)
        {
            var card = new Panel { Width = 268, Height = 128, BackColor = NuTheme.Panel, Margin = new Padding(0, 0, 8, 0), Padding = new Padding(0) };
            var pic = new PictureBox { Size = new Size(44, 44), Location = new Point(8, 10), SizeMode = PictureBoxSizeMode.Zoom, BackColor = NuTheme.Raised };
            try { string? ip = IconLoader.ResolvePath("assets/bait_icons/item_" + r.Cargo.Id + ".png"); if (ip != null && File.Exists(ip)) pic.Image = Image.FromFile(ip); } catch { }
            var nm = new Label { Text = r.Cargo.Name, Location = new Point(58, 8), Width = 204, Height = 20, ForeColor = NuTheme.Text, Font = NuTheme.F(9.5f, FontStyle.Bold) };
            var sell = new Label { Text = r.TodayRub > 0 ? "Продажа: " + r.TodayRub + " ₽" : "Продажа: —", Location = new Point(58, 30), Width = 204, Height = 18, ForeColor = NuTheme.Soft, Font = NuTheme.F(9f) };
            var unit = new Label
            {
                Text = r.TodayRub > 0
                    ? "на 1 ед.: " + (r.TodayRub / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " ₽"
                    : "Цены нет — откройте в игре «Продам паки» → «" + r.Cargo.Name + "»",
                Location = new Point(8, 60), Width = 252, Height = 44, ForeColor = NuTheme.AccentText, Font = NuTheme.F(8.8f)
            };
            card.Controls.Add(pic); card.Controls.Add(nm); card.Controls.Add(sell); card.Controls.Add(unit);
            _foot.Controls.Add(card);
        }
        _foot.ResumeLayout();
    }

    /// <summary>PACKS-03: галочки выбора паков для уведомлений (сохраняются в packs.json).</summary>
    private void FillPackChecks()
    {
        if (_packChecks.IsDisposed) return;
        _packChecks.SuspendLayout();
        foreach (Control c in _packChecks.Controls) c.Dispose();
        _packChecks.Controls.Clear();
        _packChecks.Controls.Add(new Label { Text = "Уведомлять по пакам:", AutoSize = true, ForeColor = NuTheme.Muted, Font = NuTheme.F(9f), Padding = new Padding(0, 7, 8, 0) });
        foreach (var r in _rows)
        {
            var row = r;
            var cb = new CheckBox
            {
                Text = row.Cargo.Name.Replace("Груз ", ""), AutoSize = true, ForeColor = NuTheme.Text, Font = NuTheme.F(9f),
                Checked = Settings.NotifyPacks.Count == 0 || Settings.NotifyPacks.Contains(row.Cargo.Id)
            };
            cb.CheckedChanged += (s, e) =>
            {
                if (cb.Checked) { if (!Settings.NotifyPacks.Contains(row.Cargo.Id)) Settings.NotifyPacks.Add(row.Cargo.Id); }
                else Settings.NotifyPacks.Remove(row.Cargo.Id);
                try { Settings.Save(); } catch { }
            };
            _packChecks.Controls.Add(cb);
        }
        _packChecks.ResumeLayout();
    }

    private void ApplyCalc() => _calc.Visible = Settings.CalcShown;

    private void SaveSettings()
    {
        Settings.Notify = _notify.Checked;
        Settings.Mode = Math.Clamp(_mode.SelectedIndex, 0, 1);
        Settings.MinProfitPct = (int)_minProfit.Value;
        Settings.ProfitOnly = _profitOnly.Checked;
        try { Settings.Save(); } catch { }
        SyncRows();
    }
}
