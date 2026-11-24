// NPCAP-02: Npcap is missing — offer to download the official installer from npcap.com and start it.
// The free Npcap licence forbids bundling it and silent install, so: download from the official site only, check the
// Authenticode signature (valid chain + signer «Nmap Software LLC»), then start the normal installer — the player clicks
// through it and confirms administrator rights. Nothing is installed without the player.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Windows.Forms;

namespace FisherLogger
{
    public static class NpcapSetup
    {
        public const string Page = "https://npcap.com/";
        public const string Signer = "Nmap Software LLC";
        private static readonly Regex Link = new(@"(?:https://npcap\.com/)?dist/npcap-(\d+(?:\.\d+){1,3})\.exe", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Newest installer link on the npcap.com page (absolute URL), or null. Pure (tested).</summary>
        public static string? LatestUrl(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            var best = Link.Matches(html).Cast<Match>()
                .Select(m => (v: Version.TryParse(m.Groups[1].Value, out var v) ? v : null, ver: m.Groups[1].Value))
                .Where(q => q.v != null).OrderByDescending(q => q.v).FirstOrDefault();
            return best.v == null ? null : "https://npcap.com/dist/npcap-" + best.ver + ".exe";
        }

        /// <summary>Downloads the newest installer into %TEMP%\FisherLogger and checks its signature. (path, null) or (null, error text for the player).</summary>
        public static async Task<(string? Path, string? Error)> DownloadAsync(IProgress<string>? progress = null, HttpClient? http = null, CancellationToken ct = default)
        {
            bool own = http == null; http ??= new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            try
            {
                if (!http.DefaultRequestHeaders.UserAgent.Any()) http.DefaultRequestHeaders.UserAgent.ParseAdd("FisherLogger-NpcapSetup");
                progress?.Report("Ищу последнюю версию на npcap.com…");
                string html = await http.GetStringAsync(Page, ct).ConfigureAwait(false);
                string? url = LatestUrl(html);
                if (url == null) return (null, "На странице npcap.com не нашлась ссылка на установщик.");
                string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FisherLogger", "npcap-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(new Uri(url).LocalPath));
                progress?.Report("Скачиваю " + System.IO.Path.GetFileName(file) + "…");
                using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    if (new Uri(resp.RequestMessage?.RequestUri?.ToString() ?? url).Host is not ("npcap.com" or "www.npcap.com"))
                        return (null, "Ссылка увела не на npcap.com — установка отменена.");
                    string tmp = file + ".part";
                    await using (var fs = File.Create(tmp)) await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
                    File.Move(tmp, file, true);
                }
                progress?.Report("Проверяю цифровую подпись…");
                var (ok, why) = CheckSignature(file);
                if (!ok) { try { File.Delete(file); } catch { } return (null, "Файл не прошёл проверку подписи (" + why + ") — установка отменена."); }
                return (file, null);
            }
            catch (OperationCanceledException) { return (null, "Скачивание отменено."); }
            catch (Exception ex) { return (null, "Не удалось скачать: " + ex.Message); }
            finally { if (own) http.Dispose(); }
        }

        /// <summary>Valid Authenticode signature (Windows trust check) and the signer is Nmap Software LLC.</summary>
        public static (bool ok, string why) CheckSignature(string file)
        {
            try
            {
                if (!Trusted(file)) return (false, "подпись недействительна");
                string subj;
#pragma warning disable SYSLIB0057
                using (var c = new X509Certificate2(X509Certificate.CreateFromSignedFile(file))) subj = c.Subject;
#pragma warning restore SYSLIB0057
                return subj.Contains("O=" + Signer, StringComparison.OrdinalIgnoreCase) || subj.Contains("CN=" + Signer, StringComparison.OrdinalIgnoreCase)
                    ? (true, subj) : (false, "подписан не Nmap: " + subj);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        /// <summary>Starts the official installer (the player sees it and confirms administrator rights); waits until it closes.</summary>
        public static async Task<bool> RunAsync(string installer)
        {
            try
            {
                // ui02_review5 #1: nobody may replace the file between the signature check and the start — hold it open
                // without write sharing, check the signature again, start it, keep the lock until the installer closes
                using (var hold = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var (ok, _) = CheckSignature(installer);
                    if (!ok) return false;
                    using var p = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true, Verb = "runas" });
                    if (p == null) return false;
                    await p.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch (System.ComponentModel.Win32Exception) { return false; }   // the player declined administrator rights
            finally
            {   // ui02_review5 #9: no installer left in %TEMP%
                try { File.Delete(installer); var dir = System.IO.Path.GetDirectoryName(installer); if (dir != null && Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir); } catch { }
            }
            for (int i = 0; i < 20 && !NpcapCheck.IsInstalled(); i++) await Task.Delay(250).ConfigureAwait(false);
            return NpcapCheck.IsInstalled();
        }

        // ---- WinVerifyTrust (whole-file Authenticode check, revocation of the whole chain) ----
        private static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo { public uint cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData; public uint dwUIChoice; public uint fdwRevocationChecks;
            public uint dwUnionChoice; public IntPtr pFile; public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference;
            public uint dwProvFlags; public uint dwUIContext; public IntPtr pSignatureSettings;
        }
        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WinTrustData pWVTData);

        private static bool Trusted(string file)
        {
            var fi = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = file };
            IntPtr pFi = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            try
            {
                Marshal.StructureToPtr(fi, pFi, false);
                var d = new WinTrustData
                {
                    cbStruct = (uint)Marshal.SizeOf<WinTrustData>(), dwUIChoice = 2 /*WTD_UI_NONE*/, fdwRevocationChecks = 0 /*WTD_REVOKE_NONE: no false «invalid» offline*/,
                    dwUnionChoice = 1 /*WTD_CHOICE_FILE*/, pFile = pFi, dwStateAction = 0, dwProvFlags = 0
                };
                return WinVerifyTrust(new IntPtr(-1), ActionGenericVerifyV2, ref d) == 0;
            }
            finally { Marshal.FreeHGlobal(pFi); }
        }
    }

    /// <summary>The window «Нужен Npcap»: download and install automatically / open the site / later.</summary>
    public sealed class NpcapForm : Form
    {
        private readonly Label _text = new() { AutoSize = false, Dock = DockStyle.Top, Height = 146, Padding = new Padding(18, 16, 18, 0) };
        private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Top, Height = 46, Padding = new Padding(18, 6, 18, 0) };
        private readonly Button _auto = new() { Text = "Скачать и установить", Width = 210, Height = 34 };
        private readonly Button _site = new() { Text = "Открыть сайт", Width = 140, Height = 34 };
        private readonly Button _later = new() { Text = "Позже", Width = 100, Height = 34, DialogResult = DialogResult.Cancel };
        private readonly CancellationTokenSource _cts = new();
        /// <summary>Npcap is installed now (the window closes itself).</summary>
        public bool Installed { get; private set; }
        /// <summary>For the test: replaces the real download + install.</summary>
        public Func<IProgress<string>, CancellationToken, Task<(bool ok, string? error)>>? Runner;

        public NpcapForm()
        {
            Text = "Нужен Npcap"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(580, 262); Font = new Font("Segoe UI", 10f); ShowInTaskbar = true;
            BackColor = NuTheme.Ground; ForeColor = NuTheme.Text; _status.ForeColor = NuTheme.AccentText;
            _text.Text = "Для работы программе нужен бесплатный драйвер Npcap — через него она читает данные игры (только читает, в игру ничего не отправляет).\n\n" +
                         "Программа может сама скачать официальный установщик с npcap.com и запустить его: нажмите «Далее» и «Установить» с настройками по умолчанию и подтвердите права администратора.";
            _status.Text = "";
            var row = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 10, 12, 10), BackColor = NuTheme.Ground };
            row.Controls.AddRange(new Control[] { _auto, _site, _later });
            Theme.StyleButton(_auto, Theme.ButtonPrimary); Theme.StyleButton(_site, Theme.ButtonSecondary); Theme.StyleButton(_later, Theme.ButtonNeutral);
            Controls.Add(_status); Controls.Add(_text); Controls.Add(row);
            AcceptButton = _auto; CancelButton = _later;
            _site.Click += (s, e) => { try { Process.Start(new ProcessStartInfo(NpcapCheck.DownloadUrl) { UseShellExecute = true }); } catch { } };
            _auto.Click += async (s, e) => await Auto();
            FormClosing += (s, e) => { try { _cts.Cancel(); } catch { } };
        }

        private async Task Auto()
        {
            _auto.Enabled = _site.Enabled = false; _later.Text = "Отмена";
            var prog = new Progress<string>(t => { if (!IsDisposed) _status.Text = t; });
            var run = Runner ?? (async (p, ct) =>
            {
                var (path, err) = await NpcapSetup.DownloadAsync(p, null, ct);
                if (path == null) return (false, err);
                p.Report("Открылся установщик Npcap — нажмите «Далее» и «Установить» с настройками по умолчанию.");
                return await NpcapSetup.RunAsync(path) ? (true, (string?)null) : (false, "Npcap не установлен (установщик закрыт или права администратора не подтверждены).");
            });
            (bool ok, string? error) r;
            try { r = await run(prog, _cts.Token); } catch (Exception ex) { r = (false, ex.Message); }
            if (IsDisposed) return;
            if (r.ok) { Installed = true; _status.ForeColor = NuTheme.Ok; _status.Text = "Npcap установлен — запускаю запись. Если в окне программы не появится «Запись идёт», перезапустите программу."; await Task.Delay(2500); if (!IsDisposed) { DialogResult = DialogResult.OK; Close(); } return; }
            _status.ForeColor = NuTheme.Danger; _status.Text = (r.error ?? "Не получилось.") + " Можно установить вручную: «Открыть сайт».";
            _auto.Enabled = _site.Enabled = true; _auto.Text = "Попробовать ещё раз"; _later.Text = "Позже";
        }

        protected override void Dispose(bool disposing) { if (disposing) _cts.Dispose(); base.Dispose(disposing); }
    }
}
