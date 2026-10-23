# Сборка Dota 2 Discord Rich Presence: тесты → публикация (self-contained .NET 10) → установщик dist\Setup.exe
#
#   powershell -ExecutionPolicy Bypass -File build.ps1               # полная сборка
#   powershell -ExecutionPolicy Bypass -File build.ps1 -SkipTests    # без тестов
#   powershell -ExecutionPolicy Bypass -File build.ps1 -UpdateHeroes # обновить список героев из фида Valve
#   powershell -ExecutionPolicy Bypass -File build.ps1 -ClientId 1234567890123456789
#       # вшить Application ID Discord (сохраняется в discord-app-id.txt и используется во всех следующих сборках)
#
# Требуется: .NET SDK 10 (winget install Microsoft.DotNet.SDK.10) и Inno Setup 6 (winget install JRSoftware.InnoSetup)
param(
    [switch]$SkipTests,
    [switch]$UpdateHeroes,
    [string]$ClientId,
    [string]$Version = '1.2.0'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "ОШИБКА: $text" -ForegroundColor Red; exit 1 }

Step 'Проверка инструментов'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Fail '.NET SDK не найден. Установите: winget install Microsoft.DotNet.SDK.10' }
$sdks = & dotnet --list-sdks
if (-not ($sdks | Where-Object { $_ -match '^10\.' })) { Fail ".NET SDK 10 не найден (есть: $($sdks -join ', ')). Установите: winget install Microsoft.DotNet.SDK.10" }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Fail 'Inno Setup 6 не найден. Установите: winget install JRSoftware.InnoSetup' }
Write-Host "dotnet: $($dotnet.Source)"
Write-Host "ISCC:   $iscc"

Step 'Application ID Discord'
$idFile = Join-Path $root 'discord-app-id.txt'
if ($ClientId) {
    $ClientId = $ClientId.Trim()
    if ($ClientId -notmatch '^\d{17,21}$') { Fail "Application ID «$ClientId» некорректен: нужно 17–21 цифра (Discord Developer Portal → General Information → Application ID)" }
    [IO.File]::WriteAllText($idFile, $ClientId, (New-Object Text.UTF8Encoding($false)))
}
$embeddedId = if (Test-Path $idFile) { (Get-Content $idFile -Raw).Trim() } else { '' }
if ($embeddedId -and $embeddedId -notmatch '^\d{17,21}$') { Fail "В $idFile некорректный Application ID: «$embeddedId»" }
if ($embeddedId) { Write-Host "Встраивается Application ID: $embeddedId (пользователям не нужно его вводить)" }
else { Write-Host 'Application ID не встроен: установщик попросит пользователя ввести свой. Укажите -ClientId, чтобы встроить.' -ForegroundColor Yellow }

$animFile = Join-Path $root 'hero-animations-url.txt'
if (Test-Path $animFile) { Write-Host "Анимированные иконки героев: $((Get-Content $animFile -Raw).Trim())" }
else { Write-Host 'Анимированные иконки не подключены (нет hero-animations-url.txt) — будут статичные иконки.' -ForegroundColor Yellow }

if ($UpdateHeroes) {
    Step 'Обновление списка героев'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\update-heroes.ps1')
    if ($LASTEXITCODE -ne 0) { Fail 'не удалось обновить список героев' }
}

if (-not (Test-Path 'src\Dota2RPC\Resources\app.ico')) {
    Step 'Генерация иконок'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\make-icon.ps1')
}

if (-not $SkipTests) {
    Step 'Тесты'
    & dotnet test tests\Dota2RPC.Tests\Dota2RPC.Tests.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { Fail 'тесты не пройдены' }
}

Step 'Публикация (self-contained, win-x64)'
$publish = Join-Path $root 'artifacts\publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
& dotnet publish src\Dota2RPC\Dota2RPC.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true `
    -p:DebugType=none -p:Version=$Version -o $publish --nologo
if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish завершился с ошибкой' }

Step 'Сборка установщика'
& $iscc "/DAppVersion=$Version" "/DPublishDir=$publish" "/DDiscordClientId=$embeddedId" installer\Dota2RPC.iss
if ($LASTEXITCODE -ne 0) { Fail 'Inno Setup завершился с ошибкой' }

$setup = Join-Path $root 'dist\Setup.exe'
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash
$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Step "Готово: $setup ($size МБ)"
Write-Host "SHA256: $hash"
