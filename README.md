**Русский** · [English](#english)

# Анализатор Fisher Online (FisherLogger)

Помощник для игры **The Fisher Online**. Работает на **Windows 10/11 (64-бит)**.

## Что это такое

FisherLogger — программа, которая **только читает** сетевые данные игры на вашем компьютере через драйвер **Npcap**. Она:

- **ничего не отправляет** в игру;
- **не управляет** игрой;
- **не автоматизирует** рыбалку.

Это **неофициальный** помощник. Программа **не связана** с компанией R-Age (разработчиком The Fisher Online).

## Возможности

- **Журнал уловов** — автоматически записывает, **где, на что и когда** вы поймали рыбу, её **вес** и **класс**.
- **Карта водоёма** (клавиша **F12**) — глубины и места клёва.
- **Окно в игре** — шанс клёва по вашим удочкам.
- **«Куда идти»** — подскажет, где и на что ловить выбранную рыбу.
- **Калькулятор прикормки** — поможет составить прикормку.
- **Приложение для телефона (Android, FisherCompanion)** — смотрите статус с компьютера по Wi-Fi.

## Установка

1. Установите **Npcap** с <https://npcap.com/#download> — настройки по умолчанию.
2. Скачайте архив **`AnalyzerFisherOnline-X.Y.Z-win-x64.zip`** из раздела [**Releases**](https://github.com/dmitriev02-eng/analyzer-fisher-online/releases/latest).
3. Распакуйте его в любую папку, например `C:\Games\AnalyzerFO`.
4. Запустите **`FisherLogger95.exe`**. При первом запуске выберите язык (**Русский / English**) — программа создаст ярлык **«Анализатор Fisher Online»** на рабочем столе и в меню «Пуск».
5. Запустите игру — программа **сама найдёт** нужный сетевой адаптер.

Отдельно устанавливать **.NET не нужно** — он уже внутри программы.

## Клавиши

- **F11** — окно программы в игре.
- **F12** — карта водоёма.

## Данные, настройки и удаление

Данные и настройки хранятся в папке **`%LOCALAPPDATA%\FisherLogger`**. Обновление программы их **не трогает**.

Чтобы удалить программу, удалите папку с программой и папку `%LOCALAPPDATA%\FisherLogger`.

## Обновления

Раз в сутки программа проверяет, не вышел ли новый выпуск на **GitHub**, и спрашивает разрешения. Архив сверяется по контрольной сумме **SHA-256** (файл `SHA256SUMS.txt`). Если при обновлении что-то пошло не так, программа возвращает прежнюю версию.

Проверить обновление вручную: **«Для разработчика ▾» → «О программе и обновления…»**. Сменить язык: **«Для разработчика ▾» → «Язык / Language…»**.

## Телефон (Android)

1. Установите **`FisherCompanion-X.Y.Z.apk`** из Releases (разрешите установку из этого источника). При первом запуске выберите язык.
2. На компьютере в программе включите связь с телефоном и введите **6-значный код**.
3. Телефон и компьютер должны быть в **одной Wi-Fi сети**.
4. **VPN** на телефоне может мешать — исключите приложение из VPN.

Обновление приложения телефона — кнопка **«Проверить обновления»** на вкладке **«ПК»**.

## Приватность

Обмен данными с сервером **выключен**. Если он появится — только с вашего согласия, **без ника, номера игрока и чата**. Вместо имени используется **случайный номер установки**.

## Если что-то не работает

- **«Нужен Npcap»** — установите Npcap (см. «Установка»).
- **Захват не запускается с VPN** — попробуйте выключить VPN или запустить программу ещё раз.
- **Антивирус ругается** — программа читает сетевой трафик; добавьте папку программы в исключения антивируса.
- **SmartScreen: «неизвестный издатель»** — нажмите «Подробнее» → «Выполнить в любом случае» (программа не подписана).

## Сборка из исходников

- **Windows:** .NET SDK 9, затем `dotnet build App/FisherLogger95.csproj -c Release`.
- **Android:** Android Studio / Gradle (`Android/FisherCompanion`).
- **Архив выпуска:** `tools/build_release.ps1 -Version X.Y.Z` (нужны .NET SDK 9 и папка `Data`).

## Лицензия

Лицензия пока **не выбрана**. Все права принадлежат автору.

---

# English

## Analyzer Fisher Online (FisherLogger)

A helper for the game **The Fisher Online**. Runs on **Windows 10/11 (64-bit)**.

## What it is

FisherLogger is a program that **only reads** the game's network data on your PC through the **Npcap** driver. It:

- **sends nothing** to the game;
- **does not control** the game;
- **does not automate** fishing.

This is an **unofficial** helper. The program is **not affiliated** with R-Age (the developer of The Fisher Online).

## Features

- **Catch log** — automatically records **where, on what and when** you caught a fish, its **weight** and **class**.
- **Waterbody map** (**F12** key) — depths and bite spots.
- **In-game window** — bite chance for your rods.
- **"Where to go"** — tells you where and on what to catch the fish you selected.
- **Groundbait calculator** — helps you make groundbait.
- **Phone app (Android, FisherCompanion)** — view the status from your PC over Wi-Fi.

## Installation

1. Install **Npcap** from <https://npcap.com/#download> — default settings.
2. Download the **`AnalyzerFisherOnline-X.Y.Z-win-x64.zip`** archive from the [**Releases**](https://github.com/dmitriev02-eng/analyzer-fisher-online/releases/latest) section.
3. Extract it to any folder, for example `C:\Games\AnalyzerFO`.
4. Run **`FisherLogger95.exe`**. On first launch choose the language (**Русский / English**); the program creates a shortcut **"Анализатор Fisher Online"** on the desktop and in the Start menu.
5. Start the game — the program **finds the right network adapter on its own**.

You do **not** need to install **.NET** separately — it is already inside the program.

## Hotkeys

- **F11** — in-game program window.
- **F12** — waterbody map.

## Data, settings and removal

Data and settings are stored in the **`%LOCALAPPDATA%\FisherLogger`** folder. Updates do **not** touch them.

To remove the program, delete the program folder and the `%LOCALAPPDATA%\FisherLogger` folder.

## Updates

Once a day the program checks for a new release on **GitHub** and asks for your permission. The archive is verified with a **SHA-256** checksum (the `SHA256SUMS.txt` file). If an update fails, the program restores the previous version.

Check for updates manually: **"Developer ▾" → "About and updates…"**. Change the language: **"Developer ▾" → "Язык / Language…"**.

## Phone (Android)

1. Install **`FisherCompanion-X.Y.Z.apk`** from Releases (allow installation from this source). Choose the language on first launch.
2. On the PC, enable the phone connection in the program and enter the **6-digit code**.
3. The phone and PC must be on the **same Wi-Fi network**.
4. A **VPN** on the phone may interfere — exclude the app from the VPN.

To update the phone app, use the **"Check for updates"** button on the **"PC"** tab.

## Privacy

Server data exchange is **off**. If it ever appears, it will be only with your consent, **without your nickname, player number or chat**. A **random installation number** is used instead of a name.

## Troubleshooting

- **"Npcap is required"** — install Npcap (see "Installation").
- **Capture does not start with a VPN** — try turning the VPN off or running the program again.
- **Antivirus complains** — the program reads network traffic; add the program folder to the antivirus exclusions.
- **SmartScreen: "unknown publisher"** — click "More info" → "Run anyway" (the program is not signed).

## Building from source

- **Windows:** .NET SDK 9, then `dotnet build App/FisherLogger95.csproj -c Release`.
- **Android:** Android Studio / Gradle (`Android/FisherCompanion`).
- **Release package:** `tools/build_release.ps1 -Version X.Y.Z` (needs .NET SDK 9 and the `Data` folder).

## License

The license is **not chosen yet**. All rights belong to the author.
