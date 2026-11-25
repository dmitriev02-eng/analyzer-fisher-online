using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace FisherLogger
{
    /// <summary>
    /// MASTER-01: вкладка «Цель» Советника. Игрок выбирает рыбу, класс и водоём — получает короткий план:
    /// где, на что, чем прикормить, какой снастью, когда; оценку «1 нужная на N забросов» и подсказку во время рыбалки
    /// (продолжать / сменить наживку / перейти на другое место). Вся статистика — в <see cref="Goal"/>.
    /// </summary>
    public sealed class GoalView : UserControl
    {
        private sealed class Item { public string Text = ""; public object? Val; public override string ToString() => Text; }

        private readonly Func<uint, string, ReferenceHints?> _hints;
        private readonly Action<uint?>? _openMap;
        private BiteAnalytics? _an;
        private Func<long, string>? _itemName;
        private bool _filling;

        internal readonly ComboBox FishBox = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 160, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
        internal readonly ComboBox ClassBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        internal readonly ComboBox WaterBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        private readonly FlowLayoutPanel _body = UiKit.Stack(new Padding(18, 10, 18, 12));
        private readonly PictureBox _icon = new() { Size = new Size(56, 56), SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(0, 2) };
        internal readonly Label Answer = new() { AutoSize = false, Location = new Point(68, 0), Height = 32, Font = Theme.Title, ForeColor = Theme.Text };
        internal readonly Label AnswerSub = new() { AutoSize = false, Location = new Point(70, 34), Height = 22, ForeColor = Theme.TextMuted };
        internal readonly Label AnswerNote = new() { AutoSize = false, Location = new Point(70, 56), Height = 22, ForeColor = Theme.Warning };
        internal readonly Label LivePill = UiKit.Pill("", Theme.ButtonNeutral);
        internal readonly Label LiveDetail = UiKit.Note("");
        internal readonly StatList Where = new() { EmptyText = "Своих поклёвок этой рыбы с координатами пока нет" };
        internal readonly StatList Baits = new() { EmptyText = "Своих данных о наживке пока нет" };
        internal readonly StatList Ground = new() { EmptyText = "Своих данных о прикормке пока нет" };
        internal static string GroundName(string g) => long.TryParse(g, out long id) && GroundbaitCalc.Items().TryGetValue(id, out var it) ? it.Name : g;
        /// <summary>GB-CALC-01: готовые рецепты прикормки под выбранную рыбу.</summary>
        internal readonly StatList Recipes = new() { EmptyText = "Рецепт рассчитать не из чего" };
        private readonly Label _recipeNote = UiKit.Note("");
        public GroundbaitCalc.Result? RecipeResult { get; private set; }
        internal readonly StatList Tackle = new() { EmptyText = "Снасть на забросах с этой рыбой ещё не записана" };
        internal readonly StatList When = new() { EmptyText = "Нет данных о времени" };
        internal readonly StatList Others = new() { EmptyText = "В справочнике этого вида нет" };
        private readonly Label _groundHint = UiKit.Note("");
        private readonly Label _baitHint = UiKit.Note("");
        private readonly Label _tackleHint = UiKit.Note("");
        private readonly Label _othersNote = UiKit.Note("");
        private readonly Label _foot = UiKit.Note("");
        private readonly Control _othersHead = UiKit.Section("Что известно от других игроков", "справочник · не ваша статистика");

        /// <summary>Для тестов и снимков: «сейчас» для подсказки во время рыбалки.</summary>
        internal DateTime? NowOverride;
        internal GoalPlan? Plan { get; private set; }
        internal GoalLive? LiveState { get; private set; }
        public event Action<GoalSpec>? GoalChanged;

        private readonly Action<uint, string, IReadOnlyList<(double X, double Z, string G)>>? _openRef;

        public GoalView(Func<uint, string, ReferenceHints?> hints, Action<uint?>? openMap,
                        Action<uint, string, IReadOnlyList<(double X, double Z, string G)>>? openRef = null)
        {
            _hints = hints; _openMap = openMap; _openRef = openRef;
            Dock = DockStyle.Fill; BackColor = Theme.Surface; ForeColor = Theme.Text; Font = Theme.Ui;

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 88, Padding = new Padding(14, 8, 6, 2), BackColor = Theme.Surface, WrapContents = true };
            Label Cap(string t) => new() { Text = t, AutoSize = true, Margin = new Padding(0, 6, 6, 0), ForeColor = Theme.TextMuted };
            bar.Controls.Add(Cap("Хочу поймать:")); bar.Controls.Add(FishBox);
            bar.Controls.Add(Cap("   Класс:")); bar.Controls.Add(ClassBox);
            bar.Controls.Add(Cap("   Где:")); bar.Controls.Add(WaterBox);
            foreach (var (t, v) in new[] { ("Любая", 0), ("Зелёная и крупнее", 1), ("Крупная (синяя) и трофейная", 2), ("Трофейная (оранжевая)", 3) })
                ClassBox.Items.Add(new Item { Text = t, Val = v });
            ClassBox.SelectedIndex = 2;

            var head = new Panel { Height = 82, Margin = new Padding(0, 0, 0, 2), BackColor = Theme.Surface };
            head.Controls.Add(_icon); head.Controls.Add(Answer); head.Controls.Add(AnswerSub); head.Controls.Add(AnswerNote);
            head.Resize += (s, e) => { Answer.Width = head.Width - 72; AnswerSub.Width = head.Width - 72; AnswerNote.Width = head.Width - 72; };
            LiveDetail.Height = 34; LiveDetail.Font = Theme.Ui; LiveDetail.ForeColor = Theme.Text;
            _groundHint.Height = 34; _foot.Height = 54;
            foreach (var l in new[] { Where, Baits, Recipes, Ground, Tackle, When, Others }) l.BackColor = Theme.SurfaceAlt;
            _recipeNote.Height = 34;
            Others.BackColor = Theme.Lighten(Theme.Surface, 0.03);
            Where.RowClicked += r => { if (r.Tag is GoalOption o) _openMap?.Invoke(o.LocationId); };
            // FIX-05: «Что известно от других игроков» → водоём с их точками на карте (как во вкладке «Рыба»)
            Others.RowClicked += r =>
            {
                if (r.Tag is AdvisorForm.RefPlaceTag rt && rt.Points.Count > 0 && _openRef != null) _openRef(rt.Loc, rt.Fish, rt.Points);
                else if (r.Tag is AdvisorForm.RefPlaceTag rt2) _openMap?.Invoke(rt2.Loc);
            };

            _body.Dock = DockStyle.Fill;
            _body.SuspendLayout();
            _body.Controls.AddRange(new Control[]
            {
                head,
                UiKit.Section("Сейчас", "подсказка во время рыбалки"), LivePill, LiveDetail,
                UiKit.Section("1. Где ловить", "нужная рыба / ваши забросы на этом месте"), Where,
                UiKit.Section("2. На что", "наживка"), Baits, _baitHint,
                UiKit.Section("3. Чем прикормить", "готовый рецепт · ваша статистика ниже"), Recipes, _recipeNote, Ground, _groundHint,
                UiKit.Section("4. Снасть", "на чём клевала нужная рыба"), Tackle, _tackleHint,
                UiKit.Section("5. Когда", "игровое время, приблизительно"), When,
                _othersHead, Others, _othersNote,
                _foot
            });
            _body.ResumeLayout();
            Controls.Add(_body); Controls.Add(bar);

            FishBox.SelectedIndexChanged += (s, e) => { if (!_filling) { FillWaters(); Recalc(true); } };
            FishBox.Leave += (s, e) => { if (!_filling && FishBox.SelectedItem == null && FishBox.Items.Count > 0) PickTyped(); };
            FishBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; PickTyped(); } };
            ClassBox.SelectedIndexChanged += (s, e) => { if (!_filling) Recalc(true); };
            WaterBox.SelectedIndexChanged += (s, e) => { if (!_filling) Recalc(true); };
        }

        private void PickTyped()
        {
            string t = FishBox.Text.Trim();
            var m = FishBox.Items.Cast<Item>().FirstOrDefault(i => i.Text.StartsWith(t, StringComparison.CurrentCultureIgnoreCase))
                    ?? FishBox.Items.Cast<Item>().FirstOrDefault(i => i.Text.Contains(t, StringComparison.CurrentCultureIgnoreCase));
            if (m != null) FishBox.SelectedItem = m;
        }

        public GoalSpec? Spec => FishBox.SelectedItem is Item f && f.Val is uint id
            ? new GoalSpec { Fish = id, MinClass = (ClassBox.SelectedItem as Item)?.Val is int k ? k : 0, Location = (WaterBox.SelectedItem as Item)?.Val as uint? }
            : null;

        /// <summary>Новые данные. species — виды для выбора (свои сначала). keep — цель, которую нужно сохранить выбранной.</summary>
        public void SetData(BiteAnalytics an, IEnumerable<(uint id, string name)> species, Func<long, string>? itemName, GoalSpec? keep = null)
        {
            _an = an; _itemName = itemName;
            keep ??= Spec;
            _filling = true;
            try
            {
                FishBox.Items.Clear();
                foreach (var (id, name) in species) FishBox.Items.Add(new Item { Text = name, Val = id });
                var sel = keep != null ? FishBox.Items.Cast<Item>().FirstOrDefault(i => i.Val is uint v && v == keep.Fish) : null;
                if (sel != null) FishBox.SelectedItem = sel; else if (FishBox.Items.Count > 0) FishBox.SelectedIndex = 0;
                if (keep != null) ClassBox.SelectedIndex = Math.Clamp(keep.MinClass, 0, 3);
                FillWaters(keep?.Location, keepGiven: keep != null);
            }
            finally { _filling = false; }
            Recalc(false);
        }

        private void FillWaters(uint? keep = null, bool keepGiven = false)
        {
            if (_an == null) return;
            bool was = _filling; _filling = true;
            try
            {
                uint? prev = keepGiven ? keep : (WaterBox.SelectedItem as Item)?.Val as uint?;
                WaterBox.Items.Clear();
                WaterBox.Items.Add(new Item { Text = "Любой водоём, где водится", Val = null });
                if (FishBox.SelectedItem is Item f && f.Val is uint id)
                    foreach (var w in _an.Habitat.LocationsOf(id).OrderBy(x => _an.LocName(x)))
                        WaterBox.Items.Add(new Item { Text = _an.LocName(w), Val = w });
                var sel = prev.HasValue ? WaterBox.Items.Cast<Item>().FirstOrDefault(i => i.Val is uint v && v == prev.Value) : null;
                WaterBox.SelectedItem = sel ?? WaterBox.Items[0];
            }
            finally { _filling = was; }
        }

        /// <summary>Обновить только подсказку «Сейчас» (вызывается после новых забросов вместе с SetData).</summary>
        internal void Recalc(bool userChange)
        {
            if (_an == null) return;
            var spec = Spec;
            _body.SuspendLayout();
            try
            {
                if (spec == null)
                {
                    Plan = null; LiveState = null; _icon.Image = null;
                    Answer.Text = "Выберите рыбу"; AnswerSub.Text = "Начните вводить название в поле «Хочу поймать»"; AnswerNote.Text = "";
                    UiKit.SetPill(LivePill, "Цель не выбрана", Theme.ButtonNeutral); LiveDetail.Text = "";
                    foreach (var l in new[] { Where, Baits, Recipes, Ground, Tackle, When, Others }) l.SetRows(Array.Empty<StatRow>());
            _recipeNote.Text = "";
                    _groundHint.Text = _othersNote.Text = _foot.Text = _baitHint.Text = _tackleHint.Text = "";
                    return;
                }
                var plan = Plan = Goal.Build(_an, spec, _itemName);
                var live = LiveState = Goal.Live(_an, plan, NowOverride ?? DateTime.Now, _itemName);
                _icon.Image = UiKit.FishIcon(spec.Fish);
                string cls = Goal.ClassShort(plan.ClassUsed);
                string what = plan.Fish + " (" + cls + ")";
                const string lowered = "";
                var warn = new List<string>();
                if (plan.ClassUsed < spec.MinClass) warn.Add($"Рыбы «{Goal.ClassShort(spec.MinClass)}» у вас пока {plan.KRequested} — совет показан для «{cls}».");
                if (plan.K >= Goal.MinK && plan.Sessions <= 1) warn.Add("Вся такая рыба пока с одной рыбалки — совет предварительный.");
                AnswerNote.Text = string.Join(" ", warn);

                if (plan.K >= Goal.MinK && plan.P > 0)
                {
                    Answer.Text = $"{plan.Fish}: примерно 1 на {UiKit.Casts(plan.PerCasts)}";
                    AnswerSub.Text = lowered + $"{cls} · в среднем {plan.K} на {UiKit.Casts(plan.N)}" + (plan.Best("Место") != null ? " · оценка — для лучшего места и наживки" : "");
                }
                else if (plan.K > 0)
                {
                    Answer.Text = $"{plan.Fish}: данных пока мало";
                    AnswerSub.Text = lowered + $"{cls} · поклёвок {plan.K} на {UiKit.Casts(plan.N)} — ниже то, что уже известно";
                }
                else
                {
                    Answer.Text = $"{plan.Fish}: своих поклёвок ещё нет";
                    AnswerSub.Text = "ниже — с чего начать по данным игры и других игроков";
                }

                Color tone = live.Tone switch { "ok" => Theme.AccentGreen, "move" => Theme.Danger, "bait" => Theme.Warning, "warn" => Theme.Warning, _ => Theme.ButtonNeutral };
                UiKit.SetPill(LivePill, live.Headline, tone); LiveDetail.Text = live.Detail;

                StatRow Row(GoalOption o, Image? icon, double best) => new()
                {
                    Icon = icon, Title = o.Name,
                    Subtitle = string.Join(" · ", new[] { o.Sub, !o.Enough ? "мало данных" : o.Sessions <= 1 && plan.Sessions > 1 ? "только одна рыбалка" : "" }.Where(x => x.Length > 0)),
                    Value = $"{o.K} из {o.N}", Fraction = o.Enough && best > 0 ? Math.Min(1, o.Rate / best) : null, Muted = !o.Enough,
                    Accent = o.Enough ? Theme.AccentGreen : Theme.AccentTeal, Tag = o,
                    Tooltip = $"{what}: клюнула на {o.K} из {UiKit.Casts(o.N)} ({o.Lo:P0}–{o.Hi:P0}). Это частота по вашим данным, а не гарантия."
                };
                IEnumerable<StatRow> Rows(string kind, int n, Func<GoalOption, Image?>? icon = null)
                {
                    var l = plan.Options.TryGetValue(kind, out var v) ? v.Where(o => o.K > 0).Take(n).ToList() : new List<GoalOption>();
                    double best = l.Where(o => o.Enough).Select(o => o.Rate).DefaultIfEmpty(0).Max();
                    return l.Select(o => Row(o, icon?.Invoke(o), best));
                }
                Where.SetRows(Rows("Место", 4));
                Baits.SetRows(Rows("Наживка", 4, o => UiKit.BaitIcon(o.BaitTid)));
                Ground.SetRows(Rows("Прикормка", 3));
                // GB-CALC-01: рецепт — основа по данным игры + добавки по описаниям игры и вашему журналу
                var gbr = RecipeResult = GroundbaitCalc.For(spec.Fish, _an.DbPath, plan.ClassUsed);
                Recipes.SetRows(gbr.Recipes.Select(r => new StatRow
                {
                    Title = r.Title, Subtitle = r.Summary,
                    Value = r.Ready && r.Cost == 0 ? "" : GroundbaitCalc.Money(r.Cost),
                    Accent = r.FromGame ? Theme.Warning : Theme.AccentGreen,
                    Tooltip = string.Join("\n", r.Parts.Select(p => $"{p.Slot}: {p.Name}" + (p.Cost > 0 ? $" ({GroundbaitCalc.Money(p.Cost)})" : "") + $" — {p.Source}"))
                              + (r.N > 0 ? $"\nВаш журнал: нужная рыба на {r.K} из {UiKit.Casts(r.N)} с этой основой/смесью." : "") + "\n" + r.Why
                }));
                _recipeNote.Text = gbr.Note.Length > 0 ? gbr.Note : gbr.Recipes.Count > 0 ? "Цена — за набор (обычно ≈10 порций). Наведите на строку — откуда каждый ингредиент." : "";
                _recipeNote.Visible = _recipeNote.Text.Length > 0;
                var hintsGame = plan.GameHints.Where(h => h.Category == "groundbait").Take(4).Select(h => h.Item).ToList();
                var lures = plan.GameHints.Where(h => h.Category == "lure").Take(4).Select(h => h.Item).ToList();
                // DESIGN-01: приманки (блёсны, воблеры) — это «На что», а не прикормка (раньше показывались под «Чем прикормить»)
                _groundHint.Text = hintsGame.Count > 0 ? "По файлам игры эту рыбу привлекает: " + string.Join(", ", hintsGame) + "." : "";
                _groundHint.Visible = _groundHint.Text.Length > 0;

                var gb = plan.GameBaits.Take(8).ToList();
                _baitHint.Text = (gb.Count > 0 ? "В описаниях игры для этой рыбы названы: " + string.Join(", ", gb) + (plan.GameBaits.Count > gb.Count ? " и др." : "") + ". " : "") +
                                 (lures.Count > 0 ? "Приманки: " + string.Join(", ", lures) + "." : "");
                _baitHint.Visible = _baitHint.Text.Length > 0;
                _tackleHint.Text = plan.ClassFromG.HasValue
                    ? $"Такая рыба весила {UiKit.Grams(plan.ClassFromG.Value)} – {UiKit.Grams(plan.ClassToG!.Value)} (по вашим поклёвкам и объявлениям)." +
                      (plan.HookAdvice.Length > 0 ? $" По таблице игры под этот вес подходят крючки {plan.HookAdvice}." : "")
                    : "";
                // TROPHY-01 (A): трофейный режим — при каком шансе и на чём клевала нужная рыба (на выбранном водоёме или где её больше всего)
                if (plan.ClassUsed >= 1 && TrophyProfile.For(_an.DbPath, spec.Fish, plan.ClassUsed, spec.Location, DateTime.Now) is { } tp)
                    _tackleHint.Text = (tp.Describe(what, tp.Water.HasValue ? _an.LocName(tp.Water) : "", GroundName) + " " + _tackleHint.Text).Trim();
                _tackleHint.Height = _tackleHint.Text.Length > 160 ? 70 : 34;
                _tackleHint.Visible = _tackleHint.Text.Length > 0;
                var tk = new List<StatRow>();
                foreach (var kind in new[] { "Крючок", "Поводок", "Леска" })
                    if (plan.Top(kind) is { } o)
                    { var r = Row(o, null, 0); r.Subtitle = kind.ToLowerInvariant() + (o.Enough ? "" : " · мало данных"); tk.Add(r); }
                Tackle.SetRows(tk);
                // DAYPART-01: когда клюёт на этом водоёме — по тому, как меняется шанс наживок, на которые вы ловили эту рыбу
                var dayRows = new List<StatRow>();
                try
                {
                    if (spec.Location is uint wl)
                        foreach (var d in DayParts.ForFish(DayParts.For(_an.DbPath, DateTime.Now), spec.Fish, wl))
                            dayRows.Add(new StatRow
                            {
                                Title = "Лучше всего: " + d.Part, Subtitle = DayParts.RealWindow(d.Part) + " · на «" + d.Bait + "»",
                                Value = "×" + d.X.ToString(d.X < 10 ? "0.#" : "0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")), Accent = Theme.AccentGreen,
                                Tooltip = $"На «{d.Bait}» здесь шанс в {d.X:0.#} раза выше, чем в {(d.Worst == "ночь" ? "ночь" : d.Worst == "вечер" ? "вечер" : d.Worst == "день" ? "день" : "рассвет")} (по {d.N} сменам времени суток). " +
                                          "Шанс — про наживку, а не про рыбу: эту рыбу вы ловили на неё здесь."
                            });
                }
                catch { }
                When.SetRows(dayRows.Concat(Rows("Время", 4)));

                // другие игроки: показываем всегда, но приглушённо, когда хватает своих данных
                ReferenceHints? h = null; try { h = _hints(spec.Fish, plan.Fish); } catch { }
                var ot = new List<StatRow>();
                if (h != null)
                {
                    bool muted = plan.Best("Место") != null && plan.Best("Наживка") != null;
                    foreach (var l in h.Locations.Where(l => !spec.Location.HasValue || l.LocationId == spec.Location).OrderByDescending(l => l.Count).Take(3))
                    {
                        var pts = AdvisorForm.RefWorldPoints(l.LocationId, l.Points);
                        ot.Add(new StatRow { Title = l.Name, Subtitle = "водоём" + (l.Points.Count > 0 ? " · точки " + string.Join(", ", l.Points.Take(3)) : "") + (pts.Count > 0 ? " · щёлкните — на карте" : ""),
                            Value = l.Count + " " + UiKit.Catches(l.Count), Accent = Theme.Warning, Muted = muted,
                            Tag = l.LocationId > 0 ? new AdvisorForm.RefPlaceTag((uint)l.LocationId, plan.Fish, pts) : null });
                    }
                    foreach (var b in h.Baits.Concat(h.Lures).OrderByDescending(b => b.Count).Take(3))
                        ot.Add(new StatRow { Title = b.Name, Subtitle = "наживка", Value = b.Count + " " + UiKit.Catches(b.Count), Accent = Theme.Warning, Muted = muted });
                    if (h.Depth.N > 0 && h.Depth.Max > 0) ot.Add(new StatRow { Title = FormattableString.Invariant($"{Math.Max(0, h.Depth.Min):0.0}–{h.Depth.Max:0.0} м"), Subtitle = "глубина", Value = FormattableString.Invariant($"ср. {h.Depth.Avg:0.0} м"), Accent = Theme.Warning, Muted = muted });
                }
                Others.SetRows(ot);
                _othersNote.Text = plan.OthersHere > 0 ? $"Пока вы были на водоёме, другие игроки поймали такую рыбу {plan.OthersHere} раз (объявления игры)." : "";
                _othersNote.Visible = _othersNote.Text.Length > 0;

                var foot = new List<string>(plan.Notes.Where(x => !x.StartsWith("Рыбы класса")));
                foot.Add($"Если не клюёт: дайте месту {UiKit.Casts(plan.MoveAfter)}, затем переходите на следующее из списка «Где ловить».");
                foot.Add(plan.Formula.Tested == 0 ? "Формула пока не проверялась: мало забросов."
                    : plan.Formula.Better ? $"Прогноз проверен на {UiKit.Casts(plan.Formula.Tested)} и работает лучше простой статистики."
                    : "Прогноза пока нет — показана статистика по вашим забросам.");
                _foot.Text = string.Join("  ", foot);
                if (userChange) GoalChanged?.Invoke(spec);
            }
            finally { _body.ResumeLayout(true); _body.PerformLayout(); }
        }
    }
}
