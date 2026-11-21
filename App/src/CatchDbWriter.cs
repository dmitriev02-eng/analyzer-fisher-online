using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace FisherLogger
{
    public sealed class SessionStats
    {
        public int Total { get; set; }
        public double AverageWeight { get; set; }
        public string TopBait { get; set; } = "(не определена)";
        public int TopBaitCount { get; set; }
        public string TopFish { get; set; } = "???";
        public int TopFishCount { get; set; }
    }

    /// <summary>
    /// Надёжное хранилище поимок в SQLite.
    /// Соединение держится открытым на время сессии; каждая запись коммитится
    /// сразу, поэтому аварийное завершение не теряет уже сохранённые поимки.
    /// </summary>
    public sealed class CatchDbWriter : IDisposable
    {
        private readonly object _sync = new();
        private readonly string _dbPath;
        private SqliteConnection? _connection;
        private string _sessionId = Guid.NewGuid().ToString("N");
        private bool _disposed;
        private readonly ConcurrentDictionary<long, Task> _pendingWrites = new();
        private long _writeSequence;

        public string DbPath => _dbPath;

        public string SessionId
        {
            get { lock (_sync) return _sessionId; }
        }

        public CatchDbWriter(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
                throw new ArgumentException("Не задан путь к БД.", nameof(dbPath));

            _dbPath = dbPath;
            string? dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            _connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = false
            }.ToString());
            _connection.Open();
            InitializeDatabase();
        }

        public void StartNewSession()
        {
            lock (_sync)
            {
                _sessionId = Guid.NewGuid().ToString("N");
            }
        }

        private void InitializeDatabase()
        {
            Execute("PRAGMA journal_mode=WAL;");
            Execute("PRAGMA synchronous=NORMAL;");
            Execute(@"
CREATE TABLE IF NOT EXISTS catches (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    record_id TEXT,
    timestamp TEXT NOT NULL,
    fish_name TEXT,
    fish_lang_id INTEGER,
    network_fish_id INTEGER,
    weight INTEGER,
    grade TEXT,
    bait_id INTEGER,
    bait_name TEXT,
    bait_category TEXT,
    depth_cm REAL,
    rod_id INTEGER,
    reel_id INTEGER,
    line_id INTEGER,
    hook_id INTEGER,
    float_id INTEGER,
    feeder_id INTEGER,
    equipment_source TEXT,
    cast_x REAL,
    cast_y REAL,
    cast_z REAL,
    air_temp REAL,
    wind_ms REAL,
    humidity REAL,
    location_id INTEGER,
    experience INTEGER,
    quality INTEGER,
    quality_float REAL,
    bait_uid INTEGER,
    range_values TEXT,
    spawn_locid INTEGER,
    source TEXT DEFAULT 'auto',
    autodetect_source TEXT,
    status TEXT,
    experience_formula TEXT,
    wind_kmh REAL,
    session_id TEXT
);");

            // Совместимость с ранними/упрощёнными схемами.
            EnsureColumn("record_id", "TEXT");
            EnsureColumn("bait_category", "TEXT");
            EnsureColumn("status", "TEXT");
            EnsureColumn("experience_formula", "TEXT");
            EnsureColumn("wind_kmh", "REAL");
            EnsureColumn("autodetect_source", "TEXT");
            EnsureColumn("quality_float", "REAL");
            EnsureColumn("bait_uid", "INTEGER");
            EnsureColumn("range_values", "TEXT");
            EnsureColumn("spawn_locid", "INTEGER");
            EnsureColumn("rod_id", "INTEGER");
            EnsureColumn("reel_id", "INTEGER");
            EnsureColumn("line_id", "INTEGER");
            EnsureColumn("hook_id", "INTEGER");
            EnsureColumn("float_id", "INTEGER");
            EnsureColumn("feeder_id", "INTEGER");
            EnsureColumn("equipment_source", "TEXT");
            EnsureColumn("baseid", "INTEGER");
            EnsureColumn("source_key", "TEXT");

            Execute("CREATE UNIQUE INDEX IF NOT EXISTS idx_record ON catches(record_id);");
            Execute(@"CREATE TABLE IF NOT EXISTS casts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT,
    session_id TEXT,
    rod_index INTEGER,
    cast_x REAL,
    cast_y REAL,
    cast_z REAL,
    bait_uid INTEGER,
    bait_tid INTEGER,
    bait_name TEXT,
    outcome TEXT DEFAULT 'pending',
    source_key TEXT
);");
            Execute(@"CREATE TABLE IF NOT EXISTS cast_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    cast_id INTEGER,
    timestamp TEXT,
    event_type TEXT,
    is_client INTEGER,
    fish_lang_id INTEGER,
    weight INTEGER,
    windex INTEGER,
    chance REAL,
    raw_text TEXT,
    match_status TEXT DEFAULT 'UNVERIFIED',
    source_key TEXT
);");
            EnsureColumnOn("casts", "source_key", "TEXT");
            EnsureColumnOn("cast_events", "source_key", "TEXT");
            Execute(@"CREATE UNIQUE INDEX IF NOT EXISTS ux_casts_src ON casts(source_key) WHERE source_key IS NOT NULL;");
            Execute(@"CREATE UNIQUE INDEX IF NOT EXISTS ux_events_src ON cast_events(source_key) WHERE source_key IS NOT NULL;");
Execute(@"CREATE TABLE IF NOT EXISTS weather (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT,
    session_id TEXT,
    baseid INTEGER,
    location_id INTEGER,
    cloudly REAL,
    fog REAL,
    rain REAL,
    tempr REAL,
    grom REAL,
    units_status TEXT DEFAULT 'UNVERIFIED',
    source_key TEXT
);");
            EnsureColumnOn("weather", "source_key", "TEXT");
            Execute(@"CREATE UNIQUE INDEX IF NOT EXISTS ux_weather_src ON weather(source_key) WHERE source_key IS NOT NULL;");
            Execute("CREATE INDEX IF NOT EXISTS idx_fish ON catches(fish_name);");
            Execute("CREATE INDEX IF NOT EXISTS idx_bait ON catches(bait_name);");
            Execute("CREATE INDEX IF NOT EXISTS idx_date ON catches(timestamp);");
            Execute("CREATE INDEX IF NOT EXISTS idx_session ON catches(session_id);");

            Execute(@"
CREATE TABLE IF NOT EXISTS other_players_catches (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT NOT NULL,
    player_name TEXT,
    clan TEXT,
    fish_name TEXT,
    weight INTEGER,
    my_location_id INTEGER,
    my_cast_x REAL,
    my_cast_z REAL,
    distance_meters REAL
);");
            Execute("CREATE INDEX IF NOT EXISTS idx_other_fish ON other_players_catches(fish_name);");
            Execute("CREATE INDEX IF NOT EXISTS idx_other_player ON other_players_catches(player_name);");
            Execute("CREATE INDEX IF NOT EXISTS idx_other_date ON other_players_catches(timestamp);");

            Execute(@"
CREATE TABLE IF NOT EXISTS feed_actions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT NOT NULL,
    bait_uid INTEGER,
    bait_item_id INTEGER,
    bait_name TEXT,
    has_bait INTEGER,
    session_id TEXT
);");
            Execute("CREATE INDEX IF NOT EXISTS idx_feed_date ON feed_actions(timestamp);");

            Execute(@"
CREATE TABLE IF NOT EXISTS craft_actions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT NOT NULL,
    main_wpn_uid INTEGER,
    main_wpn_item_id INTEGER,
    aroma_wpn_uid INTEGER,
    aroma_wpn_item_id INTEGER,
    add_wpn_uid INTEGER,
    add_wpn_item_id INTEGER,
    bait_uid INTEGER,
    bait_item_id INTEGER,
    session_id TEXT
);");
            Execute("CREATE INDEX IF NOT EXISTS idx_craft_date ON craft_actions(timestamp);");

            // NEXT-GTWC-01: публичные события игры (только добавление таблиц; существующие таблицы не меняются).
            // public_catches — объявления об уловах игроков (cmd 6 локация / cmd 5 клуб). Координат и снастей в событии нет.
            Execute(@"
CREATE TABLE IF NOT EXISTS public_catches (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    received_at TEXT NOT NULL,
    server_time TEXT,
    game_time_est TEXT,
    game_time_unc_min INTEGER,
    game_time_model TEXT,
    session_id TEXT,
    receiver_location_id INTEGER,
    source_cmd INTEGER NOT NULL,
    channel TEXT,
    player_id INTEGER,
    player_name TEXT,
    fish_lang_id INTEGER,
    fish_name TEXT,
    windex INTEGER,
    weight INTEGER,
    player_level INTEGER,
    title INTEGER,
    club_id INTEGER,
    club_tag TEXT,
    source_key TEXT
);");
            Execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_public_catches_src ON public_catches(source_key) WHERE source_key IS NOT NULL;");
            Execute("CREATE INDEX IF NOT EXISTS idx_public_catches_date ON public_catches(received_at);");
            // chat_messages — публичный чат и действия игроков (cmd 50). Личные сообщения (toPrivate > 0) не сохраняются.
            Execute(@"
CREATE TABLE IF NOT EXISTS chat_messages (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    received_at TEXT NOT NULL,
    server_time TEXT,
    game_time_est TEXT,
    game_time_unc_min INTEGER,
    game_time_model TEXT,
    session_id TEXT,
    room_id INTEGER,
    channel TEXT,
    message_type TEXT,
    action_name TEXT,
    action_value TEXT,
    from_id INTEGER,
    from_name TEXT,
    text TEXT,
    from_level INTEGER,
    from_title INTEGER,
    from_club_id INTEGER,
    from_club_tag TEXT,
    source_key TEXT
);");
            Execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_chat_messages_src ON chat_messages(source_key) WHERE source_key IS NOT NULL;");
            Execute("CREATE INDEX IF NOT EXISTS idx_chat_messages_date ON chat_messages(received_at);");

            ApplyAttemptTrackingMigration(); // NEXT-ANALYTICS-04
            ApplyInventoryMigration();       // NEXT-BAIT-05
            ApplyCatchContextMigration();    // NEXT-DATA-01
            ApplyBiteProtocolMigration();    // NEXT-BITE-IMPLEMENT-01
        }

        // ------------------------------------------------------------------
        // NEXT-BITE-IMPLEMENT-01: bite metadata columns + server_checks. Additive and idempotent; existing rows keep NULL.
        // ------------------------------------------------------------------
        private void ApplyBiteProtocolMigration()
        {
            EnsureColumnOn("cast_events", "wkf", "REAL");
            EnsureColumnOn("cast_events", "bite_accuraty", "REAL");
            EnsureColumnOn("cast_events", "bite_agressive", "REAL");
            EnsureColumnOn("cast_events", "bite_stamina", "REAL");
            EnsureColumnOn("cast_events", "bite_force", "REAL");
            EnsureColumnOn("cast_events", "bite_down_shance", "REAL");
            EnsureColumnOn("cast_events", "klev_type", "TEXT");
            EnsureColumnOn("cast_events", "hook_uid", "INTEGER");
            EnsureColumnOn("cast_events", "bite_meta_source", "TEXT");
            EnsureColumn("windex_source", "TEXT");   // catches: provenance of a windex filled from now on (old rows stay NULL)
            Execute(@"CREATE TABLE IF NOT EXISTS server_checks (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT NOT NULL,
    session_id TEXT,
    cast_id INTEGER,
    cast_match_status TEXT,
    rod_index INTEGER,
    rod_uid INTEGER,
    location_id INTEGER,
    bait_uid INTEGER,
    addbait_uid INTEGER,
    bait_x REAL, bait_y REAL, bait_z REAL,
    grid_x REAL, grid_y REAL, grid_z REAL,
    depth1_raw REAL,
    depth2_raw REAL,
    flag_moving INTEGER,
    flag_on_ground INTEGER,
    flag_top_down INTEGER,
    flag_simple_play INTEGER,
    flag_active_retrieve INTEGER,
    flag_ground_event INTEGER,
    flag7_raw INTEGER,
    distance REAL,
    rod_code INTEGER,
    trolling INTEGER,
    payload_hex TEXT,
    provenance TEXT,
    confidence TEXT,
    response_type TEXT,
    response_at TEXT,
    response_shance REAL,
    response_match TEXT,
    source_key TEXT
);");
            Execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_server_checks_src ON server_checks(source_key) WHERE source_key IS NOT NULL;");
            Execute("CREATE INDEX IF NOT EXISTS idx_server_checks_rod_ts ON server_checks(session_id, rod_index, timestamp);");
        }

        /// <summary>NEXT-BITE-IMPLEMENT-01: stores one outgoing check (an observation, not an attempt). Returns its id (existing id on duplicate).</summary>
        public long SaveServerCheck(ServerCheck c, string? sessionId, long? castId, string castMatchStatus, string sourceKey)
        {
            if (c == null) return 0;
            lock (_sync)
            {
                if (_connection == null) return 0;
                using (var cmd = new SqliteCommand(@"
INSERT INTO server_checks (timestamp, session_id, cast_id, cast_match_status, rod_index, rod_uid, location_id, bait_uid, addbait_uid,
    bait_x, bait_y, bait_z, grid_x, grid_y, grid_z, depth1_raw, depth2_raw,
    flag_moving, flag_on_ground, flag_top_down, flag_simple_play, flag_active_retrieve, flag_ground_event, flag7_raw,
    distance, rod_code, trolling, payload_hex, provenance, confidence, source_key)
VALUES (@ts, @sid, @cast, @cms, @rod, @uid, @loc, @bait, @add, @bx, @by, @bz, @gx, @gy, @gz, @d1, @d2,
    @f0, @f1, @f2, @f3, @f4, @f5, @f6, @dist, @code, @troll, @hex, @prov, 'CONFIRMED_LAYOUT', @sk)
ON CONFLICT(source_key) WHERE source_key IS NOT NULL DO NOTHING;", _connection))
                {
                    cmd.Parameters.AddWithValue("@ts", c.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                    cmd.Parameters.AddWithValue("@sid", (object?)sessionId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@cast", (object?)castId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@cms", castMatchStatus);
                    cmd.Parameters.AddWithValue("@rod", c.Rod);
                    cmd.Parameters.AddWithValue("@uid", c.RodUid);
                    cmd.Parameters.AddWithValue("@loc", c.LocId);
                    cmd.Parameters.AddWithValue("@bait", c.BaitUid);
                    cmd.Parameters.AddWithValue("@add", c.AddBaitUid);
                    cmd.Parameters.AddWithValue("@bx", c.BaitX); cmd.Parameters.AddWithValue("@by", c.BaitY); cmd.Parameters.AddWithValue("@bz", c.BaitZ);
                    cmd.Parameters.AddWithValue("@gx", c.GridX); cmd.Parameters.AddWithValue("@gy", c.GridY); cmd.Parameters.AddWithValue("@gz", c.GridZ);
                    cmd.Parameters.AddWithValue("@d1", (double)c.Depth1);
                    cmd.Parameters.AddWithValue("@d2", (double)c.Depth2);
                    for (int i = 0; i < 7; i++) cmd.Parameters.AddWithValue("@f" + i, (int)c.Flags[i]);
                    cmd.Parameters.AddWithValue("@dist", (double)c.Distance);
                    cmd.Parameters.AddWithValue("@code", c.RodCode);
                    cmd.Parameters.AddWithValue("@troll", c.Trolling ? 1 : 0);
                    cmd.Parameters.AddWithValue("@hex", c.PayloadHex);
                    cmd.Parameters.AddWithValue("@prov", ServerCheck.Provenance);
                    cmd.Parameters.AddWithValue("@sk", sourceKey);
                    cmd.ExecuteNonQuery();
                }
                using var sel = new SqliteCommand("SELECT id FROM server_checks WHERE source_key=@sk;", _connection);
                sel.Parameters.AddWithValue("@sk", sourceKey);
                object? r = sel.ExecuteScalar();
                return r == null || r is DBNull ? 0 : Convert.ToInt64(r, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>NEXT-BITE-IMPLEMENT-01: attaches a response to a check that has none yet (never overwrites).</summary>
        public bool SetServerCheckResponse(long checkId, string type, DateTime at, float? shance, string method)
        {
            lock (_sync)
            {
                if (_connection == null || checkId <= 0) return false;
                using var cmd = new SqliteCommand(@"UPDATE server_checks SET response_type=@t, response_at=@a, response_shance=@s, response_match=@m
WHERE id=@id AND response_type IS NULL;", _connection);
                cmd.Parameters.AddWithValue("@t", type);
                cmd.Parameters.AddWithValue("@a", at.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@s", shance.HasValue ? (double)shance.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@m", method);
                cmd.Parameters.AddWithValue("@id", checkId);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>
        /// NEXT-BITE-IMPLEMENT-01: fallback windex for catches that still have none, from the s2c 1120 flag=1 bite of the SAME
        /// proven cast. All must hold: catches.windex IS NULL; catches.cast_id set; no other catch on that cast; at least one
        /// MATCHED 'bite' event of that cast with parsed metadata and exactly the catch's species and weight; every such bite
        /// carries the same windex. Otherwise the catch is left unchanged. Never overwrites a value (incl. cmd=6).
        /// Returns the number of catches updated.
        /// </summary>
        /// <summary>FIX-02: record_id → windex of catches that have a windex (to refresh rows already shown in the table).</summary>
        public Dictionary<string, int> GetCatchWindexByRecordId()
        {
            lock (_sync)
            {
                var d = new Dictionary<string, int>(StringComparer.Ordinal);
                if (_connection == null) return d;
                using var c = new SqliteCommand("SELECT record_id, windex FROM catches WHERE windex IS NOT NULL AND record_id IS NOT NULL;", _connection);
                using var r = c.ExecuteReader();
                while (r.Read()) d[r.GetString(0)] = r.GetInt32(1);
                return d;
            }
        }

        public int AttachWindexFromBites()
        {
            lock (_sync)
            {
                if (_connection == null) return 0;
                var todo = new List<(long id, long cast, long fid, long w)>();
                using (var c = new SqliteCommand(@"SELECT k.id, k.cast_id, k.fish_lang_id, k.weight FROM catches k
WHERE k.windex IS NULL AND k.cast_id IS NOT NULL AND k.fish_lang_id IS NOT NULL AND k.weight IS NOT NULL
  AND (SELECT count(*) FROM catches o WHERE o.cast_id = k.cast_id) = 1;", _connection))
                using (var r = c.ExecuteReader())
                    while (r.Read()) todo.Add((r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)));
                int updated = 0;
                foreach (var t in todo)
                {
                    var vals = new List<long>();
                    using (var q = new SqliteCommand(@"SELECT DISTINCT windex FROM cast_events
WHERE cast_id=@c AND event_type='bite' AND match_status='MATCHED' AND bite_meta_source=@src
  AND fish_lang_id=@f AND weight=@w AND windex IS NOT NULL;", _connection))
                    {
                        q.Parameters.AddWithValue("@c", t.cast);
                        q.Parameters.AddWithValue("@src", BiteMetadata.Source);
                        q.Parameters.AddWithValue("@f", t.fid);
                        q.Parameters.AddWithValue("@w", t.w);
                        using var r = q.ExecuteReader();
                        while (r.Read()) vals.Add(r.GetInt64(0));
                    }
                    if (vals.Count != 1) continue; // none or conflicting
                    using var up = new SqliteCommand("UPDATE catches SET windex=@x, windex_source=@src WHERE id=@id AND windex IS NULL;", _connection);
                    up.Parameters.AddWithValue("@x", vals[0]);
                    up.Parameters.AddWithValue("@src", BiteMetadata.Source + "_bite_fallback");
                    up.Parameters.AddWithValue("@id", t.id);
                    if (up.ExecuteNonQuery() > 0) updated++;
                }
                return updated;
            }
        }

        // ------------------------------------------------------------------
        // NEXT-ANALYTICS-04: versioned, idempotent schema migration (attempt tracking)
        // ------------------------------------------------------------------

        public const int AttemptTrackingSchemaVersion = 1;

        /// <summary>Highest casts.id that existed before migration 1 (pre-04 semantics). 0 = new database.</summary>
        public long LegacyMaxCastId { get; private set; }

        /// <summary>
        /// Migration 1: catches.cast_id (FK → casts.id, NULL for every pre-existing row — nothing is back-filled),
        /// indexes, and a schema_migrations row that records the legacy high-water marks. Each statement is
        /// idempotent and the marker row is written last, so an interrupted run simply completes on the next start.
        /// </summary>
        private void ApplyAttemptTrackingMigration()
        {
            Execute(@"CREATE TABLE IF NOT EXISTS schema_migrations (
    version INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    applied_at TEXT NOT NULL,
    legacy_max_cast_id INTEGER,
    legacy_max_catch_id INTEGER,
    note TEXT
);");
            EnsureColumn("cast_id", "INTEGER REFERENCES casts(id)");
            EnsureColumn("windex", "INTEGER"); // NEXT-ANALYTICS-09: raw size class, NULL until proven
            Execute("CREATE INDEX IF NOT EXISTS idx_catches_cast_id ON catches(cast_id);");
            Execute("CREATE INDEX IF NOT EXISTS idx_cast_events_cast_id ON cast_events(cast_id);");
            Execute("CREATE INDEX IF NOT EXISTS idx_cast_events_ts ON cast_events(timestamp);");   // AUDIT-04 (B5): сортировка/выборка событий по времени без полного прохода
            BackfillWaitChance();
            RepairUnknownSpecies();
            Execute(@"INSERT OR IGNORE INTO schema_migrations (version, name, applied_at, legacy_max_cast_id, legacy_max_catch_id, note)
VALUES (1, 'NEXT-ANALYTICS-04 attempt tracking', strftime('%Y-%m-%d %H:%M:%S','now','localtime'),
        (SELECT COALESCE(MAX(id),0) FROM casts), (SELECT COALESCE(MAX(id),0) FROM catches),
        'catches.cast_id added, NULL for all pre-existing rows; casts with id <= legacy_max_cast_id keep pre-04 outcome semantics');");

            using (var cmd = new SqliteCommand("PRAGMA user_version;", _connection))
            {
                long v = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
                if (v < AttemptTrackingSchemaVersion) Execute("PRAGMA user_version = " + AttemptTrackingSchemaVersion + ";");
            }
            using (var cmd = new SqliteCommand("SELECT legacy_max_cast_id FROM schema_migrations WHERE version = 1;", _connection))
            {
                object? r = cmd.ExecuteScalar();
                LegacyMaxCastId = r == null || r is DBNull ? 0 : Convert.ToInt64(r, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// NEXT-ANALYTICS-04: casts left 'pending' by an earlier application run (crash, restart) can no longer be
        /// observed. Only post-migration casts (id &gt; LegacyMaxCastId) of OTHER sessions are closed as 'unknown',
        /// with one 'cast_end_unresolved' event each (idempotent source_key). Historical rows are never touched.
        /// Returns the number of casts closed.
        /// </summary>
        public int CloseStalePendingCasts(string currentSessionId, DateTime at)
        {
            lock (_sync)
            {
                if (_connection == null) return 0;
                var ids = new List<long>();
                using (var sel = new SqliteCommand(
                    "SELECT id FROM casts WHERE outcome='pending' AND id > @legacy AND (session_id IS NULL OR session_id <> @sid) ORDER BY id;", _connection))
                {
                    sel.Parameters.AddWithValue("@legacy", LegacyMaxCastId);
                    sel.Parameters.AddWithValue("@sid", currentSessionId);
                    using var r = sel.ExecuteReader();
                    while (r.Read()) ids.Add(r.GetInt64(0));
                }
                foreach (long id in ids)
                {
                    using var ev = new SqliteCommand(@"
INSERT INTO cast_events (cast_id, timestamp, event_type, is_client, raw_text, match_status, source_key)
VALUES (@id, @ts, 'cast_end_unresolved', 0, 'previous application run ended while the cast was open', 'MATCHED', @sk)
ON CONFLICT(source_key) WHERE source_key IS NOT NULL DO NOTHING;", _connection);
                    ev.Parameters.AddWithValue("@id", id);
                    ev.Parameters.AddWithValue("@ts", at.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
                    ev.Parameters.AddWithValue("@sk", "restart|cast" + id);
                    ev.ExecuteNonQuery();
                    using var up = new SqliteCommand("UPDATE casts SET outcome='unknown' WHERE id=@id AND outcome='pending';", _connection);
                    up.Parameters.AddWithValue("@id", id);
                    up.ExecuteNonQuery();
                }
                return ids.Count;
            }
        }

        private static object Db(object? v) => v ?? DBNull.Value;

        /// <summary>
        /// NEXT-ANALYTICS-09: attach a raw windex to the local player's catches from the unique own-catch cmd=6
        /// announcement. Conditions: player_id = localPlayerId, exact fish_lang_id + weight, announcement within
        /// [catch, catch+30s], and exactly one candidate on each side (no ambiguity). Idempotent: only NULL windex
        /// is filled; a verified value is never overwritten. Returns the number of catches updated.
        /// </summary>
        public int AttachOwnCatchWindex(long localPlayerId)
        {
            lock (_sync)
            {
                if (_connection == null || localPlayerId <= 0) return 0;
                int updated = 0;
                var anns = new List<(uint fid, uint weight, int windex, string at)>();
                using (var c = new SqliteCommand(
                    "SELECT fish_lang_id, weight, windex, received_at FROM public_catches WHERE player_id=@p AND windex IS NOT NULL AND fish_lang_id IS NOT NULL", _connection))
                {
                    c.Parameters.AddWithValue("@p", localPlayerId);
                    using var r = c.ExecuteReader();
                    while (r.Read()) anns.Add((Convert.ToUInt32(r.GetInt64(0)), Convert.ToUInt32(r.GetInt64(1)), r.GetInt32(2), r.GetString(3)));
                }
                if (anns.Count == 0) return 0;
                var cat = new List<(long id, uint fid, uint weight, string ts)>();
                using (var c = new SqliteCommand(
                    "SELECT id, fish_lang_id, weight, timestamp FROM catches WHERE windex IS NULL AND fish_lang_id IS NOT NULL", _connection))
                {
                    using var r = c.ExecuteReader();
                    while (r.Read()) cat.Add((r.GetInt64(0), Convert.ToUInt32(r.GetInt64(1)), Convert.ToUInt32(r.GetInt64(2)), r.GetString(3)));
                }
                foreach (var k in cat)
                {
                    if (!DateTime.TryParse(k.ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var catchTime)) continue;
                    var cand = anns.Where(a => a.fid == k.fid && a.weight == k.weight
                            && DateTime.TryParse(a.at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                            && at >= catchTime && at <= catchTime.AddSeconds(30)).ToList();
                    if (cand.Count != 1) continue; // none or ambiguous
                    var candAt = DateTime.Parse(cand[0].at, CultureInfo.InvariantCulture);
                    int otherCatches = cat.Count(c2 => c2.id != k.id && c2.fid == k.fid && c2.weight == k.weight
                            && DateTime.TryParse(c2.ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t2)
                            && candAt >= t2 && candAt <= t2.AddSeconds(30));
                    if (otherCatches != 0) continue; // multiple catches could be this announcement
                    using var up = new SqliteCommand("UPDATE catches SET windex=@w, windex_source='cmd6_own_announcement' WHERE id=@id AND windex IS NULL", _connection);
                    up.Parameters.AddWithValue("@w", cand[0].windex);
                    up.Parameters.AddWithValue("@id", k.id);
                    if (up.ExecuteNonQuery() > 0) updated++;
                }
                return updated;
            }
        }

        // ------------------------------------------------------------------
        // NEXT-BAIT-05: инвентарь (UID → тип предмета).
        //   inventory_map         — ТЕКУЩЕЕ состояние (s2c 180/181 — upsert, s2c 190 — удаление строки);
        //   inventory_uid_history — ИСТОРИЯ: каждый когда-либо виденный uid и его тип; строки не удаляются
        //                           (удаление отмечается removed_at; разные типы для одного uid → conflict = 1).
        // Разрешение uid → item_id в feed_actions / craft_actions / casts идёт ТОЛЬКО по истории без конфликта,
        // заполняет только NULL и никогда не перезаписывает уже записанное значение. Всё идемпотентно.
        // ------------------------------------------------------------------
        private void ApplyInventoryMigration()
        {
            Execute(@"CREATE TABLE IF NOT EXISTS inventory_map (
    uid INTEGER PRIMARY KEY,
    item_id INTEGER,
    owner_id INTEGER,
    inid INTEGER,
    parent_uid INTEGER,
    bag INTEGER,
    cell INTEGER,
    count INTEGER,
    inner_count INTEGER,
    dur REAL,
    is_timed INTEGER,
    item_time TEXT,
    first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL,
    source_cmd INTEGER NOT NULL
);");
            Execute("CREATE INDEX IF NOT EXISTS idx_inventory_item ON inventory_map(item_id);");
            Execute("CREATE INDEX IF NOT EXISTS idx_inventory_parent ON inventory_map(parent_uid);");
            // PRERELEASE-01: индексы для частых запросов при большом объёме (аудит DeepSeek). Только ускорение, данные не меняются.
            foreach (var ix in new[]
            {
                "CREATE INDEX IF NOT EXISTS idx_server_checks_cast ON server_checks(cast_id)",
                "CREATE INDEX IF NOT EXISTS idx_server_checks_ts ON server_checks(timestamp)",
                "CREATE INDEX IF NOT EXISTS idx_public_catches_player ON public_catches(player_id)",
                "CREATE INDEX IF NOT EXISTS idx_public_catches_fish ON public_catches(fish_lang_id)",
                "CREATE INDEX IF NOT EXISTS idx_weather_session_ts ON weather(session_id, timestamp)",
                "CREATE INDEX IF NOT EXISTS idx_groundbait_cast ON groundbait_actions(cast_id)",
                "CREATE INDEX IF NOT EXISTS idx_groundbait_session_ts ON groundbait_actions(session_id, timestamp)",
                "CREATE INDEX IF NOT EXISTS idx_casts_session_ts ON casts(session_id, timestamp)",
                "CREATE INDEX IF NOT EXISTS idx_catches_location ON catches(location_id)",
                "CREATE INDEX IF NOT EXISTS idx_catches_fish_id ON catches(fish_lang_id)"
            })
                try { Execute(ix + ";"); } catch { /* таблица ещё старого вида — индекс появится после миграции */ }
            Execute(@"CREATE TABLE IF NOT EXISTS inventory_uid_history (
    uid INTEGER PRIMARY KEY,
    item_id INTEGER,
    owner_id INTEGER,
    first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL,
    removed_at TEXT,
    conflict INTEGER NOT NULL DEFAULT 0,
    conflict_item_id INTEGER
);");
            // no schema_migrations row: both tables are created idempotently and no existing table or row is changed
        }

        // ------------------------------------------------------------------
        // NEXT-DATA-01: catch context storage (idempotent; only new tables/columns/views; no existing row is changed;
        // no schema_migrations row — the attempt-tracking invariant "one row, version 1" is kept).
        // ------------------------------------------------------------------
        public static readonly string[] CatchContextTables = { "cast_tackle", "groundbait_actions", "bait_reloads" };
        public static readonly string[] CatchContextViews = { "catch_context_v", "analysis_inputs_v" };

        private void ApplyCatchContextMigration()
        {
            // raw per-instance item data (basePar characteristics, rod props/feed, full XML) for the CURRENT inventory
            EnsureColumnOn("inventory_map", "base_par", "TEXT");
            EnsureColumnOn("inventory_map", "props_feed", "INTEGER");
            EnsureColumnOn("inventory_map", "xml", "TEXT");
            // c2s 192 structural decode: rod uid + property name (FEED = loading groundbait into the rod)
            EnsureColumnOn("feed_actions", "rod_uid", "INTEGER");
            EnsureColumnOn("feed_actions", "prop_name", "TEXT");

            string slotCols = string.Join(",\n", TackleSnapshotBuilder.SlotNames.Select(n =>
                $"    {n}_uid INTEGER,\n    {n}_item_id INTEGER,\n    {n}_name TEXT,\n    {n}_base_par TEXT,\n    {n}_dur REAL,\n    {n}_cell INTEGER,\n    {n}_slot_source TEXT"));
            Execute($@"CREATE TABLE IF NOT EXISTS cast_tackle (
    cast_id INTEGER PRIMARY KEY REFERENCES casts(id),
    captured_at TEXT NOT NULL,
    source TEXT NOT NULL,
    status TEXT NOT NULL,
    ambiguous_slots TEXT,
{slotCols},
    groundbait_status TEXT,
    groundbait_uid INTEGER,
    groundbait_item_id INTEGER,
    groundbait_name TEXT,
    gb_osnova_id INTEGER,
    gb_osnova TEXT,
    gb_aroma TEXT,
    gb_addextend TEXT,
    gb_addbaits TEXT,
    gb_klevprop TEXT,
    components_json TEXT,
    slot_conflicts TEXT,
    feeder_type TEXT,
    feeder_type_name TEXT,
    feeder_catalog_base_params TEXT,
    feeder_klevprop_raw TEXT,
    feeder_premium_status TEXT
);");
            // Stage 3b columns (idempotent also for a staging DB created by the first Stage 3 build)
            foreach (var n in TackleSnapshotBuilder.SlotNames)
            { EnsureColumnOn("cast_tackle", n + "_cell", "INTEGER"); EnsureColumnOn("cast_tackle", n + "_slot_source", "TEXT"); }
            foreach (var c in new[] { "slot_conflicts", "feeder_type", "feeder_type_name", "feeder_catalog_base_params", "feeder_klevprop_raw", "feeder_premium_status" })
                EnsureColumnOn("cast_tackle", c, "TEXT");
            Execute(@"CREATE TABLE IF NOT EXISTS groundbait_actions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT NOT NULL,
    session_id TEXT,
    groundbait_uid INTEGER NOT NULL,
    groundbait_item_id INTEGER,
    feeder_uid INTEGER,
    feeder_item_id INTEGER,
    rod_uid INTEGER,
    cast_id INTEGER,
    game_x REAL,
    game_y REAL,
    game_z REAL,
    coord_system TEXT,
    source_cmd INTEGER NOT NULL DEFAULT 1112,
    source_key TEXT
);");
            Execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_groundbait_actions_src ON groundbait_actions(source_key) WHERE source_key IS NOT NULL;");
            Execute(@"CREATE TABLE IF NOT EXISTS bait_reloads (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT NOT NULL,
    session_id TEXT,
    stack_uid INTEGER NOT NULL,
    stack_item_id INTEGER,
    count INTEGER,
    bag INTEGER,
    cell INTEGER,
    rod_uid INTEGER NOT NULL,
    rod_item_id INTEGER,
    source_cmd INTEGER NOT NULL DEFAULT 200,
    source_key TEXT
);");
            Execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_bait_reloads_src ON bait_reloads(source_key) WHERE source_key IS NOT NULL;");
            // DATA-01: сообщения игры, смысл которых ещё не разобран (s2c 186, 162, 1000, 15, 255; c2s 182) — только для исследования.
            // Хранятся сырыми байтами данных (параметр 3) 30 дней; ничего не отправляется наружу.
            Execute(@"CREATE TABLE IF NOT EXISTS research_messages (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT NOT NULL,
    session_id TEXT,
    dir TEXT NOT NULL,
    cmd INTEGER NOT NULL,
    data BLOB,
    source_key TEXT
);");
            Execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_research_messages_src ON research_messages(source_key) WHERE source_key IS NOT NULL;");
            Execute("CREATE INDEX IF NOT EXISTS ix_research_messages_cmd_ts ON research_messages(cmd, timestamp);");
            try { Execute("DELETE FROM research_messages WHERE timestamp < '" + DateTime.Now.AddDays(-ResearchKeepDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "';"); } catch { }

            // views are definitions only (no data); re-created so a newer definition replaces an older one
            string tackleCols = string.Join(", ", TackleSnapshotBuilder.SlotNames.SelectMany(n =>
                new[] { $"t.{n}_uid", $"t.{n}_item_id", $"t.{n}_name", $"t.{n}_base_par", $"t.{n}_cell", $"t.{n}_slot_source" }));
            string tol = GameCoordinates.CastMatchTolerance.ToString("R", CultureInfo.InvariantCulture);
            Execute("DROP VIEW IF EXISTS analysis_inputs_v;");
            Execute("DROP VIEW IF EXISTS catch_context_v;");
            Execute($@"CREATE VIEW catch_context_v AS
SELECT c.id AS catch_id, c.record_id, c.timestamp AS catch_time, c.session_id, c.status AS catch_status, c.source AS catch_source,
    c.fish_lang_id, c.fish_name, c.weight AS weight_g, c.windex, c.grade AS grade_weight_label,
    c.bait_id, c.bait_name, c.bait_category, c.bait_uid AS catch_bait_uid,
    c.location_id, c.baseid, c.spawn_locid,
    c.depth_cm, CASE WHEN c.depth_cm IS NULL THEN NULL WHEN c.cast_id IS NOT NULL THEN 'rod_cast_snapshot' ELSE 'catch_record' END AS depth_source,
    c.cast_x AS raw_x, c.cast_y AS raw_y, c.cast_z AS raw_z,
    CASE WHEN c.cast_x IS NULL OR c.cast_z IS NULL THEN 'none'
         WHEN c.cast_id IS NOT NULL THEN 'cast_linked'
         WHEN EXISTS (SELECT 1 FROM casts m WHERE abs(m.cast_x - c.cast_x) < {tol} AND abs(m.cast_z - c.cast_z) < {tol}) THEN 'cast_match'
         ELSE 'unverified' END AS coord_status,
    '{GameCoordinates.System}' AS coord_system,
    CASE WHEN c.cast_id IS NOT NULL OR EXISTS (SELECT 1 FROM casts m WHERE abs(m.cast_x - c.cast_x) < {tol} AND abs(m.cast_z - c.cast_z) < {tol}) THEN c.cast_x END AS game_x,
    CASE WHEN c.cast_id IS NOT NULL OR EXISTS (SELECT 1 FROM casts m WHERE abs(m.cast_x - c.cast_x) < {tol} AND abs(m.cast_z - c.cast_z) < {tol}) THEN c.cast_z END AS game_z,
    c.cast_id,
    CASE WHEN c.cast_id IS NOT NULL THEN 'linked_rod_cast'
         WHEN c.id <= COALESCE((SELECT legacy_max_catch_id FROM schema_migrations WHERE version = 1), 0) THEN 'legacy_pre_link'
         ELSE 'unlinked' END AS cast_link_status,
    k.timestamp AS cast_time, k.rod_index, k.bait_uid AS cast_bait_uid, k.bait_tid AS cast_bait_item_id, k.outcome AS cast_outcome,
    t.status AS tackle_status, t.source AS tackle_source, t.captured_at AS tackle_captured_at, t.ambiguous_slots, t.slot_conflicts,
    t.feeder_type, t.feeder_type_name, t.feeder_catalog_base_params, t.feeder_klevprop_raw, t.feeder_premium_status,
    {tackleCols},
    t.groundbait_status, t.groundbait_uid, t.groundbait_item_id, t.groundbait_name, t.gb_osnova_id, t.gb_osnova, t.gb_aroma, t.gb_addextend, t.gb_addbaits, t.gb_klevprop,
    w.timestamp AS weather_time, w.tempr AS weather_tempr, w.cloudly AS weather_cloudly, w.fog AS weather_fog, w.rain AS weather_rain, w.grom AS weather_grom,
    w.units_status AS weather_units_status,
    CASE WHEN w.id IS NULL THEN NULL ELSE 's2c1001_latest_same_session_within_10min_before_catch' END AS weather_source,
    c.air_temp AS legacy_air_temp, c.wind_ms AS legacy_wind_ms, c.wind_kmh AS legacy_wind_kmh, c.humidity AS legacy_humidity
FROM catches c
LEFT JOIN casts k ON k.id = c.cast_id
LEFT JOIN cast_tackle t ON t.cast_id = c.cast_id
LEFT JOIN weather w ON w.id = (SELECT w2.id FROM weather w2 WHERE w2.session_id = c.session_id AND w2.timestamp <= c.timestamp
                               AND w2.timestamp >= datetime(c.timestamp, '-10 minutes')
                               ORDER BY w2.timestamp DESC, w2.id DESC LIMIT 1);");
            // analysis inputs: rod, reel and float are deliberately NOT selected (reference only); no model, no formula.
            // Feeder raw catalog fields are passed as raw text with UNKNOWN semantics (not "visibility").
            string inputSlots = string.Join(", ", AnalysisEligibility.AnalysisSlots.SelectMany(n =>
                new[] { $"{n}_item_id", $"{n}_name", $"{n}_base_par" }));
            Execute($@"CREATE VIEW analysis_inputs_v AS
SELECT catch_id, catch_time, fish_lang_id, fish_name, weight_g, windex, cast_link_status, tackle_status,
    {inputSlots},
    feeder_type, feeder_type_name, feeder_catalog_base_params, feeder_klevprop_raw, feeder_premium_status,
    bait_id, bait_name,
    groundbait_status, groundbait_item_id, groundbait_name, gb_osnova_id, gb_osnova, gb_aroma, gb_addextend, gb_addbaits, gb_klevprop,
    location_id, baseid, coord_status, game_x, game_z, depth_cm, depth_source,
    weather_time, weather_source, weather_tempr, weather_cloudly, weather_fog, weather_rain, weather_grom, weather_units_status
FROM catch_context_v;");
        }

        // NEXT-DATA-01: inventory freshness. inventory_map survives app restarts, so rows of items changed while FisherLogger was
        // not running may be stale. A tackle snapshot uses ONLY rows received since the start of the latest full inventory
        // (s2c 180 burst) seen by this run; without such a burst the snapshot is "no_inventory_snapshot_this_run" (all NULL).
        public const double InventorySnapshotGapSeconds = 30;
        private string? _invSnapshotStart;
        private DateTime _invLastSnapshotPage;
        public string? InventorySnapshotStart { get { lock (_sync) return _invSnapshotStart; } }

        /// <summary>
        /// NEXT-DATA-01: снимок снасти заброса из ТЕКУЩЕГО инвентаря (вызывать сразу после SaveCast, в порядке пакетов).
        /// Первый снимок заброса не перезаписывается. Возвращает снимок (null — заброс не сохранён/уже снят).
        /// </summary>
        public TackleSnapshot? SnapshotCastTackle(long castId, long? baitUid, DateTime at, Func<long, CatalogEntry?> catalog)
        {
            if (castId <= 0) return null;
            lock (_sync)
            {
                if (_connection == null) return null;
                using (var ex = new SqliteCommand("SELECT 1 FROM cast_tackle WHERE cast_id=@c", _connection))
                { ex.Parameters.AddWithValue("@c", castId); if (ex.ExecuteScalar() != null) return null; }
                var inv = new Dictionary<long, InventoryNode>();
                using (var q = new SqliteCommand("SELECT uid, item_id, parent_uid, cell, count, dur, base_par, xml, last_seen FROM inventory_map WHERE @since IS NOT NULL AND last_seen >= @since", _connection))
                {
                    q.Parameters.AddWithValue("@since", (object?)_invSnapshotStart ?? DBNull.Value);
                    using var rd = q.ExecuteReader();
                    while (rd.Read())
                        inv[rd.GetInt64(0)] = new InventoryNode
                        {
                            Uid = rd.GetInt64(0), ItemId = rd.IsDBNull(1) ? null : rd.GetInt64(1), ParentUid = rd.IsDBNull(2) ? null : rd.GetInt64(2),
                            Cell = rd.IsDBNull(3) ? 0 : rd.GetInt32(3), Count = rd.IsDBNull(4) ? 0 : rd.GetInt32(4),
                            Dur = rd.IsDBNull(5) ? null : rd.GetDouble(5), BasePar = rd.IsDBNull(6) ? null : rd.GetString(6), Xml = rd.IsDBNull(7) ? null : rd.GetString(7),
                            LastSeen = rd.IsDBNull(8) ? null : rd.GetString(8)
                        };
                }
                InventoryNode? HistoryNode(long uid)
                {
                    using var h = new SqliteCommand("SELECT item_id FROM inventory_uid_history WHERE uid=@u AND conflict=0 AND item_id IS NOT NULL", _connection);
                    h.Parameters.AddWithValue("@u", uid);
                    object? v = h.ExecuteScalar();
                    return v == null ? null : new InventoryNode { Uid = uid, ItemId = Convert.ToInt64(v, CultureInfo.InvariantCulture) };
                }
                var snap = TackleSnapshotBuilder.Build(baitUid, inv, catalog, HistoryNode);
                if (_invSnapshotStart == null) { snap = new TackleSnapshot { Status = "no_inventory_snapshot_this_run" }; }
                snap.Source = "inventory_parent_tree;fresh_since=" + (_invSnapshotStart ?? "none");
                var cols = new List<string> { "cast_id", "captured_at", "source", "status", "ambiguous_slots", "groundbait_status", "groundbait_uid",
                    "groundbait_item_id", "groundbait_name", "gb_osnova_id", "gb_osnova", "gb_aroma", "gb_addextend", "gb_addbaits", "gb_klevprop", "components_json",
                    "slot_conflicts", "feeder_type", "feeder_type_name", "feeder_catalog_base_params", "feeder_klevprop_raw", "feeder_premium_status" };
                var vals = new List<object?> { castId, at.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), snap.Source, snap.Status,
                    snap.AmbiguousSlots.Count > 0 ? string.Join(",", snap.AmbiguousSlots) : null, snap.GroundbaitStatus, snap.GroundbaitUid,
                    snap.GroundbaitItemId, snap.GroundbaitName, snap.Composition?.OsnovaId, snap.Composition?.Osnova, snap.Composition?.Aroma,
                    snap.Composition?.AddExtend, snap.Composition?.AddBaits, snap.Composition?.KlevProp,
                    snap.Components.Count > 0 ? snap.ComponentsJson() : null,
                    snap.SlotConflicts.Count > 0 ? string.Join("; ", snap.SlotConflicts) : null };
                // LOGFIX-03 (B): состав прикорма в этот момент не прочитан (XML предмета ещё не пришёл) — тот же uid = тот же предмет,
                // берём состав из прошлого заброса с этим прикормом. Готовые прикормки (SF и т.п.) основы не имеют — это не пропуск.
                if (snap.GroundbaitUid is long gbu && gbu > 0 && (snap.Composition == null || !snap.Composition.Any))
                {
                    using var pq = new SqliteCommand("SELECT gb_osnova_id, gb_osnova, gb_aroma, gb_addextend, gb_addbaits, gb_klevprop FROM cast_tackle " +
                        "WHERE groundbait_uid=@u AND (gb_osnova IS NOT NULL OR gb_aroma IS NOT NULL OR gb_addextend IS NOT NULL OR gb_addbaits IS NOT NULL) ORDER BY cast_id DESC LIMIT 1", _connection);
                    pq.Parameters.AddWithValue("@u", gbu);
                    using var pr = pq.ExecuteReader();
                    if (pr.Read()) for (int i = 0; i < 6; i++) vals[9 + i] = pr.IsDBNull(i) ? null : pr.GetValue(i);
                }
                var fd = snap.Get("feeder");
                // feeder: catalog type + raw catalog characteristics; premium/ordinary is NOT reliably identifiable → 'unknown'
                vals.AddRange(new object?[] { fd?.CatalogSubType, fd?.CatalogTypeName, fd?.CatalogBaseParams, fd?.CatalogKlevPropRaw, fd != null ? "unknown" : null });
                foreach (var slot in TackleSnapshotBuilder.SlotNames)
                {
                    var c = snap.Get(slot);
                    cols.AddRange(new[] { slot + "_uid", slot + "_item_id", slot + "_name", slot + "_base_par", slot + "_dur", slot + "_cell", slot + "_slot_source" });
                    vals.AddRange(new object?[] { c?.Uid, c?.ItemId, c?.Name, c?.BasePar, c?.Dur, c == null ? null : c.Cell, c?.SlotSource });
                }
                using var ins = new SqliteCommand("INSERT OR IGNORE INTO cast_tackle (" + string.Join(", ", cols) + ") VALUES (" +
                                                  string.Join(", ", cols.Select((_, i) => "@p" + i)) + ")", _connection);
                for (int i = 0; i < vals.Count; i++) ins.Parameters.AddWithValue("@p" + i, vals[i] ?? DBNull.Value);
                ins.ExecuteNonQuery();
                return snap;
            }
        }

        /// <summary>NEXT-DATA-01: c2s 1112 (внесение прикорма). rod_uid — родитель кормушки в текущем инвентаре; cast_id — текущий заброс этой удочки,
        /// только если её точка заброса совпадает с точкой внесения (допуск как у карты). Иначе NULL.</summary>
        public bool SaveGroundbaitAction(GroundbaitAction a, IReadOnlyDictionary<long, (long CastId, double? X, double? Z)>? castByRodUid = null)
        {
            if (a == null) return false;
            lock (_sync)
            {
                if (_connection == null) return false;
                object? rodUid = null;
                if (a.FeederUid > 0 && _invSnapshotStart != null)   // only a fresh inventory row (see InventorySnapshotStart)
                    using (var q = new SqliteCommand("SELECT parent_uid FROM inventory_map WHERE uid=@u AND last_seen >= @since", _connection))
                    {
                        q.Parameters.AddWithValue("@u", a.FeederUid); q.Parameters.AddWithValue("@since", _invSnapshotStart);
                        rodUid = q.ExecuteScalar(); if (rodUid is DBNull) rodUid = null;
                    }
                long? castIdCandidate = null; double? castX = null, castZ = null;
                if (rodUid != null && castByRodUid != null && castByRodUid.TryGetValue(Convert.ToInt64(rodUid, CultureInfo.InvariantCulture), out var oc))
                { castIdCandidate = oc.CastId; castX = oc.X; castZ = oc.Z; }
                bool castOk = castIdCandidate > 0 && castX.HasValue && castZ.HasValue &&
                              Math.Abs(castX.Value - a.X) < GameCoordinates.CastMatchTolerance && Math.Abs(castZ.Value - a.Z) < GameCoordinates.CastMatchTolerance;
                // GBHAND-01: прикорм рукой (кормушки нет, feeder_uid = 0 — поплавок): игра кладёт его в 0,1–1,6 м от поплавка,
                // а не в точку заброса, поэтому допуск 5 см не подходит. Берём текущий заброс удочки, чья точка ближе всего к месту
                // внесения, не дальше HandThrowRadius; при равенстве (две удочки ближе 0,3 м друг от друга) — не привязываем.
                if (rodUid == null && a.FeederUid <= 0 && castByRodUid != null && HandThrowCast(a.X, a.Z, castByRodUid) is { } ht)
                { rodUid = ht.RodUid; castIdCandidate = ht.CastId; castOk = true; }
                using var cmd = new SqliteCommand(@"INSERT INTO groundbait_actions (timestamp, session_id, groundbait_uid, feeder_uid, rod_uid, cast_id,
    game_x, game_y, game_z, coord_system, source_key)
VALUES (@t, @s, @g, @f, @r, @c, @x, @y, @z, @cs, @k)
ON CONFLICT(source_key) WHERE source_key IS NOT NULL DO NOTHING;", _connection);
                cmd.Parameters.AddWithValue("@t", a.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@s", _sessionId);
                cmd.Parameters.AddWithValue("@g", a.GroundbaitUid);
                cmd.Parameters.AddWithValue("@f", a.FeederUid);
                cmd.Parameters.AddWithValue("@r", rodUid ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@c", castOk ? castIdCandidate!.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@x", a.X); cmd.Parameters.AddWithValue("@y", a.Y); cmd.Parameters.AddWithValue("@z", a.Z);
                cmd.Parameters.AddWithValue("@cs", GameCoordinates.System);
                cmd.Parameters.AddWithValue("@k", string.IsNullOrEmpty(a.SourceKey) ? DBNull.Value : a.SourceKey);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>GBHAND-01: радиус привязки прикорма, брошенного рукой, к поплавку, м (замер 17.11: 0,1–1,6 м).</summary>
        public const double HandThrowRadius = 3.0;

        /// <summary>GBHAND-01: ближайший текущий заброс к месту броска рукой; null — нет заброса в радиусе или два почти одинаково близко.</summary>
        public static (long RodUid, long CastId)? HandThrowCast(double x, double z, IReadOnlyDictionary<long, (long CastId, double? X, double? Z)> castByRodUid)
        {
            (long rod, long cast, double d)? best = null, second = null;
            foreach (var kv in castByRodUid)
            {
                if (kv.Value.CastId <= 0 || kv.Value.X is not double cx || kv.Value.Z is not double cz) continue;
                double d = Math.Sqrt((cx - x) * (cx - x) + (cz - z) * (cz - z));
                if (d > HandThrowRadius) continue;
                if (best == null || d < best.Value.d) { second = best; best = (kv.Key, kv.Value.CastId, d); }
                else if (second == null || d < second.Value.d) second = (kv.Key, kv.Value.CastId, d);
            }
            if (best == null) return null;
            if (second != null && second.Value.d - best.Value.d < 0.3) return null;   // неоднозначно
            return (best.Value.rod, best.Value.cast);
        }

        /// <summary>NEXT-HISTORY-UI-01: прикорм, внесённый (c2s 1112) на заброс. Только чтение.</summary>
        public sealed class AppliedGroundbait
        {
            public string Status { get; init; } = "";          // applied | ambiguous
            public long Uid { get; init; }
            public long? ItemId { get; init; }
            public int Applications { get; init; }
            public int DistinctGroundbaits { get; init; }
            public InventoryXml.GroundbaitComposition? Composition { get; init; }
        }

        /// <summary>
        /// NEXT-HISTORY-UI-01: прикорм заброса — только из groundbait_actions с уже установленной привязкой cast_id (кормушка
        /// удочки + точная точка заброса, см. SaveGroundbaitAction). Нет записей → null; разные прикормы → ambiguous.
        /// Состав: снимок снасти этого заброса (если там тот же uid), иначе XML предмета в inventory_map. Ничего не пишет.
        /// </summary>
        public AppliedGroundbait? GetAppliedGroundbait(long? castId)
        {
            if (castId is null or <= 0) return null;
            lock (_sync)
            {
                if (_connection == null) return null;
                try
                {
                    var rows = new List<(long Uid, long? Item)>();
                    using (var q = new SqliteCommand("SELECT groundbait_uid, groundbait_item_id FROM groundbait_actions WHERE cast_id=@c ORDER BY id", _connection))
                    {
                        q.Parameters.AddWithValue("@c", castId.Value);
                        using var rd = q.ExecuteReader();
                        while (rd.Read()) rows.Add((rd.GetInt64(0), rd.IsDBNull(1) ? null : rd.GetInt64(1)));
                    }
                    if (rows.Count == 0) return null;
                    var uids = rows.Select(r => r.Uid).Distinct().ToList();
                    if (uids.Count > 1) return new AppliedGroundbait { Status = "ambiguous", Applications = rows.Count, DistinctGroundbaits = uids.Count };
                    long uid = uids[0];
                    long? item = rows.Select(r => r.Item).FirstOrDefault(i => i.HasValue);
                    InventoryXml.GroundbaitComposition? comp = null;
                    using (var t = new SqliteCommand("SELECT gb_osnova, gb_aroma, gb_addextend, gb_addbaits, gb_klevprop FROM cast_tackle WHERE cast_id=@c AND groundbait_uid=@u", _connection))
                    {
                        t.Parameters.AddWithValue("@c", castId.Value); t.Parameters.AddWithValue("@u", uid);
                        using var rd = t.ExecuteReader();
                        if (rd.Read())
                        {
                            string? S(int i) => rd.IsDBNull(i) ? null : rd.GetString(i);
                            var c = new InventoryXml.GroundbaitComposition { Osnova = S(0), Aroma = S(1), AddExtend = S(2), AddBaits = S(3), KlevProp = S(4) };
                            if (c.Any) comp = c;
                        }
                    }
                    if (comp == null)
                        using (var x = new SqliteCommand("SELECT xml FROM inventory_map WHERE uid=@u", _connection))
                        {
                            x.Parameters.AddWithValue("@u", uid);
                            comp = InventoryXml.Composition(x.ExecuteScalar() as string);
                        }
                    return new AppliedGroundbait { Status = "applied", Uid = uid, ItemId = item, Applications = rows.Count, DistinctGroundbaits = 1, Composition = comp };
                }
                catch (SqliteException) { return null; }   // older DB without the NEXT-DATA-01 tables
            }
        }

        public const int ResearchKeepDays = 30;
        private readonly List<(DateTime ts, string dir, int cmd, byte[] data)> _research = new();
        private DateTime _researchFlushedAt = DateTime.Now;

        /// <summary>DATA-01: сообщение для исследования; пишется пачкой (≥100 шт. или раз в 10 с) одной транзакцией.</summary>
        public void EnqueueResearch(DateTime ts, bool client, int cmd, byte[] data)
        {
            if (data == null) return;
            if (!client && cmd == CmdItemPlace) { try { ApplyItemPlace(ts, data); } catch { } }   // TACKLE-01
            bool flush;
            lock (_research)
            {
                _research.Add((ts, client ? "c2s" : "s2c", cmd, data));
                flush = _research.Count >= 100 || (DateTime.Now - _researchFlushedAt).TotalSeconds >= 10;
            }
            if (flush) FlushResearch();
        }

        /// <summary>TACKLE-01: s2c 186 (37 байт) — новое место предмета: uid(8) bag(4) cell(4) kind(4) parent(8) …
        /// Проверено на 17.11: 66 из 66 последних 186 совпали с полным снимком 180 (родитель, сумка, ячейка).
        /// kind 0 — без родителя, kind 3 — внутри предмета parent; другие kind не применяем (смысл не разобран).</summary>
        public const int CmdItemPlace = 186;

        public static bool TryParseItemPlace(byte[] d, out long uid, out int bag, out int cell, out long parent)
        {
            uid = 0; bag = cell = 0; parent = 0;
            if (d == null || d.Length != 37) return false;
            uid = (long)BitConverter.ToUInt64(d, 0); bag = BitConverter.ToInt32(d, 8); cell = BitConverter.ToInt32(d, 12);
            int kind = BitConverter.ToInt32(d, 16); parent = (long)BitConverter.ToUInt64(d, 20);
            if (uid <= 0) return false;
            return kind == 0 ? parent == 0 : kind == 3 && parent > 0;
        }

        /// <summary>TACKLE-01: применить перемещение (s2c 186) к известному предмету в inventory_map. Неизвестный uid не добавляем. Возвращает 1/0.</summary>
        public int ApplyItemPlace(DateTime ts, byte[] d)
        {
            if (!TryParseItemPlace(d, out var uid, out var bag, out var cell, out var parent)) return 0;
            lock (_sync)
            {
                if (_connection == null) return 0;
                using var up = new SqliteCommand("UPDATE inventory_map SET parent_uid=@p, bag=@b, cell=@c, last_seen=@t, source_cmd=186 WHERE uid=@u", _connection);
                up.Parameters.AddWithValue("@p", parent == 0 ? (object)DBNull.Value : parent);
                up.Parameters.AddWithValue("@b", bag);
                up.Parameters.AddWithValue("@c", cell);
                up.Parameters.AddWithValue("@t", ts.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                up.Parameters.AddWithValue("@u", uid);
                return up.ExecuteNonQuery();
            }
        }

        /// <summary>DATA-01: записать накопленные сообщения (вызывается и при завершении сессии). Возвращает число новых строк.</summary>
        public int FlushResearch()
        {
            List<(DateTime ts, string dir, int cmd, byte[] data)> batch;
            lock (_research) { batch = new(_research); _research.Clear(); _researchFlushedAt = DateTime.Now; }
            if (batch.Count == 0) return 0;
            lock (_sync)
            {
                if (_connection == null) return 0;
                int n = 0;
                try
                {
                    using var tx = _connection.BeginTransaction();
                    using var cmd = new SqliteCommand("INSERT OR IGNORE INTO research_messages (timestamp, session_id, dir, cmd, data, source_key) VALUES (@t, @s, @d, @c, @b, @k)", _connection, tx);
                    var pt = cmd.Parameters.Add("@t", SqliteType.Text); var ps = cmd.Parameters.Add("@s", SqliteType.Text); var pd = cmd.Parameters.Add("@d", SqliteType.Text);
                    var pc = cmd.Parameters.Add("@c", SqliteType.Integer); var pb = cmd.Parameters.Add("@b", SqliteType.Blob); var pk = cmd.Parameters.Add("@k", SqliteType.Text);
                    foreach (var m in batch)
                    {
                        uint h = 2166136261; foreach (byte x in m.data) h = (h ^ x) * 16777619;
                        pt.Value = m.ts.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture); ps.Value = _sessionId; pd.Value = m.dir;
                        pc.Value = m.cmd; pb.Value = m.data;
                        pk.Value = m.dir + "|" + m.cmd.ToString(CultureInfo.InvariantCulture) + "|" + m.ts.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + h.ToString("x8", CultureInfo.InvariantCulture);
                        n += cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                catch (SqliteException) { }
                return n;
            }
        }

        /// <summary>NEXT-DATA-01: c2s 200 (перезарядка наживки).</summary>
        public bool SaveBaitReload(BaitReload b)
        {
            if (b == null) return false;
            lock (_sync)
            {
                if (_connection == null) return false;
                using var cmd = new SqliteCommand(@"INSERT INTO bait_reloads (timestamp, session_id, stack_uid, count, bag, cell, rod_uid, source_key)
VALUES (@t, @s, @u, @n, @b, @c, @r, @k)
ON CONFLICT(source_key) WHERE source_key IS NOT NULL DO NOTHING;", _connection);
                cmd.Parameters.AddWithValue("@t", b.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@s", _sessionId);
                cmd.Parameters.AddWithValue("@u", b.StackUid); cmd.Parameters.AddWithValue("@n", b.Count);
                cmd.Parameters.AddWithValue("@b", b.Bag); cmd.Parameters.AddWithValue("@c", b.Cell); cmd.Parameters.AddWithValue("@r", b.RodUid);
                cmd.Parameters.AddWithValue("@k", string.IsNullOrEmpty(b.SourceKey) ? DBNull.Value : b.SourceKey);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public sealed class InventoryApplyResult
        {
            public int Upserted, Inserted, Removed, RemoveUnknown, HistoryNew, HistoryConflicts;
            public override string ToString() => "upserted " + Upserted + " (new " + Inserted + "), removed " + Removed +
                (RemoveUnknown > 0 ? " (unknown " + RemoveUnknown + ")" : "") + ", history new " + HistoryNew + (HistoryConflicts > 0 ? ", conflicts " + HistoryConflicts : "");
        }

        /// <summary>Применить проверенное сообщение инвентаря одной транзакцией (всё или ничего).</summary>
        public InventoryApplyResult ApplyInventory(InventoryMessage msg)
        {
            var res = new InventoryApplyResult();
            if (msg == null) return res;
            lock (_sync)
            {
                if (_connection == null) return res;
                string ts = msg.ReceivedAt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                // NEXT-DATA-01: start of the latest full-inventory burst (s2c 180 pages) seen by THIS writer instance
                if (msg.Cmd == InventoryCodec.CmdSnapshotPage)
                {
                    if (_invSnapshotStart == null || (msg.ReceivedAt - _invLastSnapshotPage).TotalSeconds > InventorySnapshotGapSeconds)
                        _invSnapshotStart = ts;
                    _invLastSnapshotPage = msg.ReceivedAt;
                }
                using var tx = _connection.BeginTransaction();
                SqliteCommand Cmd(string sql) => new SqliteCommand(sql, _connection, tx);
                if (msg.Cmd == InventoryCodec.CmdRemove)
                {
                    using (var del = Cmd("DELETE FROM inventory_map WHERE uid=@u"))
                    { del.Parameters.AddWithValue("@u", msg.RemovedUid); int n = del.ExecuteNonQuery(); res.Removed = n; if (n == 0) res.RemoveUnknown = 1; }
                    using (var h = Cmd("UPDATE inventory_uid_history SET removed_at=@t, last_seen=@t WHERE uid=@u AND removed_at IS NULL"))
                    { h.Parameters.AddWithValue("@t", ts); h.Parameters.AddWithValue("@u", msg.RemovedUid); h.ExecuteNonQuery(); }
                }
                else
                {
                    foreach (var r in msg.Records)
                    {
                        bool existed;
                        using (var ex = Cmd("SELECT 1 FROM inventory_map WHERE uid=@u"))
                        { ex.Parameters.AddWithValue("@u", r.Uid); existed = ex.ExecuteScalar() != null; }
                        using (var up = Cmd(@"INSERT INTO inventory_map (uid, item_id, owner_id, inid, parent_uid, bag, cell, count, inner_count, dur, is_timed, item_time, first_seen, last_seen, source_cmd, base_par, props_feed, xml)
VALUES (@u, @item, @own, @inid, @par, @bag, @cell, @cnt, @inner, @dur, @timed, @it, @t, @t, @cmd, @bp, @pf, @xml)
ON CONFLICT(uid) DO UPDATE SET item_id=excluded.item_id, owner_id=excluded.owner_id, inid=excluded.inid, parent_uid=excluded.parent_uid,
    bag=excluded.bag, cell=excluded.cell, count=excluded.count, inner_count=excluded.inner_count, dur=excluded.dur, is_timed=excluded.is_timed,
    item_time=excluded.item_time, last_seen=excluded.last_seen, source_cmd=excluded.source_cmd,
    base_par=excluded.base_par, props_feed=excluded.props_feed, xml=excluded.xml"))
                        {
                            // NEXT-DATA-01: raw item XML + its characteristics and the rod's loaded groundbait (props/feed)
                            up.Parameters.AddWithValue("@bp", (object?)InventoryXml.BasePar(r.Xml) ?? DBNull.Value);
                            up.Parameters.AddWithValue("@pf", (object?)InventoryXml.PropsFeed(r.Xml) ?? DBNull.Value);
                            up.Parameters.AddWithValue("@xml", string.IsNullOrEmpty(r.Xml) ? DBNull.Value : r.Xml);
                            up.Parameters.AddWithValue("@u", r.Uid);
                            up.Parameters.AddWithValue("@item", (object?)r.ItemId ?? DBNull.Value);
                            up.Parameters.AddWithValue("@own", r.OwnerId);
                            up.Parameters.AddWithValue("@inid", r.Inid);
                            up.Parameters.AddWithValue("@par", r.ParentUid == 0 ? (object)DBNull.Value : r.ParentUid);
                            up.Parameters.AddWithValue("@bag", r.Bag);
                            up.Parameters.AddWithValue("@cell", r.Cell);
                            up.Parameters.AddWithValue("@cnt", r.Count);
                            up.Parameters.AddWithValue("@inner", r.InnerCount);
                            up.Parameters.AddWithValue("@dur", (double)r.Dur);
                            up.Parameters.AddWithValue("@timed", r.IsTimed ? 1 : 0);
                            up.Parameters.AddWithValue("@it", (object?)r.ItemTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? DBNull.Value);
                            up.Parameters.AddWithValue("@t", ts);
                            up.Parameters.AddWithValue("@cmd", msg.Cmd);
                            up.ExecuteNonQuery();
                        }
                        res.Upserted++; if (!existed) res.Inserted++;
                        // history: first item type wins; a different later type marks the uid as conflicting (never resolved)
                        long? histItem = null; bool histExists = false;
                        using (var hq = Cmd("SELECT item_id FROM inventory_uid_history WHERE uid=@u"))
                        {
                            hq.Parameters.AddWithValue("@u", r.Uid);
                            using var rd = hq.ExecuteReader();
                            if (rd.Read()) { histExists = true; histItem = rd.IsDBNull(0) ? null : rd.GetInt64(0); }
                        }
                        if (!histExists)
                        {
                            using var hi = Cmd("INSERT INTO inventory_uid_history (uid, item_id, owner_id, first_seen, last_seen) VALUES (@u, @item, @own, @t, @t)");
                            hi.Parameters.AddWithValue("@u", r.Uid); hi.Parameters.AddWithValue("@item", (object?)r.ItemId ?? DBNull.Value);
                            hi.Parameters.AddWithValue("@own", r.OwnerId); hi.Parameters.AddWithValue("@t", ts);
                            hi.ExecuteNonQuery(); res.HistoryNew++;
                        }
                        else
                        {
                            bool conflict = histItem.HasValue && r.ItemId.HasValue && histItem.Value != r.ItemId.Value;
                            using var hu = Cmd(@"UPDATE inventory_uid_history SET last_seen=@t, removed_at=NULL,
    item_id=COALESCE(item_id, @item),
    conflict=CASE WHEN @c=1 THEN 1 ELSE conflict END,
    conflict_item_id=CASE WHEN @c=1 THEN @item ELSE conflict_item_id END
WHERE uid=@u");
                            hu.Parameters.AddWithValue("@u", r.Uid); hu.Parameters.AddWithValue("@t", ts);
                            hu.Parameters.AddWithValue("@item", (object?)r.ItemId ?? DBNull.Value); hu.Parameters.AddWithValue("@c", conflict ? 1 : 0);
                            hu.ExecuteNonQuery(); if (conflict) res.HistoryConflicts++;
                        }
                    }
                }
                tx.Commit();
                return res;
            }
        }

        public sealed class InventoryResolveResult
        {
            public int Feed, Craft, Casts, Context;
            public int Total => Feed + Craft + Casts + Context;
            public override string ToString() => "feed " + Feed + ", craft " + Craft + ", casts " + Casts + (Context > 0 ? ", groundbait/reload " + Context : "");
        }

        /// <summary>До-разрешение uid → item_id по истории инвентаря: только NULL-поля, только uid без конфликта. Идемпотентно.</summary>
        public InventoryResolveResult ResolveInventoryItemIds()
        {
            var res = new InventoryResolveResult();
            lock (_sync)
            {
                if (_connection == null) return res;
                const string H = "(SELECT h.item_id FROM inventory_uid_history h WHERE h.uid={0} AND h.conflict=0 AND h.item_id IS NOT NULL)";
                string Fill(string table, string uidCol, string itemCol) =>
                    "UPDATE " + table + " SET " + itemCol + "=" + string.Format(H, table + "." + uidCol) +
                    " WHERE " + itemCol + " IS NULL AND " + uidCol + " IS NOT NULL AND " + uidCol + " > 0 AND " + string.Format(H, table + "." + uidCol) + " IS NOT NULL";
                int Run(string sql) { using var c = new SqliteCommand(sql, _connection); return c.ExecuteNonQuery(); }
                res.Feed = Run(Fill("feed_actions", "bait_uid", "bait_item_id"));
                res.Craft = Run(Fill("craft_actions", "main_wpn_uid", "main_wpn_item_id")) + Run(Fill("craft_actions", "aroma_wpn_uid", "aroma_wpn_item_id"))
                          + Run(Fill("craft_actions", "add_wpn_uid", "add_wpn_item_id")) + Run(Fill("craft_actions", "bait_uid", "bait_item_id"));
                res.Casts = Run(Fill("casts", "bait_uid", "bait_tid"));
                // NEXT-DATA-01: same NULL-only, conflict-free rule for groundbait application and bait reloads
                res.Context = Run(Fill("groundbait_actions", "groundbait_uid", "groundbait_item_id")) + Run(Fill("groundbait_actions", "feeder_uid", "feeder_item_id"))
                            + Run(Fill("bait_reloads", "stack_uid", "stack_item_id")) + Run(Fill("bait_reloads", "rod_uid", "rod_item_id"));
                return res;
            }
        }

        /// <summary>NEXT-GTWC-01: объявление об улове игрока. Повтор того же пакета (тот же source_key) игнорируется. true — строка добавлена.</summary>
        public bool SavePublicCatch(PublicCatchRecord r)
        {
            if (r == null) return false;
            lock (_sync)
            {
                if (_connection == null) return false;
                using var cmd = new SqliteCommand(@"
INSERT INTO public_catches (received_at, server_time, game_time_est, game_time_unc_min, game_time_model, session_id, receiver_location_id,
    source_cmd, channel, player_id, player_name, fish_lang_id, fish_name, windex, weight, player_level, title, club_id, club_tag, source_key)
VALUES (@received_at, @server_time, @gt, @gtu, @gtm, @session_id, @loc, @cmd, @channel, @player_id, @player_name, @fish_lang_id, @fish_name,
    @windex, @weight, @level, @title, @club_id, @club_tag, @source_key)
ON CONFLICT(source_key) WHERE source_key IS NOT NULL DO NOTHING;", _connection);
                cmd.Parameters.AddWithValue("@received_at", r.ReceivedAt.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@server_time", Db(r.ServerTime?.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)));
                cmd.Parameters.AddWithValue("@gt", Db(r.GameTimeEst));
                cmd.Parameters.AddWithValue("@gtu", Db(r.GameTimeUncMin));
                cmd.Parameters.AddWithValue("@gtm", Db(r.GameTimeModel));
                cmd.Parameters.AddWithValue("@session_id", Db(r.SessionId));
                cmd.Parameters.AddWithValue("@loc", Db(r.ReceiverLocationId));
                cmd.Parameters.AddWithValue("@cmd", r.SourceCmd);
                cmd.Parameters.AddWithValue("@channel", r.Channel);
                cmd.Parameters.AddWithValue("@player_id", r.PlayerId);
                cmd.Parameters.AddWithValue("@player_name", r.PlayerName);
                cmd.Parameters.AddWithValue("@fish_lang_id", r.FishLangId);
                cmd.Parameters.AddWithValue("@fish_name", r.FishName);
                cmd.Parameters.AddWithValue("@windex", r.Windex);
                cmd.Parameters.AddWithValue("@weight", r.Weight);
                cmd.Parameters.AddWithValue("@level", r.PlayerLevel);
                cmd.Parameters.AddWithValue("@title", r.Title);
                cmd.Parameters.AddWithValue("@club_id", r.ClubId);
                cmd.Parameters.AddWithValue("@club_tag", r.ClubTag);
                cmd.Parameters.AddWithValue("@source_key", r.SourceKey.Length > 0 ? r.SourceKey : DBNull.Value);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>NEXT-GTWC-01: публичное сообщение чата / действие игрока. true — строка добавлена.</summary>
        public bool SaveChatMessage(ChatMessageRecord r)
        {
            if (r == null) return false;
            lock (_sync)
            {
                if (_connection == null) return false;
                using var cmd = new SqliteCommand(@"
INSERT INTO chat_messages (received_at, server_time, game_time_est, game_time_unc_min, game_time_model, session_id, room_id, channel,
    message_type, action_name, action_value, from_id, from_name, text, from_level, from_title, from_club_id, from_club_tag, source_key)
VALUES (@received_at, @server_time, @gt, @gtu, @gtm, @session_id, @room_id, @channel, @message_type, @action_name, @action_value,
    @from_id, @from_name, @text, @from_level, @from_title, @from_club_id, @from_club_tag, @source_key)
ON CONFLICT(source_key) WHERE source_key IS NOT NULL DO NOTHING;", _connection);
                cmd.Parameters.AddWithValue("@received_at", r.ReceivedAt.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@server_time", Db(r.ServerTime?.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)));
                cmd.Parameters.AddWithValue("@gt", Db(r.GameTimeEst));
                cmd.Parameters.AddWithValue("@gtu", Db(r.GameTimeUncMin));
                cmd.Parameters.AddWithValue("@gtm", Db(r.GameTimeModel));
                cmd.Parameters.AddWithValue("@session_id", Db(r.SessionId));
                cmd.Parameters.AddWithValue("@room_id", r.RoomId);
                cmd.Parameters.AddWithValue("@channel", r.Channel);
                cmd.Parameters.AddWithValue("@message_type", r.MessageType);
                cmd.Parameters.AddWithValue("@action_name", Db(r.ActionName));
                cmd.Parameters.AddWithValue("@action_value", Db(r.ActionValue));
                cmd.Parameters.AddWithValue("@from_id", r.FromId);
                cmd.Parameters.AddWithValue("@from_name", r.FromName);
                cmd.Parameters.AddWithValue("@text", r.Text);
                cmd.Parameters.AddWithValue("@from_level", Db(r.FromLevel));
                cmd.Parameters.AddWithValue("@from_title", Db(r.FromTitle));
                cmd.Parameters.AddWithValue("@from_club_id", Db(r.FromClubId));
                cmd.Parameters.AddWithValue("@from_club_tag", Db(r.FromClubTag));
                cmd.Parameters.AddWithValue("@source_key", r.SourceKey.Length > 0 ? r.SourceKey : DBNull.Value);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>NEXT-GTWC-01: все записи погоды s2c1001 этой БД (для показа погоды у уловов без старых полей погоды).</summary>
        public List<WeatherRecord> GetWeatherRecords()
        {
            var list = new List<WeatherRecord>();
            lock (_sync)
            {
                if (_connection == null) return list;
                using var cmd = new SqliteCommand("SELECT timestamp, cloudly, fog, rain, tempr, grom FROM weather ORDER BY timestamp;", _connection);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.IsDBNull(0) || !DateTime.TryParseExact(r.GetString(0), new[] { "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss" },
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts)) continue;
                    float? F(int i) => r.IsDBNull(i) ? null : (float)r.GetDouble(i);
                    list.Add(new WeatherRecord { Timestamp = ts, Cloudly = F(1), Fog = F(2), Rain = F(3), Tempr = F(4), Grom = F(5) });
                }
            }
            return list;
        }

        private void EnsureColumn(string name, string type) => EnsureColumnOn("catches", name, type);

        private void EnsureColumnOn(string table, string name, string type)
        {
            bool exists = false;
            using (var cmd = new SqliteCommand($"PRAGMA table_info({table});", _connection))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
            }

            if (!exists)
                Execute($"ALTER TABLE {table} ADD COLUMN {name} {type};");
        }

        /// <summary>Вставка или обновление по record_id (идемпотентно).</summary>
        public void SaveCatch(CatchData catchData, string? sessionId = null)
        {
            if (catchData == null) return;
            if (string.IsNullOrWhiteSpace(catchData.record_id))
                catchData.record_id = Guid.NewGuid().ToString("N");

            lock (_sync)
            {
                if (_connection == null) return;

                const string sql = @"
INSERT INTO catches (
    record_id, timestamp, fish_name, fish_lang_id, network_fish_id, weight, grade,
    bait_id, bait_name, bait_category, depth_cm,
    rod_id, reel_id, line_id, hook_id, float_id, feeder_id, equipment_source,
    cast_x, cast_y, cast_z,
    air_temp, wind_ms, humidity, location_id, experience, quality,
    quality_float, baseid, bait_uid, range_values, spawn_locid,
    source, autodetect_source, status,
    experience_formula, wind_kmh, session_id, cast_id, windex)
VALUES (
    @record_id, @timestamp, @fish_name, @fish_lang_id, @network_fish_id, @weight, @grade,
    @bait_id, @bait_name, @bait_category, @depth_cm,
    @rod_id, @reel_id, @line_id, @hook_id, @float_id, @feeder_id, @equipment_source,
    @cast_x, @cast_y, @cast_z,
    @air_temp, @wind_ms, @humidity, @location_id, @experience, @quality,
    @quality_float, @baseid, @bait_uid, @range_values, @spawn_locid,
    @source, @autodetect_source, @status,
    @experience_formula, @wind_kmh, @session_id, @cast_id, @windex)
ON CONFLICT(record_id) DO UPDATE SET
    timestamp=excluded.timestamp,
    fish_name=excluded.fish_name,
    fish_lang_id=excluded.fish_lang_id,
    network_fish_id=excluded.network_fish_id,
    weight=excluded.weight,
    grade=excluded.grade,
    bait_id=excluded.bait_id,
    bait_name=excluded.bait_name,
    bait_category=excluded.bait_category,
    depth_cm=excluded.depth_cm,
    rod_id=excluded.rod_id,
    reel_id=excluded.reel_id,
    line_id=excluded.line_id,
    hook_id=excluded.hook_id,
    float_id=excluded.float_id,
    feeder_id=excluded.feeder_id,
    equipment_source=excluded.equipment_source,
    cast_x=excluded.cast_x,
    cast_y=excluded.cast_y,
    cast_z=excluded.cast_z,
    air_temp=excluded.air_temp,
    wind_ms=excluded.wind_ms,
    humidity=excluded.humidity,
    location_id=excluded.location_id,
    experience=excluded.experience,
    quality=excluded.quality,
    quality_float=excluded.quality_float,
    baseid=excluded.baseid,
    bait_uid=excluded.bait_uid,
    range_values=excluded.range_values,
    spawn_locid=excluded.spawn_locid,
    source=excluded.source,
    autodetect_source=excluded.autodetect_source,
    status=excluded.status,
    experience_formula=excluded.experience_formula,
    wind_kmh=excluded.wind_kmh,
    session_id=excluded.session_id,
    cast_id=COALESCE(excluded.cast_id, catches.cast_id),
    windex=COALESCE(excluded.windex, catches.windex);"; // NEXT-ANALYTICS-04/09: never drop a proven link/value

                using var cmd = new SqliteCommand(sql, _connection);
                Add(cmd, "@record_id", catchData.record_id);
                Add(cmd, "@timestamp", string.IsNullOrEmpty(catchData.timestamp)
                    ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                    : catchData.timestamp);
                Add(cmd, "@fish_name", catchData.fish_name);
                Add(cmd, "@fish_lang_id", catchData.fish_lang_id);
                Add(cmd, "@network_fish_id", catchData.offset_32);
                Add(cmd, "@weight", catchData.weight);
                Add(cmd, "@grade", catchData.grade);
                Add(cmd, "@bait_id", catchData.bait_id);
                Add(cmd, "@bait_name", catchData.bait_name);
                Add(cmd, "@bait_category", catchData.bait_category);
                Add(cmd, "@depth_cm", catchData.depth_cm);
                Add(cmd, "@rod_id", catchData.rod_id);
                Add(cmd, "@reel_id", catchData.reel_id);
                Add(cmd, "@line_id", catchData.line_id);
                Add(cmd, "@hook_id", catchData.hook_id);
                Add(cmd, "@float_id", catchData.float_id);
                Add(cmd, "@feeder_id", catchData.feeder_id);
                Add(cmd, "@equipment_source", catchData.equipment_source);
                Add(cmd, "@cast_x", catchData.cast_x);
                Add(cmd, "@cast_y", catchData.cast_y);
                Add(cmd, "@cast_z", catchData.cast_z);
                Add(cmd, "@air_temp", catchData.air_temperature_c);
                Add(cmd, "@wind_ms", catchData.wind_ms);
                Add(cmd, "@humidity", catchData.humidity);
                Add(cmd, "@location_id", catchData.location_id);
                Add(cmd, "@experience", catchData.exp);
                Add(cmd, "@quality", catchData.quality);
                Add(cmd, "@quality_float", catchData.quality_float);
                Add(cmd, "@baseid", catchData.baseid);
                Add(cmd, "@bait_uid", catchData.bait_uid);
                Add(cmd, "@range_values", string.IsNullOrEmpty(catchData.range_values_json) ? null : catchData.range_values_json);
                Add(cmd, "@spawn_locid", catchData.spawn_locid);
                Add(cmd, "@source", string.IsNullOrEmpty(catchData.source) ? "auto" : catchData.source);
                Add(cmd, "@autodetect_source", string.IsNullOrEmpty(catchData.autodetect_source) ? null : catchData.autodetect_source);
                Add(cmd, "@status", catchData.status);
                Add(cmd, "@experience_formula", catchData.experience_formula);
                Add(cmd, "@wind_kmh", catchData.wind_kmh);
                Add(cmd, "@session_id", sessionId ?? _sessionId);
                Add(cmd, "@cast_id", catchData.cast_id); // NEXT-ANALYTICS-04
                Add(cmd, "@windex", catchData.windex); // NEXT-ANALYTICS-09
                cmd.ExecuteNonQuery();
            }
        }

        private void TrackWrite(Task task)
        {
            long id = Interlocked.Increment(ref _writeSequence);
            _pendingWrites[id] = task;
            task.ContinueWith(t =>
            {
                _pendingWrites.TryRemove(id, out Task? _);
            }, TaskScheduler.Default);
        }

        /// <summary>Фоновая запись одной поимки (не блокирует UI-поток).</summary>
        public Task SaveCatchAsync(CatchData catchData)
        {
            var task = Task.Run(() => SaveCatch(catchData));
            TrackWrite(task);
            return task;
        }

        public Task UpdateCatchAsync(CatchData catchData)
        {
            var task = Task.Run(() => UpdateCatch(catchData));
            TrackWrite(task);
            return task;
        }

        /// <summary>
        /// Фоновая запись пачки. Транзакция общая, поэтому пачка дешевле
        /// серии одиночных коммитов.
        /// </summary>
        public Task SaveCatchBatchAsync(IReadOnlyList<CatchData> batch)
        {
            if (batch == null || batch.Count == 0) return Task.CompletedTask;

            var task = Task.Run(() =>
            {
                lock (_sync)
                {
                    if (_connection == null) return;

                    using var transaction = _connection.BeginTransaction();
                    foreach (var catchData in batch)
                    {
                        if (catchData == null) continue;
                        if (string.IsNullOrWhiteSpace(catchData.record_id))
                            catchData.record_id = Guid.NewGuid().ToString("N");

                        using var cmd = new SqliteCommand(@"
INSERT OR REPLACE INTO catches (
    record_id, timestamp, fish_name, fish_lang_id, network_fish_id, weight, grade,
    bait_id, bait_name, bait_category, depth_cm,
    rod_id, reel_id, line_id, hook_id, float_id, feeder_id, equipment_source,
    cast_x, cast_y, cast_z,
    air_temp, wind_ms, humidity, location_id, experience, quality,
    quality_float, baseid, bait_uid, range_values, spawn_locid,
    source, autodetect_source, status, experience_formula, wind_kmh, session_id, cast_id)
VALUES (
    @record_id, @timestamp, @fish_name, @fish_lang_id, @network_fish_id, @weight, @grade,
    @bait_id, @bait_name, @bait_category, @depth_cm,
    @rod_id, @reel_id, @line_id, @hook_id, @float_id, @feeder_id, @equipment_source,
    @cast_x, @cast_y, @cast_z,
    @air_temp, @wind_ms, @humidity, @location_id, @experience, @quality,
    @quality_float, @baseid, @bait_uid, @range_values, @spawn_locid,
    @source, @autodetect_source, @status, @experience_formula, @wind_kmh, @session_id, @cast_id);",
                            _connection, transaction);

                        AddCatchParameters(cmd, catchData);
                        cmd.Parameters.AddWithValue("@session_id", _sessionId);
                        cmd.ExecuteNonQuery();
                    }
                    transaction.Commit();
                }
            });
            TrackWrite(task);
            return task;
        }

        /// <summary>
        /// Обновление существующей записи по record_id с сохранением session_id.
        /// Если строки ещё нет — вставляет её в текущую сессию.
        /// </summary>
        public void UpdateCatch(CatchData catchData)
        {
            if (catchData == null) return;
            if (string.IsNullOrWhiteSpace(catchData.record_id))
            {
                SaveCatch(catchData);
                return;
            }

            lock (_sync)
            {
                if (_connection == null) return;

                const string sql = @"
UPDATE catches SET
    timestamp=@timestamp,
    fish_name=@fish_name,
    fish_lang_id=@fish_lang_id,
    network_fish_id=@network_fish_id,
    weight=@weight,
    grade=@grade,
    bait_id=@bait_id,
    bait_name=@bait_name,
    bait_category=@bait_category,
    depth_cm=@depth_cm,
    rod_id=@rod_id,
    reel_id=@reel_id,
    line_id=@line_id,
    hook_id=@hook_id,
    float_id=@float_id,
    feeder_id=@feeder_id,
    equipment_source=@equipment_source,
    cast_x=@cast_x,
    cast_y=@cast_y,
    cast_z=@cast_z,
    air_temp=@air_temp,
    wind_ms=@wind_ms,
    humidity=@humidity,
    location_id=@location_id,
    experience=@experience,
    quality=@quality,
    quality_float=@quality_float,
    bait_uid=@bait_uid,
    range_values=@range_values,
    spawn_locid=@spawn_locid,
    source=@source,
    autodetect_source=@autodetect_source,
    status=@status,
    experience_formula=@experience_formula,
    wind_kmh=@wind_kmh,
    cast_id=COALESCE(@cast_id, cast_id),
    windex=COALESCE(@windex, windex)
WHERE record_id=@record_id;"; // NEXT-ANALYTICS-04/09: never drop a proven link/value

                using var cmd = new SqliteCommand(sql, _connection);
                AddCatchParameters(cmd, catchData);
                int rows = cmd.ExecuteNonQuery();
                if (rows == 0)
                    SaveCatch(catchData);
            }
        }

        private static void AddCatchParameters(SqliteCommand cmd, CatchData catchData)
        {
            cmd.Parameters.AddWithValue("@record_id", catchData.record_id);
            cmd.Parameters.AddWithValue("@timestamp", string.IsNullOrEmpty(catchData.timestamp)
                ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                : catchData.timestamp);
            cmd.Parameters.AddWithValue("@fish_name", catchData.fish_name);
            cmd.Parameters.AddWithValue("@fish_lang_id", (object?)catchData.fish_lang_id ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@network_fish_id", (object?)catchData.offset_32 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@weight", catchData.weight);
            cmd.Parameters.AddWithValue("@grade", catchData.grade);
            cmd.Parameters.AddWithValue("@bait_id", (object?)catchData.bait_id ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@bait_name", catchData.bait_name);
            cmd.Parameters.AddWithValue("@bait_category", catchData.bait_category);
            cmd.Parameters.AddWithValue("@depth_cm", (object?)catchData.depth_cm ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@rod_id", (object?)catchData.rod_id ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@reel_id", (object?)catchData.reel_id ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@line_id", (object?)catchData.line_id ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@hook_id", (object?)catchData.hook_id ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@float_id", (object?)catchData.float_id ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@feeder_id", (object?)catchData.feeder_id ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@equipment_source", catchData.equipment_source);
            cmd.Parameters.AddWithValue("@cast_x", (object?)catchData.cast_x ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@cast_y", (object?)catchData.cast_y ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@cast_z", (object?)catchData.cast_z ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@air_temp", (object?)catchData.air_temperature_c ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@wind_ms", (object?)catchData.wind_ms ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@humidity", (object?)catchData.humidity ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@location_id", (object?)catchData.location_id ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@experience", catchData.exp);
            cmd.Parameters.AddWithValue("@quality", (object?)catchData.quality ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@quality_float", (object?)catchData.quality_float ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@bait_uid", (object?)catchData.bait_uid ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@range_values", string.IsNullOrEmpty(catchData.range_values_json) ? DBNull.Value : catchData.range_values_json);
            cmd.Parameters.AddWithValue("@spawn_locid", (object?)catchData.spawn_locid ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@source", string.IsNullOrEmpty(catchData.source) ? "auto" : catchData.source);
            cmd.Parameters.AddWithValue("@autodetect_source", string.IsNullOrEmpty(catchData.autodetect_source) ? DBNull.Value : catchData.autodetect_source);
            cmd.Parameters.AddWithValue("@status", catchData.status);
            cmd.Parameters.AddWithValue("@experience_formula", catchData.experience_formula);
            cmd.Parameters.AddWithValue("@wind_kmh", (object?)catchData.wind_kmh ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@cast_id", (object?)catchData.cast_id ?? DBNull.Value); // NEXT-ANALYTICS-04
            cmd.Parameters.AddWithValue("@windex", (object?)catchData.windex ?? DBNull.Value); // NEXT-ANALYTICS-09
        }

        private void Add(SqliteCommand cmd, string name, object? value)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        public List<CatchData> GetAllCatches()
        {
            lock (_sync)
            {
                var result = new List<CatchData>();
                if (_connection == null) return result;

                using var cmd = new SqliteCommand(
                    "SELECT * FROM catches ORDER BY timestamp DESC, id DESC;", _connection);
                using var reader = cmd.ExecuteReader();

                int idRecord = Ordinal(reader, "record_id");
                int idTs = Ordinal(reader, "timestamp");
                int idFish = Ordinal(reader, "fish_name");
                int idFishLang = Ordinal(reader, "fish_lang_id");
                int idNet = Ordinal(reader, "network_fish_id");
                int idWeight = Ordinal(reader, "weight");
                int idGrade = Ordinal(reader, "grade");
                int idBait = Ordinal(reader, "bait_id");
                int idBaitName = Ordinal(reader, "bait_name");
                int idBaitCat = Ordinal(reader, "bait_category");
                int idDepth = Ordinal(reader, "depth_cm");
                int idRod = Ordinal(reader, "rod_id");
                int idReel = Ordinal(reader, "reel_id");
                int idLine = Ordinal(reader, "line_id");
                int idHook = Ordinal(reader, "hook_id");
                int idFloat = Ordinal(reader, "float_id");
                int idFeeder = Ordinal(reader, "feeder_id");
                int idEqSource = Ordinal(reader, "equipment_source");
                int idCx = Ordinal(reader, "cast_x");
                int idCy = Ordinal(reader, "cast_y");
                int idCz = Ordinal(reader, "cast_z");
                int idTemp = Ordinal(reader, "air_temp");
                int idWind = Ordinal(reader, "wind_ms");
                int idHum = Ordinal(reader, "humidity");
                int idLoc = Ordinal(reader, "location_id");
                int idExp = Ordinal(reader, "experience");
                int idQuality = Ordinal(reader, "quality");
                int idQualityFloat = Ordinal(reader, "quality_float");
                int idBaitUid = Ordinal(reader, "bait_uid");
                int idRangeValues = Ordinal(reader, "range_values");
                int idSpawnLocid = Ordinal(reader, "spawn_locid");
                int idSource = Ordinal(reader, "source");
                int idAutoSource = Ordinal(reader, "autodetect_source");
                int idStatus = Ordinal(reader, "status");
                int idFormula = Ordinal(reader, "experience_formula");
                int idWindKmh = Ordinal(reader, "wind_kmh");
                int idCastId = Ordinal(reader, "cast_id"); // NEXT-ANALYTICS-04
                int idWindex = Ordinal(reader, "windex"); // NEXT-ANALYTICS-09

                while (reader.Read())
                {
                    string ts = GetString(reader, idTs) ?? "";
                    DateTime detected = DateTime.MinValue;
                    DateTime.TryParse(ts, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out detected);

                    var c = new CatchData
                    {
                        record_id = GetString(reader, idRecord) ?? Guid.NewGuid().ToString("N"),
                        timestamp = ts,
                        detected_at = detected,
                        fish_name = GetString(reader, idFish) ?? "",
                        fish_lang_id = GetNullableUInt(reader, idFishLang),
                        offset_32 = GetNullableInt(reader, idNet),
                        weight = GetInt(reader, idWeight),
                        grade = GetString(reader, idGrade) ?? "",
                        bait_id = GetNullableUInt(reader, idBait),
                        bait_name = GetString(reader, idBaitName) ?? "",
                        bait_category = GetString(reader, idBaitCat) ?? "",
                        depth_cm = GetNullableFloat(reader, idDepth),
                        rod_id = GetNullableUInt(reader, idRod),
                        reel_id = GetNullableUInt(reader, idReel),
                        line_id = GetNullableUInt(reader, idLine),
                        hook_id = GetNullableUInt(reader, idHook),
                        float_id = GetNullableUInt(reader, idFloat),
                        feeder_id = GetNullableUInt(reader, idFeeder),
                        equipment_source = GetString(reader, idEqSource) ?? "",
                        cast_x = GetNullableDouble(reader, idCx),
                        cast_y = GetNullableDouble(reader, idCy),
                        cast_z = GetNullableDouble(reader, idCz),
                        air_temperature_c = GetNullableFloat(reader, idTemp),
                        wind_ms = GetNullableFloat(reader, idWind),
                        humidity = GetNullableFloat(reader, idHum),
                        location_id = GetNullableUInt(reader, idLoc),
                        exp = GetInt(reader, idExp),
                        quality = GetNullableInt(reader, idQuality),
                        quality_float = GetNullableDouble(reader, idQualityFloat),
                        bait_uid = GetNullableUInt(reader, idBaitUid),
                        range_values_json = GetString(reader, idRangeValues) ?? "",
                        spawn_locid = GetNullableInt(reader, idSpawnLocid),
                        source = GetString(reader, idSource) ?? "auto",
                        autodetect_source = GetString(reader, idAutoSource) ?? "",
                        status = GetString(reader, idStatus) ?? "confirmed",
                        experience_formula = GetString(reader, idFormula) ?? "",
                        wind_kmh = GetNullableFloat(reader, idWindKmh),
                        cast_id = GetNullableLong(reader, idCastId), // NEXT-ANALYTICS-04
                        windex = GetNullableInt(reader, idWindex) // NEXT-ANALYTICS-09
                    };

                    // Раньше FULL_CATCH ошибочно писал fish_lang_id в location_id.
                    // Исправляем такие старые записи при чтении.
                    if (c.location_id.HasValue && c.fish_lang_id.HasValue &&
                        c.location_id.Value == c.fish_lang_id.Value)
                        c.location_id = null;

                    if (string.IsNullOrWhiteSpace(c.autodetect_source))
                    {
                        if (c.offset_32.HasValue) c.autodetect_source = "full";
                        else if (c.fish_lang_id.HasValue) c.autodetect_source = "fish_lang_id";
                        else if (!string.IsNullOrWhiteSpace(c.fish_name)) c.autodetect_source = "chat";
                        else c.autodetect_source = "none";
                    }

                    result.Add(c);
                }
                reader.Close();

                // NEXT-DATA-01: coordinates verified = equal to a recorded cast (same rule as the map window)
                var castPts = new List<(double x, double z)>();
                using (var kc = new SqliteCommand("SELECT cast_x, cast_z FROM casts WHERE cast_x IS NOT NULL AND cast_z IS NOT NULL", _connection))
                using (var kr = kc.ExecuteReader())
                    while (kr.Read()) castPts.Add((kr.GetDouble(0), kr.GetDouble(1)));
                // PRERELEASE-01: поиск по клеткам вместо перебора всех забросов на каждый улов (то же условие)
                var castGrid = new PointGrid<(double x, double z)>(castPts, p => p.x, p => p.z, GameCoordinates.CastMatchTolerance);
                foreach (var c in result)
                    if (c.cast_x.HasValue && c.cast_z.HasValue)
                        c.coord_verified = castGrid.Any(c.cast_x.Value, c.cast_z.Value, GameCoordinates.CastMatchTolerance);

                return result;
            }
        }

        public SessionStats GetSessionStats()
        {
            var stats = new SessionStats();

            lock (_sync)
            {
                if (_connection == null) return stats;

                using (var cmd = new SqliteCommand(@"
SELECT COUNT(*), IFNULL(AVG(weight), 0)
FROM catches
WHERE session_id=@s AND IFNULL(status,'') <> 'skipped';", _connection))
                {
                    cmd.Parameters.AddWithValue("@s", _sessionId);
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        stats.Total = reader.GetInt32(0);
                        stats.AverageWeight = reader.IsDBNull(1) ? 0 : reader.GetDouble(1);
                    }
                }

                using (var cmd = new SqliteCommand(@"
SELECT bait_name, COUNT(*) AS n
FROM catches
WHERE session_id=@s
  AND bait_name IS NOT NULL AND bait_name <> ''
  AND bait_name <> '...' AND bait_name <> '(не определена)'
GROUP BY bait_name
ORDER BY n DESC, bait_name ASC
LIMIT 1;", _connection))
                {
                    cmd.Parameters.AddWithValue("@s", _sessionId);
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        stats.TopBait = reader.GetString(0);
                        stats.TopBaitCount = reader.GetInt32(1);
                    }
                }

                using (var cmd = new SqliteCommand(@"
SELECT fish_name, COUNT(*) AS n
FROM catches
WHERE session_id=@s
  AND fish_name IS NOT NULL AND fish_name <> '' AND fish_name <> '???'
  AND fish_name NOT LIKE 'Неизвестно%' AND fish_name NOT LIKE 'Fish_%'
GROUP BY fish_name
ORDER BY n DESC, fish_name ASC
LIMIT 1;", _connection))
                {
                    cmd.Parameters.AddWithValue("@s", _sessionId);
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        stats.TopFish = reader.GetString(0);
                        stats.TopFishCount = reader.GetInt32(1);
                    }
                }
            }

            return stats;
        }

        public void SaveFeedAction(FeedAction action)
        {
            if (action == null) return;
            lock (_sync)
            {
                if (_connection == null) return;
                using var cmd = new SqliteCommand(@"
INSERT INTO feed_actions (
    timestamp, bait_uid, bait_item_id, bait_name, has_bait, session_id, rod_uid, prop_name)
VALUES (
    @timestamp, @bait_uid, @bait_item_id, @bait_name, @has_bait, @session_id, @rod_uid, @prop_name);", _connection);
                cmd.Parameters.AddWithValue("@rod_uid", (object?)action.RodUid ?? DBNull.Value);   // NEXT-DATA-01
                cmd.Parameters.AddWithValue("@prop_name", (object?)action.PropName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@timestamp",
                    action.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@bait_uid", action.BaitUid);
                cmd.Parameters.AddWithValue("@bait_item_id",
                    (object?)action.BaitItemId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@bait_name", action.BaitName);
                cmd.Parameters.AddWithValue("@has_bait", action.HasBait ? 1 : 0);
                cmd.Parameters.AddWithValue("@session_id", _sessionId);
                cmd.ExecuteNonQuery();
            }
        }

        public long SaveCast(CastRecord cast)
        {
            if (cast == null) return 0;
            lock (_sync)
            {
                if (_connection == null) return 0;
                using var cmd = new SqliteCommand(@"
INSERT INTO casts (timestamp, session_id, rod_index, cast_x, cast_y, cast_z, bait_uid, bait_tid, bait_name, outcome, source_key)
VALUES (@timestamp, @session_id, @rod, @x, @y, @z, @bait_uid, @bait_tid, @bait_name, @outcome, @source_key)
ON CONFLICT(source_key) WHERE source_key IS NOT NULL DO NOTHING;
SELECT id FROM casts WHERE source_key=@source_key AND source_key IS NOT NULL;", _connection);
                cmd.Parameters.AddWithValue("@timestamp", cast.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@session_id", (object?)cast.SessionId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@rod", cast.RodIndex);
                cmd.Parameters.AddWithValue("@x", (object?)cast.CastX ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@y", (object?)cast.CastY ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@z", (object?)cast.CastZ ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@bait_uid", (object?)cast.BaitUid ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@bait_tid", (object?)cast.BaitTid ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@bait_name", (object?)cast.BaitName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@outcome", cast.Outcome);
                cmd.Parameters.AddWithValue("@source_key", (object?)cast.SourceKey ?? DBNull.Value);
                cmd.ExecuteNonQuery(); // INSERT ... ON CONFLICT DO NOTHING (NEXT-94A S16)
                if (cast.SourceKey != null)
                {
                    using var sel = new SqliteCommand("SELECT id FROM casts WHERE source_key=@sk AND source_key IS NOT NULL;", _connection);
                    sel.Parameters.AddWithValue("@sk", cast.SourceKey);
                    object? r = sel.ExecuteScalar();
                    return r != null ? (long)r : 0L;
                }
                // null SourceKey: row always inserted -> last_insert_rowid()
                using var rowid = new SqliteCommand("SELECT last_insert_rowid();", _connection);
                return (long)(rowid.ExecuteScalar() ?? 0L);
            }
        }

        public void SaveCastEvent(CastEvent ev)
        {
            if (ev == null) return;
            lock (_sync)
            {
                if (_connection == null) return;
                using var cmd = new SqliteCommand(@"
INSERT INTO cast_events (cast_id, timestamp, event_type, is_client, fish_lang_id, weight, windex, chance, raw_text, match_status, source_key,
    wkf, bite_accuraty, bite_agressive, bite_stamina, bite_force, bite_down_shance, klev_type, hook_uid, bite_meta_source)
VALUES (@cast_id, @timestamp, @event_type, @is_client, @fid, @weight, @windex, @chance, @raw_text, @match_status, @source_key,
    @wkf, @acc, @agr, @sta, @frc, @dsh, @kt, @hook, @bms)
ON CONFLICT(source_key) WHERE source_key IS NOT NULL DO NOTHING;", _connection);
                var b = ev.Bite; // NEXT-BITE-IMPLEMENT-01: raw s2c 1120 flag=1 metadata (NULL for every other event)
                cmd.Parameters.AddWithValue("@wkf", b != null ? (double)b.Wkf : DBNull.Value);
                cmd.Parameters.AddWithValue("@acc", b != null ? (double)b.Accuraty : DBNull.Value);
                cmd.Parameters.AddWithValue("@agr", b != null ? (double)b.Agressive : DBNull.Value);
                cmd.Parameters.AddWithValue("@sta", b != null ? (double)b.Stamina : DBNull.Value);
                cmd.Parameters.AddWithValue("@frc", b != null ? (double)b.Force : DBNull.Value);
                cmd.Parameters.AddWithValue("@dsh", b != null ? (double)b.DownShance : DBNull.Value);
                cmd.Parameters.AddWithValue("@kt", b != null ? b.KlevType : DBNull.Value);
                cmd.Parameters.AddWithValue("@hook", b != null ? b.HookUid : DBNull.Value);
                cmd.Parameters.AddWithValue("@bms", b != null ? BiteMetadata.Source : DBNull.Value);
                cmd.Parameters.AddWithValue("@cast_id", ev.CastId);
                cmd.Parameters.AddWithValue("@timestamp", ev.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@event_type", ev.EventType);
                cmd.Parameters.AddWithValue("@is_client", ev.IsClient ? 1 : 0);
                cmd.Parameters.AddWithValue("@fid", (object?)ev.FishLangId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@weight", (object?)ev.Weight ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@windex", (object?)ev.Windex ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@chance", (object?)ev.Chance ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@raw_text", ev.RawText);
                cmd.Parameters.AddWithValue("@match_status", ev.MatchStatus);
                cmd.Parameters.AddWithValue("@source_key", (object?)ev.SourceKey ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        public void UpdateCastOutcome(long castId, string outcome)
        {
            if (castId <= 0) return;
            lock (_sync)
            {
                if (_connection == null) return;
                using var cmd = new SqliteCommand("UPDATE casts SET outcome=@outcome WHERE id=@id AND outcome='pending';", _connection);
                cmd.Parameters.AddWithValue("@outcome", outcome);
                cmd.Parameters.AddWithValue("@id", castId);
                cmd.ExecuteNonQuery();
            }
        }

        public void SaveWeather(WeatherRecord w)
        {
            if (w == null) return;
            lock (_sync)
            {
                if (_connection == null) return;
                using var cmd = new SqliteCommand(@"
INSERT INTO weather (timestamp, session_id, baseid, location_id, cloudly, fog, rain, tempr, grom, units_status, source_key)
VALUES (@timestamp, @session_id, @baseid, @location_id, @cloudly, @fog, @rain, @tempr, @grom, @units_status, @source_key)
ON CONFLICT(source_key) WHERE source_key IS NOT NULL DO NOTHING;", _connection);
                cmd.Parameters.AddWithValue("@timestamp", w.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@session_id", (object?)w.SessionId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@baseid", (object?)w.BaseId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@location_id", (object?)w.LocationId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@cloudly", (object?)w.Cloudly ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@fog", (object?)w.Fog ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@rain", (object?)w.Rain ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@tempr", (object?)w.Tempr ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@grom", (object?)w.Grom ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@units_status", w.UnitsStatus);
                cmd.Parameters.AddWithValue("@source_key", (object?)w.SourceKey ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        public void SaveCraftAction(CraftAction action)
        {
            if (action == null) return;
            lock (_sync)
            {
                if (_connection == null) return;
                using var cmd = new SqliteCommand(@"
INSERT INTO craft_actions (
    timestamp, main_wpn_uid, main_wpn_item_id, aroma_wpn_uid,
    aroma_wpn_item_id, add_wpn_uid, add_wpn_item_id,
    bait_uid, bait_item_id, session_id)
VALUES (
    @timestamp, @main_wpn_uid, @main_wpn_item_id, @aroma_wpn_uid,
    @aroma_wpn_item_id, @add_wpn_uid, @add_wpn_item_id,
    @bait_uid, @bait_item_id, @session_id);", _connection);
                cmd.Parameters.AddWithValue("@timestamp",
                    action.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@main_wpn_uid", action.MainWpnUid);
                cmd.Parameters.AddWithValue("@main_wpn_item_id",
                    (object?)action.MainWpnItemId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@aroma_wpn_uid", action.AromaWpnUid);
                cmd.Parameters.AddWithValue("@aroma_wpn_item_id",
                    (object?)action.AromaWpnItemId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@add_wpn_uid", action.AddWpnUid);
                cmd.Parameters.AddWithValue("@add_wpn_item_id",
                    (object?)action.AddWpnItemId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@bait_uid", action.BaitUid);
                cmd.Parameters.AddWithValue("@bait_item_id",
                    (object?)action.BaitItemId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@session_id", _sessionId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Сохранить поимку другого игрока из локального чата.</summary>
        public void SaveOtherPlayerCatch(OtherPlayerCatch catchData)
        {
            if (catchData == null) return;
            if (string.IsNullOrWhiteSpace(catchData.FishName) ||
                string.IsNullOrWhiteSpace(catchData.PlayerName))
                return;

            lock (_sync)
            {
                if (_connection == null) return;

                const string sql = @"
INSERT INTO other_players_catches (
    timestamp, player_name, clan, fish_name, weight,
    my_location_id, my_cast_x, my_cast_z, distance_meters)
VALUES (
    @timestamp, @player_name, @clan, @fish_name, @weight,
    @my_location_id, @my_cast_x, @my_cast_z, @distance_meters);";

                using var cmd = new SqliteCommand(sql, _connection);
                DateTime ts = catchData.Timestamp == default ? DateTime.Now : catchData.Timestamp;
                Add(cmd, "@timestamp", ts.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
                Add(cmd, "@player_name", catchData.PlayerName.Trim());
                Add(cmd, "@clan", string.IsNullOrWhiteSpace(catchData.Clan) ? null : catchData.Clan.Trim());
                Add(cmd, "@fish_name", catchData.FishName.Trim());
                Add(cmd, "@weight", catchData.Weight);
                Add(cmd, "@my_location_id", catchData.MyLocationId);
                Add(cmd, "@my_cast_x", catchData.MyCastX);
                Add(cmd, "@my_cast_z", catchData.MyCastZ);
                Add(cmd, "@distance_meters", catchData.DistanceMeters);
                cmd.ExecuteNonQuery();
            }
        }

        public List<OtherPlayerCatch> GetOtherPlayersCatches(int limit = 1000)
        {
            var result = new List<OtherPlayerCatch>();
            lock (_sync)
            {
                if (_connection == null) return result;

                using var cmd = new SqliteCommand(@"
SELECT id, timestamp, player_name, clan, fish_name, weight,
       my_location_id, my_cast_x, my_cast_z, distance_meters
FROM other_players_catches
ORDER BY timestamp DESC, id DESC
LIMIT @limit;", _connection);
                cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 100000));
                using var reader = cmd.ExecuteReader();

                int idId = reader.GetOrdinal("id");
                int idTs = reader.GetOrdinal("timestamp");
                int idPlayer = reader.GetOrdinal("player_name");
                int idClan = reader.GetOrdinal("clan");
                int idFish = reader.GetOrdinal("fish_name");
                int idWeight = reader.GetOrdinal("weight");
                int idLoc = reader.GetOrdinal("my_location_id");
                int idX = reader.GetOrdinal("my_cast_x");
                int idZ = reader.GetOrdinal("my_cast_z");
                int idDist = reader.GetOrdinal("distance_meters");

                while (reader.Read())
                {
                    string tsText = GetString(reader, idTs) ?? "";
                    DateTime.TryParse(tsText, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTime ts);

                    result.Add(new OtherPlayerCatch
                    {
                        id = reader.GetInt64(idId),
                        Timestamp = ts == default ? DateTime.MinValue : ts,
                        PlayerName = GetString(reader, idPlayer) ?? "",
                        Clan = GetString(reader, idClan) ?? "",
                        FishName = GetString(reader, idFish) ?? "",
                        Weight = GetInt(reader, idWeight),
                        MyLocationId = GetNullableUInt(reader, idLoc),
                        MyCastX = GetNullableDouble(reader, idX),
                        MyCastZ = GetNullableDouble(reader, idZ),
                        DistanceMeters = GetNullableDouble(reader, idDist)
                    });
                }
            }
            return result;
        }

        /// <summary>
        /// Сводка по чужим поимкам + сравнение с моими по той же рыбе.
        /// </summary>
        public OtherPlayersSummary GetOtherPlayersSummary()
        {
            var summary = new OtherPlayersSummary();
            lock (_sync)
            {
                if (_connection == null) return summary;

                using (var cmd = new SqliteCommand(@"
SELECT COUNT(*),
       COUNT(DISTINCT player_name),
       COUNT(DISTINCT fish_name)
FROM other_players_catches
WHERE fish_name IS NOT NULL AND fish_name <> '';", _connection))
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        summary.TotalCatches = reader.GetInt32(0);
                        summary.UniquePlayers = reader.GetInt32(1);
                        summary.UniqueFish = reader.GetInt32(2);
                    }
                }

                var otherFish = new Dictionary<string, (int Count, double Avg)>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = new SqliteCommand(@"
SELECT fish_name, COUNT(*) AS n, IFNULL(AVG(weight), 0) AS avg_w
FROM other_players_catches
WHERE fish_name IS NOT NULL AND fish_name <> ''
GROUP BY fish_name
ORDER BY n DESC, fish_name ASC;", _connection))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string fish = reader.GetString(0);
                        int count = reader.GetInt32(1);
                        double avg = reader.IsDBNull(2) ? 0 : Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture);
                        otherFish[fish] = (count, avg);
                    }
                }

                var myFish = new Dictionary<string, (int Count, double Avg)>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = new SqliteCommand(@"
SELECT fish_name, COUNT(*) AS n, IFNULL(AVG(weight), 0) AS avg_w
FROM catches
WHERE fish_name IS NOT NULL AND fish_name <> ''
  AND fish_name <> '???' AND fish_name NOT LIKE 'Неизвестно%' AND fish_name NOT LIKE 'Fish_%'
  AND IFNULL(status, '') <> 'skipped'
  AND weight > 0
GROUP BY fish_name;", _connection))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string fish = reader.GetString(0);
                        int count = reader.GetInt32(1);
                        double avg = reader.IsDBNull(2) ? 0 : Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture);
                        myFish[fish] = (count, avg);
                    }
                }

                foreach (var pair in otherFish)
                {
                    myFish.TryGetValue(pair.Key, out var mine);
                    summary.FishStats.Add(new OtherPlayerFishStat
                    {
                        FishName = pair.Key,
                        OtherCount = pair.Value.Count,
                        OtherAvgWeight = pair.Value.Avg,
                        MyCount = mine.Count,
                        MyAvgWeight = mine.Avg
                    });
                }

                using (var cmd = new SqliteCommand(@"
SELECT player_name, clan, COUNT(*) AS n,
       IFNULL(AVG(weight), 0) AS avg_w,
       IFNULL(MAX(timestamp), '') AS last_ts
FROM other_players_catches
WHERE player_name IS NOT NULL AND player_name <> ''
GROUP BY player_name, clan
ORDER BY n DESC, player_name ASC
LIMIT 200;", _connection))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        summary.PlayerStats.Add(new OtherPlayerStat
                        {
                            PlayerName = reader.GetString(0),
                            Clan = reader.IsDBNull(1) ? "" : reader.GetString(1),
                            Count = reader.GetInt32(2),
                            AvgWeight = reader.IsDBNull(3) ? 0 : Convert.ToDouble(reader.GetValue(3), CultureInfo.InvariantCulture),
                            LastCatch = reader.IsDBNull(4) ? "" : reader.GetString(4)
                        });
                    }
                }
            }
            return summary;
        }

        /// <summary>
        /// Удаляет из БД заведомо паразитные записи с весом меньше минимального.
        /// Возвращает количество удалённых строк.
        /// </summary>
        public int DeleteCatchesBelowWeight(int minWeight)
        {
            lock (_sync)
            {
                if (_connection == null) return 0;

                using var cmd = new SqliteCommand(
                    "DELETE FROM catches WHERE weight IS NOT NULL AND weight > 0 AND weight < @min;",
                    _connection);
                cmd.Parameters.AddWithValue("@min", minWeight);
                return cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Сброс WAL на диск. Вызывать при завершении сессии/программы.</summary>
        public void Flush()
        {
            try
            {
                var pending = _pendingWrites.Values.ToArray();
                if (pending.Length > 0)
                    Task.WaitAll(pending, 3000);
            }
            catch
            {
                // Не блокируем закрытие из-за одной зависшей записи.
            }

            lock (_sync)
            {
                if (_connection == null) return;
                try
                {
                    Execute("PRAGMA wal_checkpoint(FULL);");
                }
                catch
                {
                    // Чекпойнт не критичен: транзакции уже закоммичены.
                }
            }
        }

        /// <summary>
        /// FORMULA-06 (B0): до этой версии шанс сервера из «wait» в колонку chance не попадал (передавался null), но остался в тексте
        /// события «rod=N chance=7,42 (…)». Один раз переносим число в колонку; пустые/непонятные строки не трогаем. Идемпотентно.
        /// </summary>
        private void BackfillWaitChance()
        {
            try
            {
                var rows = new List<(long id, double v)>();
                using (var q = new SqliteCommand("SELECT id, raw_text FROM cast_events WHERE event_type = 'wait' AND chance IS NULL AND raw_text LIKE '%chance=%'", _connection))
                using (var r = q.ExecuteReader())
                    while (r.Read()) if (!r.IsDBNull(1) && ParseRawChance(r.GetString(1)) is double v) rows.Add((r.GetInt64(0), v));
                if (rows.Count == 0) return;
                using var tx = _connection!.BeginTransaction();
                using var u = new SqliteCommand("UPDATE cast_events SET chance = $v WHERE id = $id AND chance IS NULL", _connection, tx);
                var pv = u.Parameters.Add("$v", SqliteType.Real); var pid = u.Parameters.Add("$id", SqliteType.Integer);
                foreach (var (id, v) in rows) { pv.Value = v; pid.Value = id; u.ExecuteNonQuery(); }
                tx.Commit();
            }
            catch { }
        }

        /// <summary>
        /// LOGFIX-02: уловы, записанные как «Неизвестно» (Player.log опередил сводку, а сводку отбросило правило повтора по весу).
        /// Вид, класс и заброс есть в событии поклёвки/поимки того же веса за несколько минут до записи — переносим, только если совпадение одно.
        /// Идемпотентно: трогаем только записи без вида.
        /// </summary>
        private void RepairUnknownSpecies()
        {
            try
            {
                var todo = new List<(long id, int w, string ts)>();
                using (var q = new SqliteCommand("SELECT id, weight, timestamp FROM catches WHERE fish_lang_id IS NULL AND weight > 0 AND status IN ('unknown','manual','not_landed') AND timestamp LIKE '20%'", _connection))
                using (var r = q.ExecuteReader())
                    while (r.Read()) todo.Add((r.GetInt64(0), r.GetInt32(1), r.GetString(2)));
                foreach (var (id, w, ts) in todo)
                {
                    if (!DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
                    var hits = new List<(long? cast, long fish, long? windex)>();
                    using (var q = new SqliteCommand("SELECT cast_id, fish_lang_id, windex FROM cast_events WHERE event_type IN ('bite','catch') AND fish_lang_id IS NOT NULL AND weight = $w AND timestamp >= $a AND timestamp <= $b", _connection))
                    {
                        q.Parameters.AddWithValue("$w", w);
                        q.Parameters.AddWithValue("$a", t.AddMinutes(-6).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                        q.Parameters.AddWithValue("$b", t.AddMinutes(2).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                        using var r = q.ExecuteReader();
                        while (r.Read()) hits.Add((r.IsDBNull(0) ? null : r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2)));
                    }
                    if (hits.Select(h => h.fish).Distinct().Count() != 1 || hits.Where(h => h.cast.HasValue).Select(h => h.cast).Distinct().Count() > 1) continue;
                    var f = hits[0].fish; var cast = hits.FirstOrDefault(h => h.cast.HasValue).cast; var wx = hits.FirstOrDefault(h => h.windex.HasValue).windex;
                    using var u = new SqliteCommand("UPDATE catches SET fish_lang_id = $f, fish_name = $n, windex = COALESCE(windex, $x), cast_id = COALESCE(cast_id, $c), status = 'confirmed', autodetect_source = 'repair_bite_event' WHERE id = $id AND fish_lang_id IS NULL", _connection);
                    u.Parameters.AddWithValue("$f", f); u.Parameters.AddWithValue("$n", SessionCoach.FishNameOf((uint)f));
                    u.Parameters.AddWithValue("$x", (object?)wx ?? DBNull.Value); u.Parameters.AddWithValue("$c", (object?)cast ?? DBNull.Value); u.Parameters.AddWithValue("$id", id);
                    u.ExecuteNonQuery();
                }
            }
            catch { }
        }

        /// <summary>«rod=1 chance=7,42 (…)» или «chance=7.42» → 7.42 (запятая или точка — текст писался в культуре ПК).</summary>
        public static double? ParseRawChance(string raw)
        {
            int i = raw.IndexOf("chance=", StringComparison.Ordinal);
            if (i < 0) return null;
            int j = i + 7, k = j;
            while (k < raw.Length && (char.IsDigit(raw[k]) || raw[k] is ',' or '.' or '-')) k++;
            string t = raw.Substring(j, k - j).Replace(',', '.');
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > -10 && v < 1000 ? v : null;
        }

        private void Execute(string sql)
        {
            if (_connection == null) return;
            using var cmd = new SqliteCommand(sql, _connection);
            cmd.ExecuteNonQuery();
        }

        private static int Ordinal(SqliteDataReader reader, string name)
        {
            try { return reader.GetOrdinal(name); }
            catch (IndexOutOfRangeException) { return -1; }
        }

        private static string? GetString(SqliteDataReader r, int i)
            => i < 0 || r.IsDBNull(i) ? null : r.GetString(i);

        private static int GetInt(SqliteDataReader r, int i)
            => i < 0 || r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture);

        private static long? GetNullableLong(SqliteDataReader r, int i) // NEXT-ANALYTICS-04
            => i < 0 || r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);

        private static int? GetNullableInt(SqliteDataReader r, int i)
            => i < 0 || r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture);

        private static uint? GetNullableUInt(SqliteDataReader r, int i)
        {
            if (i < 0 || r.IsDBNull(i)) return null;
            long value = Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
            return value < 0 ? null : (uint)value;
        }

        private static float? GetNullableFloat(SqliteDataReader r, int i)
            => i < 0 || r.IsDBNull(i) ? null : Convert.ToSingle(r.GetValue(i), CultureInfo.InvariantCulture);

        private static double? GetNullableDouble(SqliteDataReader r, int i)
            => i < 0 || r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;

                try { _connection?.Close(); } catch { }
                try { _connection?.Dispose(); } catch { }
                _connection = null;
            }
        }
    }
}
