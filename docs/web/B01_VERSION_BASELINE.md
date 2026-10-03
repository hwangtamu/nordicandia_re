任务 ID：B01（固定原版目标版本）
原版定义 ID：不适用（版本基线，非游戏机制）
日期：2026-10-03。核对人：Melon。

## 目标版本与证据

**主参考版本：Android 1.9.3（versionCode 507033）**
包名 `com.IterativeStudios.Nordicandia`，Unity 6（il2cpp metadata v39，
`global-metadata.dat` 头 `sanity=0xFAB11BAF`，24,246,868 字节）。
minSdk 25，targetSdk 36。XAPK 格式 v2，split：base + config.arm64_v8a + UnityDataAssetPack。

本地获取时间 2026-09-25 22:50（文件 mtime；为下载时间，**不是**官方版本发布时间；
Play 商店页面未记录该版本的发布时间）。

### 哈希（sha256）

官方原版：
```
bf3271deef5d6a653dd5762a0e9a8e7b13c59774924f09cf1cd3b29a0f539ce9  nord.xapk（外层，616M）
182eb85e34ea9174638b2cc155a81b1d9e132515164655655e03f018b608ab8c  ├─ com.IterativeStudios.Nordicandia.apk（base，111,901,582 B）
16df1c4b2cf509887c9085a2e4991ff17330e67821848fab954d69949582a13e  ├─ config.arm64_v8a.apk（42,985,023 B）
1950ca6e9a0da78302ba2804c18100fa341f05d99747e4ce812f1f4f2ac2fda9  └─ UnityDataAssetPack.apk（489,549,343 B）
```
`xapk/manifest.json` 内 `version_code=507033`、`version_name=1.9.3` 与 androguard
解析 AndroidManifest.xml 的结果一致；独立解出的 base/config APK 与 XAPK 流式解出
的哈希一致，确认为同一来源。

本地补丁构建（全部派生自上述官方 1.9.3，非官方发布）：
```
d9a9e2e99da70f188b57acf91468ed0b44cfaf7253ba86ee4028af80718ac5c3  v7audit/v7.xapk ＝ v8build/base.xapk（同一文件，未注入补丁）
ff6c1a516ca561bd1f207e5afb226683136378b749c517823389cc3ab142df4d  v8build/Nordicandia_1.9.3_private_arm64-v8a_online_v8_xp10_drop5.xapk
0f25fd5c1d55782c1ddd2674d2462d52dde0f21f92957e847260f12c54a93c19  v9build/Nordicandia_1.9.3_private_arm64-v8a_online_v9_xp10_drop5.xapk
8eb44d5c01c8c20bc3ad098b632266d70b292764458f249e5d1700c6e167a72b  v9build/v9unsigned.xapk
```
Git tag 谱系（均为轻量 tag，指向补丁提交；**不是官方 release**）：
`android-v1.9.3-4` … `android-v1.9.3-16`（无 `-11`，v11 未打 tag）。
v4–v16 均为同一官方 1.9.3 base 上的 hook 迭代（xp10/drop5、技能栏、宠物等）。

### Steam 版（本地无二进制）

- Steam App 1503790，Early Access，免费（Free on Demand）。
- SteamDB：public 分支 build 22330762，构建于 2026-03-13，更新于 2026-03-16；
  depots：1503791 Windows、1503792 Linux（1503793 macOS 曾存在后被移除）。
- 商店页不公布游戏内版本号；**Steam 版与 Android 1.9.3 的版本对应关系未知**。
- 本地无 Steam 二进制，无法提取哈希或反汇编证据。

## 原版行为与边界

- 本基线只固定“参考哪一个原版二进制”。所有标 `ClientVerified` 的结论，
  默认仅适用于 Android 1.9.3（507033），除非另有注明。
- Steam 版行为（赛季/排行榜/多人等，见 O01）不得用 Android 证据直接断言；
  反之亦然。跨平台断言需各自的二进制证据。

## 平台差异（已知）

| 维度 | Android 1.9.3（主参考） | Steam（EA，build 22330762） |
|---|---|---|
| 架构 | arm64-v8a only（config split） | Windows x64 / Linux x86_64（depot 分离） |
| 构建时间 | 未知（2026-09-25 获取；1.9.2 见于 2025-05，1.9.3 在其之后） | 2026-03-13 |
| 输入 | 触控 | 键鼠（仓库有 `patch-desktop-*.mjs`，曾分析过桌面端） |
| 在线功能 | 未核对（O01） | 商店页声明有 Season/Leaderboard/Hardcore |
| 二进制可得性 | ✅ 本地哈希验证 | ❌ 本地无，需用户提供或购买后下载 |

## 修改文件 / 运行时入口 / 前端表现

无运行时修改。新增本文档 `docs/web/B01_VERSION_BASELINE.md`。
后续 B02–B05、全部 `ClientVerified` 标注以此版本为锚点。

## 对照输入与预期输出

- 输入：`nord.xapk`；预期：`manifest.json` 的 version 字段 = androguard 解析结果
  = `1.9.3 / 507033`。✅ 一致。
- 输入：流式解出内层 APK 再哈希；预期：等于独立存放的 base/config APK 哈希。
  ✅ 一致（见上表）。

## 验证命令、结果、产物路径

```bash
sha256sum nordicandia/xapk/nord.xapk v7audit/v7.xapk v8build/base.xapk \
  "v8build/Nordicandia_1.9.3_private_arm64-v8a_online_v8_xp10_drop5.xapk" \
  "v9build/Nordicandia_1.9.3_private_arm64-v8a_online_v9_xp10_drop5.xapk" \
  v9build/v9unsigned.xapk
for f in com.IterativeStudios.Nordicandia.apk config.arm64_v8a.apk UnityDataAssetPack.apk; do
  unzip -p nordicandia/xapk/nord.xapk "$f" | sha256sum
done
unzip -p nordicandia/xapk/nord.xapk manifest.json
# androguard（已装入 nordicandia/xapk/venv）：APK(...).get_androidversion_code/name
python3 -c "import struct;print(struct.unpack('<II',open('nordicandia/xapk/assets/bin/Data/Managed/Metadata/global-metadata.dat','rb').read(8)))"
# → (0xfab11baf, 39) = Unity 6 metadata
git tag --list 'android-*' | sort -V
```
Steam 信息来源：SteamDB `app/1503790`（public build 22330762，2026-03-13）；
商店页 `store.steampowered.com/app/1503790`（商店页打开失败，信息取自搜索摘要，
版本号未公布）。

## 尚存差异

- Android 1.9.3 的官方发布时间未知（只有本地获取时间）。
- Steam 二进制本地不可得：哈希、版本对应关系、平台行为差异待补（需用户提供 Steam 客户端或授权下载）。
- v7.xapk 与 v8 base.xapk 哈希相同，说明 v7 构建实际未注入补丁或复用了 base；
  作为“补丁版本谱系”的一环，其内容有效性存疑，使用时以 tag 指向的提交为准。

## 状态：已还原

B01 完成。后续规则：任何 `ClientVerified` 标注必须能追溯到本基线版本；
引用 Steam 行为前先完成 O01 核对；不得混用版本后标注。
