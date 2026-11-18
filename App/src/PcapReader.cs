using System;
using System.Collections.Generic;
using System.IO;

namespace FisherLogger
{
    internal static class PcapReader
    {
        public static List<CapturedPacket> Read(byte[] bytes)
        {
            if (bytes == null)
                throw new ArgumentNullException(nameof(bytes));

            if (bytes.Length < 24)
                throw new FormatException("Файл PCAP короче глобального заголовка.");

            bool littleEndian;
            bool nanosecondResolution;

            if (bytes[0] == 0xD4 && bytes[1] == 0xC3 &&
                bytes[2] == 0xB2 && bytes[3] == 0xA1)
            {
                littleEndian = true;
                nanosecondResolution = false;
            }
            else if (bytes[0] == 0xA1 && bytes[1] == 0xB2 &&
                     bytes[2] == 0xC3 && bytes[3] == 0xD4)
            {
                littleEndian = false;
                nanosecondResolution = false;
            }
            else if (bytes[0] == 0x4D && bytes[1] == 0x3C &&
                     bytes[2] == 0xB2 && bytes[3] == 0xA1)
            {
                littleEndian = true;
                nanosecondResolution = true;
            }
            else if (bytes[0] == 0xA1 && bytes[1] == 0xB2 &&
                     bytes[2] == 0x3C && bytes[3] == 0x4D)
            {
                littleEndian = false;
                nanosecondResolution = true;
            }
            else
            {
                throw new FormatException("Неизвестная сигнатура классического PCAP.");
            }

            ushort majorVersion = ReadU16(bytes, 4, littleEndian);
            ushort minorVersion = ReadU16(bytes, 6, littleEndian);

            if (majorVersion != 2 || minorVersion != 4)
                throw new FormatException(
                    $"Неподдерживаемая версия PCAP: {majorVersion}.{minorVersion}.");

            uint snapLength = ReadU32(bytes, 16, littleEndian);
            uint linkType = ReadU32(bytes, 20, littleEndian);

            if (snapLength == 0)
                throw new FormatException("В PCAP указан нулевой snap length.");

            var packets = new List<CapturedPacket>();
            int pos = 24;

            while (pos < bytes.Length)
            {
                if (bytes.Length - pos < 16)
                    throw new FormatException(
                        $"Обрезанный заголовок пакета PCAP на смещении 0x{pos:X}.");

                uint seconds = ReadU32(bytes, pos, littleEndian);
                uint fraction = ReadU32(bytes, pos + 4, littleEndian);
                uint capturedLength = ReadU32(bytes, pos + 8, littleEndian);
                uint originalLength = ReadU32(bytes, pos + 12, littleEndian);

                if (capturedLength > int.MaxValue)
                    throw new FormatException("Размер захваченного пакета слишком велик.");

                if (capturedLength > snapLength)
                    throw new FormatException(
                        "Размер пакета превышает snap length из заголовка PCAP.");

                if (capturedLength > (uint)(bytes.Length - pos - 16))
                    throw new FormatException(
                        $"Данные пакета выходят за границы файла на смещении 0x{pos:X}.");

                if (nanosecondResolution)
                {
                    if (fraction >= 1_000_000_000)
                        throw new FormatException(
                            "Некорректная наносекундная метка времени PCAP.");
                }
                else if (fraction >= 1_000_000)
                {
                    throw new FormatException(
                        "Некорректная микросекундная метка времени PCAP.");
                }

                DateTimeOffset? timestamp = null;

                try
                {
                    long fractionalTicks = nanosecondResolution
                        ? fraction / 100
                        : (long)fraction * 10;

                    timestamp = DateTimeOffset.UnixEpoch
                        .AddSeconds(seconds)
                        .AddTicks(fractionalTicks);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Повреждённая или неподдерживаемая дата:
                    // сохраняем пакет, но оставляем время неизвестным.
                }

                int dataOffset = pos + 16;
                int dataLength = checked((int)capturedLength);
                var packetData = new byte[dataLength];

                Array.Copy(bytes, dataOffset, packetData, 0, dataLength);

                packets.Add(new CapturedPacket
                {
                    Number = packets.Count + 1,
                    InterfaceId = 0,
                    DataOffset = dataOffset,
                    Data = packetData,
                    OriginalLength = originalLength,
                    Timestamp = timestamp
                });

                pos = checked(dataOffset + dataLength);
            }

            return packets;
        }

        private static ushort ReadU16(byte[] bytes, int offset, bool littleEndian)
        {
            if (littleEndian)
                return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

            return (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
        }

        private static uint ReadU32(byte[] bytes, int offset, bool littleEndian)
        {
            if (littleEndian)
            {
                return (uint)(
                    bytes[offset] |
                    (bytes[offset + 1] << 8) |
                    (bytes[offset + 2] << 16) |
                    (bytes[offset + 3] << 24));
            }

            return ((uint)bytes[offset] << 24) |
                   ((uint)bytes[offset + 1] << 16) |
                   ((uint)bytes[offset + 2] << 8) |
                   bytes[offset + 3];
        }
    }
}
