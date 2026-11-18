using System;
using System.Drawing;
using System.Windows.Forms;

namespace FisherLogger
{
    /// <summary>
    /// OVERLAY-04: ответ ИИ о рыбалке — небольшое всплывающее окно рядом с окном в игре.
    /// Не забирает фокус у игры; живёт 10 с (дольше для длинного ответа, до 30 с), пока курсор над ним — не закрывается; щелчок закрывает.
    /// </summary>
    public sealed class CoachPopup : Form
    {
        private const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        private static CoachPopup? _current;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
        private readonly string _text, _note;
        private int _leftMs;
        private static readonly Font FTitle = new("Segoe UI Semibold", 10f), FBody = new("Segoe UI", 9.5f), FNote = new("Segoe UI", 8f);
        public const int Width0 = 400;
        public int LifeMs { get; }
        public static CoachPopup? Current => _current is { IsDisposed: false } c ? c : null;

        public static int LifeFor(string text) => Math.Clamp(10000 + Math.Max(0, text.Length - 300) * 25, 10000, 30000);

        public static CoachPopup ShowNear(Form owner, string text, string note)
        {
            try { Current?.Close(); } catch { }
            var p = new CoachPopup(text, note, owner.RectangleToScreen(owner.ClientRectangle));
            _current = p; p.Show();
            return p;
        }

        private CoachPopup(string text, string note, Rectangle near)
        {
            _text = text.Trim(); _note = note ?? "";
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;   // поверх — через WS_EX_TOPMOST: свойство TopMost активирует окно при показе
            BackColor = Theme.Surface; ForeColor = Theme.Text; DoubleBuffered = true;   // без Opacity: с ней WinForms активирует окно при показе
            var wa = Screen.FromRectangle(near).WorkingArea;
            int w = Math.Min(Width0, wa.Width - 20);
            // без CreateGraphics: ранний дескриптор окна заставил бы Show() активировать окно (забрать фокус у игры)
            var body = UiText.MeasureText(_text, FBody, new Size(w - 28, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            int h = Math.Min(wa.Height - 40, 34 + body.Height + 22);
            // слева от окна в игре, если есть место; иначе справа; по верхнему краю окна
            int x = near.Left - w - 8 >= wa.Left ? near.Left - w - 8 : Math.Min(wa.Right - w, near.Right + 8);
            int y = Math.Clamp(near.Top, wa.Top, wa.Bottom - h);
            Bounds = new Rectangle(x, y, w, h);
            LifeMs = LifeFor(_text); _leftMs = LifeMs;
            _timer.Tick += (s, e) =>
            {
                if (Bounds.Contains(Cursor.Position)) return;   // читают — не закрываем
                _leftMs -= _timer.Interval; Invalidate(new Rectangle(0, Height - 3, Width, 3));
                if (_leftMs <= 0) { _timer.Stop(); Close(); }
            };
            MouseDown += (s, e) => Close();
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE; return cp; } }
        protected override void OnShown(EventArgs e) { base.OnShown(e); _timer.Start(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 0, 0, 4, Height);
            UiText.DrawText(g, "Совет ИИ о рыбалке", FTitle, new Point(14, 8), Theme.Text, TextFormatFlags.NoPadding);
            if (_note.Length > 0)
            {
                var nsz = UiText.MeasureText(_note, FNote, Size.Empty, TextFormatFlags.NoPadding);
                UiText.DrawText(g, _note, FNote, new Rectangle(Width - Math.Min(nsz.Width, 200) - 12, 10, Math.Min(nsz.Width, 200), 16), Theme.TextMuted, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
            UiText.DrawText(g, _text, FBody, new Rectangle(14, 32, Width - 28, Height - 32 - 18), Theme.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            UiText.DrawText(g, "щелчок — закрыть", FNote, new Point(14, Height - 16), Theme.TextMuted, TextFormatFlags.NoPadding);
            using (var p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            float frac = LifeMs > 0 ? Math.Clamp(_leftMs / (float)LifeMs, 0, 1) : 0;
            using (var b = new SolidBrush(Color.FromArgb(120, Theme.Accent))) g.FillRectangle(b, 4, Height - 3, (Width - 4) * frac, 3);
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { _timer.Dispose(); if (_current == this) _current = null; base.OnFormClosed(e); }
    }
}
