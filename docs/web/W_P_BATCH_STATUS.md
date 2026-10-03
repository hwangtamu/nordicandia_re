# 批次 3 状态：W01–W07 / P01–P04

日期：2026-10-03。对应 [完整复刻任务清单](FULL_FIDELITY_BACKLOG.md) 的第三批
（完整世界与专属单位）。基线 Android 1.9.3。

## 本轮新增

### W01 怪物属性与缩放（目录部分）

* `export_monster_catalog_full.py` 现在导出每条怪物的战斗字段：`damageType`、`caster`、
  `ranged`、`size`、`tagIds`、`affixIds`。统计：136 条，60 条有 `DamageType`，
  38 个 caster，6 个 ranged。
* `MonsterCatalog.cs` 嵌入并暴露该目录（`ByName`/`Entries`），供运行时按真实定义生成怪物。
* `W01MonsterCatalogTests`（5 条）。
* 属性曲线与稀有度系数已在 D04/D05 接入（前轮），手工 `HpMult/OffenseMult/DefenseMult`
  已删除，所以 W01 的"删除五类原型通用数值回退"已完成。

### W07 离线收益

* 定位 `Character.GetSecondHighestReachedWorldCheckpoint`（0x…，ISIL）：读
  `World_Tier_Unlocked`（属性 id 9，offset 0x70），再在同 tier 内回退一个
  checkpoint（`Checkpoints.TryGetMaxCheckpoint - 1`），仅当刚解锁该 tier 时才回退到上一 tier。
* `CombatRegistry.OfflineRate` 改为优先读属性 9（`World_Tier_Unlocked`），属性 8 作回退。
  网页尚未记录逐 tier checkpoint 进度，所以 checkpoint 回退未应用（已在代码注释与台账注明）。

## 仍待完成

| 项 | 差距 |
|---|---|
| W01 | 运行时仍用 6 个通用 archetype；接入 136 条真实怪物（等级/阶层/怪物池）是更大的改动 |
| W02 | 目标丢失、寻路、碰撞；仇恨/逃离时机 |
| W03 | 各怪物 Power 的触发/时序/召唤上限/仆从属性、Boss 阶段与清场 |
| W04 | 地图图结构、场景块、碰撞、出入口、刷怪区体积、怪物池/密度 |
| W05 | 城镇/地图解锁/checkpoint/WorldTier、返回路径；跨重登恢复 |
| W06 | 真实 `DungeonMonsterSpawnArea`、世界修饰符、boss 包、完成/失败/返回 |
| W07 | 逐 tier checkpoint 进度跟踪（`GetSecondHighestReachedWorldCheckpoint` 的 tier 回退） |
| P01 | 玩家召唤物公共模型（主人/归属、继承、AI、数量、寿命、掉落归属） |
| P02 | Odr 等专用召唤行为 |
| P03 | 战斗宠物完整生命周期 |
| P04 | 非战斗宠物 |

数据侧已具备：`monster_catalog_full.json`（136）、`world_catalog_full.json`（36 世界/阶层）、
`brains_catalog.json`（40 条件）、`MonsterPowerCatalog`（Boss 技能）。运行时接这些数据
需要重做战斗生成与地图系统，属独立大改。
