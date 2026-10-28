# Сборка TgCheck в один exe-файл: dist\TgCheck.exe
# Запуск:  powershell -ExecutionPolicy Bypass -File .\build.ps1
#   -Version 1.2.0          — версия приложения (по умолчанию из Directory.Build.props)
#   -UpdateRepo owner/repo  — GitHub-репозиторий для автообновлений (по умолчанию из Directory.Build.props)
#   -FrameworkDependent     — маленький exe, но на ПК должен быть установлен .NET 8 Desktop Runtime
param(
    [string]$Version,
    [string]$UpdateRepo,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$project = Join-Path $root 'src\TgCheck.App\TgCheck.App.csproj'

# .env в dist сохраняем между сборками
$keepEnv = $null
$distEnv = Join-Path $dist '.env'
if (Test-Path $distEnv) { $keepEnv = Get-Content $distEnv -Raw -Encoding UTF8 }
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$extra = @()
if ($Version) { $extra += "-p:Version=$Version" }
if ($UpdateRepo) { $extra += "-p:UpdateRepo=$UpdateRepo" }

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained $selfContained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=$selfContained `
    -p:DebugType=none `
    -p:GenerateDocumentationFile=false `
    @extra `
    -o $dist

if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с ошибкой ($LASTEXITCODE)" }

Copy-Item (Join-Path $root '.env.example') $dist
$envFile = Join-Path $root '.env'
if (Test-Path $envFile) { Copy-Item $envFile $dist }
elseif ($keepEnv) { Set-Content -Path $distEnv -Value $keepEnv -Encoding UTF8 -NoNewline }

$exe = Join-Path $dist 'TgCheck.exe'
$size = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Готово: $exe ($size МБ)" -ForegroundColor Green
if (-not (Test-Path $distEnv)) {
    Write-Host "Не забудьте создать dist\.env по образцу .env.example" -ForegroundColor Yellow
}
