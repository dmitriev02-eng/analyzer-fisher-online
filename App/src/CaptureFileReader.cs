using System;
using System.Collections.Generic;

namespace FisherLogger
{
    internal static class CaptureFileReader
    {
        public static List<CapturedPacket> Read(byte[] bytes)
        {
            if (bytes == null)
                throw new ArgumentNullException(nameof(bytes));

            if (bytes.Length < 4)
                throw new FormatException("Файл слишком короткий для определения формата.");

            // PCAPNG: Section Header Block type.
            if (bytes[0] == 0x0A && bytes[1] == 0x0D &&
                bytes[2] == 0x0D && bytes[3] == 0x0A)
            {
                return PcapNgReader.Read(bytes);
            }

            // Иначе проверяем классический PCAP.
            return PcapReader.Read(bytes);
        }
    }
}
