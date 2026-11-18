using System;
using System.Collections.Generic;
using System.Linq;

namespace FisherLogger
{
    /// <summary>
    /// NEXT-93: connection-aware, sequence-aware TCP stream reassembly.
    /// Replaces the legacy TcpReassembler for correctness: retransmission drop,
    /// overlap trim, out-of-order buffering, gap detection, frame emission with seq.
    /// </summary>
    public sealed class SeqTcpReassembler
    {
        public const int MaxFrameLength = 1_000_000; // bounded (largest observed ~27.7 KB inventory)
        private const int MaxPendingBytes = 1_000_000;

        public sealed class FrameChunk
        {
            public byte[] Frame = Array.Empty<byte>();
            public string StreamKey = "";
            public bool FromServer;
            public uint SeqOfFirstByte;
            public long StreamOffset;
        }

        public sealed class CoverageGapEvent
        {
            public string StreamKey = "";
            public bool FromServer;
            public uint FromSeq;
            public uint ToSeq;
            public DateTime Timestamp;
            public override string ToString() => $"CoverageGap({StreamKey}, {(FromServer ? "S2C" : "C2S")}, seq {FromSeq}..{ToSeq})";
        }

        public event Action<CoverageGapEvent>? GapDetected;

        private sealed class StreamState
        {
            public uint NextSeq;
            public bool HasSeq;
            public List<byte> Buffer = new();
            public SortedDictionary<uint, byte[]> Pending = new();
        }

        private readonly Dictionary<string, StreamState> _streams = new();

        public List<FrameChunk> Feed(TcpSegment seg)
        {
            var result = new List<FrameChunk>();
            if (seg == null || seg.Payload.Length == 0) return result;

            string key = seg.ConnKey + (seg.FromServer ? "|S" : "|C");
            if (!_streams.TryGetValue(key, out var st))
            {
                st = new StreamState();
                _streams[key] = st;
            }

            // SYN/RST/FIN -> lifecycle boundary
            if (seg.IsSyn || seg.IsRst)
            {
                if (st.Buffer.Count > 0 && st.HasSeq)
                    EmitFrames(st, key, seg.FromServer, result);
                st.Buffer.Clear();
                st.Pending.Clear();
                st.HasSeq = false;
                if (seg.IsSyn) { st.NextSeq = seg.Seq + 1; st.HasSeq = true; }
                // SYN carries no payload; return
                return result;
            }

            uint len = (uint)seg.Payload.Length;
            if (!st.HasSeq)
            {
                st.NextSeq = seg.Seq;
                st.HasSeq = true;
            }

            if (seg.Seq == st.NextSeq)
            {
                st.Buffer.AddRange(seg.Payload);
                st.NextSeq += len;
                DrainPending(st);
            }
            else if (seg.Seq + len <= st.NextSeq)
            {
                // exact retransmission or already received -> drop
            }
            else if (seg.Seq < st.NextSeq && seg.Seq + len > st.NextSeq)
            {
                int skip = (int)(st.NextSeq - seg.Seq);
                st.Buffer.AddRange(seg.Payload.Skip(skip));
                st.NextSeq += len - (uint)skip;
                DrainPending(st);
            }
            else // seg.Seq > st.NextSeq (out of order)
            {
                st.Pending[seg.Seq] = seg.Payload;
                if (st.Pending.Sum(p => (long)p.Value.Length) > MaxPendingBytes)
                {
                    uint from = st.NextSeq;
                    uint to = st.Pending.Keys.First();
                    st.Buffer.Clear();
                    st.Pending.Clear();
                    st.HasSeq = false;
                    st.NextSeq = 0;
                    GapDetected?.Invoke(new CoverageGapEvent { StreamKey = seg.ConnKey, FromServer = seg.FromServer, FromSeq = from, ToSeq = to, Timestamp = seg.Timestamp });
                }
            }

            EmitFrames(st, key, seg.FromServer, result);
            return result;
        }

        private void DrainPending(StreamState st)
        {
            while (st.Pending.TryGetValue(st.NextSeq, out var bytes))
            {
                st.Pending.Remove(st.NextSeq);
                st.Buffer.AddRange(bytes);
                st.NextSeq += (uint)bytes.Length;
            }
        }

        private void EmitFrames(StreamState st, string key, bool fromServer, List<FrameChunk> result)
        {
            long baseOffset = 0;
            while (true)
            {
                int idx = FindMagic(st.Buffer, 0);
                if (idx < 0) { break; }
                if (idx > 0) { st.Buffer.RemoveRange(0, idx); baseOffset += idx; }
                if (st.Buffer.Count < 8) break;
                if (st.Buffer[7] != 0xF3) { st.Buffer.RemoveAt(0); baseOffset++; continue; }
                uint flen = ((uint)st.Buffer[1] << 24) | ((uint)st.Buffer[2] << 16) | ((uint)st.Buffer[3] << 8) | st.Buffer[4];
                if (flen < 20 || flen > MaxFrameLength) { st.Buffer.RemoveAt(0); baseOffset++; continue; }
                if (st.Buffer.Count < (int)flen) break;
                var f = new byte[(int)flen];
                st.Buffer.CopyTo(0, f, 0, (int)flen);
                st.Buffer.RemoveRange(0, (int)flen);
                uint seqOfFirst = st.HasSeq ? (uint)(st.NextSeq - (uint)(st.Buffer.Count + (int)flen)) : 0;
                result.Add(new FrameChunk { Frame = f, StreamKey = key, FromServer = fromServer, SeqOfFirstByte = seqOfFirst, StreamOffset = baseOffset });
                baseOffset += (int)flen;
            }
            if (st.Buffer.Count > MaxFrameLength * 2) st.Buffer.Clear();
        }

        private static int FindMagic(List<byte> d, int start)
        {
            for (int i = start; i < d.Count; i++)
                if (d[i] == 0xFB) return i;
            return -1;
        }
    }
}
