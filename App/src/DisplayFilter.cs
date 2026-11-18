using System;
using System.Collections.Generic;
using System.Linq;

namespace FisherLogger
{
    // =====================================================================================
    // PRERELEASE-01: что показывать, когда уловов много. ТОЛЬКО отображение — БД, статистика, советник, формула и зоны
    // карты по-прежнему считают ВСЕ уловы.
    //  Режим: −1 авто; 0 — все; 1 — хорошие (зелёные) и крупнее; 2 — крупные (синие) и трофеи.
    //  Авто: за последние сутки ≤ AutoGreyAt уловов — показываем всё; больше — прячем обычные (серые);
    //        больше AutoGreenAt — прячем и хорошие (зелёные).
    //  Всегда видны: последние AlwaysRecent уловов и всё, что поймано за последние RecentMinutes минут, а также уловы с
    //  неизвестным классом (не угадываем). Затем — не больше MaxRows строк.
    // =====================================================================================
    public sealed class DisplaySettings
    {
        public int Mode { get; set; } = -1;
        public int MaxRows { get; set; } = 200;
        /// <summary>Всплывающее окно о поимке — только для класса не ниже этого (0 — для всех; неизвестный класс показывается всегда).</summary>
        public int NotifyMinClass { get; set; } = 0;
        /// <summary>DESIGN-02: показывать технические данные (числа шанса, id, номера забросов). По умолчанию выключено.</summary>
        public bool ShowTech { get; set; }
        public void Normalize()
        {
            if (Mode is < -1 or > 2) Mode = -1;
            MaxRows = Math.Clamp(MaxRows, 50, 2000);
            NotifyMinClass = Math.Clamp(NotifyMinClass, 0, 3);
        }
    }

    public sealed class DisplayResult<T>
    {
        public List<T> Shown = new();
        public int HiddenGrey, HiddenGreen, HiddenByLimit;
        /// <summary>Режим, который реально применён (для авто — вычисленный).</summary>
        public int Effective;
        public int Hidden => HiddenGrey + HiddenGreen + HiddenByLimit;
    }

    public static class DisplayFilter
    {
        public const int AlwaysRecent = 10, RecentMinutes = 15, AutoGreyAt = 60, AutoGreenAt = 150;

        public static string ModeName(int mode) => mode switch
        {
            0 => "все уловы", 1 => "хорошие и крупнее", 2 => "крупные и трофеи", _ => "авто"
        };

        /// <summary>Название для списка выбора (с заглавной буквы).</summary>
        public static string ModeTitle(int mode) { var n = ModeName(mode); return char.ToUpper(n[0]) + n[1..]; }

        /// <summary>Режим «авто» по числу уловов за последние сутки.</summary>
        public static int AutoMode(int lastDay) => lastDay > AutoGreenAt ? 2 : lastDay > AutoGreyAt ? 1 : 0;

        /// <param name="newestFirst">уловы от новых к старым</param>
        public static DisplayResult<T> Apply<T>(IEnumerable<T> newestFirst, Func<T, int?> cls, Func<T, DateTime> time, DateTime now, int mode, int maxRows)
        {
            var all = newestFirst.ToList();
            var res = new DisplayResult<T>();
            int eff = mode >= 0 ? mode : AutoMode(all.Count(x => time(x) <= now && now - time(x) <= TimeSpan.FromDays(1)));
            res.Effective = eff;
            for (int i = 0; i < all.Count; i++)
            {
                var x = all[i];
                int? c = cls(x);
                bool keep = i < AlwaysRecent || (now - time(x)).TotalMinutes <= RecentMinutes || c == null || c.Value >= eff;
                if (!keep) { if (c == 0) res.HiddenGrey++; else res.HiddenGreen++; continue; }
                if (res.Shown.Count >= maxRows) { res.HiddenByLimit++; continue; }
                res.Shown.Add(x);
            }
            return res;
        }

        /// <summary>Короткая подпись «скрыто …» для заголовка списка; пусто — ничего не скрыто.</summary>
        public static string HiddenText<T>(DisplayResult<T> r)
        {
            var parts = new List<string>();
            if (r.HiddenGrey > 0) parts.Add(r.HiddenGrey + " обычн.");
            if (r.HiddenGreen > 0) parts.Add(r.HiddenGreen + " хорош.");
            if (r.HiddenByLimit > 0) parts.Add(r.HiddenByLimit + " старых");
            return parts.Count == 0 ? "" : "скрыто: " + string.Join(", ", parts) + " — в статистике учтены все";
        }

        /// <summary>Показывать ли всплывающее окно о поимке этого класса.</summary>
        public static bool Notify(int? windex, int minClass) => windex == null || minClass <= 0 || windex.Value >= minClass;
    }
}
