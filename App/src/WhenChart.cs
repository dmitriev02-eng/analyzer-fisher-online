using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace FisherLogger
{
    /// <summary>
    /// BRIDGE-FB-01: «Когда» в карточке рыбы — схема по 24 игровым часам: доля записей вида в каждом часе,
    /// отдельно ваши уловы (зелёный) и объявления других игроков (синий), всё время, все водоёмы.
    /// Доли, а не штуки, — чтобы серии разного размера были сравнимы. Серым — час, где ваших забросов мало
    /// («мало данных», а не «не ловится»). Тот же расчёт, что вкладка «По часам» (HourStats); то же на телефоне (WhenK).
    /// Частота записей, не вероятность клёва.
    /// </summary>
    public sealed class WhenChart : Control
    {
        public HourReport? Mine { get; private set; }
        public HourReport? Pub { get; private set; }
        public string BestText { get; private set; } = "";
        public string BestFrom { get; private set; } = "";
        private readonly ToolTip _tip = new() { InitialDelay = 150 };
        private int _hover = -1;
        private Rectangle _plot;

        public WhenChart()
        {
            DoubleBuffered = true; BackColor = Theme.SurfaceAlt; ForeColor = Theme.Text; Font = UiKit.Small;
            Height = 240; Margin = new Padding(0, 0, 0, 6); Cursor = Cursors.Hand;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        public static (string text, string from) Best(HourReport? m, HourReport? p)
        {
            static string Hrs(HourReport r) => string.Join(", ", r.BestHours.OrderBy(h => h).Select(h => h.ToString("00", CultureInfo.InvariantCulture) + ":00"));
            if (m != null && m.Level == "ok" && m.BestHours.Count > 0) return (Hrs(m), "по вашим уловам");
            if (p != null && p.Level == "ok" && p.BestHours.Count > 0) return (Hrs(p), "по объявлениям других игроков");
            return ("", "");
        }

        public void SetData(HourReport? mine, HourReport? pub)
        {
            Mine = mine; Pub = pub;
            (BestText, BestFrom) = Best(mine, pub);
            Invalidate();
        }

        private int HourAt(int x) => _plot.Width <= 0 || x < _plot.Left || x >= _plot.Right ? -1 : Math.Clamp((x - _plot.Left) * 24 / _plot.Width, 0, 23);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HourAt(e.X);
            if (h == _hover) return;
            _hover = h; Invalidate();
            if (h < 0 || (Mine == null && Pub == null)) { _tip.Hide(this); return; }
            string L(HourReport? r, string who) => r == null || r.Total == 0 ? "" :
                $"{who}: {r.Hours[h].N} из {r.Total} ({100.0 * r.Hours[h].N / r.Total:0}%)" + (r.Hours[h].Low ? " — " + r.LowText : "") + "\n";
            _tip.Show($"{h:00}:00–{h:00}:59\n" + L(Mine, "мои") + L(Pub, "другие игроки") + "нажмите — вкладка «По часам»", this, e.X + 14, e.Y + 14, 8000);
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; _tip.Hide(this); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            using var txt = new SolidBrush(Theme.TextMuted);
            int mt = Mine?.Total ?? 0, pt = Pub?.Total ?? 0;
            if (mt == 0 && pt == 0) { g.DrawStringT("Записей с игровым временем для этой рыбы пока нет.", Font, txt, 10, 10); return; }
            _plot = new Rectangle(10, 22, Width - 20, Height - 106);
            if (_plot.Width < 100 || _plot.Height < 40) return;
            float w = _plot.Width / 24f;
            using (var band = new SolidBrush(Color.FromArgb(70, Theme.Background)))
                foreach (var (h0, name) in new[] { (0, "ночь"), (6, "утро"), (12, "день"), (18, "вечер") })
                {
                    if (h0 % 12 == 0) g.FillRectangle(band, _plot.Left + h0 * w, _plot.Top - 18, 6 * w, _plot.Height + 18);
                    g.DrawStringT(name, Font, txt, _plot.Left + h0 * w + 3, 3);
                }
            double sm = Math.Max(1, mt), sp = Math.Max(1, pt);
            double maxShare = Math.Max(0.01, Math.Max(Mine?.Hours.Max(b => b.N / sm) ?? 0, Pub?.Hours.Max(b => b.N / sp) ?? 0));
            using (var axis = new Pen(Color.FromArgb(70, Theme.Text))) g.DrawLine(axis, _plot.Left, _plot.Bottom, _plot.Right, _plot.Bottom);
            var bestM = new HashSet<int>(Mine?.BestHours ?? new List<int>());
            var bestP = new HashSet<int>(Pub?.BestHours ?? new List<int>());
            for (int h = 0; h < 24; h++)
            {
                float x = _plot.Left + h * w;
                if (h == _hover) using (var hb = new SolidBrush(Color.FromArgb(35, Theme.Accent))) g.FillRectangle(hb, x, _plot.Top - 18, w, _plot.Height + 18);
                if (Mine != null && mt > 0)
                {
                    var b = Mine.Hours[h]; int bh = (int)(b.N / sm / maxShare * _plot.Height);
                    var c = b.Low ? Color.FromArgb(110, Theme.TextMuted) : bestM.Contains(h) ? Theme.Lighten(Theme.AccentGreen, 0.3) : Theme.AccentGreen;
                    if (bh > 0) using (var br = new SolidBrush(c)) g.FillRectangle(br, x + w * 0.08f, _plot.Bottom - bh, w * 0.40f, bh);
                }
                if (Pub != null && pt > 0)
                {
                    var b = Pub.Hours[h]; int bh = (int)(b.N / sp / maxShare * _plot.Height);
                    var c = bestP.Contains(h) ? Theme.Lighten(Theme.Others, 0.3) : Color.FromArgb(230, Theme.Others);
                    if (bh > 0) using (var br = new SolidBrush(c)) g.FillRectangle(br, x + w * 0.52f, _plot.Bottom - bh, w * 0.40f, bh);
                }
                if (h % 3 == 0) g.DrawStringT(h.ToString("00", CultureInfo.InvariantCulture), Font, txt, x, _plot.Bottom + 2);
            }
            int ly = _plot.Bottom + 18;
            using (var gb = new SolidBrush(Theme.AccentGreen)) g.FillRectangle(gb, _plot.Left, ly + 4, 10, 10);
            g.DrawStringT($"мои уловы: {mt}", Font, txt, _plot.Left + 14, ly + 1);
            using (var bb = new SolidBrush(Theme.Others)) g.FillRectangle(bb, _plot.Left + 130, ly + 4, 10, 10);
            g.DrawStringT($"другие игроки: {pt}", Font, txt, _plot.Left + 144, ly + 1);
            // вывод — отдельно по каждой серии, цветом серии; при нехватке данных так и сказано
            int y = ly + 18;
            foreach (var (r, who, col) in new[] { (Mine, "Ваши уловы", Theme.AccentGreen), (Pub, "Другие игроки", Theme.Others) })
            {
                if (r == null || r.Total == 0) continue;
                bool okr = r.Level == "ok" && r.BestHours.Count > 0;
                string t = okr ? who + ": чаще всего " + string.Join(", ", r.BestHours.OrderBy(h => h).Select(h => h.ToString("00", CultureInfo.InvariantCulture) + ":00"))
                               : who + ": мало данных для вывода (" + r.Total + " зап.)";
                using var bl = new SolidBrush(okr ? col : Theme.Warning);
                g.DrawStringT(t, UiKit.SectionFont, bl, _plot.Left, y); y += 20;
            }
        }
    }
}
