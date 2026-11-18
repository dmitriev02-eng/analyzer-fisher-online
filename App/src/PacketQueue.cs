using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace FisherLogger
{
    public readonly struct PacketItem
    {
        public PacketItem(byte[] data, bool fromServer, DateTime timestamp, bool isUdp = false, long connectionEpoch = 0, TcpSegment? segment = null)
        {
            Segment = segment;
            Data = data;
            FromServer = fromServer;
            Timestamp = timestamp;
            IsUdp = isUdp;
            ConnectionEpoch = connectionEpoch;
        }

        public byte[] Data { get; }
        public bool FromServer { get; }
        public DateTime Timestamp { get; }
        public bool IsUdp { get; }
        /// <summary>NEXT-ANALYTICS-14: номер игрового соединения (GameConnectionTracker), 0 = неизвестен.</summary>
        public long ConnectionEpoch { get; }
        /// <summary>NEXT-BAIT-05: TCP-сегмент с seq (только сервер → клиент) для канала инвентаря; null — нет.</summary>
        public TcpSegment? Segment { get; }
    }

    /// <summary>
    /// Producer-consumer очередь между SharpPcap-потоком и потоком разбора.
    /// Захват не должен ждать парсер и SQLite.
    /// </summary>
    public sealed class PacketQueue : IDisposable
    {
        private readonly ConcurrentQueue<PacketItem> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);
        private const int MaxQueued = 50_000;
        private long _dropped;
        private bool _disposed;

        public int Count => _queue.Count;
        public long Dropped => Interlocked.Read(ref _dropped);

        public bool Enqueue(byte[] data, bool fromServer, DateTime timestamp, bool isUdp = false, long connectionEpoch = 0, TcpSegment? segment = null)
        {
            if (_disposed || data == null || data.Length == 0) return false;
            if (_queue.Count >= MaxQueued)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }

            _queue.Enqueue(new PacketItem(data, fromServer, timestamp, isUdp, connectionEpoch, segment));
            _signal.Release();
            return true;
        }

        public PacketItem? Take(CancellationToken ct)
        {
            try
            {
                _signal.Wait(ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            return _queue.TryDequeue(out PacketItem item) ? item : null;
        }

        public async Task<PacketItem?> DequeueAsync(CancellationToken ct)
        {
            try
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            return _queue.TryDequeue(out PacketItem item) ? item : null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _signal.Dispose();
        }
    }
}
