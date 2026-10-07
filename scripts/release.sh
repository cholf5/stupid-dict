#!/bin/sh
# Release script: bump csproj <Version> -> commit -> tag vX.Y.Z -> push; CI
# (.github/workflows/dotnet-desktop.yml) then tests, packages all three
# platforms, carries dictionary.zip / audio-pack.zip forward from the previous
# release, and creates the GitHub Release.
#
# Usage: scripts/release.sh <x.y.z> [--skip-test] [--watch]
#   --skip-test  skip local dotnet test (CI still runs them; a failing test
#                produces no release)
#   --watch      poll CI after push and verify the release assets are complete
#                (requires gh and a login)
#
# Recovery: if CI fails the tag is already pushed. After fixing:
#   git push origin :refs/tags/vX.Y.Z && git tag -d vX.Y.Z
# then rerun this script (a csproj already at the target version skips the
# bump commit). The version must equal the csproj <Version>; the workflow
# enforces this on tags.
#
# Note: variables adjacent to full-width characters in echo must be written
# ${VAR}, or macOS sh (bash 3.2) folds multibyte characters into the variable
# name and dies with unbound variable (UTF-8 locales report high bytes as
# alpha in isalpha).
set -eu
cd "$(dirname "$0")/.."

usage() { echo "用法: scripts/release.sh <x.y.z> [--skip-test] [--watch]" >&2; exit 1; }

VERSION=""
SKIP_TEST=false
WATCH=false
for arg in "$@"; do
  case "$arg" in
    --skip-test) SKIP_TEST=true ;;
    --watch) WATCH=true ;;
    *) VERSION=$arg ;;
  esac
done
[ -n "$VERSION" ] || usage

# Three numeric segments, matching the update checker's version comparison.
echo "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' || { echo "版本号须为 x.y.z 三段数字：$VERSION" >&2; exit 1; }

PROJECT=src/StupidDict.App/StupidDict.App.csproj
TAG=v$VERSION

[ "$(git symbolic-ref --short HEAD)" = main ] || { echo "不在 main 分支" >&2; exit 1; }
git fetch --quiet
BEHIND=$(git rev-list --count HEAD..@{u})
[ "$BEHIND" -eq 0 ] || { echo "main 落后远端 $BEHIND 个提交，先 pull" >&2; exit 1; }
git diff --quiet && git diff --cached --quiet || { echo "工作树有未提交改动，先提交或暂存" >&2; exit 1; }
UNTRACKED=$(git ls-files --others --exclude-standard | wc -l | tr -d ' ')
[ "$UNTRACKED" -eq 0 ] || echo "注意：有 $UNTRACKED 个未跟踪文件，不会进入本次发布" >&2

# "Already released" really means the tag exists; a matching csproj version
# only means no bump is needed (a pre-written first version and a failed
# release retry both land on this path).
git rev-parse -q --verify "refs/tags/$TAG" >/dev/null && { echo "本地已存在 $TAG" >&2; exit 1; }
[ -z "$(git ls-remote --tags origin "refs/tags/$TAG")" ] || { echo "远端已存在 $TAG" >&2; exit 1; }

CUR=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$PROJECT" | head -1 | tr -d '[:space:]')
[ -n "$CUR" ] || { echo "无法从 $PROJECT 读取 <Version>" >&2; exit 1; }

if [ "$SKIP_TEST" != true ]; then
  echo "本地跑测试（--skip-test 可跳过）..."
  dotnet test StupidDict.slnx --nologo -v q || { echo "本地测试失败，中止" >&2; exit 1; }
fi

if [ "$CUR" != "$VERSION" ]; then
  # Replace <Version>, writing through a temp file for BSD/GNU sed compat.
  TMP=$(mktemp)
  sed "s#\(<Version>\)[^<]*\(</Version>\)#\1$VERSION\2#" "$PROJECT" > "$TMP"
  mv "$TMP" "$PROJECT"
  NEW=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$PROJECT" | head -1 | tr -d '[:space:]')
  [ "$NEW" = "$VERSION" ] || { echo "版本替换校验失败：期望 ${VERSION}，实得 $NEW" >&2; exit 1; }

  git add "$PROJECT"
  git commit -m "Bump version to $VERSION"
else
  echo "csproj 已是 ${VERSION}，跳过 bump 提交，直接打 tag"
fi

git tag -a "$TAG" -m "Stupid Dict $TAG"
git push origin main "$TAG"

SLUG=$(git remote get-url origin | sed -E 's#.*github\.com[:/]##; s#\.git$##')
RUN_URL="https://github.com/$SLUG/actions"
REL_URL="https://github.com/$SLUG/releases/tag/$TAG"

if [ "$WATCH" != true ]; then
  echo "已推送 ${TAG}，CI 会自动打包并发布："
  echo "  Actions: $RUN_URL"
  echo "  Release: ${REL_URL}（流水线跑完后出现）"
  exit 0
fi

command -v gh >/dev/null 2>&1 || { echo "未安装 gh，无法 watch，手动看 $RUN_URL" >&2; exit 1; }

# Find the run triggered by the tag (registration takes a few seconds after
# push). Filter by the commit the tag points at: re-releasing the same
# version has an older run matching --branch's tag name, and taking the first
# by branch alone would grab that stale run.
TAG_SHA=$(git rev-parse "$TAG^{commit}")
RUN_ID=
i=0
while [ $i -lt 12 ]; do
  RUN_ID=$(gh run list --branch "$TAG" --limit 10 --json databaseId,headSha \
    --jq "[.[] | select(.headSha == \"$TAG_SHA\")][0].databaseId" 2>/dev/null || true)
  [ -n "$RUN_ID" ] && [ "$RUN_ID" != null ] && break
  i=$((i + 1)); sleep 5
done
if [ -z "$RUN_ID" ] || [ "$RUN_ID" = null ]; then
  echo "未找到 $TAG 触发的 CI run，手动看 $RUN_URL" >&2
  exit 1
fi
echo "等待 CI（run ${RUN_ID}，最长 45 分钟）..."

i=0
while [ $i -lt 90 ]; do
  STATE=$(gh run view "$RUN_ID" --json status,conclusion --jq '.status+"/"+(.conclusion // "-")' 2>/dev/null || echo unknown)
  case "$STATE" in
    completed/success) break ;;
    completed/*)
      echo "CI 失败（${STATE}）。修复后删除 tag 重跑：git push origin :refs/tags/$TAG && git tag -d $TAG" >&2
      echo "日志：$RUN_URL/$RUN_ID" >&2
      exit 1
      ;;
  esac
  i=$((i + 1)); sleep 30
done
[ $i -lt 90 ] || { echo "等待超时（45 分钟），手动看 $RUN_URL/$RUN_ID" >&2; exit 1; }

# Verify the platform builds and the data assets are present. audio-pack is
# optional (only released once built locally); dictionary.zip is required —
# the in-app first-run download resolves against the latest release.
ASSETS=$(gh release view "$TAG" --json assets --jq '[.assets[].name] | join(",")' 2>/dev/null || true)
MISSING=
for want in "StupidDict-$VERSION-osx-arm64.zip" "StupidDict-$VERSION-osx-x64.zip" \
  "StupidDict-$VERSION-win-x64.zip" "StupidDict-$VERSION-linux-x64.zip" "dictionary.zip"; do
  case ",$ASSETS," in
    *",$want,"*) ;;
    *) MISSING="$MISSING $want" ;;
  esac
done
if [ -n "$MISSING" ]; then
  echo "Release 产物缺失：${MISSING}。可在 Actions 页 Re-run release job，或删 tag 重跑脚本。" >&2
  exit 1
fi
case ",$ASSETS," in
  *,audio-pack.zip,*) ;;
  *) echo "注意：本版无 audio-pack.zip，发音包回退系统语音；构建后可手动补传。" >&2 ;;
esac

echo "发版完成 ${TAG}：$REL_URL"
echo "产物：$ASSETS"
