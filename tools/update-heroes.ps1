# Обновляет встроенный справочник героев из официального фида Valve.
# Использование: powershell -ExecutionPolicy Bypass -File tools\update-heroes.ps1
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$url = 'https://www.dota2.com/datafeed/herolist?language=english'
$out = Join-Path $PSScriptRoot '..\src\Dota2RPC\Resources\heroes.json'

$response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 30
$data = $response.Content | ConvertFrom-Json
$heroes = $data.result.data.heroes | Sort-Object id | ForEach-Object {
    [ordered]@{
        id          = [int]$_.id
        name        = ($_.name -replace '^npc_dota_hero_', '')
        displayName = $(if ($_.name_english_loc) { $_.name_english_loc } else { $_.name_loc })
    }
}
if ($heroes.Count -lt 100) { throw "Фид вернул подозрительно мало героев: $($heroes.Count)" }

$json = ConvertTo-Json -InputObject @($heroes) -Depth 3
[IO.File]::WriteAllText([IO.Path]::GetFullPath($out), $json, (New-Object Text.UTF8Encoding($false)))
Write-Host "Сохранено героев: $($heroes.Count) -> $out"
