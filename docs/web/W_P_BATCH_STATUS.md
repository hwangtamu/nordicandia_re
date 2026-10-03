# 批次 3 状态：W01–W07 / P01–P04

日期：2026-10-03。对应 [完整复刻任务清单](FULL_FIDELITY_BACKLOG.md) 的第三批
（完整世界与专属单位）。基线 Android 1.9.3。

## 已接入（本轮）

### W01 怪物属性与缩放

* `export_monster_catalog_full.py` 导出每条怪物的战斗字段（`damageType`/`caster`/`ranged`/
  `size`/`tagIds`/`affixIds`）：136 条，60 有伤害类型，38 caster，6 ranged。
* `MonsterCatalog.cs`（嵌入）暴露目录 + `DamageTypeName`/`DamageBundle`。
* **伤害类型接入战斗**：`DamageType` id 经 dump.cs 确认为
  `Physical=0 Fire=1 Cold=2 Lightning=3 Poison=4 Pure=5`；archetype 改用**真实 gamedata 怪物名**
  （`SkeletonArcher1`、`GrayWolf` 等）并由目录取伤害类型——**CasterDemon1 实为 Cold（id 2）**，
  修正了网页原先手工设的 Fire。
* 属性曲线/稀有度系数已在 D04/D05 接入，手工原型倍率已删。
* `W01MonsterCatalogTests` 8 条。

### W02 完整 AI 条件与状态迁移

* `UpdateBrain` 显式处理 Brains.json 的非技能动作：`MeleeWeaponSwing`、
  `MinionReturnToMaster`、`PetLoot`、`UniqueItemHuginnPull`（先前落入 switch 默认，
  现显式列出）——宠物/图腾类动作在网页切片回退为追击/攻击。
* `W02W03BrainTests` 校验：brain 引用的技能全部有 `MonsterPowerCatalog` 条目或已知动作（0 缺失）。

### W03 全怪物技能与 Boss 阶段

* `MonsterPowerCatalog` 35 个技能条目（WolfKing/Dragon/Bolomehl/Hel/Kibu/Odr/诅咒）；
  召唤技能的 `Minion` 全部对应真实 gamedata 怪物（已校验）。
* 常量仍以客户端实现类为准；未恢复的怪物技能定义参数标 Provisional。

### W04 / W05 世界与进度（数据接入）

* `world_catalog_full.json`（36 世界：tier/theme/boss/`MonsterTypeSpawnWeights`）嵌入为
  `GameData/world_catalog.json`，新增 `WorldCatalog.cs`（`ForTier`/`ById`/`Entries`）。
* `W04WorldCatalogTests` 5 条：tier 1 = Grasshill（Beast 权重），boss = Boss_WolfKing，
  35 个 tiered 世界 + 1 城镇。

### W07 离线收益

* `Character.GetSecondHighestReachedWorldCheckpoint` 已定位：读 `World_Tier_Unlocked`（id 9，
  offset 0x70），同 tier 回退一个 checkpoint，仅在刚解锁 tier 时回退上一 tier。
* `OfflineRate` 改用属性 9（属性 8 回退）。

## 仍待完成

| 项 | 差距 |
|---|---|
| W01 | 运行时仍用 6 个 archetype；接入 136 条真实怪物（怪物池/密度/阶层）需重做战斗生成 |
| W02 | 目标丢失、寻路、碰撞、仇恨/逃离时机的**数值**；宠物/图腾动作 |
| W03 | 各 Power 的精确定量参数（触发/时序/召唤上限）与 Boss **阶段**、清场 |
| W04 | 地图图结构、场景块、碰撞、出入口、刷怪区体积（`DungeonMonsterSpawnArea` 的 `PathTypeIndex`/`IndexOnPath` 依赖地图路径系统） |
| W05 | 城镇/地图解锁/checkpoint/WorldTier 选择、返回路径、跨重登恢复 |
| W06 | `DungeonMonsterSpawnArea` 真实体积、世界修饰符、boss 包、完成/失败/返回 |
| W07 | 逐 tier checkpoint 进度跟踪（tier 回退） |

数据侧已齐备（`monster_catalog_full` 136、`world_catalog_full` 36、`brains_catalog` 40 条件、
`MonsterPowerCatalog` 35 技能）。把 W04/W05 的**地图系统**建起来后，W01 的怪物池、W06 的
真实刷怪区与 W07 的 checkpoint 才能逐条落地——这是下一步的主体工作。
