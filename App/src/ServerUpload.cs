using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace FisherLogger
{
    /// <summary>
    /// SERVER-01: отправка обезличенных событий рыбалки на сервер проекта — только с согласия (ServerSettings.CanSend).
    /// Отправляется: вид, вес, класс, водоём, клетка 5 м, игровое время, удочка, наживка и крючок (id предметов), шанс.
    /// Никогда: ник, номер игрока, чат, данные других игроков. Установка подписывает каждый запрос своим ключом (ECDSA P-256),
    /// ключ хранится зашифрованным (DPAPI, текущий пользователь Windows). Сервер принимается только с закреплённым сертификатом.
    /// История до включения не отправляется — только новое.
    /// </summary>
    public sealed class ServerUploader : IDisposable
    {
        public const string DefaultUrl = "https://155.212.244.164:8443";
        /// <summary>SHA-256 открытого ключа сертификата сервера (base64). Другой сертификат — соединение отклоняется.</summary>
        public const string PinSpkiSha256 = "nDG/da8pMMF0BivkJcNr6cUoAh6lJceP6yt7ggh2NQA=";
        public static string? KeyPathOverride { get; set; }
        public static string KeyPath => KeyPathOverride ?? Path.Combine(AppInfo.UserDir, "server_key.bin");

        private readonly Func<string> _db; private readonly Func<uint?> _loc; private readonly Func<TimeSpan?> _off; private readonly Action<string>? _log;
        private System.Threading.Timer? _timer; private int _busy;

        public ServerUploader(Func<string> currentDb, Func<uint?> location, Func<TimeSpan?> serverOffset, Action<string>? log)
        { _db = currentDb; _loc = location; _off = serverOffset; _log = log; }

        public void Start() => _timer ??= new System.Threading.Timer(_ => Tick(), null, 20_000, 60_000);
        public void Dispose() { _timer?.Dispose(); _timer = null; }

        public static HttpClient Http(string? pin = PinSpkiSha256)
        {
            var h = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) => cert != null && pin != null && SpkiPin(cert) == pin
            };
            var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(30) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("FisherLogger/" + AppInfo.Version);
            return c;
        }

        public static string SpkiPin(X509Certificate2 cert)
        {
            using var k = (AsymmetricAlgorithm?)cert.GetECDsaPublicKey() ?? cert.GetRSAPublicKey();
            byte[] spki = k switch { ECDsa e => e.ExportSubjectPublicKeyInfo(), RSA r => r.ExportSubjectPublicKeyInfo(), _ => Array.Empty<byte>() };
            return Convert.ToBase64String(SHA256.HashData(spki));
        }

        /// <summary>Ключ установки: создаётся один раз, хранится зашифрованным DPAPI.</summary>
        public static ECDsa LoadOrCreateKey()
        {
            var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            try
            {
                if (File.Exists(KeyPath)) { k.ImportPkcs8PrivateKey(ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), null, DataProtectionScope.CurrentUser), out _); return k; }
            }
            catch { }
            Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
            File.WriteAllBytes(KeyPath, ProtectedData.Protect(k.ExportPkcs8PrivateKey(), null, DataProtectionScope.CurrentUser));
            return k;
        }

        public static string Sign(ECDsa key, string ts, string nonce, byte[] body)
        {
            string msg = "POST\n/api/v1/submit\n" + ts + "\n" + nonce + "\n" + Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            return Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(msg), HashAlgorithmName.SHA256));   // r||s (IEEE P1363)
        }

        /// <summary>Событие для сервера или null, если обязательного нет (водоём). Ничего не выдумывается.</summary>
        public static Dictionary<string, object?>? EventJson(string uid, string kind, DateTime local, uint? fish, int? weight, int? windex, int? rod,
                                                           double? x, double? z, long? bait, long? hook, double? chance, uint? loc, TimeSpan? off)
        {
            if (loc == null) return null;
            var e = new Dictionary<string, object?>
            {
                ["uid"] = uid, ["kind"] = kind, ["ts"] = new DateTimeOffset(local.ToUniversalTime()).ToUnixTimeMilliseconds(), ["waterbody"] = (int)loc.Value,
                ["fish"] = fish.HasValue ? (int)fish.Value : null, ["weight"] = weight, ["windex"] = windex, ["rod"] = rod,
                ["bait"] = bait, ["hook"] = hook, ["chance"] = chance.HasValue ? Math.Round(chance.Value, 3) : null
            };
            if (x.HasValue && z.HasValue && MapGridCoordinates.ToGrid(loc, x.Value, z.Value) is { } g && g.X >= 0 && g.Y >= 0) e["cell"] = new[] { g.X, g.Y };
            var gt = GameClock.Estimate(local, off).text;
            if (gt != null && gt.Length == 5 && gt[2] == ':') e["game_time"] = gt;
            return e;
        }

        private void Tick()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                var ss = ServerSettings.Load();
                if (!ss.CanSend) return;
                string db = _db();
                if (!File.Exists(db)) return;
                using var con = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False");
                con.Open();
                long max;
                using (var q = new SqliteCommand("SELECT ifnull(max(id),0) FROM cast_events", con)) max = Convert.ToInt64(q.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
                if (ss.LastDb != db || ss.LastEventId > max) { ss.LastDb = db; ss.LastEventId = max; ss.Save(); return; }   // новая база: история не отправляется
                if (max <= ss.LastEventId) return;
                using var key = LoadOrCreateKey();
                using var http = Http();
                string url = ss.Url.TrimEnd('/');
                if (string.IsNullOrEmpty(ss.RemoteId))
                {
                    var reg = JsonSerializer.Serialize(new { pubkey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
                    var rr = http.PostAsync(url + "/api/v1/register", new StringContent(reg, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                    if (!rr.IsSuccessStatusCode) { Note(ss, "регистрация: " + (int)rr.StatusCode); return; }
                    ss.RemoteId = JsonDocument.Parse(rr.Content.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.GetProperty("install_id").GetString(); ss.Save();
                }
                uint? loc = _loc(); var off = _off();
                var events = new List<Dictionary<string, object?>>(); long top = ss.LastEventId;
                string salt = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(db))).Substring(0, 8);
                using (var q = new SqliteCommand("SELECT e.id, e.timestamp, e.event_type, e.fish_lang_id, e.weight, e.windex, c.rod_index, c.cast_x, c.cast_z, t.bait_item_id, t.hook_item_id, " +
                    "(SELECT w.chance FROM cast_events w WHERE w.cast_id = e.cast_id AND w.event_type = 'wait' AND w.chance IS NOT NULL AND w.id < e.id ORDER BY w.id DESC LIMIT 1) " +
                    "FROM cast_events e LEFT JOIN casts c ON c.id = e.cast_id LEFT JOIN cast_tackle t ON t.cast_id = e.cast_id " +
                    "WHERE e.id > $s AND e.event_type IN ('bite','empty_bite','catch') ORDER BY e.id LIMIT 500", con))
                {
                    q.Parameters.AddWithValue("$s", ss.LastEventId);
                    using var r = q.ExecuteReader();
                    while (r.Read())
                    {
                        long id = r.GetInt64(0); top = id;
                        if (!DateTime.TryParse(r.IsDBNull(1) ? "" : r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)) continue;
                        long? L(int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
                        double? D(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
                        var e = EventJson(salt + "-" + id.ToString(CultureInfo.InvariantCulture), r.GetString(2), t, L(3) is long f ? (uint)f : null, (int?)L(4), (int?)L(5),
                                          L(6) is long rod ? (int)rod : null, D(7), D(8), L(9), L(10), D(11), loc, off);
                        if (e != null) events.Add(e);
                    }
                }
                if (events.Count == 0) { ss.LastEventId = top; ss.Save(); return; }
                byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { events });
                string ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                using var req = new HttpRequestMessage(HttpMethod.Post, url + "/api/v1/submit") { Content = new ByteArrayContent(body) };
                req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                req.Headers.Add("X-AFO-Id", ss.RemoteId); req.Headers.Add("X-AFO-Ts", ts); req.Headers.Add("X-AFO-Nonce", nonce); req.Headers.Add("X-AFO-Sig", Sign(key, ts, nonce, body));
                var resp = http.SendAsync(req).GetAwaiter().GetResult();
                string txt = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode) { ss.LastEventId = top; Note(ss, "отправлено " + events.Count + ": " + txt); }
                else if ((int)resp.StatusCode == 401 && txt.Contains("unknown_install")) { ss.RemoteId = null; Note(ss, "сервер не знает установку — повторная регистрация"); }
                else Note(ss, "ответ " + (int)resp.StatusCode + " " + txt);
            }
            catch (Exception ex) { _log?.Invoke("[SERVER] " + ex.Message); }
            finally { _busy = 0; }
        }

        private void Note(ServerSettings ss, string text)
        {
            ss.LastResult = text; ss.LastUtc = DateTime.UtcNow.ToString("o"); try { ss.Save(); } catch { }
            _log?.Invoke("[SERVER] " + text);
        }
    }

    /// <summary>SERVER-02: объединённый улов (со всех устройств, что шлют данные на сервер).</summary>
    public sealed class AfoCatch { public int CX, CZ; public uint? Fish; public int? Weight, Windex; public long Ts; public long ServerTs; public string Kind = "", Status = ""; }

    /// <summary>SERVER-02: чтение объединённых уловов — GET /api/v1/catches?waterbody=...&amp;days=...</summary>
    public static class AfoCatches
    {
        public static System.Collections.Generic.List<AfoCatch> Fetch(uint waterbody, int days = 30)
        {
            try
            {
                var ss = ServerSettings.Load();
                string url = (string.IsNullOrWhiteSpace(ss.Url) ? ServerUploader.DefaultUrl : ss.Url).TrimEnd('/');
                using var http = ServerUploader.Http();
                var resp = http.GetAsync(url + "/api/v1/catches?waterbody=" + waterbody.ToString(System.Globalization.CultureInfo.InvariantCulture) + "&days=" + days.ToString(System.Globalization.CultureInfo.InvariantCulture)).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode) return new System.Collections.Generic.List<AfoCatch>();
                using var doc = System.Text.Json.JsonDocument.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                var list = new System.Collections.Generic.List<AfoCatch>();
                if (doc.RootElement.TryGetProperty("catches", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var s in arr.EnumerateArray())
                    {
                        if (!s.TryGetProperty("cell", out var cell) || cell.ValueKind != System.Text.Json.JsonValueKind.Array || cell.GetArrayLength() < 2) continue;
                        var sp = new AfoCatch { CX = cell[0].GetInt32(), CZ = cell[1].GetInt32() };
                        if (s.TryGetProperty("fish", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.Number) sp.Fish = (uint)f.GetInt32();
                        if (s.TryGetProperty("weight", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.Number) sp.Weight = w.GetInt32();
                        if (s.TryGetProperty("windex", out var wi) && wi.ValueKind == System.Text.Json.JsonValueKind.Number) sp.Windex = wi.GetInt32();
                        if (s.TryGetProperty("ts", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.Number) sp.Ts = t.GetInt64();
                        if (s.TryGetProperty("server_ts", out var sts) && sts.ValueKind == System.Text.Json.JsonValueKind.Number) sp.ServerTs = sts.GetInt64();   // MAPS-FIX-01: время сервера
                        if (s.TryGetProperty("kind", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.String) sp.Kind = k.GetString() ?? "";
                        if (s.TryGetProperty("status", out var st) && st.ValueKind == System.Text.Json.JsonValueKind.String) sp.Status = st.GetString() ?? "";
                        list.Add(sp);
                    }
                return list;
            }
            catch { return new System.Collections.Generic.List<AfoCatch>(); }
        }
    }
}
