# Шифрует ключи из .env для встраивания в exe (вызывается из MSBuild, см. TgCheck.App.csproj).
# Берутся только строки TG_*; результат: 16 байт IV + AES-256-CBC. Ключ выводится из той же фразы, что и в AppEnv.cs.
# Это защита от простого просмотра строк/ресурсов exe, а не от разбора программы декомпилятором.
param(
    [Parameter(Mandatory = $true)][string]$EnvFile,
    [Parameter(Mandatory = $true)][string]$OutFile
)
$ErrorActionPreference = 'Stop'

$lines = Get-Content -LiteralPath $EnvFile -Encoding UTF8 |
    Where-Object { $_ -match '^\s*(export\s+)?TG_[A-Z_]+\s*=' }
$plain = [System.Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))

$sha = [System.Security.Cryptography.SHA256]::Create()
$key = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes('TgCheck.EmbeddedEnv.v1|7f3c9a1e'))

$aes = [System.Security.Cryptography.Aes]::Create()
$aes.Key = $key
$aes.GenerateIV()
$cipher = $aes.CreateEncryptor().TransformFinalBlock($plain, 0, $plain.Length)

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutFile) | Out-Null
[System.IO.File]::WriteAllBytes($OutFile, [byte[]]($aes.IV + $cipher))
