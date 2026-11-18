using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace FisherLogger
{
    /// <summary>
    /// NEXT-104: минимальный PCAPNG-писатель (SHB + IDB + EPB).
    /// Маленький байт-порядок (little-endian), разрешение метки времени 1 мкс.
    /// </summary>
    public sealed class PcapNgWriter : IDisposable
    {
        private readonly FileStream _fs;
        private bool _closed;
        private bool _failed;

        public bool IsComplete => !_failed && _closed;
        public string Path { get; }

        /// <summary>Size of an empty capture (SHB 28 B + IDB 20 B): such a file holds no packets.</summary>
        public const long HeaderOnlyBytes = 48;

        /// <summary>
        /// FIX-03: the writer opens its file with FileMode.Create, so every launch used to erase the previous run's capture.
        /// Before a new capture is opened, a previous file that holds packets is moved to "captures\capture_&lt;last write
        /// time&gt;.pcapng" next to it (never overwritten: a numeric suffix is added if the name is taken). A header-only file
        /// (no packets) is left in place and overwritten. Returns the archive path, or null if nothing was moved.
        /// Throws only if the move itself fails (the caller then must not open the writer over the old file).
        /// </summary>
        public static string? ArchivePrevious(string path)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length <= HeaderOnlyBytes) return null;
            string dir = System.IO.Path.Combine(fi.DirectoryName ?? ".", "captures");
            Directory.CreateDirectory(dir);
            string stem = "capture_" + fi.LastWriteTime.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string target = System.IO.Path.Combine(dir, stem + ".pcapng");
            for (int i = 1; File.Exists(target); i++) target = System.IO.Path.Combine(dir, stem + "_" + i + ".pcapng");
            File.Move(path, target);
            return target;
        }

        // AUDIT-04 (C1): фоновая запись — поток захвата только кладёт пакет в очередь (диск не задерживает приём пакетов)
        private readonly BlockingCollection<(byte[] data, uint len, ulong ts)>? _queue;
        private readonly Thread? _thread;
        public const int QueueLimit = 50000;
        public long Dropped;
        /// <summary>Ошибка фоновой записи (вызывается один раз из потока записи).</summary>
        public Action<string>? OnError;
        public bool Background => _queue != null;

        public PcapNgWriter(string path, ushort linkType = 1 /* Ethernet */, bool background = false)
            : this(path, linkType)
        {
            if (!background) return;
            _queue = new BlockingCollection<(byte[], uint, ulong)>(QueueLimit);
            _thread = new Thread(WriterLoop) { IsBackground = true, Name = "pcapng-writer", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        private void WriterLoop()
        {
            try
            {
                foreach (var (data, len, ts) in _queue!.GetConsumingEnumerable())
                {
                    WriteDirect(data, len, ts);
                    if (_queue.Count == 0) _fs.Flush();
                }
            }
            catch (Exception ex) { _failed = true; try { OnError?.Invoke(ex.Message); } catch { } }
        }

        public PcapNgWriter(string path, ushort linkType)
        {
            Path = path;
            _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);

            // SHB: 28 байт (без опций)
            WriteU32(0x0A0D0D0A);
            WriteU32(28);
            WriteU32(0x1A2B3C4D);      // byte-order magic (little-endian)
            WriteU16(1);                // major
            WriteU16(0);                // minor
            WriteU64(0xFFFFFFFFFFFFFFFFL); // section length = -1
            WriteU32(28);               // trailing length

            // IDB: 20 байт
            WriteU32(0x00000001);
            WriteU32(20);
            WriteU16(linkType);
            WriteU16(0);                // reserved
            WriteU32(65535);            // snaplen
            WriteU32(20);               // trailing length

            _fs.Flush();
        }

        /// <summary>Записать один пакет (Enhanced Packet Block). В фоновом режиме — только в очередь (копия данных).</summary>
        public void WritePacket(byte[] packetBytes, uint originalLen, ulong timestampMicroseconds)
        {
            if (_failed || _closed) return;
            if (_queue != null)
            {
                if (_queue.IsAddingCompleted) return;
                if (!_queue.TryAdd(((byte[])packetBytes.Clone(), originalLen, timestampMicroseconds))) Interlocked.Increment(ref Dropped);
                return;
            }
            WriteDirect(packetBytes, originalLen, timestampMicroseconds);
        }

        private void WriteDirect(byte[] packetBytes, uint originalLen, ulong timestampMicroseconds)
        {
            try
            {
                uint capturedLen = (uint)Math.Min(packetBytes.Length, int.MaxValue);
                uint paddedLen = (capturedLen + 3u) & ~3u; // паддинг до 4 байт
                uint blockLen = 32u + paddedLen;

                WriteU32(0x00000006);          // EPB
                WriteU32(blockLen);
                WriteU32(0);                   // interface id
                WriteU32((uint)(timestampMicroseconds >> 32)); // ts high
                WriteU32((uint)(timestampMicroseconds & 0xFFFFFFFF)); // ts low
                WriteU32(capturedLen);
                WriteU32(originalLen);
                WriteBytes(packetBytes, (int)capturedLen);
                for (uint i = capturedLen; i < paddedLen; i++)
                    WriteByte(0);              // padding
                WriteU32(blockLen);            // trailing length
            }
            catch (Exception)
            {
                _failed = true;
                throw;
            }
        }

        public void Flush()
        {
            if (_closed) return;
            if (_queue != null) { var until = DateTime.UtcNow.AddSeconds(3); while (_queue.Count > 0 && DateTime.UtcNow < until && !_failed) Thread.Sleep(5); }
            try { _fs.Flush(); }
            catch (Exception) { _failed = true; throw; }
        }

        public void Dispose()
        {
            if (_closed) return;
            try
            {
                if (_queue != null) { _queue.CompleteAdding(); _thread?.Join(5000); }
                Flush();
                _fs.Dispose();
                _closed = true;
            }
            catch (Exception) { _failed = true; }
        }

        private void WriteU16(ushort v) { _fs.WriteByte((byte)(v & 0xFF)); _fs.WriteByte((byte)(v >> 8)); }
        private void WriteU32(uint v) { _fs.WriteByte((byte)(v & 0xFF)); _fs.WriteByte((byte)((v >> 8) & 0xFF)); _fs.WriteByte((byte)((v >> 16) & 0xFF)); _fs.WriteByte((byte)(v >> 24)); }
        private void WriteU64(ulong v) { WriteU32((uint)(v & 0xFFFFFFFF)); WriteU32((uint)(v >> 32)); }
        private void WriteByte(byte b) => _fs.WriteByte(b);
        private void WriteBytes(byte[] b, int len) => _fs.Write(b, 0, len);
    }
}
