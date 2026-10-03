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

## 规格：PowerShot（Hunter 主动，弹体）

| 项 | 值 | 证据 |
|---|---|---|
| 实现类 | `Game.Skills.PowerShot` | `IsilDump/.../Game/Skills/PowerShot.txt` |
| `RequiresTarget` / `IsAbility` | true / true | `get_RequiresTarget`/`get_IsAbility` |
| `GetActionSpeed` | `Base_Action_Speed`（`0xCC8`）= 1.0 | `InternalInitializePowerParameters` |
| `Base_Power_Weapon_Damage_Multiplier` | 客户端默认 **4.0**（`0x13C8`） | 同上 |
| `Base_Cooldown` / `Base_Mana_Cost` | **8.0** / **16.0**（`0x1340`/`0x1398`） | 同上 |
| 穿透 | `Power_Projectile_Pierce_Chance`（`0x1488`）、`Power_Power_Shot_Pierce_Chance_Percent`（`0x2570`） | 同上 |
| 弹体 | `CreateProjectile(projectileSpeed, canChain)`、`HandleForkAndChain`、`LaunchProjectile` | 方法表 |
| 网页当前 | `projectile`, mult **4.5**, cd 8.0, mana **25.0** | `PowerProfiles.generated.cs` |
| 差异 | 倍率 4.5 vs **4.0**、蓝耗 25 vs **16**；且 `projectile` 原型未接弹速/穿透/分叉 | |

## 规格：ChainLightning（Mage 主动，链式）

| 项 | 值 | 证据 |
|---|---|---|
| 实现类 | `Game.Skills.ChainLightning` | `IsilDump/.../Game/Skills/ChainLightning.txt` |
| `RequiresTarget` | true | `get_RequiresTarget` |
| `Base_Power_Weapon_Damage_Multiplier` | **2.0**（`0x13C8`） | `InternalInitializePowerParameters` |
| `ChainLightning_Max_Num_Chains` | **4**（`0x2528`） | 同上 |
| `Base_Cooldown` / `Base_Mana_Cost` | **16.0** / **16.0** | 同上 |
| 伤害类型 | Lightning（`skill_damage_types.json`） | |
| 网页当前 | `chain`, mult **2.5**, cd **20.0**, radius 5.5, mana **25.0** | `PowerProfiles.generated.cs` |
| 差异 | 倍率/冷却/蓝耗/链数均需按客户端重建 | |

## 规格：IceNova（Mage 主动，范围 + 冻结）

| 项 | 值 | 证据 |
|---|---|---|
| 实现类 | `Game.Skills.IceNova` | `IsilDump/.../Game/Skills/IceNova.txt` |
| `Base_Power_Radius` | **4.0**（`0x1418`） | `InternalInitializePowerParameters` |
| `Base_Power_Weapon_Damage_Multiplier` | **0.5**（`0x13C8`） | 同上 |
| `Power_Freeze_Duration` | **2.0**（`0x1390`） | 同上 |
| `Base_Cooldown` / `Base_Mana_Cost` | **16.0** / **16.0** | 同上 |
| 网页当前 | `nova`, mult 0.5, cd **30.0**, radius **6.0**, mana **20.0** | `PowerProfiles.generated.cs` |
| 差异 | 冷却 30 vs **16**、半径 6 vs **4**、蓝耗 20 vs **16**；冻结时长未接入 | |

## 规格：SummonSkeleton（Necromancer 主动，召唤）

| 项 | 值 | 证据 |
|---|---|---|
| 实现类 | `Game.SummonSkeleton` + `SummonSkeletonMinion` | `IsilDump/.../Game/SummonSkeleton.txt` |
| `RequiresTarget` | true | `get_RequiresTarget` |
| 仆从继承 | Life/ArmorEvasion/Resistances **+0.5**；CritChance/CritDamage **+1.0**（`0x1698`/`0x16A8`/`0x16B0`/`0x16B8`/`0x16C0`） | `InternalInitializePowerParameters` |
| 数量/时长 | `Power_Base_Max_Num_Minions`（`0x1A70`）、`Power_Minion_Duration`（`0x2650`） | 同上 |
| `Base_Cooldown` / `Base_Mana_Cost` | **8.0** / **16.0** | 同上 |
| 网页当前 | `summon`, cd **10.0**, mana **20.0** | `PowerProfiles.generated.cs` |
| 差异 | 冷却/蓝耗偏高；仆从属性用统一倍率而非 `Minion_Inheritance_*` | |

## 系统性发现：网页数值普遍高于客户端 rank-3 默认

| 技能 | 客户端倍率 | 网页倍率 | 客户端冷却 | 网页冷却 | 客户端蓝耗 | 网页蓝耗 |
|---|---:|---:|---:|---:|---:|---:|
| Shatter | 8.0 | 9.0 | 4.0 | 5.0 | ? | 35 |
| PowerShot | 4.0 | 4.5 | 8.0 | 8.0 | 16 | 25 |
| ChainLightning | 2.0 | 2.5 | 16.0 | 20.0 | 16 | 25 |
| IceNova | 0.5 | 0.5 | 16.0 | 30.0 | 16 | 20 |
| SummonSkeleton | — | 0 | 8.0 | 10.0 | 16 | 20 |

网页数值约比客户端 **`SetPowerParameter(..., rank=3, ...)` 默认高 10–56%**，蓝耗差异最大。
可能原因：网页用了不同 rank 的值，或 `export_powers.py` 的默认回退参与了计算。**这是 C01/C06 的
系统性差异，必须先定位再逐技能改**，否则 93 个技能都会带着相同偏差。
