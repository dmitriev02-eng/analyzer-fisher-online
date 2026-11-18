using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FisherLogger
{
    /// <summary>
    /// World X/Z → the grid coordinates the game shows on its map ("NNN:NNN").
    ///
    /// Game code (GameAssembly.dll SHA-256 7E61DAD3…, dump.cs of the same build):
    ///   Cartografer.realToGameCoord(Vector3 pos, bool isCeil = true)  RVA 0x83EE00:
    ///       t = Cartografer.activeTerrain;  g = (pos − t.transform.position) / 5.0f;  if isCeil → (int)Math.Ceiling(g.x), (int)Math.Ceiling(g.z)
    ///   Cartografer.get_activeTerrain()  RVA 0x83EBB0:
    ///       sd = LocNewLogic.sd (SceneData);  if (sd.mainTerrain == null || !sd.isMultiTerrain) → UnityEngine.Terrain.activeTerrain
    ///       else → sd.mainTerrain.GetComponent&lt;Terrain&gt;()
    /// So the origin is the world position of the waterbody's active terrain. For a scene with exactly ONE terrain both branches
    /// give that terrain; its position is read from the installed scene bundle (NEXT-127 scan; game data unchanged since — see
    /// NEXT-MAP-ALL-WATERBODIES-01). The grid size is terrain size / 5.
    ///
    /// Per-waterbody registry: one entry per location id, each with its own origin and evidence. No universal origin.
    /// Display only: stored coordinates are never changed.
    /// </summary>
    public static class MapGridCoordinates
    {
        public enum Status
        {
            VERIFIED_FROM_GAME_CODE_AND_SAMPLES,
            VERIFIED_FROM_GAME_CODE,
            VERIFIED_FROM_SAMPLES,
            PARTIALLY_VERIFIED,
            UNVERIFIED,
            NOT_APPLICABLE
        }

        /// <summary>One waterbody's transform. Origin/size are null when not established (never guessed).</summary>
        public sealed record Transform(
            uint LocationId, string Name, string Bundle, uint? ParentId,
            float? OriginX, float? OriginZ, float? SizeX, float? SizeZ,
            float Cell, string AxisMapping, string Rounding, string Boundary,
            Status Verification, string Evidence, int SampleCount, int SampleExact, string GameVersion, string VerifiedOn)
        {
            public bool HasTransform => OriginX.HasValue && OriginZ.HasValue && SizeX.HasValue && SizeZ.HasValue;
            public int? MaxCellX => SizeX.HasValue ? (int)MathF.Ceiling(SizeX.Value / Cell) : null;
            public int? MaxCellY => SizeZ.HasValue ? (int)MathF.Ceiling(SizeZ.Value / Cell) : null;
            /// <summary>Legacy field (NEXT-MAP-COORDINATES-02): max cell index along X.</summary>
            public int MaxCell => MaxCellX ?? 0;
            public bool Displayable => HasTransform && DisplayStatuses.Contains(Verification);
        }

        public const float CellSize = 5f;
        public const string GameBuild = "GameAssembly 7E61DAD3D198C7D1…; theFisher_Data = baseline NEXT-127 (4933 файла без изменений)";

        /// <summary>Which statuses are shown as NNN:NNN in the Catch History. Everything else keeps the raw world X/Z.</summary>
        public static readonly IReadOnlyCollection<Status> DisplayStatuses = new[]
        {
            Status.VERIFIED_FROM_GAME_CODE_AND_SAMPLES,
            Status.VERIFIED_FROM_GAME_CODE,
        };

        private const string Axis = "первое число = +X (восток), второе = +Z (север)";
        private const string Round = "ceil((мир − начало)/5), float";
        private const string Bound = "вне террейна → сырые X/Z";
        private const string CodeEv = "код: realToGameCoord + activeTerrain; сцена: ровно один активный Terrain (NEXT-127/135), все точки спавна внутри него";
        private const string Date = "2026-11-08";

        private static Transform Code(uint id, string name, string bundle, float ox, float oz, float sx, float sz, string extra = "") =>
            new(id, name, bundle, null, ox, oz, sx, sz, CellSize, Axis, Round, Bound, Status.VERIFIED_FROM_GAME_CODE,
                CodeEv + (extra.Length > 0 ? "; " + extra : "") + "; замеров в игре нет", 0, 0, GameBuild, Date);

        /// <summary>Code-derived transform plus a few exact in-game readings (fewer than 2 → status stays VERIFIED_FROM_GAME_CODE).</summary>
        private static Transform Sampled(uint id, string name, string bundle, float ox, float oz, float sx, float sz, string extra, string samples, int n, int exact) =>
            new(id, name, bundle, null, ox, oz, sx, sz, CellSize, Axis, Round, Bound,
                n >= 2 && exact == n ? Status.VERIFIED_FROM_GAME_CODE_AND_SAMPLES : Status.VERIFIED_FROM_GAME_CODE,
                CodeEv + (extra.Length > 0 ? "; " + extra : "") + "; " + samples, n, exact, GameBuild, Date);

        private static Transform None(uint id, string name, string bundle, Status st, string why) =>
            new(id, name, bundle, null, null, null, null, null, CellSize, Axis, Round, Bound, st, why, 0, 0, GameBuild, Date);

        /// <summary>All 36 location records of the installed game (maps.txt). Origins only where the scene gives them.</summary>
        public static readonly IReadOnlyDictionary<uint, Transform> Registry = new Transform[]
        {
            Sampled(1, "Германия: оз. Вассердам", "b1", -1024, -1024, 2048, 2048, "SceneData: isMultiTerrain=0, mainTerrain = этот Terrain",
                "1 замер в игре 2026-11-08 (телепорт через «Путешествия» на точку прибытия, координаты игрока на карте; точка подтверждена getSpawnPoint в Player.log): 1/0 (6.74, −57.31) → 207:194 — точно", 1, 1),
            new(2, "Россия: р. Пра", "b2", null, -750, -750, 1500, 1500, CellSize, Axis, Round, Bound, Status.VERIFIED_FROM_GAME_CODE_AND_SAMPLES,
                CodeEv + "; SceneData: isMultiTerrain=0; 9 замеров в игре (NEXT-131): 7 точно, 2 — на 1 клетку по Y (телепорт рядом со спавном, PROBABLE)",
                9, 7, GameBuild, Date),
            new(3, "Франция: Пруды Перруше", "b3", null, -512, -512, 1500, 1500, CellSize, Axis, Round, Bound, Status.VERIFIED_FROM_GAME_CODE_AND_SAMPLES,
                CodeEv + "; SceneData: isMultiTerrain=0; 4 замера в игре 2026-11-08 (телепорт на точку спавна, [LOCATION] в журнале FisherLogger): " +
                "31/0 (4.49, 53.64) → 104:114, 31/1 (346.51, 229.61) → 172:149, 31/3 (431.85, 393.33) → 189:182, 31/5 (−43.36, 459.20) → 94:195 — 4 из 4 точно " +
                "(начало −512 не по центру — проверяет именно данные сцены)", 4, 4, GameBuild, Date),
            Sampled(4, "Финляндия: оз. Кулгу", "b4", -1000, -1000, 2000, 2000, "",
                "1 замер в игре 2026-11-08 (телепорт через «Путешествия» на точку прибытия, координаты игрока на карте; точка подтверждена getSpawnPoint в Player.log): 41/0 (−299.10, 313.74) → 141:263 — точно", 1, 1),
            Sampled(5, "Херсон: Дельта Днепра", "b5", -500, -500, 1600, 1600, "",
                "1 замер в игре 2026-11-08 (телепорт через «Путешествия» на точку прибытия, координаты игрока на карте; точка подтверждена getSpawnPoint в Player.log): 51/0 (79.25, −67.44) → 116:87 — точно", 1, 1),
            new(6, "Беларусь: Пруд Дукора", "b6", null, -500, -500, 1000, 1000, CellSize, Axis, Round, Bound, Status.VERIFIED_FROM_GAME_CODE_AND_SAMPLES,
                CodeEv + "; 3 замера в игре 2026-11-08 (телепорт на точку спавна, координаты игрока на карте; точка подтверждена [LOCATION] в журнале FisherLogger): " +
                "63/1 (−1.81, 119.60) → 100:124, 62/0 (112.84, −264.06) → 123:48, 61/0 (−1.53, −76.05) → 100:85 — 3 из 3 точно; " +
                "клетка 5 м подтверждена расстоянием до маркера (77:127 → 107:110 = 173 м)", 3, 3, GameBuild, Date),
            Sampled(7, "Италия: р. По", "b7", -800, -800, 1024, 1024, "",
                "1 замер в игре 2026-11-08 (телепорт через «Путешествия» на точку прибытия, координаты игрока на карте; точка подтверждена getSpawnPoint в Player.log): 71/0 (−599.99, −325.37) → 41:95 — точно", 1, 1),
            Sampled(8, "Польша: Карпаты", "b8", -500, -500, 1000, 1000, "",
                "2 замера в игре 2026-11-08 (телепорт на точку спавна, координаты игрока на карте; точка подтверждена getSpawnPoint в Player.log): 81/0 (41.30, 26.50) → 109:106, 82/0 (241.83, −182.88) → 149:64 — 2 из 2 точно", 2, 2),
            Code(9, "Сербия: р. Дунай", "b9", -2000, -2000, 4000, 4000),
            Code(10, "Азербайджан: Каспий", "b10", -2000, -2000, 4000, 4000),
            Code(11, "Норвегия: Нейдагер-фьорд", "b11", -2048, -2048, 4096, 4096, "SceneData: isMultiTerrain=0"),
            Sampled(12, "Чехия: пруд Рыбавицы", "b12", -500, -500, 1000, 1000, "SceneData: isMultiTerrain=0",
                "3 замера в игре 2026-11-08 (телепорт на точку спавна, координаты игрока на карте; точка подтверждена getSpawnPoint в Player.log): 121/0 (−180.90, −274.57) → 64:46, 121/3 (−321.22, −202.89) → 36:60, 121/2 (−239.79, −170.12) → 53:66 — 3 из 3 точно", 3, 3),
            Sampled(13, "США: \"1000 островов\"", "b13", -1000, -1000, 2000, 2000, "",
                "1 замер в игре 2026-11-08 (телепорт через «Путешествия» на точку прибытия, координаты игрока на карте; точка подтверждена getSpawnPoint в Player.log): 131/0 (−417.75, 388.85) → 117:278 — точно", 1, 1),
            Sampled(14, "Нидерланды: канал", "b14", -500, -500, 1000, 1000, "",
                "1 замер в игре 2026-11-08 (телепорт через «Путешествия» на точку прибытия, координаты игрока на карте; точка подтверждена getSpawnPoint в Player.log): 141/0 (−314.10, 275.62) → 38:156 — точно", 1, 1),
            Code(15, "Атом: пустоши", "b15", -500, -500, 1000, 1000, "SceneData: isMultiTerrain=0"),
            Sampled(16, "Россия: р. Енисей", "b16", -1000, -1000, 2000, 2000, "SceneData: isMultiTerrain=0",
                "1 замер в игре 2026-11-08 (телепорт через «Путешествия» на точку прибытия, координаты игрока на карте; точка подтверждена getSpawnPoint в Player.log): 161/0 (−267.77, −76.76) → 147:185 — точно", 1, 1),
            Code(17, "Шотландия: Лох-Мaро", "b17", -1500, -1500, 3500, 3500),
            Code(18, "Казахстан: оз Жусымбай", "b18", -1024, -1024, 2048, 2048, "SceneData: isMultiTerrain=0"),
            Code(19, "Коми: Верхняя Печора", "b19", -1500, -1500, 3000, 3000, "SceneData: isMultiTerrain=0"),
            Code(20, "Архипелаг Мадейра", "b20", -2048, -2048, 4096, 4096, "карта в FisherLogger не включена (NEXT-135)"),
            Code(21, "Россия: р.Дон", "b21", -1500, -1500, 3000, 3000),
            Sampled(22, "Швейцария: Альпы", "b22", -500, -500, 1000, 1000, "",
                "1 замер в игре 2026-11-08 (телепорт через «Путешествия» на точку прибытия, координаты игрока на карте; точка подтверждена getSpawnPoint в Player.log): 221/0 (−39.21, −261.34) → 93:48 — точно", 1, 1),
            Sampled(23, "Китай: оз. Хэнань", "b23", -1024, -1024, 2048, 2048, "SceneData: isMultiTerrain=0",
                "1 замер в игре 2026-11-08 (телепорт через «Путешествия» на точку прибытия, координаты игрока на карте; точка подтверждена getSpawnPoint в Player.log): 231/0 (−434.61, 566.90) → 118:319 — точно", 1, 1),
            Code(24, "Гренландия: Тинитекилаак", "b24", -2048, -2048, 4096, 4096),
            Sampled(25, "Болгария: Луда Камчия", "b25", 0, 0, 512, 512,
                "3 Terrain 512×512 в бандле: (0,0) активен и он же SceneData.mainTerrain, (0,−512) и (512,0) выключены; все 8 точек спавна в (0,0)",
                "1 замер в игре 2026-11-08 (телепорт через «Путешествия» на точку прибытия, координаты игрока на карте; точка подтверждена getSpawnPoint в Player.log): 251/0 (311.00, 292.70) → 63:59 — точно; при начале (0,−512) было бы 63:161, при (512,0) точка вне террейна → в игре действует террейн (0,0)", 1, 1),
            None(26, "Россия: р.Волга", "—", Status.NOT_APPLICABLE, "в установленной игре нет бандла сцены"),
            Code(27, "Бавария: р.Лех", "b27", -500, -500, 1000, 1000, "SceneData: isMultiTerrain=0"),
            Code(28, "Россия: р. Кола", "b28", -1000, -1000, 2000, 2000, "SceneData: isMultiTerrain=0"),
            Code(29, "Испания водхр. Эбро", "b29", -2048, -2048, 4096, 4096, "SceneData: isMultiTerrain=0"),
            None(30, "Беларусь рыбхоз Ельский", "—", Status.NOT_APPLICABLE, "в установленной игре нет бандла сцены"),
            None(31, "Вьетнам р. Меконг", "—", Status.NOT_APPLICABLE, "в установленной игре нет бандла сцены"),
            Code(100, "Дом рыбака: Нёман", "b100", -250, -250, 500, 500),
            Sampled(101, "Поместье Карпинских", "b101", -500, -500, 1000, 1000, "SceneData: isMultiTerrain=0; карта в FisherLogger не включена",
                "1 замер в игре 2026-11-08 (телепорт через «Путешествия» на точку прибытия, координаты игрока на карте; точка подтверждена getSpawnPoint в Player.log): 1011/0 (165.15, −5.10) → 134:99 — точно", 1, 1),
            None(102, "Таверна \"Гарцующий карп\"", "—", Status.NOT_APPLICABLE, "в установленной игре нет бандла сцены"),
            None(180, "Зимняя сказка", "—", Status.NOT_APPLICABLE, "в установленной игре нет бандла сцены"),
            Code(185, "Спасение «Челюскина»", "b185", -1000, -1000, 2000, 2000,
                 "SceneData: isMultiTerrain=0; префаб home_ny_185 — FisherLogger не получает этот location_id из Player.log"),
        }.ToDictionary(t => t.LocationId);

        /// <summary>Entries shown as NNN:NNN (compatibility name from NEXT-MAP-COORDINATES-02).</summary>
        public static IReadOnlyDictionary<uint, Transform> Verified =>
            Registry.Where(kv => kv.Value.Displayable).ToDictionary(kv => kv.Key, kv => kv.Value);

        public static Transform? Entry(uint? locationId) =>
            locationId.HasValue && Registry.TryGetValue(locationId.Value, out var t) ? t : null;

        /// <summary>Displayable transform for the waterbody, or null.</summary>
        public static Transform? For(uint? locationId) => Entry(locationId) is { Displayable: true } t ? t : null;

        /// <summary>Pure conversion with an explicit transform (for tests / diagnostics). Null outside the terrain.</summary>
        public static (int X, int Y)? Convert(Transform t, double x, double z)
        {
            if (!t.HasTransform || double.IsNaN(x) || double.IsNaN(z) || double.IsInfinity(x) || double.IsInfinity(z)) return null;
            float fx = (float)x, fz = (float)z, ox = t.OriginX!.Value, oz = t.OriginZ!.Value;
            if (fx < ox || fz < oz || fx > ox + t.SizeX!.Value || fz > oz + t.SizeZ!.Value) return null;   // outside the terrain
            float gx = MathF.Ceiling((fx - ox) / t.Cell);
            float gy = MathF.Ceiling((fz - oz) / t.Cell);
            return ((int)gx, (int)gy);
        }

        /// <summary>Grid cell like the game (single-precision, ceil). Null if the waterbody is not displayable or the point is outside its terrain.</summary>
        public static (int X, int Y)? ToGrid(uint? locationId, double x, double z)
        {
            var t = For(locationId);
            return t == null ? null : Convert(t, x, z);
        }

        public static string FormatGrid((int X, int Y) g) =>
            g.X.ToString(CultureInfo.InvariantCulture) + ":" + g.Y.ToString(CultureInfo.InvariantCulture);

        /// <summary>Status text for one registry entry: code-derived entries with a single in-game reading say so.</summary>
        public static string StatusText(Transform t) =>
            t.Verification == Status.VERIFIED_FROM_GAME_CODE && t.SampleCount > 0
                ? "по коду и данным сцены игры; сверено " + t.SampleCount.ToString(CultureInfo.InvariantCulture) + " замером в игре (" +
                  t.SampleExact.ToString(CultureInfo.InvariantCulture) + " точно)"
                : StatusText(t.Verification);

        public static string StatusText(Status s) => s switch
        {
            Status.VERIFIED_FROM_GAME_CODE_AND_SAMPLES => "проверено по коду игры и замерам в игре",
            Status.VERIFIED_FROM_GAME_CODE => "по коду и данным сцены игры; замерами в игре не сверено",
            Status.VERIFIED_FROM_SAMPLES => "по замерам в игре",
            Status.PARTIALLY_VERIFIED => "проверено частично",
            Status.UNVERIFIED => "пересчёт не установлен",
            _ => "пересчёт неприменим"
        };

        /// <summary>Catch History cell: "NNN:NNN" for a displayable waterbody and verified coordinates; otherwise the existing raw display.</summary>
        public static string Format(CatchData c)
        {
            var w = GameCoordinates.Resolve(c);
            if (w == null) return GameCoordinates.Format(c);
            var g = ToGrid(c.location_id, w.Value.X, w.Value.Z);
            return g == null ? GameCoordinates.Format(c) : FormatGrid(g.Value);
        }

        /// <summary>Tooltip line for the coordinates cell: grid (if shown), original world X/Z, waterbody, conversion status.</summary>
        public static string Tooltip(CatchData c)
        {
            var w = GameCoordinates.Resolve(c);
            if (w == null)
                return c.cast_x.HasValue ? "Координаты: не подтверждены забросом — не показываются." : "Координаты: нет.";
            string raw = string.Format(CultureInfo.InvariantCulture, "мировые X {0:F1}  Z {1:F1}", w.Value.X, w.Value.Z);
            var e = Entry(c.location_id);
            if (e == null)
                return "Координаты: " + raw + " (водоём неизвестен — пересчёт в координаты карты не применён).";
            var t = For(c.location_id);
            if (t == null)
                return "Координаты: " + raw + " (" + e.Name + ": " + StatusText(e) + ").";
            var g = Convert(t, w.Value.X, w.Value.Z);
            if (g == null)
                return "Координаты: " + raw + " (" + t.Name + ": точка вне карты водоёма — пересчёт не применён).";
            return "Координаты карты в игре: " + FormatGrid(g.Value) + " (" + t.Name + "; " + StatusText(t) + "); " + raw + ".";
        }

        /// <summary>Developer diagnostic: the registry as a text table (optionally with one world point converted).</summary>
        public static string DiagnosticReport(uint? locationId = null, double? x = null, double? z = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("id | водоём | бандл | начало X,Z | размер | клеток | статус | показ | замеров (точно) | доказательства");
            foreach (var t in Registry.Values.OrderBy(t => t.LocationId))
            {
                if (locationId.HasValue && t.LocationId != locationId.Value) continue;
                string org = t.HasTransform ? string.Format(CultureInfo.InvariantCulture, "{0},{1}", t.OriginX, t.OriginZ) : "—";
                string size = t.HasTransform ? string.Format(CultureInfo.InvariantCulture, "{0}×{1}", t.SizeX, t.SizeZ) : "—";
                string cells = t.HasTransform ? t.MaxCellX + "×" + t.MaxCellY : "—";
                sb.Append(t.LocationId).Append(" | ").Append(t.Name).Append(" | ").Append(t.Bundle).Append(" | ").Append(org).Append(" | ").Append(size)
                  .Append(" | ").Append(cells).Append(" | ").Append(t.Verification).Append(" | ").Append(t.Displayable ? "NNN:NNN" : "сырые X/Z")
                  .Append(" | ").Append(t.SampleCount).Append(" (").Append(t.SampleExact).Append(") | ").Append(t.Evidence).AppendLine();
                if (x.HasValue && z.HasValue)
                {
                    var g = Convert(t, x.Value, z.Value);
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "    X {0} Z {1} → {2}", x, z, g == null ? "вне террейна / нет пересчёта" : FormatGrid(g.Value)));
                }
            }
            return sb.ToString();
        }
    }
}
