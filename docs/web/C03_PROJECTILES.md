# C03：弹体与空间命中还原（2026-10-03；网页运行时更新）

## 证据

- `HandleForkAndChain`（0x029F8DBC–0x029F959C）反汇编，见
  `docs/web/FORK_CHAIN_RECOVERY_2026-10-03.md`：分叉=命中后掷骰，2 支 0.5x 伤害新弹体、
  `RangeExclusive(45.0, 90.0)` 散布、`AddHitActor` 已命中集合，子弹 `canFork=canChain=false`；
  连锁=fork 未中后掷骰，`GetEnemiesInRadius(命中点, 10.0)` 按距离排序取最近非原目标 `Retarget`
  （同一弹体、全伤害），成功时 `ClearValues`。
- `GetProjectileSpeed`（0x2c38a4c）：速度=基础属性 ×(1+加成) ×乘积，精确属性 id 未映射。
- `GetProjectileSpawnPosition`（0x2c38c20）：出生点=玩家位置+朝向×玩家半径。

证据等级：机制 **ClientVerified**；速度/碰撞体/射程数值 **Provisional**。

## 网页运行时

`server/Nordicandia.Simulation/CombatInstance.Projectiles.cs`：

- 弹体是持续实体，固定以 `CombatInstance.StepSeconds`（50ms）推进；累计不足一个步长的 `Advance`
  不会提前移动弹体。施法只发射，不在 `UseSkill` 内同步跑完；远处目标在飞行时间结束前不会受伤。
- 每步对当前怪物位置做线段扫掠圆形碰撞，支持移动目标，不因单步位移跨过目标；命中后仍按
  分叉→连锁→穿透的规则处理。
- 存在 `MapLayout` 时对弹道采样检查地面格，墙体/非地面阻挡弹体；同一步同时出现 actor 与墙时，
  仅结算更早发生的碰撞。无 `MapLayout` 的竞技场仍没有障碍物。
- `UseSkill` 对弹体技能返回时，`Damage` 是当下已结算伤害（因此通常为 0）；实际命中通过后续战斗
  更新与 `projectile`/`damage` 事件发生。`ActiveProjectiles` 可供模拟层和测试读取；WebApi 事件/实体
  快照接线尚未完成，不能视为网页渲染贯通。
- 分叉子弹继承本次飞行的命中处理，不分叉/连锁；穿透保持原方向，连锁改变同一弹体方向且最多一跳。

## 仍为近似/待办

- 速度、碰撞半径、射程仍分别使用暂定值 20 y/s、0.5y、14y；`GetProjectileSpeed` 的三个属性槽位未映射。出生点仍从玩家坐标起步，没有接入 `GetProjectileSpawnPosition` 的玩家半径偏移。
- `GenerateFixedSpacingSpreadPositions` 的散布角是总夹角还是单侧角尚未展开，目前为对称 ±(45°–90°)。
- 多弹体技能的散射模式（`NumProjectiles`）未接入；Tornado 的 7 秒游走生命周期仍只按普通弹体射程处理。
- 仍需原版场景对照移动目标、墙体阻挡、速度与碰撞半径；当前几何测试是网页运行时回归，不是原版实机验收。
