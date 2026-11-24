using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FisherLogger;

// NEXT-95 replay harness. Based on NEXT-93 ReplayProgram.cs (E530E475…): same extraction
// (TcpPayloadExtractor.ExtractSegment), same per-file SeqTcpReassembler, same parser and
// same source_key format (fileHash|conn|dir|seqOfFirstByte).
// NEXT-95 changes (harness only):
//  * one capture session (= one folder under captures) = one deterministic session_id "capture:<folder>" and its own rod->cast map;
//  * coverage: a seq discontinuity between consecutive files of a session, or a reassembler GapDetected,
//    marks all casts open at that moment as uncertain -> their later events/catch are AMBIGUOUS (not MATCHED);
//  * catches without an open cast are stored as UNMATCHED (previously silently skipped);
//  * CATCH_SUMMARY is also persisted to `catches` via DeepCatchSession.ToCatchData + SaveCatch,
//    with deterministic record_id = "next95|" + source_key (idempotent re-import);
//  * cast outcome on close: caught / ambiguous / no_catch / unknown;
//  * optional JSON summary output (args[3]).
// NEXT-ANALYTICS-04 changes: cast lifecycle via the shared CastLifecycleTracker (same semantics as the live app):
//  * s2c 1120 flag=1 bites are stored as 'bite' events; event source_key = frame key + "|" + type + "|r" + rod
//    (several events of one frame no longer collide on the frame key);
//  * outcomes caught / no_bite / unknown / pending (no_catch and ambiguous are no longer written);
//  * a MATCHED catch summary sets catches.cast_id; AMBIGUOUS/UNMATCHED leave it NULL;
//  * refuses any database under the production session or application folders.
// Usage: Replay95 <dbPath> <capturesRoot> [fresh|append] [summary.json]
class Program
{
    static string? _sk;

    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: Replay95 <dbPath> <capturesRoot> [fresh|append] [summary.json]"); return 2; }
        string dbPath = Path.GetFullPath(args[0]);
        string capturesRoot = args[1];
        bool append = args.Length > 2 && args[2] == "append";
        string? summaryPath = args.Length > 3 ? args[3] : null;
        if (IsProtectedPath(dbPath)) { Console.WriteLine("REFUSED: production path " + dbPath); return 3; }
        if (File.Exists(dbPath) && !append) File.Delete(dbPath);

        var db = new CatchDbWriter(dbPath);
        var parser = new PacketDeepParser();
        Console.WriteLine("DB path: " + db.DbPath);
        Console.WriteLine("Parser data dir: " + parser.DataDirectory);

        var counts = new SortedDictionary<string, int>();
        void Inc(string k) => counts[k] = counts.GetValueOrDefault(k) + 1;
        string curSession = "";
        string sid = ""; // deterministic session_id = capture folder name (stable across re-imports)
        var attempts = new CastLifecycleTracker(db, () => sid, "replay");

        void MarkOpenUncertain(string why)
        {
            int open = attempts.OpenCount;
            attempts.MarkAllOpenUncertain();
            Inc("coverage_gap");
            Console.WriteLine("  COVERAGE GAP [" + curSession + "] " + why + " -> open casts marked uncertain: " + open);
        }

        string Key(string type, int? rod) => _sk + "|" + type + "|r" + (rod.HasValue ? rod.Value.ToString() : "?");

        void RodEvent(string type, PacketDeepParser.DeepCatchData ev, bool isClient)
        {
            Inc(type);
            var (_, st) = attempts.Resolve(ev.RodIndex);
            Inc(type + "_" + st);
            attempts.OnRodEvent(type, ev.RodIndex, ev.Timestamp, isClient, ev.FishLangId, ev.Weight, ev.Windex, type == "wait" ? ev.Shance : null, ev.RawText ?? "", Key(type, ev.RodIndex));
        }

        parser.CastDetected += ev =>
        {
            Inc("cast");
            long id = attempts.OnCast(ev.RodIndex, ev.Timestamp, ev.CastX, ev.CastY, ev.CastZ, ev.BaitUid, _sk);
            if (id <= 0) Inc("cast_id_zero");
        };
        parser.HookDetected += ev => RodEvent("hook", ev, true);
        parser.EmptyBiteDetected += ev => RodEvent("empty_bite", ev, false);
        parser.WaitDetected += ev => RodEvent("wait", ev, false);
        parser.WeatherDetected += ev =>
        {
            Inc("weather");
            db.SaveWeather(new WeatherRecord { Timestamp = ev.Timestamp, SessionId = sid, BaseId = ev.BaseId, Cloudly = ev.Cloudly, Fog = ev.Fog, Rain = ev.Rain, Tempr = ev.AirTemperatureC, Grom = ev.Grom, UnitsStatus = "UNVERIFIED", SourceKey = _sk });
        };

        var files = Directory.GetFiles(capturesRoot, "*.pcapng", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var sessions = files.GroupBy(f => Path.GetFileName(Path.GetDirectoryName(f)) ?? "").OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        Console.WriteLine("PCAPNG files: " + files.Count + ", sessions: " + sessions.Count + (append ? " (append mode)" : " (fresh)"));

        foreach (var session in sessions)
        {
            curSession = session.Key;
            sid = "capture:" + session.Key;
            db.StartNewSession();
            attempts.ResetInMemory();
            DateTime lastTs = DateTime.MinValue; // last packet time of this capture session
            var lastEnd = new Dictionary<string, uint>();   // stream (conn|dir) -> seq after last payload byte of previous file
            Console.WriteLine("SESSION " + curSession + " session_id=" + sid + " files=" + session.Count());

            foreach (var f in session.OrderBy(x => x, StringComparer.Ordinal))
            {
                try
                {
                    string fileHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))).Substring(0, 16);
                    var packets = CaptureFileReader.Read(File.ReadAllBytes(f));
                    var server = new SeqTcpReassembler();
                    var client = new SeqTcpReassembler();
                    server.GapDetected += g => MarkOpenUncertain(g.ToString());
                    client.GapDetected += g => MarkOpenUncertain(g.ToString());
                    var firstSeen = new HashSet<string>();
                    var fileEnd = new Dictionary<string, uint>();
                    foreach (var packet in packets)
                    {
                        DateTime ts = packet.Timestamp.HasValue ? packet.Timestamp.Value.DateTime : DateTime.MinValue;
                        if (ts > lastTs) lastTs = ts;
                        var seg = TcpPayloadExtractor.ExtractSegment(packet.Data, ts, fileHash);
                        if (seg == null || seg.Payload.Length == 0) continue;
                        string stream = seg.ConnKey + (seg.FromServer ? "|S" : "|C");
                        if (firstSeen.Add(stream) && lastEnd.TryGetValue(stream, out uint expected) && expected != seg.Seq)
                            MarkOpenUncertain($"file boundary {Path.GetFileName(f)} {stream} expected seq {expected} got {seg.Seq} ({unchecked((int)(seg.Seq - expected))} bytes)");
                        uint end = unchecked(seg.Seq + (uint)seg.Payload.Length);
                        if (!fileEnd.TryGetValue(stream, out uint cur) || unchecked((int)(end - cur)) > 0) fileEnd[stream] = end;

                        var r = seg.FromServer ? server : client;
                        foreach (var fc in r.Feed(seg))
                        {
                            _sk = $"{fileHash}|{seg.ConnKey}|{(seg.FromServer ? "S" : "C")}|{fc.SeqOfFirstByte}";
                            foreach (var ev in parser.ParseFrames(fc.Frame, ts))
                            {
                                Inc("msg_" + ev.MessageType);
                                if (ev.MessageType == "EARLY_BITE_1120") { RodEvent("bite", ev, false); continue; }
                                if (ev.MessageType != "CATCH_SUMMARY") continue;
                                var (cid, st) = attempts.Resolve(ev.RodIndex);
                                Inc("catch_" + st);
                                if (cid > 0)
                                    attempts.OnCatch(cid, ev.Timestamp, ev.FishLangId, ev.Weight, Key("catch", ev.RodIndex), ambiguous: st != CastLifecycleTracker.Matched);
                                else
                                    db.SaveCastEvent(new CastEvent { CastId = 0, Timestamp = ev.Timestamp, EventType = "catch", FishLangId = ev.FishLangId, Weight = ev.Weight, MatchStatus = st, SourceKey = Key("catch", ev.RodIndex) });
                                var cd = DeepCatchSession.ToCatchData(ev);
                                if (cid > 0 && st == CastLifecycleTracker.Matched) cd.cast_id = cid; // NEXT-ANALYTICS-04
                                cd.record_id = "next95|" + _sk;
                                cd.status = "confirmed";
                                cd.source = "replay95";
                                db.SaveCatch(cd, sid);
                            }
                        }
                    }
                    foreach (var kv in fileEnd) lastEnd[kv.Key] = kv.Value;
                }
                catch (Exception ex) { Inc("file_error"); Console.WriteLine("ERR " + f + ": " + ex); }
            }
            attempts.CloseAll(lastTs == DateTime.MinValue ? DateTime.Now : lastTs, "end of captured coverage"); // outcome cannot be known
        }

        Console.WriteLine("=== counts ===");
        foreach (var kv in counts) Console.WriteLine("  " + kv.Key + ": " + kv.Value);
        if (summaryPath != null)
        {
            var sb = new StringBuilder("{\n");
            sb.Append("  \"db\": \"" + dbPath.Replace("\\", "\\\\") + "\",\n  \"mode\": \"" + (append ? "append" : "fresh") + "\",\n  \"counts\": {\n");
            sb.Append(string.Join(",\n", counts.Select(kv => "    \"" + kv.Key + "\": " + kv.Value)));
            sb.Append("\n  }\n}\n");
            File.WriteAllText(summaryPath, sb.ToString(), new UTF8Encoding(false));
        }
        db.Dispose();
        return counts.GetValueOrDefault("file_error") == 0 ? 0 : 1;
    }

    /// <summary>NEXT-ANALYTICS-04: never write into production data (legacy root, sessions, application folder).</summary>
    static bool IsProtectedPath(string fullPath)
    {
        string p = Path.GetFullPath(fullPath);
        string[] roots =
        {
            @"D:\FisherLogger",
            @"D:\TheFisherProject\Sessions",
            @"D:\TheFisherProject\App",
            @"D:\TheFisherProject\Data",
            @"D:\TheFisherProject\Research",
            @"D:\TheFisherProject\Backups"
        };
        foreach (var r in roots)
            if (p.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            if (string.Equals(p, Path.GetFullPath(AppPaths.CatchDb), StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }
}
