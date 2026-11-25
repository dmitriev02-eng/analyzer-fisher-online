# Что нового / Changelog

## 1.2.3 — 25.11.2026 — новый интерфейс и общие уловы
**Русский**
- **Новый интерфейс главного окна**: карта, журнал, статистика, удочки и уловы — в одном окне, без отдельных форм.
- **Карта любого водоёма**: кнопка на правой панели + выбор водоёма из списка; показывает **все уловы выбранного водоёма за всё время**.
- **Объединённые уловы**: на карте теперь видны уловы **всех устройств**, которые отправляют данные на сервер (голубые точки). Кто-то поймал — видят все: люди знают, где клюёт.
- Матчер поклёвок **фидера и спиннинга**: ответы сервера сопоставляются и с этими снастями (раньше — только поплавок).
- Советник: **рецепт прикормки** — какие рыбы её привлекают и из чего она состоит.
- Исправлено: полоса выбора водоёма больше не накладывается на карту; у кнопки «Карта любого водоёма» своя иконка.
- Список водоёмов: теперь **все 36** (в т.ч. Болгария: Луда Камчия и др., где ещё нет карты-реконструкции).
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
