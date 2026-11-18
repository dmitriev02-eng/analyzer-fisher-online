using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FisherLogger
{
    // =====================================================================================
    // NOTIFY-02: уведомление о поклёвке — своё окно на каждую удочку (1 — слева, 2 — по центру, 3 — справа, верх экрана).
    //  * Окно со сглаженной формой и прозрачностью (UpdateLayeredWindow), не активируется и не забирает фокус у игры.
    //  * Трофейный класс (windex 3) — золотая рамка и свечение.
    //  * Новая поклёвка той же удочки заменяет её окно; щелчок закрывает.
    // =====================================================================================
    public enum ToastStyle { Oval = 0, Capsule = 1, Medal = 2 }

    public sealed class BiteToastModel
    {
        public int Slot;                 // 0, 1, 2 — удочка 1, 2, 3
        public string Fish = "";
        public int Weight;
        public int? Windex;
        public Image? Icon;
        public bool RodKnown = true;
        public bool Gold = true;                 // золотая рамка трофея (настройка)
        public bool Trophy => Windex == 3 && Gold;
    }

    public static class BiteToastRenderer
    {
        public static readonly Color Gold = Color.FromArgb(247, 200, 74), GoldDeep = Color.FromArgb(176, 122, 22), GoldLight = Color.FromArgb(255, 236, 160);
        private static readonly Color Body = Color.FromArgb(17, 30, 33), BodyTrophy = Color.FromArgb(36, 27, 9);

        public static Color NameColor(int? w) => OverlayForm.GradeNameColor(w);
        public static Color RingColor(int? w, bool gold = true) => w == 3 && gold ? Gold : GradeBadge.ColorOf(w) ?? Theme.Border;

        public static Size SizeOf(ToastStyle s, double scale)
        {
            var b = s switch { ToastStyle.Oval => new Size(262, 140), ToastStyle.Capsule => new Size(318, 86), _ => new Size(190, 160) };
            return new Size((int)Math.Round(b.Width * scale), (int)Math.Round(b.Height * scale));
        }

        /// <summary>Отрисовать уведомление в ARGB-картинку (прозрачность вне формы; рамка и свечение внутри картинки).</summary>
        public static Bitmap Render(BiteToastModel m, ToastStyle style, double scale = 1.0)
        {
            var sz = SizeOf(style, scale);
            var bmp = new Bitmap(sz.Width, sz.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.ScaleTransform((float)scale, (float)scale);
            switch (style)
            {
                case ToastStyle.Oval: Oval(g, m); break;
                case ToastStyle.Capsule: Capsule(g, m); break;
                default: Medal(g, m); break;
            }
            return bmp;
        }

        // ---------- helpers ----------
        private static Font F(string family, float size, FontStyle st = FontStyle.Regular)
        {
            try { var f = new Font(family, size, st, GraphicsUnit.Pixel); if (f.Name == family) return f; f.Dispose(); } catch { }
            return new Font("Segoe UI", size, st, GraphicsUnit.Pixel);
        }

        private static string W(int g) => g.ToString("#,0", CultureInfo.GetCultureInfo("ru-RU")) + " г";

        private static void Glow(Graphics g, GraphicsPath shape, Color c, int width)
        {
            for (int i = width; i >= 1; i--)
                using (var p = new Pen(Color.FromArgb(Math.Max(4, 70 / i), c), i * 2) { LineJoin = LineJoin.Round }) g.DrawPath(p, shape);
        }

        private static void Frame(Graphics g, GraphicsPath shape, BiteToastModel m, RectangleF bounds)
        {
            if (m.Trophy)
            {
                using var lg = new LinearGradientBrush(bounds, GoldLight, GoldDeep, 60f);
                using var p = new Pen(lg, 3.2f);
                g.DrawPath(p, shape);
                using var inner = new Pen(Color.FromArgb(120, GoldLight), 1f);
                using var shrink = (GraphicsPath)shape.Clone();
                using (var mx = new Matrix()) { float k = 0.955f; mx.Translate(bounds.X + bounds.Width * (1 - k) / 2, bounds.Y + bounds.Height * (1 - k) / 2); mx.Scale(k, k); mx.Translate(-bounds.X, -bounds.Y); shrink.Transform(mx); }
                g.DrawPath(inner, shrink);
            }
            else
            {
                using var p = new Pen(Color.FromArgb(230, RingColor(m.Windex, m.Gold)), 2f);
                g.DrawPath(p, shape);
            }
        }

        private static void Fill(Graphics g, GraphicsPath shape, BiteToastModel m, RectangleF b)
        {
            var top = m.Trophy ? Color.FromArgb(250, 52, 39, 12) : Color.FromArgb(246, 24, 41, 45);
            var bot = m.Trophy ? Color.FromArgb(250, BodyTrophy) : Color.FromArgb(246, Body);
            using var br = new LinearGradientBrush(b, top, bot, 90f);
            g.FillPath(br, shape);
        }

        private static void RodBadge(Graphics g, BiteToastModel m, float cx, float cy, float r)
        {
            var c = m.Trophy ? Gold : Theme.Accent;
            using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - r, cy - r, 2 * r, 2 * r);
            using (var p = new Pen(m.Trophy ? Color.FromArgb(255, 60, 42, 8) : Theme.Background, 2f)) g.DrawEllipse(p, cx - r, cy - r, 2 * r, 2 * r);
            using var f = F("Bahnschrift SemiBold", r * 1.25f);
            string t = m.RodKnown ? (m.Slot + 1).ToString(CultureInfo.InvariantCulture) : "?";
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var tb = new SolidBrush(m.Trophy ? Color.FromArgb(60, 40, 6) : Theme.TextOnAccent);
            g.DrawStringT(t, f, tb, new RectangleF(cx - r, cy - r + 0.5f, 2 * r, 2 * r), sf);
        }

        private static void Icon(Graphics g, BiteToastModel m, RectangleF r, bool well)
        {
            if (well)
            {
                using (var b = new SolidBrush(m.Trophy ? Color.FromArgb(70, 50, 14) : Color.FromArgb(32, 54, 58))) g.FillEllipse(b, r);
                using (var p = new Pen(RingColor(m.Windex, m.Gold), m.Trophy ? 2.6f : 2f)) g.DrawEllipse(p, r);
                r.Inflate(-r.Width * 0.12f, -r.Height * 0.12f);
            }
            if (m.Icon != null)
            {
                var src = Opaque(m.Icon);
                float k = Math.Min(r.Width / src.Width, r.Height / src.Height);
                float w = src.Width * k, h = src.Height * k;
                g.DrawImage(m.Icon, new RectangleF(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h), src, GraphicsUnit.Pixel);
            }
        }

        private static readonly Dictionary<Image, RectangleF> _opaque = new();
        /// <summary>Границы непрозрачной части картинки (иконки игры с большими пустыми полями).</summary>
        public static RectangleF Opaque(Image img)
        {
            lock (_opaque)
            {
                if (_opaque.TryGetValue(img, out var r)) return r;
                r = new RectangleF(0, 0, img.Width, img.Height);
                if (img is Bitmap b)
                {
                    int x0 = b.Width, y0 = b.Height, x1 = -1, y1 = -1;
                    for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++)
                        if (b.GetPixel(x, y).A > 24) { if (x < x0) x0 = x; if (y < y0) y0 = y; if (x > x1) x1 = x; if (y > y1) y1 = y; }
                    if (x1 >= x0 && y1 >= y0) r = new RectangleF(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
                }
                _opaque[img] = r; return r;
            }
        }

        private static void Text(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment a = StringAlignment.Center)
        {
            using var sf = new StringFormat { Alignment = a, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            using (var sh = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.DrawStringT(s, f, sh, new RectangleF(r.X + 1, r.Y + 1, r.Width, r.Height), sf);
            using var b = new SolidBrush(c); g.DrawStringT(s, f, b, r, sf);
        }

        private static string WeightText(BiteToastModel m) => (m.Trophy ? "★ " : "") + W(m.Weight);

        // ---------- A: овал по схеме ----------
        private static void Oval(Graphics g, BiteToastModel m)
        {
            var b = new RectangleF(7, 12, 248, 120);
            using var shape = new GraphicsPath(); shape.AddEllipse(b);
            if (m.Trophy) Glow(g, shape, Gold, 6); else Glow(g, shape, Color.Black, 4);
            Fill(g, shape, m, b); Frame(g, shape, m, b);
            Icon(g, m, new RectangleF(131 - 52, 17, 104, 46), false);
            using var fn = F("Segoe UI Semibold", 17f); using var fw = F("Bahnschrift SemiBold", 20f);
            Text(g, m.Fish, fn, NameColor(m.Windex), new RectangleF(30, 60, 202, 24));
            Text(g, WeightText(m), fw, m.Trophy ? GoldLight : Theme.Text, new RectangleF(30, 84, 202, 28));
            RodBadge(g, m, 131, 12, 11);
        }

        // ---------- B: капсула ----------
        private static void Capsule(Graphics g, BiteToastModel m)
        {
            var b = new RectangleF(8, 8, 302, 70);
            using var shape = new GraphicsPath();
            float d = b.Height; shape.AddArc(b.X, b.Y, d, d, 90, 180); shape.AddArc(b.Right - d, b.Y, d, d, 270, 180); shape.CloseFigure();
            if (m.Trophy) Glow(g, shape, Gold, 6); else Glow(g, shape, Color.Black, 4);
            Fill(g, shape, m, b); Frame(g, shape, m, b);
            Icon(g, m, new RectangleF(b.X + 6, b.Y + 6, 58, 58), true);
            using var fn = F("Segoe UI Semibold", 16f); using var fw = F("Bahnschrift SemiBold", 22f);
            Text(g, m.Fish, fn, NameColor(m.Windex), new RectangleF(b.X + 74, b.Y + 8, b.Width - 104, 24), StringAlignment.Near);
            Text(g, WeightText(m), fw, m.Trophy ? GoldLight : Theme.Text, new RectangleF(b.X + 74, b.Y + 33, b.Width - 104, 30), StringAlignment.Near);
            RodBadge(g, m, b.Right - 22, b.Y + b.Height / 2, 12);
        }

        // ---------- C: медальон ----------
        private static void Medal(Graphics g, BiteToastModel m)
        {
            var plate = new RectangleF(10, 70, 170, 80);
            using var shape = new GraphicsPath();
            float r = 16; shape.AddArc(plate.X, plate.Y, r, r, 180, 90); shape.AddArc(plate.Right - r, plate.Y, r, r, 270, 90);
            shape.AddArc(plate.Right - r, plate.Bottom - r, r, r, 0, 90); shape.AddArc(plate.X, plate.Bottom - r, r, r, 90, 90); shape.CloseFigure();
            var circle = new RectangleF(95 - 42, 6, 84, 84);
            using var disc = new GraphicsPath(); disc.AddEllipse(circle);
            using var all = new GraphicsPath(); all.AddPath(shape, false); all.AddPath(disc, false);
            if (m.Trophy) { Glow(g, shape, Gold, 6); Glow(g, disc, Gold, 6); } else { Glow(g, shape, Color.Black, 4); Glow(g, disc, Color.Black, 4); }
            Fill(g, shape, m, plate); Frame(g, shape, m, plate);
            Fill(g, disc, m, circle);
            Icon(g, m, circle, true);
            if (m.Trophy) using (var p = new Pen(Gold, 3f)) g.DrawEllipse(p, circle);
            using var fn = F("Segoe UI Semibold", 16f); using var fw = F("Bahnschrift SemiBold", 21f);
            Text(g, m.Fish, fn, NameColor(m.Windex), new RectangleF(plate.X + 8, plate.Y + 22, plate.Width - 16, 22));
            Text(g, WeightText(m), fw, m.Trophy ? GoldLight : Theme.Text, new RectangleF(plate.X + 8, plate.Y + 46, plate.Width - 16, 28));
            RodBadge(g, m, circle.Right - 6, circle.Top + 12, 11);
        }
    }

    /// <summary>Окно уведомления одной удочки (полупрозрачное, со сглаженными краями).</summary>
    public sealed class BiteToastForm : Form
    {
        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte op, flags, alpha, format; }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
        [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        /// <summary>DIB с предумноженной альфой (как требует UpdateLayeredWindow).</summary>
        private static IntPtr PremultipliedDib(Bitmap bmp, IntPtr dc)
        {
            var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = bmp.Width, biHeight = -bmp.Height, biPlanes = 1, biBitCount = 32 };
            IntPtr hb = CreateDIBSection(dc, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var row = new byte[bmp.Width * 4];
                for (int y = 0; y < bmp.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    Marshal.Copy(row, 0, bits + y * bmp.Width * 4, row.Length);
                }
            }
            finally { bmp.UnlockBits(data); }
            return hb;
        }

        private const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x08000000;
        private readonly Bitmap _bmp;
        private readonly System.Windows.Forms.Timer _life = new(), _fade = new() { Interval = 15 };
        private readonly byte _alpha;
        private int _fadeStep;
        private bool _closing;
        public BiteToastModel Model { get; }
        /// <summary>Режим расстановки: окно можно перетащить, по окончании — событие Moved.</summary>
        [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Draggable { get; set; }
        public event Action<Point>? Moved;
        public int DurationMs { get; }
        public byte Alpha => _alpha;
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public BiteToastForm(BiteToastModel m, Bitmap bmp, Point location, double opacity, int durationMs)
        {
            Model = m; _bmp = bmp;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;   // OVERLAY-04: поверх — WS_EX_TOPMOST; свойство TopMost активировало окно (забирало фокус у игры)
            Location = location; Size = bmp.Size;
            _alpha = (byte)Math.Clamp((int)Math.Round(opacity * 255), 40, 255);
            _life.Interval = Math.Max(1000, durationMs); DurationMs = _life.Interval;
            _life.Tick += (s, e) => { _life.Stop(); FadeOut(); };
            _fade.Tick += (s, e) => StepFade();
            MouseDown += (s, e) =>
            {
                if (!Draggable) { FadeOut(); return; }
                if (e.Button != MouseButtons.Left) { FadeOut(); return; }
                ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);   // перетаскивание за любое место
                Moved?.Invoke(Location);
            };
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE; return cp; } }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Apply(0); }
        protected override void OnShown(EventArgs e) { base.OnShown(e); _fadeStep = 1; _fade.Start(); _life.Start(); }

        private void StepFade()
        {
            const int n = 10;   // ~150 мс
            if (_fadeStep > 0) { Apply((byte)(_alpha * Math.Min(1.0, _fadeStep / (double)n))); if (++_fadeStep > n) { _fade.Stop(); _fadeStep = 0; } }
            else if (_fadeStep < 0) { Apply((byte)(_alpha * Math.Max(0, 1 + _fadeStep / (double)n))); if (--_fadeStep < -n) { _fade.Stop(); Close(); } }
        }

        public void FadeOut() { if (_closing || IsDisposed) return; _closing = true; _life.Stop(); _fadeStep = -1; _fade.Start(); }

        private void Apply(byte alpha)
        {
            if (!IsHandleCreated) return;
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hb = PremultipliedDib(_bmp, screen); old = SelectObject(mem, hb);
                var size = new SIZE { cx = _bmp.Width, cy = _bmp.Height }; var src = new POINT(); var dst = new POINT { x = Left, y = Top };
                var bf = new BLENDFUNCTION { op = 0, flags = 0, alpha = alpha, format = 1 };
                UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref bf, 2);
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(mem, old);
                if (hb != IntPtr.Zero) DeleteObject(hb);
                DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { _life.Dispose(); _fade.Dispose(); _bmp.Dispose(); base.OnFormClosed(e); }
    }
}
