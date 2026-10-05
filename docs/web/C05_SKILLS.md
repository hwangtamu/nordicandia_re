# C05 — 首批 4 个代表技能贯通（2026-10-03）

每职业一个代表技能，覆盖直接攻击 / 弹体 / 持续范围 / 召唤。
每个技能目标：原版规格 → 服务端结算 → 前端表现 → 成长 → 重登的完整对照。本轮将事件批次及云/仆从/弹体/陷阱/引导状态接入 WebApi 与通用前端标记；这只补传输和基础可视化，不表示技能专属 VFX 或原版场景对照通过。

| # | 技能 | 职业 | 类型 | C04 状态 | C05 结论 |
|---|------|------|------|----------|----------|
| 1 | Shatter | Warrior | 直接攻击 | 无 gap | 全链路验证通过，无需改代码 |
| 2 | PowerShot | Hunter | 弹体 | 1 gap（Base_Action_Speed） | 走 C03 弹体路径验证通过；施法时间 gap 留待后续 |
| 3 | PoisonCloud | Mage | 持续/范围 | 2 gaps | **已实现**：6s 毒云实体 + 1s tick + 20% 减速 |
| 4 | SummonSkeleton | Necromancer | 召唤 | 行为缺失 | **已实现**：骷髅实体，继承属性，寻敌攻击 |

## 1. Shatter（Warrior，直接攻击）

- **原版规格**：9.0x 武器伤害，单体近战目标，35 法力，5s 冷却（client-verified）。
- **服务端结算**：`UseSkill` 默认 strike 分支 → `NearestAliveMonster(8)` → `HitTarget(9.0x)`。13 步伤害管线（C01）。
- **前端表现**：`damage` 事件（已有）。
- **成长**：精通 `MasteryShatterImprovedShatter`（id 2）每级 +0.05 武器伤害倍率 → `EffectivePowers` 改写 `Multiplier` → 伤害按比例变化（测试：9.0→9.05，伤害比 1.00552，误差 <1e-4）。
- **重登**：精通等级存于 `GameStore`，重登后 `GetOrCreate` 经 `EffectivePowers` 重新应用（测试：快照 `Confidence == "mastery-modified"`，重登前后一致）。
- **测试**：`shatter` 4 条（伤害/法力/冷却/9.0x 倍率）。

## 2. PowerShot（Hunter，弹体）

- **原版规格**：4.5x 武器伤害，95% 穿透几率（`Power_Power_Shot_Pierce_Chance_Percent`），25 法力，8s 冷却。
- **服务端结算**：C03 真实弹体（0.25y 步进、圆形碰撞、已命中集合），穿透走逐命中掷骰。
- **前端表现**：`projectile` 事件（spawn/hit，Detail 带坐标）；本轮增加弹体权威位置/方向/剩余时间快照及通用飞行标记。
- **剩余 gap**：`Base_Action_Speed`（施法时间）——全技能通用，`BeginCast` 目前只设冷却+扣蓝。留待后续系统级实现，不在 C05 范围内单独立项。
- **测试**：`powershot` 3 条（命中主目标、spawn 事件、hit 事件）。

## 3. PoisonCloud（Mage，持续/范围）——本轮主要实现

C04 时被折叠进 instant nova 原型，丢失两个真实属性。本轮实现为持久云实体：

- **实体**：`PoisonCloud { X, Z, Radius=3.0, Remaining=6.0, TickInterval=1.0, DamageMult=0.9, SlowAmount=0.2 }`
  （`server/Nordicandia.Simulation/CombatInstance.C05.cs`）。
- **施放**：`UseSkill` 的 nova 分支对 `PoisonCloud` 特判——在最近怪物处生成云（C06 再泛化为 cloud 原型）。
- **结算**：`TickClouds(dt)` 每 1s 对云内怪物造成 0.9x 武器伤害（Poison 元素，走 `SkillDamageTypes`），并施加 `slow` debuff（20%，2s，云内刷新）。
- **减速生效**：怪物 4 处移动改用 `EffectiveMonsterSpeed()`（原 `monster.Speed` × (1 − slow 层数)）。
- **前端表现**：`cloud` 事件（spawn / tick / expire，Detail 带坐标与伤害）；本轮增加带半径和剩余时间的权威快照及地面范围标记。
- **关闭的 gaps**：`Power_Duration`、`Power_Slow_Effect_Percent`（C04 反静默清单 −2，剩余 27）。
- **测试**：`poisoncloud` 7 条（生成云、半径/时长、tick 伤害、减速 debuff、云外不受影响、6s 消散、spawn/expire 事件）。

## 4. SummonSkeleton（Necromancer，召唤）——本轮主要实现

C04 时 summon 原型只给了一个 offense buff，没有实体。本轮实现真实仆从：

- **实体**：`PlayerMinion { X, Z, Hp, Damage, AttackInterval, AttackRange=2.0, Speed=6.0 }`
  （`CombatInstance.C05.cs`）。
- **施放**：`UseSkill` 的 summon 分支对 `SummonSkeleton` 特判——生成 3 只骷髅（数量为 Provisional，客户端未恢复出数量属性）。
- **继承**（Provisional 公式，用恢复的 `Minion_Inheritance_*` 属性）：
  - 伤害 = 武器总伤 × (1 + 0.15)
  - 生命 = 玩家最大生命 × (1 + 0.5)
  - 攻击间隔 = 2.0s / (1 + 0.8)
- **行为**：`TickMinions(dt)` 寻 30y 内最近怪物，近战范围外靠近、范围内攻击；目标死亡后转火。
- **前端表现**：`summon` 事件（生成）+ `minion` 事件（仆从攻击，Detail 带伤害）；本轮增加仆从权威位置/生命快照及基础实体标记。
- **已知简化**（文档化）：怪物 AI 暂不以仆从为目标（仆从不会被打死）；其他 7 个召唤技能仍走旧 buff 路径（C06 泛化）。
- **测试**：`summon` 4 条（3 只生成、寻敌+转火、summon/minion 事件、伤害继承）；新增 server→WebApi 事件单次消费和瞬时实体快照测试。

## 成长（Growth）

链路：精通点数（`GameStore`）→ `CombatRegistry.EffectivePowers` → 改写 `SkillProfile.Multiplier/Cooldown/Radius/ManaCost/Values` → `UpdatePowers` → 伤害。

- 测试 `growth`：同种子下 +0.05 倍率 → 伤害比精确为 9.05/9.0。
- 精通点数预算、分配走已有 `AllocateMastery`（C05 未改）。

## 重登（Relogin）

| 状态 | 重登后 | 说明 |
|------|--------|------|
| 精通等级 | ✅ 保留并生效 | 存于 `GameStore`，`GetOrCreate` 重算 `EffectivePowers`（测试 `relogin`） |
| 技能装配 | ✅ 保留 | `SetLoadout` 存于 store |
| 技能冷却 | ❌ 重置 | `CombatInstance` 重建，`skillCooldowns` 归零（客户端行为一致：冷却短） |
| 毒云 | ❌ 消失 | 战斗实例级实体，不跨重登 |
| 骷髅 | ❌ 消失 | 同上 |

## 本轮附带修复

C05 测试暴露的真 bug：`SetLoadout` → `UpdatePowers` 更换技能列表后，`skillCooldowns` 数组长度未同步，`Snapshot()` 越界崩溃。
已修：`UpdatePowers` 内按槽位保留已有冷却重建数组（`CombatInstance.cs`）。

## 测试

`server/Nordicandia.StoreTests/C05SkillTests.cs`，20 条新增断言全部通过。
全回归：**537 PASS**（517 + 20），fidelity **51/51**。

## 留给 C06

- cloud 原型泛化（`export_powers.py` 分类器加 cloud 规则，`UseSkill` 去掉按名特判）。
- 其余 7 个召唤技能的实体化；怪物 AI 以仆从为目标。
- `Base_Action_Speed` 施法时间系统（全技能通用）。
- C04 反静默清单剩余 27 个 gaps。
