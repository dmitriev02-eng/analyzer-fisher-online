using System;

namespace FisherLogger
{
    /// <summary>
    /// Извлекает TCP-payload из кадра Ethernet (включая VLAN-теги) для импорта
    /// PCAP/PCAPNG. Если кадр не Ethernet/IPv4/IPv6 + TCP, возвращает null.
    /// Ничего не изменяет и не отправляет — только чтение.
    /// </summary>
    internal static class TcpPayloadExtractor
    {
        public const int FisherPort = 9205;

        public static byte[]? Extract(byte[]? frame)
            => Extract(frame, out _, out _);

        public static byte[]? Extract(byte[]? frame, out int srcPort, out int dstPort)
        {
            srcPort = 0;
            dstPort = 0;
            if (frame == null || frame.Length < 14) return null;

            int l3 = 14;
            if (!TryU16(frame, 12, out ushort etherType)) return null;

            // VLAN / QinQ
            int guard = 0;
            while ((etherType == 0x8100 || etherType == 0x88A8 || etherType == 0x9100) && guard++ < 3)
            {
                if (frame.Length < l3 + 4) return null;
                if (!TryU16(frame, l3 + 2, out etherType)) return null;
                l3 += 4;
            }

            if (etherType == 0x0800) return ExtractIpv4(frame, l3, out srcPort, out dstPort);
            if (etherType == 0x86DD) return ExtractIpv6(frame, l3, out srcPort, out dstPort);
            return null;
        }

        private static byte[]? ExtractIpv4(byte[] frame, int l3, out int srcPort, out int dstPort)
        {
            srcPort = 0;
            dstPort = 0;
            if (frame.Length < l3 + 20) return null;
            int ihl = (frame[l3] & 0x0F) * 4;
            if (ihl < 20 || frame.Length < l3 + ihl) return null;
            if (frame[l3 + 9] != 6) return null; // TCP

            int totalLength = ReadU16(frame, l3 + 2);
            return ExtractTcp(frame, l3 + ihl, l3, totalLength, ihl, 0, out srcPort, out dstPort);
        }

        private static byte[]? ExtractIpv6(byte[] frame, int l3, out int srcPort, out int dstPort)
        {
            srcPort = 0;
            dstPort = 0;
            if (frame.Length < l3 + 40) return null;
            if (frame[l3 + 6] != 6) return null; // next header = TCP

            int payloadLength = ReadU16(frame, l3 + 4);
            return ExtractTcp(frame, l3 + 40, l3, payloadLength + 40, 40, 0, out srcPort, out dstPort);
        }

        private static byte[]? ExtractTcp(byte[] frame, int tcp, int l3, int ipTotalLength, int ipHeaderLength, int reserved,
                                          out int srcPort, out int dstPort)
        {
            srcPort = 0;
            dstPort = 0;
            if (frame.Length < tcp + 20) return null;

            srcPort = ReadU16(frame, tcp);
            dstPort = ReadU16(frame, tcp + 2);
            if (srcPort != FisherPort && dstPort != FisherPort) return null;

            int tcpHeaderLength = ((frame[tcp + 12] >> 4) & 0x0F) * 4;
            if (tcpHeaderLength < 20 || frame.Length < tcp + tcpHeaderLength) return null;

            int payloadStart = tcp + tcpHeaderLength;
            int payloadLength = frame.Length - payloadStart;

            // Отсекаем Ethernet-паддинг: используем длину из IP-заголовка.
            if (ipTotalLength > 0)
            {
                int declaredPayload = ipTotalLength - ipHeaderLength - tcpHeaderLength - reserved;
                if (declaredPayload >= 0 && declaredPayload < payloadLength)
                    payloadLength = declaredPayload;
            }

            if (payloadLength <= 0) return Array.Empty<byte>();

            var payload = new byte[payloadLength];
            Array.Copy(frame, payloadStart, payload, 0, payloadLength);
            return payload;
        }

        private static bool TryU16(byte[] data, int offset, out ushort value)
        {
            if (offset < 0 || offset + 2 > data.Length)
            {
                value = 0;
                return false;
            }

            value = (ushort)ReadU16(data, offset);
            return true;
        }

        private static int ReadU16(byte[] data, int offset)
            => (data[offset] << 8) | data[offset + 1];

        private static uint ReadU32(byte[] data, int offset)
            => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

        /// <summary>NEXT-93: полный TcpSegment (conn, seq, ack, flags, payload, timestamp).</summary>
        public static TcpSegment? ExtractSegment(byte[]? frame, DateTime timestamp, string fileId = "")
        {
            if (frame == null || frame.Length < 14) return null;
            int l3 = 14;
            if (!TryU16(frame, 12, out ushort etherType)) return null;
            int guard = 0;
            while ((etherType == 0x8100 || etherType == 0x88A8 || etherType == 0x9100) && guard++ < 3)
            {
                if (frame.Length < l3 + 4) return null;
                if (!TryU16(frame, l3 + 2, out etherType)) return null;
                l3 += 4;
            }
            if (etherType == 0x0800) return ExtractSegmentIpv4(frame, l3, timestamp, fileId);
            if (etherType == 0x86DD) return ExtractSegmentIpv6(frame, l3, timestamp, fileId);
            return null;
        }

        private static TcpSegment? ExtractSegmentIpv4(byte[] frame, int l3, DateTime ts, string fileId)
        {
            if (frame.Length < l3 + 20) return null;
            int ihl = (frame[l3] & 0x0F) * 4;
            if (ihl < 20 || frame.Length < l3 + ihl) return null;
            if (frame[l3 + 9] != 6) return null;
            int totalLength = ReadU16(frame, l3 + 2);
            string srcIp = frame[l3 + 12] + "." + frame[l3 + 13] + "." + frame[l3 + 14] + "." + frame[l3 + 15];
            string dstIp = frame[l3 + 16] + "." + frame[l3 + 17] + "." + frame[l3 + 18] + "." + frame[l3 + 19];
            return BuildSegment(frame, l3 + ihl, totalLength - ihl, srcIp, dstIp, ts, fileId);
        }

        private static TcpSegment? ExtractSegmentIpv6(byte[] frame, int l3, DateTime ts, string fileId)
        {
            if (frame.Length < l3 + 40) return null;
            if (frame[l3 + 6] != 6) return null;
            int payloadLength = ReadU16(frame, l3 + 4);
            string srcIp = Convert.ToHexString(frame, l3 + 8, 16);
            string dstIp = Convert.ToHexString(frame, l3 + 24, 16);
            return BuildSegment(frame, l3 + 40, payloadLength, srcIp, dstIp, ts, fileId);
        }

        private static TcpSegment? BuildSegment(byte[] frame, int tcp, int ipPayloadLength, string srcIp, string dstIp, DateTime ts, string fileId)
        {
            if (frame.Length < tcp + 20) return null;
            int srcPort = ReadU16(frame, tcp);
            int dstPort = ReadU16(frame, tcp + 2);
            if (srcPort != FisherPort && dstPort != FisherPort) return null;
            int tcpHeaderLength = ((frame[tcp + 12] >> 4) & 0x0F) * 4;
            if (tcpHeaderLength < 20 || frame.Length < tcp + tcpHeaderLength) return null;

            int payloadStart = tcp + tcpHeaderLength;
            int payloadLength = frame.Length - payloadStart;
            if (ipPayloadLength >= 0 && ipPayloadLength < payloadLength)
                payloadLength = ipPayloadLength;
            if (payloadLength < 0) payloadLength = 0;

            var seg = new TcpSegment
            {
                ConnKey = $"{srcIp}:{srcPort}->{dstIp}:{dstPort}",
                FromServer = srcPort == FisherPort,
                Seq = ReadU32(frame, tcp + 4),
                Ack = ReadU32(frame, tcp + 8),
                Flags = frame[tcp + 13],
                Timestamp = ts,
                Payload = new byte[payloadLength]
            };
            if (payloadLength > 0)
                Array.Copy(frame, payloadStart, seg.Payload, 0, payloadLength);
            return seg;
        }

    }
}
