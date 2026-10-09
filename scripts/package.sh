#!/usr/bin/env bash
# Builds every release asset into dist/:
#   StupidDict-{rid}.zip                  — app only (small)
#   StupidDict-{rid}-with-dictionary.zip  — app + dictionary.db (offline install)
#   dictionary.zip + .sha256              — for the in-app first-run download
#   audio-pack.zip + .sha256              — pronunciation pack, if one was built
#
# Usage: scripts/package.sh [--rids "osx-arm64 win-x64 ..."] [--dictionary <db>] [--audio-pack <zip>]
# App packages upload to the version Release; dictionary.zip / audio-pack.zip
# (+ .sha256) upload to the separate data prerelease (data-N, see
# ReleaseAssets.DataTag) — never one shared "gh release create vX dist/*": a
# data release must not compete for releases/latest, which the in-app update
# check reads. See the summary this script prints and README「打包与发布」.
# Windows Setup 安装包（StupidDict-{ver}-win-x64-setup.exe，scripts/StupidDict.iss）
# 只在 CI 出：Inno Setup 的 ISCC 仅 Windows，本脚本（macOS）仍只出绿色 zip。

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

# RID whitelist: rids are spliced into rm -rf / output paths below, so reject
# anything that is not a plain lowercase RID ("../.." must never reach rm -rf).
# The regex lives in a variable: unquoted =~ literals are flaky on bash 3.2
# (the stock macOS bash this script must survive).
rid_re='^[a-z0-9]+(-[a-z0-9.]+)*$'
for rid in $RID_LIST; do
  [[ "$rid" =~ $rid_re ]] || {
    echo "非法 RID: ${rid}（须形如 osx-arm64 / win-x64：小写字母数字，短横线或点分段，不含路径分隔符）"
    exit 1
  }
done

# macOS .app 的 Info.plist 是静态 heredoc，Finder「显示简介」读
# CFBundleShortVersionString —— 从 csproj <Version> 注入（与发版 tag 同一来源），
# 别再让 .app 永远显示 1.0。读不到时回退 1.0（本机临时打包的兜底）。
APP_VERSION=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$REPO_ROOT/src/StupidDict.App/StupidDict.App.csproj" | head -1 | tr -d '[:space:]')
APP_VERSION=${APP_VERSION:-1.0}

sha256_file() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1"
  else shasum -a 256 "$1"; fi
}

# Ad-hoc sign the assembled .app: the single-file apphost's embedded signature
# covers only the executable itself, so a bundle that was never re-sealed
# fails `codesign --verify`, and a *downloaded* copy (quarantine attribute set)
# is rejected by Gatekeeper as "damaged" with no Allow-Anyway path — that
# escape hatch exists only for a valid signature from an unidentified
# developer. Ad-hoc keeps the seal intact, so the downloaded copy gets the
# standard unidentified-developer prompt instead. Notarization would remove
# the prompt entirely but needs a paid Apple Developer account. The
# with-dictionary variant drops dictionary.db into the bundle after the first
# zip, so it must re-sign: a stale CodeResources seal is as broken as none.
sign_app() {
  local app="$1"
  if command -v codesign >/dev/null 2>&1; then
    codesign --force -s - "$app" || { echo "ad-hoc 签名失败: ${app}" >&2; exit 1; }
    codesign --verify --strict "$app" || { echo "签名校验失败（封签不完整）: ${app}" >&2; exit 1; }
  else
    echo "警告: 本机无 codesign（Linux 交叉构建 osx 产物），${app} 未封签——本机可直接运行，但从网络下载分发会被 Gatekeeper 判 damaged" >&2
  fi
}

cd "$REPO_ROOT"
mkdir -p "$DIST"
[[ -f "$DICTIONARY" ]] || echo "提示: 未找到 dictionary.db（${DICTIONARY}），跳过带词典包和 dictionary.zip"

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
    cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
  <dict>
    <key>CFBundleName</key><string>Stupid Dict</string>
    <key>CFBundleExecutable</key><string>StupidDict</string>
    <key>CFBundleIdentifier</key><string>com.cholf5.stupiddict</string>
    <key>CFBundleShortVersionString</key><string>${APP_VERSION}</string>
    <key>CFBundleIconFile</key><string>app-icon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleHighResolutionCapable</key><true/>
    <key>NSHighResolutionCapable</key><true/>
  </dict>
</plist>
PLIST
    sign_app "$APP"
  else
    cp "$PUBLISHED" "$STAGE/"
  fi
  APP_ZIP="$DIST/StupidDict-$rid.zip"
  # zip -r into an existing file UPDATES it instead of rebuilding: entries from
  # an earlier run would survive (stale layout / swapped dictionary.db mixed
  # with new). Always start from a fresh archive.
  rm -f "$APP_ZIP"
  (cd "$STAGE" && zip -q -r -X "$APP_ZIP" .)
  echo "    $APP_ZIP"

  # 2) drop the dictionary in, then zip the full variant.
  if [[ -f "$DICTIONARY" ]]; then
    if [[ "$rid" == osx-* ]]; then
      # Resources 而非 MacOS：封签把 MacOS/ 里主执行文件之外的一切当嵌套代码，
      # 拒签普通数据文件（AppPaths 侧按 ../Resources 探测）。
      cp "$DICTIONARY" "$STAGE/Stupid Dict.app/Contents/Resources/dictionary.db"
      sign_app "$STAGE/Stupid Dict.app"
    else
      cp "$DICTIONARY" "$STAGE/dictionary.db"
    fi
    FULL_ZIP="$DIST/StupidDict-$rid-with-dictionary.zip"
    rm -f "$FULL_ZIP"
    (cd "$STAGE" && zip -q -r -X "$FULL_ZIP" .)
    echo "    $FULL_ZIP"
  fi
done

if [[ -f "$DICTIONARY" ]]; then
  echo "==> dictionary.zip"
  # Same rebuild-not-update rule as the app zips above.
  rm -f "$DIST/dictionary.zip"
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
1. 应用包进版本 Release。正常发版走 scripts/release.sh（CI 打包并建 Release，
   无需本步）；仅本地手动兜底时:
     gh release create vX dist/StupidDict-*.zip --title "..." --notes "..."
   注意: 不能 gh release create vX dist/* —— dist/ 里还有 dictionary.zip /
   audio-pack.zip（含 .sha256），它们必须进独立 prerelease data-N；传进 App
   Release 会挤占 releases/latest 语义（应用内检查更新读的就是那个页面）。
2. 数据资产进 data prerelease（仅首次发布或数据更新时）:
     gh release create data-N dist/dictionary.zip dist/dictionary.zip.sha256 \
       dist/audio-pack.zip dist/audio-pack.zip.sha256 --prerelease
   应用内下载钉在 data-N（ReleaseAssets.DataTag）；资产名必须保持:
     dictionary.zip / audio-pack.zip / dictionary.zip.sha256 / audio-pack.zip.sha256
3. macOS 用户首次打开 .app: Gatekeeper 会提示无法验证开发者（ad-hoc 签名、
   未公证）——右键「打开」一次，或系统设置 → 隐私与安全性 → 仍要打开；
   彻底绕过: xattr -cr "Stupid Dict.app"
NEXT
