using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FisherLogger
{
    /// <summary>
    /// DESIGN-02 (вариант A): вкладки главного окна. «Сейчас» — часы игры, каждая удочка простыми словами, подсказки тренера;
    /// «Куда идти» — советник (цель, рыба, водоёмы, часы) прямо в окне; «Журнал» — история поимок.
    /// Данные «Сейчас» читаются из БД в фоне раз в 5 с и только пока вкладка видна.
    /// </summary>
    public partial class Form1
    {
        private PageHost? _pages;
        private Panel? _goPage;
        private NowView? _nowView;
        private System.Windows.Forms.Timer? _nowTimer;
        private SoakCurve? _nowCurve; private DateTime _nowCurveAt = DateTime.MinValue; private bool _nowCurveLoading, _nowBusy;
        private DateTime _nowAt = DateTime.MinValue;

        private void StartNowPanel()
        {
            Plain.Tech = _appSettings.Display.ShowTech;
            try { historyGrid.Columns["cast"]!.Visible = Plain.Tech; } catch { }
            _nowTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _nowTimer.Tick += (s, e) => RefreshNow(force: false);
            _nowTimer.Start();
            RefreshNow(force: true);
        }

        private void OnPageChanged()
        {
            if (_pages == null) return;
            if (_pages.SelectedIndex == 0) RefreshNow(force: true);
            if (_pages.SelectedIndex == 1 && (_advisorForm == null || _advisorForm.IsDisposed)) OpenAdvisor();
        }

        /// <summary>Советник как часть вкладки «Куда идти» (без своей рамки).</summary>
        private void EmbedAdvisor(AdvisorForm f)
        {
            if (_goPage == null) { f.Show(this); return; }
            f.TopLevel = false; f.FormBorderStyle = FormBorderStyle.None; f.MinimumSize = Size.Empty; f.Dock = DockStyle.Fill;
            _goPage.Controls.Clear(); _goPage.Controls.Add(f);
            f.Show();
        }

        private void RefreshNow(bool force)
        {
            var view = _nowView;
            if (view == null || _pages == null) return;
            view.Clock = DayParts.Clock(DateTime.Now);           // часы — каждую секунду, без БД
            view.Invalidate();
            if (_suppressPersistence || _pages.SelectedIndex != 0 || WindowState == FormWindowState.Minimized || !Visible) return;
            if (_nowBusy || (!force && (DateTime.Now - _nowAt).TotalSeconds < 5)) return;
            _nowAt = DateTime.Now; _nowBusy = true;
            string db = AppPaths.CatchDb;
            var aset = _appSettings.Analytics;
            if (!_nowCurveLoading && (_nowCurve == null || (DateTime.Now - _nowCurveAt).TotalMinutes > 10))
            {
                _nowCurveLoading = true;
                Task.Run(() => { try { return BiteAnalytics.Load(db, aset).Soak; } catch { return null; } })
                    .ContinueWith(t => { _nowCurve = t.Result ?? _nowCurve; _nowCurveAt = DateTime.Now; _nowCurveLoading = false; }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            var curve = _nowCurve;
            Task.Run(() => { try { return SessionCoach.Build(db, curve, DateTime.Now, AdvisorLocName); } catch { return null; } })
                .ContinueWith(t => { _nowBusy = false; if (t.Result != null && !view.IsDisposed) { view.Snap = t.Result; view.Invalidate(); } },
                              TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    /// <summary>DESIGN-02: страницы главного окна без рамки стандартных вкладок Windows (переключаются кнопками над ними).</summary>
    public sealed class PageHost : Panel
    {
        private readonly List<(string title, Panel page)> _list = new();
        private int _sel = -1;
        public event EventHandler? SelectedIndexChanged;
        public int Count => _list.Count;
        public string Title(int i) => _list[i].title;
        public Panel Page(int i) => _list[i].page;
        public Panel Add(string title)
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Visible = _list.Count == 0, Margin = new Padding(0) };
            _list.Add((title, p)); Controls.Add(p);
            if (_sel < 0) _sel = 0;
            return p;
        }
        [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int SelectedIndex
        {
            get => _sel;
            set
            {
                if (value < 0 || value >= _list.Count || value == _sel) return;
                _sel = value;
                SuspendLayout();
                for (int i = 0; i < _list.Count; i++) _list[i].page.Visible = i == value;
                ResumeLayout();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>DESIGN-02: «Ваши удочки» — цветная точка и одна строка совета на удочку, ниже подсказки и часы игры.</summary>
    public sealed class NowView : Control
    {
        public CoachSnapshot? Snap;
        public string Clock = "";
        private static readonly Color Ok = Color.FromArgb(127, 203, 143), Soon = Color.FromArgb(237, 184, 78),
                                      Recast = Color.FromArgb(226, 96, 107), Bite = Color.FromArgb(76, 201, 191), Idle = Color.FromArgb(110, 128, 126);
        private static readonly Font Big = new("Segoe UI Semibold", 11f), Body = new("Segoe UI", 10.5f), Small = new("Segoe UI", 9.5f);

        public NowView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
        }

        /// <summary>Строки для показа (и для тестов): (текст, уровень: -1 нет, 0 норм, 1 скоро, 2 смените, 3 клюёт; null — подсказка).</summary>
        public static List<(string text, int? level)> Lines(CoachSnapshot? s)
        {
            var list = new List<(string, int?)>();
            if (s == null || !s.HasData)
            {
                list.Add(("Сейчас вы не рыбачите. После первого заброса здесь будет совет по каждой удочке.", null));
                return list;
            }
            foreach (var r in s.Rods)
                list.Add(($"Удочка {r.Slot + 1} · {SoakCurve.RodName(r.RodCode)}" + (r.Setup.Bait.Length > 0 ? " · «" + r.Setup.Bait + "»" : "") + " — " + r.Status, r.Level));
            foreach (var h in s.Hints)
                if (!h.StartsWith("В игре ", StringComparison.Ordinal)) list.Add((h, null));   // часы показаны отдельной строкой сверху
            return list;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int x0 = 16, w = Math.Max(50, Width - 32), y = 12, bottom = Height - 6;
            void Line(string text, Color col, Font f, int indent)
            {
                if (y >= bottom) return;
                var sz = UiText.MeasureText(g, text, f, new Size(w - indent, 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                UiText.DrawText(g, text, f, new Rectangle(x0 + indent, y, w - indent, Math.Min(sz.Height, bottom - y)), col, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                y += sz.Height + 8;
            }
            if (Clock.Length > 0) Line(Clock, Theme.Text, Big, 0);
            foreach (var (text, level) in Lines(Snap))
            {
                if (level is int lv)
                {
                    var col = lv switch { 0 => Ok, 1 => Soon, 2 => Recast, 3 => Bite, _ => Idle };
                    using (var b = new SolidBrush(col)) g.FillEllipse(b, x0, y + 5, 11, 11);
                    Line(text, Theme.Text, Body, 20);
                }
                else Line("• " + text, Snap?.HasData == true ? Soon : Theme.TextMuted, Snap?.HasData == true ? Small : Body, 0);
            }
        }
    }
}
