using System;
using System.Collections.Generic;
using System.Globalization;

namespace FisherLogger
{
    /// <summary>
    /// NEXT-ANALYTICS-04: single implementation of the fishing-attempt (cast) lifecycle, shared by the live
    /// application (Form1) and the offline replay harness (ReplayProgram), so both write the same semantics.
    ///
    /// One attempt = one cast (c2s 1110) of one rod. Events inside it (cast_events.event_type):
    ///   bite        s2c 1120 flag=1 (rod from the same cmd-1120 layout as the wait frame)
    ///   empty_bite  s2c 1122
    ///   hook        c2s 1125
    ///   wait        s2c 1120 flag=0
    ///   catch       a catch summary associated with this cast (DeepCatchSession / replay rod map)
    ///   retrieve    the cast was closed because the SAME rod was cast again (the rod must have been taken out of
    ///               the water before; the exact moment is unknown — the event carries the time of the new cast)
    ///               FIX-01: or because the server checks (c2s 1120, every ~8 s) of this rod stopped for a long time while
    ///               the game kept talking (CONTROLS-01: a reeled-in rig produces no packet of its own, its checks just stop).
    ///               The text names the last check seen (the rig left the water between that check and the event time).
    ///   cast_end_unresolved  the cast was closed without observing its end (session end, coverage gap, restart)
    ///
    /// Outcomes (casts.outcome):
    ///   pending  the cast is still open in this run
    ///   caught   a catch was associated with this cast
    ///   no_bite  closed by a recast on the same rod, observed without interruption, with ZERO bite/empty_bite/hook
    ///   missed   FIX-01: closed (recast or stopped server checks), observed without interruption, with bite/empty_bite but
    ///            ZERO hook — a fish cannot be landed without a hook (c2s 1125; a feeder on the pod sends it by itself),
    ///            so this attempt certainly ended without a catch (CONTROLS-01: bite without hook → no catch, bait kept)
    ///   unknown  everything else — bite/hook without an associated catch (possible escape, NOT confirmed),
    ///            session end, coverage gap, application restart
    /// A suspected escape is never written as such; an interrupted cast is never written as no_bite.
    ///
    /// Duplicate processing of the same packet is absorbed by deterministic source_key values
    /// (cast_events / casts have UNIQUE indexes on source_key): live keys are built from session, event type, rod
    /// and packet timestamp; replay passes its own packet-based key.
    /// Thread-safe.
    /// </summary>
    public sealed class CastLifecycleTracker
    {
        public const string Pending = "pending";
        public const string Caught = "caught";
        public const string NoBite = "no_bite";
        public const string Missed = "missed";   // FIX-01
        public const string Unknown = "unknown";

        public const string Matched = "MATCHED";
        public const string Unmatched = "UNMATCHED";
        public const string Ambiguous = "AMBIGUOUS";
        public const string Unassociated = "UNASSOCIATED";

        private sealed class OpenCast
        {
            public long Id;
            public int Rod;
            public DateTime At;
            public string? Key;
            public int Bites, EmptyBites, Hooks;
            public bool Uncertain;
            public DateTime? LastCheckAt, LastBiteAt;   // FIX-01
        }

        private readonly object _sync = new();
        private readonly CatchDbWriter _db;
        private readonly Func<string> _sessionId;
        private readonly string _keyPrefix;
        private readonly Dictionary<int, OpenCast> _open = new();
        private readonly Dictionary<int, long> _quietClosed = new();   // FIX-01: rod → cast closed by CloseQuiet (diagnostics only)

        /// <summary>FIX-01: number of server checks seen for a rod after its cast was closed by CloseQuiet (should stay 0).</summary>
        public int LateChecksAfterQuietClose { get; private set; }

        /// <param name="keyPrefix">"live" for the application; replay passes explicit keys per event.</param>
        public CastLifecycleTracker(CatchDbWriter db, Func<string> sessionId, string keyPrefix = "live")
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _keyPrefix = keyPrefix;
        }

        /// <summary>Diagnostics ([ATTEMPT] ...).</summary>
        public event Action<string>? Diagnostics;

        private string LiveKey(string type, int? rod, DateTime ts, string? extra = null)
            => _keyPrefix + "|" + _sessionId() + "|" + type + "|r" + (rod.HasValue ? rod.Value.ToString(CultureInfo.InvariantCulture) : "?") +
               "|" + ts.Ticks.ToString(CultureInfo.InvariantCulture) + (extra != null ? "|" + extra : "");

        private static string Ts(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

        /// <summary>Open cast id of a rod (0 = none).</summary>
        public long CurrentCastId(int rod)
        {
            lock (_sync) return _open.TryGetValue(rod, out var c) ? c.Id : 0;
        }

        public int OpenCount { get { lock (_sync) return _open.Count; } }

        /// <summary>
        /// c2s 1110. Closes the previous open cast of the same rod (retrieve → no_bite/unknown) and stores the new
        /// cast. A repeated identical packet (same key) is recognised and ignored. Returns the cast id (0 = not stored).
        /// </summary>
        public long OnCast(int? rod, DateTime ts, double? x, double? y, double? z, long? baitUid, string? sourceKey = null)
        {
            string key = sourceKey ?? LiveKey("cast", rod, ts);
            lock (_sync)
            {
                if (rod.HasValue) _quietClosed.Remove(rod.Value);   // FIX-01: a new cast ends the watch for late checks
                if (rod.HasValue && _open.TryGetValue(rod.Value, out var prev))
                {
                    if (prev.Key == key) return prev.Id; // duplicate processing of the same cast packet
                    CloseLocked(prev, ts, byRecast: true);
                    _open.Remove(rod.Value);
                }

                long id = _db.SaveCast(new CastRecord
                {
                    Timestamp = ts,
                    SessionId = _sessionId(),
                    RodIndex = rod ?? 0,
                    CastX = x,
                    CastY = y,
                    CastZ = z,
                    BaitUid = baitUid,       // bait instance uid only; its type is NOT resolved (no reliable mapping)
                    Outcome = Pending,
                    SourceKey = key
                });
                if (id > 0 && rod.HasValue)
                    _open[rod.Value] = new OpenCast { Id = id, Rod = rod.Value, At = ts, Key = key };
                else if (id > 0)
                    Report("[ATTEMPT] заброс #" + id + " без номера удочки — события к нему не привязываются");
                return id;
            }
        }

        /// <summary>Resolves the open cast of a rod: (id, MATCHED|AMBIGUOUS) or (0, UNMATCHED).</summary>
        public (long CastId, string Status) Resolve(int? rod)
        {
            lock (_sync) return ResolveLocked(rod);
        }

        private (long, string) ResolveLocked(int? rod)
        {
            if (!rod.HasValue || !_open.TryGetValue(rod.Value, out var c)) return (0, Unmatched);
            return (c.Id, c.Uncertain ? Ambiguous : Matched);
        }

        /// <summary>Stores a rod event (bite / empty_bite / hook / wait). Returns the cast id it was attached to (0 = none).</summary>
        public long OnRodEvent(string type, int? rod, DateTime ts, bool isClient, uint? fishLangId = null, uint? weight = null,
                               uint? windex = null, float? chance = null, string raw = "", string? sourceKey = null,
                               BiteMetadata? bite = null)
        {
            lock (_sync)
            {
                var (castId, status) = ResolveLocked(rod);
                if (castId > 0 && _open.TryGetValue(rod!.Value, out var c))
                {
                    if (ts < c.At)
                    {
                        // An event older than the open cast cannot belong to it.
                        castId = 0;
                        status = Unmatched;
                    }
                    else
                    {
                        if (type == "bite") { c.Bites++; c.LastBiteAt = ts; }
                        else if (type == "empty_bite") c.EmptyBites++;
                        else if (type == "hook") c.Hooks++;
                    }
                }
                _db.SaveCastEvent(new CastEvent
                {
                    CastId = castId,
                    Timestamp = ts,
                    EventType = type,
                    IsClient = isClient,
                    FishLangId = fishLangId,
                    Weight = weight,
                    Windex = windex,
                    Chance = chance,
                    RawText = raw ?? "",
                    MatchStatus = status,
                    SourceKey = sourceKey ?? LiveKey(type, rod, ts, weight.HasValue ? "w" + weight.Value.ToString(CultureInfo.InvariantCulture) : null),
                    Bite = type == "bite" ? bite : null   // NEXT-BITE-IMPLEMENT-01
                });
                if (castId == 0 && type != "wait")
                    Report("[ATTEMPT] " + type + " удочки " + (rod?.ToString(CultureInfo.InvariantCulture) ?? "?") +
                           " в " + Ts(ts) + " — открытого заброса нет, сохранено без привязки");
                return castId;
            }
        }

        /// <summary>
        /// A catch was associated with cast <paramref name="castId"/> (by DeepCatchSession or the replay rod map).
        /// Writes a 'catch' event, sets outcome = caught (only from pending) and closes the attempt.
        /// </summary>
        public void OnCatch(long castId, DateTime ts, uint? fishLangId, uint? weight, string? sourceKey = null, bool ambiguous = false)
        {
            if (castId <= 0) return;
            lock (_sync)
            {
                int? rod = null;
                foreach (var kv in _open)
                    if (kv.Value.Id == castId) { rod = kv.Key; break; }

                _db.SaveCastEvent(new CastEvent
                {
                    CastId = castId,
                    Timestamp = ts,
                    EventType = "catch",
                    IsClient = false,
                    FishLangId = fishLangId,
                    Weight = weight,
                    RawText = "catch summary associated with this cast",
                    MatchStatus = ambiguous ? Ambiguous : Matched,
                    SourceKey = sourceKey ?? (_keyPrefix + "|" + _sessionId() + "|catch|cast" + castId.ToString(CultureInfo.InvariantCulture))
                });
                _db.UpdateCastOutcome(castId, ambiguous ? Unknown : Caught);
                if (rod.HasValue) _open.Remove(rod.Value);
            }
        }

        /// <summary>FIX-01: a server check (c2s 1120) of the rod was seen at <paramref name="ts"/>.</summary>
        public void NoteCheck(int rod, DateTime ts)
        {
            lock (_sync)
            {
                if (_open.TryGetValue(rod, out var c))
                {
                    if (ts >= c.At && (!c.LastCheckAt.HasValue || ts > c.LastCheckAt.Value)) c.LastCheckAt = ts;
                }
                else if (_quietClosed.TryGetValue(rod, out long qid))
                {
                    LateChecksAfterQuietClose++;
                    _quietClosed.Remove(rod);
                    Report("[ATTEMPT] проверка удочки " + rod + " после закрытия заброса #" + qid + " по тишине — закрытие было преждевременным");
                }
            }
        }

        /// <summary>
        /// FIX-01: closes open casts whose server checks stopped for at least <paramref name="quiet"/> (the rig was reeled in).
        /// Only casts that had at least one check; never a cast with a hook (fight / possible escape stays open until the recast);
        /// nothing at all while ANY open cast has a hook — during a fight the checks of the other rods are skipped (gaps of up to
        /// ~55 s were seen, CONTROLS-01). The caller must make sure the game connection is alive (packets still arrive).
        /// Returns the number of closed casts.
        /// </summary>
        public int CloseQuiet(DateTime now, TimeSpan quiet)
        {
            lock (_sync)
            {
                foreach (var c in _open.Values) if (c.Hooks > 0) return 0;
                var done = new List<int>();
                foreach (var kv in _open)
                {
                    var c = kv.Value;
                    if (!c.LastCheckAt.HasValue) continue;
                    DateTime last = c.LastBiteAt.HasValue && c.LastBiteAt.Value > c.LastCheckAt.Value ? c.LastBiteAt.Value : c.LastCheckAt.Value;
                    if (now - last < quiet) continue;
                    CloseLocked(c, now, byRecast: false, quietClose: true);
                    done.Add(kv.Key);
                    _quietClosed[kv.Key] = c.Id;
                }
                foreach (int r in done) _open.Remove(r);
                return done.Count;
            }
        }

        /// <summary>Replay: a coverage gap — every open cast can no longer be observed without interruption.</summary>
        public void MarkAllOpenUncertain()
        {
            lock (_sync)
                foreach (var c in _open.Values) c.Uncertain = true;
        }

        /// <summary>Session end / application close / end of captured coverage: all open casts → unknown.</summary>
        public int CloseAll(DateTime ts, string reason)
        {
            lock (_sync)
            {
                int n = _open.Count;
                foreach (var c in _open.Values)
                {
                    _db.SaveCastEvent(new CastEvent
                    {
                        CastId = c.Id,
                        Timestamp = ts,
                        EventType = "cast_end_unresolved",
                        RawText = reason,
                        MatchStatus = Matched,
                        SourceKey = _keyPrefix + "|" + _sessionId() + "|end|cast" + c.Id.ToString(CultureInfo.InvariantCulture)
                    });
                    _db.UpdateCastOutcome(c.Id, Unknown);
                }
                _open.Clear();
                return n;
            }
        }

        /// <summary>
        /// LOGFIX-03 (C): the player moved (teleport to another spawn point or location load, Player.log) — every rig is out of the
        /// water at that moment, so open casts that started before <paramref name="ts"/> END here and are judged like a recast
        /// (observed without interruption: no activity → no_bite, bites without hook → missed, otherwise unknown).
        /// Before, they stayed open and became 'unknown' when the session or application was closed later.
        /// </summary>
        public int CloseEnded(DateTime ts, string reason)
        {
            lock (_sync)
            {
                var done = new List<int>();
                foreach (var kv in _open)
                {
                    if (kv.Value.At >= ts.AddSeconds(-3)) continue;   // a cast made right at / after the move is not touched
                    CloseLocked(kv.Value, ts, byRecast: false, endReason: reason);
                    done.Add(kv.Key);
                }
                foreach (int r in done) _open.Remove(r);
                return done.Count;
            }
        }

        /// <summary>Forget open casts without writing (in-memory reset only; e.g. a fresh replay).</summary>
        public void ResetInMemory()
        {
            lock (_sync) _open.Clear();
        }

        private void CloseLocked(OpenCast c, DateTime nextCastAt, bool byRecast, bool quietClose = false, string? endReason = null)
        {
            int activity = c.Bites + c.EmptyBites + c.Hooks;
            bool observed = !c.Uncertain && (byRecast || quietClose || endReason != null);
            // FIX-01: bites without any hook cannot have produced a catch → missed (not unknown)
            string outcome = observed && activity == 0 ? NoBite : observed && c.Hooks == 0 ? Missed : Unknown;
            string lastCheck = c.LastCheckAt.HasValue ? "; last server check " + Ts(c.LastCheckAt.Value) : "";
            _db.SaveCastEvent(new CastEvent
            {
                CastId = c.Id,
                Timestamp = nextCastAt,
                EventType = "retrieve",
                IsClient = true,
                RawText = (endReason != null ? endReason + " (all rigs leave the water at this time)" : quietClose
                              ? "server checks of rod " + c.Rod.ToString(CultureInfo.InvariantCulture) + " stopped (reeled in between the last check and this time)"
                              : "inferred from the next cast of rod " + c.Rod.ToString(CultureInfo.InvariantCulture) + " (retrieve happened at or before this time)") +
                          "; bites=" + c.Bites + " empty=" + c.EmptyBites + " hooks=" + c.Hooks + lastCheck + (c.Uncertain ? " coverage=uncertain" : ""),
                MatchStatus = c.Uncertain ? Ambiguous : Matched,
                SourceKey = _keyPrefix + "|" + _sessionId() + "|retrieve|cast" + c.Id.ToString(CultureInfo.InvariantCulture)
            });
            _db.UpdateCastOutcome(c.Id, outcome);
            Report("[ATTEMPT] заброс #" + c.Id + " удочки " + c.Rod + (endReason != null ? " закрыт: игрок сменил место (" + endReason + ") → " : quietClose ? " закрыт: проверки сервера прекратились (снасть смотана) → " : " закрыт перезабросом → ") + outcome +
                   " (поклёвок " + c.Bites + ", пустых " + c.EmptyBites + ", подсечек " + c.Hooks + ")");
        }

        private void Report(string message) => Diagnostics?.Invoke(message);
    }
}
