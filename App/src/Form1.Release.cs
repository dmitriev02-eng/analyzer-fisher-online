using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FisherLogger
{
    /// <summary>RELEASE-02: «О программе и обновления», автопроверка обновлений на GitHub, предупреждение о Npcap.</summary>
    public partial class Form1
    {
        private bool _updateCheckStarted, _npcapWarned;

        /// <summary>Npcap не установлен — один раз за запуск объяснить и предложить открыть официальный сайт.</summary>
        private bool EnsureNpcap()
        {
            if (NpcapCheck.IsInstalled()) return true;
            Log("ЗАХВАТ НЕ ЗАПУЩЕН: не установлен Npcap (" + NpcapCheck.DownloadUrl + ")");
            if (_npcapWarned) return false;
            _npcapWarned = true;
            void Ask()
            {
                if (UiText.Box(this, "Для работы программе нужен бесплатный драйвер Npcap — через него она читает данные игры.\n\n" +
                        "Открыть официальный сайт npcap.com? Установите Npcap с настройками по умолчанию и перезапустите программу.",
                        "Нужен Npcap", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    try { Process.Start(new ProcessStartInfo(NpcapCheck.DownloadUrl) { UseShellExecute = true }); } catch { }
            }
            try { if (IsHandleCreated) BeginInvoke((Action)Ask); else Ask(); } catch { }
            return false;
        }

        private void RememberCaptureAdapter(CaptureCandidate dev)
        {
            try
            {
                var inst = InstallInfo.LoadOrCreate();
                string g = CaptureInterfaceSelector.GuidOf(dev.Name);
                if (g.Length > 0 && inst.CaptureAdapterGuid != g) { inst.CaptureAdapterGuid = g; inst.CaptureAdapterName = dev.FriendlyName.Length > 0 ? dev.FriendlyName : dev.Description; inst.Save(); }
            }
            catch { }
        }

        private void StartAutoUpdateCheck()
        {
            if (_updateCheckStarted) return; _updateCheckStarted = true;
            try { if (Shortcuts.EnsureOnce(Application.ExecutablePath) is string sc) Log("[INSTALL] " + sc); } catch { }   // RELEASE-03
            var us = UpdateSettings.Load();
            if (!us.DueForCheck(DateTime.UtcNow)) return;
            _ = Task.Run(async () =>
            {
                var (rel, err) = await Updater.CheckAsync(us.Repo, AppInfo.Version);
                us.LastCheckUtc = DateTime.UtcNow.ToString("o"); try { us.Save(); } catch { }
                if (err != null) { Log("[UPDATE] проверка не удалась: " + err); return; }
                if (rel == null || rel.Version == us.SkipVersion) return;
                Log("[UPDATE] доступна версия " + rel.Version);
                try { BeginInvoke((Action)(() => OfferUpdate(rel, auto: true))); } catch { }
            });
        }

        private void OfferUpdate(ReleaseInfo rel, bool auto)
        {
            string notes = string.IsNullOrWhiteSpace(rel.Notes) ? "" : "\n\nЧто нового:\n" + (rel.Notes.Length > 600 ? rel.Notes.Substring(0, 600) + "…" : rel.Notes);
            var r = UiText.Box(this, "Доступна новая версия " + rel.Version + " (сейчас " + AppInfo.Version + ")." + notes +
                "\n\nОбновить сейчас? Программа закроется на несколько секунд и запустится снова. Ваши уловы и настройки сохранятся." +
                (auto ? "\n\n«Нет» — напомнить позже, «Отмена» — пропустить эту версию." : ""),
                "Обновление", auto ? MessageBoxButtons.YesNoCancel : MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Cancel) { var us = UpdateSettings.Load(); us.SkipVersion = rel.Version; try { us.Save(); } catch { } return; }
            if (r != DialogResult.Yes) return;
            _ = Task.Run(async () =>
            {
                Log("[UPDATE] загрузка " + rel.ZipName);
                var (dir, err) = await Updater.DownloadAsync(rel);
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (dir == null) { UiText.Box(this, "Не удалось обновить: " + err, "Обновление", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                        string cmd = Updater.WriteApplyScript(dir, AppContext.BaseDirectory, Environment.ProcessId, AppInfo.Version);
                        Log("[UPDATE] версия " + rel.Version + " проверена (SHA-256), перезапуск");
                        Updater.RunApplyScript(cmd);
                        Application.Exit();
                    }));
                }
                catch { }
            });
        }

        private void ChooseLanguage()
        {
            using var lf = new LanguageForm();
            if (lf.ShowDialog(this) != DialogResult.OK || lf.Choice == UiText.Lang) return;
            UiText.Set(lf.Choice);
            UiText.Box(this, lf.Choice == "en" ? "Language changed. Restart the program to apply." : "Язык изменён. Перезапустите программу.", "Анализатор Fisher Online", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowAboutAndUpdates()
        {
            var inst = InstallInfo.LoadOrCreate(); var us = UpdateSettings.Load(); var ss = ServerSettings.Load();
            string text = Shortcuts.Title + " (FisherLogger) " + AppInfo.Version + "\n\n" +
                          "Номер установки: " + inst.InstallId + "\n(случайный, вместо имени — для будущего обмена данными)\n\n" +
                          "Адаптер: " + (inst.CaptureAdapterName ?? "определяется автоматически") + "\n" +
                          "Npcap: " + (NpcapCheck.IsInstalled() ? "установлен" : "НЕ установлен") + "\n" +
                          "Обмен данными с сервером: " + (ss.CanSend ? "включён" : "выключен") + "\n" +
                          "Обновления: " + (string.IsNullOrWhiteSpace(us.Repo) ? "источник не задан" : "github.com/" + us.Repo) + "\n\n" +
                          "Проверить обновления сейчас?";
            if (UiText.Box(this, text, "О программе", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
            _ = Task.Run(async () =>
            {
                var (rel, err) = await Updater.CheckAsync(us.Repo, AppInfo.Version);
                us.LastCheckUtc = DateTime.UtcNow.ToString("o"); try { us.Save(); } catch { }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (rel != null) OfferUpdate(rel, auto: false);
                        else UiText.Box(this, err != null ? "Не удалось проверить: " + err : "У вас последняя версия.", "Обновления", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                }
                catch { }
            });
        }
    }
}
