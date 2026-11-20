# FEAT-07: build a release package for other PCs (does NOT touch the production App or its bin).
# usage: build_release.ps1 -Version 1.0.0 [-Src <app folder with FisherLogger95.csproj>] [-Out <folder>]
# Result: <Out>\FisherLogger-<ver>-win-x64.zip + SHA256SUMS.txt + RELEASE_NOTES.md (to upload to a GitHub release tagged v<ver>).
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$Src = (Join-Path $PSScriptRoot '..\App'),
  [string]$Out = (Join-Path $PSScriptRoot '..\out'),
  [string]$Data = (Join-Path $PSScriptRoot '..\Data'),
  # -SelfContained: .NET inside the package (user installs nothing). Needs the runtime pack of the SDK's version in the NuGet cache
  # (Microsoft.NETCore.App.Runtime.win-x64 + WindowsDesktop) — one-time download, only with the owner's OK.
  [switch]$SelfContained,
  # optional: the signed release APK of the phone app; copied as FisherCompanion-<ver>.apk and added to SHA256SUMS.txt
  [string]$Apk
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "version must be X.Y.Z" }
$pub = Join-Path $Out "publish_$Version"; $zipName = "AnalyzerFisherOnline-$Version-win-x64.zip"; $zip = Join-Path $Out $zipName
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
New-Item -ItemType Directory $pub -Force | Out-Null

$args2 = @('publish', "`"$Src\FisherLogger95.csproj`"", '-c', 'Release', '-r', 'win-x64', "-p:Version=$Version", '-o', "`"$pub`"", '-nologo', '-v', 'q')
if ($SelfContained) { $args2 += @('--self-contained', 'true') } else { $args2 += @('--self-contained', 'false') }   # framework-dependent: needs .NET 9 Desktop Runtime (Windows offers the link itself)
$p = Start-Process dotnet -ArgumentList $args2 -RedirectStandardOutput (Join-Path $Out "publish_$Version.log") -PassThru -WindowStyle Hidden
$null = $p.Handle   # keep the handle so ExitCode is available
try { $p.PriorityClass = 'BelowNormal' } catch { }
$p.WaitForExit()
if ($p.ExitCode -ne 0 -or -not (Test-Path "$pub\FisherLogger95.exe")) { throw "publish failed, see publish_$Version.log" }

# reference data next to the exe (PacketDeepParser.ResolveDefaultDataDir prefers <exe>\data)
New-Item -ItemType Directory "$pub\data" -Force | Out-Null
Get-ChildItem $Data -File -Filter *.json | Copy-Item -Destination "$pub\data"
if (Test-Path "$Data\assets") { robocopy "$Data\assets" "$pub\data\assets" /E /XD location_images /NFL /NDL /NJH /NJS | Out-Null }

@"
Анализатор Fisher Online $Version (FisherLogger) — помощник для The Fisher Online

Установка
1. Установите бесплатный драйвер Npcap с официального сайта: https://npcap.com/#download (настройки по умолчанию).
2. Распакуйте архив в любую папку (например, C:\Games\AnalyzerFO) и запустите FisherLogger95.exe.
   При первом запуске появится ярлык «Анализатор Fisher Online» на рабочем столе и в меню Пуск.
3. Программа сама находит сетевой адаптер, через который идёт игра. Уловы и настройки хранятся в
   %LOCALAPPDATA%\FisherLogger — их не трогают обновления и переустановка.

Что делает: только читает данные игры на этом ПК. Ничего не отправляет в игру и не управляет ей.
Обмен данными: программа анонимно отправляет на сервер проекта ваши новые поклёвки и уловы (без ника, чата и
учётной записи; прошлая история не отправляется). Выключить: «Для разработчика ▾» → «Обмен данными с сервером…».
Обновления: программа раз в сутки проверяет новую версию на GitHub и спрашивает перед установкой
(архив сверяется по SHA-256). Меню «Для разработчика ▾» → «О программе и обновления…».

Программа неофициальная. Антивирус может предупреждать о программе, читающей сетевой трафик.
"@ | Set-Content "$pub\README.txt" -Encoding UTF8

# nothing private may get into the package
$bad = Get-ChildItem $pub -Recurse -File | Where-Object { $_.Name -match '\.(db|db-wal|db-shm|pcap|pcapng|log)$|settings\.json$|install\.json$|server\.json$|token|secret|\.env$' }
if ($bad) { throw "private-looking files in package: $(($bad | ForEach-Object Name) -join ', ')" }

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$pub\*" -DestinationPath $zip -CompressionLevel Optimal
$h = (Get-FileHash $zip -Algorithm SHA256).Hash
$sums = @("$h  $zipName")
if ($Apk) { $apkName = "FisherCompanion-$Version.apk"; Copy-Item $Apk (Join-Path $Out $apkName) -Force; $sums += "$((Get-FileHash (Join-Path $Out $apkName) -Algorithm SHA256).Hash)  $apkName" }
$sums | Set-Content (Join-Path $Out 'SHA256SUMS.txt') -Encoding ASCII
"FisherLogger $Version`n`n- (что нового)`n" | Set-Content (Join-Path $Out 'RELEASE_NOTES.md') -Encoding UTF8
"OK $zip $([math]::Round((Get-Item $zip).Length/1MB,1)) MB sha256 $h"

