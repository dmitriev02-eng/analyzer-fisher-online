using System;

namespace FisherLogger
{
    /// <summary>
    /// NEXT-GTWC-01: оценка игрового времени суток.
    ///
    /// Формула клиента (ПОДТВЕРЖДЕНО дизассемблированием TimeController.currentTime, RVA 0x7AE010):
    ///   serverNow = DateTime.Now + delta   (delta задаёт setServerTime по времени сервера, cmd 105 / cmd 255)
    ///   gameHours = (serverNow.Hour + Minute/60 + Second/3600 + ms/3600000) * daysOfRealDay + addHours;  затем по модулю 24.
    /// Значения daysOfRealDay и addHours хранятся в ресурсах сцены и из кода не читаются.
    /// По 5 снимкам экрана 07.11.2026 12:10–12:18 (часы игры и часы Windows, точность — минута) они
    /// согласуются с daysOfRealDay = 12, addHours = 0 (24 игровых часа = 2 реальных часа) — ВЕРОЯТНО, не доказано:
    /// допустимый диапазон скорости по тем же снимкам 10.8–14.1 игровых минут за реальную минуту.
    /// Поэтому каждая оценка хранится вместе с версией модели и погрешностью и помечается как приблизительная.
    /// </summary>
    public static class GameClock
    {
        public const double DaysPerRealDay = 12.0;
        public const double AddHours = 0.0;
        public const string ModelVersion = "gt1:days=12,add=0:probable";
        /// <summary>Погрешность при известном времени сервера (минутная точность калибровочных снимков), игровые минуты.</summary>
        public const int BaseUncertaintyMinutes = 2;
        /// <summary>Добавка, если время сервера ещё не получено (используются часы ПК; расхождение обычно 2–3 с).</summary>
        public const int NoServerTimeExtraMinutes = 1;

        /// <summary>Игровое время суток для данного времени сервера (по формуле клиента).</summary>
        public static TimeSpan GameTimeOfDay(DateTime serverTime)
        {
            double h = serverTime.TimeOfDay.TotalHours * DaysPerRealDay + AddHours;
            h %= 24.0;
            if (h < 0) h += 24.0;
            return TimeSpan.FromHours(h);
        }

        public static string Format(TimeSpan t) => ((int)t.TotalHours % 24).ToString("00") + ":" + t.Minutes.ToString("00");

        /// <summary>(игровое время «HH:mm», погрешность в игровых минутах).</summary>
        public static (string text, int uncertaintyMinutes) Estimate(DateTime receivedLocal, TimeSpan? serverOffset)
        {
            DateTime server = receivedLocal + (serverOffset ?? TimeSpan.Zero);
            int unc = BaseUncertaintyMinutes + (serverOffset.HasValue ? 0 : NoServerTimeExtraMinutes);
            return (Format(GameTimeOfDay(server)), unc);
        }
    }
}
