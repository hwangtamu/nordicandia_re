# M2 状态 — 装备与成长循环（2026-10-02）

M2 目标：掉落、背包、穿戴、升级；补齐技能/敌人/Boss 与一个完整地下城循环。
通过条件：能完成一个地下城循环；装备改变实际战斗数值；奖励和消耗不重复。

## 交付物

| 区域 | 内容 |
|---|---|
| 掉落 | `CombatInstance` 击杀时按概率产出 `LootDrop`（槽位/稀有度/等级）；`LootTable` 解析为 `SerializedItem` 并写入背包 |
| 装备属性 | `LootTable` 提供 per-slot 属性权重；装备加成在投影/战斗时叠加 |
| 权威装备 | `CombatInstance.UpdateStats` + `CombatRegistry`：穿戴/卸下后立即重算 Offense/Defense/Recovery 并同步到模拟实例 |
| Boss / 地下城 | 击杀 8 只杂兵召唤 Boss「Frostbound Jarl」；击杀 Boss 清除地下城、给银币与保底 2 件装备、进入下一循环 |
| 网页 API | `GET /characters/{id}/inventory`；`commands` 新增 `equip` / `unequip`（携带 itemId，仍幂等） |
| 客户端 | 背包面板（装备/卸下、有效属性）、掉落滚动条、Boss 进度与「已通关」HUD |
| 测试 | `LootEquipmentTests.cs`（11 项）+ 浏览器冒烟新增「开背包并穿戴」 |

## 规则可信度（见 M0_RULES 的分级）

* **掉落概率、稀有度权重、装备属性权重、物品命名全部为 Provisional**。首版限定小范围，便于校准；
  真正的客户端词缀结算（`LocalImplicitBasePhysicalDamage` 等，属性 id 2500/2501/462）尚未复原。
* 装备属性使用高位临时属性 id（`99001` Offense、`99002` Defense、`99003` Recovery、`99004` 可穿槽位），
  存放在 `SerializedItem.Attributes[Item]`，不占用客户端真实属性 id。
* 装备加成 = `Σ 已装备物品属性`；战斗数值 = 基础（角色创建的起始值 + 升级成长）+ 装备加成。
  注意：原生客户端上报的 `CombatStats` 通常已含装备，网页角色为独立存档，暂不混算（见限制）。

## 幂等与不重复

* 掉落由服务端在击杀时生成并 `GrantItems` 落盘，客户端无法伪造或重复领取。
* `equip`/`unequip` 使用唯一 commandId：重复请求返回原快照，不会二次改属性（测试：
  `duplicate does not double-apply stats`）。
* Boss 奖励与地下城计数在 `CombatInstance` 内一次性结算；命令重放不会重复给奖。

## M2 退出条件对照

| 通过条件 | 状态 | 证据 |
|---|---|---|
| 能完成一个地下城循环 | ✅ | 测试 `m2 dungeon: boss cleared at least once`；冒烟 HUD 显示 Boss 进度与通关数 |
| 装备改变实际战斗数值 | ✅ | 测试 `offense gains the item bonus`；冒烟 `OFF 47`（基础 35 + 装备）；截图 `tmp/web-m2/smoke.png` |
| 奖励和消耗不重复 | ✅ | 命令幂等 + 服务端掉落；`duplicate does not double-apply stats` |

## 手工验证（API）

```bash
# 连续轮询触发战斗与掉落
for i in 1 2 3; do sleep 5; curl -s -b cookies .../characters/$CID/state; done   # loot: [Buckler (E) ...]
curl -s -b cookies .../characters/$CID/inventory                                 # 物品 + 有效 OFF/DEF/REC
# 穿戴 → OFF 35 -> 56，重复请求返回 duplicate
```

## 与计划的差异修复（本次）

| 原差异 | 处理 |
|---|---|
| 同一槽位可叠加装备 | ✅ 强制一槽一件：穿 B 时自动将同槽 A 放回背包（测试 `m2 slots: only one slot bonus is counted`） |
| 稀有度权重为编造值 | ✅ 改为来自 `gamedata_decrypted/Droprates.json` 的真实权重（F..SS），只有掉落“概率”仍为 Provisional |
| 技能仅 1 个主动 | ✅ 3 个主动（0 单体 Strike、1 范围 Nova、2 集结 Rally）+ 1 个被动（+10% Offense / +10% MaxHealth）；冷却独立追踪 |
| 敌人仅 1 类 | ✅ 5 类普通敌人（Bat/DemonOrc/Skeleton/BloodHound/StoneGolem，各自数值倍率），按索引循环刷新 |
| 无词缀 | ✅ 有限词缀集（of Might/Warding/Vigor/Fury/the Bulwark/Focus）；按稀有度 1–3 条，写入 `SerializedItem.Affixes` 并展示 |

## 已知限制 / 与计划的差异（修正后）

* 掉落**概率**与装备/词缀属性权重仍为 **Provisional**；客户端词缀结算（属性 2500/2501/462）未复原。
* 背包无格子/堆叠/拖拽；无分解/合成/商店融合（属后续里程碑，非 M2 要求）。
* 玩家位置不持久化；物品、装备槽位、经验、货币、击杀持久化。

## 已验证

* 服务端测试 **166 PASS**（其中 M2 相关 21 项）。
* `tsc` 通过；`npm run smoke` PASS：`7 kills`、`HP 154/154`（被动 +10%）、`OFF 36 · DEF 41 · REC 12`、3 个技能按钮（含冷却）、词缀展示；截图 `tmp/web-m2/smoke.png`。

## 下一步（M3 持久化与特殊流程）

1. 事务存档：单机 SQLite/事务提交，避免两个进程覆写 `world.json`。
2. 断线恢复：快照 + 递增事件序号；后台标签页/离线收益按服务器时间。
3. 技能槽扩展、Aesir 祝福、Niflheim 传送门的网页流程与到期/幂等验证。
