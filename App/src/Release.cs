using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FisherLogger
{
    /// <summary>RELEASE-02: версия программы, данные установки, настройки сервера и обновлений, проверка Npcap.
    /// Всё хранится в %LOCALAPPDATA%\FisherLogger (как app_settings.json). Никаких ников, номеров игрока и ключей.</summary>
    public static class AppInfo
    {
        public static string Version
        {
            get
            {
                var a = typeof(AppInfo).Assembly;
                string? v = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (string.IsNullOrWhiteSpace(v)) v = a.GetName().Version?.ToString(3) ?? "0.0.0";
                int plus = v.IndexOf('+'); return plus > 0 ? v.Substring(0, plus) : v;
            }
        }
        public static string UserDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FisherLogger");
        public const string DevSessionDir = @"D:\TheFisherProject\Sessions\default";
        /// <summary>Рабочая папка (база, журналы): на ПК разработчика — прежняя, на остальных — в профиле пользователя.</summary>
        public static string DefaultSessionDir(string? devDir = DevSessionDir, string? userDir = null)
            => devDir != null && Directory.Exists(devDir) ? devDir : Path.Combine(userDir ?? UserDir, "session");
    }

    internal static class JsonFile
    {
        public static readonly JsonSerializerOptions Opt = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
        public static T Load<T>(string path) where T : new()
        {
            try { if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Opt) ?? new T(); } catch { }
            return new T();
        }
        public static void Save<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(value, Opt), new UTF8Encoding(false));
            File.Move(tmp, path, true);
        }
    }

    /// <summary>Установка: случайный номер (вместо имени игрока для будущего сервера) и запомненный адаптер захвата.</summary>
    public sealed class InstallInfo
    {
        public string InstallId { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public string? CaptureAdapterGuid { get; set; }
        public string? CaptureAdapterName { get; set; }
        public bool ShortcutsCreated { get; set; }
        public static string? PathOverride { get; set; }
        public static string FilePath => PathOverride ?? Path.Combine(AppInfo.UserDir, "install.json");
        public static InstallInfo LoadOrCreate()
        {
            var i = JsonFile.Load<InstallInfo>(FilePath);
            if (!Guid.TryParse(i.InstallId, out _))
            {
                i.InstallId = Guid.NewGuid().ToString("D");
                i.CreatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                try { i.Save(); } catch { }
            }
            return i;
        }
        public void Save() => JsonFile.Save(FilePath, this);
    }

    /// <summary>Обмен данными с сервером. FEAT-07: по решению владельца включён по умолчанию — условия показываются при установке
    /// (окно выбора языка) и в README; выключается в «Обмен данными с сервером…». Отправляются только новые события.</summary>
    public sealed class ServerSettings
    {
        public bool Enabled { get; set; } = true;
        public string Url { get; set; } = ServerUploader.DefaultUrl;
        /// <summary>SERVER-01: номер установки на сервере (= отпечаток ключа), последнее отправленное событие текущей базы, итог.</summary>
        public string? RemoteId { get; set; }
        public long LastEventId { get; set; }
        public string? LastDb { get; set; }
        public string? LastResult { get; set; }
        public string? LastUtc { get; set; }
        public bool Consent { get; set; } = true;   // FEAT-07: принято условиями использования при установке
        public string? ConsentUtc { get; set; }
        /// <summary>FEAT-07: пользователь видел условия обмена данными (при установке или один раз после обновления).</summary>
        public bool TermsShown { get; set; }
        public static string? PathOverride { get; set; }
        public static string FilePath => PathOverride ?? Path.Combine(AppInfo.UserDir, "server.json");
        public static ServerSettings Load() => JsonFile.Load<ServerSettings>(FilePath);
        public void Save() => JsonFile.Save(FilePath, this);
        /// <summary>Отправлять можно только при включении, согласии и адресе https.</summary>
        [JsonIgnore]
        public bool CanSend => Enabled && Consent && Uri.TryCreate(Url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;
    }

    public sealed class UpdateSettings
    {
        /// <summary>owner/repo на GitHub; пусто — обновления не проверяются.</summary>
        public string Repo { get; set; } = DefaultRepo;
        public bool AutoCheck { get; set; } = true;
        public string? LastCheckUtc { get; set; }
        public string? SkipVersion { get; set; }
        public const string DefaultRepo = "dmitriev02-eng/analyzer-fisher-online";   // RELEASE-02: заполнить после создания репозитория (owner/repo)
        public static string? PathOverride { get; set; }
        public static string FilePath => PathOverride ?? Path.Combine(AppInfo.UserDir, "update.json");
        public static UpdateSettings Load() { var s = JsonFile.Load<UpdateSettings>(FilePath); if (string.IsNullOrWhiteSpace(s.Repo)) s.Repo = DefaultRepo; return s; }
        public void Save() => JsonFile.Save(FilePath, this);
        public bool DueForCheck(DateTime nowUtc)
            => AutoCheck && !string.IsNullOrWhiteSpace(Repo) &&
               (!DateTime.TryParse(LastCheckUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var last) || (nowUtc - last).TotalHours >= 20);
    }

    public sealed class ReleaseInfo
    {
        public string Tag { get; init; } = "";
        public string Version { get; init; } = "";
        public string Notes { get; init; } = "";
        public string? ZipUrl { get; init; }
        public string? ZipName { get; init; }
        public string? SumsUrl { get; init; }
    }

    /// <summary>RELEASE-02: обновление через GitHub Releases (публичный репозиторий, без ключей).
    /// Выпуск содержит FisherLogger-&lt;версия&gt;-win-x64.zip и SHA256SUMS.txt. Архив без совпавшей суммы не ставится.</summary>
    public static class Updater
    {
        public const string ZipPrefix = "AnalyzerFisherOnline-", ZipSuffix = "-win-x64.zip", SumsName = "SHA256SUMS.txt", ExeName = "FisherLogger95.exe";

        public static int[] ParseVersion(string v)
        {
            v = (v ?? "").Trim().TrimStart('v', 'V'); int dash = v.IndexOf('-'); if (dash >= 0) v = v.Substring(0, dash);
            var p = v.Split('.'); var r = new int[3];
            for (int i = 0; i < 3; i++) r[i] = i < p.Length && int.TryParse(p[i], NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;
            return r;
        }
        public static int Compare(string a, string b)
        {
            var x = ParseVersion(a); var y = ParseVersion(b);
            for (int i = 0; i < 3; i++) if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            return 0;
        }

        /// <summary>Разбор ответа GET /repos/{repo}/releases/latest. Черновики и пред-выпуски не берём.</summary>
        public static ReleaseInfo? ParseLatest(string json)
        {
            using var d = JsonDocument.Parse(json); var r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("tag_name", out var tagE)) return null;
            if (r.TryGetProperty("draft", out var dr) && dr.ValueKind == JsonValueKind.True) return null;
            if (r.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True) return null;
            string tag = tagE.GetString() ?? ""; string? zip = null, zipName = null, sums = null;
            if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    string n = a.TryGetProperty("name", out var ne) ? ne.GetString() ?? "" : "";
                    string? u = a.TryGetProperty("browser_download_url", out var ue) ? ue.GetString() : null;
                    if (n.StartsWith(ZipPrefix, StringComparison.OrdinalIgnoreCase) && n.EndsWith(ZipSuffix, StringComparison.OrdinalIgnoreCase)) { zip = u; zipName = n; }
                    else if (string.Equals(n, SumsName, StringComparison.OrdinalIgnoreCase)) sums = u;
                }
            return new ReleaseInfo { Tag = tag, Version = tag.TrimStart('v', 'V'), Notes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "", ZipUrl = zip, ZipName = zipName, SumsUrl = sums };
        }

        /// <summary>Сумма для файла из SHA256SUMS.txt (строки «HASH  имя» или «HASH *имя»).</summary>
        public static string? HashFor(string sums, string fileName)
        {
            foreach (var line in sums.Split('\n'))
            {
                var t = line.Trim(); if (t.Length < 66) continue;
                string h = t.Substring(0, 64), n = t.Substring(64).Trim().TrimStart('*').Trim();
                if (string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase) && h.All(Uri.IsHexDigit)) return h.ToUpperInvariant();
            }
            return null;
        }

        public static string Sha256(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)); }

        public static HttpClient Http()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("FisherLogger/" + AppInfo.Version);
            h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return h;
        }

        /// <summary>Есть ли версия новее текущей. Release == null — нет новой или проверить не удалось (тогда Error заполнен).</summary>
        public static async Task<(ReleaseInfo? Release, string? Error)> CheckAsync(string repo, string currentVersion, HttpClient? http = null, string apiBase = "https://api.github.com", CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(repo) || repo.Trim().Count(c => c == '/') != 1) return (null, "репозиторий обновлений не задан");
            var h = http ?? Http();
            try
            {
                using var resp = await h.GetAsync(apiBase.TrimEnd('/') + "/repos/" + repo.Trim() + "/releases/latest", ct).ConfigureAwait(false);
                if ((int)resp.StatusCode == 404) return (null, null);   // выпусков ещё нет
                if (!resp.IsSuccessStatusCode) return (null, "GitHub ответил " + (int)resp.StatusCode);
                var rel = ParseLatest(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (rel == null || rel.ZipUrl == null || rel.SumsUrl == null) return (null, null);
                return Compare(rel.Version, currentVersion) > 0 ? (rel, null) : (null, null);
            }
            catch (Exception ex) { return (null, ex.Message); }
            finally { if (http == null) h.Dispose(); }
        }

        /// <summary>Скачать, сверить SHA-256, распаковать в updates\&lt;версия&gt;. Возвращает папку с exe или ошибку.</summary>
        public static async Task<(string? Dir, string? Error)> DownloadAsync(ReleaseInfo rel, string? updatesRoot = null, HttpClient? http = null, CancellationToken ct = default)
        {
            if (rel.ZipUrl == null || rel.SumsUrl == null || rel.ZipName == null) return (null, "в выпуске нет архива или контрольных сумм");
            if (rel.ZipName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return (null, "недопустимое имя архива");
            string root = updatesRoot ?? Path.Combine(AppInfo.UserDir, "updates"); Directory.CreateDirectory(root);
            string zip = Path.Combine(root, rel.ZipName), dir = Path.Combine(root, "v" + string.Join(".", ParseVersion(rel.Version)));
            var h = http ?? Http();
            try
            {
                string sums = await h.GetStringAsync(rel.SumsUrl, ct).ConfigureAwait(false);
                string? want = HashFor(sums, rel.ZipName); if (want == null) return (null, "в SHA256SUMS.txt нет суммы для " + rel.ZipName);
                using (var s = await h.GetStreamAsync(rel.ZipUrl, ct).ConfigureAwait(false)) using (var f = File.Create(zip)) await s.CopyToAsync(f, ct).ConfigureAwait(false);
                string got = Sha256(zip);
                if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase)) { File.Delete(zip); return (null, "контрольная сумма не совпала — обновление отменено"); }
                return Extract(zip, dir);
            }
            catch (Exception ex) { return (null, ex.Message); }
            finally { if (http == null) h.Dispose(); }
        }

        /// <summary>Распаковка с проверкой путей (ничего вне папки) и наличия exe.</summary>
        public static (string? Dir, string? Error) Extract(string zip, string dir)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir); string full = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
            using (var a = ZipFile.OpenRead(zip))
                foreach (var e in a.Entries)
                {
                    string dest = Path.GetFullPath(Path.Combine(dir, e.FullName));
                    if (!dest.StartsWith(full, StringComparison.OrdinalIgnoreCase)) return (null, "небезопасный путь в архиве: " + e.FullName);
                    if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(dest); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!); e.ExtractToFile(dest, true);
                }
            string root = Directory.GetFiles(dir, ExeName, SearchOption.AllDirectories).Select(p => Path.GetDirectoryName(p) ?? "").OrderBy(p => p.Length).FirstOrDefault() ?? "";
            return root.Length == 0 ? (null, "в архиве нет " + ExeName) : (root, null);
        }

        /// <summary>Скрипт замены: ждёт выхода программы, сохраняет прежние файлы в updates\backup_&lt;версия&gt;, копирует новую версию
        /// поверх папки программы (данные пользователя лежат в профиле и не трогаются), при ошибке возвращает прежние, запускает программу.</summary>
        public static string WriteApplyScript(string newDir, string appDir, int pid, string oldVersion, string? updatesRoot = null, bool restart = true)
        {
            string root = updatesRoot ?? Path.Combine(AppInfo.UserDir, "updates"); Directory.CreateDirectory(root);
            string backup = Path.Combine(root, "backup_" + oldVersion), log = Path.Combine(root, "apply.log"), cmd = Path.Combine(root, "apply_update.cmd");
            appDir = appDir.TrimEnd('\\'); newDir = newDir.TrimEnd('\\');
            string exe = Path.Combine(appDir, ExeName);
            var sb = new StringBuilder();
            sb.AppendLine("@echo off").AppendLine("chcp 65001 >nul");
            sb.AppendLine(":wait").AppendLine($"tasklist /FI \"PID eq {pid}\" | find \" {pid} \" >nul && (timeout /t 1 /nobreak >nul & goto wait)");
            sb.AppendLine($"echo %date% %time% update from {oldVersion} >> \"{log}\"");
            sb.AppendLine($"robocopy \"{appDir}\" \"{backup}\" /E /NFL /NDL /NJH /NJS /R:2 /W:1 >> \"{log}\"");
            sb.AppendLine($"robocopy \"{newDir}\" \"{appDir}\" /E /NFL /NDL /NJH /NJS /R:5 /W:1 >> \"{log}\"");
            sb.AppendLine("if %ERRORLEVEL% GEQ 8 (");
            sb.AppendLine($"  echo FAILED, restoring >> \"{log}\"");
            sb.AppendLine($"  robocopy \"{backup}\" \"{appDir}\" /E /NFL /NDL /NJH /NJS /R:5 /W:1 >> \"{log}\"");
            sb.AppendLine(") else (");
            sb.AppendLine($"  echo OK >> \"{log}\"");
            sb.AppendLine(")");
            if (restart) sb.AppendLine($"start \"\" \"{exe}\"");
            File.WriteAllText(cmd, sb.ToString(), new UTF8Encoding(false));
            return cmd;
        }

        public static Process? RunApplyScript(string cmd)
            => Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + cmd + "\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
    }

    /// <summary>Npcap нужен для чтения трафика; класть его в архив нельзя (лицензия бесплатной версии) — только ссылка на официальный сайт.</summary>
    public static class NpcapCheck
    {
        public const string DownloadUrl = "https://npcap.com/#download";
        public static bool IsInstalled()
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return File.Exists(Path.Combine(sys, "Npcap", "wpcap.dll")) || File.Exists(Path.Combine(sys, "wpcap.dll"));
        }
    }

    /// <summary>RELEASE-03: ярлыки «Анализатор Fisher Online» на рабочем столе и в меню Пуск — один раз, только у установленной копии
    /// (не на ПК разработчика и не из папки сборки). Пользователь может удалить ярлык — повторно не создаём.</summary>
    public static class Shortcuts
    {
        public const string Title = "Анализатор Fisher Online";
        public static bool IsInstalledCopy(string exeDir)
            => !exeDir.StartsWith(@"D:\TheFisherProject\", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(exeDir, "data", "baits_map.json"));
        public static string[] Targets() => new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Title + ".lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Title + ".lnk"),
        };
        public static void Create(string lnk, string exe)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell недоступен");
            dynamic sh = Activator.CreateInstance(t)!;
            dynamic sc = sh.CreateShortcut(lnk);
            sc.TargetPath = exe; sc.WorkingDirectory = Path.GetDirectoryName(exe); sc.IconLocation = exe + ",0"; sc.Description = Title;
            sc.Save();
        }
        /// <summary>Создать ярлыки при первом запуске установленной копии. Возвращает текст для журнала или null.</summary>
        public static string? EnsureOnce(string exePath)
        {
            string dir = Path.GetDirectoryName(exePath) ?? "";
            if (!IsInstalledCopy(dir)) return null;
            var inst = InstallInfo.LoadOrCreate();
            if (inst.ShortcutsCreated) return null;
            var made = new List<string>();
            foreach (var l in Targets()) { try { Directory.CreateDirectory(Path.GetDirectoryName(l)!); Create(l, exePath); made.Add(l); } catch { } }
            inst.ShortcutsCreated = true; try { inst.Save(); } catch { }
            return made.Count > 0 ? "ярлыки созданы: " + string.Join("; ", made) : "ярлыки создать не удалось";
        }
    }

    /// <summary>RELEASE-02: запомненный адаптер — запасной путь, если маршрут Windows к серверу не определился (VPN, сбой).</summary>
    public static class CaptureMemory
    {
        public static CaptureCandidate? Remembered(IReadOnlyList<CaptureCandidate> candidates, string? guid)
        {
            if (string.IsNullOrWhiteSpace(guid)) return null;
            var m = candidates.Where(c => string.Equals(CaptureInterfaceSelector.GuidOf(c.Name), guid.Trim().ToUpperInvariant(), StringComparison.Ordinal)).ToList();
            return m.Count == 1 ? m[0] : null;
        }
    }
}
