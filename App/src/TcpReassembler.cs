using System;
using System.Collections.Generic;

namespace FisherLogger
{
    /// <summary>
    /// Собирает TCP-payload в целые кадры FB 00 00 00 ... .
    /// Нужен потому, что FULL_CATCH (209/218 байт) может прийти несколькими
    /// TCP-сегментами, а PacketDeepParser.ParseFrames разбирает только то,
    /// что целиком лежит в одном payload.
    /// </summary>
    public sealed class TcpReassembler
    {
        private static readonly byte[] Magic = { 0xFB };  // NEXT-89 R1: 1-byte magic + u32 BE length @1-4
        private const int MinFrameLength = 20;
        private const int MaxFrameLength = 10_000;
        private const int MaxBufferLength = 100_000;

        private readonly List<byte> _buffer = new();

        public int BufferedBytes => _buffer.Count;

        public void Clear() => _buffer.Clear();

        /// <summary>
        /// Добавляет payload и возвращает все полностью собранные кадры.
        /// </summary>
        public List<byte[]> Feed(byte[] payload)
        {
            var frames = new List<byte[]>();
            if (payload == null || payload.Length == 0)
                return frames;

            _buffer.AddRange(payload);

            while (true)
            {
                int marker = IndexOfMagic(_buffer, 0);
                if (marker < 0)
                {
                    // Магия могла разрезаться между сегментами: держим хвост.
                    if (_buffer.Count > 7)
                        _buffer.RemoveRange(0, _buffer.Count - 7);
                    break;
                }

                if (marker > 0)
                    _buffer.RemoveRange(0, marker);

                if (_buffer.Count < 8)
                    break;

                if (_buffer[7] != 0xF3) { _buffer.RemoveAt(0); continue; } // NEXT-89 R1: header check
                uint frameLength = ((uint)_buffer[1] << 24) | ((uint)_buffer[2] << 16) | ((uint)_buffer[3] << 8) | _buffer[4];
                if (frameLength < MinFrameLength || frameLength > MaxFrameLength)
                {
                    _buffer.RemoveAt(0);
                    continue;
                }

                if (_buffer.Count < (int)frameLength)
                    break; // кадр ещё не пришёл целиком

                var frame = new byte[(int)frameLength];
                _buffer.CopyTo(0, frame, 0, (int)frameLength);
                _buffer.RemoveRange(0, (int)frameLength);
                frames.Add(frame);
            }

            if (_buffer.Count > MaxBufferLength)
                _buffer.Clear();

            return frames;
        }

        private static int IndexOfMagic(List<byte> data, int start)
        {
            for (int i = start; i < data.Count; i++)
                if (data[i] == Magic[0]) return i;
            return -1;
        }
    }
}
