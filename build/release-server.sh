#!/usr/bin/env bash
# Выпуск релиза на сервере сборки: TgCheck.exe + патч от последней опубликованной версии + SHA-256.
#
#   build/release-server.sh 2.2.4 /root/notes-2.2.4.md
#
# Нужны: /root/tgcheck-build (этот репозиторий) с .env, официальный SDK в /root/.dotnet-ms, токен GitHub в /root/.gh-token,
# zstd, openssl, python3. Создаётся черновик релиза — опубликовать его нужно вручную на GitHub.
set -euo pipefail

VERSION=${1:?версия, например 2.2.4}
NOTES=${2:?файл с описанием релиза (markdown)}
REPO=olegabdhse/tg-check
API=https://api.github.com/repos/$REPO
TOKEN=$(cat /root/.gh-token)
AUTH="Authorization: Bearer $TOKEN"
cd /root/tgcheck-build
git pull -q
echo "commit $(git log --oneline -1 | cut -c1-8)"

# ---- ключи из .env (только TG_*) → obj/env.bin: 16 байт IV + AES-256-CBC (как build/embed-env.ps1)
python3 - <<'PY'
import re
lines = [l.rstrip("\r\n") for l in open(".env", encoding="utf-8-sig")]
keep = [l for l in lines if re.match(r"^\s*(export\s+)?TG_[A-Z_]+\s*=", l)]
open("/tmp/env.plain", "wb").write("\n".join(keep).encode("utf-8"))
PY
KEY=$(printf "%s" "TgCheck.EmbeddedEnv.v1|7f3c9a1e" | sha256sum | cut -d" " -f1)
IV=$(openssl rand -hex 16)
{ printf "%s" "$IV" | xxd -r -p; openssl enc -aes-256-cbc -K "$KEY" -iv "$IV" -in /tmp/env.plain; } > src/TgCheck.App/obj/env.bin
tail -c +17 src/TgCheck.App/obj/env.bin | openssl enc -d -aes-256-cbc -K "$KEY" -iv "$IV" | cmp -s - /tmp/env.plain
rm -f /tmp/env.plain

# ---- сборка
touch src/TgCheck.App/obj/env.bin src/TgCheck.App/obj/vlc-3.0.21.zip src/TgCheck.App/obj/logo/app.*
rm -rf dist
DOTNET_CLI_TELEMETRY_OPTOUT=1 /root/.dotnet-ms/dotnet publish src/TgCheck.App/TgCheck.App.csproj -c Release -r win-x64 \
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:GenerateDocumentationFile=false \
    -p:EnableWindowsTargeting=true -p:Version="$VERSION" -o dist -v q -nologo
EXE=dist/TgCheck.exe
sha256sum "$EXE" | cut -d" " -f1 > dist/TgCheck.exe.sha256
echo "exe $(stat -c %s "$EXE") байт"

# ---- патч от последней опубликованной версии: программа этой версии скачает его вместо всего exe
ASSETS=("$EXE" dist/TgCheck.exe.sha256)
PREV=$(curl -s -H "$AUTH" "$API/releases/latest" | python3 -c "import json,sys; print(json.load(sys.stdin).get('tag_name','').lstrip('vV'))")
if [ -n "$PREV" ] && [ "$PREV" != "$VERSION" ]; then
    curl -sL -o /tmp/prev.exe "https://github.com/$REPO/releases/download/v$PREV/TgCheck.exe"
    PATCH=dist/TgCheck-$PREV.patch
    zstd -q -f --patch-from=/tmp/prev.exe "$EXE" -o "$PATCH" --long=28 -19
    # Проверка: из прошлого exe и патча собирается ровно новый.
    zstd -q -d -f --patch-from=/tmp/prev.exe "$PATCH" -o /tmp/check.exe --long=28
    cmp -s /tmp/check.exe "$EXE"
    rm -f /tmp/prev.exe /tmp/check.exe
    echo "патч от $PREV: $(stat -c %s "$PATCH") байт"
    ASSETS+=("$PATCH")
fi

# ---- черновик релиза и файлы
python3 -c "import json,sys; print(json.dumps({'tag_name':'v$VERSION','target_commitish':'main','name':'TG Check $VERSION',
'body':open(sys.argv[1],encoding='utf-8').read(),'draft':True}))" "$NOTES" > /tmp/rel.json
ID=$(curl -s -H "$AUTH" -H "Accept: application/vnd.github+json" -d @/tmp/rel.json "$API/releases" \
    | python3 -c "import json,sys; print(json.load(sys.stdin)['id'])")
for f in "${ASSETS[@]}"; do
    curl -s -o /dev/null -w "$(basename "$f"): %{http_code}\n" -H "$AUTH" -H "Content-Type: application/octet-stream" \
        --data-binary @"$f" "https://uploads.github.com/repos/$REPO/releases/$ID/assets?name=$(basename "$f")"
done
echo "черновик v$VERSION готов (id $ID) — опубликуйте его на GitHub"
