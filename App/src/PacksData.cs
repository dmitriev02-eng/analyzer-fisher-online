using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FisherLogger;

/// <summary>PACKS-01: предмет каталога (для калькулятора паков).</summary>
public sealed class PackItem
{
    public long Id; public string Name = ""; public int Wtype; public long CostCr; public bool Auctionable;
}

/// <summary>PACKS-01: цена предмета из трафика «Продам паки» (sale_limit: база=средняя, сегодня=текущая).</summary>
public sealed class PackPrice
{
    public long ItemId; public string Name = ""; public long BaseCr; public long TodayCr;
    public DateTime SeenAt; public int Sources;
    /// <summary>сегодня в % от средней (100 = как обычно).</summary>
    public int Pct => BaseCr > 0 ? (int)Math.Round(TodayCr * 100.0 / BaseCr) : 0;
    /// <summary>2 — пик (≥120% средней), 1 — выше, 0 — как обычно, -1 — ниже.</summary>
    public int Level => BaseCr <= 0 || TodayCr <= 0 ? 0 : TodayCr * 100 >= BaseCr * 120 ? 2 : TodayCr > BaseCr ? 1 : TodayCr == BaseCr ? 0 : -1;
    public string LevelText => Level switch { 2 => "↑↑ пик", 1 => "↑ выше средней", 0 => "= как обычно", _ => "↓ ниже средней" };
    public double Rub => TodayCr / 100.0;
    public double BaseRub => BaseCr / 100.0;
}

/// <summary>PACKS-01: торговый груз (wtype 57) — состав: 20×ресурс + 5 упаковок + 1 корд + работа.</summary>
public sealed class PackCargo
{
    public long Id; public string Name = "";
    public long IngredientId; public string IngredientName = ""; public int Count = 20;
    public long CostCr;                                     // cost_cr каталога (справочно)
    public int Packaging = 5; public long PackagingRub = 500;   // по скриншоту верстака
    public int Cord = 1; public long CordRub = 200;
    public long WorkRub = 200;                                 // «работа 200 OP»
}

/// <summary>PACKS-01: строка калькулятора.</summary>
public sealed class PackRow
{
    public PackCargo Cargo = new();
    public string IngredientName => Cargo.IngredientName;
    public long IngredientRub;          // цена ресурса (₽), 0 если неизвестна
    public string IngredientSource = "нет данных";
    public long CostRub;                // 20×ресурс + упаковка + корд + работа
    public long TodayRub;               // цена груза «сегодня»
    public long BaseRub;                // средняя цена груза
    public bool TodayFromTraffic;
    public int SoldToday;               // подтверждённые продажи (ok=item/count)
    public int HavePacks;               // сколько паков в инвентаре
    public int HaveIngredient;          // сколько ресурса в инвентаре
    public int PackagingOverride;       // эффективные настройки (для UI)
    public long PackagingPriceOverride;
    public long WorkOverride;
    public long Profit => TodayRub - CostRub;
    public int Pct => CostRub > 0 ? (int)Math.Round(Profit * 100.0 / CostRub) : 0;
    public bool Profitable => Profit > 0;
}

/// <summary>
/// PACKS-01: цены торговых паков и калькулятор.
/// Источник цен — трафик игры: пакет «Продам паки», структура sale_limit:
/// [item_id i32][count i32][len u8]"base|0|0|0|0"[len u8]"today|0|0|0|0"[12 байт] (повтор).
/// Цены в «копейках»: 39100 = 391 ₽.
/// </summary>
public static class PacksData
{
    public static string? CatalogPathOverride;
    public const int PackLimitPerDay = 10;

    /// <summary>
    /// PACKS-04: в каталоге есть «Грузы», которых нет в обычной игре (сезонные/событийные).
    /// Их не показываем: Груз мутомяса (2411), Груз Арктик (2419), Груз деликатесов (2600).
    /// </summary>
    private static readonly HashSet<long> NotCraftable = new() { 2411, 2419, 2600 };

    private static Dictionary<long, PackItem>? _items; private static string _itemsPath = ""; private static DateTime _itemsAt;
    private static readonly object Gate = new();

    private static string? DefaultDataDir()
    {
        try { return PacketDeepParser.ResolveDefaultDataDir(); } catch { return null; }
    }
    private static string? Safe(Func<string?> f) { try { return f(); } catch { return null; } }

    // ------------------------------------------------------------ каталог
    public static Dictionary<long, PackItem> Catalog()
    {
        lock (Gate)
        {
            string? path = CatalogPathOverride ?? new[]
            {
                Safe(() => DefaultDataDir() is string d ? Path.Combine(d, "items_full_database.json") : null),
                Safe(() => Path.Combine(AppPaths.DataDirectory, "items_full_database.json")),
            }.FirstOrDefault(p => p != null && File.Exists(p));
            if (path == null) return _items ?? new();
            var mt = File.GetLastWriteTimeUtc(path);
            if (_items != null && _itemsPath == path && _itemsAt == mt) return _items;
            _items = ParseCatalog(path); _itemsPath = path; _itemsAt = mt;
            return _items;
        }
    }

    private static Dictionary<long, PackItem> ParseCatalog(string path)
    {
        var res = new Dictionary<long, PackItem>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("items", out var arr)) return res;
            foreach (var it in arr.EnumerateArray())
            {
                long id = it.TryGetProperty("id", out var ide) && ide.TryGetInt64(out long iv) ? iv : 0;
                if (id <= 0) continue;
                string name = it.TryGetProperty("name_ru", out var ne) && ne.ValueKind == JsonValueKind.String ? (ne.GetString() ?? "") : "";
                int w = it.TryGetProperty("wtype", out var we) && we.TryGetInt32(out int wv) ? wv : 0;
                long cost = it.TryGetProperty("cost_cr", out var ce) && ce.TryGetInt64(out long cv) ? cv : 0;
                bool auck = it.TryGetProperty("sale_to_auck", out var ae) && ae.TryGetInt32(out int av) && av > 0;
                res[id] = new PackItem { Id = id, Name = name, Wtype = w, CostCr = cost, Auctionable = auck };
            }
        }
        catch { }
        return res;
    }

    // ------------------------------------------------------------ парсер sale_limit
    private static readonly Regex RowRe = new(@"^\s*(\d{1,9})\|(\d+)\|(\d+)\|(\d+)\|(\d+)\s*$", RegexOptions.Compiled);

    public static List<PackPrice> ParseSaleLimit(byte[] data, DateTime seenAt)
    {
        var res = new Dictionary<long, PackPrice>();
        var cat = Catalog();
        ReadOnlySpan<byte> key = "sale_limit"u8;
        int idx = 0;
        while (idx < data.Length)
        {
            int p = data.AsSpan(idx).IndexOf(key);
            if (p < 0) break;
            int q = idx + p + key.Length;
            // запись: [item i32][count i32][len u8]"base|..."[len u8]"today|..."[12 байт]
            while (q + 10 < data.Length)
            {
                long item = BitConverter.ToInt32(data, q);
                if (item <= 0 || item > 1_000_000) break;   // следующий блок/мусор
                int qq = q + 8;
                if (!TryStr(data, ref qq, out string s1) || !TryStr(data, ref qq, out string s2)) break;
                var m1 = RowRe.Match(s1); var m2 = RowRe.Match(s2);
                if (m1.Success && m2.Success)
                {
                    long b = long.Parse(m1.Groups[1].Value, CultureInfo.InvariantCulture);
                    long t = long.Parse(m2.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (b > 0 && t > 0)
                    {
                        string nm = cat.TryGetValue(item, out var it) ? it.Name : "#" + item;
                        if (res.TryGetValue(item, out var prev)) { prev.BaseCr = b; prev.TodayCr = t; prev.SeenAt = seenAt; prev.Sources++; }
                        else res[item] = new PackPrice { ItemId = item, Name = nm, BaseCr = b, TodayCr = t, SeenAt = seenAt, Sources = 1 };
                    }
                }
                qq += 12;
                q = qq;
            }
            idx = Math.Max(q, idx + p + 1);
        }
        return res.Values.OrderBy(v => v.Name, StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), true)).ToList();
    }

    private static bool TryStr(byte[] d, ref int q, out string s)
    {
        s = "";
        if (q + 2 > d.Length) return false;
        int n = d[q] | (d[q + 1] << 8);   // длина строки — u16 LE
        q += 2;
        if (n < 4 || n > 60 || q + n > d.Length) return false;
        s = System.Text.Encoding.ASCII.GetString(d, q, n); q += n; return true;
    }
    public static List<PackPrice> ParseSaleLimitFile(string capturePath, DateTime? seenAt = null)
    {
        try { return ParseSaleLimit(File.ReadAllBytes(capturePath), seenAt ?? File.GetLastWriteTime(capturePath)); }
        catch { return new(); }
    }

    public static Dictionary<long, int> ParseSales(byte[] data)
    {
        var res = new Dictionary<long, int>();
        string text = System.Text.Encoding.ASCII.GetString(data);
        foreach (Match m in Regex.Matches(text, @"ok=(\d+)/(\d+)"))
        {
            if (!long.TryParse(m.Groups[1].Value, out long id) || !int.TryParse(m.Groups[2].Value, out int n)) continue;
            res[id] = res.GetValueOrDefault(id) + n;
        }
        return res;
    }

    /// <summary>PACKS-01: инвентарь игрока из трафика — XML &lt;wpn_add&gt; с &lt;tid&gt;; считаем количество по id.</summary>
    public static Dictionary<long, int> ParseInventory(byte[] data)
    {
        var res = new Dictionary<long, int>();
        string text = System.Text.Encoding.ASCII.GetString(data);
        foreach (Match m in Regex.Matches(text, @"<wpn_add>.*?<tid>(\d+)</tid>.*?</wpn_add>", RegexOptions.Singleline))
            if (long.TryParse(m.Groups[1].Value, out long tid) && tid > 0)
                res[tid] = res.GetValueOrDefault(tid) + 1;
        return res;
    }

    public static Dictionary<long, int> ParseInventoryFile(string capturePath)
    {
        try { return ParseInventory(File.ReadAllBytes(capturePath)); }
        catch { return new(); }
    }
    // ------------------------------------------------------------ грузы
    public static List<PackCargo> Cargos()
    {
        var cat = Catalog();
        var list = new List<PackCargo>();
        foreach (var it in cat.Values)
            if (it.Wtype == 57 && it.Name.StartsWith("Груз", StringComparison.Ordinal) && !NotCraftable.Contains(it.Id))
                list.Add(new PackCargo { Id = it.Id, Name = it.Name, CostCr = it.CostCr });
        FillIngredients(list);
        return list.OrderBy(c => c.Name, StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), true)).ToList();
    }

    private static void FillIngredients(List<PackCargo> cargos)
    {
        string? path = Safe(() => DefaultDataDir() is string d ? Path.Combine(d, "items_full_database.json") : null)
                    ?? Safe(() => Path.Combine(AppPaths.DataDirectory, "items_full_database.json"));
        if (path == null || !File.Exists(path)) return;
        var byId = cargos.ToDictionary(c => c.Id);
        var cat = Catalog();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("items", out var arr)) return;
            foreach (var it in arr.EnumerateArray())
            {
                if (!it.TryGetProperty("id", out var ide) || !ide.TryGetInt64(out long id) || !byId.TryGetValue(id, out var cargo)) continue;
                string bp = it.TryGetProperty("base_params", out var bpe) && bpe.ValueKind == JsonValueKind.String ? (bpe.GetString() ?? "") : "";
                var parts = bp.Split('/');
                if (parts.Length >= 2 && long.TryParse(parts[0], out long ing) && int.TryParse(parts[1], out int count))
                { cargo.IngredientId = ing; cargo.Count = count > 0 ? count : 20; }
                if (cargo.IngredientId > 0 && cat.TryGetValue(cargo.IngredientId, out var ci)) cargo.IngredientName = ci.Name;
            }
        }
        catch { }
    }

    // ------------------------------------------------------------ калькулятор
    /// <summary>Себестоимость: count×интерес + упаковка + корд + работа (в ₽). Цена ресурса — из трафика, иначе каталог.</summary>
    public static PackRow Calc(PackCargo cargo, IReadOnlyDictionary<long, PackPrice>? prices, IReadOnlyDictionary<long, long>? manualRub = null,
                               int? packaging = null, long? packagingRub = null, long? workRub = null, IReadOnlyDictionary<long, int>? inv = null)
    {
        var cat = Catalog();
        var row = new PackRow
        {
            Cargo = cargo,
            PackagingOverride = packaging ?? cargo.Packaging,
            PackagingPriceOverride = packagingRub ?? cargo.PackagingRub,
            WorkOverride = workRub ?? cargo.WorkRub,
        };
        long ing = 0; string src = "нет данных";
        if (cargo.IngredientId > 0 && prices != null && prices.TryGetValue(cargo.IngredientId, out var pp) && pp.TodayCr > 0)
        { ing = (long)Math.Round(pp.Rub); src = "трафик (" + (pp.TodayCr / 100.0).ToString("0.##", CultureInfo.InvariantCulture) + " ₽, " + pp.LevelText + ")"; }
        else if (cargo.IngredientId > 0 && cat.TryGetValue(cargo.IngredientId, out var ci) && ci.CostCr > 0)
        { ing = ci.CostCr; src = "каталог (" + ci.CostCr + " ₽)"; }
        row.IngredientRub = ing; row.IngredientSource = src;
        if (inv != null) { row.HavePacks = inv.GetValueOrDefault(cargo.Id); row.HaveIngredient = inv.GetValueOrDefault(cargo.IngredientId); }
        row.CostRub = ing * Math.Max(0, cargo.Count) + row.PackagingOverride * row.PackagingPriceOverride + cargo.Cord * cargo.CordRub + row.WorkOverride;
        if (prices != null && prices.TryGetValue(cargo.Id, out var cp))
        { row.TodayRub = (long)Math.Round(cp.Rub); row.BaseRub = (long)Math.Round(cp.BaseRub); row.TodayFromTraffic = true; }
        if (manualRub != null && manualRub.TryGetValue(cargo.Id, out long m) && m > 0) { row.TodayRub = m; row.TodayFromTraffic = false; }
        return row;
    }

    public static List<PackRow> Rows(IReadOnlyDictionary<long, PackPrice>? prices, IReadOnlyDictionary<long, long>? manualRub = null,
                                     int? packaging = null, long? packagingRub = null, long? workRub = null, IReadOnlyDictionary<long, int>? inv = null)
        => Cargos().Select(c => Calc(c, prices, manualRub, packaging, packagingRub, workRub, inv)).ToList();
}
