// UI-01 / FEAT-11…15: Form1 drives the new window and the new in-game minimap. The old main window is no longer shown
// (only as a service window from the tray: import, program log); Form1 keeps doing all the recording work hidden.
using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace FisherLogger
{
    public partial class Form1
    {
        private NuShellForm? _nuShell;
        private bool NewUiOn => true;   // FEAT-15: the new interface is the only one

        // «Запись идёт» only while packets from the game really arrive (last 60 s)
        private int _nuLastPk = -1; private DateTime _nuLastPkAt = DateTime.MinValue;
        private bool NuReceiving()
        {
            int n = Volatile.Read(ref totalPackets);
            if (n != _nuLastPk) { _nuLastPk = n; _nuLastPkAt = DateTime.Now; }
            return n > 0 && (DateTime.Now - _nuLastPkAt).TotalSeconds <= 60;
        }

        /// <summary>Opens (or brings forward) the new window. If it cannot be built, the service window is shown instead (never no window).</summary>
        internal void ShowNuShell()
        {
            try
            {
                if (_nuShell == null || _nuShell.IsDisposed)
                {
                    var shell = new NuShellForm
                    {
                        WaterName = () => { try { return _currentBaseId is int id && id >= 0 ? AdvisorLocName((uint)id) ?? "" : ""; } catch { return ""; } },
                        Recording = NuReceiving,
                        MiniMapShown = () => { try { return _overlay?.IsVisible ?? false; } catch { return false; } },
                        ToggleMiniMap = () => _overlay?.Toggle(),
                        ExitApp = ExitApp,
                        OpenLegacy = OpenLegacyFromNu,
                    };
                    // ui01_review3 #16 / #15: ✕ and «свернуть» hide like the old window: the game gets the focus back, a hint once
                    shell.HideToTray = () => { if (_tray != null) _tray.HideForm(shell, returnFocus: true); else shell.Hide(); };
                    shell.MinimizeToTray = () => (_appSettings.Window ??= new WindowSettings()).MinimizeToTray;
                    shell.MiniKeyText = () => { try { var os = _overlay?.Settings; return os != null && os.HotkeyEnabled && os.HotkeyKey != 0 ? OverlaySettings.HotkeyText(os.HotkeyKey, os.HotkeyModifiers) : ""; } catch { return "\\"; } };
                    shell.SettingsHost.Log = shell.PhoneHost.Log = shell.AiHost.Log = Log;
                    shell.SettingsHost.Closed = () => shell.ShowMap();   // ui01_review3 #33: «Сохранить» leaves the settings page
                    try { NuMapInit(shell); }
                    catch { shell.Dispose(); throw; }
                    // UI-02-6: «Карта любого водоёма» — заполнить список водоёмов + переписывать карту при выборе.
                    shell.MapPickChanged += () => NuMapTick(null, EventArgs.Empty);
                    try { shell.SetWaterList(NuWaterList()); } catch (Exception ex) { Log("[NEWUI] список водоёмов: " + ex.Message); }
                    // MAPS-FIX-01: имя рыбы для фильтра объединённых уловов.
                    shell.MapView.FishName = id => { try { return _deepParser.GetFish(id)?.Name ?? ""; } catch { return ""; } };
                    _nuShell = shell;
                }
                if (!_nuShell.Visible) _nuShell.Show();
                if (_nuShell.WindowState == FormWindowState.Minimized) _nuShell.WindowState = FormWindowState.Normal;
                _nuShell.TopMost = true; _nuShell.TopMost = false;
                _nuShell.Activate();
            }
            catch (Exception ex)
            {
                Log("[NEWUI] окно не открыто: " + ex.Message);
                try { _nuShell?.Dispose(); } catch { }
                _nuShell = null;
                _tray?.ShowLegacyMain();   // never leave the player without a window
            }
        }

        // ---------------- the data feed (map, rods, catches, tips, minimap) ----------------
        private OverlayDataSource? _nuSrc;
        private readonly System.Windows.Forms.Timer _nuMapTimer = new() { Interval = 3000 };
        private bool _nuMapBusy;

        private void NuMapInit(NuShellForm shell)
        {
            var w = _appSettings.Window ??= new WindowSettings();
            var mv = shell.MapView;
            mv.PoseSource = NuShownPose;
            mv.OpenFullMap = ToggleGameMap;
            mv.FullMapKeyText = () => { var ww = _appSettings.Window; return ww != null && ww.MapHotkeyEnabled ? TrayController.HotkeyText(ww.MapHotkeyKey, ww.MapHotkeyModifiers) : ""; };   // ui01_review3 #27
            mv.ZonesOn = w.NuZonesOn; mv.ZoneMinLevel = Math.Clamp(w.NuZoneMinLevel, 0, 2);
            // ui01_review3 #5: saving the settings replaces _appSettings (Clone) — always write into the current Window object
            mv.ZoneSettingsChanged += () => { var ww = _appSettings.Window ??= new WindowSettings(); ww.NuZonesOn = mv.ZonesOn; ww.NuZoneMinLevel = mv.ZoneMinLevel; NuSave(); };
            // UI-02/5: places from old catches — switch and filters are remembered; the minimap uses the same filter
            mv.PlacesOn = w.NuPlacesOn; mv.PlacesFolded = w.NuPlacesFolded; mv.PlaceFilter = NuPlaceFilter.FromSettings(w);
            mv.PlaceSettingsChanged += () => { var ww = _appSettings.Window ??= new WindowSettings(); ww.NuPlacesOn = mv.PlacesOn; ww.NuPlacesFolded = mv.PlacesFolded; mv.PlaceFilter.ToSettings(ww); NuSave(); try { _nuMini?.Radar.Invalidate(); } catch { } };
            mv.OpenPlaceJournal = (pl, f) =>
            {
                if (mv.Data?.LocationId is not uint water) return;
                string cell = MapGridCoordinates.ToGrid(water, pl.X, pl.Z) is { } gg ? MapGridCoordinates.FormatGrid(gg) : Math.Round(pl.X) + " : " + Math.Round(pl.Z);
                double r = Math.Max(5, pl.RadiusM + 2);   // ui02_review5 #3: the circle of this place, not a fixed 20 m
                shell.Press("journal");
                shell.Stats.ShowPlace(water, pl.X, pl.Z, r, cell + " · " + Math.Round(r) + " м · на карте " + pl.Count, f.Fish, f.MinClass, f.Period);
                Log("[NEWUI] журнал места " + cell + " (" + pl.Count + " на карте)");
            };
            shell.RodsStrip.DataSource = () => mv.Data;
            shell.Catches.DataSource = () => mv.Data;
            shell.Catches.ShowOnMap = c => { if (c.X is double x && c.Z is double z) mv.ShowPin(x, z, c.Fish + " " + c.Weight + " г"); };
            shell.TipsPanel.DataSource = () => mv.Data;
            shell.Hours.Stats = () => { shell.Stats.Reload(false); return shell.Stats.Data; };   // ui01_review3 #8: cheap stamp check, reloads only on new catches
            shell.Hours.Water = () => mv.Data?.LocationId;
            shell.Stats.DataChanged += () => { if (shell.Hours.Visible) shell.Hours.Fill(); };
            // journal and statistics
            shell.Stats.Loader = () => NuStatsData.Load(AppPaths.CatchDb, _deepParser, AdvisorLocName);
            shell.Stats.FishIdByName = n => { try { return _deepParser.GetFishByName(n)?.Id; } catch { return null; } };   // ui01_review3 #7
            shell.Stats.OpenLegacyJournal = () => _tray?.ShowLegacyMain();
            shell.Stats.ShowOnMap = c =>
            {
                if (c.X is not double x || c.Z is not double z) return;
                string t = c.Fish + " " + c.Weight + " г" + (c.Cell.Length > 0 ? " · " + c.Cell : "");
                if (c.Water.HasValue && c.Water == mv.Data?.LocationId) { shell.ShowMap(); mv.ShowPin(x, z, t); }
                else { OpenWaterbodyMap(); if (c.Water is uint l) _mapForm?.SelectWaterbody(l); }   // another water: the full map of that water
            };
            // the old forms that now live inside the new window
            shell.SettingsHost.Create = () => NuCreateSettings(shell);
            shell.PhoneHost.Create = () => { EnsureCompanion(); return new CompanionDialog(_companion!); };
            shell.AiHost.Create = NuCreateAdvisor;
            NuFeedStart();
            shell.VisibleChanged += (s, e) => { if (shell.Visible) NuMapTick(null, EventArgs.Empty); };
            NuMapTick(null, EventArgs.Empty);
        }

        private void NuSave() { try { _appSettings.Save(); } catch (Exception ex) { Log("[SETTINGS] не сохранено: " + ex.Message); } }

        private Form NuCreateSettings(NuShellForm shell)
        {
            var f = new SettingsForm(_appSettings, s =>
                {
                    _appSettings = s.Clone();
                    try
                    {   // the settings window works on a copy made when it opened: keep what was changed on the map since then
                        var ww = _appSettings.Window ??= new WindowSettings(); var mv0 = shell.MapView;
                        ww.NuZonesOn = mv0.ZonesOn; ww.NuZoneMinLevel = mv0.ZoneMinLevel; ww.NuPlacesOn = mv0.PlacesOn; ww.NuPlacesFolded = mv0.PlacesFolded; mv0.PlaceFilter.ToSettings(ww);
                    }
                    catch { }
                    Log("[SETTINGS] Настройки сохранены: " + AppSettings.FilePath + (s.Agent.Enabled ? " · ИИ-агент включён (" + s.Agent.Model + ")" : ""));
                    try { if (_advisorForm != null && !_advisorForm.IsDisposed) _advisorForm.ReloadData(learn: false); } catch { }
                    try { _tray?.ApplyHotkey(); } catch { }
                    try { if (_showBox != null) _showBox.SelectedIndex = Math.Clamp(_appSettings.Display.Mode + 1, 0, 3); RebuildHistoryGrid(); } catch { }
                },
                OverlaySummary, () => { if (_overlay != null) _overlay.ShowSettings(shell); }, PhoneSummary, () => shell.Press("phone"), null);   // ui01_review3 #4
            // «Отмена» inside the window: forget the edits (the embedded form is not modal, DialogResult alone does nothing)
            void Hook(Control c)
            {
                foreach (Control k in c.Controls)
                {
                    if (k is Button b && b.DialogResult == DialogResult.Cancel) b.Click += (s, e) => BeginInvoke(new Action(() => { shell.SettingsHost.Drop(); shell.ShowMap(); }));   // ui01_review3 #33
                    Hook(k);
                }
            }
            Hook(f);
            return f;
        }

        private Form? NuCreateAdvisor()
        {
            if (_advisorForm != null && !_advisorForm.IsDisposed) { try { _advisorForm.Close(); } catch { } }
            var f = new AdvisorForm(AppPaths.CatchDb, () => _appSettings, AdvisorFishName, AdvisorLocName,
                loc => { OpenWaterbodyMap(); if (loc is uint l) _mapForm?.SelectWaterbody(l); }, () => _nuShell?.Press("settings"), Log)
            {
                BaitName = AdvisorBaitName,
                OpenReferencePoints = (loc, fish, pts) =>
                {
                    OpenWaterbodyMap();
                    if (_mapForm != null && !_mapForm.ShowReferencePoints(loc, fish, pts)) Log("[MAP] водоём " + loc + " не найден в списке карты");
                }
            };
            f.FormClosed += (s, e) => { if (ReferenceEquals(_advisorForm, f)) _advisorForm = null; };
            _advisorForm = f;
            return f;
        }

        private void NuFeedStart()
        {
            _nuSrc ??= new OverlayDataSource(_deepParser, () => AppPaths.CatchDb, () => AppPaths.BaseDirectory);
            _nuMapTimer.Tick -= NuMapTick; _nuMapTimer.Tick += NuMapTick; _nuMapTimer.Start();
        }

        // ---------------- the new in-game minimap replaces the old window in the game ----------------
        private NuMiniMap? _nuMini;

        /// <summary>Called right after the in-game window manager is created: «\», the tray item and the title button open the new minimap.</summary>
        private void NuMiniHook()
        {
            if (_overlay == null) return;
            _overlay.ReplacementActive = () => NewUiOn;
            _overlay.ReplacementVisible = () => _nuMini?.Visible ?? false;
            _overlay.ReplacementShow = show =>
            {
                try
                {
                    if (show)
                    {
                        _nuMini ??= new NuMiniMap(() => _appSettings.Window ??= new WindowSettings(), NuSave,
                            NuShownPose, id => { try { return _deepParser.GetFish(id)?.Name ?? ""; } catch { return ""; } },
                            () => AdvisorLocName(_currentBaseId is int b && b >= 0 ? (uint)b : null), () => _nuShell?.MapView.ZoneRodSlot);
                        if (_nuMini.HideRequested == null)
                        {
                            _nuMini.HideRequested = () => _overlay?.Hide();
                            FormClosed += (s, e) => { try { _nuMini?.Dispose(); } catch { } _nuMini = null; };
                        }
                        NuFeedStart();
                        _nuMini.Show();
                        NuMapTick(null, EventArgs.Empty);
                    }
                    else _nuMini?.Hide();
                    try { _nuShell?.Invalidate(true); } catch { }
                }
                catch (Exception ex) { Log("[NEWUI] мини-карта: " + ex.Message); }
            };
        }

        private PlayerPose? NuShownPose()
        {
            var p = _deepParser.Track.Last; if (p == null) return null;
            if ((DateTime.Now - p.Time).TotalMinutes > 30) return null;
            if (_currentBaseSince is DateTime s && p.Time < s.AddSeconds(-2)) return null;
            return p;
        }

        /// <summary>UI-02-6: список водоёмов для режима «карта любого водоёма» (свои уловы + карты).</summary>
        private System.Collections.Generic.List<(uint id, string name)> NuWaterList()
        {
            var set = new System.Collections.Generic.SortedDictionary<uint, string>();
            try
            {
                using var con = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + AppPaths.CatchDb + ";Mode=ReadOnly;Pooling=False");
                con.Open();
                using var cmd = new Microsoft.Data.Sqlite.SqliteCommand("SELECT DISTINCT location_id FROM catches WHERE location_id IS NOT NULL ORDER BY location_id", con);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.IsDBNull(0)) continue;
                    long v = r.GetInt64(0);
                    if (v <= 0 || v > 1000000) continue;
                    set[(uint)v] = AdvisorLocName((uint)v) ?? "";
                }
            }
            catch { }
            try { foreach (var m in MapRegistry.All) if (m.LocationId > 0 && !set.ContainsKey(m.LocationId)) set[m.LocationId] = m.Name ?? ""; } catch { }
            // UI-02-6: полный список водоёмов из location_map.json (в т.ч. без карты/без уловов — напр. Болгария: Луда Камчия).
            try
            {
                string lmPath = AppPaths.LocationMapPath;
                if (System.IO.File.Exists(lmPath))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(lmPath));
                    if (doc.RootElement.TryGetProperty("locations", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                        foreach (var loc in arr.EnumerateArray())
                        {
                            if (!loc.TryGetProperty("id", out var idEl) || idEl.ValueKind != System.Text.Json.JsonValueKind.Number) continue;
                            long id = idEl.GetInt64();
                            if (id <= 0 || id > 1000000) continue;
                            string nm = loc.TryGetProperty("name", out var nEl) && nEl.ValueKind == System.Text.Json.JsonValueKind.String ? (nEl.GetString() ?? "") : "";
                            if (!set.ContainsKey((uint)id)) set[(uint)id] = nm;
                        }
                }
            }
            catch { }
            var list = new System.Collections.Generic.List<(uint id, string name)>();
            foreach (var kv in set) list.Add((kv.Key, kv.Value.Length > 0 ? kv.Value : "loc " + kv.Key));
            return list;
        }

        private void NuMapTick(object? sender, EventArgs e)
        {
            var shell = _nuShell;
            bool shellOn = shell != null && !shell.IsDisposed && shell.Visible && shell.WindowState != FormWindowState.Minimized;
            bool miniOn = _nuMini?.Visible == true;
            if ((!shellOn && !miniOn) || _nuMapBusy || _nuSrc == null) return;
            _nuMapBusy = true;
            // UI-02-6: в режиме «карта любого водоёма» — выбранный водоём + все уловы (since = null).
            // SERVER-02: в этом режиме тянем и объединённые (серверные) точки.
            bool mapMode = shell != null && shell.Mode == "map";
            uint? loc; DateTime? since;
            if (mapMode && shell!.MapPickLoc is uint pick) { loc = pick; since = null; }
            else { loc = _currentBaseId is int id && id >= 0 ? (uint)id : null; since = _currentBaseSince; }
            string db = AppPaths.CatchDb; var src = _nuSrc;
            var pfilter = NuPlaceFilter.FromSettings(_appSettings.Window ??= new WindowSettings());
            System.Threading.Tasks.Task.Run(() =>
            {
                var md = NuMapFeed.Build(src, db, loc, since, AdvisorLocName, DateTime.Now);
                try { NuPlaces.Cached(md, pfilter); } catch { }   // ui02_review5 #8: count the places here, not in OnPaint
                if (mapMode && loc.HasValue) { try { md.MergedCatches = AfoCatches.Fetch(loc.Value); } catch { } }   // SERVER-02: все уловы всех устройств
                return md;
            }).ContinueWith(t =>
            {
                _nuMapBusy = false;
                if (t.Exception != null) { Log("[NEWUI] карта: " + t.Exception.InnerException?.Message); return; }
                if (shell != null && !shell.IsDisposed)
                {
                    shell.MapView.Data = t.Result;
                    shell.MapView.ShowPose = !mapMode;   // UI-02-6: на чужой карте не рисуем свою позу/забросы/маркер
                    var ws = _appSettings.Window;
                    if (ws != null && shell.MapView.PlacesOn != ws.NuPlacesOn) shell.MapView.PlacesOn = ws.NuPlacesOn;   // the minimap menu can switch the layer on
                    shell.RodsStrip.Invalidate(); shell.Catches.Invalidate(); shell.TipsPanel.Invalidate();
                }
                if (_nuMini != null) { _nuMini.Data = t.Result; _nuMini.Refresh(); }
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void OpenLegacyFromNu(string what)
        {
            switch (what)
            {
                case "phone": _nuShell?.Press("phone"); break;
                case "settings": _nuShell?.Press("settings"); break;
                default: _tray?.ShowLegacyMain(); break;
            }
        }

        /// <summary>After the tray started: the new window instead of the old one (unless started with the game, --tray).</summary>
        private void NuStartup()
        {
            if (_tray != null)
            {
                _tray.OpenNewUi = ShowNuShell;
                _tray.Recording = NuReceiving;   // ui01_review3 #31
                _tray.ToggleNewUi = () => { if (_nuShell == null || _nuShell.IsDisposed) ShowNuShell(); else _tray.ToggleForm(_nuShell, ShowNuShell); };
            }
            ListenSecondStart();   // ui01_review3 #1
            BeginInvoke(new Action(() =>
            {
                if (Visible) _tray?.HideToTray(returnFocus: false);   // the old window is never the main one now
                if (!StartInTray) ShowNuShell();
            }));
        }
    }
}
