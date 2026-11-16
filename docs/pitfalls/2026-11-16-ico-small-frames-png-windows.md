# ico 全帧 PNG 压缩：Windows shell 对 <256px PNG 帧取帧不可靠，桌面图标可能显小

- **状态**：已修复（ico 帧格式改 Vista 惯例：256=PNG、<256=32bpp BMP；素材像素零漂移），回归测试 `IcoSmallFramesAreBmpAndOnly256IsPng`
- **日期**：2026-11-16
- **背景**：用户报告 Windows 安装后桌面快捷方式图标偏小。上一次图标问题（2026-10-09 任务栏显小）根因是 Apple 网格边距烤进 ico，修的是**画布占用**；这次几何层面已无问题，坑在更深一层的**容器编码**。

## 排查过程

1. **排除发版错包**：仓库 `app-icon.ico` 与 v1.2.0 tag 的 blob sha256 一致，安装包图标即仓库当前素材。
2. **排除素材几何**：逐帧解析 alpha——16..256 帧 bbox 全为满帧、不透明像素占比 0.91–0.95（透明只在烤进去的圆角弧），渲染到 /tmp 蒙太奇目检内容顶边。`WindowsTaskbarIconArtworkIsFullBleed` 钉的满幅几何仍在。
3. **找到实锤**：解析 ICONDIRENTRY 看每帧载荷签名——**7 帧全部 PNG 魔数开头**（16/24/32/48/64/128/256 全是 PNG 压缩）。

## 症状与根因

Vista 图标惯例（Microsoft 图标文档 + ICO 规范；GIMP/ImageMagick 均按此出图）：**PNG 压缩只用于 256 帧，更小帧必须是 32bpp BMP/DIB**。Pillow ≥9.3 的 `Image.save(format="ICO")` 默认把**所有帧**写成 PNG（只有显式 `bitmap_format="bmp"` 才落 BMP，且会把 256 也写成 BMP——无法混合），本仓自 2026-10-08 首版图标起一直用该默认路径。

Windows shell 对小尺寸 PNG 帧的解码在多条取帧路径上不可靠（文档化的兼容性风险；具体回退行为随 Windows 版本而异，无法在 macOS 侧复现渲染）——取帧失败时落到错误回退帧，桌面图标就可能渲染得比满幅邻居小一圈。

## 修复

`make-icon.py` 弃用 `Image.save(format="ICO")`，自拼容器（`write_ico`/`ico_bmp_frame`）：

- **256 帧**：PNG 压缩（canonical，全平台支持）；
- **<256 帧**：32bpp BMP/DIB——40 字节 BITMAPINFOHEADER（**biHeight 翻倍**：XOR 位图 + 下方 AND mask）、BGRA **自底向上**逐行、全零 AND mask 占位（32bpp 下 alpha 决定透明度，mask 只为满足老解析器的 stride 预期；在线转换器搞坏 BMP alpha 是另一类经典图标坑，别在这里塞垃圾）；
- 脚本自检：写完即解析回读，逐帧断言签名（256=PNG 魔数、小帧 `biSize=40`）。

再生成走真源 `design/icon-candidates/b-derp-book-v5-master.png` 端到端重跑：**png/icns 与改前逐字节一致**（Apple 网格路径零扰动），ico 的 256 帧 PNG 与改前逐字节一致、小帧与旧 PNG 帧**逐像素一致**（LANCZOS 确定性 + 同 Pillow 版本）——纯容器编码变化，素材零漂移。文件 22KB → 110KB（小帧变原始 BMP，exe 内嵌量级无碍）。

防线：`IcoSmallFramesAreBmpAndOnly256IsPng` 手解 ICONDIR 逐帧断言（256 帧必须是 PNG、小帧必须有 `biSize=40/biHeight=2h/32bpp/BI_RGB` 头且带全零 AND mask、尺寸集合固定）—— regenerated 回全 PNG 在 CI 即拦。

## 排查教训

1. **素材像素对 ≠ 容器格式对**。图标资产有两层可错：画布几何（上次）与容器编码（这次）。消费端渲染规则（有无蒙版/缩放/补边、认不认某种帧编码）每层都要单独对着平台文档核实，别因为上一层的断言绿了就默认这层没事。
2. **生成脚本的隐式行为随依赖版本漂移**。`save(format="ICO")` 的帧编码策略是 Pillow 的实现细节（9.3 起默认全 PNG），不是 ICO 规范的承诺——凡「生成的二进制要喂给另一个平台解析」的路径，容器格式要显式钉住（测试 + 脚本自检双防线），不能靠库的默认值长期正确。
3. **一个量测数定位一类根因**（同任务栏那次的手法）：帧载荷前 4 字节是 `\x89PNG` 还是 `\x28\x00\x00\x00`（biSize=40），一个签名就把「几何问题」和「编码问题」切干净。
4. **桌面图标显小的另一常见原因是 shell 图标缓存陈旧**（升级安装后旧素材残留）。本次格式修复随下个发版生效；若升级后现场仍显小而任务栏/开始菜单正常，先重建图标缓存（`ie4uinit -show`、或删 `%LOCALAPPDATA%\Microsoft\Windows\Explorer\iconcache_*.db` 后重启 explorer）再观察，别急着改素材。
