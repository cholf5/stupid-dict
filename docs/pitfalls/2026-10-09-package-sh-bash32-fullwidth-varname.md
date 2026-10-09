# bash 3.2 会把紧邻全角字符的 `$VAR` 并进变量名：package.sh 在 macOS runner 必炸

- **状态**：已修复（ca771e1，`${DICTIONARY}` 括定界；v1.0.0 tag run 的 macOS job 已端到端验证）
- **日期**：2026-10-09
- **背景**：v1.0.0 发版，CI 测试 job 第一次活到打包阶段（此前被外部取消者连杀，打包 job 从未执行过——见 `2026-10-09-external-canceller-round2-release-playbook.md`），macOS 打包 job 立刻炸出这个潜伏 bug。`scripts/package.sh` 与 `scripts/release.sh` 是同一类坑的两只脚：AGENTS.md 里 release.sh 那条注记当时只当成它一个文件的事，实际是**所有会碰全角字符的 shell 脚本**的事。

## 症状

macOS (x64) job 的 `Package .app bundle` 步骤：

```
scripts/package.sh: line 36: DICTIONARY?: unbound variable
```

错误信息里的 `?` 是 U+FFFD 替换符——被并入变量名的高位字节在 runner 控制台渲染不出来，所以**报出来的变量名不是真正的名字**，第一次很难对上线 36 的 `$DICTIONARY`。同一个提交的 linux job 用同一行脚本安然通过。

## 机理

出错的行是给 CI runner 的提示分支：

```sh
[[ -f "$DICTIONARY" ]] || echo "提示: 未找到 dictionary.db（$DICTIONARY），跳过带词典包和 dictionary.zip"
```

`$DICTIONARY` 紧贴全角右括号 `）`。展开 `$NAME` 时 bash 沿「变量名字符」逐字吃字符；macOS 自带的 bash 3.2 在 UTF-8 locale 下 `isalpha()` 把高位字节也判成字母，`）`（E3 80 89）整个被并进变量名，名字变成 `DICTIONARY）…`——查无此变量，`set -u` 直接退出。Linux 的 bash 5 有正确的多字节名字边界判定，同一行永远不炸。

要点：

- 触发条件是「`$VAR` 后第一个字符是非 ASCII」，与引号无关（双引号里照样折叠）。
- macOS 上 `/bin/sh` 和 `bash` 都是 3.2；package.sh 是 `#!/usr/bin/env bash`，release.sh 是 POSIX sh——**shebang 救不了，问题在 bash 3.2 这一个二进制**。
- Linux 静默通过意味着同一脚本跨平台行为分化：macos 炸 + linux 绿 = 脚本可移植性 bug，不是环境问题。和 NU1903 那次「本地绿不算数」同构：**绿灯平台不能为红灯平台作证**。

## 为什么潜伏到 v1.0.0 才爆

两层互相掩护：

1. **该行是条件分支**：只有默认 `$DICTIONARY` 路径不存在时才执行。开发者机器上永远有 dictionary.db，只有 CI runner 走这条分支——「本地不炸」是必然，不是健康信号。
2. **打包 job 此前从未被执行过**：打包 job 需要 test job 先绿，而 test job 在发版日被外部取消者连杀七次。第一次活到打包就撞上了它。

## 修复与扫描

修复：`${DICTIONARY}` 花括号定界，名字边界唯一。修复在本地 macOS bash 3.2 上验证过（`package.sh --dictionary <不存在路径>` 跑提示分支，正常打印继续），随后 tag run 全绿的 macOS job 端到端确认。

同类隐患扫描（`$NAME` 后紧跟非 ASCII）：

```python
import re, pathlib
pat = re.compile(r'\$([A-Za-z_][A-Za-z0-9_]*)([^\x00-\x7f])')
for p in pathlib.Path('scripts').glob('*'):
    for i, line in enumerate(p.read_text(encoding='utf-8').splitlines(), 1):
        for m in pat.finditer(line):
            print(f"{p}:{i}: ${m.group(1)} followed by {m.group(2)!r}")
```

当天扫描结果：全目录仅 package.sh:36 一处。**新增带中文提示的脚本时，`${VAR}` 应当是默认写法**，不要依赖事后扫描。

## 教训浓缩

1. macOS bash 3.2 对 `$VAR` 的名字边界是坏的：紧邻全角字符必须 `${VAR}`。AGENTS.md 的注记已从 release.sh 推广到 scripts/ 全部脚本。
2. 条件分支里的提示文案是 CI 专属路径，本地永远测不到——写「只有 runner 会走」的分支时按「必然在 CI 首跑」来审查。
3. 绿灯平台不能为红灯平台的脚本行为作证。
4. 操作性故障（取消者）会掩盖潜伏 bug、也会被潜伏 bug 消耗重试预算——修一个的时候顺手问「还有没有同类的」。
