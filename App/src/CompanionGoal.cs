using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FisherLogger
{
    /// <summary>
    /// GOAL-PHONE-01: «Цель» для телефона — тот же план и та же подсказка «Сейчас», что вкладка «Цель» Советника в Windows
    /// (расчёт <see cref="Goal"/>, без изменений), готовыми строками. Телефон только показывает и сохраняет последний ответ.
    /// Тексты повторяют <see cref="GoalView"/> — менять вместе. Только чтение.
    /// </summary>
    public static class CompanionGoal
    {
        private static object SpecJson(GoalSpec s) => new { fish = s.Fish, cls = s.MinClass, loc = s.Location };

        public static object Build(BiteAnalytics an, ReferenceProvider? refs, GoalSpec? spec, Func<long, string>? itemName, DateTime now, GoalSpec? pcGoal)
        {
            string generated = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            if (spec == null)
                return new { none = true, generated, pcGoal = (object?)null, answer = "Цель не выбрана", answerSub = "Выберите рыбу, которую хотите поймать" };

            var plan = Goal.Build(an, spec, itemName);
            var live = Goal.Live(an, plan, now, itemName);
            string cls = Goal.ClassShort(plan.ClassUsed);
            string what = plan.Fish + " (" + cls + ")";

            var warn = new List<string>();
            if (plan.ClassUsed < spec.MinClass) warn.Add($"Рыбы «{Goal.ClassShort(spec.MinClass)}» у вас пока {plan.KRequested} — совет показан для «{cls}».");
            if (plan.K >= Goal.MinK && plan.Sessions <= 1) warn.Add("Вся такая рыба пока с одной рыбалки — совет предварительный.");

            string answer, answerSub;
            if (plan.K >= Goal.MinK && plan.P > 0)
            {
                answer = $"{plan.Fish}: примерно 1 на {UiKit.Casts(plan.PerCasts)}";
                answerSub = $"{cls} · в среднем {plan.K} на {UiKit.Casts(plan.N)}" + (plan.Best("Место") != null ? " · оценка — для лучшего места и наживки" : "");
            }
            else if (plan.K > 0)
            {
                answer = $"{plan.Fish}: данных пока мало";
                answerSub = $"{cls} · поклёвок {plan.K} на {UiKit.Casts(plan.N)} — ниже то, что уже известно";
            }
            else
            {
                answer = $"{plan.Fish}: своих поклёвок ещё нет";
                answerSub = "ниже — с чего начать по данным игры и других игроков";
            }

            object Row(GoalOption o, double best, string? subOverride = null) => new
            {
                title = o.Name,
                sub = subOverride ?? string.Join(" · ", new[] { o.Sub, !o.Enough ? "мало данных" : o.Sessions <= 1 && plan.Sessions > 1 ? "только одна рыбалка" : "" }.Where(x => x.Length > 0)),
                value = $"{o.K} из {o.N}",
                frac = o.Enough && best > 0 ? Math.Min(1, o.Rate / best) : (double?)null,
                muted = !o.Enough, kind = "own",
                baitTid = o.BaitTid, locationId = o.LocationId, x = o.X, z = o.Z,
                tip = $"{what}: клюнула на {o.K} из {UiKit.Casts(o.N)}. Это частота по вашим данным, а не гарантия."
            };
            List<object> Rows(string kind, int n)
            {
                var l = plan.Options.TryGetValue(kind, out var v) ? v.Where(o => o.K > 0).Take(n).ToList() : new List<GoalOption>();
                double best = l.Where(o => o.Enough).Select(o => o.Rate).DefaultIfEmpty(0).Max();
                return l.Select(o => Row(o, best)).ToList();
            }

            var hintsGame = plan.GameHints.Where(h => h.Category == "groundbait").Take(4).Select(h => h.Item).ToList();
            var lures = plan.GameHints.Where(h => h.Category == "lure").Take(4).Select(h => h.Item).ToList();
            string groundNote = hintsGame.Count > 0 ? "По файлам игры эту рыбу привлекает: " + string.Join(", ", hintsGame) + "." : "";   // DESIGN-01: приманки — в «На что»
            var gb = plan.GameBaits.Take(8).ToList();
            string baitNote = (gb.Count > 0 ? "В описаниях игры для этой рыбы названы: " + string.Join(", ", gb) + (plan.GameBaits.Count > gb.Count ? " и др." : "") + ". " : "") + (lures.Count > 0 ? "Приманки: " + string.Join(", ", lures) + "." : "");
            string tackleNote = plan.ClassFromG.HasValue
                ? $"Такая рыба весила {UiKit.Grams(plan.ClassFromG.Value)} – {UiKit.Grams(plan.ClassToG!.Value)} (по вашим поклёвкам и объявлениям)." +
                  (plan.HookAdvice.Length > 0 ? $" По таблице игры под этот вес подходят крючки {plan.HookAdvice}." : "")
                : "";
            if (plan.ClassUsed >= 1 && TrophyProfile.For(an.DbPath, spec.Fish, plan.ClassUsed, spec.Location, now) is { } tp)   // TROPHY-01 (A)
                tackleNote = (tp.Describe(what, tp.Water.HasValue ? an.LocName(tp.Water) : "", GoalView.GroundName) + " " + tackleNote).Trim();
            var tackle = new List<object>();
            foreach (var kind in new[] { "Крючок", "Поводок", "Леска" })
                if (plan.Top(kind) is { } o) tackle.Add(Row(o, 0, kind.ToLowerInvariant() + (o.Enough ? "" : " · мало данных")));

            // другие игроки (справочник): приглушённо, когда хватает своих данных
            var others = new List<object>();
            ReferenceHints? h = null; try { h = refs?.GetHintsForSpecies(spec.Fish, plan.Fish); } catch { }
            if (h != null)
            {
                bool muted = plan.Best("Место") != null && plan.Best("Наживка") != null;
                foreach (var l in h.Locations.Where(l => !spec.Location.HasValue || l.LocationId == spec.Location).OrderByDescending(l => l.Count).Take(3))
                    others.Add(new { title = l.Name, sub = "водоём" + (l.Points.Count > 0 ? " · точки " + string.Join(", ", l.Points.Take(3)) : ""), value = l.Count + " ул.",
                                     frac = (double?)null, muted, kind = "ref", baitTid = (long?)null, locationId = l.LocationId > 0 ? (uint?)l.LocationId : null, x = (double?)null, z = (double?)null, tip = "",
                                     pts = CompanionAdvisor.RefPoints(l.LocationId, l.Points) });   // FIX-05: точки других игроков для карты телефона
                foreach (var b in h.Baits.Concat(h.Lures).OrderByDescending(b => b.Count).Take(3))
                    others.Add(new { title = b.Name, sub = "наживка", value = b.Count + " ул.",
                                     frac = (double?)null, muted, kind = "ref", baitTid = (long?)null, locationId = (uint?)null, x = (double?)null, z = (double?)null, tip = "" });
                if (h.Depth.N > 0 && h.Depth.Max > 0)
                    others.Add(new { title = FormattableString.Invariant($"{Math.Max(0, h.Depth.Min):0.0}–{h.Depth.Max:0.0} м"), sub = "глубина", value = FormattableString.Invariant($"ср. {h.Depth.Avg:0.0} м"),
                                     frac = (double?)null, muted, kind = "ref", baitTid = (long?)null, locationId = (uint?)null, x = (double?)null, z = (double?)null, tip = "" });
            }
            string othersNote = plan.OthersHere > 0 ? $"Пока вы были на водоёме, другие игроки поймали такую рыбу {plan.OthersHere} раз (объявления игры)." : "";

            var foot = new List<string>(plan.Notes.Where(x => !x.StartsWith("Рыбы класса")));
            foot.Add($"Если не клюёт: дайте месту {UiKit.Casts(plan.MoveAfter)}, затем переходите на следующее из списка «Где ловить».");
            foot.Add(plan.Formula.Tested == 0 ? "Формула пока не проверялась: мало забросов."
                : plan.Formula.Better ? $"Формула проверена на {UiKit.Casts(plan.Formula.Tested)} «вперёд по времени»: точнее среднего на {plan.Formula.Improvement:P0}."
                : $"Формула на {UiKit.Casts(plan.Formula.Tested)} пока не точнее простого среднего — показана обычная статистика по месту.");

            object Sec(string key, string title, string hint, List<object> rows, string empty, string note) => new { key, title, hint, rows, empty, note };
            // GB-CALC-01: рецепт прикормки (телефон показывает раздел без обновления приложения)
            var gbr = GroundbaitCalc.For(spec.Fish, an.DbPath, plan.ClassUsed);
            var recipeRows = gbr.Recipes.Select(rc => (object)new
            {
                title = rc.Title, sub = rc.Summary, value = rc.Ready && rc.Cost == 0 ? "" : GroundbaitCalc.Money(rc.Cost), frac = (double?)null, muted = false,
                kind = rc.FromGame ? "ref" : "own",
                tip = string.Join("\n", rc.Parts.Select(p => $"{p.Slot}: {p.Name} — {p.Source}")) + "\n" + rc.Why
            }).ToList();
            string recipeNote = gbr.Note.Length > 0 ? gbr.Note : gbr.Recipes.Count > 0 ? "Цена — за набор (обычно ≈10 порций)." : "";
            var waters = an.Habitat.LocationsOf(spec.Fish).OrderBy(x => an.LocName(x), StringComparer.CurrentCultureIgnoreCase).Select(w => new { id = w, name = an.LocName(w) }).ToList();

            return new
            {
                none = false, generated,
                fish = spec.Fish, fishName = plan.Fish, cls = spec.MinClass, clsText = Goal.ClassShort(spec.MinClass), clsUsed = plan.ClassUsed,
                loc = spec.Location, locName = spec.Location.HasValue ? an.LocName(spec.Location) : "Любой водоём, где водится",
                pcGoal = pcGoal != null ? SpecJson(pcGoal) : null,
                waters,
                answer, answerSub, note = string.Join(" ", warn),
                n = plan.N, k = plan.K, perCasts = plan.PerCasts, moveAfter = plan.MoveAfter,
                live = new
                {
                    tone = live.Tone, headline = live.Headline, detail = live.Detail, misses = live.MissesHere, threshold = live.Threshold,
                    locationId = live.Location, moveLoc = live.MoveTo?.LocationId, moveX = live.MoveTo?.X, moveZ = live.MoveTo?.Z, moveName = live.MoveTo?.Name
                },
                sections = new[]
                {
                    Sec("where", "1. Где ловить", "нужная рыба / ваши забросы на этом месте · нажмите — карта", Rows("Место", 4), "Своих поклёвок этой рыбы с координатами пока нет", ""),
                    Sec("bait", "2. На что", "наживка", Rows("Наживка", 4), "Своих данных о наживке пока нет", baitNote),
                    Sec("recipe", "3. Чем прикормить", "готовый рецепт", recipeRows, "Рецепт рассчитать не из чего", recipeNote),
                    Sec("ground", "Прикормка: ваша статистика", "на чём клевала нужная рыба", Rows("Прикормка", 3), "Своих данных о прикормке пока нет", groundNote),
                    Sec("tackle", "4. Снасть", "на чём клевала нужная рыба", tackle, "Снасть на забросах с этой рыбой ещё не записана", tackleNote),
                    Sec("when", "5. Когда", "игровое время, приблизительно", Rows("Время", 4), "Нет данных о времени", ""),
                    Sec("others", "Что известно от других игроков", "справочник · не ваша статистика", others, "В справочнике этого вида нет", othersNote)
                },
                foot = string.Join(" ", foot)
            };
        }
    }
}
