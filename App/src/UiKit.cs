using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace FisherLogger
{
    // =====================================================================================
    // ADVISOR-UX-01: one visual language for the player-facing views («Советник», карта).
    //  * StatList      — rows «иконка · название · подпись · полоса · значение», щелчок по строке
    //  * UiKit.Section — заголовок блока; UiKit.Pill — цветная плашка-статус
    //  * SpeciesPicker — поиск по названию + список видов с иконками и разделами
    // Only presentation: no data is computed here.
    // =====================================================================================

    public sealed class StatRow
    {
        public Image? Icon;
        public string Title = "";
        public string Subtitle = "";
        public string Value = "";
        public double? Fraction;              // 0…1 → полоса; null = без полосы
        public Color Accent = Theme.AccentTeal;
        public bool Muted;                    // «мало данных» — приглушённо
        /// <summary>MAPCARD-01: уловы по классу 0…3 — полоса делится на доли цвета класса (серый, зелёный, синий, оранжевый).</summary>
        public int[]? Classes;
        public string Tooltip = "";
        public object? Tag;
    }

    public sealed class StatList : Control
    {
        private readonly List<StatRow> _rows = new();
        private int _hover = -1;
        private readonly ToolTip _tip = new() { InitialDelay = 400, ReshowDelay = 200, AutoPopDelay = 15000 };
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string EmptyText { get; set; } = "Нет данных";
        public event Action<StatRow>? RowClicked;
        public IReadOnlyList<StatRow> Rows => _rows;
        public int RowHeight => _rows.Any(r => r.Subtitle.Length > 0) ? 50 : 38;

        public StatList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.SurfaceAlt; ForeColor = Theme.Text; Font = Theme.Ui;
            Margin = new Padding(0, 0, 0, 10);
            Height = 38;
        }

        public void SetRows(IEnumerable<StatRow> rows)
        {
            _rows.Clear(); _rows.AddRange(rows); _hover = -1;
            Height = Math.Max(1, _rows.Count) * RowHeight + 2;
            Invalidate();
        }

        private int IndexAt(Point p) => p.Y < 1 ? -1 : Math.Min(_rows.Count - 1, (p.Y - 1) / RowHeight);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = _rows.Count == 0 ? -1 : IndexAt(e.Location);
            if (i == _hover) return;
            _hover = i; Invalidate();
            Cursor = i >= 0 && RowClicked != null && _rows[i].Tag != null ? Cursors.Hand : Cursors.Default;
            _tip.SetToolTipT(this, i >= 0 ? _rows[i].Tooltip : "");
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int i = _rows.Count == 0 ? -1 : IndexAt(e.Location);
            if (i >= 0) RowClicked?.Invoke(_rows[i]);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Surface);
            using (var bg = new SolidBrush(BackColor)) using (var path = UiKit.Round(new Rectangle(0, 0, Width - 1, Height - 1), 8)) g.FillPath(bg, path);
            if (_rows.Count == 0)
            {
                UiText.DrawText(g, EmptyText, Theme.Ui, new Rectangle(12, 0, Width - 24, Height), Theme.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }
            int rh = RowHeight;
            var small = UiKit.Small;
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                var rc = new Rectangle(0, 1 + i * rh, Width, rh);
                if (i == _hover)
                    using (var hb = new SolidBrush(Theme.Lighten(BackColor, 0.06))) g.FillRectangle(hb, new Rectangle(rc.X + 2, rc.Y, rc.Width - 4, rc.Height));
                if (i > 0) using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, 12, rc.Y, Width - 12, rc.Y);
                int x = 12;
                if (r.Icon != null)
                {
                    int s = Math.Min(32, rh - 10);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(r.Icon, new Rectangle(x, rc.Y + (rh - s) / 2, s, s));
                    x += s + 10;
                }
                var fg = r.Muted ? Theme.TextMuted : Theme.Text;
                int valueW = r.Value.Length > 0 ? Math.Max(70, UiText.MeasureText(r.Value, Theme.Numeric).Width + 8) : 0;
                int textW = Width - x - valueW - 14;
                bool sub = r.Subtitle.Length > 0;
                UiText.DrawText(g, r.Title, Theme.UiBold, new Rectangle(x, rc.Y + (sub ? 6 : 0), textW, sub ? 20 : rh - (r.Fraction.HasValue ? 6 : 0)), fg,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | (sub ? 0 : TextFormatFlags.VerticalCenter));
                if (sub)
                    UiText.DrawText(g, r.Subtitle, small, new Rectangle(x, rc.Y + 25, textW, 16), Theme.TextMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                if (r.Fraction.HasValue)
                {
                    int by = rc.Bottom - 7, bw = Math.Max(20, textW);
                    using (var track = new SolidBrush(Theme.Lighten(BackColor, 0.08))) using (var tp = UiKit.Round(new Rectangle(x, by, bw, 4), 2)) g.FillPath(track, tp);
                    int fw = (int)Math.Round(bw * Math.Clamp(r.Fraction.Value, 0, 1));
                    int known = r.Classes?.Sum() ?? 0;
                    if (fw > 0 && known > 0)
                    {
                        // MAPCARD-01: доли классов внутри полосы (неизвестный класс — цветом строки)
                        int total = Math.Max(known, ParseCount(r.Value)), cx = x; fw = Math.Max(fw, 6);
                        var state = g.Save();
                        using (var clip = UiKit.Round(new Rectangle(x, by, fw, 4), 2)) g.SetClip(clip);
                        for (int k = 0; k < 4; k++)
                        {
                            if (r.Classes![k] == 0) continue;
                            int sw = (int)Math.Round(fw * r.Classes[k] / (double)total);
                            using var b = new SolidBrush(GradeBadge.ColorOf(k) ?? r.Accent); g.FillRectangle(b, cx, by, Math.Max(2, sw), 4); cx += sw;
                        }
                        if (cx < x + fw) using (var b = new SolidBrush(Theme.Darken(r.Accent, 0.2))) g.FillRectangle(b, cx, by, x + fw - cx, 4);
                        g.Restore(state);
                    }
                    else if (fw > 0)
                        using (var fill = new SolidBrush(r.Muted ? Theme.Darken(r.Accent, 0.45) : r.Accent)) using (var fp = UiKit.Round(new Rectangle(x, by, Math.Max(fw, 4), 4), 2)) g.FillPath(fill, fp);
                }
                if (valueW > 0)
                    UiText.DrawText(g, r.Value, Theme.Numeric, new Rectangle(Width - valueW - 12, rc.Y, valueW, rh), r.Muted ? Theme.TextMuted : r.Accent,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        private static int ParseCount(string v) => int.TryParse(new string(v.TakeWhile(char.IsDigit).ToArray()), out int n) ? n : 0;

        protected override void Dispose(bool disposing) { if (disposing) _tip.Dispose(); base.Dispose(disposing); }
    }

    public static class UiKit
    {
        /// <summary>FEAT-08b: окно не больше рабочей области своего экрана и целиком на нём (с учётом панели задач).
        /// Вызывать в конструкторе после Size/MinimumSize: размеры ужимаются сразу, положение — при показе.</summary>
        public static void FitToScreen(Form f)
        {
            void Clamp()
            {
                var wa = (f.IsHandleCreated ? Screen.FromControl(f) : Screen.FromPoint(Cursor.Position)).WorkingArea;
                f.MinimumSize = new Size(Math.Min(f.MinimumSize.Width, wa.Width), Math.Min(f.MinimumSize.Height, wa.Height));
                if (f.Width > wa.Width || f.Height > wa.Height) f.Size = new Size(Math.Min(f.Width, wa.Width), Math.Min(f.Height, wa.Height));
                if (f.IsHandleCreated && f.WindowState == FormWindowState.Normal)
                    f.Location = new Point(Math.Max(wa.Left, Math.Min(f.Left, wa.Right - f.Width)), Math.Max(wa.Top, Math.Min(f.Top, wa.Bottom - f.Height)));
            }
            try { Clamp(); f.Load += (_, _) => { try { Clamp(); } catch { } }; } catch { }
        }

        public static readonly Font Small = new("Segoe UI", 8.75f);
        public static readonly Font Tiny = new("Segoe UI", 8.5f);
        public static readonly Font SectionFont = Theme.Heading;   // DESIGN-01: заголовки блоков — Bahnschrift, цвет текста (акцент — только для действий)
        public static readonly Font HeaderCaps = new("Segoe UI Semibold", 8.5f);
        public static readonly Font Search = new("Segoe UI", 11f);
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath(); int d = radius * 2;
            if (r.Width < d || r.Height < d) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        /// <summary>Заголовок блока: акцентный текст + необязательная серая подпись справа.</summary>
        public static Control Section(string title, string hint = "")
        {
            var l = new Label { AutoSize = false, Height = 28, Margin = new Padding(0, 12, 0, 4), ForeColor = Theme.Text, Font = SectionFont, Text = title, Tag = hint, TextAlign = ContentAlignment.MiddleLeft };
            l.Paint += (s, e) =>
            {
                if (l.Tag is string h && h.Length > 0)
                    UiText.DrawText(e.Graphics, h, Small, new Rectangle(0, 0, l.Width, l.Height), Theme.TextMuted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            };
            return l;
        }

        /// <summary>Плашка-статус (скруглённый прямоугольник с текстом).</summary>
        public static Label Pill(string text, Color color)
        {
            var l = new Label { AutoSize = false, Height = 30, Margin = new Padding(0, 6, 0, 8), Text = text, ForeColor = Color.White, Font = Theme.UiBold, Tag = color, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 8, 0) };
            l.BackColor = Color.Transparent;
            l.Paint += PaintPill;
            return l;
        }

        private static void PaintPill(object? sender, PaintEventArgs e)
        {
            if (sender is not Label l || l.Tag is not Color c) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(l.Parent?.BackColor ?? Theme.Surface);
            using var br = new SolidBrush(Theme.Darken(c, 0.35));
            using var path = Round(new Rectangle(0, 0, l.Width - 1, l.Height - 1), 8);
            e.Graphics.FillPath(br, path);
            using var bar = new SolidBrush(c);
            e.Graphics.FillRectangle(bar, new Rectangle(0, 6, 4, l.Height - 12));
            UiText.DrawText(e.Graphics, l.Text, l.Font, new Rectangle(12, 0, l.Width - 16, l.Height), Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        public static void SetPill(Label pill, string text, Color color) { pill.Text = text; pill.Tag = color; pill.Invalidate(); }

        /// <summary>Мелкая серая сноска.</summary>
        public static Label Note(string text) => new() { AutoSize = false, Height = 36, ForeColor = Theme.TextMuted, Font = Tiny, Text = text, Margin = new Padding(0, 2, 0, 6) };

        public static Image? FishIcon(uint? fishId) => fishId.HasValue ? IconLoader.Load("assets/fish_icons/fish_" + fishId.Value.ToString(CultureInfo.InvariantCulture) + ".png") : null;
        public static Image? BaitIcon(long? tid) => tid.HasValue && tid.Value > 0 ? IconLoader.Load("assets/bait_icons/item_" + tid.Value.ToString(CultureInfo.InvariantCulture) + ".png") : null;

        /// <summary>Класс улова по windex (цвета как в истории поимок).</summary>
        public static Color GradeColor(int windex) => GradeBadge.ColorOf(windex) is Color c ? c : Theme.TextMuted;

        /// <summary>Вертикальный стек, где все дочерние элементы растягиваются по ширине.</summary>
        public static FlowLayoutPanel Stack(Padding padding)
        {
            var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = padding, BackColor = Theme.Surface };
            void Fit()
            {
                int w = f.ClientSize.Width - f.Padding.Horizontal - (f.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0) - 2;
                foreach (Control c in f.Controls) if (c.Width != w && w > 50) c.Width = w;
            }
            f.Resize += (s, e) => Fit();
            f.ControlAdded += (s, e) => Fit();
            f.Layout += (s, e) => Fit();
            return f;
        }

        public static string Grams(int g) => g >= 1000 ? (g / 1000.0).ToString("0.##", CultureInfo.GetCultureInfo("ru-RU")) + " кг" : g + " г";

        public static string Casts(int n)
        {
            int m10 = n % 10, m100 = n % 100;
            string w = m10 == 1 && m100 != 11 ? "заброс" : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? "заброса" : "забросов";
            return n + " " + w;
        }

        /// <summary>DESIGN-02: «улов / улова / уловов» (без числа).</summary>
        public static string Catches(int n)
        {
            int m10 = n % 10, m100 = n % 100;
            return m10 == 1 && m100 != 11 ? "улов" : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? "улова" : "уловов";
        }
    }

    /// <summary>Поиск вида по названию + список с иконками и разделами («Ловили вы» / «Ловят другие игроки» / «Справочник»).</summary>
    public sealed class SpeciesPicker : UserControl
    {
        public sealed class Item
        {
            public uint Id; public string Name = ""; public string Subtitle = ""; public string Section = "";
            public bool Header;
            public override string ToString() => Header ? Section : Name;
        }

        private readonly TextBox _search = new() { Dock = DockStyle.Top, BorderStyle = BorderStyle.FixedSingle, Font = UiKit.Search };
        private readonly ListBox _list = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawVariable, IntegralHeight = false };
        private List<Item> _all = new();
        private const string Placeholder = "🔍  Найти рыбу…";
        public event Action<uint>? Selected;
        public TextBox SearchBox => _search;
        public ListBox List => _list;
        public uint? SelectedId => _list.SelectedItem is Item { Header: false } it ? it.Id : null;

        public SpeciesPicker()
        {
            BackColor = Theme.Surface;
            Padding = new Padding(10, 10, 6, 10);
            _search.BackColor = Theme.SurfaceAlt; _search.ForeColor = Theme.TextMuted; _search.Text = Placeholder;
            _search.GotFocus += (s, e) => { if (_search.Text == Placeholder) { _search.Text = ""; _search.ForeColor = Theme.Text; } };
            _search.LostFocus += (s, e) => { if (_search.Text.Length == 0) { _search.ForeColor = Theme.TextMuted; _search.Text = Placeholder; } };
            _search.TextChanged += (s, e) => Apply();
            _search.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Down) { _list.Focus(); SelectFirst(); e.Handled = true; }
                if (e.KeyCode == Keys.Enter) { SelectFirst(); e.SuppressKeyPress = true; }
            };
            _list.BackColor = Theme.Surface; _list.ForeColor = Theme.Text;
            _list.MeasureItem += (s, e) => e.ItemHeight = e.Index >= 0 && e.Index < _list.Items.Count && _list.Items[e.Index] is Item { Header: true } ? 30 : 44;
            _list.DrawItem += DrawItem;
            _list.SelectedIndexChanged += (s, e) =>
            {
                if (_list.SelectedItem is Item { Header: true }) { int i = _list.SelectedIndex; if (i + 1 < _list.Items.Count) _list.SelectedIndex = i + 1; return; }
                if (_list.SelectedItem is Item it) Selected?.Invoke(it.Id);
            };
            var gap = new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Theme.Surface };
            Controls.Add(_list); Controls.Add(gap); Controls.Add(_search);
        }

        public void SetItems(IEnumerable<Item> items, uint? keep)
        {
            _all = items.ToList();
            Apply(keep);
        }

        private string Query => _search.Text == Placeholder ? "" : _search.Text.Trim();

        private void Apply(uint? keep = null)
        {
            keep ??= SelectedId;
            string q = ReferenceProvider.Norm(Query);
            var shown = new List<Item>();
            foreach (var grp in _all.Where(i => !i.Header).GroupBy(i => i.Section))
            {
                var hits = grp.Where(i => q.Length == 0 || ReferenceProvider.Norm(i.Name).Contains(q)).ToList();
                if (hits.Count == 0) continue;
                shown.Add(new Item { Header = true, Section = grp.Key + "  ·  " + hits.Count });
                shown.AddRange(hits);
            }
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var i in shown) _list.Items.Add(i);
            _list.EndUpdate();
            if (keep.HasValue)
                for (int i = 0; i < _list.Items.Count; i++) if (_list.Items[i] is Item { Header: false } it && it.Id == keep) { _list.SelectedIndex = i; return; }
            if (q.Length > 0) SelectFirst();
        }

        public void SelectFirst()
        {
            for (int i = 0; i < _list.Items.Count; i++) if (_list.Items[i] is Item { Header: false }) { _list.SelectedIndex = i; return; }
        }

        public bool SelectId(uint id)
        {
            for (int i = 0; i < _list.Items.Count; i++) if (_list.Items[i] is Item { Header: false } it && it.Id == id) { if (_list.SelectedIndex != i) _list.SelectedIndex = i; return true; }
            return false;
        }

        private void DrawItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || _list.Items[e.Index] is not Item it) return;
            var g = e.Graphics;
            if (it.Header)
            {
                using (var bg = new SolidBrush(Theme.Surface)) g.FillRectangle(bg, e.Bounds);
                UiText.DrawText(g, it.Section, Theme.UiBold, new Rectangle(e.Bounds.X + 6, e.Bounds.Y + 7, e.Bounds.Width - 8, 20), Theme.TextMuted, TextFormatFlags.NoPadding);   // DESIGN-01: без капса
                return;
            }
            bool sel = (e.State & DrawItemState.Selected) != 0;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Theme.Surface)) g.FillRectangle(bg, e.Bounds);
            if (sel) using (var sb = new SolidBrush(Theme.Selection)) using (var p = UiKit.Round(new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 2, e.Bounds.Width - 6, e.Bounds.Height - 4), 6)) g.FillPath(sb, p);
            var icon = UiKit.FishIcon(it.Id);
            int x = e.Bounds.X + 8;
            if (icon != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(icon, new Rectangle(x, e.Bounds.Y + 6, 32, 32)); }
            x += 40;
            UiText.DrawText(g, it.Name, Theme.UiBold, new Rectangle(x, e.Bounds.Y + 5, e.Bounds.Right - x - 4, 20), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (it.Subtitle.Length > 0)
                UiText.DrawText(g, it.Subtitle, UiKit.Tiny, new Rectangle(x, e.Bounds.Y + 24, e.Bounds.Right - x - 4, 16), sel ? Theme.Text : Theme.TextMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }
}
