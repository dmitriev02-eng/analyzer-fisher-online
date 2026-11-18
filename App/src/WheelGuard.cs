using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FisherLogger
{
    /// <summary>
    /// UIFIX-02: колесо мыши не меняет значения выпадающих списков, счётчиков, дат и ползунков — оно прокручивает страницу,
    /// на которой они стоят (как просил пользователь). Открытый список (DroppedDown) прокручивается как обычно.
    /// </summary>
    public sealed class WheelGuard : IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private static bool _installed;

        public static void Install() { if (_installed) return; _installed = true; Application.AddMessageFilter(new WheelGuard()); }

        public static bool IsValueControl(Control c) => c is ComboBox { DroppedDown: false } || c is UpDownBase || c is DateTimePicker || c is TrackBar;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;
            var c = Control.FromHandle(m.HWnd) ?? Control.FromChildHandle(m.HWnd);
            if (c == null) return false;
            var v = c; while (v != null && !IsValueControl(v) && v is not Form) v = v.Parent;   // поле редактирования внутри ComboBox/NumericUpDown
            if (v == null || !IsValueControl(v)) return false;
            for (var p = v.Parent; p != null; p = p.Parent)
                if (p is ScrollableControl sc && sc.AutoScroll && sc.VerticalScroll.Visible) { SendMessage(p.Handle, WM_MOUSEWHEEL, m.WParam, m.LParam); return true; }
            return true;   // прокручивать нечего — просто не меняем значение
        }
    }
}
