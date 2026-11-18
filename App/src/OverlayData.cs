using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace FisherLogger
{
    /// <summary>Заброс с координатами (из таблицы casts).</summary>
    public sealed class OverlayCast
    {
        public long Id;
        public DateTime Time;          // время ПК в момент заброса (как записано в casts.timestamp)
        public double X, Z;
        public int Rod;
        /// <summary>true: заброс сделан после загрузки текущей базы (Player.log) — точно на текущем водоёме.</summary>
        public bool LocationConfirmed;
    }

    /// <summary>Событие рыбалки, привязанное к забросу с координатами (cast_events.match_status = MATCHED).</summary>
    public sealed class OverlayEvent
    {
        public string Type = "";       // "hook" (действие клиента), "empty_bite" (сообщение сервера cmd 1122)
        public DateTime Time;
        public double X, Z;
    }

    /// <summary>
    /// OVERLAY-03: заброс текущей рыбалки для круглой карты: точка наживки, где стоял игрок (по позе 1006) и что было дальше.
    /// State: wait (наживка в воде), bite (поклёвка/пустая поклёвка), hook (подсечка), catch (улов), done (вытащен без поклёвки / закрыт).
    /// </summary>
    public sealed class OverlayCastMark
    {
        public long Id;
        public DateTime Time;
        public int Rod;
        public double X, Z;
        public string State = "wait";
        public bool Active;            // последний заброс этой удочки и ещё не закрыт — наживка в воде
        public double? PlayerX, PlayerZ;
    }

    /// <summary>Снимок данных для оверлея (только чтение БД; ничего не записывается).</summary>
    public sealed class OverlaySnapshot
    {
        public uint? LocationId;
        public string LocationSource = "";              // "Player.log" / "последний улов" / ""
        public string LocationName = "";
        public WaterbodyMapInfo? Map;
        public System.Drawing.Image? MapImage;
        public List<MapCatch> Catches = new();          // подтверждённые уловы текущего водоёма с координатами
        public List<OverlayEvent> Events = new();       // hook / empty_bite текущего водоёма
        public List<MapCatch> RecentLog = new();        // последние уловы (текущая сессия, затем история)
        public OverlayCast? LastCast;
        /// <summary>OVERLAY-03: забросы текущей рыбалки на этом водоёме (новые первыми, до 24).</summary>
        public List<OverlayCastMark> Casts = new();
        /// <summary>OVERLAY-03: время загрузки текущей базы (Player.log); поза игрока раньше него — с прошлого водоёма.</summary>
        public DateTime? LocationSince;
        public List<string> Errors = new();
        public DateTime LoadedAt;
        /// <summary>NEXT-GTWC-01: лента — объявления об уловах игроков и публичный чат текущей БД, новые первыми.</summary>
        public List<FeedItem> Feed = new();
    }

    /// <summary>NEXT-GTWC-01: строка ленты (public_catches / chat_messages).</summary>
    public sealed class FeedItem
    {
        public string Kind = "";          // catch / rcrd / club / text / action
        public DateTime At;               // время приёма (ПК)
        public string? GameTime;          // оценка игрового времени «HH:mm» (модель GameClock), может отсутствовать
        public int? GameUncMin;
        public string Name = "";
        public string Text = "";          // текст сообщения или «Рыба 1 234 г»
        public string Channel = "";
        public int FishLangId;
        public int Weight;
        public long RowId;
    }

    /// <summary>
    /// Источник данных оверлея. Использует те же БД и тот же загрузчик, что окно карты (MapDataLoader, Mode=ReadOnly).
    /// История (все сессии) читается при смене водоёма; текущая БД — при изменении файла (catches.db / -wal).
    /// </summary>
    public sealed class OverlayDataSource
    {
        private readonly PacketDeepParser? _parser;
        private readonly Func<string> _currentDb;
        private readonly Func<string> _sessionDir;
        private List<MapCatch> _history = new();
        private List<OverlayEvent> _historyEvents = new();
        private uint? _historyLocation;
        private string _historyFor = "";
        private string _dbStamp = "";
        private OverlaySnapshot? _last;
        private uint? _lastReq;
        private DateTime? _lastSince;

        public OverlayDataSource(PacketDeepParser? parser, Func<string> currentDb, Func<string> sessionDir)
        {
            _parser = parser; _currentDb = currentDb; _sessionDir = sessionDir;
        }

        /// <summary>OVERLAY-03: живая позиция игрока (из трафика, не из БД).</summary>
        public PlayerTrack? Track => _parser?.Track;

        /// <summary>
        /// NEXT-GTWC-01: время записи, пригодное для сортировки и периодов. Даты из будущего (импорт PCAP с годом 3995)
        /// и нераспознанные — null: такие записи раньше всегда стояли первыми в «последних уловах» и закрывали новые.
        /// </summary>
        public static DateTime? PlausibleTime(string? text)
        {
            var t = ParseTime(text);
            if (!t.HasValue || t.Value.Year < 2000 || t.Value > DateTime.Now.AddDays(1)) return null;
            return t;
        }

        public static DateTime SortTime(MapCatch c) => PlausibleTime(c.TimeText) ?? DateTime.MinValue;

        /// <summary>Сколько раз перечитывались уловы текущей БД (для тестов: лента и погода их не перечитывают).</summary>
        public int CatchReloads { get; private set; }
        private List<MapCatch>? _cur;
        private string _curSig = "", _curDb = "";

        /// <summary>Дешёвая подпись таблицы catches: меняется при добавлении и при изменении вида/наживки/статуса/координат.</summary>
        private static string CatchesSignature(string db)
        {
            try
            {
                if (!File.Exists(db)) return "none";
                using var con = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False");
                con.Open();
                using var cmd = new SqliteCommand("SELECT count(*), ifnull(max(id),0), total(ifnull(weight,0) + length(ifnull(fish_name,'')) + length(ifnull(status,'')) + " +
                                                  "ifnull(bait_id,0) + ifnull(cast_x,0) + ifnull(location_id,0) + length(ifnull(timestamp,''))) FROM catches", con);
                using var r = cmd.ExecuteReader();
                return r.Read() ? r.GetInt64(0) + ":" + r.GetInt64(1) + ":" + r.GetDouble(2).ToString("R", CultureInfo.InvariantCulture) : "?";
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        public static DateTime? ParseTime(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string[] f = { "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss" };
            return DateTime.TryParseExact(text.Trim(), f, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) ? t : null;
        }

        /// <summary>Отметка изменения текущей БД: путь + время и размер catches.db и catches.db-wal.</summary>
        private string DbStamp()
        {
            string db = _currentDb();
            string Part(string p) { var fi = new FileInfo(p); return fi.Exists ? fi.LastWriteTimeUtc.Ticks + ":" + fi.Length : "-"; }
            return db + "|" + Part(db) + "|" + Part(db + "-wal");
        }

        /// <summary>Пересчитать снимок, если изменились БД или водоём; иначе вернуть прежний.</summary>
        public OverlaySnapshot Refresh(uint? liveLocation, DateTime? liveLocationSince, bool force = false)
        {
            var stamp = DbStamp();
            if (!force && _last != null && stamp == _dbStamp && _lastReq == liveLocation && _lastSince == liveLocationSince) return _last;
            _dbStamp = stamp; _lastReq = liveLocation; _lastSince = liveLocationSince;
            var snap = new OverlaySnapshot { LoadedAt = DateTime.Now, LocationSince = liveLocation.HasValue ? liveLocationSince : null };
            string db = _currentDb();
            string session = Path.GetFileName(_sessionDir().TrimEnd('\\', '/'));

            // ---- current session ----
            // NEXT-GTWC-01: уловы перечитываются только при изменении таблицы catches (а не при каждой записи погоды/чата).
            string sig = CatchesSignature(db);
            List<MapCatch> cur;
            if (!force && _cur != null && sig == _curSig && db == _curDb) cur = _cur;
            else
            {
                var (loaded, _, err) = MapDataLoader.Load(new[] { (session, db) }, _parser);
                snap.Errors.AddRange(err);
                cur = loaded; _cur = loaded; _curSig = sig; _curDb = db; CatchReloads++;
            }

            uint? loc = liveLocation;
            if (loc.HasValue) snap.LocationSource = "Player.log";
            else
            {
                var lastWithLoc = cur.Where(c => c.LocationId.HasValue).OrderByDescending(SortTime).ThenByDescending(c => c.TimeText, StringComparer.Ordinal).FirstOrDefault();
                if (lastWithLoc != null) { loc = lastWithLoc.LocationId; snap.LocationSource = "последний улов"; }
            }
            snap.LocationId = loc;

            // ---- history of this waterbody (all session DBs), reloaded when the waterbody changes ----
            if (loc != _historyLocation || _historyFor != db || force)
            {
                LoadHistory(loc, db);
                _historyLocation = loc; _historyFor = db;
            }

            // catches of this waterbody: verified coordinates only
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in cur.Concat(_history))
            {
                if (!c.Verified || !c.X.HasValue || !c.Z.HasValue || c.LocationId != loc || !loc.HasValue) continue;
                string key = string.IsNullOrEmpty(c.RecordId) ? c.Session + "|" + c.TimeText + "|" + c.X + "|" + c.Z : c.RecordId;
                if (seen.Add(key)) snap.Catches.Add(c);
            }

            // recent log: current session first (all catches, with or without coordinates), then history
            // NEXT-GTWC-01: по разобранному времени; записи с датой из будущего (год 3995) — в конце, а не сверху.
            snap.RecentLog = cur.OrderByDescending(SortTime).ThenByDescending(c => c.TimeText, StringComparer.Ordinal)
                .Concat(_history.Where(h => !cur.Any(c => !string.IsNullOrEmpty(c.RecordId) && c.RecordId == h.RecordId))
                                .OrderByDescending(SortTime).ThenByDescending(c => c.TimeText, StringComparer.Ordinal))
                .Take(200).ToList();   // NEXT-OVERLAY-02: запас для фильтра веса при показе

            // ---- casts / events of the current DB ----
            try
            {
                if (File.Exists(db))
                {
                    using var con = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False");
                    con.Open();
                    if (TableExists(con, "casts"))
                    {
                        using (var cmd = new SqliteCommand("SELECT id, timestamp, rod_index, cast_x, cast_z FROM casts WHERE cast_x IS NOT NULL AND cast_z IS NOT NULL ORDER BY id DESC LIMIT 1", con))
                        using (var r = cmd.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                var t = ParseTime(r.IsDBNull(1) ? null : r.GetString(1));
                                if (t.HasValue)
                                    snap.LastCast = new OverlayCast
                                    {
                                        Id = r.GetInt64(0), Time = t.Value, Rod = r.IsDBNull(2) ? -1 : r.GetInt32(2),
                                        X = r.GetDouble(3), Z = r.GetDouble(4),
                                        LocationConfirmed = liveLocation.HasValue && liveLocationSince.HasValue && t.Value >= liveLocationSince.Value
                                    };
                            }
                        }
                        snap.Casts = ReadCastMarks(con, liveLocation.HasValue ? liveLocationSince : null, DateTime.Now, Track);
                        // events of the live waterbody: only casts made after the current base was loaded
                        if (liveLocation.HasValue && liveLocationSince.HasValue && TableExists(con, "cast_events"))
                            snap.Events.AddRange(ReadEvents(con, liveLocationSince.Value));
                    }
                }
            }
            catch (Exception ex) { snap.Errors.Add("текущая БД: " + ex.Message); }
            snap.Events.AddRange(_historyEvents);
            try { snap.Feed = ReadFeed(db, 200, _parser); } catch (Exception ex) { snap.Errors.Add("лента: " + ex.Message); }

            // ---- map ----
            snap.Map = MapRegistry.Get(loc);
            snap.MapImage = snap.Map != null ? MapRegistry.LoadImage(snap.Map) : null;
            if (snap.MapImage == null) snap.Map = null;
            snap.LocationName = snap.Map?.Name ?? (loc.HasValue ? _parser?.GetLocation(loc)?.Name ?? "" : "");
            _last = snap;
            return snap;
        }

        public const double CastWindowMinutes = 90, ActiveCastMinutes = 45;
        public const int MaxCastMarks = 24;

        /// <summary>
        /// OVERLAY-03: забросы за последние 90 мин (и не раньше загрузки текущей базы) с исходом по cast_events (MATCHED).
        /// Последний заброс каждой удочки без закрывающего события и моложе 45 мин — «в воде».
        /// </summary>
        public static List<OverlayCastMark> ReadCastMarks(SqliteConnection con, DateTime? since, DateTime now, PlayerTrack? track)
        {
            var list = new List<OverlayCastMark>();
            if (!TableExists(con, "casts")) return list;
            bool ev = TableExists(con, "cast_events");
            DateTime from = now.AddMinutes(-CastWindowMinutes);
            if (since.HasValue && since.Value > from) from = since.Value;
            string sql = "SELECT c.id, c.timestamp, c.rod_index, c.cast_x, c.cast_z, ifnull(c.outcome,''), " +
                         (ev ? "(SELECT group_concat(e.event_type) FROM cast_events e WHERE e.cast_id = c.id AND e.match_status = 'MATCHED' " +
                               "AND e.event_type IN ('bite','empty_bite','hook','catch','retrieve','cast_end_unresolved'))" : "NULL") +
                         " FROM casts c WHERE c.cast_x IS NOT NULL AND c.cast_z IS NOT NULL ORDER BY c.id DESC LIMIT 200";
            using var cmd = new SqliteCommand(sql, con);
            using var r = cmd.ExecuteReader();
            var newestPerRod = new HashSet<int>();
            while (r.Read() && list.Count < MaxCastMarks)
            {
                var t = ParseTime(r.IsDBNull(1) ? null : r.GetString(1));
                if (!t.HasValue || t.Value < from || t.Value > now.AddMinutes(5)) continue;
                int rod = r.IsDBNull(2) ? -1 : r.GetInt32(2);
                string evs = r.IsDBNull(6) ? "" : r.GetString(6);
                string outcome = r.GetString(5);
                bool Has(string k) => ("," + evs + ",").Contains("," + k + ",");
                string state = Has("catch") || outcome == "caught" ? "catch"
                             : Has("hook") ? "hook"
                             : Has("bite") || Has("empty_bite") ? "bite"
                             : Has("retrieve") || Has("cast_end_unresolved") || outcome == "no_bite" ? "done" : "wait";
                bool newest = newestPerRod.Add(rod);
                bool closed = Has("catch") || Has("retrieve") || Has("cast_end_unresolved") || outcome is "caught" or "no_bite";
                var m = new OverlayCastMark
                {
                    Id = r.GetInt64(0), Time = t.Value, Rod = rod, X = r.GetDouble(3), Z = r.GetDouble(4), State = state,
                    Active = newest && !closed && (now - t.Value).TotalMinutes <= ActiveCastMinutes
                };
                if (!m.Active && state == "wait") m.State = "done";   // старый заброс без исхода: наживка уже не в воде
                var pose = track?.At(t.Value, 15);
                if (pose != null) { m.PlayerX = pose.X; m.PlayerZ = pose.Z; }
                list.Add(m);
            }
            return list;
        }

        private static IEnumerable<OverlayEvent> ReadEvents(SqliteConnection con, DateTime? since)
        {
            var list = new List<OverlayEvent>();
            using var cmd = new SqliteCommand(
                "SELECT e.event_type, e.timestamp, c.cast_x, c.cast_z, c.timestamp FROM cast_events e JOIN casts c ON c.id = e.cast_id " +
                "WHERE e.match_status = 'MATCHED' AND e.event_type IN ('hook','empty_bite') AND c.cast_x IS NOT NULL AND c.cast_z IS NOT NULL", con);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var t = ParseTime(r.IsDBNull(1) ? null : r.GetString(1));
                var ct = ParseTime(r.IsDBNull(4) ? null : r.GetString(4));
                if (!t.HasValue) continue;
                if (since.HasValue && (!ct.HasValue || ct.Value < since.Value)) continue;
                list.Add(new OverlayEvent { Type = r.GetString(0), Time = t.Value, X = r.GetDouble(2), Z = r.GetDouble(3) });
            }
            return list;
        }

        /// <summary>
        /// История: уловы всех сессий + события тех БД, где все подтверждённые уловы относятся к одному водоёму
        /// (то же правило привязки забросов к водоёму, что в окне карты). Текущая БД здесь не учитывается.
        /// </summary>
        private void LoadHistory(uint? loc, string currentDb)
        {
            _history = new(); _historyEvents = new();
            if (!loc.HasValue) return;
            string? root = Directory.GetParent(_sessionDir().TrimEnd('\\', '/'))?.FullName;
            if (root == null || !Directory.Exists(root)) return;
            foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string db = Path.Combine(dir, "data", "catches.db");
                if (!File.Exists(db) || string.Equals(Path.GetFullPath(db), Path.GetFullPath(currentDb), StringComparison.OrdinalIgnoreCase)) continue;
                var (cs, _, _) = MapDataLoader.Load(new[] { (Path.GetFileName(dir), db) }, _parser);
                _history.AddRange(cs);
                var locs = cs.Where(c => c.Verified && c.LocationId.HasValue).Select(c => c.LocationId).Distinct().ToList();
                if (locs.Count != 1 || locs[0] != loc) continue;
                try
                {
                    using var con = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False");
                    con.Open();
                    if (TableExists(con, "casts") && TableExists(con, "cast_events")) _historyEvents.AddRange(ReadEvents(con, null));
                }
                catch { /* history DB without events: ignored */ }
            }
        }

        /// <summary>NEXT-GTWC-01: последние записи ленты из public_catches и chat_messages (если таблиц нет — пусто).</summary>
        public static List<FeedItem> ReadFeed(string db, int limit, PacketDeepParser? parser = null)
        {
            var list = new List<FeedItem>();
            if (!File.Exists(db)) return list;
            using var con = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False");
            con.Open();
            if (TableExists(con, "public_catches"))
            {
                using var cmd = new SqliteCommand("SELECT id, received_at, game_time_est, game_time_unc_min, player_name, fish_lang_id, fish_name, weight, channel, club_tag " +
                                                  "FROM public_catches ORDER BY id DESC LIMIT $n", con);
                cmd.Parameters.AddWithValue("$n", limit);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var at = ParseTime(r.IsDBNull(1) ? null : r.GetString(1));
                    if (!at.HasValue) continue;
                    string ch = r.IsDBNull(8) ? "" : r.GetString(8);
                    string tag = r.IsDBNull(9) ? "" : r.GetString(9);
                    int w = r.IsDBNull(7) ? 0 : r.GetInt32(7);
                    list.Add(new FeedItem
                    {
                        Kind = ch == "rcrd" || ch == "clubtrof" ? "rcrd" : ch.StartsWith("club") ? "club" : "catch",
                        At = at.Value, GameTime = r.IsDBNull(2) ? null : r.GetString(2), GameUncMin = r.IsDBNull(3) ? null : r.GetInt32(3),
                        Name = (tag.Length > 0 ? "[" + tag + "] " : "") + (r.IsDBNull(4) ? "" : r.GetString(4)),
                        FishLangId = r.IsDBNull(5) ? 0 : r.GetInt32(5), Weight = w,
                        Text = (r.IsDBNull(6) ? "" : r.GetString(6)) + " " + w.ToString("#,0", CultureInfo.GetCultureInfo("ru-RU")) + " г",
                        Channel = ch, RowId = r.GetInt64(0)
                    });
                }
            }
            // NEXT-GTWC-02: обычный чат (chat_messages) больше не читается в ленту — только public_catches.
            return list.OrderByDescending(f => f.At).ThenByDescending(f => f.RowId).Take(limit).ToList();
        }

        private static string ActionText(string name, string value, string raw, PacketDeepParser? parser) => name switch
        {
            "levelup" => "новый уровень " + value,
            "usewpn" => "использовал: " + (uint.TryParse(value, out uint iid) && parser?.GetItem(iid)?.Name is { Length: > 0 } nm ? nm : "#" + value),
            "brkline" => "обрыв лески",
            "brkrod" => "сломал удочку",
            "trophbase" => "трофей на базе",
            "groupinvite" => "приглашение в группу",
            "setitem" => "предмет " + value,
            _ => raw
        };

        private static bool TableExists(SqliteConnection con, string name)
        {
            using var cmd = new SqliteCommand("SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$n", con);
            cmd.Parameters.AddWithValue("$n", name);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
    }
}
