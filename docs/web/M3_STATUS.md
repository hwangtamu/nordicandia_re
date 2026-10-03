# M3 状态 — 持久化与特殊流程（2026-10-02）

M3 目标：事务存档、断线恢复、技能槽扩展、Aesir 祝福、Niflheim 传送门。
通过条件：重登/服务重启一致；到期正确；网络重试不会复制道具或多扣款。

## 交付物

| 区域 | 内容 | 可信度 |
|---|---|---|
| 事务存档 | `GameStore` 单写者 `lock(gate)` + `world.json.tmp` 原子 `File.Move(...,true)`；命令记录 `world.json` 内一致提交。单机内测级别（未上 SQLite） | ClientVerified 结构 / 单机内测 |
| 断线恢复 | 权威快照 + 递增 `CombatVersion` + 命令 ID 幂等 + `expectedVersion` 边界；`MaxCatchUpSeconds=5` 防止长时间离线被当成实时战斗快进 | ClientVerified 命令契约 / 阈值 Provisional |
| 离线收益 | `GET/POST /characters/{id}/offline[/claim]`；`OfflineRewards`：`(秒/60) * killsPerMinute * 0.15 * 击杀经验`，12h（720min）上限、20000 击杀上限；服务端计算、幂等（第二次领取为 0） | 公式/上限 ClientVerified；killsPerMinute Provisional |
| 技能槽（6 主动/3 被动） | 已有：`GET/POST /loadout` + UI（`SkillSlotRules` 6/3） | ClientVerified |
| Aesir 祝福 | `GET/POST /characters/{id}/blessings`；`MakeOffering`（时长 ClientVerified）+ `Blessings` 效果表（类型/目标/幅度全部 ClientVerified，见下） | ClientVerified |
| Niflheim 传送门 | `GET /portal`、`POST /portal/enter`、`POST /portal/return`；消耗 `NiflheimPortal`(159) 一次，按物品的 `NumMonsterPacks`(133) 设置波次与 boss 目标；重试不重复扣道具；状态持久化（`99020`） | 物品/世界类型/包数 ClientVerified；缩放 Provisional |
| 客户端 UI | HUD `Offerings (O)`、`Portal (N)`；离线横幅“Claim”；祝福购买窗口；传送门窗口 | — |

## M3 出口条件对照

| 通过条件 | 状态 | 证据 |
|---|---|---|
| 重登/服务重启一致 | ✅ | `M3Tests`：传送门激活标记跨 store 重启保留；`BlessingPersistenceTests` 已覆盖祝福时长重登不叠加 |
| 到期正确 | ✅ | `M3Tests` 祝福 10 分钟后失效；`BlessingPersistenceTests` 精确到 0.25s |
| 重试不复制道具/多扣款 | ✅ | `M3Tests`：传送门重试返回 `already_in_niflheim` 且道具仍只消耗一次；offline 二次领取为 0 |
| 技能槽扩展 | ✅ | 既有 `PowerPoolTests` + npc-smoke `3/6 skills · 1/3 masteries` |
| Niflheim 消耗/进入/返回一致 | ✅ | `M3Tests`：进入消耗、重试不消耗、返回离场、无道具拒绝、跨重启保留 |

## 验证

* 服务端 **388 PASS**（新增 M3 17 项）。
* `npm run build`、`npm run smoke`、`npm run npc-smoke` 全绿；npc-smoke 断言 `offerings: blessings=4 buttons=16`、`portal: No portal`。
* 局域网地址冒烟同样通过。

## 祝福效果（本轮从源码完全复原）

`WindowAesirOffering.CreateAesirBuffOffline`（Android `0x0255FAE8`）按 `offeringType` 选择目标属性，
统一写入常量 **0.4**（.so `0x1387368`）；`offeringSize` 只改时长（10m/30m/1h/4h）：

| 祝福 | 目标属性（`*_Final` id） | 幅度 |
|---|---|---:|
| Odin | Strength/Dexterity/Intelligence/Vitality_Bonus_Percent_Final（244/245/246/247） | +40% |
| Tyr | Movement_Speed_Bonus_Percent_Final（115） | +40% |
| Frigg | Item_Quantity_Bonus_Percent（367） | +40% |
| Thor | Weapon_Damage_Percent_Bonus_Final（89） | +40% |

`CharacterAttributeEngine` 之前缺这些 `*_Final` 名称→id 映射（`attribute_ids.json` 不含），已改为从
`attribute_formulas.json` 的恒等公式补齐，并把 `script == name` 当透传值处理；因此祝福会真正进入
属性合计（测试：Odin 使 Strength 15→21）。

## Niflheim 包数（本轮复原并接入）

`Affixes.json` 的 `NumMonsterPacks`（IntegerId 747）写属性 id **133**，按稀有度给 11–500 包
（稀有度 F 用默认 11–20；1→21-30、2→31-40、…）。网页商人把包数写进传送门物品，`EnterPortal` 用它
设置 Niflheim 波次与 boss 击杀目标。

## 已知限制 / Interim

* **事务存储**：仍是单进程 `world.json` + 原子替换，不是 SQLite；多进程并发写入不支持（见计划：单机内测可接受）。
* **断线恢复**：用快照 + 版本，而非计划里写的递增事件序号；网页端靠轮询 `/state` 对齐。
* **传送门 / Niflheim pack 布局**：已按 `NiflheimPortalGameMode` + `GameWorld.GetRandomPackSize` 复原：`TotalPacks = max(Num_Monster_Packs, 2)`，每包大小是 `Rand.RangeExclusive(2, 4)` 的随机取整（`Area_Pack_Size_Bonus_Percent_Final` 缩放，余数进位），包按清理顺序依次刷新，清完所有包完成一次 run。`CombatInstance` 以 `packMode` 实现，`CombatSnapshot.PacksRemaining/TotalPacks` + HUD “Packs X/Y”。仍为近似：原版每包成员按压 0.1s 逐个生成、包在随机刷怪区生成，网页整包一次生成并在随机环形位置；“更多 boss”分支（`Area_Contains_More_Bosses`）未接入；怪物数值缩放仍 Provisional；`WorldTier`/`WorldWaypoint` 未涉及。
* **离线收益**：基础经验公式与常量同前。2026-10-03 直接解码 ELF 后确认 `killsPerMinute = truncate(Offline_Battle_Efficiency_Multiplier · clamp(1.5 · secondHighestReachedTier, 10, 45))`；属性为 **565**（=`Base_Stamina_Multiplier`），阶层来源为 `Character.GetSecondHighestReachedWorldCheckpoint`。现已接入：`CombatRegistry.OfflineRate` 从属性引擎取 `Offline_Battle_Efficiency_Multiplier`，并用角色当前世界阶层（属性 8，默认 1）作为 tier。**仍为近似**：网页不追踪“第二高已到达检查点”历史，tier 输入以当前阶层代替；击杀经验仍取网页 `CombatModel.ExperienceReward(level)`。详见 [恢复报告](LUCK_AND_ON_HIT_RECOVERY_2026-10-03.md)。
* **祝福 opal 价格**：已从 `LocalCatalog.cctor` 提取 Small/Medium/Large/ExtraLarge = **200/500/800/2500** opals（与 `OfflineCatalog.GetOpalPrice` 一致）。客户端源码明确标注为 "TODO-PRICE: seed values, production export pending"，因此这是客户端实际使用的 seed 值，但预计会在生产导出后变动。
