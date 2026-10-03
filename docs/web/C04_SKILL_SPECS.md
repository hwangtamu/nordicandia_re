# C04 逐技能行为规格

日期：2026-10-03。对应 [完整复刻任务清单](FULL_FIDELITY_BACKLOG.md) 的 **C04**（逐技能/精通建立行为规格）。
基线 Android **1.9.3**（versionCode 507033）。分母见 [B02_CONTENT_CATALOG.md](B02_CONTENT_CATALOG.md)。

## 规格模板（每个技能一份）

```text
技能 / 定义 ID / 整数 ID：
来源版本与证据（实现类、方法地址、PowerDefinition Guid）：
施法条件（RequiresTarget / 目标类型 / 武器要求 / 资源 / 冷却）：
等级曲线（每级参数）：
效果时间线（施法 → 命中 → 后处理，含 tick）：
命中/伤害（伤害类型、武器倍率、范围/半径、目标数）：
Buff / 召唤 / 弹体：
精通改写（逐个 mastery 的增量）：
网页当前实现与差异：
对照样本（B04 ID）：
状态：
```

## 通用模式：目标攻击技能（Targeted Attack）

多数主动攻击技能（Shatter、PowerShot、Shadowbolt 等）是 `ActionTimedSkill` + `RequiresTarget`：
构造一次 `AttackPayload`（武器伤害 × `Base_Power_Weapon_Damage_Multiplier`），对目标结算，
再跑技能专属的 `onPostHit`（精通/附加效果）。共性：

* `GetActionSpeed()` 多数返回 0（瞬发）；`GetAttackRadius()` 读 `Attack_Range_MainHand_Total`（`0x660`）或技能半径属性。
* `InternalInitializePowerParameters(rank)` 用 `SetPowerParameter(value, rank, attribute)` 写入每级参数。
* 每个技能有 `_Perform_d__<n>` 协程状态机；`<Perform>b__<n>_0` 设置攻击代理，`b__<n>_1` 是命中后处理。

## 首个规格：Shatter（Warrior 主动）

| 项 | 值 | 证据 |
|---|---|---|
| 实现类 | `Game.Skills.Shatter` | `steam_analysis/dump.cs:115247` |
| 整数 ID | 19 | `powers_full.json` |
| `RequiresTarget` | true | `Shatter.get_RequiresTarget`（`0x02D484xx` 段） |
| `GetAttackRadius` | `Attack_Range_MainHand_Total`（属性 `0x660`） | `0x02D48748` |
| `GetActionSpeed` | 0（瞬发） | `0x02D48764`（`MOVI D0, #0`） |
| `Base_Power_Weapon_Damage_Multiplier` | 客户端默认 **8.0** | `InternalInitializePowerParameters`（`0x13C8`，`FMOV D0, 8.0`） |
| `Base_Cooldown` | 客户端默认 **4.0** | 同函数（`0x1340`，`FMOV D0, 4.0`） |
| 伤害类型 | 武器物理（走武器伤害管线） | `Perform` 构造 `AttackPayload` |
| 目标 | 单体 | `<Perform>b__13_0` |
| 精通（7） | ImprovedShatter(2)、SplittingPower(6)、Overpower(8)、PrecisionStrike(132)、AutoShatter(225)、CrushingBlow(391)、LightningStrike(393) | `fidelity_catalog.json` |

**网页当前实现**：`new(4, "Shatter", …, "strike", 9.0D, 5.0D, …, "client-verified")`（`PowerProfiles.generated.cs`）。

**差异**：网页倍率 **9.0**/冷却 **5.0** 与客户端默认 **8.0**/4.0 不一致；且网页走 `strike` 原型，
未实现 `AttackPayload`/`onPostHit`/精通改写。需按 `GetActionSpeed`、`Attack_Range` 和 7 个精通重建。
（这是 B03/C01 要处理的差异，也是 C05 的目标之一。）

## 待补规格（C05 其余三个代表）

| 职业 | 技能 | 覆盖机制 | 实现类 |
|---|---|---|---|
| Hunter | PowerShot | 弹体 | `Game.Skills.PowerShot` |
| Mage | ChainLightning / IceNova | 链式 / 范围持续 | `Game.Skills.*`（待定位） |
| Necromancer | SummonSkeleton | 召唤 | `Game.SummonSkeleton` |

每个按上面模板补齐后再改运行时；每改一个先加 B04 样本。

## 现状

* `Shatter`：已定位 + 已提取（参数/条件/精通），运行时未接入（仍原型），对照未通过。
* 其余 92 个玩家技能：已定位 + 已提取元数据，规格待写（D01）。
