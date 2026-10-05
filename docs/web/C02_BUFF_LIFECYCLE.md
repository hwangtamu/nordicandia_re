# C02：Buff 生命周期还原（2026-10-03）

## 证据

客户端基线 Android 1.9.3（B01）。`Buff` 基类（`Buff(IGameContext, BuffDefinition)` 0x2b2076c
附近方法表）反汇编：

| 方法 | VA | 恢复的规则 |
|---|---|---|
| `Buff.Stack(Buff)` | 0x2b22f88–0x2b23264（732 字节） | 同键重上：可叠加→层数 +1；持续时间**仅当新剩余时长更长时**替换（`fcmp` + 条件替换，受 `CanRefreshDurationOnStack` 虚方法 gating）；返回 `w21^1` |
| `Buff.IsStrongerThan(Buff)` | 0x2b21c10–0x2b21d04（244 字节） | 基类先比 Guid，再比**剩余时长**，最后比**层数**；`DebuffPoisoned.IsStrongerThan` 有 override（0x02B2F7A0），先比较 DPS/TimeoutDuration，非零差值优先返回，否则回退基类规则 |
| `BuffManager+DistinctComparer` | 0x2b2583c | 同（定义，来源）去重 |

`TickTimer.get_RemainingDuration`（0x2c70224）、`get_TimeoutDuration`（0x2c70188）在
基类 `IsStrongerThan` 直接读取 `RemainingDuration` 和 `Stacks`；毒 override 读取 `Tick_Damage_Per_Second` 并除以 `TimeoutDuration`。因此毒的专属比较是每单位总时长的 DPS 比较，而不是单纯“新鲜毒时长更长就替换”。

证据等级：**ClientVerified**（指令级）。

## 网页实现

`server/Nordicandia.Simulation/BuffManager.cs`：

- `BuffInstance`：`Key = DefinitionId|Source`；`Duration/Remaining/Stacks`；
  `Stackable/AllowMultipleInstances/CanRefreshDurationOnStack/IsPersistent`；
  `TickDps`、`TickDamageType`（类型化 DoT）、`Magnitude`（增益幅度）。普通 Buff 比较时长→层数；毒先比较 `TickDps / Duration`，相等或时长为零才回退时长→层数。
- `BuffManager.Add`：无实例→加入；`AllowMultipleInstances`→加后缀键加入；
  可叠加→层数+1、时长仅延长不缩短；不可叠加→新实例更强才替换。
- `Tick(dt, onTick)`：`elapsed = Min(dt, Remaining)` 传给回调，临界 tick 不多算不漏算；
  快照迭代（回调可能杀死 owner 并 `Clear()`，避免枚举中修改字典）。DoT 结算按 `TickDamageType` 选择物理护甲或对应元素抗性。
- `Dispel(predicate)`：驱散；`Clear(includePersistent)`：死亡/换图移除。

## 迁移

| 旧字段 | 新位置 |
|---|---|
| `CombatMonster.PoisonTimer/PoisonDps` | `monster.Buffs["poison\|player"]`（`TickDps`，1s） |
| `offenseBuffTimer/Bonus` | `PlayerBuffs["offense\|"]`（`Magnitude`） |
| `moveSpeedBuffTimer/Bonus` | `PlayerBuffs["movespeed\|"]`（`Magnitude`） |
| 5 种诅咒 timer/amount | `PlayerBuffs["curse-slow\|"]` 等；吸血诅咒另保留 `curseLeechSource` 怪物引用（buff 过期时清空） |
| `TickPoison/TickCurses` | `TickMonsterBuffs` + `UpdatePlayer` 中的 `PlayerBuffs.Tick(dt)` |
| 玩家死亡 | `PlayerBuffs.Clear()`（非持久 buff 移除，客户端规则）；怪物死亡/重生 `monster.Buffs.Clear()` |

## 行为变更（相对旧近似，均为向客户端规则对齐）

1. **毒**：旧"最高 DPS + 刷新"近似已移除；同键重上先按 `Tick_Damage_Per_Second / TimeoutDuration` 比较，非零差值决定强弱，平局再比较剩余时长/层数。跨来源实例的去重/交互仍未有足够证据，不能将网页 `DefinitionId|Source` 键宣称为原版完整语义。
2. **燃烧**：DoT 具备独立 `DamageOverTimeType`，InfernalBlast 使用 Fire 抗性而不再伪装为 poison；燃烧幅度仍待原版校准。
3. **换图**：`SetWorld` 清除非持久玩家 Buff、云、仆从、陷阱、弹体和持续施法状态；死亡清理仍由原有入口执行。
2. **增益/诅咒**：旧"各取 max（时长取 max、幅度取 max/最新）"→整实例按"时长→层数"比较，
   更强才替换；时长只延长不缩短，幅度不参与比较。
3. 快照 `OffenseBuffRemaining` 改从 `PlayerBuffs.RemainingOf("offense")` 取。

## 样本

新增 9 条 `buff_lifecycle` 样本（`samples/fidelity_samples.json`）：stronger-wins、
no-shorten、refresh-extends、stackable-count、dispel、death-clears、critical-tick、
critical-tick-expires、poison-reapply-replaces。`FidelityReplayTests` 新增同 kind 的脚本化
求值器（ops: add/advance/dispel/clear）。

## 回归

- 全回归：**507 PASS**，exit 0
- fidelity：**48/48 pass**

## 待办

- S-BUFF-1/2 实机样本（用户推迟）：判定跨来源毒实例去重/交互，以及吸血 `LifeLeechBuff/LeechInstance` 的精确数值链（伤害类型吸血属性 313–317、`Base_Life_Leech_Rate`）。
  的精确数值链（伤害类型吸血属性 313–317、`Base_Life_Leech_Rate`）。
- 祝福（`GameStore` 到期时间戳）仍是账号级实现，未迁入战斗 BuffManager。
