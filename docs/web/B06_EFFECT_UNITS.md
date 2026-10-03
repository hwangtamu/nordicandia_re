# B06 统一效果与状态接口

任务 ID：B06。日期：2026-10-03。依赖：B03。

## 做了什么

新增 `server/Nordicandia.Simulation/CombatInstance.Effects.cs`
（`CombatInstance` 的 partial 类），把原来散在 `UseSkill` 各原型分支里的内联逻辑
收拢为可复用执行单元：

**伤害单元**
- `HitTarget(target, skill, multiplier)`：核心伤害单元（ResolveSkill + ApplyOnHit + DamageMonster + 伤害事件）
- `HitNova(skill, radius)`：范围伤害，返回被命中的怪列表（stun 等按命中前列表施加，与原版一致）
- `HitChain(skill, radius, maxTargets)`：连锁（最近优先 + 每跳衰减）
- `HitProjectile(skill, range)`：弹体（首目标 + 穿透掷骰）

**治疗/Buff 单元**
- `HealPercent`、`GainShield`、`ApplyOffenseBuff`、`ApplyMoveSpeedBuff`

**召唤单元**
- `SummonMinions`：仆从继承加成占位（D11），挂载点已归位

**死亡触发单元**
- `OnMonsterDeath`：从 `DamageMonster` 完整提取（击杀计数/经验/升级、Boss 奖励、
  掉落、pack 进度、地牢循环、重生计时）；死亡触发类技能效果以后挂这里

**战斗事件**
- `CombatEvent(Type, TargetIndex, Amount, Detail)` + `DrainEvents()`：
  damage/heal/buff/summon/death 事件供渲染消费，上限 200 条防堆积

`UseSkill` 的 5 个原型分支（nova/chain/projectile/strike/summon-mobility-shield-aura-rally）
已全部改走上述单元。

## 回归

`dotnet run --project Nordicandia.StoreTests`：exit 0，495 PASS，0 FAIL，
fidelity 36/36。迁移中发现并修复一处语义差异：nova 的 stun 必须作用于
命中前列表（含被打死的怪），否则 `skill: IceNova freezes enemies` 回归失败。

## 状态：已完成（行为一致，单元就绪，待 C04–C06 逐技能迁移）
