using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace FisherLogger
{
    /// <summary>RELEASE-03: язык интерфейса (ru/en). Тексты в коде остаются русскими; на английском они подменяются только при показе
    /// (подписи, меню, таблицы, сообщения, рисованный текст). Логика и данные не меняются. Словарь — resources\ui_en.json (встроен).</summary>
    public static class UiText
    {
        public static string Lang { get; private set; } = "ru";
        public static bool En => Lang == "en";
        public static string? PathOverride { get; set; }
        public static string FilePath => PathOverride ?? Path.Combine(AppInfo.UserDir, "language.json");
        private static Dictionary<string, string> _map = new(StringComparer.Ordinal);
        private static Regex? _frag;
        private static readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
        private static readonly Regex Cyr = new("[А-Яа-яЁё]", RegexOptions.Compiled);

        private sealed class LangFile { public string Lang { get; set; } = "ru"; }

        /// <summary>Выбран ли язык (первый запуск — нет).</summary>
        public static bool IsChosen => File.Exists(FilePath);

        public static void Load()
        {
            try { if (File.Exists(FilePath)) Lang = JsonSerializer.Deserialize<LangFile>(File.ReadAllText(FilePath))?.Lang == "en" ? "en" : "ru"; } catch { Lang = "ru"; }
            if (En) LoadMap();
        }

        public static void Set(string lang)
        {
            Lang = lang == "en" ? "en" : "ru";
            try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, JsonSerializer.Serialize(new LangFile { Lang = Lang })); } catch { }
            _cache.Clear();
            if (En) LoadMap();
        }

        /// <summary>Только для тестов: свой словарь.</summary>
        public static void UseMap(Dictionary<string, string> map, string lang = "en") { Lang = lang; SetMap(map); }

        private static void LoadMap()
        {
            try
            {
                using var s = typeof(UiText).Assembly.GetManifestResourceStream("FisherLogger.resources.ui_en.json");
                if (s == null) return;
                var m = JsonSerializer.Deserialize<Dictionary<string, string>>(s);
                if (m != null) SetMap(m);
            }
            catch { }
        }

        private static void SetMap(Dictionary<string, string> m)
        {
            _map = new Dictionary<string, string>(m.Where(kv => !string.IsNullOrEmpty(kv.Key) && kv.Value != null), StringComparer.Ordinal);
            _cache.Clear();
            // фрагменты для составных строк: длинные раньше коротких; совсем короткие (1–2 буквы) не подставляем
            var keys = _map.Keys.Where(k => k.Trim().Length >= 3 && Cyr.IsMatch(k)).OrderByDescending(k => k.Length).Select(Regex.Escape).ToList();
            _frag = keys.Count == 0 ? null : new Regex(string.Join("|", keys), RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }

        /// <summary>Текст для показа: на русском — как есть; на английском — целиком из словаря, иначе по известным кускам.</summary>
        public static string T(string? s)
        {
            if (string.IsNullOrEmpty(s) || !En || !Cyr.IsMatch(s)) return s ?? "";
            if (_cache.TryGetValue(s, out var c)) return c;
            string r;
            if (_map.TryGetValue(s, out var exact)) r = exact;
            else if (_frag != null) r = _frag.Replace(s, m => _map.TryGetValue(m.Value, out var v) ? v : m.Value);
            else r = s;
            if (_cache.Count > 50000) _cache.Clear();
            _cache[s] = r;
            return r;
        }

        // ---- сообщения ----
        public static DialogResult Box(string text) => MessageBox.Show(T(text));
        public static DialogResult Box(string text, string caption) => MessageBox.Show(T(text), T(caption));
        public static DialogResult Box(string text, string caption, MessageBoxButtons b) => MessageBox.Show(T(text), T(caption), b);
        public static DialogResult Box(string text, string caption, MessageBoxButtons b, MessageBoxIcon i) => MessageBox.Show(T(text), T(caption), b, i);
        public static DialogResult Box(string text, string caption, MessageBoxButtons b, MessageBoxIcon i, MessageBoxDefaultButton d) => MessageBox.Show(T(text), T(caption), b, i, d);
        public static DialogResult Box(IWin32Window? o, string text) => MessageBox.Show(o, T(text));
        public static DialogResult Box(IWin32Window? o, string text, string caption) => MessageBox.Show(o, T(text), T(caption));
        public static DialogResult Box(IWin32Window? o, string text, string caption, MessageBoxButtons b) => MessageBox.Show(o, T(text), T(caption), b);
        public static DialogResult Box(IWin32Window? o, string text, string caption, MessageBoxButtons b, MessageBoxIcon i) => MessageBox.Show(o, T(text), T(caption), b, i);
        public static DialogResult Box(IWin32Window? o, string text, string caption, MessageBoxButtons b, MessageBoxIcon i, MessageBoxDefaultButton d) => MessageBox.Show(o, T(text), T(caption), b, i, d);

        // ---- TextRenderer ----
        public static void DrawText(IDeviceContext dc, string? t, Font? f, Point p, Color c) => TextRenderer.DrawText(dc, T(t), f, p, c);
        public static void DrawText(IDeviceContext dc, string? t, Font? f, Rectangle r, Color c) => TextRenderer.DrawText(dc, T(t), f, r, c);
        public static void DrawText(IDeviceContext dc, string? t, Font? f, Point p, Color c, TextFormatFlags fl) => TextRenderer.DrawText(dc, T(t), f, p, c, fl);
        public static void DrawText(IDeviceContext dc, string? t, Font? f, Rectangle r, Color c, TextFormatFlags fl) => TextRenderer.DrawText(dc, T(t), f, r, c, fl);
        public static void DrawText(IDeviceContext dc, string? t, Font? f, Point p, Color c, Color b) => TextRenderer.DrawText(dc, T(t), f, p, c, b);
        public static void DrawText(IDeviceContext dc, string? t, Font? f, Rectangle r, Color c, Color b) => TextRenderer.DrawText(dc, T(t), f, r, c, b);
        public static void DrawText(IDeviceContext dc, string? t, Font? f, Point p, Color c, Color b, TextFormatFlags fl) => TextRenderer.DrawText(dc, T(t), f, p, c, b, fl);
        public static void DrawText(IDeviceContext dc, string? t, Font? f, Rectangle r, Color c, Color b, TextFormatFlags fl) => TextRenderer.DrawText(dc, T(t), f, r, c, b, fl);
        public static Size MeasureText(string? t, Font? f) => TextRenderer.MeasureText(T(t), f);
        public static Size MeasureText(string? t, Font? f, Size s) => TextRenderer.MeasureText(T(t), f, s);
        public static Size MeasureText(string? t, Font? f, Size s, TextFormatFlags fl) => TextRenderer.MeasureText(T(t), f, s, fl);
        public static Size MeasureText(IDeviceContext dc, string? t, Font? f) => TextRenderer.MeasureText(dc, T(t), f);
        public static Size MeasureText(IDeviceContext dc, string? t, Font? f, Size s) => TextRenderer.MeasureText(dc, T(t), f, s);
        public static Size MeasureText(IDeviceContext dc, string? t, Font? f, Size s, TextFormatFlags fl) => TextRenderer.MeasureText(dc, T(t), f, s, fl);
    }

    /// <summary>RELEASE-03: Graphics.DrawString/MeasureString с переводом (в коде заменены на *T).</summary>
    public static class UiTextGraphics
    {
        public static void DrawStringT(this Graphics g, string? s, Font f, Brush b, PointF p) => g.DrawString(UiText.T(s), f, b, p);
        public static void DrawStringT(this Graphics g, string? s, Font f, Brush b, float x, float y) => g.DrawString(UiText.T(s), f, b, x, y);
        public static void DrawStringT(this Graphics g, string? s, Font f, Brush b, RectangleF r) => g.DrawString(UiText.T(s), f, b, r);
        public static void DrawStringT(this Graphics g, string? s, Font f, Brush b, PointF p, StringFormat? sf) => g.DrawString(UiText.T(s), f, b, p, sf);
        public static void DrawStringT(this Graphics g, string? s, Font f, Brush b, float x, float y, StringFormat? sf) => g.DrawString(UiText.T(s), f, b, x, y, sf);
        public static void DrawStringT(this Graphics g, string? s, Font f, Brush b, RectangleF r, StringFormat? sf) => g.DrawString(UiText.T(s), f, b, r, sf);
        public static SizeF MeasureStringT(this Graphics g, string? s, Font f) => g.MeasureString(UiText.T(s), f);
        public static SizeF MeasureStringT(this Graphics g, string? s, Font f, int w) => g.MeasureString(UiText.T(s), f, w);
        public static SizeF MeasureStringT(this Graphics g, string? s, Font f, SizeF a) => g.MeasureString(UiText.T(s), f, a);
        public static SizeF MeasureStringT(this Graphics g, string? s, Font f, int w, StringFormat? sf) => g.MeasureString(UiText.T(s), f, w, sf);
        public static SizeF MeasureStringT(this Graphics g, string? s, Font f, PointF o, StringFormat? sf) => g.MeasureString(UiText.T(s), f, o, sf);
        public static SizeF MeasureStringT(this Graphics g, string? s, Font f, SizeF a, StringFormat? sf) => g.MeasureString(UiText.T(s), f, a, sf);
        public static void SetToolTipT(this ToolTip tt, Control c, string? s) => tt.SetToolTip(c, UiText.T(s));
    }

    /// <summary>RELEASE-03: раз в 0,4 с переводит видимые подписи всех окон программы и всплывающих меню (только на английском).
    /// Значения в списках и ячейках таблиц не меняются — подменяется только показ (события Format/CellFormatting).</summary>
    public static class UiTranslator
    {
        private static readonly ConditionalWeakTable<object, string> _seen = new();
        private static readonly ConditionalWeakTable<object, object> _hooked = new();
        private static System.Windows.Forms.Timer? _timer;

        public static void Start()
        {
            if (!UiText.En || _timer != null) return;
            _timer = new System.Windows.Forms.Timer { Interval = 400 };
            _timer.Tick += (s, e) => { try { Pass(); } catch { } };
            _timer.Start();
        }

        public static void Pass()
        {
            foreach (Form f in Application.OpenForms.Cast<Form>().ToList()) Translate(f);
            foreach (var c in ThreadPopups()) Translate(c);
        }

        private static void Fix(object o, Func<string?> get, Action<string> set)
        {
            string? cur = get();
            if (string.IsNullOrEmpty(cur)) return;
            if (_seen.TryGetValue(o, out var last) && last == cur) return;
            string t = UiText.T(cur);
            if (t != cur) set(t);
            _seen.AddOrUpdate(o, t);
        }

        public static void Translate(Control c)
        {
            if (c is not TextBoxBase && c is not NumericUpDown && c is not DateTimePicker) Fix(c, () => c.Text, v => c.Text = v);
            switch (c)
            {
                case ToolStrip ts: foreach (ToolStripItem it in ts.Items) Item(it); break;
                case DataGridView g:
                    foreach (DataGridViewColumn col in g.Columns) Fix(col, () => col.HeaderText, v => col.HeaderText = v);
                    if (!_hooked.TryGetValue(g, out _)) { _hooked.Add(g, g); g.CellFormatting += (s, e) => { if (e.Value is string sv && UiText.En) { e.Value = UiText.T(sv); e.FormattingApplied = false; } }; g.Invalidate(); }
                    break;
                case ComboBox cb:
                    if (!_hooked.TryGetValue(cb, out _)) { _hooked.Add(cb, cb); cb.FormattingEnabled = true; cb.Format += (s, e) => { if (e.Value is string sv) e.Value = UiText.T(sv); else if (e.Value != null) e.Value = UiText.T(e.Value.ToString()); }; cb.Invalidate(); }
                    break;
                case ListBox lb:
                    if (!_hooked.TryGetValue(lb, out _)) { _hooked.Add(lb, lb); lb.FormattingEnabled = true; lb.Format += (s, e) => { if (e.Value != null) e.Value = UiText.T(e.Value.ToString()); }; lb.Invalidate(); }
                    break;
                case ListView lv:
                    foreach (ColumnHeader h in lv.Columns) Fix(h, () => h.Text, v => h.Text = v);
                    break;
                case TabControl tc:
                    foreach (TabPage p in tc.TabPages) Fix(p, () => p.Text, v => p.Text = v);
                    break;
            }
            if (c.ContextMenuStrip != null) foreach (ToolStripItem it in c.ContextMenuStrip.Items) Item(it);
            foreach (Control ch in c.Controls) Translate(ch);
        }

        private static void Item(ToolStripItem it)
        {
            Fix(it, () => it.Text, v => it.Text = v);
            if (!string.IsNullOrEmpty(it.ToolTipText)) { string tt = UiText.T(it.ToolTipText); if (tt != it.ToolTipText) it.ToolTipText = tt; }
            if (it is ToolStripDropDownItem dd && dd.HasDropDownItems) foreach (ToolStripItem s in dd.DropDownItems) Item(s);
        }

        // всплывающие меню (ContextMenuStrip, выпадающие списки меню) — отдельные окна потока, не в OpenForms
        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint tid, EnumProc cb, IntPtr l);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        private static List<Control> ThreadPopups()
        {
            var r = new List<Control>();
            EnumThreadWindows(GetCurrentThreadId(), (h, l) => { var c = Control.FromHandle(h); if (c is ToolStripDropDown && c.Visible) r.Add(c); return true; }, IntPtr.Zero);
            return r;
        }
    }

    /// <summary>RELEASE-03: выбор языка при первом запуске (до главного окна).</summary>
    public sealed class LanguageForm : Form
    {
        public string Choice { get; private set; } = "ru";
        public LanguageForm()
        {
            Text = "Анализатор Fisher Online"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(420, 170); BackColor = Theme.Background; ForeColor = Theme.Text; Font = Theme.Ui;
            try { Icon = Theme.AppIcon; } catch { }
            var lbl = new Label { Text = "Выберите язык / Choose language", AutoSize = false, Dock = DockStyle.Top, Height = 60, TextAlign = ContentAlignment.MiddleCenter };
            var ru = new Button { Text = "Русский", Size = new Size(160, 48), Location = new Point(40, 90) };
            var en = new Button { Text = "English", Size = new Size(160, 48), Location = new Point(220, 90) };
            try { Theme.StyleButton(ru, Theme.ButtonNeutral); Theme.StyleButton(en, Theme.ButtonNeutral); } catch { }
            ru.Click += (s, e) => { Choice = "ru"; DialogResult = DialogResult.OK; };
            en.Click += (s, e) => { Choice = "en"; DialogResult = DialogResult.OK; };
            Controls.AddRange(new Control[] { ru, en, lbl });
        }

        /// <summary>Загрузить язык; на первом запуске установленной копии — спросить.</summary>
        public static void EnsureChosen(bool ask)
        {
            if (!UiText.IsChosen && ask)
            {
                using var f = new LanguageForm();
                UiText.Set(f.ShowDialog() == DialogResult.OK ? f.Choice : "ru");
            }
            UiText.Load();
        }
    }
}
