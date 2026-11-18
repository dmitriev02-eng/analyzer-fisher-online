using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FisherLogger
{
    /// <summary>
    /// GB-CALC-01: калькулятор прикормки «выбрали рыбу → готовый рецепт».
    /// Что известно из игры (DeepSeek T20261115-1135-gbcalc):
    ///  * CONFIRMED: привлечение рыбы задаёт только ОСНОВА (сухой прикорм, wtype 37) или готовая смесь (wtype 38) — поле klevprop, kind 2 = целевой вид;
    ///    у смеси итоговый klevprop = klevprop основы (11/11 замесов). Аромат/добавка/наживка привязки к рыбе не несут.
    ///  * PROBABLE: аромат — как в готовой смеси игры для этой рыбы; добавка и наживка — те, в описании которых игра называет вид («ловится на»).
    ///  * Ваш журнал — частота целевой рыбы на забросах с этой смесью (не вероятность).
    /// Самопополнение: (1) база предметов перечитывается при обновлении файла; (2) klevprop, который сервер прислал для ваших замесов
    /// (cast_tackle.gb_klevprop), добавляется к основе, если в файлах его нет; (3) журнал пересчитывается по мере ваших забросов —
    /// как только у смеси наберётся ≥10 забросов и ≥2 целевые рыбы, появляется вариант «по вашему журналу».
    /// </summary>
    public static class GroundbaitCalc
    {
        public const int JournalMinN = 10, JournalMinK = 2;

        public sealed class Item
        {
            public long Id; public string Name = ""; public int Wtype; public int Cost; public bool Sold; public int Level;
            public long BaseBait;                                  // основа (wtype 39): какой наживке соответствует (base_params)
            public Dictionary<uint, double> Kind2 = new();         // целевые виды → модификатор
            public HashSet<uint> Fish = new();                     // «ловится на» (для наживок)
            public long AromaOf; public bool Observed;             // аромат готовой смеси; klevprop добавлен из ваших замесов
        }
        public sealed class Part { public string Slot = "", Name = "", Source = ""; public long Id; public int Cost; }
        public sealed class Recipe
        {
            public string Title = "", Why = ""; public List<Part> Parts = new(); public int N, K; public bool Ready; public bool FromGame;
            public int Cost => Parts.Sum(p => p.Cost);
            public string Summary => string.Join(" + ", Parts.Select(p => p.Name));
        }
        public sealed class Result { public List<Recipe> Recipes = new(); public string Note = ""; public bool GameKnows; public int ObservedAdded; }

        // ---------------- база предметов
        private static Dictionary<long, Item>? _items; private static string _itemsPath = ""; private static DateTime _itemsAt;
        private static readonly object Gate = new();
        public static string? ItemsPathOverride;

        public static Dictionary<long, Item> Items()
        {
            lock (Gate)
            {
                string? path = ItemsPathOverride ?? new[] { Safe(() => Path.Combine(PacketDeepParser.ResolveDefaultDataDir(), "items_full_database.json")), Safe(() => Path.Combine(AppPaths.DataDirectory, "items_full_database.json")) }
                    .FirstOrDefault(p => p != null && File.Exists(p));
                if (path == null) return _items ?? new();
                var mt = File.GetLastWriteTimeUtc(path);
                if (_items != null && _itemsPath == path && _itemsAt == mt) return _items;
                _items = Parse(path); _itemsPath = path; _itemsAt = mt;
                return _items;
            }
        }

        private static string? Safe(Func<string> f) { try { return f(); } catch { return null; } }
        private static long FirstId(string? s) => s != null && long.TryParse(s.Split('/', ',')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;

        public static Dictionary<uint, double> ParseKind2(string? klev)
        {
            var d = new Dictionary<uint, double>();
            foreach (var p in (klev ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = p.Split('/');
                if (f.Length >= 3 && f[0].Trim() == "2" && uint.TryParse(f[1], out uint fid) && double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m > 0) d[fid] = m;
            }
            return d;
        }

        private static Dictionary<long, Item> Parse(string path)
        {
            var res = new Dictionary<long, Item>();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var it in doc.RootElement.GetProperty("items").EnumerateArray())
                {
                    int w = it.TryGetProperty("wtype", out var wt) && wt.TryGetInt32(out int wv) ? wv : 0;
                    if (w is not (37 or 38 or 39 or 40 or 10)) continue;
                    string S(string k) => it.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    int I(string k) => it.TryGetProperty(k, out var v) && v.TryGetInt32(out int iv) ? iv : 0;
                    var x = new Item
                    {
                        Id = it.GetProperty("id").GetInt64(), Name = GameKnowledge.Readable(S("name_ru")), Wtype = w, Cost = I("cost_cr"), Level = I("min_level"),
                        BaseBait = w == 39 ? FirstId(S("base_params")) : 0
                    };
                    x.Sold = I("shop_count") > 0 && x.Cost > 0 && !S("info_ru").Contains("выведен из продажи", StringComparison.OrdinalIgnoreCase);
                    string klev = S("klevprop_raw");
                    if (it.TryGetProperty("add_options", out var ao) && ao.ValueKind == JsonValueKind.Object)
                    {
                        if (klev.Length == 0 && ao.TryGetProperty("klevprop", out var kp) && kp.ValueKind == JsonValueKind.String) klev = kp.GetString() ?? "";
                        if (ao.TryGetProperty("aroma", out var ar) && ar.ValueKind == JsonValueKind.String) x.AromaOf = FirstId(ar.GetString());
                    }
                    x.Kind2 = ParseKind2(klev);
                    if (it.TryGetProperty("fish_ids", out var fi) && fi.ValueKind == JsonValueKind.Array)
                        foreach (var f in fi.EnumerateArray()) if (f.TryGetUInt32(out uint fid)) x.Fish.Add(fid);
                    res[x.Id] = x;
                }
            }
            catch { }
            return res;
        }

        // ---------------- журнал
        private sealed class Use { public long Osnova, Aroma, Extend, Bait; public string Ready = ""; public string Klev = ""; }
        private sealed class Journal { public Dictionary<long, Use> ByCast = new(); public Dictionary<long, List<(uint fish, int? w)>> Catches = new(); }
        private static Journal? _j; private static string _jDb = ""; private static DateTime _jAt;

        private static Journal LoadJournal(string db)
        {
            lock (Gate)
            {
                if (_j != null && _jDb == db && (DateTime.UtcNow - _jAt).TotalMinutes < 5) return _j;
                var j = new Journal();
                try
                {
                    using var c = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False");
                    c.Open();
                    using (var q = new SqliteCommand("SELECT cast_id, gb_osnova, gb_aroma, gb_addextend, gb_addbaits, gb_klevprop, groundbait_name FROM cast_tackle WHERE cast_id IS NOT NULL AND (gb_osnova IS NOT NULL OR groundbait_name IS NOT NULL)", c))
                    using (var r = q.ExecuteReader())
                        while (r.Read())
                        {
                            string S(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                            var u = new Use { Osnova = FirstId(S(1)), Aroma = FirstId(S(2)), Extend = FirstId(S(3)), Bait = FirstId(S(4)), Klev = S(5) };
                            if (u.Osnova == 0) u.Ready = S(6);
                            if (u.Osnova == 0 && u.Ready.Length == 0) continue;
                            j.ByCast[r.GetInt64(0)] = u;
                        }
                    using (var q = new SqliteCommand("SELECT cast_id, fish_lang_id, windex FROM catches WHERE cast_id IS NOT NULL AND fish_lang_id IS NOT NULL", c))
                    using (var r = q.ExecuteReader())
                        while (r.Read())
                        {
                            long id = r.GetInt64(0);
                            if (!j.Catches.TryGetValue(id, out var l)) j.Catches[id] = l = new();
                            l.Add(((uint)r.GetInt64(1), r.IsDBNull(2) ? null : (int)r.GetInt64(2)));
                        }
                }
                catch { }
                _j = j; _jDb = db; _jAt = DateTime.UtcNow;
                return j;
            }
        }

        public static void ResetCache() { lock (Gate) { _j = null; _items = null; _gm = null; } }

        // ---------------- GB-LEARN-01: как основа меняет ШАНС (по вашим сравнениям)
        // Сравнение = одна удочка, одна наживка и крючок, одно время суток игры, одна точка (≤12 м), прикормка разная.
        // Пары внутри одной точки зависимы, поэтому N — число таких точек-сравнений. Множитель основы — медиана её отношения
        // к средней по точке, сжатый к 1: w = N/(N+10); не больше ×3 в любую сторону (само время суток даёт ×2–4).
        private static Dictionary<string, (double M, int N, int Waters)>? _gm; private static string _gmDb = ""; private static DateTime _gmAt;
        public static string GbKey(long osnova, string ready) => osnova > 0 ? "O:" + osnova.ToString(CultureInfo.InvariantCulture) : ready.Length > 0 ? "R:" + ready.Trim() : "none";

        public static Dictionary<string, (double M, int N, int Waters)> ChanceByBase(string? db)
        {
            lock (Gate)
            {
                if (db == null) return new();
                if (_gm != null && _gmDb == db && (DateTime.UtcNow - _gmAt).TotalMinutes < 10) return _gm;
                var res = new Dictionary<string, (double, int, int)>();
                try
                {
                    using var c = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False");
                    c.Open();
                    var casts = new List<(long id, string grp, double x, double z, string gb, uint water)>();
                    var lvl = new Dictionary<long, double>();
                    using (var q = new SqliteCommand(@"SELECT k.id, k.timestamp, k.session_id, k.rod_index, k.cast_x, k.cast_z, t.bait_name, t.hook_base_par, t.gb_osnova, t.groundbait_name,
       (SELECT MAX(s.location_id) FROM server_checks s WHERE s.cast_id = k.id)
FROM casts k JOIN cast_tackle t ON t.cast_id = k.id WHERE k.cast_x IS NOT NULL AND t.bait_name IS NOT NULL", c))
                    using (var r = q.ExecuteReader())
                        while (r.Read())
                        {
                            if (r.IsDBNull(10) || !DateTime.TryParse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
                            string S(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                            uint w = BiteAnalytics.WaterFromSpawn(r.GetInt64(10), new Dictionary<long, uint>()) ?? (uint)r.GetInt64(10);
                            string grp = string.Join("|", S(2), S(3), S(6), S(7), DayParts.Part(DayParts.GameHour(t)), w.ToString(CultureInfo.InvariantCulture));
                            casts.Add((r.GetInt64(0), grp, r.GetDouble(4), r.GetDouble(5), GbKey(FirstId(S(8)), S(9)), w));
                        }
                    using (var q = new SqliteCommand("SELECT cast_id, chance FROM cast_events WHERE event_type = 'wait' AND chance > 0 AND cast_id IS NOT NULL ORDER BY cast_id, timestamp", c))
                    using (var r = q.ExecuteReader())
                    {
                        var tmp = new Dictionary<long, List<double>>();
                        while (r.Read()) { long id = r.GetInt64(0); if (!tmp.TryGetValue(id, out var l)) tmp[id] = l = new(); l.Add(r.GetDouble(1)); }
                        foreach (var (id, l) in tmp) if (l.Count >= 3) lvl[id] = l.Skip(1).Take(3).OrderBy(v => v).ElementAt(1);   // медиана тактов 2–4
                    }
                    var logs = new Dictionary<string, List<(double lr, uint w)>>();
                    foreach (var g in casts.Where(k => lvl.ContainsKey(k.id)).GroupBy(k => k.grp))
                    {
                        // точки ≤12 м внутри группы
                        var pts = new List<List<(long id, double x, double z, string gb, uint water)>>();
                        foreach (var k in g)
                        {
                            var p = pts.FirstOrDefault(pp => Math.Sqrt(Math.Pow(pp[0].x - k.x, 2) + Math.Pow(pp[0].z - k.z, 2)) <= 12);
                            if (p == null) pts.Add(p = new());
                            p.Add((k.id, k.x, k.z, k.gb, k.water));
                        }
                        foreach (var p in pts.Where(pp => pp.Select(k => k.gb).Distinct().Count() >= 2))
                        {
                            var byGb = p.GroupBy(k => k.gb).ToDictionary(x => x.Key, x => x.Select(k => lvl[k.id]).OrderBy(v => v).ToList());
                            double Med(List<double> v) => v[v.Count / 2];
                            double mid = Math.Exp(byGb.Values.Average(v => Math.Log(Med(v))));
                            foreach (var (gb, v) in byGb)
                            {
                                if (!logs.TryGetValue(gb, out var l)) logs[gb] = l = new();
                                l.Add((Math.Log(Med(v) / mid), p[0].water));
                            }
                        }
                    }
                    foreach (var (gb, l) in logs)
                    {
                        var s = l.Select(x => x.lr).OrderBy(v => v).ToList();
                        double med = s[s.Count / 2], wgt = l.Count / (l.Count + 10.0);
                        res[gb] = (Math.Exp(Math.Clamp(med * wgt, -Math.Log(3), Math.Log(3))), l.Count, l.Select(x => x.w).Distinct().Count());
                    }
                }
                catch { }
                _gm = res; _gmDb = db; _gmAt = DateTime.UtcNow;
                return res;
            }
        }

        /// <summary>«По шансу: ×1,3 (3 сравнения)» — пусто, если сравнений нет.</summary>
        public static string ChanceNote(string? db, long osnova, string ready = "")
        {
            if (!ChanceByBase(db).TryGetValue(GbKey(osnova, ready), out var v) || v.N == 0) return "";
            string x = v.M.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"));
            return $" По вашим сравнениям (та же снасть и точка, другая прикормка) шанс с этой основой ×{x} — сравнений {v.N}" + (v.Waters > 1 ? $", водоёмов {v.Waters}" : "") +
                   (v.N < 5 ? "; пока мало, оценка осторожная." : ".");
        }

        private static double Wilson(int k, int n, double z = 1.28)
        {
            if (n == 0) return 0; double p = k / (double)n, d = 1 + z * z / n;
            return (p + z * z / (2 * n) - z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n))) / d;
        }

        // ---------------- расчёт
        public static Result For(uint fish, string? db, int minClass = 0)
        {
            var res = new Result();
            var items = Items();
            if (items.Count == 0) { res.Note = "Нет файла предметов игры (items_full_database.json) — рецепт рассчитать нельзя."; return res; }
            var j = db != null && File.Exists(db) ? LoadJournal(db) : new Journal();

            // самопополнение: klevprop, присланный сервером для ваших замесов
            foreach (var u in j.ByCast.Values.Where(u => u.Osnova > 0 && u.Klev.Length > 0).GroupBy(u => u.Osnova).Select(g => g.First()))
            {
                var k2 = ParseKind2(u.Klev);
                if (k2.Count == 0) continue;
                if (!items.TryGetValue(u.Osnova, out var it)) { it = new Item { Id = u.Osnova, Name = "Основа №" + u.Osnova, Wtype = 37, Sold = false }; items[u.Osnova] = it; }
                foreach (var (f, m) in k2) if (!it.Kind2.ContainsKey(f)) { it.Kind2[f] = m; it.Observed = true; res.ObservedAdded++; }
            }

            bool Target(long castId) => j.Catches.TryGetValue(castId, out var l) && l.Any(x => x.fish == fish && (minClass <= 0 || (x.w ?? -1) >= minClass));
            (int n, int k) Stat(Func<Use, bool> f) { int n = 0, k = 0; foreach (var (id, u) in j.ByCast) if (f(u)) { n++; if (Target(id)) k++; } return (n, k); }
            string Nm(long id) => items.TryGetValue(id, out var x) ? x.Name : "№" + id;
            int Cost(long id) => items.TryGetValue(id, out var x) ? x.Cost : 0;

            var osnovas = items.Values.Where(i => i.Wtype == 37 && i.Kind2.ContainsKey(fish) && (i.Sold || i.Observed)).OrderByDescending(i => i.Kind2[fish]).ThenBy(i => i.Kind2.Count).ThenBy(i => i.Cost).ToList();
            var readies = items.Values.Where(i => i.Wtype == 38 && i.Sold && i.Kind2.ContainsKey(fish)).OrderByDescending(i => i.Kind2[fish]).ThenBy(i => i.Cost).ToList();
            res.GameKnows = osnovas.Count > 0 || readies.Count > 0;
            var dips = items.Values.Where(i => i.Wtype == 40 && i.Sold).ToList();
            var baits = items.Values.Where(i => i.Wtype == 10 && i.Sold && i.Level <= 5 && i.Fish.Contains(fish)).OrderBy(i => i.Cost).ToList();
            var bases = items.Values.Where(i => i.Wtype == 39 && i.Sold && items.TryGetValue(i.BaseBait, out var bb) && bb.Fish.Contains(fish)).OrderBy(i => i.Cost).ToList();

            Part PickAroma(Item o)
            {
                var jr = j.ByCast.Where(kv => kv.Value.Osnova == o.Id && kv.Value.Aroma > 0).GroupBy(kv => kv.Value.Aroma)
                              .Select(g => (id: g.Key, n: g.Count(), k: g.Count(kv => Target(kv.Key)))).Where(x => x.k > 0).OrderByDescending(x => Wilson(x.k, x.n)).FirstOrDefault();
                if (jr.id > 0) return new Part { Slot = "аромат", Id = jr.id, Name = Nm(jr.id), Cost = Cost(jr.id), Source = $"ваш журнал: {jr.k} нужной рыбы на {UiKit.Casts(jr.n)}" };
                var fromReady = readies.Select(r => r.AromaOf).FirstOrDefault(a => a > 0 && items.TryGetValue(a, out var ai) && ai.Sold);
                if (fromReady > 0) return new Part { Slot = "аромат", Id = fromReady, Name = Nm(fromReady), Cost = Cost(fromReady), Source = "как в готовой смеси игры для этой рыбы" };
                var ch = dips.OrderBy(d => d.Cost).First();
                return new Part { Slot = "аромат", Id = ch.Id, Name = ch.Name, Cost = ch.Cost, Source = "любой — игра не связывает аромат с рыбой (выбран дешёвый)" };
            }
            Part? PickExtend()
            {
                var b = bases.FirstOrDefault();
                return b == null ? null : new Part { Slot = "добавка", Id = b.Id, Name = b.Name, Cost = b.Cost, Source = $"по описанию игры: на «{Nm(b.BaseBait)}» ловится эта рыба" };
            }
            Part? PickBait(Item o)
            {
                var jr = j.ByCast.Where(kv => kv.Value.Osnova == o.Id && kv.Value.Bait > 0).GroupBy(kv => kv.Value.Bait)
                              .Select(g => (id: g.Key, n: g.Count(), k: g.Count(kv => Target(kv.Key)))).Where(x => x.k > 0).OrderByDescending(x => Wilson(x.k, x.n)).FirstOrDefault();
                if (jr.id > 0) return new Part { Slot = "наживка", Id = jr.id, Name = Nm(jr.id), Cost = Cost(jr.id), Source = $"ваш журнал: {jr.k} нужной рыбы на {UiKit.Casts(jr.n)}" };
                var b = baits.FirstOrDefault();
                return b == null ? null : new Part { Slot = "наживка", Id = b.Id, Name = b.Name, Cost = b.Cost, Source = "по описанию игры: «ловится на»" };
            }
            Recipe Craft(Item o, string title, bool full)
            {
                var r = new Recipe { Title = title, FromGame = true };
                r.Parts.Add(new Part { Slot = "основа", Id = o.Id, Name = o.Name, Cost = o.Cost, Source = o.Observed ? "сервер прислал для вашего замеса (привлекает эту рыбу)" : "в файлах игры: привлекает эту рыбу" });
                r.Parts.Add(full ? PickAroma(o) : dips.OrderBy(d => d.Cost).Select(d => new Part { Slot = "аромат", Id = d.Id, Name = d.Name, Cost = d.Cost, Source = "любой (дешёвый)" }).First());
                if (full) { if (PickExtend() is { } e) r.Parts.Add(e); if (PickBait(o) is { } b) r.Parts.Add(b); }
                (r.N, r.K) = Stat(u => u.Osnova == o.Id);
                r.Why = "Основа привлекает эту рыбу по данным игры — это главное в смеси. Остальное улучшает рецепт по описаниям игры и вашему журналу (проверка, не гарантия).";
                return r;
            }

            // GB-LEARN-01: при равной силе привлечения в файлах игры — выше основа, которая по вашим сравнениям поднимала шанс
            var gm = ChanceByBase(db);
            osnovas = osnovas.OrderByDescending(i => i.Kind2[fish]).ThenByDescending(i => gm.TryGetValue(GbKey(i.Id, ""), out var v) ? v.M : 1.0).ThenBy(i => i.Kind2.Count).ThenBy(i => i.Cost).ToList();
            if (osnovas.Count > 0) { var gr = Craft(osnovas[0], "Лучший по данным игры", true); gr.Why += ChanceNote(db, osnovas[0].Id); res.Recipes.Add(gr); }

            // по вашему журналу: смесь с наибольшей (по нижней границе) долей нужной рыбы
            var groups = j.ByCast.GroupBy(kv => kv.Value.Osnova > 0 ? $"{kv.Value.Osnova}|{kv.Value.Aroma}|{kv.Value.Extend}|{kv.Value.Bait}" : "R|" + kv.Value.Ready)
                              .Select(g => (g, n: g.Count(), k: g.Count(kv => Target(kv.Key)))).Where(x => x.n >= JournalMinN && x.k >= JournalMinK)
                              .OrderByDescending(x => Wilson(x.k, x.n)).ToList();
            if (groups.Count > 0)
            {
                var (g, n, k) = groups[0]; var u = g.First().Value;
                var r = new Recipe { Title = "По вашему журналу", N = n, K = k, Ready = u.Osnova == 0 };
                if (u.Osnova > 0)
                {
                    r.Parts.Add(new Part { Slot = "основа", Id = u.Osnova, Name = Nm(u.Osnova), Cost = Cost(u.Osnova), Source = items.TryGetValue(u.Osnova, out var oi) && oi.Kind2.ContainsKey(fish) ? "в игре привлекает эту рыбу" : "игра не связывает с этой рыбой" });
                    foreach (var (slot, id) in new[] { ("аромат", u.Aroma), ("добавка", u.Extend), ("наживка", u.Bait) }) if (id > 0) r.Parts.Add(new Part { Slot = slot, Id = id, Name = Nm(id), Cost = Cost(id), Source = "ваш замес" });
                }
                else
                {
                    var ri = items.Values.FirstOrDefault(i => i.Wtype == 38 && string.Equals(i.Name.Trim(), u.Ready.Trim(), StringComparison.OrdinalIgnoreCase));
                    r.Parts.Add(new Part { Slot = "готовая", Id = ri?.Id ?? 0, Name = u.Ready, Cost = ri?.Cost ?? 0, Source = ri != null && ri.Kind2.ContainsKey(fish) ? "в игре привлекает эту рыбу" : "ваш журнал (игра не связывает с этой рыбой)" });
                }
                r.Why = $"На этой смеси нужная рыба была на {k} из {UiKit.Casts(n)}. Это частота по вашим данным (место и время не учитываются), а не гарантия." + ChanceNote(db, u.Osnova, u.Ready);
                if (res.Recipes.Count == 0 || res.Recipes[0].Summary != r.Summary) res.Recipes.Add(r);
                else { res.Recipes[0].N = n; res.Recipes[0].K = k; res.Recipes[0].Title += " · подтверждён вашим журналом"; }
            }

            if (readies.Count > 0)
            {
                var rd = readies[0];
                var r = new Recipe { Title = "Готовая, без замеса", Ready = true, FromGame = true };
                r.Parts.Add(new Part { Slot = "готовая", Id = rd.Id, Name = rd.Name, Cost = rd.Cost, Source = "в файлах игры: привлекает эту рыбу" });
                r.Why = "Готовая смесь из магазина с тем же целевым привлечением — проще, но без вашей наживки в составе.";
                res.Recipes.Add(r);
            }
            else if (osnovas.Count > 0)
            {
                var cheap = osnovas.Where(o => o.Sold).OrderBy(o => o.Cost).First();
                var r = Craft(cheap, "Подешевле", false);
                r.Why = "Минимальный замес: основа, которая привлекает эту рыбу, и самый дешёвый аромат.";
                if (!res.Recipes.Any(x => x.Summary == r.Summary)) res.Recipes.Add(r);
            }

            if (!res.GameKnows)
            {
                res.Note = "В файлах игры нет прикормки, нацеленной на эту рыбу." + (res.Recipes.Count > 0 ? " Ниже — то, что работало у вас." : " Подойдёт универсальная смесь; рецепт появится, когда наберётся ваш журнал.");
                if (res.Recipes.Count == 0)
                {
                    var uni = items.Values.Where(i => i.Wtype == 38 && i.Sold && i.Kind2.Count == 0).OrderBy(i => i.Cost).FirstOrDefault();
                    if (uni != null)
                    {
                        var r = new Recipe { Title = "Универсальная", Ready = true };
                        r.Parts.Add(new Part { Slot = "готовая", Id = uni.Id, Name = uni.Name, Cost = uni.Cost, Source = "без привязки к виду" });
                        (r.N, r.K) = Stat(u => u.Ready.Length > 0 && u.Ready.Contains(uni.Name.Trim('"'), StringComparison.OrdinalIgnoreCase));
                        r.Why = "Игра не задаёт для этой рыбы целевой прикормки — универсальная смесь без привязки к виду.";
                        res.Recipes.Add(r);
                    }
                }
            }
            return res;
        }

        public static string Money(int cr) => cr.ToString("#,0", CultureInfo.GetCultureInfo("ru-RU")) + " кр";
    }
}
