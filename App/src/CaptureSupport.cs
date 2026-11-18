using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace FisherLogger
{
    /// <summary>NEXT-107: one capture device as reported by Npcap/SharpPcap (no device is opened to build it).</summary>
    public sealed class CaptureCandidate
    {
        public CaptureCandidate(string name, string? description, string? friendlyName, IEnumerable<string>? ipv4)
        {
            Name = name ?? "";
            Description = description ?? "";
            FriendlyName = friendlyName ?? "";
            IPv4 = (ipv4 ?? Array.Empty<string>()).ToList();
        }
        /// <summary>Npcap device name, e.g. \Device\NPF_{GUID}.</summary>
        public string Name { get; }
        public string Description { get; }
        public string FriendlyName { get; }
        public IReadOnlyList<string> IPv4 { get; }
        public override string ToString() => (FriendlyName.Length > 0 ? FriendlyName : Description) + " [" + Name + "]" + (IPv4.Count > 0 ? " " + string.Join(",", IPv4) : "");
    }

    /// <summary>NEXT-107: the interface Windows would use for the game server (routing-table lookup only, no traffic).</summary>
    public sealed class RouteInfo
    {
        public RouteInfo(uint interfaceIndex, string adapterId, string name, string description)
        {
            InterfaceIndex = interfaceIndex;
            AdapterId = adapterId ?? "";
            Name = name ?? "";
            Description = description ?? "";
        }
        public uint InterfaceIndex { get; }
        /// <summary>Adapter GUID as "{...}" (NetworkInterface.Id).</summary>
        public string AdapterId { get; }
        public string Name { get; }
        public string Description { get; }
        public override string ToString() => $"ifIndex={InterfaceIndex} '{Name}' ({Description}) {AdapterId}";
    }

    public sealed class CaptureSelection
    {
        public bool Ok { get; init; }
        public CaptureCandidate? Device { get; init; }
        public string Reason { get; init; } = "";
        public string Error { get; init; } = "";
    }

    /// <summary>
    /// NEXT-107: chooses the capture interface. Never "the first adapter with an IPv4 address":
    ///  1. explicit selector (--capture-interface): exact match on Npcap name / GUID / friendly name / description,
    ///     or a unique substring match; none or several -> error;
    ///  2. otherwise the interface of the OS route to the server (GetBestInterface) matched to the Npcap device by
    ///     adapter GUID; no route, no device or several devices -> error (no guessing).
    /// No interface index is hard-coded and no VPN is assumed.
    /// </summary>
    public static class CaptureInterfaceSelector
    {
        public static CaptureSelection Select(IReadOnlyList<CaptureCandidate> candidates, RouteInfo? route, string? explicitSelector)
        {
            if (candidates == null || candidates.Count == 0)
                return Fail("Npcap не вернул ни одного адаптера.");

            if (!string.IsNullOrWhiteSpace(explicitSelector))
            {
                string s = explicitSelector.Trim();
                var exact = candidates.Where(c => Eq(c.Name, s) || Eq(c.FriendlyName, s) || Eq(c.Description, s) || Eq(GuidOf(c.Name), NormGuid(s))).ToList();
                var pick = exact.Count > 0 ? exact : candidates.Where(c => Has(c.Name, s) || Has(c.FriendlyName, s) || Has(c.Description, s)).ToList();
                if (pick.Count == 1)
                    return new CaptureSelection { Ok = true, Device = pick[0], Reason = "явно задан --capture-interface '" + s + "'" };
                if (pick.Count == 0)
                    return Fail("--capture-interface '" + s + "' не найден. Доступны: " + List(candidates));
                return Fail("--capture-interface '" + s + "' неоднозначен (" + pick.Count + " совпадения): " + List(pick));
            }

            if (route == null)
                return Fail("Не удалось определить маршрут Windows к серверу; задайте --capture-interface. Доступны: " + List(candidates));

            string guid = NormGuid(route.AdapterId);
            var byGuid = guid.Length == 0 ? new List<CaptureCandidate>() : candidates.Where(c => Eq(GuidOf(c.Name), guid)).ToList();
            if (byGuid.Count == 1)
                return new CaptureSelection { Ok = true, Device = byGuid[0], Reason = "маршрут Windows к серверу: " + route };
            if (byGuid.Count == 0)
                return Fail("Интерфейс маршрута " + route + " не найден среди адаптеров Npcap (Npcap не привязан?). Доступны: " + List(candidates));
            return Fail("Интерфейс маршрута " + route + " соответствует нескольким адаптерам Npcap: " + List(byGuid));
        }

        /// <summary>"{GUID}" from "\Device\NPF_{GUID}" (upper case), or "".</summary>
        public static string GuidOf(string npcapName)
        {
            if (string.IsNullOrEmpty(npcapName)) return "";
            int a = npcapName.IndexOf('{'), b = npcapName.IndexOf('}', a + 1);
            return a >= 0 && b > a ? npcapName.Substring(a, b - a + 1).ToUpperInvariant() : "";
        }

        private static string NormGuid(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            string t = s.Trim();
            if (!t.StartsWith("{")) t = "{" + t;
            if (!t.EndsWith("}")) t += "}";
            return Guid.TryParse(t, out _) ? t.ToUpperInvariant() : "";
        }

        private static bool Eq(string a, string b) => a.Length > 0 && b.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static bool Has(string a, string b) => a.Length > 0 && a.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0;
        private static string List(IEnumerable<CaptureCandidate> c) => string.Join("; ", c.Select(x => x.ToString()));
        private static CaptureSelection Fail(string e) => new CaptureSelection { Ok = false, Error = e };
    }

    /// <summary>NEXT-107: OS routing-table lookup (GetBestInterface). Reads the table only; sends nothing.</summary>
    public static class OsRouteLookup
    {
        [DllImport("iphlpapi.dll", SetLastError = false)]
        private static extern int GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);

        public static RouteInfo? ForIPv4(string serverIp, out string? error)
        {
            error = null;
            try
            {
                if (!IPAddress.TryParse(serverIp, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                { error = "не IPv4: " + serverIp; return null; }
                uint dest = BitConverter.ToUInt32(ip.GetAddressBytes(), 0); // network byte order as in memory
                int rc = GetBestInterface(dest, out uint idx);
                if (rc != 0) { error = "GetBestInterface rc=" + rc; return null; }
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        var p4 = ni.GetIPProperties().GetIPv4Properties();
                        if (p4 != null && (uint)p4.Index == idx)
                            return new RouteInfo(idx, ni.Id, ni.Name, ni.Description);
                    }
                    catch (NetworkInformationException) { }
                    catch (PlatformNotSupportedException) { }
                }
                error = "ifIndex " + idx + " не найден среди NetworkInterface";
                return null;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }
    }

    /// <summary>
    /// NEXT-107: brings every captured frame to Ethernet framing so that the existing Ethernet-offset code,
    /// the PCAPNG file (LINKTYPE_ETHERNET) and the offline readers/replay keep working unchanged.
    /// WireGuard/Wintun and other L3 adapters deliver raw IP (no Ethernet header); loopback delivers a 4-byte
    /// family header. For those a synthetic 14-byte header (zero MACs, EtherType from the IP version) is prepended;
    /// the IP packet bytes are kept unchanged.
    /// </summary>
    public static class CaptureFrameNormalizer
    {
        public const int DltNull = 0, DltEthernet = 1, DltRaw12 = 12, DltRaw14 = 14, LinktypeRaw = 101, DltLoop = 108, LinktypeIPv4 = 228, LinktypeIPv6 = 229;

        public static bool IsSupported(int linkType) =>
            linkType is DltEthernet or DltNull or DltLoop or DltRaw12 or DltRaw14 or LinktypeRaw or LinktypeIPv4 or LinktypeIPv6;

        public static string Describe(int linkType) => linkType switch
        {
            DltEthernet => "Ethernet",
            DltNull => "BSD loopback (Null)",
            DltLoop => "OpenBSD loopback",
            DltRaw12 or DltRaw14 or LinktypeRaw => "Raw IP",
            LinktypeIPv4 => "Raw IPv4",
            LinktypeIPv6 => "Raw IPv6",
            _ => "не поддерживается (" + linkType + ")"
        };

        public static bool TryToEthernet(byte[] raw, int linkType, out byte[] frame)
        {
            frame = Array.Empty<byte>();
            if (raw == null || raw.Length == 0) return false;
            switch (linkType)
            {
                case DltEthernet:
                    frame = raw;
                    return true;
                case DltNull:
                case DltLoop:
                    if (raw.Length <= 4) return false;
                    return Wrap(raw, 4, out frame);
                case DltRaw12:
                case DltRaw14:
                case LinktypeRaw:
                case LinktypeIPv4:
                case LinktypeIPv6:
                    return Wrap(raw, 0, out frame);
                default:
                    return false;
            }
        }

        private static bool Wrap(byte[] raw, int ipOffset, out byte[] frame)
        {
            frame = Array.Empty<byte>();
            int version = raw[ipOffset] >> 4;
            ushort etherType = version == 4 ? (ushort)0x0800 : version == 6 ? (ushort)0x86DD : (ushort)0;
            if (etherType == 0) return false;
            int len = raw.Length - ipOffset;
            frame = new byte[14 + len];
            frame[12] = (byte)(etherType >> 8);
            frame[13] = (byte)etherType;
            Buffer.BlockCopy(raw, ipOffset, frame, 14, len);
            return true;
        }
    }

    /// <summary>NEXT-107: the game-traffic filter, as a BPF string and as the equivalent managed check.</summary>
    public static class GameTrafficFilter
    {
        /// <summary>Both directions (host = src or dst, port = src or dst), TCP and UDP.</summary>
        public static string Bpf(string serverIp, int port) => "(tcp port " + port + " or udp port " + port + ") and host " + serverIp;

        /// <summary>Managed equivalent applied to an Ethernet-framed IPv4 frame (after normalization).</summary>
        public static bool Matches(byte[] ethFrame, string serverIp, int port, out bool fromServer)
        {
            fromServer = false;
            if (ethFrame == null || ethFrame.Length < 14 + 20 + 8) return false;
            if (ethFrame[12] != 0x08 || ethFrame[13] != 0x00) return false;
            int l3 = 14;
            if ((ethFrame[l3] >> 4) != 4) return false;
            int ihl = (ethFrame[l3] & 0x0F) * 4;
            if (ihl < 20 || ethFrame.Length < l3 + ihl + 8) return false;
            byte proto = ethFrame[l3 + 9];
            if (proto != 6 && proto != 17) return false;
            string src = ethFrame[l3 + 12] + "." + ethFrame[l3 + 13] + "." + ethFrame[l3 + 14] + "." + ethFrame[l3 + 15];
            string dst = ethFrame[l3 + 16] + "." + ethFrame[l3 + 17] + "." + ethFrame[l3 + 18] + "." + ethFrame[l3 + 19];
            int t = l3 + ihl;
            int sp = (ethFrame[t] << 8) | ethFrame[t + 1], dp = (ethFrame[t + 2] << 8) | ethFrame[t + 3];
            bool hostOk = src == serverIp || dst == serverIp;
            bool portOk = sp == port || dp == port;
            fromServer = src == serverIp;
            return hostOk && portOk;
        }
    }
}

namespace FisherLogger
{
    /// <summary>NEXT-107: opening the capture device with an explicit, complete failure message (no fallback).</summary>
    public static class CaptureOpen
    {
        public static bool TryOpen(Action open, string description, string name, out string message)
        {
            try
            {
                open();
                message = "адаптер открыт: " + description + " (" + name + ")";
                return true;
            }
            catch (Exception ex)
            {
                message = "не удалось открыть адаптер '" + description + "' (" + name + "): " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }
    }
}
