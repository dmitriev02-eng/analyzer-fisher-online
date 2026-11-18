using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace FisherLogger
{
    /// <summary>
    /// AUDIT-04 (A1): категория сети Windows (общедоступная / частная / доменная) для адреса этого ПК.
    /// Сервис телефона не отвечает в общедоступных сетях (кафе, гостиница, если Windows так её пометил) — независимо от брандмауэра.
    /// Источник — штатный Network List Manager (COM), только чтение; при ошибке — «неизвестно» (не блокирует).
    /// </summary>
    public static class NetworkGuard
    {
        public enum Category { Unknown = -1, Public = 0, Private = 1, Domain = 2 }
        public sealed record NetInfo(string Name, Category Category, Guid Adapter);

        /// <summary>Тесты: подмена списка сетей.</summary>
        public static Func<List<NetInfo>>? Override;
        private static List<NetInfo> _cache = new();
        private static DateTime _cacheAt = DateTime.MinValue;
        private static readonly object Sync = new();

        public static List<NetInfo> Networks()
        {
            if (Override != null) return Override();
            lock (Sync)
            {
                if ((DateTime.UtcNow - _cacheAt).TotalSeconds < 20) return _cache;
                var list = new List<NetInfo>();
                try
                {
                    var nlm = (INetworkListManager)new NetworkListManagerClass();
                    var en = nlm.GetNetworkConnections();
                    while (en.Next(1, out var conn, out uint got) == 0 && got == 1 && conn != null)
                    {
                        try
                        {
                            if (!conn.get_IsConnected()) continue;
                            var net = conn.GetNetwork();
                            list.Add(new NetInfo(net.GetName(), (Category)net.GetCategory(), conn.GetAdapterId()));
                        }
                        catch { }
                    }
                }
                catch { }
                _cache = list; _cacheAt = DateTime.UtcNow;
                return list;
            }
        }

        /// <summary>Категория сети, к которой относится локальный адрес соединения (адрес этого ПК).</summary>
        public static Category CategoryOf(IPAddress local)
        {
            if (IPAddress.IsLoopback(local)) return Category.Private;
            if (local.IsIPv4MappedToIPv6) local = local.MapToIPv4();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!ni.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(local))) continue;
                    if (!Guid.TryParse(ni.Id, out var id)) return Category.Unknown;
                    var nets = Networks().Where(n => n.Adapter == id).ToList();
                    if (nets.Count == 0) return Category.Unknown;
                    return nets.Any(n => n.Category == Category.Public) ? Category.Public : nets.Max(n => n.Category);
                }
            }
            catch { }
            return Category.Unknown;
        }

        // ---- Network List Manager (netlistmgr.h), порядок методов как в интерфейсах Windows ----
        [ComImport, Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")] private class NetworkListManagerClass { }

        [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface INetworkListManager
        {
            [return: MarshalAs(UnmanagedType.IUnknown)] object GetNetworks(int flags);
            INetwork GetNetwork(Guid id);
            IEnumNetworkConnections GetNetworkConnections();
        }

        [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IEnumNetworkConnections
        {
            [return: MarshalAs(UnmanagedType.IUnknown)] object get__NewEnum();
            [PreserveSig] int Next(uint celt, out INetworkConnection conn, out uint fetched);
        }

        [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface INetworkConnection
        {
            INetwork GetNetwork();
            [return: MarshalAs(UnmanagedType.VariantBool)] bool get_IsConnectedToInternet();
            [return: MarshalAs(UnmanagedType.VariantBool)] bool get_IsConnected();
            int GetConnectivity();
            Guid GetConnectionId();
            Guid GetAdapterId();
        }

        [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface INetwork
        {
            [return: MarshalAs(UnmanagedType.BStr)] string GetName();
            void SetName([MarshalAs(UnmanagedType.BStr)] string name);
            [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
            void SetDescription([MarshalAs(UnmanagedType.BStr)] string d);
            Guid GetNetworkId();
            int GetDomainType();
            IEnumNetworkConnections GetNetworkConnections();
            void GetTimeCreatedAndConnected(out uint a, out uint b, out uint c, out uint d);
            [return: MarshalAs(UnmanagedType.VariantBool)] bool get_IsConnectedToInternet();
            [return: MarshalAs(UnmanagedType.VariantBool)] bool get_IsConnected();
            int GetConnectivity();
            int GetCategory();
        }

        public static string Title(Category c) => c switch { Category.Public => "общедоступная", Category.Private => "частная (домашняя)", Category.Domain => "рабочая (домен)", _ => "неизвестно" };

        /// <summary>Строка для окна «Телефон»: какие сети сейчас подключены и что это значит.</summary>
        public static string Describe()
        {
            var nets = Networks();
            if (nets.Count == 0) return "Сети: не удалось определить категорию (сервис отвечает только частным адресам).";
            return "Сети сейчас: " + string.Join("; ", nets.Select(n => "«" + n.Name + "» — " + Title(n.Category))) + ".";
        }
    }
}
