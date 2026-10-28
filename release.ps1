# Выпуск новой версии на GitHub Releases (приложения сами предложат обновиться).
#
#   $env:GITHUB_TOKEN = "ghp_..."      # токен с правом Contents: Read and write на репозиторий
#   powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version 1.2.0 -Repo owner/tg-check -Notes "Что нового"
#
# Скрипт: собирает dist\TgCheck.exe с этой версией → создаёт релиз v<Version> → прикрепляет TgCheck.exe.
# Репозиторий должен быть публичным (иначе приложения не смогут скачать обновление без токена)
# и содержать хотя бы один коммит.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Repo,
    [string]$Notes = ""
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not $Repo) {
    $props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
    if ($props -match '<UpdateRepo[^>]*>([^<]+)</UpdateRepo>') { $Repo = $Matches[1].Trim() }
}
if (-not $Repo -or $Repo -notmatch '^[\w.-]+/[\w.-]+$') {
    throw "Укажите репозиторий: -Repo owner/name (или пропишите <UpdateRepo> в Directory.Build.props)."
}
if (-not $env:GITHUB_TOKEN) { throw "Не задан GITHUB_TOKEN (Settings → Developer settings → Personal access tokens)." }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Версия должна быть вида 1.2.0" }

& (Join-Path $root 'build.ps1') -Version $Version -UpdateRepo $Repo
$exe = Join-Path $root 'dist\TgCheck.exe'

$headers = @{
    Authorization          = "Bearer $($env:GITHUB_TOKEN)"
    Accept                 = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent'           = 'TgCheck-release'
}
$tag = "v$Version"
$body = @{ tag_name = $tag; name = "TG Check $Version"; body = $Notes; draft = $false; prerelease = $false } | ConvertTo-Json

Write-Host "Создаю релиз $tag в $Repo…"
$release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Repo/releases" -Headers $headers `
    -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json; charset=utf-8'

Write-Host "Загружаю TgCheck.exe…"
$uploadUrl = $release.upload_url -replace '\{.*\}$', ''
Invoke-RestMethod -Method Post -Uri "$($uploadUrl)?name=TgCheck.exe" -Headers $headers `
    -InFile $exe -ContentType 'application/octet-stream' | Out-Null

Write-Host ""
Write-Host "Готово: $($release.html_url)" -ForegroundColor Green
