using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;

namespace FisherLogger
{
    // =====================================================================================
    // NEXT-ANDROID-01: сервис только для чтения для Android-компаньона в локальной сети.
    //
    //  * Выключен по умолчанию; включается вручную кнопкой «Телефон» и не включается сам при запуске.
    //  * Принимает подключения только с частных адресов (127/8, 10/8, 172.16/12, 192.168/16, 169.254/16, fe80::/10, fc00::/7).
    //  * Сопряжение: 6-значный код (2 минуты, 5 попыток, одноразовый) + ECDH P-256. Код подтверждает обмен
    //    (PBKDF2-SHA256 → HMAC), общий секрет устройства из кода и ECDH по сети не передаётся.
    //  * Каждый запрос подписан HMAC-SHA256 (метод, путь, время, одноразовое число); окно ±120 с, повтор числа отвергается.
    //    Ответ подписан тем же секретом (целостность). Данные НЕ шифруются (HTTP в локальной сети).
    //  * Секреты устройств на ПК хранятся под DPAPI (текущий пользователь Windows), не в открытом виде.
    //  * Только GET (и POST сопряжения). Базы открываются Mode=ReadOnly; ни одного INSERT/UPDATE/DELETE в этом файле.
    //  * Аналитика считается тем же кодом, что окно «Аналитика» (AnalyticsLoader/AnalyticsEngine).
    //
    // NEXT-ANDROID-03:
    //  * ServerId — случайный постоянный идентификатор этого ПК (не секрет). Отдаётся в /api/ping (по нему телефон отличает
    //    «свой» ПК при поиске в сети) и в подписанном /api/status. Подлинность ПК по-прежнему доказывает только подпись
    //    ответа секретом устройства: чужой сервер с тем же ServerId подпись не подделает.
    //  * /api/status содержит dataVersion — отпечаток размеров и времени изменения файлов catches.db/-wal всех сессий и
    //    MAX(id) таблиц текущей базы. Телефон перезагружает данные только при смене dataVersion.
    //  * StartOnLaunch — сервис включается при запуске FisherLogger, только если пользователь сам включил его в прошлый раз
    //    и есть сопряжённые устройства. Ограничения (локальная сеть, подпись, только чтение) не меняются.
    // =====================================================================================

    public sealed class CompanionDevice
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string SecretDpapi { get; set; } = "";
        public DateTime Created { get; set; }
        public DateTime? LastSeen { get; set; }
        public string LastIp { get; set; } = "";
    }

    public sealed class CompanionSettings
    {
        public int Port { get; set; } = 47811;
        public List<CompanionDevice> Devices { get; set; } = new();
        /// <summary>NEXT-ANDROID-03: постоянный идентификатор этого ПК (не секрет).</summary>
        public string ServerId { get; set; } = "";
        /// <summary>NEXT-ANDROID-03: пользователь оставил сервис включённым — включить при следующем запуске.</summary>
        public bool StartOnLaunch { get; set; }
        /// <summary>AUDIT-04 (A1): отвечать и в общедоступных сетях Windows (по умолчанию нет — кафе, гостиница).</summary>
        public bool AllowPublicNetwork { get; set; }

        /// <summary>NEXT-ANDROID-03: файл настроек отсутствовал или прочитан без ошибок (только тогда его можно перезаписывать при выдаче ServerId).</summary>
        [System.Text.Json.Serialization.JsonIgnore] public bool SafeToRewrite { get; private set; }

        /// <summary>Только для тестов: другой путь файла настроек.</summary>
        public static string? PathOverride;
        public static string FilePath => PathOverride ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FisherLogger", "companion_settings.json");
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public static CompanionSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = JsonSerializer.Deserialize<CompanionSettings>(File.ReadAllText(FilePath), Json);
                    if (loaded != null) { loaded.SafeToRewrite = true; return loaded; }
                    return new CompanionSettings();
                }
                return new CompanionSettings { SafeToRewrite = true };
            }
            catch { }
            return new CompanionSettings(); // повреждённый файл: не перезаписываем автоматически
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
        }
    }

    /// <summary>DPAPI (CryptProtectData) без дополнительных пакетов.</summary>
    internal static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr hMem);
        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        public static byte[] Protect(byte[] data) => Run(data, true);
        public static byte[] Unprotect(byte[] data) => Run(data, false);

        private static byte[] Run(byte[] data, bool protect)
        {
            var input = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
            var output = new DATA_BLOB();
            try
            {
                Marshal.Copy(data, 0, input.pbData, data.Length);
                bool ok = protect
                    ? CryptProtectData(ref input, "FisherLogger companion", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
                if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
                var res = new byte[output.cbData];
                Marshal.Copy(output.pbData, res, 0, output.cbData);
                return res;
            }
            finally
            {
                Marshal.FreeHGlobal(input.pbData);
                if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            }
        }
    }

    public sealed class CompanionContext
    {
        public string CurrentDb = "";
        public string SessionDir = "";
        public uint? CurrentLocationId;
        public TimeSpan? ServerOffset;
        /// <summary>ANDROID-UX-01: настройки аналитики для «Советника» (null — прочитать из файла настроек, только чтение).</summary>
        public AnalyticsSettings? Analytics;
    }

    public sealed class CompanionServer : IDisposable
    {
        /// <summary>ANDROID-UX-01: новые возможности API (версия не меняется: старые клиенты их не видят).</summary>
        public static readonly string[] Features = { "advisor", "depthgrid", "icons", "gridTransform", "pointWindex", "hours", "coach", "live", "events", "empties" };
        public const int ApiVersion = 1; // NEXT-ANDROID-03: поля serverId/dataVersion только добавлены — версия API не меняется (совместимость)
        public const int PairCodeSeconds = 120;
        public const int PairMaxAttempts = 5;
        public const int Pbkdf2Iterations = 100_000;
        public const int ClockSkewSeconds = 120;
        private const int MaxHeaderBytes = 16 * 1024;
        private const int MaxBodyBytes = 8 * 1024;

        private readonly Func<CompanionContext> _ctx;
        private readonly PacketDeepParser? _parser;
        private readonly Action<string> _log;
        private readonly object _sync = new();
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        public CompanionSettings Settings { get; private set; }
        public bool IsRunning => _listener != null;
        public int Port { get; private set; }
        public event Action? StatusChanged;

        // сопряжение
        private string? _pairCode;
        private DateTime _pairExpires;
        private int _pairAttempts;
        private readonly Dictionary<string, (ECDiffieHellman key, byte[] salt, DateTime created)> _hellos = new();

        // защита от повторов и перебора
        private readonly ConcurrentDictionary<string, DateTime> _nonces = new();
        private readonly ConcurrentDictionary<string, (int fails, DateTime since, DateTime blockedUntil)> _ipFails = new();
        /// <summary>Сколько неудачных запросов за минуту с одного адреса приводят к блокировке на 5 минут.</summary>
        public int BlockAfterFailures = 20;
        /// <summary>AUDIT-04 (A3): не больше стольких одновременных подключений; лишние сразу закрываются.</summary>
        public const int MaxConnections = 16;
        private readonly SemaphoreSlim _conns = new(MaxConnections, MaxConnections);
        private DateTime _failsCleanAt = DateTime.UtcNow;
        public long RejectedPublicNetwork;
        private long _rejLogAt;   // VPNLAN-01: отказы пишем в журнал не чаще раза в минуту
        private void LogReject(IPAddress remote, string why)
        {
            long now = Environment.TickCount64, last = Interlocked.Read(ref _rejLogAt);
            if (now - last < 60_000 || Interlocked.CompareExchange(ref _rejLogAt, now, last) != last) return;
            _log("[COMPANION] Отклонено подключение от " + remote + ": " + why);
        }
        public int RequestsServed;
        public int RequestsRejected;

        public CompanionServer(Func<CompanionContext> ctx, PacketDeepParser? parser, Action<string>? log = null, CompanionSettings? settings = null)
        {
            _ctx = ctx;
            _parser = parser;
            _log = log ?? (_ => { });
            Settings = settings ?? CompanionSettings.Load();
            // NEXT-ANDROID-03: выдать ServerId один раз. Повреждённый файл настроек не перезаписывается.
            if (string.IsNullOrEmpty(Settings.ServerId))
            {
                Settings.ServerId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                if (settings != null || Settings.SafeToRewrite)
                {
                    try { Settings.Save(); } catch (Exception ex) { _log("[COMPANION] ServerId не сохранён: " + ex.Message); }
                }
            }
        }

        /// <summary>NEXT-ANDROID-03: запомнить выбор пользователя (включён/выключен) для следующего запуска.</summary>
        public void RememberRunning(bool running)
        {
            lock (_sync)
            {
                if (Settings.StartOnLaunch == running) return;
                Settings.StartOnLaunch = running;
                try { Settings.Save(); } catch (Exception ex) { _log("[COMPANION] Настройки не сохранены: " + ex.Message); }
            }
        }

        // ------------------------------------------------------------------ lifecycle
        /// <param name="bindAddress">NEXT-ANDROID-03: только для тестовых хостов (например, IPAddress.Loopback). По умолчанию — все интерфейсы, как раньше.</param>
        public void Start(int port, IPAddress? bindAddress = null)
        {
            lock (_sync)
            {
                if (_listener != null) return;
                var l = new TcpListener(bindAddress ?? IPAddress.IPv6Any, port);
                if (bindAddress == null) l.Server.DualMode = true;
                l.Start();
                _listener = l;
                Port = port;
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                Task.Run(() => AcceptLoop(l, ct));
            }
            _log("[COMPANION] Сервис включён, порт " + port + " (только локальная сеть, только чтение)");
            StatusChanged?.Invoke();
        }

        public void Stop()
        {
            lock (_sync)
            {
                if (_listener == null) return;
                try { _cts?.Cancel(); _listener.Stop(); } catch { }
                _listener = null;
                _pairCode = null;
                foreach (var h in _hellos.Values) h.key.Dispose();
                _hellos.Clear();
            }
            _log("[COMPANION] Сервис выключен");
            StatusChanged?.Invoke();
        }

        public void Dispose() => Stop();

        public string NewPairingCode()
        {
            lock (_sync)
            {
                _pairCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("000000", CultureInfo.InvariantCulture);
                _pairExpires = DateTime.UtcNow.AddSeconds(PairCodeSeconds);
                _pairAttempts = 0;
                return _pairCode;
            }
        }

        public (string? code, int secondsLeft) PairingState()
        {
            lock (_sync)
            {
                if (_pairCode == null || DateTime.UtcNow > _pairExpires) return (null, 0);
                return (_pairCode, (int)Math.Ceiling((_pairExpires - DateTime.UtcNow).TotalSeconds));
            }
        }

        public void CancelPairing() { lock (_sync) _pairCode = null; }

        public void Revoke(string id)
        {
            lock (_sync) { Settings.Devices.RemoveAll(d => d.Id == id); Settings.Save(); }
            _log("[COMPANION] Устройство отозвано: " + id);
            StatusChanged?.Invoke();
        }

        /// <summary>AUDIT-04 (A2): подпись этого ПК на телефоне — без имени компьютера.</summary>
        public string DisplayName => "FisherLogger · " + (Settings.ServerId.Length >= 4 ? Settings.ServerId[..4].ToUpperInvariant() : "ПК");

        public static List<string> LocalAddresses()
        {
            var list = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (IsTunnel(ni)) continue;   // VPNLAN-01: адрес VPN-туннеля телефону не подходит — не показывать
                    foreach (var a in ni.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a.Address))
                            list.Add(a.Address.ToString());
                }
            }
            catch { }
            return list.Distinct().ToList();
        }

        /// <summary>VPNLAN-01: адаптер VPN (WireGuard/Amnezia/TAP/TUN/PPP) — не домашняя сеть.</summary>
        public static bool IsTunnel(NetworkInterface ni) =>
            ni.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel
            || System.Text.RegularExpressions.Regex.IsMatch(ni.Description + " " + ni.Name, "WireGuard|Wintun|Amnezia|TAP-|TAP |\\bTUN\\b|VPN|OpenVPN|dedproxy", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public static bool IsPrivate(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
            }
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var b = ip.GetAddressBytes();
                return ip.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC;
            }
            return false;
        }

        // ------------------------------------------------------------------ HTTP
        private async Task AcceptLoop(TcpListener l, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                catch { break; }
                // AUDIT-04 (A3): медленные клиенты не держат неограниченное число потоков
                if (!_conns.Wait(0)) { Interlocked.Increment(ref RequestsRejected); try { c.Dispose(); } catch { } continue; }
                _ = Task.Run(() => { try { Handle(c); } finally { _conns.Release(); } });
            }
        }

        private sealed class Req
        {
            public string Method = "", Target = "", Path = "";
            public Dictionary<string, string> Query = new(StringComparer.Ordinal);
            public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
            public byte[] Body = Array.Empty<byte>();
            public IPAddress Remote = IPAddress.None;
        }

        private class Resp
        {
            public int Status = 200;
            public string ContentType = "application/json; charset=utf-8";
            public byte[] Body = Array.Empty<byte>();
            public static Resp Json(object o, int status = 200) => new() { Status = status, Body = JsonSerializer.SerializeToUtf8Bytes(o) };
            public static Resp Error(int status, string msg) => Json(new { error = msg }, status);
        }

        private void Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 10000;
                    client.SendTimeout = 15000;
                    var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                    if (!IsPrivate(remote)) { Interlocked.Increment(ref RequestsRejected); LogReject(remote, "адрес не из локальной сети"); return; } // не локальная сеть — молча закрыть
                    // AUDIT-04 (A1): сеть, которую Windows считает общедоступной (кафе, гостиница), — молча закрыть
                    var local = ((IPEndPoint)client.Client.LocalEndPoint!).Address;
                    if (!Settings.AllowPublicNetwork && NetworkGuard.CategoryOf(local) == NetworkGuard.Category.Public)
                    { Interlocked.Increment(ref RequestsRejected); Interlocked.Increment(ref RejectedPublicNetwork); LogReject(remote, "сеть Windows «общедоступная» (" + local + ")"); return; }
                    using var stream = client.GetStream();
                    var req = Read(stream);
                    if (req == null) { Write(client, Resp.Error(400, "bad request")); return; }
                    if (IsBlocked(remote)) { Interlocked.Increment(ref RequestsRejected); Write(client, Resp.Error(429, "too many failed attempts")); return; }
                    req.Remote = remote;
                    Resp resp;
                    try { resp = Route(req); }
                    catch (Exception ex) { resp = Resp.Error(500, "internal error"); _log("[COMPANION] Ошибка: " + ex.Message); }
                    Write(client, resp);
                }
                catch { }
            }
        }

        private static Req? Read(NetworkStream s)
        {
            var buf = new List<byte>(1024);
            int b;
            while (true)
            {
                b = s.ReadByte();
                if (b < 0) return null;
                buf.Add((byte)b);
                int n = buf.Count;
                if (n >= 4 && buf[n - 4] == 13 && buf[n - 3] == 10 && buf[n - 2] == 13 && buf[n - 1] == 10) break;
                if (n > MaxHeaderBytes) return null;
            }
            var lines = Encoding.ASCII.GetString(buf.ToArray()).Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 3) return null;
            var r = new Req { Method = first[0], Target = first[1] };
            foreach (var line in lines.Skip(1))
            {
                int i = line.IndexOf(':');
                if (i > 0) r.Headers[line[..i].Trim()] = line[(i + 1)..].Trim();
            }
            int q = r.Target.IndexOf('?');
            r.Path = Uri.UnescapeDataString(q < 0 ? r.Target : r.Target[..q]);
            if (q >= 0)
                foreach (var kv in r.Target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int e = kv.IndexOf('=');
                    string k = Uri.UnescapeDataString(e < 0 ? kv : kv[..e]);
                    string v = e < 0 ? "" : Uri.UnescapeDataString(kv[(e + 1)..].Replace('+', ' '));
                    r.Query[k] = v;
                }
            if (r.Headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out int len) && len > 0)
            {
                if (len > MaxBodyBytes) return null;
                r.Body = new byte[len];
                int read = 0;
                while (read < len)
                {
                    int k = s.Read(r.Body, read, len - read);
                    if (k <= 0) return null;
                    read += k;
                }
            }
            return r;
        }

        private static void Write(TcpClient c, Resp r, Dictionary<string, string>? extra = null)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(r.Status).Append(' ').Append(r.Status switch { 200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 409 => "Conflict", 429 => "Too Many Requests", _ => "Error" }).Append("\r\n");
            sb.Append("Content-Type: ").Append(r.ContentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(r.Body.Length).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\nConnection: close\r\n");
            if (r is SignedResp sr) foreach (var kv in sr.Headers) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            sb.Append("\r\n");
            var s = c.GetStream();
            var head = Encoding.ASCII.GetBytes(sb.ToString());
            s.Write(head, 0, head.Length);
            s.Write(r.Body, 0, r.Body.Length);
            s.Flush();
        }

        private sealed class SignedResp : Resp { public Dictionary<string, string> Headers = new(); }

        private bool IsBlocked(IPAddress ip)
        {
            return _ipFails.TryGetValue(ip.ToString(), out var f) && f.blockedUntil > DateTime.UtcNow;
        }

        private void Fail(IPAddress ip)
        {
            Interlocked.Increment(ref RequestsRejected);
            string k = ip.ToString();
            var now = DateTime.UtcNow;
            // AUDIT-04 (A3): старые записи о неудачах удаляются (словарь не растёт без предела)
            if ((now - _failsCleanAt).TotalMinutes >= 1 || _ipFails.Count > 512)
            {
                _failsCleanAt = now;
                foreach (var kv in _ipFails) if (now - kv.Value.since > TimeSpan.FromMinutes(10) && kv.Value.blockedUntil < now) _ipFails.TryRemove(kv.Key, out _);
            }
            _ipFails.AddOrUpdate(k, _ => (1, now, DateTime.MinValue), (_, f) =>
            {
                if (now - f.since > TimeSpan.FromMinutes(1)) return (1, now, f.blockedUntil);
                int n = f.fails + 1;
                return (n, f.since, n >= BlockAfterFailures ? now.AddMinutes(5) : f.blockedUntil);
            });
        }

        // ------------------------------------------------------------------ routing
        private Resp Route(Req r)
        {
            if (r.Method == "GET" && r.Path == "/api/ping")
                return Resp.Json(new { app = "FisherLogger", api = ApiVersion, pairing = PairingState().code != null, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), serverId = Settings.ServerId });
            if (r.Path == "/api/pair/hello")
                return r.Method == "GET" ? PairHello(r) : Resp.Error(405, "method not allowed");
            if (r.Path == "/api/pair")
                return r.Method == "POST" ? Pair(r) : Resp.Error(405, "method not allowed");
            if (r.Method != "GET") { Fail(r.Remote); return Resp.Error(405, "read-only service: only GET"); }

            var dev = Authenticate(r, out string? why);
            if (dev == null) { Fail(r.Remote); return Resp.Error(401, why ?? "unauthorized"); }
            byte[] secret = Dpapi.Unprotect(Convert.FromBase64String(dev.SecretDpapi));

            Resp inner;
            var parts = r.Path.Trim('/').Split('/');
            if (r.Path == "/api/status") inner = Status();
            else if (r.Path == "/api/options") inner = Options(r);
            else if (r.Path == "/api/analytics") inner = Analytics(r);
            else if (r.Path == "/api/catches") inner = Catches(r);
            else if (r.Path == "/api/live") inner = Live();            // FEAT-06: мини-карта телефона
            else if (r.Path == "/api/events") inner = Events(r);       // FEAT-06: уведомления о поклёвках
            else if (r.Path == "/api/public") inner = PublicFeed(r);
            else if (r.Path == "/api/maps") inner = Maps(r);
            else if (parts.Length == 4 && parts[0] == "api" && parts[1] == "maps" && uint.TryParse(parts[2], out uint mid) && parts[3] == "image") inner = MapImage(mid);
            else if (parts.Length == 4 && parts[0] == "api" && parts[1] == "maps" && uint.TryParse(parts[2], out uint pid) && parts[3] == "points") inner = MapPoints(r, pid);
            else if (r.Path == "/api/bathymetry") inner = BathyList();                                                                                   // NEXT-ANDROID-02
            else if (parts.Length == 4 && parts[0] == "api" && parts[1] == "maps" && uint.TryParse(parts[2], out uint bid) && parts[3] == "bathymetry") inner = BathyImage(bid);
            else if (parts.Length == 4 && parts[0] == "api" && parts[1] == "maps" && uint.TryParse(parts[2], out uint did) && parts[3] == "depth") inner = DepthAt(r, did);
            else if (parts.Length == 4 && parts[0] == "api" && parts[1] == "maps" && uint.TryParse(parts[2], out uint zid) && parts[3] == "zones") inner = Zones(r, zid);
            else if (parts.Length == 4 && parts[0] == "api" && parts[1] == "maps" && uint.TryParse(parts[2], out uint gid) && parts[3] == "depthgrid") inner = DepthGrid(gid);   // ANDROID-UX-01
            else if (r.Path == "/api/advisor") inner = Advisor();                                                                                        // ANDROID-UX-01
            else if (r.Path == "/api/icons") inner = Icons();                                                                                            // ANDROID-UX-01
            else if (r.Path == "/api/hourdata") inner = HourData();                                                                                      // HOURS-01
            else if (r.Path == "/api/hours") inner = Hours(r);
            else if (r.Path == "/api/goal") inner = GoalPlan(r);                                                                                         // GOAL-PHONE-01                                                                                           // HOURS-01
            else inner = Resp.Error(404, "not found");

            Interlocked.Increment(ref RequestsServed);
            lock (_sync)
            {
                dev.LastSeen = DateTime.Now;
                dev.LastIp = r.Remote.ToString();
            }
            var sr = new SignedResp { Status = inner.Status, ContentType = inner.ContentType, Body = inner.Body };
            string nonce = r.Headers["X-FL-Nonce"];
            string bodyHash = Convert.ToHexString(SHA256.HashData(inner.Body)).ToLowerInvariant();
            sr.Headers["X-FL-Sig"] = Convert.ToBase64String(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(nonce + "\n" + inner.Status + "\n" + bodyHash)));
            CryptographicOperations.ZeroMemory(secret);
            return sr;
        }

        public static string RequestSignatureBase(string method, string target, string time, string nonce) =>
            method + "\n" + target + "\n" + time + "\n" + nonce;

        private CompanionDevice? Authenticate(Req r, out string? why)
        {
            why = null;
            if (!r.Headers.TryGetValue("X-FL-Device", out var id) || !r.Headers.TryGetValue("X-FL-Time", out var time) ||
                !r.Headers.TryGetValue("X-FL-Nonce", out var nonce) || !r.Headers.TryGetValue("X-FL-Sig", out var sig))
            { why = "missing auth headers"; return null; }
            CompanionDevice? dev;
            lock (_sync) dev = Settings.Devices.FirstOrDefault(d => d.Id == id);
            if (dev == null) { why = "unknown device (pair again)"; return null; }
            if (!long.TryParse(time, out long ms) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ms) > ClockSkewSeconds * 1000L)
            { why = "clock skew too large"; return null; }
            if (nonce.Length < 16 || nonce.Length > 64) { why = "bad nonce"; return null; }
            byte[] secret = Dpapi.Unprotect(Convert.FromBase64String(dev.SecretDpapi));
            byte[] expect = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(RequestSignatureBase(r.Method, r.Target, time, nonce)));
            CryptographicOperations.ZeroMemory(secret);
            byte[] got;
            try { got = Convert.FromBase64String(sig); } catch { why = "bad signature"; return null; }
            if (!CryptographicOperations.FixedTimeEquals(expect, got)) { why = "bad signature"; return null; }
            var now = DateTime.UtcNow;
            foreach (var kv in _nonces.Where(kv => now - kv.Value > TimeSpan.FromSeconds(ClockSkewSeconds * 2 + 10)).ToList()) _nonces.TryRemove(kv.Key, out _);
            if (!_nonces.TryAdd(id + ":" + nonce, now)) { why = "replayed request"; return null; }
            return dev;
        }

        // ------------------------------------------------------------------ pairing
        private Resp PairHello(Req r)
        {
            lock (_sync)
            {
                if (_pairCode == null || DateTime.UtcNow > _pairExpires) return Resp.Error(409, "pairing not active: create a code in FisherLogger");
                foreach (var k in _hellos.Where(h => DateTime.UtcNow - h.Value.created > TimeSpan.FromSeconds(PairCodeSeconds)).Select(h => h.Key).ToList())
                { _hellos[k].key.Dispose(); _hellos.Remove(k); }
                if (_hellos.Count > 20) return Resp.Error(429, "too many pairing sessions");
                var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                var salt = RandomNumberGenerator.GetBytes(16);
                string pid = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
                _hellos[pid] = (key, salt, DateTime.UtcNow);
                return Resp.Json(new
                {
                    pairingId = pid,
                    serverPub = Convert.ToBase64String(key.PublicKey.ExportSubjectPublicKeyInfo()),
                    salt = Convert.ToBase64String(salt),
                    iterations = Pbkdf2Iterations
                });
            }
        }

        public static byte[] CodeKey(string code, byte[] salt, int iterations) =>
            Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(code), salt, iterations, HashAlgorithmName.SHA256, 32);

        private Resp Pair(Req r)
        {
            JsonElement j;
            try { j = JsonDocument.Parse(r.Body).RootElement; } catch { return Resp.Error(400, "bad json"); }
            string pid = j.TryGetProperty("pairingId", out var a) ? a.GetString() ?? "" : "";
            string clientPub = j.TryGetProperty("clientPub", out var b) ? b.GetString() ?? "" : "";
            string name = j.TryGetProperty("deviceName", out var c) ? (c.GetString() ?? "") : "";
            string proof = j.TryGetProperty("proof", out var d) ? d.GetString() ?? "" : "";
            name = new string(name.Where(ch => !char.IsControl(ch)).Take(60).ToArray());
            if (name.Length == 0) name = "Android";

            ECDiffieHellman key; byte[] salt; string code;
            lock (_sync)
            {
                if (_pairCode == null || DateTime.UtcNow > _pairExpires) return Resp.Error(409, "pairing not active");
                if (!_hellos.TryGetValue(pid, out var h)) return Resp.Error(400, "unknown pairing session");
                key = h.key; salt = h.salt; code = _pairCode;
            }
            byte[] kc = CodeKey(code, salt, Pbkdf2Iterations);
            string serverPub = Convert.ToBase64String(key.PublicKey.ExportSubjectPublicKeyInfo());
            byte[] expect = HMACSHA256.HashData(kc, Encoding.UTF8.GetBytes("pair1|" + serverPub + "|" + clientPub));
            byte[] got;
            try { got = Convert.FromBase64String(proof); } catch { got = Array.Empty<byte>(); }
            if (!CryptographicOperations.FixedTimeEquals(expect, got))
            {
                Fail(r.Remote);
                lock (_sync)
                {
                    _pairAttempts++;
                    if (_pairAttempts >= PairMaxAttempts) { _pairCode = null; _log("[COMPANION] Код сопряжения аннулирован: превышено число попыток"); StatusChanged?.Invoke(); }
                }
                return Resp.Error(401, "wrong pairing code");
            }
            byte[] shared;
            try
            {
                using var peer = ECDiffieHellman.Create();
                peer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(clientPub), out _);
                shared = key.DeriveRawSecretAgreement(peer.PublicKey);
            }
            catch { return Resp.Error(400, "bad client key"); }
            byte[] secret = HMACSHA256.HashData(kc, Concat(shared, Encoding.UTF8.GetBytes("fl-companion-v1")));
            string devId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            lock (_sync)
            {
                Settings.Devices.Add(new CompanionDevice
                {
                    Id = devId, Name = name, Created = DateTime.Now,
                    SecretDpapi = Convert.ToBase64String(Dpapi.Protect(secret)), LastIp = r.Remote.ToString()
                });
                Settings.Save();
                _pairCode = null; // одноразовый
                foreach (var hh in _hellos.Values) hh.key.Dispose();
                _hellos.Clear();
            }
            string confirm = Convert.ToBase64String(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes("confirm|" + devId)));
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(shared);
            _log("[COMPANION] Сопряжено устройство «" + name + "» (" + r.Remote + ")");
            StatusChanged?.Invoke();
            return Resp.Json(new { deviceId = devId, confirm });
        }

        private static byte[] Concat(byte[] a, byte[] b) { var r = new byte[a.Length + b.Length]; a.CopyTo(r, 0); b.CopyTo(r, a.Length); return r; }

        // ------------------------------------------------------------------ data (read-only)
        public static List<(string session, string db)> Sources(CompanionContext ctx, bool all)
        {
            var sources = new List<(string, string)> { (Path.GetFileName(ctx.SessionDir.TrimEnd('\\', '/')), ctx.CurrentDb) };
            if (all)
            {
                string? root = Directory.GetParent(ctx.SessionDir.TrimEnd('\\', '/'))?.FullName;
                if (root != null && Directory.Exists(root))
                    foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    {
                        string db = Path.Combine(dir, "data", "catches.db");
                        if (File.Exists(db) && !string.Equals(Path.GetFullPath(db), Path.GetFullPath(ctx.CurrentDb), StringComparison.OrdinalIgnoreCase))
                            sources.Add((Path.GetFileName(dir), db));
                    }
            }
            return sources;
        }

        private static bool AllScope(Req r) => !r.Query.TryGetValue("scope", out var s) || s != "current";
        private static int Limit(Req r, int def) => r.Query.TryGetValue("limit", out var s) && int.TryParse(s, out int n) ? Math.Clamp(n, 1, 2000) : def;
        private static string? T(DateTime? t) => t?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        private string LocName(uint? id)
        {
            if (!id.HasValue) return "";
            return MapRegistry.Get(id)?.Name ?? _parser?.GetLocation(id)?.Name ?? ("loc " + id);
        }

        // DESIGN-03: снимок тренера для телефона — кэш 5 с (телефон спрашивает статус раз в 10 с), кривая «минут в воде» — в фоне раз в 10 мин
        private readonly object _coachLock = new();
        private DateTime _coachAt = DateTime.MinValue; private object? _coachVal;
        private SoakCurve? _coachCurve; private DateTime _coachCurveAt = DateTime.MinValue; private int _coachCurveLoading;

        private List<object> _coachSpots = new();
        private object? CoachJson(CompanionContext ctx, DateTime now)
        {
            try
            {
                lock (_coachLock) if ((now - _coachAt).TotalSeconds < 5) return _coachVal;
                string db = ctx.CurrentDb;
                if ((_coachCurve == null || (now - _coachCurveAt).TotalMinutes > 10) && Interlocked.Exchange(ref _coachCurveLoading, 1) == 0)
                    Task.Run(() => { try { _coachCurve = BiteAnalytics.Load(db, new AnalyticsSettings()).Soak ?? _coachCurve; } catch { } _coachCurveAt = DateTime.Now; _coachCurveLoading = 0; });
                var snap = SessionCoach.Build(db, _coachCurve, now, LocName);
                // FEAT-07: пятна клёва для мини-карты телефона — подпись «% от обычного для этой снасти», как на ПК
                _coachSpots = snap.Spots.Where(sp => sp.Ticks >= ChanceSpots.MinTicks).OrderByDescending(sp => sp.Median)
                    .Select(sp => (object)new { x = sp.X, z = sp.Z, radius = ChanceSpots.MergeM, label = sp.Label, pct = sp.Pct, level = sp.Level,
                                                ageMin = Math.Round(sp.AgeMin, 1), bait = sp.Bait, grade = sp.Grade }).ToList();
                var val = new
                {
                    clock = DayParts.Clock(now),
                    fishing = snap.HasData,
                    location = snap.LocationName,
                    rods = snap.Rods.Select(r => new { slot = r.Slot + 1, rod = SoakCurve.RodName(r.RodCode), bait = r.Setup.Bait, status = r.Status, level = r.Level, active = r.Active, bites = r.Bites, empty = r.Empty,
                        empties = r.RecentEmpty.Select(e => new { fishId = e.Fish, fish = e.Name, weight = e.Weight, windex = e.Windex, time = e.At > DateTime.MinValue ? e.At.ToString("HH:mm", CultureInfo.InvariantCulture) : null }).ToList() }).ToList(),   // FEAT-06
                    hints = snap.Hints.Where(h => !h.StartsWith("В игре ", StringComparison.Ordinal)).ToList()
                };
                lock (_coachLock) { _coachAt = now; _coachVal = val; }
                return val;
            }
            catch { return null; }
        }

        /// <summary>FEAT-06: игрок (последняя поза из трафика) и забросы последних 90 мин — как мини-карта ПК (север вверх).</summary>
        private Resp Live()
        {
            var ctx = _ctx(); var now = DateTime.Now;
            var pose = _parser?.Track.Last;
            List<OverlayCastMark> casts = new();
            try
            {
                using var con = new SqliteConnection("Data Source=" + ctx.CurrentDb + ";Mode=ReadOnly;Pooling=False");
                con.Open();
                casts = OverlayDataSource.ReadCastMarks(con, null, now, _parser?.Track);
            }
            catch { }
            CoachJson(ctx, now);   // FEAT-07: обновляет пятна (кэш 5 с)
            return Resp.Json(new
            {
                spots = _coachSpots,
                locationId = ctx.CurrentLocationId, locationName = LocName(ctx.CurrentLocationId),
                player = pose == null ? null : new { x = pose.X, z = pose.Z, yaw = pose.Yaw, ageSec = (int)Math.Max(0, (now - pose.Time).TotalSeconds) },
                casts = casts.Select(c => new { id = c.Id, rod = c.Rod + 1, x = c.X, z = c.Z, state = c.State, active = c.Active,
                                                minutes = Math.Round((now - c.Time).TotalMinutes, 1), playerX = c.PlayerX, playerZ = c.PlayerZ }).ToList()
            });
        }

        /// <summary>FEAT-06: поклёвки, пустые поклёвки и уловы новее since (id cast_events текущей базы). since &lt; 0 — только номер последнего
        /// события (чтобы телефон не уведомлял о старом). Вид, вес и класс сообщает сервер игры.</summary>
        private Resp Events(Req r)
        {
            long since = r.Query.TryGetValue("since", out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : -1;
            var ctx = _ctx(); long last = 0; var list = new List<object>();
            try
            {
                using var con = new SqliteConnection("Data Source=" + ctx.CurrentDb + ";Mode=ReadOnly;Pooling=False");
                con.Open();
                using (var q = new SqliteCommand("SELECT ifnull(max(id),0) FROM cast_events", con)) last = Convert.ToInt64(q.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
                if (since >= 0)
                {
                    using var q = new SqliteCommand("SELECT e.id, e.timestamp, e.event_type, e.fish_lang_id, e.weight, e.windex, c.rod_index FROM cast_events e " +
                                                    "LEFT JOIN casts c ON c.id = e.cast_id WHERE e.id > $s AND e.event_type IN ('bite','empty_bite','catch') ORDER BY e.id LIMIT 50", con);
                    q.Parameters.AddWithValue("$s", since);
                    using var rd = q.ExecuteReader();
                    while (rd.Read())
                    {
                        uint? fid = rd.IsDBNull(3) ? null : (uint)rd.GetInt64(3);
                        list.Add(new
                        {
                            id = rd.GetInt64(0), time = rd.IsDBNull(1) ? null : rd.GetString(1), kind = rd.GetString(2),
                            fishId = fid, fish = fid.HasValue ? SessionCoach.FishNameOf(fid.Value) : null,
                            weight = rd.IsDBNull(4) ? (int?)null : (int)rd.GetInt64(4), windex = rd.IsDBNull(5) ? (int?)null : (int)rd.GetInt64(5),
                            rod = rd.IsDBNull(6) ? (int?)null : rd.GetInt32(6) + 1
                        });
                    }
                }
            }
            catch { }
            return Resp.Json(new { last, events = list });
        }

        private Resp Status()
        {
            var ctx = _ctx();
            var now = DateTime.Now;
            var (gt, unc) = GameClock.Estimate(now, ctx.ServerOffset);
            object? weather = null;
            try
            {
                using var con = new SqliteConnection("Data Source=" + ctx.CurrentDb + ";Mode=ReadOnly;Pooling=False");
                con.Open();
                using var cmd = new SqliteCommand("SELECT timestamp,cloudly,fog,rain,tempr,grom FROM weather ORDER BY id DESC LIMIT 1", con);
                using var rd = cmd.ExecuteReader();
                if (rd.Read())
                {
                    double? F(int i) => rd.IsDBNull(i) ? null : rd.GetDouble(i);
                    weather = new { time = rd.IsDBNull(0) ? null : rd.GetString(0), cloudly = F(1), fog = F(2), rain = F(3), temperature = F(4), thunder = F(5), units = "UNKNOWN (s2c1001 raw values)" };
                }
            }
            catch { }
            return Resp.Json(new
            {
                app = "FisherLogger", api = ApiVersion, features = Features,
                pcTime = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                serverTimeKnown = ctx.ServerOffset.HasValue,
                gameTime = gt, gameTimeUncMin = unc, gameTimeModel = GameClock.ModelVersion,
                currentLocationId = ctx.CurrentLocationId, currentLocationName = LocName(ctx.CurrentLocationId),
                session = Path.GetFileName(ctx.SessionDir.TrimEnd('\\', '/')),
                weather,
                coach = CoachJson(ctx, now),             // DESIGN-03: «Сейчас» на телефоне — те же советы по удочкам, что в окне ПК (только добавлено)
                serverId = Settings.ServerId,            // NEXT-ANDROID-03 (ответ подписан — подтверждённый идентификатор)
                serverName = DisplayName,    // AUDIT-04 (A2): не имя компьютера (часто = имя пользователя), а нейтральная метка
                dataVersion = DataVersion(ctx)           // NEXT-ANDROID-03
            });
        }

        /// <summary>
        /// NEXT-ANDROID-03: отпечаток данных — длина и время изменения catches.db и catches.db-wal всех баз сессий (те же
        /// источники, что scope=all) плюс MAX(id) четырёх таблиц текущей базы (дешёвый запрос по rowid, Mode=ReadOnly).
        /// </summary>
        // PRERELEASE-01: тяжёлые загрузки (все уловы и объявления) кэшируются до изменения данных (dataVersion) — раньше каждый
        // запрос телефона читал и сопоставлял всё заново. Результаты только читаются обработчиками.
        private readonly object _cacheLock = new();
        private readonly Dictionary<string, (string ver, object val)> _cache = new();
        private T Cached<T>(string kind, IEnumerable<(string session, string db)> src, Func<List<(string session, string db)>, T> load) where T : notnull
        {
            var list = src.ToList();
            string key = kind + "|" + string.Join(";", list.Select(x => x.session + "=" + x.db));
            string ver = "";
            try { ver = DataVersion(_ctx()); } catch { }
            lock (_cacheLock)
                if (ver.Length > 0 && _cache.TryGetValue(key, out var hit) && hit.ver == ver && hit.val is T t) return t;
            var val = load(list);
            lock (_cacheLock)
            {
                if (_cache.Count > 8) _cache.Clear();
                if (ver.Length > 0) _cache[key] = (ver, val);
            }
            return val;
        }
        private AnalyticsDataset CachedAnalytics(IEnumerable<(string session, string db)> src) => Cached("A", src, l => AnalyticsLoader.Load(l, _parser));
        private (List<MapCatch> catches, List<MapCast> casts, List<string> errors) CachedMap(IEnumerable<(string session, string db)> src) => Cached("M", src, l => MapDataLoader.Load(l, _parser));

        public static string DataVersion(CompanionContext ctx)
        {
            var sb = new StringBuilder();
            foreach (var (session, db) in Sources(ctx, true))
            {
                sb.Append(session).Append('|');
                foreach (var f in new[] { db, db + "-wal" })
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.Exists) sb.Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks);
                    }
                    catch { }
                    sb.Append(';');
                }
                sb.Append('\n');
            }
            // Текущая база: максимальные id (по rowid — без сканирования таблиц), только чтение.
            try
            {
                using var con = new SqliteConnection("Data Source=" + ctx.CurrentDb + ";Mode=ReadOnly;Pooling=False");
                con.Open();
                foreach (var t in new[] { "catches", "cast_events", "public_catches", "casts" })
                {
                    try
                    {
                        using var cmd = new SqliteCommand("SELECT COALESCE(MAX(id),0) FROM " + t, con);
                        sb.Append(t).Append('=').Append(Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture)).Append(';');
                    }
                    catch { sb.Append(t).Append("=?;"); }
                }
            }
            catch { sb.Append("db=?"); }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).Substring(0, 16).ToLowerInvariant();
        }

        private Resp Options(Req r)
        {
            var ctx = _ctx();
            var src = Sources(ctx, AllScope(r));
            var ds = CachedAnalytics(src);
            var all = ds.Mine.Concat(ds.Public).ToList();
            var dates = all.Where(a => a.Real.HasValue).Select(a => a.Real!.Value.Date).ToList();
            return Resp.Json(new
            {
                sessions = ds.SourcesRead,
                species = all.GroupBy(a => a.SpeciesKey).OrderByDescending(g => g.Count()).ThenBy(g => g.First().Fish, StringComparer.CurrentCulture)
                             .Select(g => new { key = g.Key, name = g.First().Fish, mine = g.Count(a => a.IsMine), pub = g.Count(a => !a.IsMine) }),
                waterbodies = all.GroupBy(a => a.LocationId).OrderByDescending(g => g.Count())
                                 .Select(g => new { id = g.Key, name = g.Key.HasValue ? LocName(g.Key) : "водоём не записан", mine = g.Count(a => a.IsMine), pub = g.Count(a => !a.IsMine) }),
                dateMin = dates.Count > 0 ? dates.Min().ToString("yyyy-MM-dd") : null,
                dateMax = dates.Count > 0 ? dates.Max().ToString("yyyy-MM-dd") : null
            });
        }

        private Resp Analytics(Req r)
        {
            var ctx = _ctx();
            var ds = CachedAnalytics(Sources(ctx, AllScope(r)));
            var q = r.Query;
            var f = new AnalyticsFilter();
            if (q.TryGetValue("species", out var sp) && sp.Length > 0)
            {
                f.SpeciesKey = sp;
                f.SpeciesName = ds.Mine.Concat(ds.Public).FirstOrDefault(a => a.SpeciesKey == sp)?.Fish ?? sp;
            }
            if (q.TryGetValue("loc", out var loc) && loc.Length > 0)
            {
                f.AnyLocation = false;
                f.LocationId = loc == "none" ? null : uint.TryParse(loc, out uint l) ? l : null;
                f.LocationName = f.LocationId.HasValue ? LocName(f.LocationId) : "водоём не записан";
            }
            if (q.TryGetValue("from", out var fr) && DateTime.TryParseExact(fr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fd)) f.From = fd;
            if (q.TryGetValue("to", out var to) && DateTime.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var td)) f.To = td;
            if (q.TryGetValue("series", out var se)) { f.ShowMine = se != "pub"; f.ShowPublic = se != "mine"; }
            if (q.TryGetValue("excludeOwn", out var eo)) f.ExcludeOwnFromPublic = eo != "0";
            var res = AnalyticsEngine.Compute(ds, f);
            object S(AnalyticsSeries s) => new
            {
                hours = s.Hours, total = s.Total, missingTime = s.MissingTime, nearBoundary = s.NearBoundary, ownExcluded = s.OwnExcluded,
                species = s.Species, models = s.Models, top = s.Top(3).Select(t => new { hour = t.hour, count = t.count }),
                sampleNote = AnalyticsEngine.SampleNote(s.Total)
            };
            return Resp.Json(new
            {
                filter = new { species = f.SpeciesKey, speciesName = f.SpeciesName, loc = f.AnyLocation ? null : (object?)(f.LocationId?.ToString() ?? "none"), locationName = f.LocationName,
                               from = f.From?.ToString("yyyy-MM-dd"), to = f.To?.ToString("yyyy-MM-dd"), showMine = f.ShowMine, showPublic = f.ShowPublic, excludeOwn = f.ExcludeOwnFromPublic },
                mine = f.ShowMine ? S(res.Mine) : null,
                pub = f.ShowPublic ? S(res.Public) : null,
                coverageMinutes = res.CoverageMinutes,
                species = res.Species,
                summary = AnalyticsEngine.Summary(ds, res),
                interpretation = AnalyticsForm.InterpretationText,
                model = GameClock.ModelVersion
            });
        }

        private Resp Catches(Req r)
        {
            var ctx = _ctx();
            var src = Sources(ctx, AllScope(r));
            var ds = CachedAnalytics(src);
            var (mapCatches, _, _) = CachedMap(src);
            var byRec = mapCatches.GroupBy(c => c.RecordId).ToDictionary(g => g.Key, g => g.First());
            var grades = ReadGrades(src);
            var list = ds.Mine.OrderByDescending(a => a.Real ?? DateTime.MinValue).ThenByDescending(a => a.RealText, StringComparer.Ordinal).Take(Limit(r, 300)).Select(a =>
            {
                byRec.TryGetValue(a.Key, out var m);
                grades.TryGetValue(a.Key, out var gw);
                var (g, wx) = gw;
                return new
                {
                    key = a.Key, session = a.Session, realTime = a.RealText, realTimeValid = a.Real.HasValue,
                    gameTime = a.GameMinute.HasValue ? a.GameText : null, gameTimeUncMin = a.GameMinute.HasValue ? a.UncMin : (int?)null,
                    gameTimeModel = a.GameMinute.HasValue ? a.Model : null, timeIssue = a.TimeIssue,
                    fish = a.Fish, fishId = a.FishId, weight = a.Weight, grade = g ?? "",
                    // BRIDGE-FB-01: класс из игры (windex); grade — старая метка по весу, оставлена для совместимости
                    windex = wx, classText = wx is int cw && cw >= 0 ? GradeBadge.ShortName(cw) : "",
                    bait = m?.Bait ?? "", depthCm = m?.DepthCm, status = m?.Status ?? "",
                    locationId = a.LocationId, locationName = LocName(a.LocationId),
                    x = m?.X, z = m?.Z, castVerified = m?.Verified ?? false
                };
            });
            return Resp.Json(new { items = list, total = ds.Mine.Count, timeSource = "PC local time at packet receipt / Player.log read; game time estimated by GameClock without server offset" });
        }

        private static Dictionary<string, (string? grade, int? windex)> ReadGrades(List<(string session, string db)> src)
        {
            var d = new Dictionary<string, (string? grade, int? windex)>(StringComparer.Ordinal);
            foreach (var (_, db) in src)
            {
                try
                {
                    using var con = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False");
                    con.Open();
                    using var cmd = new SqliteCommand("SELECT record_id, grade, windex FROM catches", con);
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read())
                        if (!rd.IsDBNull(0))
                            d.TryAdd(rd.GetString(0), (rd.IsDBNull(1) ? null : rd.GetString(1), rd.IsDBNull(2) ? null : (int)rd.GetInt64(2)));
                }
                catch { }
            }
            return d;
        }

        private Resp PublicFeed(Req r)
        {
            var ds = CachedAnalytics(Sources(_ctx(), AllScope(r)));
            var list = ds.Public.OrderByDescending(a => a.Real ?? DateTime.MinValue).Take(Limit(r, 300)).Select(a => new
            {
                key = a.Key, receivedAt = a.RealText, receivedValid = a.Real.HasValue, serverTime = T(a.Server),
                gameTime = a.GameMinute.HasValue ? a.GameText : null, gameTimeUncMin = a.GameMinute.HasValue ? a.UncMin : (int?)null,
                gameTimeModel = a.GameMinute.HasValue ? a.Model : null, timeIssue = a.TimeIssue,
                player = a.Player, playerId = a.PlayerId, fish = a.Fish, fishId = a.FishId, weight = a.Weight,
                receiverLocationId = a.LocationId, receiverLocationName = LocName(a.LocationId), matchedOwn = a.MatchedOwn
            });
            return Resp.Json(new { items = list, total = ds.Public.Count, locationNote = "receiver location: where you were when the announcement arrived; the packet has no catch location" });
        }

        private Resp Maps(Req r)
        {
            var (catches, _, _) = CachedMap(Sources(_ctx(), AllScope(r)));
            var counts = catches.Where(c => c.X.HasValue && c.Z.HasValue && c.LocationId.HasValue).GroupBy(c => c.LocationId!.Value)
                                .ToDictionary(g => g.Key, g => (all: g.Count(), verified: g.Count(c => c.Verified)));
            var maps = MapRegistry.All.OrderBy(m => m.LocationId).Select(m => new
            {
                locationId = m.LocationId, name = m.Name, width = m.Width, height = m.Height,
                minX = m.MinX, maxX = m.MaxX, minZ = m.MinZ, maxZ = m.MaxZ, sha256 = m.Sha256,
                confidence = m.Confidence, validation = m.Validation, validationText = m.ValidationText, mapVersion = m.MapVersion,
                catches = counts.TryGetValue(m.LocationId, out var c) ? c.all : 0,
                bathyStatus = Bathymetry.Get(m.LocationId)?.Status ?? "NO_DATA",
                grid = MapGridCoordinates.For(m.LocationId) is { } gt
                    ? new { originX = gt.OriginX, originZ = gt.OriginZ, sizeX = gt.SizeX, sizeZ = gt.SizeZ, cell = gt.Cell } : null,   // ANDROID-UX-01: координаты NNN:NNN без связи
                verifiedCatches = counts.TryGetValue(m.LocationId, out var c2) ? c2.verified : 0
            });
            return Resp.Json(new { maps, errors = MapRegistry.Errors });
        }

        private Resp MapImage(uint id)
        {
            var m = MapRegistry.Get(id);
            if (m == null) return Resp.Error(404, "no map");
            string file = Path.GetFileName(m.File); // только имя файла из реестра
            string path = IconLoader.ResolvePath("assets/maps/" + file);
            if (!File.Exists(path)) return Resp.Error(404, "map file missing");
            byte[] bytes = File.ReadAllBytes(path);
            if (!string.IsNullOrEmpty(m.Sha256) && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(m.Sha256, StringComparison.OrdinalIgnoreCase))
                return Resp.Error(409, "map sha256 mismatch");
            return new Resp { ContentType = "image/png", Body = bytes };
        }

        // ------------------------------------------------------------------ NEXT-ANDROID-02: батиметрия и зоны (тот же код, что в Windows)
        private static object BathyJson(BathyInfo b) => new
        {
            locationId = b.LocationId, name = b.Name, status = b.Status, statusText = Bathymetry.StatusText(b.Status), note = b.Note,
            underwaterRelief = b.UnderwaterRelief, absoluteDepth = b.AbsoluteDepth, depthConfidence = b.DepthConfidence, alignmentConfidence = b.AlignmentConfidence,
            coverage = b.Coverage, maxDepthM = b.MaxDepthM, meanDepthM = b.MeanDepthM, waterLevel = b.WaterLevel, contourIntervalM = b.ContourIntervalM,
            frame = b.Implemented ? new { minX = b.MinX, maxX = b.MaxX, minZ = b.MinZ, maxZ = b.MaxZ } : null,
            overlaySha256 = b.OverlaySha,
            depthGrid = b.Implemented && b.GridFile.Length > 0 ? new { width = b.GridW, height = b.GridH, sha256 = b.GridSha, encoding = "gzip uint16 LE, cm, 65535 = нет данных, строка 0 = maxZ" } : null,   // ANDROID-UX-01
            labels = b.Labels.Select(l => new { x = l.X, z = l.Z, depthM = l.DepthM, kind = l.Kind }),
            bands = b.Bands.Select(x => new { fromM = x.FromM, toM = x.ToM, rgb = x.Rgb, alpha = x.Alpha })
        };

        private Resp BathyList() =>
            Resp.Json(new { items = Bathymetry.All.OrderBy(b => b.LocationId).Select(BathyJson), errors = Bathymetry.Errors,
                            definition = "depth (m) = scene water level − terrain height (game TerrainData)" });

        private Resp BathyImage(uint id)
        {
            var b = Bathymetry.Get(id);
            if (b == null || !b.Implemented || MapRegistry.Get(id) == null) return Resp.Error(404, "no bathymetry for this waterbody");
            var bytes = Bathymetry.VerifiedBytes(b.OverlayFile, b.OverlaySha);
            return bytes == null ? Resp.Error(409, "bathymetry file sha256 mismatch") : new Resp { ContentType = "image/png", Body = bytes };
        }

        private Resp DepthAt(Req r, uint id)
        {
            if (!r.Query.TryGetValue("x", out var xs) || !r.Query.TryGetValue("z", out var zs) ||
                !double.TryParse(xs, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) || !double.TryParse(zs, NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                return Resp.Error(400, "x and z required");
            var b = Bathymetry.Get(id);
            return Resp.Json(new { locationId = id, x, z, depthM = Bathymetry.DepthAt(id, x, z), status = b?.Status ?? "NO_DATA" });
        }

        private Resp Zones(Req r, uint id)
        {
            var (catches, _, _) = CachedMap(Sources(_ctx(), AllScope(r)));
            r.Query.TryGetValue("species", out var sp);
            DateTime? from = null, to = null;
            if (r.Query.TryGetValue("from", out var fr) && DateTime.TryParseExact(fr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fd)) from = fd;
            if (r.Query.TryGetValue("to", out var tt) && DateTime.TryParseExact(tt, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var td)) to = td;
            bool unv = r.Query.TryGetValue("unverified", out var u) && u == "1";
            var pts = ActivityZones.Filter(catches, id, string.IsNullOrEmpty(sp) ? null : sp, from, to, unv);
            var map = MapRegistry.Get(id);
            double span = map != null ? Math.Max(map.SpanX, map.SpanZ) :
                (pts.Count > 0 ? Math.Max(pts.Max(c => c.X!.Value) - pts.Min(c => c.X!.Value), pts.Max(c => c.Z!.Value) - pts.Min(c => c.Z!.Value)) * 6 : 300);
            double cell = ActivityZones.CellSize(span);
            var zones = ActivityZones.Compute(pts, cell);
            var ptIndex = new Dictionary<MapCatch, int>(ReferenceEqualityComparer.Instance); for (int i = 0; i < pts.Count; i++) ptIndex[pts[i]] = i;   // ZONES-02: члены зоны → индексы points
            var speciesAll = catches.Where(c => c.LocationId == id && c.X.HasValue && (c.Verified || unv)).GroupBy(c => c.Fish)
                                    .OrderByDescending(g => g.Count()).Select(g => new { name = g.Key, count = g.Count() });
            return Resp.Json(new
            {
                locationId = id, cellM = cell, radiusM = cell / 2, zoneShape = "circle", total = pts.Count, species = speciesAll,
                zones = zones.Select(z => new
                {
                    x = z.CenterX, z = z.CenterZ, r = z.Radius, minX = z.MinX, maxX = z.MaxX, minZ = z.MinZ, maxZ = z.MaxZ, count = z.Count,
                    species = z.Species.Select(s => new { name = s.fish, count = s.count }), first = z.First, last = z.Last,
                    depthM = Bathymetry.DepthAt(id, z.CenterX, z.CenterZ),
                    pts = z.Members.Select(m => ptIndex.TryGetValue(m, out int k) ? k : -1).Where(k => k >= 0)
                }),
                points = pts.Select(c => new { recordId = c.RecordId, time = c.TimeText, fish = c.Fish, fishId = c.FishId, windex = c.Windex, weight = c.Weight, x = c.X, z = c.Z, verified = c.Verified,
                                               bait = c.Bait, depthCm = c.DepthCm, castId = c.CastId, castRod = c.CastRod }),
                note = "Zones aggregate MY recorded catches by cast point (not a confirmed landing location). Counts are not bite probability. Public announcements have no coordinates."
            });
        }

        // ------------------------------------------------------------------ ANDROID-UX-01: советник, сетка глубин, иконки (для работы без связи)
        private Resp DepthGrid(uint id)
        {
            var b = Bathymetry.Get(id);
            if (b == null || !b.Implemented || b.GridFile.Length == 0) return Resp.Error(404, "no depth grid for this waterbody");
            var bytes = Bathymetry.VerifiedBytes(b.GridFile, b.GridSha);
            return bytes == null ? Resp.Error(409, "depth grid sha256 mismatch") : new Resp { ContentType = "application/octet-stream", Body = bytes };
        }

        private BiteAnalytics LoadAdvisor()
        {
            var ctx = _ctx();
            var s = ctx.Analytics ?? AppSettings.Load().Analytics;
            return BiteAnalytics.Load(ctx.CurrentDb, s, AdvFish, AdvLoc, AdvBait);
        }
        private string AdvFish(uint id) { try { return _parser?.GetFish(id)?.Name ?? ""; } catch { return ""; } }
        private string AdvLoc(uint? id) { try { return id.HasValue ? _parser?.GetLocation(id.Value)?.Name ?? "" : ""; } catch { return ""; } }
        private string AdvBait(long id) { try { return id >= 0 && id <= uint.MaxValue ? _parser?.GetBait((uint)id)?.Name ?? _parser?.GetItem((uint)id)?.Name ?? "" : ""; } catch { return ""; } }
        private ReferenceProvider? _refs;
        private ReferenceProvider? Refs() { try { return _refs ??= new ReferenceProvider(); } catch { return null; } }

        private Resp Advisor()
        {
            try { return Resp.Json(CompanionAdvisor.Build(LoadAdvisor(), Refs())); }
            catch (Exception ex) { _log("[COMPANION] советник: " + ex.Message); return Resp.Error(500, "advisor failed"); }
        }

        private Resp Icons()
        {
            try
            {
                var an = LoadAdvisor();
                var fish = an.KnownSpecies().Select(x => x.id).ToList();
                try { fish.AddRange(Refs()?.ReferenceSpecies().Select(x => x.Id) ?? Enumerable.Empty<uint>()); } catch { }
                var baits = an.KnownSpecies().Select(x => x.id).SelectMany(f => an.Advise(f).Rows.Where(r => r.BaitTid.HasValue).Select(r => r.BaitTid!.Value)).ToList();
                return Resp.Json(CompanionAdvisor.Icons(fish, baits));
            }
            catch (Exception ex) { _log("[COMPANION] иконки: " + ex.Message); return Resp.Error(500, "icons failed"); }
        }

        // ------------------------------------------------------------------ HOURS-01: по игровым часам (тот же расчёт, что Советник на ПК)
        private static readonly JsonSerializerOptions HourJson = new() { IncludeFields = true };
        private (List<HourCast> casts, HabitatIndex hab) HourExtra()
        {
            try { var an = LoadAdvisor(); return (HourStats.Casts(an.Attempts), an.Habitat); } catch { return (new(), new HabitatIndex()); }
        }

        private Resp HourData()
        {
            var ds = CachedAnalytics(Sources(_ctx(), true));
            var (recs, cov) = HourStats.From(ds);
            var (casts, hab) = HourExtra();
            HourStats.AssignLocations(cov, recs, casts);
            return Resp.Json(HourStats.Export(recs, cov, casts, HourStats.HabitatMap(hab, recs)));
        }

        private Resp Hours(Req r)
        {
            var ds = CachedAnalytics(Sources(_ctx(), true));
            var (recs, cov) = HourStats.From(ds);
            var (casts, hab) = HourExtra();
            HourStats.AssignLocations(cov, recs, casts);
            var q = new HourQuery { Habitat = HourStats.HabitatMap(hab, recs) };
            if (r.Query.TryGetValue("series", out var se) && se is "mine" or "pub" or "auto" or "all") q.Series = se;
            if (r.Query.TryGetValue("period", out var pe) && pe is "auto" or "day" or "3days" or "all") q.Period = pe;
            if (r.Query.TryGetValue("loc", out var lo) && uint.TryParse(lo, out var lid)) q.LocationId = lid;
            if (r.Query.TryGetValue("species", out var sp) && !string.IsNullOrEmpty(sp)) q.SpeciesKey = sp;
            if (r.Query.TryGetValue("now", out var nw) && DateTime.TryParseExact(nw, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var nd)) q.Now = nd;
            return new Resp { Body = JsonSerializer.SerializeToUtf8Bytes(HourStats.Build(recs, cov, casts, q), HourJson) };
        }

        // ------------------------------------------------------------------ GOAL-PHONE-01: «Цель» (тот же расчёт, что вкладка «Цель» на ПК)
        private Resp GoalPlan(Req r)
        {
            try
            {
                GoalSpec? pc = null;
                try { var g = AppSettings.Load().Goal; if (g != null && g.Fish > 0) pc = new GoalSpec { Fish = g.Fish, MinClass = g.MinClass, Location = g.Location }; } catch { }
                GoalSpec? spec = null;
                if (r.Query.TryGetValue("fish", out var fs) && uint.TryParse(fs, out uint fid) && fid > 0)
                {
                    spec = new GoalSpec { Fish = fid, MinClass = 2 };
                    if (r.Query.TryGetValue("cls", out var cs) && int.TryParse(cs, out int c)) spec.MinClass = Math.Clamp(c, 0, 3);
                    if (r.Query.TryGetValue("loc", out var ls) && uint.TryParse(ls, out uint l) && l > 0) spec.Location = l;
                }
                spec ??= pc;
                DateTime now = DateTime.Now;
                if (r.Query.TryGetValue("now", out var nw) && DateTime.TryParseExact(nw, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var nd)) now = nd;
                return Resp.Json(CompanionGoal.Build(LoadAdvisor(), Refs(), spec, AdvBait, now, pc));
            }
            catch (Exception ex) { _log("[COMPANION] цель: " + ex.Message); return Resp.Error(500, "goal failed"); }
        }

        private Resp MapPoints(Req r, uint id)
        {
            var (catches, casts, errors) = CachedMap(Sources(_ctx(), AllScope(r)));
            var pts = catches.Where(c => c.LocationId == id && c.X.HasValue && c.Z.HasValue).Select(c => new
            {
                recordId = c.RecordId, session = c.Session, time = c.TimeText, fish = c.Fish, fishId = c.FishId, windex = c.Windex, weight = c.Weight,
                bait = c.Bait, depthCm = c.DepthCm, x = c.X, z = c.Z, spawnLocId = c.SpawnLocId, status = c.Status,
                verified = c.Verified, castId = c.CastId, castRod = c.CastRod, castOutcome = c.CastOutcome
            });
            var cs = casts.Where(k => k.WaterKey.StartsWith("L" + id + "/", StringComparison.Ordinal)).Select(k => new
            { session = k.Session, id = k.Id, rod = k.Rod, x = k.X, z = k.Z, outcome = k.Outcome });
            return Resp.Json(new
            {
                locationId = id, catches = pts, casts = cs, errors,
                note = "Coordinates are the recorded cast point of the catch (verified = equal to a recorded cast). Not a confirmed physical landing location."
            });
        }
    }

    /// <summary>Окно управления сервисом компаньона.</summary>
    public sealed class CompanionDialog : Form
    {
        private readonly CompanionServer _srv;
        private readonly Label _state = new() { AutoSize = true, Font = Theme.UiBold, Margin = new Padding(0, 4, 0, 8) };
        private readonly Button _toggle = new() { Width = 220, Height = 38 };
        private readonly NumericUpDown _port = new() { Minimum = 1024, Maximum = 65535, Width = 100 };
        private readonly Label _addr = new() { AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.Text };
        private readonly Label _net = new() { AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.TextMuted, Margin = new Padding(0, 4, 0, 0) };
        private DateTime _netAt = DateTime.MinValue;
        private readonly Button _pairBtn = new() { Text = "Создать код сопряжения", Width = 220, Height = 38 };
        private readonly Label _code = new() { AutoSize = true, Font = new Font("Consolas", 26f, FontStyle.Bold), ForeColor = Theme.Warning };
        private readonly ListBox _devices = new() { Width = 470, Height = 110 };
        private readonly Button _revoke = new() { Text = "Отозвать выбранное", Width = 220, Height = 34 };
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

        public CompanionDialog(CompanionServer srv)
        {
            _srv = srv;
            Text = "Android-компаньон — FisherLogger";
            Size = new Size(540, 720);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Background; ForeColor = Theme.Text; Font = Theme.Ui;
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;

            var p = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(16) };
            Label H(string t) => new() { Text = t, AutoSize = true, ForeColor = Theme.Accent, Font = Theme.UiBold, Margin = new Padding(0, 10, 0, 4) };
            p.Controls.Add(new Label
            {
                Text = "Сервис только для чтения: телефон видит уловы, аналитику и карты, но не может ничего изменить. " +
                       "Работает только в локальной сети. Если вы оставили сервис включённым, он включится сам при следующем запуске FisherLogger " +
                       "(при наличии сопряжённых устройств); выключенный — останется выключенным.",
                AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.TextMuted
            });
            p.Controls.Add(H("Состояние"));
            p.Controls.Add(_state);
            var portRow = new FlowLayoutPanel { AutoSize = true };
            portRow.Controls.Add(new Label { Text = "Порт:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
            portRow.Controls.Add(_port);
            p.Controls.Add(portRow);
            p.Controls.Add(_toggle);
            p.Controls.Add(H("Адрес для телефона"));
            p.Controls.Add(_addr);
            p.Controls.Add(_net);   // AUDIT-04 (A1)
            p.Controls.Add(H("Сопряжение"));
            p.Controls.Add(_pairBtn);
            p.Controls.Add(_code);
            p.Controls.Add(H("Сопряжённые устройства"));
            p.Controls.Add(_devices);
            p.Controls.Add(_revoke);
            p.Controls.Add(new Label
            {
                Text = "Сервис не отвечает в сетях, которые Windows считает общедоступными. Гостиница, кафе, общежитие — сделайте такую сеть общедоступной " +
                       "(Параметры → Сеть → свойства сети → «Общедоступная»): тогда соседи по сети не увидят сервис. Брандмауэр: разрешите FisherLogger только «Частные сети».",
                AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.TextMuted, Margin = new Padding(0, 10, 0, 0)
            });
            Controls.Add(p);
            Theme.StyleButton(_toggle, Theme.ButtonPrimary);
            Theme.StyleButton(_pairBtn, Theme.ButtonSecondary);
            Theme.StyleButton(_revoke, Theme.ButtonWarn);
            _devices.BackColor = Theme.SurfaceAlt; _devices.ForeColor = Theme.Text;
            _port.Value = Math.Clamp(srv.Settings.Port, 1024, 65535);

            _toggle.Click += (s, e) =>
            {
                try
                {
                    if (_srv.IsRunning) { _srv.Stop(); _srv.RememberRunning(false); } // NEXT-ANDROID-03
                    else
                    {
                        _srv.Settings.Port = (int)_port.Value;
                        _srv.Settings.Save();
                        _srv.Start((int)_port.Value);
                        _srv.RememberRunning(true); // NEXT-ANDROID-03
                    }
                }
                catch (Exception ex) { UiText.Box(this, "Не удалось включить сервис: " + ex.Message, "Компаньон", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                Refresh2();
            };
            _pairBtn.Click += (s, e) => { if (_srv.IsRunning) _srv.NewPairingCode(); Refresh2(); };
            _revoke.Click += (s, e) => { if (_devices.SelectedItem is DevItem d) _srv.Revoke(d.Id); Refresh2(); };
            _timer.Tick += (s, e) => Refresh2();
            _timer.Start();
            FormClosed += (s, e) => { _timer.Stop(); _srv.CancelPairing(); };
            Refresh2();
        }

        private sealed class DevItem { public string Id = ""; public string Text = ""; public override string ToString() => Text; }

        private void Refresh2()
        {
            bool on = _srv.IsRunning;
            _state.Text = on ? "Включён · порт " + _srv.Port + " · запросов " + _srv.RequestsServed + ", отклонено " + _srv.RequestsRejected : "Выключен";
            _state.ForeColor = on ? Theme.AccentGreen : Theme.TextMuted;
            _toggle.Text = on ? "Выключить сервис" : "Включить сервис";
            _port.Enabled = !on;
            _pairBtn.Enabled = on;
            var ips = CompanionServer.LocalAddresses();
            if ((DateTime.Now - _netAt).TotalSeconds > 5) { _netAt = DateTime.Now; _net.Text = NetworkGuard.Describe() + (_srv.RejectedPublicNetwork > 0 ? " Отклонено из общедоступной сети: " + _srv.RejectedPublicNetwork + "." : ""); }
            _addr.Text = on ? (ips.Count == 0 ? "нет адреса локальной сети" : string.Join("\n", ips.Select(ip => ip + " : " + _srv.Port))) : "—";
            var (code, left) = _srv.PairingState();
            _code.Text = code == null ? "" : code.Substring(0, 3) + " " + code.Substring(3) + "   (" + left + " с)";
            string? sel = (_devices.SelectedItem as DevItem)?.Id;
            var items = _srv.Settings.Devices.Select(d => new DevItem
            {
                Id = d.Id,
                Text = d.Name + " · сопряжено " + d.Created.ToString("dd.MM HH:mm") + (d.LastSeen.HasValue ? " · был " + d.LastSeen.Value.ToString("dd.MM HH:mm:ss") + " (" + d.LastIp + ")" : "")
            }).ToList();
            if (!items.Select(i => i.Text).SequenceEqual(_devices.Items.Cast<DevItem>().Select(i => i.Text)))
            {
                _devices.Items.Clear();
                foreach (var i in items) _devices.Items.Add(i);
                int idx = items.FindIndex(i => i.Id == sel);
                if (idx >= 0) _devices.SelectedIndex = idx;
            }
        }
    }
}
