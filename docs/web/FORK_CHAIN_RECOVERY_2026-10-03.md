# Fork / Chain 机制恢复

日期：2026-10-03。方法：`HandleForkAndChain`（0x029F8DBC–0x029F959C）反汇编，
`libil2cpp.so`（官方 1.9.3），VA→方法名经 `il2cpp/out/metadata.json`（170k 条）解析。
替代 B04 S-FORK-1 实机采集（无 Hunter 角色）。

## Fork（分叉）

触发：弹体命中时 `CalculateChance(ForkChance)` 掷骰。
几率属性：`Projectile_Auto_Attacks_Fork_Chance`（431）/ `Power_Projectile_Fork_Chance`（432）。

掷骰成功且 `canFork` 时：

1. 清除几率属性（`ClearValues`）：分叉出的子弹体不再二次分叉/连锁。
2. 生成 **2** 支新弹体（`CreateProjectile` × 2）：
   - `damageMultiplier = 0.5`（`fmov d4, #0.5`，写死常量）
   - `canFork = false`，`canChain = false`
   - 原命中目标经 `AddHitActor` 加入子弹体的已命中集合（不重复命中原目标）
3. 散布：`RangeExclusive(45.0, 90.0)`（.rodata `0x13872d8`/`0x1387830`）随机角度，
   `GenerateFixedSpacingSpreadPositions(..., count=2)` 固定间隔散布，`Launch` 发射。

即：**命中后 50% 概率（按几率）分裂出 2 支 50% 伤害的子弹，呈 45°–90° 散布，不重复命中原目标，子弹不再分叉/连锁。**

## Chain（连锁）

Fork 未触发时掷骰 `CalculateChance(ChainChance)`。
几率属性：`Projectile_Auto_Attacks_Chain_Chance`（663）/ `Power_Projectile_Chain_Chance`（687）。

成功时：

1. 清除几率属性（同上）。
2. `GetEnemiesInRadius(TargetList, 命中点, 10.0, -1)`：半径 **10.0** 内找敌。
3. `SortByDistanceFrom`：按距离排序（最近优先）。
4. 遍历，跳过原命中目标，对第一个其余目标 `Retarget`——**复用同一弹体转向，不生成新弹体，无伤害乘区**（`Retarget` 签名无伤害参数）。
5. 返回 false（弹体继续飞行；fork 路径返回 true 表示原弹体已消耗）。

即：**连锁将同一弹体转向 10 码内最近的另一个敌人，伤害不变，每次命中最多跳一次。**

## 网页对照

- 网页 `CombatInstance.SecondaryTargets` 目前用"附近敌人"代替分叉碰撞（D08），
  与上述机制不符：实际分叉是**发射新弹体**（0.5x 伤害、45°–90° 散布），
  连锁是**复用弹体转向**（10 码、最近优先、全伤害）。
- 本恢复为 ClientVerified（指令级），网页实现待 C03。

## 未完全确认

- 散布角是总张角还是单侧偏移（`GenerateFixedSpacingSpreadPositions` 未展开）。
- 连锁在第三跳及以后的几率来源（子弹体的几率属性已被清除，多跳连锁的驱动待查）。
- 返回值 true/false 的调用方语义（推断：弹体是否消耗）。

## 状态：已还原（机制）/ 待接入（网页实现，C03）
