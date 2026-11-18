using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace FisherLogger
{
    /// <summary>
    /// Немодальное всплывающее уведомление о поимке.
    /// Появляется вверху по центру, живёт 5 секунд, не активируется
    /// (не забирает фокус у игры) и не показывается на панели задач.
    /// </summary>
    public sealed class CatchNotificationForm : Form
    {
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TOPMOST = 0x00000008;

        private static readonly object _sync = new();
        private static CatchNotificationForm? _currentNotification;
        private static int _activeCount;
        private static int _showCount;
        private static DateTime _lastShowTime = DateTime.MinValue;
        private static string _lastShowText = "";

        /// <summary>Куда писать отладочные сообщения [NOTIFY] (задаётся из Form1).</summary>
        public static Action<string>? Logger;

        private readonly System.Windows.Forms.Timer _closeTimer;
        private readonly PictureBox picFish;
        private readonly Label lblFishName;
        private readonly Label lblWeight;
        private readonly Label lblGrade;
        private readonly Label lblBait;

        private static void NotifyLog(string message)
        {
            try
            {
                Logger?.Invoke("[NOTIFY] " + message);
            }
            catch
            {
                // Логирование не должно ломать уведомление.
            }
        }

        /// <summary>Показывает уведомление, закрывая предыдущее (без наложения).</summary>
        public static void ShowFor(CatchData catchData, DateTime? catchTime = null)
        {
            if (catchData == null) return;

            int showNumber;
            lock (_sync)
            {
                showNumber = ++_showCount;
            }

            // NEXT-SESSION-53: пометка неопределённой связи показывается ОТДЕЛЬНО от вида/ID.
            string text = CatchDisplay.Fish(catchData) + " " + catchData.weight + "гр";
            if (!string.IsNullOrEmpty(catchData.notification_note))
                text += " (" + catchData.notification_note + ")";
            NotifyLog("#" + showNumber + " Попытка показать: " + text);

            DateTime now = DateTime.Now;
            bool isDuplicate;

            lock (_sync)
            {
                isDuplicate = text == _lastShowText &&
                              (now - _lastShowTime).TotalSeconds < 10;
                if (!isDuplicate)
                {
                    _lastShowText = text;
                    _lastShowTime = now;
                }
            }

            if (isDuplicate)
            {
                NotifyLog("Пропуск дубликата: " + text);
                return;
            }

            CatchNotificationForm? old;
            lock (_sync)
            {
                old = _currentNotification;
                _currentNotification = null;
            }

            try
            {
                if (old != null && !old.IsDisposed)
                    old.Close();
            }
            catch { }

            if (catchTime.HasValue)
            {
                TimeSpan delay = now - catchTime.Value;
                NotifyLog("Задержка от поклевки: " + delay.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) + " сек");
            }

            int active;
            lock (_sync)
            {
                _activeCount++;
                active = _activeCount;
            }

            NotifyLog("Создание уведомления #" + active + ": " + text);

            try
            {
                var form = new CatchNotificationForm(catchData);
                form.FormClosed += (s, e) =>
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_currentNotification, form))
                            _currentNotification = null;
                    }
                };

                lock (_sync)
                {
                    _currentNotification = form;
                }

                form.Show();
                NotifyLog("Показано: " + text);
            }
            catch (Exception ex)
            {
                int activeNow;
                lock (_sync)
                {
                    if (_activeCount > 0) _activeCount--;
                    activeNow = _activeCount;
                }
                NotifyLog("Ошибка показа: " + ex.Message + ". Активных: " + activeNow);
            }
        }

        private CatchNotificationForm(CatchData data)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Theme.Surface; // DESIGN
            Opacity = 0.92;
            Size = new Size(440, 120);
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;

            string fishName = CatchDisplay.Fish(data);
            // BRIDGE-FB-01: класс — из игры (windex), а не старая метка по весу (она называла карпа 2 кг «трофейным»); нет windex — пусто
            string grade = data.windex is int gw0 && gw0 >= 0 && gw0 <= 3 ? GradeBadge.ShortName(gw0) : "";
            // FIX-01: the early-bite popup appears at the BITE — the fish is not caught yet; say so instead of an empty grade
            // FIX-02: with the class of this fish from the bite packet (windex, CONFIRMED) in the game's colour scheme
            bool earlyBite = string.Equals(data.source, "early_bite", StringComparison.Ordinal);
            if (earlyBite) grade = "поклёвка" + (data.windex is int bw && bw >= 0 && bw <= 3 ? " · " + GradeBadge.ShortName(bw) : "");
            string bait = !string.IsNullOrWhiteSpace(data.bait_name)
                ? "Наживка: " + data.bait_name
                : (data.bait_id.HasValue
                    ? "Наживка: " + CatchDisplay.Bait(data)
                    : "Наживка: ожидание сводки...");

            NotifyLog("Конструктор: " + fishName + " " + data.weight + "гр" +
                      (string.IsNullOrEmpty(data.fish_icon_path) ? "" : " icon=" + data.fish_icon_path));

            picFish = new PictureBox
            {
                Location = new Point(14, 22),
                Size = new Size(64, 64),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Theme.SurfaceAlt
            };
            var fishIcon = IconLoader.Load(data.fish_icon_path);
            if (fishIcon != null)
                picFish.Image = fishIcon;
            else
                picFish.Visible = false;

            lblFishName = new Label
            {
                Text = fishName,
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                AutoEllipsis = true,
                AutoSize = false,
                Location = new Point(86, 8),
                Size = new Size(340, 32)
            };

            lblWeight = new Label
            {
                Text = data.weight + " гр",
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = Theme.Warning,
                BackColor = Color.Transparent,
                AutoSize = false,
                Location = new Point(86, 42),
                Size = new Size(160, 26)
            };

            lblGrade = new Label
            {
                Text = grade,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = data.windex is int cw && GradeBadge.ColorOf(cw) is Color gc ? gc : Theme.TextMuted,
                BackColor = Color.Transparent,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                Location = new Point(250, 44),
                Size = new Size(176, 24)
            };

            lblBait = new Label
            {
                Text = bait,
                Font = new Font("Segoe UI", 9f),
                ForeColor = Theme.TextMuted,
                BackColor = Color.Transparent,
                AutoEllipsis = true,
                AutoSize = false,
                Location = new Point(86, 76),
                Size = new Size(340, 24)
            };

            Controls.Add(picFish);
            Controls.Add(lblFishName);
            Controls.Add(lblWeight);
            Controls.Add(lblGrade);
            Controls.Add(lblBait);

            // Тонкая рамка, чтобы уведомление читалось на светлом фоне игры.
            Paint += (s, e) =>
            {
                using var pen = new Pen(Theme.Border);
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
                using var accent = new SolidBrush(Theme.AccentTeal); // DESIGN: accent strip
                e.Graphics.FillRectangle(accent, 0, 0, 4, Height);
            };

            // Клик по уведомлению закрывает его досрочно.
            Click += (s, e) => Close();
            foreach (Control control in Controls)
                control.Click += (s, e) => Close();

            _closeTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _closeTimer.Tick += (s, e) =>
            {
                _closeTimer.Stop();
                Close();
            };
            _closeTimer.Start();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            var screen = Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position);
            var area = screen.WorkingArea;
            Location = new Point(
                area.Left + (area.Width - Width) / 2,
                area.Top + 80);

            NotifyLog("Показано на экране: X=" + Location.X + ", Y=" + Location.Y);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try
            {
                _closeTimer.Stop();
                _closeTimer.Dispose();
            }
            catch
            {
                // ignore
            }

            int active;
            lock (_sync)
            {
                if (_activeCount > 0) _activeCount--;
                active = _activeCount;
            }

            NotifyLog("Закрыто. Активных: " + active);

            base.OnFormClosed(e);
        }

        private static Color GradeColor(string grade)
        {
            return grade switch
            {
                "ОБЫЧНЫЙ" => Color.Silver,
                "КРУПНЫЙ" => Color.LightGreen,
                "ТРОФЕЙНЫЙ" => Color.Orange,
                "УНИКАЛЬНЫЙ" => Color.Orange,
                "ЗОЛОТОЙ" => Color.Gold,
                "РЕЛИКТ" => Color.MediumPurple,
                _ => Color.White
            };
        }
    }
}
