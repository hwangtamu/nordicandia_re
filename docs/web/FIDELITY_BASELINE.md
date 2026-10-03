# 完整复刻基线（B01）

日期：2026-10-03。对应 [完整复刻任务清单](FULL_FIDELITY_BACKLOG.md) 的 **B01：固定原版目标版本**。
本文只记录参考版本、哈希与平台差异；不改运行时代码。

## 主参考版本

| 项 | 值 |
|---|---|
| 包名 | `com.IterativeStudios.Nordicandia` |
| 版本 | **1.9.3** |
| versionCode | **507033** |
| 主参考平台 | Android arm64-v8a（`dist/android-arm64-release/`） |
| 提取 IL2CPP | `tmp/apk-libil2cpp.so` |

主参考选 Android 1.9.3：现有反汇编、属性公式、IL2CPP 工具链和绝大部分 ClientVerified 结论都基于该二进制；`tmp/android-metadata.json` 与 Cpp2IL `tmp/cpp2il-isil2/IsilDump/` 同源。

## 次参考版本

| 项 | 值 |
|---|---|
| 来源 | Steam 桌面构建（`steam_analysis/`） |
| Unity | `6000.2.8f1 (c9992ac36c34)`（`steam_analysis/Player.log`） |
| 类型转储 | `steam_analysis/dump.cs`（Il2CppInspector stub，仅用于类型/字段/方法签名与偏移） |
| 用途 | 类型定义、字段偏移、`desktop_*` 事件表；**不用于 C# 方法体逻辑**（stub 无实现） |

Steam 游戏 build id 未在仓库内找到；如后续需要以 Steam 为准的结论，先补 appmanifest/buildid 再标 ClientVerified。

## 参考产物哈希（sha256）

| 产物 | sha256 |
|---|---|
| `tmp/apk-libil2cpp.so`（主二进制） | `529bd257af0cd6e953fb50f51a69857f42f04eb70acab0586f8413d1f97afd1d` |
| `tmp/android-metadata.json` | `42ba4996eb724898bf8a39722bec542855472cbb516a2e58ca16780e2b7da7f4` |
| `gamedata_decrypted/*.json`（聚合） | `e4739fa5908555133bd079baa2cc9f2745a0ed45e1eaf86a28c709fb70af6486` |
| `steam_analysis/dump.cs` | `17f6a04ce9711378a6fde902218a63727ae41aa356ff6e5fe3860dd323bebb5f` |

`gamedata_decrypted` 聚合哈希按 `find gamedata_decrypted -name '*.json' | sort | xargs shasum -a 256 | shasum -a 256` 计算；新增/修改任一 JSON 都会改变它。

## 平台差异（已知）

* Android 与桌面 `GameAttributes` 布局一致（此前核对过 `Crit_Chance_MainHand_Total @0x1120`、`Crit_Chance_OffHand_Total @0x1128`）。
* 桌面端使用 Il2CppInspector stub，方法体不可用；Android `.so` 有真实指令，是方法级证据的唯一来源。
* 离线/商店等部分逻辑在桌面端文件（`desktop_Buffs.json` 等）中有独立事件表，引用时需注明来源平台。
* 网页运行时为重新实现，**不承诺与任一平台逐位一致**；一致性按 [清单](FULL_FIDELITY_BACKLOG.md) 的可观察行为/分布/录像对照判定。

## 结论归属规则

每个 ClientVerified/已还原结论必须注明：
1. 参考版本（Android 1.9.3 或 Steam build）；
2. 证据位置（`.so` 地址、gamedata Guid、dump 行、录像/样本路径）；
3. 是否有对照结果。

不得把「方法名/参数表/网页 smoke 通过」当作行为一致的证据；不得混用版本后标 ClientVerified。

## 待办（B01 后续）

* 补 Steam build id（appmanifest 或 Steam 客户端记录）。
* 若选定 Steam 为主参考，需重跑属性/伤害样本并重新绑定哈希。
