#Requires -RunAsAdministrator
#Requires -PSEdition Desktop
<#
.SYNOPSIS
  Публикует тестовый сайт в IIS, чтобы он работал постоянно, без открытого терминала.

.DESCRIPTION
  Запускать в Windows PowerShell (не PowerShell 7) от администратора, из любой папки:

    powershell -ExecutionPolicy Bypass -File tools\TestSite\install-iis.ps1

  Что делает:
    1. Собирает tools\TestSite в -Path (по умолчанию C:\inetpub\relayo-testsite).
    2. Переносит настройки тестового сайта из .env в appsettings.json рядом со сборкой:
       сайт в IIS не видит ни .env, ни переменные терминала.
    3. Создаёт пул приложений (без .NET CLR, всегда запущен, без простоя и плановых перезапусков)
       и сайт с двумя привязками: TEST_SITE_PORT (основной) и TEST_SITE_MIRROR_PORT (зеркало).

  Повторный запуск обновляет сборку и настройки: после изменений в коде сайта или в .env запустите снова.
  Удаление: -Uninstall.

  Нужны IIS и ASP.NET Core Hosting Bundle для .NET 10 — см. docs/windows-dev.md.
#>
param(
    [string]$SiteName = 'relayo-testsite',
    [string]$Path = 'C:\inetpub\relayo-testsite',
    [string]$EnvFile,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $EnvFile) { $EnvFile = Join-Path $repo '.env' }

# --- Проверки окружения ---------------------------------------------------------

if (-not (Get-Service W3SVC -ErrorAction SilentlyContinue)) {
    throw "IIS is not installed. Enable it (then run this script again):`n" +
          "  Enable-WindowsOptionalFeature -Online -All -FeatureName IIS-WebServerRole,IIS-WebServer,IIS-ManagementConsole,IIS-ApplicationInit"
}
Import-Module WebAdministration

function Wait-PoolState([string]$name, [string]$state) {
    for ($i = 0; $i -lt 60; $i++) {
        if ((Get-WebAppPoolState -Name $name).Value -eq $state) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "App pool '$name' did not reach state '$state'"
}

function Stop-Pool([string]$name) {
    if ((Test-Path "IIS:\AppPools\$name") -and (Get-WebAppPoolState -Name $name).Value -ne 'Stopped') {
        Stop-WebAppPool -Name $name
        Wait-PoolState $name 'Stopped'
    }
}

if ($Uninstall) {
    if (Test-Path "IIS:\Sites\$SiteName") { Remove-Website -Name $SiteName; Write-Host "Site '$SiteName' removed" }
    Stop-Pool $SiteName
    if (Test-Path "IIS:\AppPools\$SiteName") { Remove-WebAppPool -Name $SiteName; Write-Host "App pool '$SiteName' removed" }
    Write-Host "Files in $Path are kept, delete them manually if not needed"
    return
}

$ancm = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
if (-not (Test-Path $ancm)) {
    throw "ASP.NET Core Module for IIS is not installed. Install 'ASP.NET Core Runtime 10.0 - Windows Hosting Bundle'`n" +
          "from https://dotnet.microsoft.com/download/dotnet/10.0 (if IIS was enabled after it, run the installer again and choose Repair),`n" +
          "then restart IIS: net stop was /y; net start w3svc"
}

if (-not (Test-Path $EnvFile)) { throw "Settings file not found: $EnvFile" }

# --- Настройки из .env ------------------------------------------------------------

# Только то, что читает тестовый сайт (tools/TestSite/Program.cs).
$settings = [ordered]@{}
foreach ($line in Get-Content $EnvFile -Encoding UTF8) {
    if ($line -match '^\s*(TEST_SITE_[A-Z0-9_]+|TEST_WIDGET_[A-Z0-9_]+|WIDGET_FILES_BASE_URL|PUBLIC_BASE_URL)\s*=(.*)$') {
        $value = $Matches[2].Trim()
        if ($value.Length -ge 2 -and $value.StartsWith('"') -and $value.EndsWith('"')) { $value = $value.Substring(1, $value.Length - 2) }
        if ($value) { $settings[$Matches[1]] = $value }
    }
}
if (-not $settings['TEST_SITE_SECRET'] -or $settings['TEST_SITE_SECRET'].Length -lt 32) {
    throw "TEST_SITE_SECRET in $EnvFile must be set (at least 32 characters)"
}
$port = if ($settings['TEST_SITE_PORT']) { [int]$settings['TEST_SITE_PORT'] } else { 5300 }
$mirrorPort = if ($settings['TEST_SITE_MIRROR_PORT']) { [int]$settings['TEST_SITE_MIRROR_PORT'] } else { 5301 }

# Порт занят другим процессом (dotnet run, Docker) — IIS не сможет запустить сайт.
# Процесс 4 (System) — это http.sys, то есть сам IIS: это нормально.
foreach ($p in @($port, $mirrorPort)) {
    $owner = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue |
        Where-Object { $_.OwningProcess -ne 4 } | Select-Object -First 1
    if ($owner) {
        $proc = Get-Process -Id $owner.OwningProcess -ErrorAction SilentlyContinue
        throw "Port $p is already used by $($proc.ProcessName) (pid $($owner.OwningProcess)). Stop it (dotnet run or Docker test-site) and run again"
    }
}

# --- Сборка -----------------------------------------------------------------------

# Работающий пул держит DLL сайта: останавливаем на время копирования.
Stop-Pool $SiteName

Write-Host "Publishing tools\TestSite to $Path ..."
& dotnet publish (Join-Path $repo 'tools\TestSite\TestSite.csproj') -c Release -o $Path --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$settings | ConvertTo-Json | Set-Content (Join-Path $Path 'appsettings.json') -Encoding UTF8

# --- Пул и сайт --------------------------------------------------------------------

$poolPath = "IIS:\AppPools\$SiteName"
if (-not (Test-Path $poolPath)) {
    New-WebAppPool -Name $SiteName | Out-Null
    Write-Host "App pool '$SiteName' created"
}
Set-ItemProperty $poolPath -Name managedRuntimeVersion -Value ''          # No Managed Code: .NET 10 грузит модуль ASP.NET Core
Set-ItemProperty $poolPath -Name startMode -Value 'AlwaysRunning'         # запускать вместе с IIS, не ждать первого запроса
Set-ItemProperty $poolPath -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)        # не останавливать при простое
Set-ItemProperty $poolPath -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)  # без перезапуска раз в 29 часов

# Учётке пула нужно чтение папки сайта.
& icacls $Path /grant "IIS AppPool\${SiteName}:(OI)(CI)RX" /T /Q | Out-Null

$bindings = @(
    @{ protocol = 'http'; bindingInformation = "*:${port}:" },
    @{ protocol = 'http'; bindingInformation = "*:${mirrorPort}:" }
)
$sitePath = "IIS:\Sites\$SiteName"
if (-not (Test-Path $sitePath)) {
    # Id задаём сами: без него New-Website падает, если в IIS нет ни одного сайта.
    $id = ((Get-ChildItem IIS:\Sites | Measure-Object -Property id -Maximum).Maximum) + 1
    New-Website -Name $SiteName -Id $id -PhysicalPath $Path -ApplicationPool $SiteName -Port $port | Out-Null
    Write-Host "Site '$SiteName' created"
}
Set-ItemProperty $sitePath -Name physicalPath -Value $Path
Set-ItemProperty $sitePath -Name applicationPool -Value $SiteName
Set-ItemProperty $sitePath -Name bindings -Value $bindings
Set-ItemProperty $sitePath -Name applicationDefaults.preloadEnabled -Value $true   # с IIS-ApplicationInit стартует сразу

Start-WebAppPool -Name $SiteName
Wait-PoolState $SiteName 'Started'
if ((Get-WebsiteState -Name $SiteName).Value -ne 'Started') { Start-Website -Name $SiteName }

# --- Проверка ---------------------------------------------------------------------

foreach ($p in @($port, $mirrorPort)) {
    $ok = $false
    for ($i = 0; $i -lt 20 -and -not $ok; $i++) {
        try {
            $ok = (Invoke-WebRequest "http://localhost:$p/" -UseBasicParsing -TimeoutSec 10).StatusCode -eq 200
        } catch { Start-Sleep -Seconds 1 }
    }
    if (-not $ok) {
        throw "http://localhost:$p/ does not answer. See Event Viewer -> Windows Logs -> Application (source 'IIS AspNetCore Module V2')"
    }
}
Write-Host "Test site is running in IIS: http://localhost:$port/ and mirror http://localhost:$mirrorPort/"
