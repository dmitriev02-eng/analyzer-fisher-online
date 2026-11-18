using System;
using System.Globalization;

namespace FisherLogger
{
    /// <summary>
    /// DESIGN-02: простые слова вместо сырых чисел. «Шанс» сервера — интенсивность клёва для снасти, а не вероятность,
    /// и шансы разных снастей несравнимы, поэтому игроку показываем слова (как обычно / выше / ниже для этой снасти).
    /// Число — только при включённых технических данных («Для разработчика ▾»).
    /// </summary>
    public static class Plain
    {
        /// <summary>Показывать технические данные: числа шанса, id, номера забросов.</summary>
        public static bool Tech;

        public static string Num(double v) => v.ToString(v < 10 ? "0.0" : "0", CultureInfo.GetCultureInfo("ru-RU"));

        /// <summary>«выше обычного для этой снасти» / «как обычно…» / «ниже обычного…»; без обычного диапазона — пусто.</summary>
        public static string Relative(double chance, (double lo, double hi)? typical) => typical is { } t
            ? chance > t.hi ? "выше обычного для этой снасти" : chance < t.lo ? "ниже обычного для этой снасти" : "для этой снасти это обычно"
            : "";

        /// <summary>Хвост с числом для разработчика: « [шанс 1,8; обычно 0,9–2,4]» или пусто.</summary>
        public static string TechChance(double chance, (double lo, double hi)? typical = null) => Tech
            ? " [шанс " + Num(chance) + (typical is { } t ? "; обычно " + Num(t.lo) + "–" + Num(t.hi) : "") + "]"
            : "";
    }
}
