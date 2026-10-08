#!/usr/bin/env bash
# Builds every release asset into dist/:
#   StupidDict-{rid}.zip                  — app only (small)
#   StupidDict-{rid}-with-dictionary.zip  — app + dictionary.db (offline install)
#   dictionary.zip + .sha256              — for the in-app first-run download
#   audio-pack.zip + .sha256              — pronunciation pack, if one was built
#
# Usage: scripts/package.sh [--rids "osx-arm64 win-x64 ..."] [--dictionary <db>] [--audio-pack <zip>]
# Upload with: gh release create v1.x dist/* — see the summary the script prints.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIST="$REPO_ROOT/dist"
RID_LIST=""
DICTIONARY="${DICTIONARY:-$HOME/Library/Application Support/StupidDict/dictionary.db}"
AUDIO_PACK=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rids) RID_LIST="$2"; shift 2 ;;
    --dictionary) DICTIONARY="$2"; shift 2 ;;
    --audio-pack) AUDIO_PACK="$2"; shift 2 ;;
    *) echo "未知参数: $1"; exit 1 ;;
  esac
done
RID_LIST=${RID_LIST:-"osx-arm64"}

sha256_file() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1"
  else shasum -a 256 "$1"; fi
}

cd "$REPO_ROOT"
mkdir -p "$DIST"
[[ -f "$DICTIONARY" ]] || echo "提示: 未找到 dictionary.db（$DICTIONARY），跳过带词典包和 dictionary.zip"

for rid in $RID_LIST; do
  echo "==> publish $rid"
  # Windows needs the windows TFM: it unlocks System.Speech for the TTS fallback.
  case "$rid" in
    win-*) TFM="net10.0-windows" ;;
    *) TFM="net10.0" ;;
  esac
  dotnet publish src/StupidDict.App -c Release -f "$TFM" -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "dist/publish/$rid"

  EXE="StupidDict"
  [[ "$rid" == win-* ]] && EXE="StupidDict.exe"
  PUBLISHED="dist/publish/$rid/$EXE"
  [[ -f "$PUBLISHED" ]] || { echo "发布产物缺失: $PUBLISHED"; exit 1; }

  STAGE="dist/stage/$rid"
  rm -rf "$STAGE"; mkdir -p "$STAGE"

  # 1) app-only stage: no dictionary anywhere inside, then zip.
  if [[ "$rid" == osx-* ]]; then
    # A minimal .app bundle so macOS users get a double-clickable program.
    APP="$STAGE/Stupid Dict.app"
    mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
    cp "$PUBLISHED" "$APP/Contents/MacOS/StupidDict"
    chmod +x "$APP/Contents/MacOS/StupidDict"
    # Same icns MacDockIcon embeds for bare `dotnet run`; LaunchServices reads it
    # from CFBundleIconFile for the packaged bundle.
    cp src/StupidDict.App/Assets/app-icon.icns "$APP/Contents/Resources/app-icon.icns"
    cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
  <dict>
    <key>CFBundleName</key><string>Stupid Dict</string>
    <key>CFBundleExecutable</key><string>StupidDict</string>
    <key>CFBundleIdentifier</key><string>com.cholf5.stupiddict</string>
    <key>CFBundleShortVersionString</key><string>1.0</string>
    <key>CFBundleIconFile</key><string>app-icon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleHighResolutionCapable</key><true/>
    <key>NSHighResolutionCapable</key><true/>
  </dict>
</plist>
PLIST
  else
    cp "$PUBLISHED" "$STAGE/"
  fi
  APP_ZIP="$DIST/StupidDict-$rid.zip"
  (cd "$STAGE" && zip -q -r -X "$APP_ZIP" .)
  echo "    $APP_ZIP"

  # 2) drop the dictionary in, then zip the full variant.
  if [[ -f "$DICTIONARY" ]]; then
    if [[ "$rid" == osx-* ]]; then
      cp "$DICTIONARY" "$STAGE/Stupid Dict.app/Contents/MacOS/dictionary.db"
    else
      cp "$DICTIONARY" "$STAGE/dictionary.db"
    fi
    FULL_ZIP="$DIST/StupidDict-$rid-with-dictionary.zip"
    (cd "$STAGE" && zip -q -r -X "$FULL_ZIP" .)
    echo "    $FULL_ZIP"
  fi
done

if [[ -f "$DICTIONARY" ]]; then
  echo "==> dictionary.zip"
  # -j puts dictionary.db at the zip root, exactly what the in-app importer expects.
  (cd "$(dirname "$DICTIONARY")" && zip -q -j "$DIST/dictionary.zip" "$(basename "$DICTIONARY")")
  (cd "$DIST" && sha256_file dictionary.zip > dictionary.zip.sha256)
fi

if [[ -n "$AUDIO_PACK" ]]; then
  cp "$AUDIO_PACK" "$DIST/audio-pack.zip"
elif [[ -f "$REPO_ROOT/audio-pack.zip" ]]; then
  cp "$REPO_ROOT/audio-pack.zip" "$DIST/audio-pack.zip"
fi
if [[ -f "$DIST/audio-pack.zip" ]]; then
  echo "==> audio-pack.zip"
  (cd "$DIST" && sha256_file audio-pack.zip > audio-pack.zip.sha256)
fi

rm -rf dist/stage dist/publish

echo
echo "=== dist/ ==="
ls -lh "$DIST" | awk 'NR>1 {print "  " $9 "  (" $5 ")"}'

cat <<'NEXT'

=== 发布步骤（手动） ===
1. gh release create v1.x dist/* --title "..." --notes "..."
2. 应用内下载依赖 Release 的 latest 语义，资产名必须保持:
   dictionary.zip / audio-pack.zip / dictionary.zip.sha256 / audio-pack.zip.sha256
3. macOS 用户首次打开 .app 如被 Gatekeeper 拦截: xattr -cr "Stupid Dict.app"
NEXT
