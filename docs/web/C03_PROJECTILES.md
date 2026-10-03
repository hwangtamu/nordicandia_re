# C03：弹体与空间命中还原（2026-10-03）

## 证据

- `HandleForkAndChain`（0x029F8DBC–0x029F959C）反汇编，见
  `docs/web/FORK_CHAIN_RECOVERY_2026-10-03.md`：分叉=命中后掷骰，2 支 0.5x 伤害新弹体、
  `RangeExclusive(45.0, 90.0)` 散布（.rodata 0x13872d8/0x1387830）、`AddHitActor` 已命中集合、
  子弹 `canFork=canChain=false`；连锁=fork 未中后掷骰，`GetEnemiesInRadius(命中点, 10.0)`
  按距离排序取最近非原目标 `Retarget`（同一弹体、全伤害），成功时 `ClearValues`。
- `GetProjectileSpeed`（0x2c38a4c）：速度=基础属性 ×(1+加成) ×乘积，精确属性 id 未映射。
- `GetProjectileSpawnPosition`（0x2c38c20）：出生点=玩家位置+朝向×玩家半径。

证据等级：机制 **ClientVerified**；速度/碰撞体/射程数值 **Provisional**。

## 网页实现

`server/Nordicandia.Simulation/CombatInstance.Projectiles.cs`（new）：

- `Projectile`：位置、归一化方向、速度（暂定 20 y/s）、剩余飞行时间、碰撞半径（暂定 0.5y）、
  伤害乘区、CanFork/CanChain、`HitActors` 已命中集合。
- `FireProjectiles`：从玩家位置向瞄准点发射，同步模拟到完成（队列处理分叉子弹），
  保持 `UseSkill` 同步契约（伤害在施法时已知）。发射/命中/分叉/连锁/穿透发 `projectile`
  战斗事件（带坐标）供渲染。
- `FlyProjectile`：0.25y 步进（小于碰撞直径，不隧穿）；每步圆形碰撞检测（存活、非命中集合、
  最近优先）；命中后按顺序：分叉掷骰→连锁掷骰→穿透掷骰→弹体消亡。
- 分叉子弹：命中点生成、0.5x 伤害、不再分叉/连锁、原目标预加入命中集合、±(45°–90°) 随机散布
  （`GenerateFixedSpacingSpreadPositions` 的单/双侧约定未展开，取对称 ±θ，已注明）。
- 连锁：10 码内最近的其他敌人，全伤害；成功后 `CanChain=false`（对应 `ClearValues`，最多一跳）。

## 行为变更

- 删除 `SecondaryTargets`（"附近敌人直接替代分叉碰撞"占位，D08 关闭）。
- 自动攻击（`ProjectileAutoAttack`）与 `projectile` 技能统一走真实弹体；穿透改为沿弹道继续飞行、
  按真实碰撞命中后续目标（旧逻辑是跳到"下一个附近敌人"）。
- 技能分叉/连锁：从 `Values` 读 `Power_Projectile_Fork_Chance` / `Power_Projectile_Chain_Chance`
 （默认 0，客户端属性 432/687 真实存在）。
- 视线：竞技场无障碍物，恒为真（已注明）。

## 样本

新增 3 条 `projectile` 场景样本（`FidelityReplayTests` 新增求值器，走真实 `CombatInstance`）：
`proj.chain-range`（10 码外不连锁）、`proj.chain-in-range`（10 码内全伤害连锁）、
`proj.fork-no-doublehit`（同种子下分叉与不分叉的首命中伤害完全一致，命中集合生效）。

单元测试同步改为真实几何：环形阵（分叉子弹必截获、各自独立结算护甲、无双重命中、
分叉压制连锁）、直线阵（连锁一跳）。

## 回归

- 全回归：**513 PASS**，exit 0
- fidelity：**51/51 pass**

## 待办

- 弹体速度/碰撞半径/射程的精确属性 id 映射（`GetProjectileSpeed` 的三个属性槽位）。
- 散布角单侧/双侧约定（`GenerateFixedSpacingSpreadPositions` 展开）。
- 多弹体技能的散射模式（`NumProjectiles` 网页暂无数据）。
