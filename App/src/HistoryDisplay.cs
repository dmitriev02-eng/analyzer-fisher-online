using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace FisherLogger
{
    // =====================================================================================
    // NEXT-HISTORY-UI-01: отображение двух столбцов таблицы «История поимок».
    //  * Грейд — цветной значок по СЫРОМУ windex улова (собственное объявление cmd=6, NEXT-ANALYTICS-09/11).
    //    Метка по весу (catches.grade) НЕ используется: граница видоспецифична, вывод грейда из веса запрещён.
    //    windex отсутствует → значка нет (неизвестно), ничего не угадывается.
    //  * Прикорм — прикорм, ВНЕСЁННЫЙ на заброс улова: только запись c2s 1112, уже привязанная к этому забросу
    //    (groundbait_actions.cast_id = catches.cast_id; привязка делается при записи по кормушке удочки и точной точке).
    //    Несколько разных прикормов на один заброс → неоднозначно, не показывается.
    // =====================================================================================
    public static class GradeBadge
    {
        /// <summary>Цвет класса как в игре: 0 — обычный (серый), 1 — хороший (зелёный), 2 — крупный (синий),
        /// 3 — трофейный (оранжевый со звездой). DESIGN-01: исправлен устаревший комментарий.</summary>
        public static Color? ColorOf(int? windex) => windex switch
        {
            0 => Color.FromArgb(150, 150, 150),
            1 => Color.FromArgb(76, 175, 80),
            2 => Color.FromArgb(66, 133, 244),
            3 => Color.Orange,
            _ => null
        };

        private static readonly Font BadgeFont = new("Segoe UI", 7.5f, FontStyle.Bold);

        public static string Glyph(int windex) => windex switch { 3 => "★", 0 => "", _ => windex.ToString(CultureInfo.InvariantCulture) };

        /// <summary>FIX-02: короткое имя класса для уведомления о поклёвке.</summary>
        public static string ShortName(int windex) => windex switch { 0 => "обычный", 1 => "хороший", 2 => "крупный", 3 => "★ трофейный", _ => "класс " + windex.ToString(CultureInfo.InvariantCulture) };

        /// <summary>Текстовое описание для подсказки и доступности.
        /// FIX-02: сверено с кодом игры (FishTemplate.getColorFullName): цвет имени рыбы выбирается ТОЛЬКО по windex —
        /// 0 светлый (#FFFFC7), 1 зелёный (#80FF60), 2 синий (#6080FF), 3 оранжевый (#FFA020); особые метки
        /// (чемпион/задание) красят имя иначе — этих флагов логгер не получает.</summary>
        public static string Describe(int? windex) => windex switch
        {
            // MASTER-01: названия как в игре (FishTemplate.getShortName): windex 2 — «Крупный», 3 — «Трофейный»; 0 и 1 — без приставки
            0 => "Класс: обычный (серый)",
            1 => "Класс: хороший (зелёный)",
            2 => "Класс: крупный (синий) — в игре «Крупный»",
            3 => "Класс: трофейный (оранжевый) — в игре «Трофейный»",
            null => "Класс: неизвестен (игра не сообщила класс; по весу не угадываем)",
            _ => "Класс: неизвестное значение " + windex.Value.ToString(CultureInfo.InvariantCulture)
        };

        /// <summary>Нарисовать значок в ячейке (фон и рамка ячейки — штатные).</summary>
        public static void Paint(DataGridViewCellPaintingEventArgs e)
        {
            e.PaintBackground(e.ClipBounds, true);
            int? w = e.Value is int i ? i : (int?)null;
            var color = ColorOf(w);
            if (color != null && w.HasValue)
            {
                var b = e.CellBounds;
                int d = Math.Max(10, Math.Min(18, b.Height - 10));
                var r = new Rectangle(b.Left + 8, b.Top + (b.Height - d) / 2, d, d);
                var g = e.Graphics!;
                var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var br = new SolidBrush(color.Value)) g.FillEllipse(br, r);
                using (var pen = new Pen(Color.FromArgb(40, 40, 40), 1f)) g.DrawEllipse(pen, r);
                g.SmoothingMode = old;
                string glyph = Glyph(w.Value);
                if (glyph.Length > 0)
                    UiText.DrawText(g, glyph, BadgeFont, r, Color.White,
                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            e.Handled = true;
        }
    }

    public static class GroundbaitDisplay
    {
        /// <summary>Текст ячейки: имя готового прикорма или «—» (нет/неизвестно/неоднозначно).</summary>
        public static string Cell(CatchDbWriter.AppliedGroundbait? gb, Func<long, string?> name)
        {
            if (gb == null || gb.Status != "applied") return "—";
            return (gb.ItemId.HasValue ? name(gb.ItemId.Value) : null) ?? (Plain.Tech ? "прикорм uid " + gb.Uid.ToString(CultureInfo.InvariantCulture) : "прикорм");
        }

        /// <summary>Подсказка: что внесено и состав (id → имя по каталогу; сырые хвосты «/100/0» — как есть, смысл UNKNOWN).</summary>
        public static string Tooltip(CatchDbWriter.AppliedGroundbait? gb, Func<long, string?> name, long? castId)
        {
            if (castId is null or <= 0) return "Прикорм: неизвестен (улов не привязан к забросу)";
            if (gb == null) return Plain.Tech ? "Прикорм: на заброс #" + castId + " внесение c2s 1112 не записано (или не привязано к этому забросу)" : "Прикорм: на этот заброс не вносился (или не удалось связать)";
            if (gb.Status == "ambiguous")
                return "Прикорм: неоднозначно — к забросу #" + castId + " привязано " + gb.DistinctGroundbaits + " разных прикорма; не показывается";
            var sb = new StringBuilder("Прикорм: " + Cell(gb, name) + (Plain.Tech && gb.ItemId.HasValue ? " (tid " + gb.ItemId + ")" : "") +
                                       (Plain.Tech ? ", внесений на заброс #" + castId : ", внесений") + ": " + gb.Applications);
            void Part(string label, string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return;
                foreach (var piece in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string head = piece.Split('/')[0];
                    string? n = long.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && id > 0 ? name(id) : null;
                    sb.Append("\r\n").Append(label).Append(": ").Append(n ?? (Plain.Tech ? "id " + head : "неизвестный компонент")).Append(Plain.Tech && piece.Contains('/') ? " (" + piece + ")" : "");
                }
            }
            if (gb.Composition == null) sb.Append("\r\nСостав: нет данных");
            else
            {
                Part("Основа", gb.Composition.Osnova);
                Part("Аромат", gb.Composition.Aroma);
                Part("Добавка", gb.Composition.AddExtend);
                Part("Добавка (наживка)", gb.Composition.AddBaits);
            }
            return sb.ToString();
        }
    }
}
