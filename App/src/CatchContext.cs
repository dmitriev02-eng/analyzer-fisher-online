using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FisherLogger
{
    // =====================================================================================
    // NEXT-DATA-01: полный контекст улова — снасть заброса, прикорм, игровые координаты, грейд для показа,
    // правила пригодности полей для анализа. Здесь нет формулы клёва и нет оценки вероятности.
    //
    // Источники (проверены на данных, см. Workbench\reports\NEXT-DATA-01\REPORT.md):
    //  * снасть = дерево инвентаря (s2c 180/181, поле addid = uid родителя): удочка (без родителя) → её дети
    //    (катушка, леска, поводок, крючок, поплавок, кормушка, грузило, наживка, приманка); категория ребёнка — по
    //    Data\items_full_database.json; bait_uid заброса (c2s 1110) — ребёнок удочки;
    //  * XML удочки <props><feed>uid</feed> — прикорм, ЗАГРУЖЕННЫЙ в кормушку (c2s 192 'feed' ставит это свойство);
    //  * c2s 1112 — ВНЕСЕНИЕ прикорма: int64 uid прикорма, int64 uid кормушки (0 = без кормушки), 3×double xyz;
    //  * XML прикорма <addoptions>: osnova / aroma / addextend / addbaits / klevprop — состав замеса (сырые строки);
    //  * игровые координаты: X/Z мира игры из c2s 1110 (те же, что использует карта и батиметрия; без преобразования).
    //
    // Stage 3b (продолжение, после NEXT-TACKLE-RESEARCH-01):
    //  * слот предмета = категория каталога по <tid> (item_id) и сверка с ячейкой addid+cell (таблица CellMap с уровнем
    //    достоверности; ячейка 10 = крючок — CONFIRMED по NEXT-TACKLE-RESEARCH-02). Расхождение категории и ячейки → слот NULL, причина в slot_conflicts;
    //  * inid НИКОГДА не используется как каталоговый id;
    //  * поплавок, удочка, катушка — только справка (не входят в analysis_inputs_v);
    //  * кормушка — вход анализа: тип (subtype/type_name каталога) и СЫРЫЕ base_params/klevprop_raw; их смысл UNKNOWN,
    //    они не называются «видимостью»; признак «премиум» надёжно не определяется → feeder_premium_status = 'unknown'.
    // =====================================================================================

    /// <summary>Запись каталога items_full_database по каталоговому id (&lt;tid&gt;). Сырые поля — без трактовки.</summary>
    public sealed record CatalogEntry(string Name, string Category, string? BaseParams = null, string? KlevPropRaw = null,
                                      string? SubType = null, string? TypeName = null);

    /// <summary>
    /// Ячейка (cell) предмета, установленного на удочку (addid/parent_uid = uid удочки) → слот.
    /// Источник: NEXT-81, NEXT-TACKLE-RESEARCH-01, NEXT-TACKLE-RESEARCH-02 (ячейка 10 = крючок — CONFIRMED с RESEARCH-02).
    /// ВАЖНО: ячейка означает слот снасти ТОЛЬКО у прямых детей удочки; у остальных предметов это обычная позиция инвентаря
    /// (TackleSnapshotBuilder смотрит ячейки только у детей корня-удочки).
    /// </summary>
    public static class CellMap
    {
        public const string Confirmed = "CONFIRMED", Probable = "PROBABLE";
        public static readonly IReadOnlyDictionary<int, (string Slot, string Confidence)> ByCell = new Dictionary<int, (string, string)>
        {
            [2] = ("reel", Confirmed), [3] = ("line", Confirmed), [4] = ("float", Confirmed), [10] = ("hook", Confirmed),
            [12] = ("leader", Confirmed), [15] = ("feeder", Confirmed), [20] = ("bait", Confirmed)
        };
        public static (string Slot, string Confidence)? Of(int cell) => ByCell.TryGetValue(cell, out var v) ? v : null;
        /// <summary>Ячейка 20 — место наживки; приманка (lure/fly) стоит там же, поэтому это не конфликт.</summary>
        public static bool Compatible(string cellSlot, string catalogSlot) =>
            cellSlot == catalogSlot || (cellSlot == "bait" && catalogSlot == "lure");
    }

    /// <summary>Разбор нужных фрагментов XML предмета инвентаря (строго по тегам; нет тега — null).</summary>
    public static class InventoryXml
    {
        private static string? Tag(string? xml, string tag)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            var m = Regex.Match(xml, "<" + tag + @">\s*([^<]*?)\s*</" + tag + ">", RegexOptions.CultureInvariant);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>basePar — характеристики предмета (напр. леска «5/0.28/100»); смысл частей — PROBABLE, хранится как есть.</summary>
        public static string? BasePar(string? xml) => Tag(xml, "basePar");

        /// <summary>props/feed удочки: uid загруженного прикорма; 0 = пусто; null = свойства нет.</summary>
        public static long? PropsFeed(string? xml)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            var props = Regex.Match(xml, @"<props>(.*?)</props>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (!props.Success) return null;
            string? v = Tag(props.Groups[1].Value, "feed");
            return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long u) && u >= 0 ? u : null;
        }

        public sealed class GroundbaitComposition
        {
            public int? OsnovaId { get; init; }
            public string? Osnova { get; init; }
            public string? Aroma { get; init; }
            public string? AddExtend { get; init; }
            public string? AddBaits { get; init; }
            public string? KlevProp { get; init; }
            public bool Any => Osnova != null || Aroma != null || AddExtend != null || AddBaits != null || KlevProp != null;
        }

        /// <summary>Состав прикорма из &lt;addoptions&gt;; null — блока нет.</summary>
        public static GroundbaitComposition? Composition(string? xml)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            var m = Regex.Match(xml, @"<addoptions>(.*?)</addoptions>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (!m.Success) return null;
            string b = m.Groups[1].Value;
            string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            string? osn = N(Tag(b, "osnova"));
            var c = new GroundbaitComposition
            {
                Osnova = osn,
                OsnovaId = int.TryParse(osn, NumberStyles.Integer, CultureInfo.InvariantCulture, out int o) && o > 0 ? o : null,
                Aroma = N(Tag(b, "aroma")), AddExtend = N(Tag(b, "addextend")), AddBaits = N(Tag(b, "addbaits")), KlevProp = N(Tag(b, "klevprop"))
            };
            return c.Any ? c : null;
        }
    }

    /// <summary>Строка текущего инвентаря, нужная для снимка снасти.</summary>
    public sealed class InventoryNode
    {
        public long Uid { get; init; }
        public long? ItemId { get; init; }
        public long? ParentUid { get; init; }
        public int Cell { get; init; }
        public int Count { get; init; }
        public double? Dur { get; init; }
        public string? BasePar { get; init; }
        public string? Xml { get; init; }
        public string? LastSeen { get; init; }   // TACKLE-01: когда строка инвентаря обновлялась последний раз
    }

    public sealed class TackleComponent
    {
        public string Slot { get; set; } = "";
        public long Uid { get; set; }
        public long? ItemId { get; set; }
        public string? Name { get; set; }
        public string? Category { get; set; }
        public string? BasePar { get; set; }
        public double? Dur { get; set; }
        public int Cell { get; set; }
        // NEXT-DATA-01 Stage 3b
        public string? CellSlot { get; set; }          // slot expected by CellMap for this cell (null = unmapped cell)
        public string? CellConfidence { get; set; }    // CONFIRMED | PROBABLE | null
        public string? SlotSource { get; set; }        // how the slot was decided (see TackleSnapshotBuilder)
        public string? CatalogBaseParams { get; set; } // raw items_full_database.base_params (semantics UNKNOWN)
        public string? CatalogKlevPropRaw { get; set; }// raw items_full_database.klevprop_raw (semantics UNKNOWN)
        public string? CatalogSubType { get; set; }
        public string? CatalogTypeName { get; set; }
    }

    /// <summary>Снимок снасти одного заброса. null у слота = неизвестно (не 0 и не «нет»).</summary>
    public sealed class TackleSnapshot
    {
        // complete | ambiguous_slots | no_inventory | bait_not_in_inventory | root_not_rod | no_bait_uid
        public string Status { get; set; } = "";
        public string Source { get; set; } = "inventory_parent_tree";
        public readonly Dictionary<string, TackleComponent> Slots = new();
        public readonly List<TackleComponent> Components = new();
        public readonly List<string> AmbiguousSlots = new();
        public readonly List<string> SlotConflicts = new();   // "uid:cell N expects X, catalog says Y" etc.
        public long? GroundbaitUid { get; set; }
        public long? GroundbaitItemId { get; set; }
        public string? GroundbaitName { get; set; }
        // loaded | empty | unknown (нет XML удочки — после обновления до прихода нового снимка инвентаря)
        public string GroundbaitStatus { get; set; } = "unknown";
        public InventoryXml.GroundbaitComposition? Composition { get; set; }
        public TackleComponent? Get(string slot) => Slots.TryGetValue(slot, out var c) ? c : null;

        public string ComponentsJson()
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < Components.Count; i++)
            {
                var c = Components[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"slot\":").Append(J(c.Slot)).Append(",\"uid\":").Append(c.Uid.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"item_id\":").Append(c.ItemId?.ToString(CultureInfo.InvariantCulture) ?? "null")
                  .Append(",\"category\":").Append(J(c.Category)).Append(",\"cell\":").Append(c.Cell.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"dur\":").Append(c.Dur.HasValue ? c.Dur.Value.ToString("R", CultureInfo.InvariantCulture) : "null")
                  .Append(",\"base_par\":").Append(J(c.BasePar))
                  .Append(",\"cell_slot\":").Append(J(c.CellSlot)).Append(",\"cell_conf\":").Append(J(c.CellConfidence))
                  .Append(",\"slot_source\":").Append(J(c.SlotSource))
                  .Append(",\"cat_base_params\":").Append(J(c.CatalogBaseParams)).Append(",\"cat_klevprop_raw\":").Append(J(c.CatalogKlevPropRaw))
                  .Append(",\"cat_subtype\":").Append(J(c.CatalogSubType)).Append('}');
            }
            return sb.Append(']').ToString();
        }

        private static string J(string? s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }
    }

    public static class TackleSnapshotBuilder
    {
        /// <summary>TACKLE-01: несколько предметов одного слота в ОДНОЙ ячейке удочки — в ячейке помещается один предмет, значит старый уже снят
        /// (сообщение о снятии не пришло). Берём единственный самый свежий; при равном времени — неоднозначно (null).</summary>
        public static TackleComponent? NewestInSameCell(List<TackleComponent> cands, IReadOnlyDictionary<long, InventoryNode> inv)
        {
            if (cands.Count < 2 || cands.Select(c => c.Cell).Distinct().Count() != 1) return null;
            var t = cands.Select(c => (c, ls: inv.TryGetValue(c.Uid, out var n) ? n.LastSeen : null)).ToList();
            if (t.Any(x => x.ls == null)) return null;
            var o = t.OrderByDescending(x => x.ls, StringComparer.Ordinal).ToList();
            return string.CompareOrdinal(o[0].ls, o[1].ls) > 0 ? o[0].c : null;
        }

        /// <summary>Слоты снимка. rod и reel — только справка (см. AnalysisEligibility).</summary>
        public static readonly string[] SlotNames = { "rod", "reel", "line", "leader", "hook", "float", "feeder", "sinker", "lure", "bait" };

        private static string? SlotOf(string? category) => category switch
        {
            "reel" => "reel", "line" => "line", "leader" => "leader", "hook" => "hook", "float" => "float",
            "feeder" => "feeder", "sinker" => "sinker", "lure" => "lure", "fly" => "lure", "bait" => "bait", "dip" => "bait",
            _ => null
        };

        /// <summary>
        /// Снимок снасти по uid наживки заброса и ТЕКУЩЕМУ инвентарю (вызывать в момент заброса, в порядке пакетов).
        /// Ничего не угадывает: нет инвентаря / наживки / удочки → статус и пустые слоты; два предмета одной категории
        /// у одной удочки → слот пуст, статус ambiguous_slots (все предметы сохранены в Components).
        /// </summary>
        public static TackleSnapshot Build(long? baitUid, IReadOnlyDictionary<long, InventoryNode> inv,
                                           Func<long, CatalogEntry?> catalog,
                                           Func<long, InventoryNode?>? lookupAny = null)
        {
            var s = new TackleSnapshot();
            if (!baitUid.HasValue || baitUid.Value <= 0) { s.Status = "no_bait_uid"; return s; }
            if (inv.Count == 0) { s.Status = "no_inventory"; return s; }
            if (!inv.TryGetValue(baitUid.Value, out var bait)) { s.Status = "bait_not_in_inventory"; return s; }

            // up from the bait to the root (no parent); at most 4 levels, a loop is rejected
            var node = bait; var seen = new HashSet<long> { bait.Uid };
            for (int i = 0; i < 4 && node.ParentUid.HasValue && node.ParentUid.Value > 0; i++)
            {
                if (!inv.TryGetValue(node.ParentUid.Value, out var parent) || !seen.Add(parent.Uid)) { s.Status = "root_not_rod"; return s; }
                node = parent;
            }
            var root = node;
            var rootCat = root.ItemId.HasValue ? catalog(root.ItemId.Value) : null;
            if (root.Uid == bait.Uid || rootCat?.Category != "rod" || (root.ParentUid.HasValue && root.ParentUid.Value > 0))
            { s.Status = "root_not_rod"; return s; }

            TackleComponent Comp(InventoryNode n, string slot)
            {
                var cat = n.ItemId.HasValue ? catalog(n.ItemId.Value) : null;
                var cm = CellMap.Of(n.Cell);
                return new TackleComponent { Slot = slot, Uid = n.Uid, ItemId = n.ItemId, Name = cat?.Name, Category = cat?.Category,
                                             BasePar = n.BasePar, Dur = n.Dur, Cell = n.Cell,
                                             CellSlot = cm?.Slot, CellConfidence = cm?.Confidence,
                                             CatalogBaseParams = cat?.BaseParams, CatalogKlevPropRaw = cat?.KlevPropRaw,
                                             CatalogSubType = cat?.SubType, CatalogTypeName = cat?.TypeName };
            }
            var rodC = Comp(root, "rod");
            rodC.SlotSource = "tree_root_catalog_rod";
            s.Slots["rod"] = rodC; s.Components.Add(rodC);

            // descendants of the rod (2 levels), direct children fill the slots
            var children = inv.Values.Where(n => n.ParentUid == root.Uid).OrderBy(n => n.Cell).ThenBy(n => n.Uid).ToList();
            var bySlot = new Dictionary<string, List<TackleComponent>>();
            foreach (var ch in children)
            {
                var cat = ch.ItemId.HasValue ? catalog(ch.ItemId.Value) : null;
                string? catSlot = SlotOf(cat?.Category);
                var cm = CellMap.Of(ch.Cell);
                // Slot decision (no guessing):
                //  catalog slot + mapped cell agree        → slot (source "tid_category+cell")
                //  catalog slot, cell not in CellMap       → slot (source "tid_category")
                //  catalog slot and mapped cell DISAGREE   → no slot, conflict recorded (ambiguous → NULL)
                //  no catalog slot, CONFIRMED cell         → slot from cell (source "cell_confirmed")
                //  no catalog slot, PROBABLE/unmapped cell → no slot (kept in components as "other")
                string? slot = null, source = null;
                if (catSlot != null && cm != null && !CellMap.Compatible(cm.Value.Slot, catSlot))
                    s.SlotConflicts.Add(ch.Uid.ToString(CultureInfo.InvariantCulture) + ": cell " + ch.Cell.ToString(CultureInfo.InvariantCulture) +
                                        " → " + cm.Value.Slot + " (" + cm.Value.Confidence + "), catalog → " + catSlot);
                else if (catSlot != null) { slot = catSlot; source = cm != null ? "tid_category+cell" : "tid_category"; }
                else if (cm != null && cm.Value.Confidence == CellMap.Confirmed) { slot = cm.Value.Slot; source = "cell_confirmed"; }
                var comp = Comp(ch, slot ?? "other");
                comp.SlotSource = source ?? (catSlot != null ? "conflict" : "unidentified");
                s.Components.Add(comp);
                if (slot != null) { if (!bySlot.TryGetValue(slot, out var l)) bySlot[slot] = l = new(); l.Add(comp); }
                foreach (var gc in inv.Values.Where(n => n.ParentUid == ch.Uid).OrderBy(n => n.Uid))
                    s.Components.Add(Comp(gc, "nested"));
            }
            foreach (var kv in bySlot)
            {
                if (kv.Key == "bait") continue;   // the bait slot is the cast's own bait uid (below)
                if (kv.Value.Count == 1) s.Slots[kv.Key] = kv.Value[0];
                else if (NewestInSameCell(kv.Value, inv) is { } nw) { nw.SlotSource += ";newest_in_cell"; s.Slots[kv.Key] = nw; }
                else s.AmbiguousSlots.Add(kv.Key);
            }
            // bait = exactly the uid sent in c2s 1110 (verified identifier), not "a bait child of the rod"
            var baitC = s.Components.FirstOrDefault(c => c.Uid == bait.Uid) ?? Comp(bait, "bait");
            baitC.Slot = "bait"; baitC.SlotSource ??= "cast_bait_uid"; s.Slots["bait"] = baitC;
            if (!s.Components.Contains(baitC)) s.Components.Add(baitC);

            // groundbait loaded in the rod's feeder (rod XML props/feed)
            if (root.Xml == null) s.GroundbaitStatus = "unknown";
            else
            {
                long? gb = InventoryXml.PropsFeed(root.Xml);
                if (!gb.HasValue) s.GroundbaitStatus = "unknown";
                else if (gb.Value == 0) s.GroundbaitStatus = "empty";
                else
                {
                    s.GroundbaitStatus = "loaded"; s.GroundbaitUid = gb.Value;
                    var g = inv.TryGetValue(gb.Value, out var gn) ? gn : lookupAny?.Invoke(gb.Value);
                    if (g != null)
                    {
                        s.GroundbaitItemId = g.ItemId;
                        s.GroundbaitName = g.ItemId.HasValue ? catalog(g.ItemId.Value)?.Name : null;
                        s.Composition = InventoryXml.Composition(g.Xml);
                    }
                }
            }
            s.Status = s.AmbiguousSlots.Count > 0 || s.SlotConflicts.Count > 0 ? "ambiguous_slots" : "complete";
            return s;
        }
    }

    /// <summary>
    /// Игровые координаты улова: X (восток) и Z (север) мира игры в единицах игры (1 ед. = 1 м по батиметрии NEXT-ANDROID-02).
    /// Сырые значения c2s 1110 уже в этой системе — карта, батиметрия и зоны используют их без преобразования (NEXT-135),
    /// поэтому преобразование — тождественное. Координаты показываются только если они ПРОВЕРЕНЫ: улов привязан к забросу
    /// (cast_id) или совпадает с записанным забросом (допуск 0,05 — как в окне карты). Иначе — «недоступно».
    /// </summary>
    public static class GameCoordinates
    {
        public const string System = "game_world_xz";
        public const double CastMatchTolerance = 0.05;

        public static (double X, double Z)? Resolve(CatchData c)
        {
            if (!c.cast_x.HasValue || !c.cast_z.HasValue) return null;
            if (double.IsNaN(c.cast_x.Value) || double.IsNaN(c.cast_z.Value)) return null;
            bool verified = (c.cast_id.HasValue && c.cast_id.Value > 0) || c.coord_verified == true;
            return verified ? (c.cast_x.Value, c.cast_z.Value) : null;
        }

        public static string Status(CatchData c)
        {
            if (!c.cast_x.HasValue || !c.cast_z.HasValue) return "none";
            if (c.cast_id.HasValue && c.cast_id.Value > 0) return "cast_linked";
            if (c.coord_verified == true) return "cast_match";
            return "unverified";
        }

        public static string Format(CatchData c)
        {
            var g = Resolve(c);
            if (g == null) return c.cast_x.HasValue ? "не подтв." : "—";
            return string.Format(CultureInfo.InvariantCulture, "X {0:F1}  Z {1:F1}", g.Value.X, g.Value.Z);
        }
    }

    /// <summary>Грейд для показа: сырой windex (подтверждён по собственному объявлению) или метка по весу с пометкой «?».</summary>
    public static class GradeDisplay
    {
        public static string Text(CatchData c)
        {
            if (c.windex.HasValue)
                return c.windex.Value == 3 ? "класс 3 (трофей)" : "класс " + c.windex.Value.ToString(CultureInfo.InvariantCulture);
            string g = c.grade?.Trim() ?? "";
            return g.Length == 0 ? "—" : g + "?";
        }

        public const string Tooltip = "«класс N» — сырой windex из собственного объявления (проверено). " +
                                      "«ОБЫЧНЫЙ?» и т. п. — метка по весу, НЕ проверена (граница видоспецифична).";
    }

    /// <summary>
    /// Какие поля контекста — возможные входы будущего анализа, а какие — только справка (решение пользователя, NEXT-DATA-01):
    ///  * входы анализа: леска, поводок, крючок, кормушка (тип и сырые характеристики), наживка; плюс состав прикорма,
    ///    координаты, глубина, погода (в analysis_inputs_v);
    ///  * только справка: удочка, катушка, поплавок (поплавок — визуальная снасть; его тип/грузоподъёмность не анализируются);
    ///  * грузило и приманка сохраняются в cast_tackle/catch_context_v, но во входы не включены (нет решения пользователя).
    /// Модели и формулы здесь нет.
    /// </summary>
    public static class AnalysisEligibility
    {
        public static readonly string[] ReferenceOnly = { "rod", "reel", "float" };
        public static readonly string[] AnalysisSlots = { "line", "leader", "hook", "feeder", "bait" };
        public static readonly string[] StoredOnly = { "sinker", "lure" };
        public static bool IsAnalysisInput(string slot) => AnalysisSlots.Contains(slot) && !ReferenceOnly.Contains(slot);
    }
}
