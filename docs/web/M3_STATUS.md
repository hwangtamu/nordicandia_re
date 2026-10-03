# M3 状态 — 持久化与特殊流程（2026-10-02）

M3 目标：事务存档、断线恢复、技能槽扩展、Aesir 祝福、Niflheim 传送门。
通过条件：重登/服务重启一致；到期正确；网络重试不会复制道具或多扣款。

## 交付物

| 区域 | 内容 | 可信度 |
|---|---|---|
| 事务存档 | `GameStore` 单写者 `lock(gate)` + `world.json.tmp` 原子 `File.Move(...,true)`；命令记录 `world.json` 内一致提交。单机内测级别（未上 SQLite） | ClientVerified 结构 / 单机内测 |
| 断线恢复 | 权威快照 + 递增 `CombatVersion` + 命令 ID 幂等 + `expectedVersion` 边界；`MaxCatchUpSeconds=5` 防止长时间离线被当成实时战斗快进 | ClientVerified 命令契约 / 阈值 Provisional |
| 离线收益 | `GET/POST /characters/{id}/offline[/claim]`；`OfflineRewards`：每 30s 一次击杀经验，8h 上限；服务端计算、幂等（第二次领取为 0） | Provisional 规则 |
| 技能槽（6 主动/3 被动） | 已有：`GET/POST /loadout` + UI（`SkillSlotRules` 6/3） | ClientVerified |
| Aesir 祝福 | `GET/POST /characters/{id}/blessings`；`MakeOffering`（时长 ClientVerified）+ `Blessings` 效果表（Provisional）；购买立即生效、到期剔除、重登由 `EnsureBlessingBuffs` 恢复 | 时长/buff id ClientVerified；cost/效果 Provisional |
| Niflheim 传送门 | `GET /portal`、`POST /portal/enter`、`POST /portal/return`；消耗 `NiflheimPortal`(159) 一次，`entry.Niflheim` 切换缩放怪物；boss 通关自动返回；重试不重复扣道具；状态持久化（`99020` 角色标记） | 物品/世界类型 ClientVerified；缩放/规则 Provisional |
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

## 已知限制 / Interim

* **事务存储**：仍是单进程 `world.json` + 原子替换，不是 SQLite；多进程并发写入不支持（见计划：单机内测可接受）。
* **断线恢复**：用快照 + 版本，而非计划里写的递增事件序号；网页端靠轮询 `/state` 对齐。
* **祝福效果**：`Aesir*Buff.Init` 的具体 `GameAttributeDA` 未解码，当前用主题化 Provisional 效果（Odin 魔寻、Tyr 物理伤害、Frigg 护甲、Thor 全抗）注入属性引擎；opal 价格 Provisional。
* **传送门**：`NiflheimPortalGameMode` 的 “packs”（`NumMonsterPacks` 词缀）未还原，当前用缩放的同类怪物 + 更大波次；`WorldTier`/`WorldWaypoint` 未涉及。
* **离线收益**：固定 30s/击杀、8h 上限，未接客户端 `CalculateIdleLevelsGained`。
