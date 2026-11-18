using System;
using System.Collections.Generic;

namespace FisherLogger
{
    internal sealed class CapturedPacket
    {
        public int Number { get; init; }
        public int InterfaceId { get; init; }
        public int DataOffset { get; init; }
        public byte[] Data { get; init; } = Array.Empty<byte>();
        public uint OriginalLength { get; init; }
        public DateTimeOffset? Timestamp { get; init; }
    }

    internal static class PcapNgReader
    {
        private sealed class InterfaceInfo
        {
            public double TimestampResolution { get; set; } = 1e-6;
            public long TimestampOffsetSeconds { get; set; }
        }

        public static List<CapturedPacket> Read(byte[] bytes)
        {
            var packets = new List<CapturedPacket>();
            var interfaces = new List<InterfaceInfo>();

            int pos = 0;
            bool littleEndian = true;
            bool haveSection = false;
            int packetNumber = 0;

            while (pos <= bytes.Length - 12)
            {
                uint rawType = ReadU32(bytes, pos, littleEndian);

                // Section Header Block has a byte-order-independent byte signature.
                if (bytes[pos] == 0x0A && bytes[pos + 1] == 0x0D &&
                    bytes[pos + 2] == 0x0D && bytes[pos + 3] == 0x0A)
                {
                    if (pos > bytes.Length - 28)
                        throw new FormatException("Обрезанный Section Header Block.");

                    if (bytes[pos + 8] == 0x4D && bytes[pos + 9] == 0x3C &&
                        bytes[pos + 10] == 0x2B && bytes[pos + 11] == 0x1A)
                        littleEndian = true;
                    else if (bytes[pos + 8] == 0x1A && bytes[pos + 9] == 0x2B &&
                             bytes[pos + 10] == 0x3C && bytes[pos + 11] == 0x4D)
                        littleEndian = false;
                    else
                        throw new FormatException("Неизвестный порядок байтов PCAPNG.");

                    uint sectionLength = ReadU32(bytes, pos + 4, littleEndian);
                    ValidateBlock(bytes, pos, sectionLength, littleEndian);

                    haveSection = true;
                    interfaces.Clear();
                    pos += checked((int)sectionLength);
                    continue;
                }

                if (!haveSection)
                    throw new FormatException("В начале файла отсутствует Section Header Block.");

                uint blockType = rawType;
                uint blockLength = ReadU32(bytes, pos + 4, littleEndian);
                ValidateBlock(bytes, pos, blockLength, littleEndian);
                int len = checked((int)blockLength);

                if (blockType == 1) // Interface Description Block
                {
                    if (len < 20)
                        throw new FormatException("Слишком короткий Interface Description Block.");

                    var info = new InterfaceInfo();
                    int opt = pos + 16;
                    int end = pos + len - 4;

                    while (opt + 4 <= end)
                    {
                        ushort code = ReadU16(bytes, opt, littleEndian);
                        ushort optionLength = ReadU16(bytes, opt + 2, littleEndian);
                        opt += 4;

                        if (code == 0)
                            break;

                        if (opt + optionLength > end)
                            throw new FormatException("Повреждены опции интерфейса.");

                        if (code == 9 && optionLength >= 1) // if_tsresol
                        {
                            byte r = bytes[opt];
                            info.TimestampResolution = (r & 0x80) != 0
                                ? Math.Pow(2, -(r & 0x7F))
                                : Math.Pow(10, -r);
                        }
                        else if (code == 14 && optionLength == 8) // if_tsoffset
                        {
                            info.TimestampOffsetSeconds = ReadI64(bytes, opt, littleEndian);
                        }

                        opt += (optionLength + 3) & ~3;
                    }

                    interfaces.Add(info);
                }
                else if (blockType == 6) // Enhanced Packet Block
                {
                    if (len < 32)
                        throw new FormatException("Слишком короткий Enhanced Packet Block.");

                    uint interfaceId = ReadU32(bytes, pos + 8, littleEndian);
                    uint high = ReadU32(bytes, pos + 12, littleEndian);
                    uint low = ReadU32(bytes, pos + 16, littleEndian);
                    uint capturedLength = ReadU32(bytes, pos + 20, littleEndian);
                    uint originalLength = ReadU32(bytes, pos + 24, littleEndian);

                    if (interfaceId >= interfaces.Count)
                        throw new FormatException("Пакет ссылается на неизвестный интерфейс.");

                    if (capturedLength > int.MaxValue ||
                        28L + ((capturedLength + 3L) & ~3L) > len - 4L)
                        throw new FormatException("Некорректная длина данных пакета.");

                    int dataOffset = pos + 28;
                    int cap = (int)capturedLength;
                    ulong ticks = ((ulong)high << 32) | low;
                    var info = interfaces[(int)interfaceId];

                    DateTimeOffset? timestamp = null;
                    try
                    {
                        double seconds = ticks * info.TimestampResolution +
                                         info.TimestampOffsetSeconds;
                        if (double.IsFinite(seconds) &&
                            seconds >= -62135596800d && seconds <= 253402300799d)
                            timestamp = DateTimeOffset.UnixEpoch.AddSeconds(seconds);
                    }
                    catch (ArgumentOutOfRangeException) { }

                    var packetData = new byte[cap];
                    Array.Copy(bytes, dataOffset, packetData, 0, cap);
                    packets.Add(new CapturedPacket
                    {
                        Number = ++packetNumber,
                        InterfaceId = (int)interfaceId,
                        DataOffset = dataOffset,
                        Data = packetData,
                        OriginalLength = originalLength,
                        Timestamp = timestamp
                    });
                }
                else if (blockType == 3) // Simple Packet Block
                {
                    if (len < 16)
                        throw new FormatException("Слишком короткий Simple Packet Block.");

                    if (interfaces.Count == 0)
                        throw new FormatException("Simple Packet Block без интерфейса.");

                    uint originalLength = ReadU32(bytes, pos + 8, littleEndian);
                    int available = len - 16;
                    int cap = (int)Math.Min(originalLength, (uint)available);
                    int dataOffset = pos + 12;
                    var packetData = new byte[cap];
                    Array.Copy(bytes, dataOffset, packetData, 0, cap);

                    packets.Add(new CapturedPacket
                    {
                        Number = ++packetNumber,
                        InterfaceId = 0,
                        DataOffset = dataOffset,
                        Data = packetData,
                        OriginalLength = originalLength,
                        Timestamp = null
                    });
                }

                pos += len;
            }

            if (!haveSection)
                throw new FormatException("Файл не распознан как PCAPNG.");

            if (pos != bytes.Length)
                throw new FormatException("В конце файла остались неполные данные.");

            return packets;
        }

        private static void ValidateBlock(byte[] bytes, int pos, uint length, bool littleEndian)
        {
            if (length < 12 || (length & 3) != 0 || length > int.MaxValue ||
                (long)pos + length > bytes.Length)
                throw new FormatException("Некорректная длина блока PCAPNG.");

            uint trailingLength = ReadU32(bytes, pos + (int)length - 4, littleEndian);
            if (trailingLength != length)
                throw new FormatException("Длина блока PCAPNG не совпадает с завершающим полем.");
        }

        private static ushort ReadU16(byte[] b, int p, bool le) =>
            le
                ? (ushort)(b[p] | (b[p + 1] << 8))
                : (ushort)((b[p] << 8) | b[p + 1]);

        private static uint ReadU32(byte[] b, int p, bool le) =>
            le
                ? (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24))
                : (uint)((b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]);

        private static long ReadI64(byte[] b, int p, bool le)
        {
            ulong value = le
                ? ((ulong)ReadU32(b, p + 4, true) << 32) | ReadU32(b, p, true)
                : ((ulong)ReadU32(b, p, false) << 32) | ReadU32(b, p + 4, false);
            return unchecked((long)value);
        }
    }
}
