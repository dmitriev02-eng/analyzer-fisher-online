using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace FisherLogger
{
    /// <summary>
    /// DESIGN-01: палитра «ночная вода / эхолот». Тёмная, потому что окно стоит рядом с игрой (часто ночью) и не должно слепить.
    /// Один интерактивный акцент — «сонар» (кнопка-действие, выбранное, ссылки); цвета классов рыбы (зелёный/синий/оранжевый)
    /// заданы игрой и используются только для класса. Цифры и заголовки — Bahnschrift (как показания прибора), текст — Segoe UI.
    /// Только внешний вид; логика приложения не затрагивается.
    /// </summary>
    public static class Theme
    {
        // Фон и поверхности (от глубины к поверхности)
        public static readonly Color Background = Color.FromArgb(11, 20, 22);     // Ink — фон окна
        public static readonly Color Surface = Color.FromArgb(17, 30, 33);        // Hull — панели
        public static readonly Color SurfaceAlt = Color.FromArgb(23, 38, 41);     // Deck — строки, поднятые блоки
        public static readonly Color HeaderBar = Color.FromArgb(27, 46, 50);
        public static readonly Color Border = Color.FromArgb(36, 58, 62);
        public static readonly Color Console = Color.FromArgb(8, 16, 18);
        public static readonly Color Selection = Color.FromArgb(25, 81, 79);

        // Текст
        public static readonly Color Text = Color.FromArgb(229, 237, 235);
        public static readonly Color TextMuted = Color.FromArgb(142, 164, 161);
        public static readonly Color TextOnAccent = Color.FromArgb(6, 32, 29);

        // Акценты
        public static readonly Color Accent = Color.FromArgb(76, 201, 191);      // «сонар» — единственный интерактивный акцент
        public static readonly Color AccentTeal = Color.FromArgb(47, 181, 168);   // данные (полосы, зоны «мало»)
        public static readonly Color AccentGreen = Color.FromArgb(127, 203, 143); // «хорошо», доли успеха, мои уловы
        public static readonly Color Others = Color.FromArgb(90, 169, 230);       // другие игроки (серия на графиках)
        public static readonly Color Warning = Color.FromArgb(237, 184, 78);
        public static readonly Color Danger = Color.FromArgb(226, 96, 107);
        public static readonly Color ConsoleText = Color.FromArgb(150, 196, 182);

        // Кнопки: одна главная (сонар, тёмный текст) на окно, остальные — спокойные
        public static readonly Color ButtonPrimary = Accent;
        public static readonly Color ButtonSecondary = Color.FromArgb(30, 58, 61);
        public static readonly Color ButtonNeutral = Color.FromArgb(24, 44, 48);
        public static readonly Color ButtonWarn = Color.FromArgb(90, 52, 38);
        public static readonly Color ButtonMap = ButtonSecondary;

        // Шрифты: Bahnschrift — заголовки и цифры, Segoe UI — текст
        private static bool Has(string family)
        {
            try { using var f = new FontFamily(family); return true; } catch { return false; }
        }
        private static readonly string Display = Has("Bahnschrift") ? "Bahnschrift" : "Segoe UI";
        private static readonly string DisplaySemi = Has("Bahnschrift SemiBold") ? "Bahnschrift SemiBold" : "Segoe UI Semibold";
        public static readonly Font Title = new Font(DisplaySemi, 17f, FontStyle.Regular);
        public static readonly Font Heading = new Font(DisplaySemi, 12.5f, FontStyle.Regular);
        public static readonly Font Numeric = new Font(DisplaySemi, 10.5f, FontStyle.Regular);
        public static readonly Font NumericLarge = new Font(DisplaySemi, 20f, FontStyle.Regular);
        public static readonly Font Status = new Font(Display, 12f, FontStyle.Regular);
        public static readonly Font Ui = new Font("Segoe UI", 10f, FontStyle.Regular);
        public static readonly Font UiBold = new Font("Segoe UI Semibold", 10f, FontStyle.Regular);
        public static readonly Font ButtonFont = new Font("Segoe UI Semibold", 10f, FontStyle.Regular);
        public static readonly Font Grid = new Font("Segoe UI", 9.75f, FontStyle.Regular);
        public static readonly Font GridHeader = new Font("Segoe UI Semibold", 9.25f, FontStyle.Regular);
        public static readonly Font Mono = new Font("Consolas", 9.75f, FontStyle.Regular);

        /// <summary>Тёмный или светлый текст — что контрастнее на этом фоне.</summary>
        public static Color TextOn(Color bg) => bg.R * 0.299 + bg.G * 0.587 + bg.B * 0.114 > 150 ? TextOnAccent : Text;

        public static Color Lighten(Color c, double f) => Color.FromArgb(c.A,
            (int)Math.Min(255, c.R + (255 - c.R) * f), (int)Math.Min(255, c.G + (255 - c.G) * f), (int)Math.Min(255, c.B + (255 - c.B) * f));
        public static Color Darken(Color c, double f) => Color.FromArgb(c.A,
            (int)(c.R * (1 - f)), (int)(c.G * (1 - f)), (int)(c.B * (1 - f)));

        private static Icon? _appIcon;
        private static bool _appIconTried;

        /// <summary>Иконка приложения (встроенный ресурс app.ico); null, если недоступна.</summary>
        public static Icon? AppIcon
        {
            get
            {
                if (_appIconTried) return _appIcon;
                _appIconTried = true;
                try
                {
                    using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("FisherLogger.app.ico");
                    if (s != null) _appIcon = new Icon(s);
                }
                catch { _appIcon = null; }
                return _appIcon;
            }
        }

        public static void StyleButton(Button b, Color background)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = background;
            b.ForeColor = TextOn(background);
            b.Font = ButtonFont;
            b.FlatAppearance.MouseOverBackColor = Lighten(background, 0.14);
            b.FlatAppearance.MouseDownBackColor = Darken(background, 0.18);
            b.Cursor = Cursors.Hand;
            b.Margin = new Padding(6, 5, 6, 5);
            b.UseVisualStyleBackColor = false;
        }

        public static void StyleGrid(DataGridView g)
        {
            g.BackgroundColor = Surface;
            g.BorderStyle = BorderStyle.None;
            g.GridColor = Border;
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            g.ColumnHeadersDefaultCellStyle.BackColor = HeaderBar;
            g.ColumnHeadersDefaultCellStyle.ForeColor = TextMuted;
            g.ColumnHeadersDefaultCellStyle.Font = GridHeader;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = HeaderBar;
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            g.ColumnHeadersHeight = 34;
            g.DefaultCellStyle.BackColor = Surface;
            g.DefaultCellStyle.ForeColor = Text;
            g.DefaultCellStyle.Font = Grid;
            g.DefaultCellStyle.SelectionBackColor = Selection;
            g.DefaultCellStyle.SelectionForeColor = Color.White;
            g.DefaultCellStyle.Padding = new Padding(3, 0, 3, 0);
            g.AlternatingRowsDefaultCellStyle.BackColor = SurfaceAlt;
            g.AlternatingRowsDefaultCellStyle.ForeColor = Text;
        }

        /// <summary>DESIGN-01: тёмный выпадающий список. Системный ComboBox рисует поле белым, поэтому список «только выбор»
        /// рисуется сам (фон Deck, выбранный пункт — Selection). Списки со своим рисованием не трогаются.</summary>
        public static void DarkCombo(ComboBox cb)
        {
            cb.FlatStyle = FlatStyle.Flat; cb.BackColor = SurfaceAlt; cb.ForeColor = Text;
            if (cb.DrawMode != DrawMode.Normal || cb.DropDownStyle != ComboBoxStyle.DropDownList || cb.Tag is "dark") return;
            cb.Tag ??= "dark";
            cb.DrawMode = DrawMode.OwnerDrawFixed;
            cb.ItemHeight = Math.Max(cb.ItemHeight, cb.Font.Height + 4);
            cb.DrawItem += (s, e) =>
            {
                bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0, sel = (e.State & DrawItemState.Selected) != 0;
                using (var bg = new SolidBrush(sel && !edit ? Selection : SurfaceAlt)) e.Graphics.FillRectangle(bg, e.Bounds);
                if (e.Index >= 0)
                    UiText.DrawText(e.Graphics, cb.GetItemText(cb.Items[e.Index]), cb.Font, new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height),
                        cb.Enabled ? Text : TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
        }

        /// <summary>DESIGN-01: только поля ввода (списки, числа, текст, флажки) — безопасно для любого окна; вызывается при показе окна.</summary>
        public static void ApplyInputs(Control root)
        {
            foreach (Control c in root.Controls)
            {
                switch (c)
                {
                    case ComboBox cb: DarkCombo(cb); break;
                    case NumericUpDown nu: nu.BackColor = SurfaceAlt; nu.ForeColor = Text; nu.BorderStyle = BorderStyle.FixedSingle; break;
                    case TextBox tb when !tb.Multiline && tb.BackColor == SystemColors.Window: tb.BackColor = SurfaceAlt; tb.ForeColor = Text; tb.BorderStyle = BorderStyle.FixedSingle; break;
                    case CheckBox chk when chk.ForeColor == SystemColors.ControlText: chk.ForeColor = Text; break;
                    case RadioButton rb when rb.ForeColor == SystemColors.ControlText: rb.ForeColor = Text; break;
                }
                if (c.HasChildren) ApplyInputs(c);
            }
        }

        /// <summary>Рекурсивно применяет оформление к кнопкам, таблицам, группам и вкладкам.</summary>
        public static void ApplyTree(Control root)
        {
            foreach (Control c in root.Controls)
            {
                switch (c)
                {
                    case Button b when b.FlatStyle == FlatStyle.Flat:
                        b.FlatAppearance.BorderSize = 0;
                        if (b.ForeColor == Color.White || b.ForeColor == Text || b.ForeColor == TextOnAccent) b.ForeColor = TextOn(b.BackColor);
                        b.FlatAppearance.MouseOverBackColor = Lighten(b.BackColor, 0.14);
                        b.FlatAppearance.MouseDownBackColor = Darken(b.BackColor, 0.18);
                        b.Cursor = Cursors.Hand;
                        if (b.Font.Size >= 10f) b.Font = ButtonFont;
                        break;
                    case DataGridView g:
                        StyleGrid(g);
                        break;
                    case GroupBox gb:
                        gb.ForeColor = TextMuted;
                        gb.Font = UiBold;
                        break;
                    case Label lb when lb.ForeColor == Color.White:
                        lb.ForeColor = Text;
                        break;
                    case TabPage tp:
                        tp.BackColor = Background;
                        tp.ForeColor = Text;
                        break;
                    case ListBox lb:
                        lb.BorderStyle = BorderStyle.None;
                        break;
                    case TextBox tb when tb.Multiline:
                        tb.BorderStyle = BorderStyle.None;
                        break;
                    // DESIGN-01: поля ввода — тёмные, как остальное окно (раньше белые системные)
                    case TextBox tb1:
                        if (tb1.BackColor == SystemColors.Window) { tb1.BackColor = SurfaceAlt; tb1.ForeColor = Text; }
                        tb1.BorderStyle = BorderStyle.FixedSingle;
                        break;
                    case ComboBox cb:
                        DarkCombo(cb);
                        break;
                    case NumericUpDown nu:
                        nu.BackColor = SurfaceAlt; nu.ForeColor = Text; nu.BorderStyle = BorderStyle.FixedSingle;
                        break;
                    case CheckBox chk when chk.ForeColor == SystemColors.ControlText || chk.ForeColor == Color.White:
                        chk.ForeColor = Text;
                        break;
                    case RadioButton rb when rb.ForeColor == SystemColors.ControlText || rb.ForeColor == Color.White:
                        rb.ForeColor = Text;
                        break;
                }
                if (c.HasChildren) ApplyTree(c);
            }
        }
    }
}
