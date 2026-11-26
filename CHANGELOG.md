# Что нового / Changelog

## 1.4.0 — 26.11.2026 — «торговые паки (БЕТА)»
**Русский**

**Главное:** новая вкладка **«Торговые паки»** — кнопка на боковой панели. ⚠️ **Калькулятор паков пока в БЕТЕ**: формула и данные ещё уточняются, цифры могут отличаться от игры.

- **Вкладка «Торговые паки»**: таблица грузов (себестоимость, цена «сегодня», средняя, прибыль, сигнал ↑/↓, «в наличии»), сноска **«на 1 единицу»** с иконками паков из игры.
- **Парсер трафика**: `sale_limit` (цены торговца «средняя/сегодня»), инвентарь `<wpn_add>` (358 предметов), подтверждения продаж `ok=id/count`.
- **Уведомления в стиле поклёвок** (`BiteToast`): выбор паков галочками, режим «пик / предмаксимум», кнопка «Проверить уведомление».
- **Кнопка «Калькулятор»** — включение/выключение.
- **Исправления**: серверные поимки подмешиваются по **всем водоёмам** (журнал «Все водоёмы» теперь видит чужие уловы); карта фильтрует уловы **по своему водоёму** — больше не «путешествуют» с других карт.
- **Неигровые грузы** (Груз Арктик / мутомяса / деликатесов) скрыты — их нет в обычной игре.

**English**
- New **"Trade packs"** tab (rail button). ⚠️ **The pack calculator is BETA** — formula/data still being refined.
- Pack prices from game traffic, cost breakdown, profit, per-unit footnote with real game icons.
- Traffic parsers: `sale_limit`, `<wpn_add>` inventory, `ok=` sales. Bite-style notifications, per-pack selection, calculator on/off.
- Fixes: server catches merged for all waterbodies; the map filters catches by its own waterbody; non-craftable cargos hidden.

## 1.3.0 — 25.11.2026 — «все устройства — одна лодка»
**Русский**

**Короче, что реально поменялось:** мы наконец **смержили уловы всех устройств в один общий слой**. Раньше чужие поимки были отдельным «синим» слоем-сиротой — теперь они живут как родные: те же точки, те же «места», тот же журнал, те же фильтры. Дедуп по своему устройству делается **на сервере** (`exclude_install`), так что своих уловов дважды не будет.

- **Карта больше не тонет в точках.** Когда на популярной точке 300+ уловов, включается **LOD по зуму** (на дальнем плане — только крупняк), вылезает плашка **«Упростить»** `[Только места] [Крупные и выше] [Показать всё]`, а по кнопке **«Тепло»** включается тепловая карта плотности.
- **Круги клёва — общие.** Если сервер вернул `chance` для чужих уловов, круги строятся и по ним, а не только по своим замерам.
- **Журнал места больше не пустой.** Раньше клик по «серому» кругу открывал пустоту: данные лежали на сервере, а журнал читал только локальную БД. Теперь журнал подмешивает серверные уловы (для текущего, выбранного и «места») и по клику делает `Reload`, а не просто `Refill`.
- **Сервер теперь хранит всё.** В таблицу `events` добавлены колонки `leader`, `groundbait`, `depth_cm` (с миграцией — старые записи не трогали); `submit` их принимает, `/api/v1/catches` отдаёт. Заодно `windex` больше не теряется: если в `cast_events` пусто, берём из `catches.windex`. Наживка/крючок раньше хранились, но не отдавались — починили.
- **Мелкие UX-фиксы**: «Крупных+» → «Крупные и выше», «Прежний журнал (удаление дублей)» → «Открыть старый журнал», «Поклёвок» → «всего поклёвок (пустых N)» / «Поклёвок с рыбой»; пустая статистика больше не выглядит как краш (человеческое empty state); статус записи — «Ждём данные игры — запустите игру»; трей-уведомление больше не врёт про «Запись идёт».
- **Советник в панели** (тот самый блокер) — фильтры «Цель»/«По часам» теперь с `WrapContents`, «Ещё» стал нормальным `ContextMenuStrip`, а не «раскрывашкой за край экрана».

**Технический долг (не прячем):** у старых записей `leader`/`groundbait`/`depth_cm` останутся NULL — бэкфила нет, данные не выдумываем. В последних забросах (25.11) `cast_tackle` писал `no_inventory_snapshot_this_run` — снасть не снята на том запуске, поэтому эти события уйдут пустыми по снасти (отдельная задача).

**English**
- **Merged catches from all devices into one common layer** — other players' catches now render like your own (same dots, same places, same journal, same filters). Own-device dedup happens server-side (`exclude_install`), so no double counting.
- **Map no longer drowns in dots**: zoom-based LOD (distant view → only big ones), a "Simplify" plaque `[Places only] [Big and above] [Show all]`, and a "Heat" density map.
- **Shared bite circles** from server `chance`; **place journal fixed** (pulls server catches, `Reload` on click).
- **Server stores everything**: `events` gained `leader`, `groundbait`, `depth_cm` (with migration), `windex` fallback from `catches.windex`, and `/api/v1/catches` returns `bait`/`hook`/`leader`/`groundbait`/`depth_cm`.
- Minor UX fixes (labels, empty states, recording status, tray hint) and the Advisor-in-panel layout blocker.

## 1.2.3 — 25.11.2026 — новый интерфейс и общие уловы
**Русский**
- **Новый интерфейс главного окна**: карта, журнал, статистика, удочки и уловы — в одном окне, без отдельных форм.
- **Карта любого водоёма**: кнопка на правой панели + выбор водоёма из списка; показывает **все уловы выбранного водоёма за всё время**.
- **Объединённые уловы**: на карте теперь видны уловы **всех устройств**, которые отправляют данные на сервер (голубые точки). Кто-то поймал — видят все: люди знают, где клюёт.
- Матчер поклёвок **фидера и спиннинга**: ответы сервера сопоставляются и с этими снастями (раньше — только поплавок).
- Советник: **рецепт прикормки** — какие рыбы её привлекают и из чего она состоит.
- Исправлено: полоса выбора водоёма больше не накладывается на карту; у кнопки «Карта любого водоёма» своя иконка.
- Список водоёмов: теперь **все 36** (в т.ч. Болгария: Луда Камчия и др., где ещё нет карты-реконструкции).
- **Карта Болгарии: Луда Камчия** (id 25) — реконструирована по активному террейну (3 террейна): подложка + глубины.
- Пустые поклёвки: список показывает **до 5 последних** рыб (раньше — только одну).
- Надёжность: убраны мёртвые поля и старые артефакты.

**English**
- **New main window interface**: map, journal, statistics, rods and catches in one window.
- **Any waterbody map**: a button on the right panel + a waterbody picker; shows **all catches of the chosen waterbody for all time**.
- **Merged catches**: the map now shows catches from **all devices** that send data to the server (blue dots). Someone catches a fish and everyone sees it — people know where the fish bite.
- Bite matcher for **feeder and spinning** rods: server responses are matched for those rods too (previously float only).
- Advisor: **groundbait recipe** — which fish it attracts and what it is made of.
- Fixed: the waterbody picker no longer overlaps the map; the "Any waterbody map" button has its own icon.
- Reliability: dead fields and old artifacts removed.

## 1.2.2 — 21.11.2026 — важное обновление
**Русский**
- **Окно о поклёвке снова показывается для всех рыб.** Раньше у части видов (например, у сома) окно не появлялось: программа принимала поклёвку за поимку.
- Крупную рыбу, которую вываживали больше 3 минут, программа больше не записывает дважды. Две рыбы одного веса при этом не путаются.
- ✕ главного окна сворачивает программу к часам, запись продолжается. Закрыть совсем можно кнопкой ⏻ вверху или пунктом «Выход» в меню значка.
- Окно в игре: если растянуть его вниз, вся добавленная высота уходит на список удочек и уловов, пустых полей вокруг карты нет.
- Телефон 1.2.2: изменений нет, обновился только номер версии.

**English**
- **The bite popup shows for every fish again.** For some species (e.g. catfish) it did not appear: the bite was taken for a catch.
- A big fish fought for more than 3 minutes is no longer recorded twice. Two fish of the same weight are not mixed up.
- ✕ on the main window minimizes to the tray and recording continues. To quit, use the ⏻ button or "Выход" in the tray menu.
- In-game window: stretching it down gives the extra height to the rod/catch list, with no empty bands around the map.
- Phone 1.2.2: same app, version number only.

## 1.2.1 — 21.11.2026
**Русский**
- Программа одинаково работает на Windows с любым языком и форматом даты: раньше на финской, тайской, арабской и персидской Windows время в базе записывалось неверно, и ломались история, «По часам» и советы.
- Экраны 1366×768 и масштаб 125–200 %: окна не выходят за экран и не прячутся под панель задач, надписи не обрезаются, окно в игре возвращается на экран.
- Если программа лежит в Program Files, обновление не ломается, а подсказывает, куда перенести папку.
- README: что делать, если не работает захват, телефон или обновление.

**English**
- Works the same on Windows in any language and date format: on Finnish, Thai, Arabic and Persian Windows, times were stored incorrectly before, which broke history, "By hour" and tips.
- 1366×768 screens and 125–200 % scaling: windows stay on screen and above the taskbar, labels are not cut off, the in-game window returns to the screen.
- If the program is in Program Files, the update does not fail but tells you where to move the folder.
- README: what to do if capture, the phone or the update does not work.

## 1.2.0 — 21.11.2026
**Русский**
- Исправлено: в 1.1.0 мини-карта и уведомления о поклёвках на телефоне не получали данных с ПК.
- Зоны клёва на картах (окно в игре, карта водоёма, телефон) подписаны в % от обычного для этой наживки и крючка: 100% — как обычно. Снасть без истории — «новая». Цвет: зелёная — выше обычного, жёлтая — как обычно, серая — ниже.
- Телефон: кнопка «Мини-карта» вверху «Сейчас» — карта на весь экран с зонами клёва и вкладками «Мои уловы / Лента / Советы».
- Телефон: уведомление «Слежу за поклёвками» тихое, с кнопкой «Остановить»; само исчезает, если ПК не отвечает 5 минут.
- Обмен данными с сервером включён по умолчанию по условиям использования (показываются при установке, см. README). Выключить: «Для разработчика ▾ → Обмен данными с сервером…». Отправляются только новые поклёвки и уловы, без ника, чата и учётной записи.

**English**
- Fixed: in 1.1.0 the phone mini-map and bite notifications got no data from the PC.
- Bite zones on maps (in-game window, water map, phone) are labelled in % of usual for this bait and hook: 100% = usual. Tackle without history — "new". Colour: green above usual, yellow usual, grey below.
- Phone: "Mini-map" button at the top of "Now" — full-screen map with bite zones and "My catches / Feed / Tips" tabs.
- Phone: the "Watching for bites" notification is silent, has a "Stop" button and disappears by itself if the PC does not answer for 5 minutes.
- Server data sharing is on by default under the terms of use (shown at installation, see README). Turn off: "Developer ▾ → Server data sharing…". Only new bites and catches are sent, no nickname, chat or account.

## 1.1.0 — 20.11.2026
**Русский**
- По каждой удочке — список последних пустых поклёвок: рыба, вес и класс (цветом класса). Сколько показывать (1–10) — в настройках.
- «По часам» по умолчанию учитывает и ваши уловы, и уловы других игроков (свои объявления не считаются дважды).
- Телефон: уведомления о поклёвке — номер удочки, картинка рыбы в рамке цвета класса, название, вес и класс; работают и при свёрнутом приложении (вкладка «ПК» → «Уведомлять о поклёвках»).
- Телефон: мини-карта на «Сейчас» — вы, направление взгляда и забросы по удочкам, как в окне игры на ПК.
- Обмен данными с сервером (по желанию, выключен по умолчанию): «Для разработчика ▾ → Обмен данными с сервером…». Без ника, номера игрока и чата; каждый запрос подписан ключом установки, сервер проверяет правдоподобие данных.

**English**
- Per rod — the last empty bites: fish, weight and class (in class colour). How many (1–10) — in settings.
- "By hour" now counts both your catches and other players' catches by default (your own announcements are not counted twice).
- Phone: bite notifications — rod number, fish picture framed in the class colour, name, weight and class; they work with the app in the background (PC tab → "Bite notifications").
- Phone: mini-map on "Now" — you, view direction and casts per rod, like the PC in-game window.
- Server data sharing (optional, off by default): "Developer ▾ → Server data sharing…". No nickname, player number or chat; every request is signed with the installation key, the server checks plausibility.

## 1.0.0 — 18.11.2026
Первый выпуск / First release.
