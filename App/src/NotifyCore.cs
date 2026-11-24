using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace FisherLogger
{
    // =====================================================================================
    // NOTIFY-02: настройки уведомлений о поклёвке, звуки, окна по удочкам; трей и клавиша главного окна.
    // =====================================================================================
    public enum ToastPlacement { Top = 0, Bottom = 1, Custom = 2 }

    public sealed class NotifySettings
    {
        public bool Enabled { get; set; } = true;
        public ToastStyle Style { get; set; } = ToastStyle.Oval;
        public int DurationSec { get; set; } = 10;            // было 5 с — увеличено вдвое
        public int TrophyExtraSec { get; set; } = 5;          // трофей висит дольше
        public double Opacity { get; set; } = 0.94;
        public double Scale { get; set; } = 1.0;
        public ToastPlacement Placement { get; set; } = ToastPlacement.Top;
        public int OffsetY { get; set; } = 60;
        public List<int> CustomX { get; set; } = new() { int.MinValue, int.MinValue, int.MinValue };
        public List<int> CustomY { get; set; } = new() { int.MinValue, int.MinValue, int.MinValue };
        public bool TrophyGold { get; set; } = true;
        public bool SoundEnabled { get; set; } = true;
        public int Volume { get; set; } = 80;                 // 0…100
        public int MaxSoundSec { get; set; } = 4;
        /// <summary>Звук на класс 0…3: "default", "none" или имя файла в папке звуков.</summary>
        public List<string> Sounds { get; set; } = new() { "default", "default", "default", "default" };

        public void Normalize()
        {
            DurationSec = Math.Clamp(DurationSec, 2, 60); TrophyExtraSec = Math.Clamp(TrophyExtraSec, 0, 60);
            Opacity = Math.Clamp(double.IsNaN(Opacity) ? 0.94 : Opacity, 0.3, 1.0); Scale = Math.Clamp(double.IsNaN(Scale) ? 1 : Scale, 0.7, 1.6);
            OffsetY = Math.Clamp(OffsetY, 0, 600); Volume = Math.Clamp(Volume, 0, 100); MaxSoundSec = Math.Clamp(MaxSoundSec, 1, 15);
            if (!Enum.IsDefined(typeof(ToastStyle), Style)) Style = ToastStyle.Oval;
            if (!Enum.IsDefined(typeof(ToastPlacement), Placement)) Placement = ToastPlacement.Top;
            CustomX ??= new(); CustomY ??= new(); Sounds ??= new();
            while (CustomX.Count < 3) CustomX.Add(int.MinValue); while (CustomY.Count < 3) CustomY.Add(int.MinValue);
            while (Sounds.Count < 4) Sounds.Add("default");
            for (int i = 0; i < Sounds.Count; i++) if (string.IsNullOrWhiteSpace(Sounds[i])) Sounds[i] = "default";
        }

        public NotifySettings Clone()
        {
            var c = (NotifySettings)MemberwiseClone();
            c.CustomX = new(CustomX); c.CustomY = new(CustomY); c.Sounds = new(Sounds);
            return c;
        }
    }

    public sealed class WindowSettings
    {
        public bool MinimizeToTray { get; set; } = true;
        public bool HotkeyEnabled { get; set; } = true;
        public int HotkeyKey { get; set; } = (int)Keys.F11;    // GAMEMAP-01: окно программы — F11 (F12 теперь карта на весь экран)
        public int HotkeyModifiers { get; set; } = 0;          // Keys.Control | Keys.Shift | Keys.Alt
        public int TrayHintsShown { get; set; } = 0;
        /// <summary>UI-01 / FEAT-11: новый интерфейс (бета). По умолчанию выкл — прежнее окно.</summary>
        public bool NewUiBeta { get; set; } = true;
        /// <summary>FEAT-14: новое окно стало основным; 1 — переход выполнен (дальше выбор пользователя сохраняется).</summary>
        public int UiVersion { get; set; } = 0;
        /// <summary>FEAT-14: новая мини-карта поверх игры (части: верх, карта, удочки). -99999 — место по умолчанию.</summary>
        public int MiniX { get; set; } = -99999;
        public int MiniY { get; set; } = -99999;
        public int MiniSize { get; set; } = 260;
        public double MiniRadius { get; set; } = 50;
        public int MiniOpacity { get; set; } = 92;
        public string MiniDock { get; set; } = "bottom";
        public int PanelX { get; set; } = -99999;
        public int PanelY { get; set; } = -99999;
        public int HdrX { get; set; } = -99999;
        public int HdrY { get; set; } = -99999;
        public bool MiniHdr { get; set; } = true;
        public bool MiniPanel { get; set; } = true;
        public bool MiniPinned { get; set; } = true;
        /// <summary>FEAT-12: круги клёва на карте нового окна: показывать и с какого уровня (0 все, 1 обычно и выше, 2 выше обычного).</summary>
        public bool NuZonesOn { get; set; } = true;
        public int NuZoneMinLevel { get; set; } = 0;
        /// <summary>UI-02/5: слой «Места по уловам» и его фильтры (рыба, класс, глубина дна, наживка, часть суток игры, период).</summary>
        public bool NuPlacesOn { get; set; } = true;
        public bool NuPlacesMini { get; set; } = true;
        public bool NuPlacesFolded { get; set; } = false;
        public string NuPlaceFish { get; set; } = "";
        public int NuPlaceMinClass { get; set; } = -1;
        public int NuPlaceDepth { get; set; } = -1;
        public string NuPlaceBait { get; set; } = "";
        public string NuPlacePart { get; set; } = "";
        public int NuPlacePeriod { get; set; } = 3;
        /// <summary>GAMEMAP-01: карта водоёма на весь экран поверх игры.</summary>
        public bool MapHotkeyEnabled { get; set; } = true;
        public int MapHotkeyKey { get; set; } = (int)Keys.F12;
        public int MapHotkeyModifiers { get; set; } = 0;
        /// <summary>Версия раскладки клавиш: 2 — окно F11, карта F12 (старое «окно на F12» переносится на F11 один раз).</summary>
        public int KeysVersion { get; set; } = 0;
        public void Normalize()
        {
            NewUiBeta = true; if (UiVersion < 1) UiVersion = 1;   // FEAT-15: старого интерфейса больше нет   // FEAT-14: новое окно — основное у всех
            MiniSize = Math.Clamp(MiniSize, 160, 520); MiniOpacity = Math.Clamp(MiniOpacity, 40, 100);
            if (MiniRadius is not (25 or 50 or 100 or 200)) MiniRadius = 50;
            if (MiniDock is not ("bottom" or "top" or "left" or "right" or "free")) MiniDock = "bottom";
            NuPlaceFish ??= ""; NuPlaceBait ??= ""; NuPlacePart ??= "";
            if (NuPlaceMinClass is not (-1 or 1 or 2 or 3)) NuPlaceMinClass = -1;
            if (NuPlaceDepth < -1 || NuPlaceDepth > NuPlaces.DepthEdges.Length - 2) NuPlaceDepth = -1;
            if (NuPlacePart.Length > 0 && Array.IndexOf(NuPlaces.Parts, NuPlacePart) < 0) NuPlacePart = "";
            NuPlacePeriod = Math.Clamp(NuPlacePeriod, 0, 3);
            if (KeysVersion < 2) { if (HotkeyKey == (int)Keys.F12 && HotkeyModifiers == 0) HotkeyKey = (int)Keys.F11; KeysVersion = 2; }
            if (HotkeyKey <= 0 || HotkeyKey > 254) HotkeyKey = (int)Keys.F11; HotkeyModifiers &= (int)(Keys.Control | Keys.Shift | Keys.Alt);
            if (MapHotkeyKey <= 0 || MapHotkeyKey > 254) MapHotkeyKey = (int)Keys.F12; MapHotkeyModifiers &= (int)(Keys.Control | Keys.Shift | Keys.Alt);
        }
    }

    // ------------------------------------------------------------------ звуки
    public static class SoundBank
    {
        public const string DefaultBite = "Поклёвка (стандарт).wav", DefaultTrophy = "Трофей — фанфары.wav", MemeTrophy = "Трофей — бобёр (мем).wav";
        public static readonly string[] Extensions = { ".wav", ".mp3", ".wma", ".m4a" };
        public static string? DirOverride;
        public static string Dir => DirOverride ?? Path.Combine(AppPaths.BaseDirectory, "sounds");
        public static readonly string[] ClassNames = { "Обычный", "Хороший", "Крупный", "Трофейный" };

        public static void EnsureDefaults()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string a = Path.Combine(Dir, DefaultBite), b = Path.Combine(Dir, DefaultTrophy);
                if (!File.Exists(a)) File.WriteAllBytes(a, SynthBite());
                if (!File.Exists(b)) File.WriteAllBytes(b, SynthFanfare());
            }
            catch { }
        }

        public static List<string> List()
        {
            try { return Directory.Exists(Dir) ? Directory.GetFiles(Dir).Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).Select(Path.GetFileName).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList()! : new(); }
            catch { return new(); }
        }

        /// <summary>Файл звука для класса (null — без звука).</summary>
        public static string? Resolve(int cls, string setting)
        {
            cls = Math.Clamp(cls, 0, 3);
            if (setting == "none") return null;
            if (setting != "default") { var p = Path.Combine(Dir, Path.GetFileName(setting)); if (File.Exists(p)) return p; }
            EnsureDefaults();
            if (cls == 3)
            {
                var meme = Path.Combine(Dir, MemeTrophy);
                return File.Exists(meme) ? meme : Path.Combine(Dir, DefaultTrophy);
            }
            return Path.Combine(Dir, DefaultBite);
        }

        public static string DefaultName(int cls) => cls == 3 ? (File.Exists(Path.Combine(Dir, MemeTrophy)) ? MemeTrophy : DefaultTrophy) : DefaultBite;

        /// <summary>Скопировать файл пользователя в папку звуков; вернуть имя.</summary>
        public static string Import(string path)
        {
            if (!Extensions.Contains(Path.GetExtension(path).ToLowerInvariant())) throw new InvalidOperationException("Поддерживаются файлы " + string.Join(", ", Extensions));
            Directory.CreateDirectory(Dir);
            string name = Path.GetFileName(path), dst = Path.Combine(Dir, name);
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) return name;
            int n = 2; while (File.Exists(dst)) { name = Path.GetFileNameWithoutExtension(path) + " (" + n++ + ")" + Path.GetExtension(path); dst = Path.Combine(Dir, name); }
            File.Copy(path, dst);
            return name;
        }

        // ---- синтез: короткий «буль-дзинь» для поклёвки и фанфары для трофея (свои, без чужих записей) ----
        private const int Rate = 44100;
        private static byte[] Wav(float[] s)
        {
            float peak = Math.Max(1e-6f, s.Max(v => Math.Abs(v)));
            using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + s.Length * 2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(s.Length * 2);
            foreach (var v in s) w.Write((short)Math.Clamp(v / peak * 0.85f * 32767f, -32767, 32767));
            return ms.ToArray();
        }

        private static void Tone(float[] buf, double t0, double dur, double f0, double f1, double amp, double decay, double[]? harm = null)
        {
            harm ??= new[] { 1.0, 0.35, 0.12 };
            int a = (int)(t0 * Rate), n = (int)(dur * Rate); double ph = 0;
            for (int i = 0; i < n && a + i < buf.Length; i++)
            {
                double t = i / (double)Rate, f = f0 + (f1 - f0) * (i / (double)n);
                ph += 2 * Math.PI * f / Rate;
                double env = Math.Min(1, t / 0.006) * Math.Exp(-t * decay);
                double v = 0; for (int h = 0; h < harm.Length; h++) v += harm[h] * Math.Sin(ph * (h + 1));
                buf[a + i] += (float)(v * amp * env);
            }
        }

        public static byte[] SynthBite()
        {
            var b = new float[(int)(0.62 * Rate)];
            Tone(b, 0.00, 0.12, 620, 260, 0.9, 28, new[] { 1.0, 0.2 });          // «буль» — капля
            Tone(b, 0.10, 0.50, 1318.5, 1318.5, 0.55, 7.5);                       // E6
            Tone(b, 0.19, 0.42, 1760, 1760, 0.45, 8.5);                            // A6
            return Wav(b);
        }

        public static byte[] SynthFanfare()
        {
            var b = new float[(int)(1.9 * Rate)];
            double[] brass = { 1.0, 0.6, 0.45, 0.3, 0.2, 0.12 };
            double[] n = { 523.25, 659.25, 783.99, 1046.5 };
            for (int i = 0; i < 3; i++) Tone(b, i * 0.13, 0.16, n[i], n[i], 0.6, 5, brass);
            foreach (var f in new[] { 523.25, 659.25, 783.99, 1046.5 }) Tone(b, 0.42, 1.4, f, f * 1.003, 0.32, 2.2, brass);
            Tone(b, 0.42, 1.2, 2093, 2093, 0.12, 4);                                // блеск
            return Wav(b);
        }
    }

    /// <summary>Проигрывание wav/mp3/wma/m4a штатным MCI Windows (без сторонних пакетов), с громкостью и ограничением длительности.</summary>
    public static class MciAudio
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern int mciSendString(string cmd, StringBuilder? ret, int len, IntPtr cb);
        private static int _n;
        public static Action<string>? Log;
        public static string LastError = "";

        public static bool Play(string? path, int volume, int maxMs)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || volume <= 0) return false;
            string alias = "fl_snd" + (++_n);
            int r = mciSendString("open \"" + path + "\" type mpegvideo alias " + alias, null, 0, IntPtr.Zero);
            if (r != 0) { LastError = "open " + r; Log?.Invoke("[SOUND] не удалось открыть " + Path.GetFileName(path) + " (MCI " + r + ")"); return false; }
            mciSendString("set " + alias + " time format milliseconds", null, 0, IntPtr.Zero);
            mciSendString("setaudio " + alias + " volume to " + Math.Clamp(volume, 0, 100) * 10, null, 0, IntPtr.Zero);
            var sb = new StringBuilder(32); mciSendString("status " + alias + " length", sb, sb.Capacity, IntPtr.Zero);
            int len = int.TryParse(sb.ToString(), out var l) && l > 0 ? l : maxMs;
            int stopAt = Math.Min(len, maxMs);
            r = mciSendString("play " + alias + " from 0 to " + stopAt, null, 0, IntPtr.Zero);
            if (r != 0) { LastError = "play " + r; mciSendString("close " + alias, null, 0, IntPtr.Zero); return false; }
            var t = new System.Windows.Forms.Timer { Interval = stopAt + 400 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); mciSendString("close " + alias, null, 0, IntPtr.Zero); };
            t.Start();
            LastError = "";
            return true;
        }

        /// <summary>Длина файла, мс (0 — не открылся). Для проверки.</summary>
        public static int LengthMs(string path)
        {
            string alias = "fl_len" + (++_n);
            if (mciSendString("open \"" + path + "\" type mpegvideo alias " + alias, null, 0, IntPtr.Zero) != 0) return 0;
            mciSendString("set " + alias + " time format milliseconds", null, 0, IntPtr.Zero);
            var sb = new StringBuilder(32); mciSendString("status " + alias + " length", sb, sb.Capacity, IntPtr.Zero);
            mciSendString("close " + alias, null, 0, IntPtr.Zero);
            return int.TryParse(sb.ToString(), out var l) ? l : 0;
        }
    }

    // ------------------------------------------------------------------ окна по удочкам
    public static class BiteToastManager
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        private static readonly BiteToastForm?[] _slots = new BiteToastForm?[3];
        private static string _lastKey = ""; private static DateTime _lastAt = DateTime.MinValue;
        public static Action<string>? Log;
        public static Func<Screen>? ScreenOverride;
        public static BiteToastForm? SlotForm(int i) => _slots[i] is { IsDisposed: false } f ? f : null;

        public static int SlotFor(int? rod)
        {
            if (rod is >= 0 and <= 2) return rod.Value;
            foreach (int i in new[] { 1, 0, 2 }) if (SlotForm(i) == null) return i;
            return 1;
        }

        public static Screen TargetScreen()
        {
            if (ScreenOverride != null) return ScreenOverride();
            try { var h = GetForegroundWindow(); if (h != IntPtr.Zero) return Screen.FromHandle(h); } catch { }
            return Screen.PrimaryScreen ?? Screen.AllScreens[0];
        }

        public static Point Place(int slot, Size size, NotifySettings s, Rectangle wa)
        {
            if (s.Placement == ToastPlacement.Custom && s.CustomX[slot] != int.MinValue && s.CustomY[slot] != int.MinValue)
            {
                var p = new Point(s.CustomX[slot], s.CustomY[slot]);
                var vs = SystemInformation.VirtualScreen;
                return new Point(Math.Clamp(p.X, vs.Left, Math.Max(vs.Left, vs.Right - size.Width)), Math.Clamp(p.Y, vs.Top, Math.Max(vs.Top, vs.Bottom - size.Height)));
            }
            int x = wa.Left + wa.Width * (slot + 1) / 4 - size.Width / 2;
            int y = s.Placement == ToastPlacement.Bottom ? wa.Bottom - s.OffsetY - size.Height : wa.Top + s.OffsetY;
            return new Point(x, y);
        }

        public static BiteToastModel Model(CatchData d, int slot, bool rodKnown, NotifySettings s) => new()
        {
            Slot = slot, RodKnown = rodKnown, Fish = CatchDisplay.Fish(d), Weight = d.weight,
            Windex = d.windex is int w && w >= 0 && w <= 3 ? w : null, Icon = IconLoader.Load(d.fish_icon_path), Gold = s.TrophyGold
        };

        /// <summary>Показать поклёвку в окне её удочки (+ звук класса).</summary>
        public static BiteToastForm? Show(CatchData d, int? rod, NotifySettings s, bool sound = true)
        {
            s.Normalize();
            string key = rod + "|" + d.fish_lang_id + "|" + d.weight;
            if (key == _lastKey && (DateTime.Now - _lastAt).TotalSeconds < 3) { Log?.Invoke("[NOTIFY] пропуск дубликата " + key); return null; }
            _lastKey = key; _lastAt = DateTime.Now;
            int slot = SlotFor(rod);
            var m = Model(d, slot, rod is >= 0 and <= 2, s);
            return ShowModel(m, s, sound);
        }

        public static BiteToastForm ShowModel(BiteToastModel m, NotifySettings s, bool sound, bool draggable = false, int? durationMs = null)
        {
            var bmp = BiteToastRenderer.Render(m, s.Style, s.Scale);
            var wa = TargetScreen().WorkingArea;
            var loc = Place(m.Slot, bmp.Size, s, wa);
            var old = SlotForm(m.Slot);
            if (old != null) try { old.Close(); } catch { }
            int dur = durationMs ?? (s.DurationSec + (m.Windex == 3 ? s.TrophyExtraSec : 0)) * 1000;
            var f = new BiteToastForm(m, bmp, loc, s.Opacity, dur) { Draggable = draggable };
            _slots[m.Slot] = f;
            f.Show();
            if (sound && s.SoundEnabled)
            {
                int cls = m.Windex ?? 0;
                try { MciAudio.Play(SoundBank.Resolve(cls, s.Sounds[cls]), s.Volume, s.MaxSoundSec * 1000); } catch (Exception ex) { Log?.Invoke("[SOUND] " + ex.Message); }
            }
            Log?.Invoke("[NOTIFY] удочка " + (m.Slot + 1) + ": " + m.Fish + " " + m.Weight + " г" + (m.Windex.HasValue ? ", класс " + m.Windex : "") + " @" + loc.X + "," + loc.Y);
            return f;
        }

        public static BiteToastModel[] Samples(NotifySettings s) => new[]
        {
            new BiteToastModel { Slot = 0, Fish = "Карась", Weight = 386, Windex = 0, Icon = UiKit.FishIcon(37), Gold = s.TrophyGold },
            new BiteToastModel { Slot = 1, Fish = "Окунь речной", Weight = 1006, Windex = 2, Icon = UiKit.FishIcon(66), Gold = s.TrophyGold },
            new BiteToastModel { Slot = 2, Fish = "Карп зеркальный", Weight = 13474, Windex = 3, Icon = UiKit.FishIcon(412), Gold = s.TrophyGold },
        };

        /// <summary>Пример трёх окон с текущими настройками (звук — только у трофея).</summary>
        public static void Preview(NotifySettings s)
        {
            var ms = Samples(s);
            for (int i = 0; i < 3; i++) ShowModel(ms[i], s, sound: i == 2);
        }

        /// <summary>Расстановка вручную: три окна можно перетащить мышью; положение сохраняется в s (Custom).</summary>
        public static void Arrange(NotifySettings s, Action onChanged)
        {
            foreach (var m in Samples(s))
            {
                var f = ShowModel(m, s, sound: false, draggable: true, durationMs: 120000);
                int slot = m.Slot;
                f.Moved += p => { s.CustomX[slot] = p.X; s.CustomY[slot] = p.Y; s.Placement = ToastPlacement.Custom; onChanged(); };
            }
        }

        public static void CloseAll() { for (int i = 0; i < 3; i++) { try { SlotForm(i)?.Close(); } catch { } _slots[i] = null; } }
    }

    // ------------------------------------------------------------------ трей и клавиша главного окна
    public sealed class TrayController : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
        private const int WM_HOTKEY = 0x312, HotkeyId = 0x4E21, MapHotkeyId = 0x4E22;
        private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;

        private sealed class HotkeyWindow : NativeWindow
        {
            public Action? Pressed, MapPressed;
            public HotkeyWindow() => CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY && (int)m.WParam == HotkeyId) Pressed?.Invoke();
                else if (m.Msg == WM_HOTKEY && (int)m.WParam == MapHotkeyId) MapPressed?.Invoke();
                base.WndProc(ref m);
            }
        }

        private readonly Form _form;
        private readonly Func<WindowSettings> _settings;
        private readonly Action<string>? _log;
        private readonly Action? _toggleOverlay;
        private readonly Action? _saveSettings;
        private readonly HotkeyWindow _hk = new();
        public NotifyIcon Icon { get; }
        public bool HotkeyRegistered { get; private set; }
        public string HotkeyStatus { get; private set; } = "";
        public static TrayController? Current;
        /// <summary>FEAT-09: полный выход (✕ окна теперь сворачивает к часам).</summary>
        public Action? Exit;
        /// <summary>FEAT-11: открыть новое окно (бета).</summary>
        public Action? OpenNewUi;
        /// <summary>ui01_review3 #31: packets from the game really arrive (the tray text must not claim recording otherwise).</summary>
        public Func<bool>? Recording;
        /// <summary>FEAT-14: клавиша окна (F11) открывает/прячет новое окно, когда оно основное.</summary>
        public Action? ToggleNewUi;
        private IntPtr _prev;
        // Клавиши, которые Windows не даёт занять (F12 зарезервирована системой), — отслеживаются опросом состояния:
        // клавиша не перехватывается (доходит и до игры), никаких хуков; так же окно в игре следит за Ctrl.
        private readonly System.Windows.Forms.Timer _poll = new() { Interval = 40 };
        private bool _pollDown;
        public bool PollingMode { get; private set; }
        /// <summary>GAMEMAP-01: открыть/закрыть карту на весь экран (клавиша карты).</summary>
        public Action? ToggleMap;
        public bool MapHotkeyRegistered { get; private set; }
        public bool MapPollingMode { get; private set; }
        public string MapHotkeyStatus { get; private set; } = "";
        private bool _mapPollDown;
        private bool _startHidden, _disposed;
        public Func<IntPtr>? ForegroundOverride;   // тесты

        public TrayController(Form form, Func<WindowSettings> settings, Action<string>? log, Action? toggleOverlay, Action? saveSettings)
        {
            _form = form; _settings = settings; _log = log; _toggleOverlay = toggleOverlay; _saveSettings = saveSettings;
            Icon = new NotifyIcon { Icon = Theme.AppIcon ?? SystemIcons.Application, Visible = true };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть FisherLogger", null, (s, e) => ShowMain());
            menu.Items.Add("Служебное окно (журнал программы, импорт)", null, (s, e) => ShowLegacyMain());
            menu.Items.Add("Мини-карта в игре", null, (s, e) => _toggleOverlay?.Invoke());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, (s, e) => { if (Exit != null) Exit(); else { _form.Show(); _form.Close(); } });
            Icon.ContextMenuStrip = menu;
            Icon.MouseMove += (s, e) => { try { UpdateText(); } catch { } };
            Icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) { if (OpenNewUi != null && _settings().NewUiBeta) OpenNewUi(); else Toggle(); } };
            Icon.BalloonTipClicked += (s, e) => ShowMain();
            _hk.Pressed += Toggle;
            _hk.MapPressed += () => ToggleMap?.Invoke();
            _poll.Tick += (s, e) => PollKey();
            _form.Resize += (s, e) => { if (_form.WindowState == FormWindowState.Minimized && _settings().MinimizeToTray && _form.Visible) HideToTray(returnFocus: false); };
            _form.Shown += (s, e) => { if (_startHidden) { _startHidden = false; HideToTray(returnFocus: true); Hint(true); } };
            Current = this;
            UpdateText();
        }

        public void Start(bool startHidden)
        {
            _startHidden = startHidden; ApplyHotkey();
            // the tray is created inside Form1.Shown: the Shown subscription above comes too late — hide here (start with the game, --tray)
            if (startHidden && _form.IsHandleCreated && _form.Visible) _form.BeginInvoke(new Action(() => { _startHidden = false; HideToTray(returnFocus: true); Hint(true); }));
        }

        public string KeyText { get { var w = _settings(); return w.HotkeyEnabled ? HotkeyText(w.HotkeyKey, w.HotkeyModifiers) : ""; } }

        public static string HotkeyText(int key, int mods)
        {
            var sb = new StringBuilder();
            if ((mods & (int)Keys.Control) != 0) sb.Append("Ctrl+");
            if ((mods & (int)Keys.Alt) != 0) sb.Append("Alt+");
            if ((mods & (int)Keys.Shift) != 0) sb.Append("Shift+");
            sb.Append((Keys)key switch { Keys.Oemplus => "=", Keys.OemMinus => "-", Keys.OemPipe => "\\", Keys.Oemtilde => "Ё", _ => ((Keys)key).ToString() });
            return sb.ToString();
        }

        private void UpdateText()
        {
            string k = KeyText;
            bool rec = Recording?.Invoke() ?? true;
            string t = "FisherLogger — " + (rec ? "запись идёт" : "нет данных от игры") + (k.Length > 0 ? ". " + k + " — открыть" : "");
            Icon.Text = t.Length > 63 ? t[..63] : t;
        }

        public void ApplyHotkey()
        {
            UnregisterHotKey(_hk.Handle, HotkeyId);
            var w = _settings(); w.Normalize();
            HotkeyRegistered = false;
            if (w.HotkeyEnabled)
            {
                uint mods = MOD_NOREPEAT | ((w.HotkeyModifiers & (int)Keys.Control) != 0 ? MOD_CONTROL : 0) | ((w.HotkeyModifiers & (int)Keys.Alt) != 0 ? MOD_ALT : 0) | ((w.HotkeyModifiers & (int)Keys.Shift) != 0 ? MOD_SHIFT : 0);
                HotkeyRegistered = RegisterHotKey(_hk.Handle, HotkeyId, mods, (uint)w.HotkeyKey);
                PollingMode = !HotkeyRegistered;
                HotkeyStatus = HotkeyRegistered ? "клавиша " + KeyText + " работает"
                             : "клавиша " + KeyText + " работает (Windows не даёт её занять, поэтому нажатие только отслеживается: оно дойдёт и до игры/Steam)";
            }
            else { HotkeyStatus = "клавиша отключена"; PollingMode = false; }
            _pollDown = false;
            // GAMEMAP-01: клавиша карты на весь экран (F12 Windows не даёт занять — тогда только отслеживаем нажатие)
            UnregisterHotKey(_hk.Handle, MapHotkeyId);
            MapHotkeyRegistered = false; MapPollingMode = false;
            if (w.MapHotkeyEnabled)
            {
                uint mm = MOD_NOREPEAT | ((w.MapHotkeyModifiers & (int)Keys.Control) != 0 ? MOD_CONTROL : 0) | ((w.MapHotkeyModifiers & (int)Keys.Alt) != 0 ? MOD_ALT : 0) | ((w.MapHotkeyModifiers & (int)Keys.Shift) != 0 ? MOD_SHIFT : 0);
                MapHotkeyRegistered = RegisterHotKey(_hk.Handle, MapHotkeyId, mm, (uint)w.MapHotkeyKey);
                MapPollingMode = !MapHotkeyRegistered;
                MapHotkeyStatus = "карта: клавиша " + HotkeyText(w.MapHotkeyKey, w.MapHotkeyModifiers) + " работает" + (MapHotkeyRegistered ? "" : " (нажатие отслеживается — дойдёт и до игры/Steam)");
            }
            else MapHotkeyStatus = "карта: клавиша отключена";
            _mapPollDown = false;
            if (PollingMode || MapPollingMode) _poll.Start(); else _poll.Stop();
            _log?.Invoke("[TRAY] " + MapHotkeyStatus);
            _log?.Invoke("[TRAY] " + HotkeyStatus);
            UpdateText();
        }

        private IntPtr Foreground() => ForegroundOverride?.Invoke() ?? GetForegroundWindow();

        private void PollKey()
        {
            var w = _settings();
            bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
            bool mods = Down(0x11) == ((w.HotkeyModifiers & (int)Keys.Control) != 0) && Down(0x12) == ((w.HotkeyModifiers & (int)Keys.Alt) != 0) && Down(0x10) == ((w.HotkeyModifiers & (int)Keys.Shift) != 0);
            if (PollingMode)
            {
                bool down = Down(w.HotkeyKey) && mods;
                if (down && !_pollDown) Toggle();
                _pollDown = Down(w.HotkeyKey);
            }
            if (MapPollingMode)
            {
                bool mm = Down(0x11) == ((w.MapHotkeyModifiers & (int)Keys.Control) != 0) && Down(0x12) == ((w.MapHotkeyModifiers & (int)Keys.Alt) != 0) && Down(0x10) == ((w.MapHotkeyModifiers & (int)Keys.Shift) != 0);
                bool md = Down(w.MapHotkeyKey) && mm;
                if (md && !_mapPollDown) ToggleMap?.Invoke();
                _mapPollDown = Down(w.MapHotkeyKey);
            }
        }

        /// <summary>Сделать окно активным даже если клавишу получила игра (Windows ограничивает смену активного окна).</summary>
        public static void ForceForeground(IntPtr h)
        {
            if (SetForegroundWindow(h) && GetForegroundWindow() == h) return;
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
            bool att = fgThread != 0 && fgThread != me && AttachThreadInput(fgThread, me, true);
            try { BringWindowToTop(h); SetForegroundWindow(h); }
            finally { if (att) AttachThreadInput(fgThread, me, false); }
        }

        public bool MainActive => _form.Visible && _form.WindowState != FormWindowState.Minimized && Foreground() == _form.Handle;

        /// <summary>Клавиша: окно программы активно → в трей и вернуть игру; иначе — показать по центру и сделать активным.</summary>
        public void Toggle() { if (ToggleNewUi != null && _settings().NewUiBeta) { ToggleNewUi(); return; } if (MainActive) HideToTray(returnFocus: true); else ShowMain(); }

        /// <summary>FEAT-14: окно активно → спрятать и вернуть игру; иначе запомнить активное окно (игру) и показать.</summary>
        public void ToggleForm(Form f, Action show)
        {
            var fg = Foreground();
            if (f.Visible && f.WindowState != FormWindowState.Minimized && fg == f.Handle) { f.Hide(); if (_prev != IntPtr.Zero) ForceForeground(_prev); return; }
            if (fg != IntPtr.Zero && fg != f.Handle && fg != _form.Handle) _prev = fg;
            show();
            try { ForceForeground(f.Handle); } catch { }
        }

        /// <summary>FEAT-15: the new window is the main one; the old window is only the service window.</summary>
        public void ShowMain() { if (OpenNewUi != null) { OpenNewUi(); return; } ShowLegacyMain(); }

        public void ShowLegacyMain()
        {
            var fg = Foreground();
            if (fg != IntPtr.Zero && fg != _form.Handle) _prev = fg;
            Screen scr = _prev != IntPtr.Zero ? Screen.FromHandle(_prev) : Screen.FromPoint(Cursor.Position);
            _form.ShowInTaskbar = true;
            if (!_form.Visible) _form.Show();
            if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
            var wa = scr.WorkingArea;
            var sz = new Size(Math.Min(_form.Width, wa.Width), Math.Min(_form.Height, wa.Height));
            _form.Bounds = new Rectangle(wa.Left + (wa.Width - sz.Width) / 2, wa.Top + (wa.Height - sz.Height) / 2, sz.Width, sz.Height);
            _form.TopMost = true; _form.TopMost = false;          // поверх полноэкранной игры без ошибки фокуса
            _form.Activate();
            ForceForeground(_form.Handle);
        }

        /// <summary>ui01_review3 #16: the new window hides the same way as the old one did — the game gets the focus back, a hint once.</summary>
        public void HideForm(Form f, bool returnFocus)
        {
            f.Hide();
            if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
            if (returnFocus && _prev != IntPtr.Zero && IsWindow(_prev))
            {
                if (IsIconic(_prev)) ShowWindow(_prev, 9);
                ForceForeground(_prev);
            }
            Hint(false);
        }

        public void HideToTray(bool returnFocus)
        {
            _form.Hide();
            if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
            if (returnFocus && _prev != IntPtr.Zero && IsWindow(_prev))
            {
                if (IsIconic(_prev)) ShowWindow(_prev, 9);         // SW_RESTORE — игру не сворачиваем, а возвращаем
                ForceForeground(_prev);
            }
            Hint(false);
        }

        private void Hint(bool startedWithGame)
        {
            var w = _settings();
            if (!startedWithGame && w.TrayHintsShown >= 3) return;
            string k = KeyText;
            Icon.BalloonTipTitle = startedWithGame ? "FisherLogger запущен вместе с игрой" : "FisherLogger работает в фоне";
            Icon.BalloonTipText = "Запись рыбалки идёт. Открыть окно — " + (k.Length > 0 ? "клавиша " + k + " или " : "") + "значок рыбки у часов. Окно в игре — его клавиша, как раньше.";
            Icon.BalloonTipIcon = ToolTipIcon.Info;
            try { Icon.ShowBalloonTip(6000); } catch { }
            if (!startedWithGame) { w.TrayHintsShown++; try { _saveSettings?.Invoke(); } catch { } }
        }

        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            try { _poll.Stop(); _poll.Dispose(); UnregisterHotKey(_hk.Handle, HotkeyId); UnregisterHotKey(_hk.Handle, MapHotkeyId); _hk.DestroyHandle(); } catch { }
            try { Icon.Visible = false; Icon.Dispose(); } catch { }
            if (Current == this) Current = null;
        }
    }

    /// <summary>Запуск вместе с игрой через параметры запуска Steam (без автозагрузки Windows).</summary>
    public static class GameLaunch
    {
        public static string ScriptPath => Path.Combine(Path.GetDirectoryName(Application.ExecutablePath) ?? "", "StartWithGame.cmd");
        public static string SteamLine => "\"" + ScriptPath + "\" %command%";

        public static string Script(string exePath) =>
            "@echo off\r\n" +
            "rem FisherLogger: start together with The Fisher Online.\r\n" +
            "rem Steam > Library > theFisher Online > Properties > Launch options:  \"" + Path.Combine(Path.GetDirectoryName(exePath) ?? "", "StartWithGame.cmd") + "\" %command%\r\n" +
            "tasklist /FI \"IMAGENAME eq " + Path.GetFileName(exePath) + "\" 2>nul | find /I \"" + Path.GetFileName(exePath) + "\" >nul\r\n" +
            "if errorlevel 1 start \"\" /D \"" + Path.GetDirectoryName(exePath) + "\" \"" + exePath + "\" --tray\r\n" +
            "if \"%~1\"==\"\" exit /b 0\r\n" +
            "%*\r\n";

        /// <summary>Создать/обновить StartWithGame.cmd рядом с программой.</summary>
        public static string Ensure()
        {
            string exe = Application.ExecutablePath;
            string text = Script(exe);
            try { if (!File.Exists(ScriptPath) || File.ReadAllText(ScriptPath) != text) File.WriteAllText(ScriptPath, text, new UTF8Encoding(false)); } catch { }
            return ScriptPath;
        }
    }
}
