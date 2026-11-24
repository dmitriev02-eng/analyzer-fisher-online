using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FisherLogger
{
    /// <summary>
    /// NEXT-BITE-IMPLEMENT-01: fields of s2c 1120 flag=1 (bite) that NEXT-BITE-PROTOCOL-01 marks CONFIRMED
    /// (client UserActRod.URod.klevAction read order, checked on 94 captured bites).
    /// Layout of the cmd-1120 byte[] (little-endian):
    ///   rod int32 @0 | flag byte @4 (=1) | weight int32 @5 (g) | klevType: uint16 length @9 + bytes @11 |
    ///   q = 11 + length: accuraty f32 @q | agressive f32 @q+4 | stamina f32 @q+8 | force f32 @q+12 | downShance f32 @q+16 |
    ///   fishid int32 @q+20 | wkf f32 @q+24 | windex int32 @q+28 | hook_uid int64 @q+32 | end = q+40 (exact length).
    /// Values are stored raw (units of accuraty/agressive/stamina/force/downShance are not interpreted).
    /// The client multiplies force by its own random variance AFTER reading it; the stored value is the packet value.
    /// </summary>
    public sealed class BiteMetadata
    {
        public const string Source = "s2c1120_flag1";

        public int Rod { get; init; }
        public int Weight { get; init; }
        public string KlevType { get; init; } = "";
        public float Accuraty { get; init; }
        public float Agressive { get; init; }
        public float Stamina { get; init; }
        public float Force { get; init; }
        public float DownShance { get; init; }
        public int FishId { get; init; }
        public float Wkf { get; init; }
        public int Windex { get; init; }
        public long HookUid { get; init; }

        /// <summary>Strict parse: wrong flag, truncated or over-long payloads return null (nothing is guessed).</summary>
        public static BiteMetadata? Parse(byte[]? d)
        {
            if (d == null || d.Length < 11 || d[4] != 1) return null;
            int slen = BitConverter.ToUInt16(d, 9);
            int q = 11 + slen;
            if (d.Length != q + 40) return null;
            string kt;
            try { kt = new UTF8Encoding(false, true).GetString(d, 11, slen); }
            catch (DecoderFallbackException) { return null; }
            var m = new BiteMetadata
            {
                Rod = BitConverter.ToInt32(d, 0),
                Weight = BitConverter.ToInt32(d, 5),
                KlevType = kt,
                Accuraty = BitConverter.ToSingle(d, q),
                Agressive = BitConverter.ToSingle(d, q + 4),
                Stamina = BitConverter.ToSingle(d, q + 8),
                Force = BitConverter.ToSingle(d, q + 12),
                DownShance = BitConverter.ToSingle(d, q + 16),
                FishId = BitConverter.ToInt32(d, q + 20),
                Wkf = BitConverter.ToSingle(d, q + 24),
                Windex = BitConverter.ToInt32(d, q + 28),
                HookUid = BitConverter.ToInt64(d, q + 32)
            };
            if (m.Weight <= 0 || m.FishId <= 0 || m.Windex < 0 || m.Windex > 3 || float.IsNaN(m.Wkf)) return null;
            return m;
        }
    }

    /// <summary>
    /// NEXT-BITE-IMPLEMENT-01: one outgoing c2s 1120 (client URod.KLUpd, periodic rod state / server check), 112 bytes:
    ///   locid i32 @0 | rod i32 @4 | rod_uid i64 @8 | bait_pos 3×f64 @16 | grid_pos 3×f64 @40 (server-side 5 m cell) |
    ///   depth1 f32 @64 | depth2 f32 @68 | bait_uid i64 @72 | addbait_uid i64 @80 | zero i64 @88 | 7 flags @96..102 |
    ///   distance f32 @103 | rodcode i32 @107 | trolling bool @111.
    /// Flag order (KLUpd): moving, on_ground, top_down, simple_play, active_retrieve (play|jig step|uniform|long flight),
    /// ground_event, flag7 (getter not identified). depth1/depth2 units and meaning are NOT interpreted (stored raw).
    /// A check is an observation, NOT an independent attempt.
    /// </summary>
    public sealed class ServerCheck
    {
        public const string Provenance = "c2s1120 URod.KLUpd 112B (NEXT-BITE-PROTOCOL-01)";
        public DateTime Timestamp { get; init; }
        public int LocId { get; init; }
        public int Rod { get; init; }
        public long RodUid { get; init; }
        public double BaitX { get; init; }
        public double BaitY { get; init; }
        public double BaitZ { get; init; }
        public double GridX { get; init; }
        public double GridY { get; init; }
        public double GridZ { get; init; }
        public float Depth1 { get; init; }
        public float Depth2 { get; init; }
        public long BaitUid { get; init; }
        public long AddBaitUid { get; init; }
        public long Zero { get; init; }
        public byte[] Flags { get; init; } = Array.Empty<byte>();
        public float Distance { get; init; }
        public int RodCode { get; init; }
        public bool Trolling { get; init; }
        public string PayloadHex { get; init; } = "";

        public static ServerCheck? Parse(byte[]? d, DateTime ts)
        {
            if (d == null || d.Length != 112) return null;
            var flags = new byte[7];
            Array.Copy(d, 96, flags, 0, 7);
            foreach (var f in flags) if (f > 1) return null;   // bools only
            if (d[111] > 1) return null;
            var c = new ServerCheck
            {
                Timestamp = ts,
                LocId = BitConverter.ToInt32(d, 0),
                Rod = BitConverter.ToInt32(d, 4),
                RodUid = BitConverter.ToInt64(d, 8),
                BaitX = BitConverter.ToDouble(d, 16), BaitY = BitConverter.ToDouble(d, 24), BaitZ = BitConverter.ToDouble(d, 32),
                GridX = BitConverter.ToDouble(d, 40), GridY = BitConverter.ToDouble(d, 48), GridZ = BitConverter.ToDouble(d, 56),
                Depth1 = BitConverter.ToSingle(d, 64),
                Depth2 = BitConverter.ToSingle(d, 68),
                BaitUid = BitConverter.ToInt64(d, 72),
                AddBaitUid = BitConverter.ToInt64(d, 80),
                Zero = BitConverter.ToInt64(d, 88),
                Flags = flags,
                Distance = BitConverter.ToSingle(d, 103),
                RodCode = BitConverter.ToInt32(d, 107),
                Trolling = d[111] == 1,
                PayloadHex = Convert.ToHexString(d)
            };
            if (c.Rod < 0 || c.Rod > 15 || c.Zero != 0) return null;
            return c;
        }
    }

    /// <summary>
    /// NEXT-BITE-IMPLEMENT-01: logs every outgoing server check and attaches the s2c 1120 response (flag 0 or 1) of the
    /// SAME rod arriving 0…1.5 s after the latest unanswered check of that rod in this run. UI-02-4-FIX (правка B): the
    /// matcher now covers ALL rod codes (float/feeder/spinning/fly), verified on captures (98–100% same-rod, dt ≤ 1.5 s);
    /// rod 0 keeps the legacy match method, other rods use MatchMethodAllRods. A missing response stays NULL (never
    /// "no bite"); a recast of the rod drops its pending check, so a delayed response cannot be attached across casts.
    /// Thread-safe.
    /// </summary>
    public sealed class ServerCheckLogger
    {
        public const double FloatResponseWindowSeconds = 1.5;
        public const string MatchMethod = "float_rodcode0_same_rod_dt<=1.5s";
        public const string MatchMethodAllRods = "same_rod_dt<=1.5s";

        private sealed class Pending { public long Id; public DateTime At; public int RodCode; public bool Answered; }

        private readonly object _sync = new();
        private readonly CatchDbWriter _db;
        private readonly Func<string> _sessionId;
        private readonly Func<int, (long CastId, string Status)> _resolveCast;
        private readonly Dictionary<int, Pending> _last = new();

        public ServerCheckLogger(CatchDbWriter db, Func<string> sessionId, Func<int, (long CastId, string Status)> resolveCast)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _resolveCast = resolveCast ?? throw new ArgumentNullException(nameof(resolveCast));
        }

        public static string SourceKeyFor(string sessionId, ServerCheck c)
        {
            string h = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(c.PayloadHex))).Substring(0, 16);
            return "chk|" + sessionId + "|r" + c.Rod.ToString(CultureInfo.InvariantCulture) + "|u" + c.RodUid.ToString(CultureInfo.InvariantCulture) +
                   "|" + c.Timestamp.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + h;
        }

        /// <summary>Stores the check (dedup by source key). Returns server_checks.id (0 = not stored).</summary>
        public long OnCheck(ServerCheck c, string? sourceKey = null)
        {
            if (c == null) return 0;
            lock (_sync)
            {
                string sid = _sessionId();
                var (castId, status) = _resolveCast(c.Rod);
                long? cast = status == CastLifecycleTracker.Matched && castId > 0 ? castId : null;
                long id = _db.SaveServerCheck(c, sid, cast, cast.HasValue ? status : (castId > 0 ? status : CastLifecycleTracker.Unmatched),
                                              sourceKey ?? SourceKeyFor(sid, c));
                if (id <= 0) return 0;
                if (_last.TryGetValue(c.Rod, out var p) && p.Id == id) return id;   // duplicate processing of the same packet
                _last[c.Rod] = new Pending { Id = id, At = c.Timestamp, RodCode = c.RodCode };
                return id;
            }
        }

        /// <summary>c2s 1110 of a rod: its pending check can no longer receive a response.</summary>
        public void OnCast(int? rod)
        {
            if (!rod.HasValue) return;
            lock (_sync) _last.Remove(rod.Value);
        }

        /// <summary>s2c 1120 response (type "wait" or "bite"). Returns the matched check id, or 0 (left unassociated).</summary>
        public long OnResponse(string type, int? rod, DateTime ts, float? shance)
        {
            if (!rod.HasValue || (type != "wait" && type != "bite")) return 0;
            lock (_sync)
            {
                if (!_last.TryGetValue(rod.Value, out var p) || p.Answered) return 0;
                double dt = (ts - p.At).TotalSeconds;
                if (dt < 0 || dt > FloatResponseWindowSeconds) return 0;
                string method = p.RodCode == 0 ? MatchMethod : MatchMethodAllRods;
                if (!_db.SetServerCheckResponse(p.Id, type, ts, type == "wait" ? shance : null, method)) return 0;
                p.Answered = true;
                return p.Id;
            }
        }
    }
}
