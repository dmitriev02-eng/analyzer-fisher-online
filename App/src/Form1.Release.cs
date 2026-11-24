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
        private ServerUploader? _uploader;   // SERVER-01

        private void StartUploader()
        {
            if (_uploader != null) return;
            _uploader = new ServerUploader(() => AppPaths.CatchDb, () => _currentBaseId.HasValue && _currentBaseId.Value >= 0 ? (uint)_currentBaseId.Value : null, () => _serverOffset, Log);
            _uploader.Start();
        }

        /// <summary>FEAT-07: условия обмена данными — один раз. Новая установка видела их в окне выбора языка; после обновления
        /// с прежней версии — короткое окно «оставить включённым?».</summary>
        private void ShowTermsOnce()
        {
            var ss = ServerSettings.Load();
            if (ss.TermsShown) return;
            if (!LanguageForm.TermsJustShown)
            {
                var r = UiText.Box(this, "Новое в этой версии: программа отправляет на сервер проекта ваши новые поклёвки и уловы, " +
                    "чтобы собирать общие места клёва.\n\nОтправляется: вид, вес и класс рыбы, водоём, клетка 5 м, игровое время, удочка, наживка, шанс.\n" +
                    "Не отправляется: ник, чат, учётная запись, прошлая история.\n\nОставить обмен данными включённым? " +
                    "Изменить можно в любой момент: «Для разработчика ▾» → «Обмен данными с сервером…».", "Обмен данными с сервером", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (r == DialogResult.No) { ss.Enabled = false; ss.Consent = false; }
            }
            ss.TermsShown = true; ss.ConsentUtc ??= ss.Consent ? DateTime.UtcNow.ToString("o") : null;
            try { ss.Save(); } catch { }
            Log("[SERVER] обмен данными " + (ss.CanSend ? "включён (условия использования)" : "выключен пользователем"));
        }

        /// <summary>SERVER-01: согласие на обмен данными — что уходит, что никогда, как выключить.</summary>
        private void ShowServerConsent()
        {
            var ss = ServerSettings.Load();
            string state = ss.CanSend ? "Сейчас: ВКЛЮЧЕНО" + (ss.LastResult != null ? " (последний обмен: " + ss.LastResult + ")" : "") : "Сейчас: выключено";
            string text = "Обмен данными помогает всем: из поклёвок многих игроков сервер собирает общие места клёва.\n\n" +
                          "Отправляется: вид, вес и класс рыбы, водоём, клетка 5 м, игровое время, номер удочки, наживка и крючок, шанс клёва.\n" +
                          "Никогда не отправляется: ник, номер игрока, чат, сведения о других игроках, учётная запись.\n" +
                          "Вместо имени — случайный ключ этой установки. История до включения не отправляется.\n" +
                          "Обмен включается сам по условиям использования, которые показываются при установке (README → «Обмен данными»).\n\n" + state + "\n\n" +
                          (ss.CanSend ? "Выключить обмен данными?" : "Включить обмен данными?");
            if (UiText.Box(this, text, "Обмен данными с сервером", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            ss.Enabled = ss.Consent = !ss.CanSend;
            ss.ConsentUtc = ss.Consent ? DateTime.UtcNow.ToString("o") : null;
            if (string.IsNullOrWhiteSpace(ss.Url)) ss.Url = ServerUploader.DefaultUrl;
            try { ss.Save(); } catch { }
            Log("[SERVER] обмен данными " + (ss.CanSend ? "включён" : "выключен"));
            if (ss.CanSend) StartUploader();
        }

        /// <summary>Npcap не установлен — один раз за запуск объяснить и предложить открыть официальный сайт.</summary>
        private bool EnsureNpcap()
        {
            if (NpcapCheck.IsInstalled()) return true;
            Log("ЗАХВАТ НЕ ЗАПУЩЕН: не установлен Npcap (" + NpcapCheck.DownloadUrl + ")");
            if (_npcapWarned) return false;
            _npcapWarned = true;
            void Ask()
            {
                // NPCAP-02: offer to download the official installer and start it; after a successful install start recording right away
                using var f = new NpcapForm();
                f.ShowDialog();
                if (!f.Installed) { Log("[NPCAP] не установлен — окно закрыто"); return; }
                Log("[NPCAP] установлен — запускаю запись");
                _npcapWarned = false;
                try { StartSniffer(); } catch (Exception ex) { Log("[NPCAP] запись не запущена: " + ex.Message + " — перезапустите программу"); }
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
            try { if (!_suppressPersistence) { ShowTermsOnce(); StartUploader(); } } catch { }   // FEAT-07: шлёт по условиям использования; выключается в меню
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
            if (!CanWriteDir(AppContext.BaseDirectory))   // FEAT-08: Program Files и другие закрытые папки
            {
                UiText.Box(this, "Программа лежит в папке, куда нельзя записывать без прав администратора (например, Program Files):\n" +
                    AppContext.BaseDirectory + "\n\nПеренесите папку программы, например в C:\\Games\\AnalyzerFO, и запустите её оттуда — " +
                    "или скачайте новую версию вручную со страницы выпуска.", "Обновление", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
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

        private static bool CanWriteDir(string dir)
        {
            try { string t = System.IO.Path.Combine(dir, ".write_test_" + Guid.NewGuid().ToString("N")); System.IO.File.WriteAllText(t, ""); System.IO.File.Delete(t); return true; }
            catch { return false; }
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
