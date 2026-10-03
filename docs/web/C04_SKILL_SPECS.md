# C04 逐技能行为规格

日期：2026-10-03。对应 [完整复刻任务清单](FULL_FIDELITY_BACKLOG.md) 的 **C04**（逐技能/精通建立行为规格）。
基线 Android **1.9.3**（versionCode 507033）。分母见 [B02_CONTENT_CATALOG.md](B02_CONTENT_CATALOG.md)。

> 数值以 `tools/web-content/disasm_powers.py` 的输出为准（它跟踪真实的 `GameAttributeMap.set_Item`
> 调用）；**不要**手工按 `FMOV`/`LDR` 相邻关系配对，那样会得到错误数值（本项目曾因此误报"网页数值系统性偏高"）。
> `power_values.json` 中的数值与网页 `PowerProfiles.generated.cs` 一致，均为 ClientVerified。

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

## 规格：Shatter（Warrior 主动）

| 项 | 值 | 证据 |
|---|---|---|
| 实现类 | `Game.Skills.Shatter` | `dump.cs:115247` |
| 整数 ID | 19 | `powers_full.json` |
| `RequiresTarget` | true | `get_RequiresTarget` |
| `GetAttackRadius` | `Attack_Range_MainHand_Total`（`0x660`） | `0x02D48748` |
| `GetActionSpeed` | 0（瞬发） | `GetActionSpeed`（`MOVI D0, #0`） |
| 数值 | `Base_Power_Weapon_Damage_Multiplier=9.0`、`Base_Cooldown=5.0`、`Base_Mana_Cost=35.0` | `disasm_powers.py Shatter` @ `0x2d48348` |
| 伤害类型 | 武器物理（走武器伤害管线） | `Perform` 构造 `AttackPayload` |
| 目标 | 单体 | `<Perform>b__13_0` |
| 精通（7） | ImprovedShatter(2)、SplittingPower(6)、Overpower(8)、PrecisionStrike(132)、AutoShatter(225)、CrushingBlow(391)、LightningStrike(393) | `fidelity_catalog.json` |

**网页当前**：`new(4, "Shatter", …, "strike", 9.0D, 5.0D, 0.0D, 35.0D, …, "client-verified")`。
数值已一致；**差异在执行**：走 `strike` 原型，未实现 `AttackPayload`/`onPostHit`/精通改写。
`GetActionSpeed`、`Attack_Range` 和 7 个精通未接入。→ C05。

## 规格：PowerShot（Hunter 主动，弹体）

| 项 | 值 | 证据 |
|---|---|---|
| 实现类 | `Game.Skills.PowerShot` | `IsilDump/.../Game/Skills/PowerShot.txt` |
| `RequiresTarget` / `IsAbility` | true / true | `get_RequiresTarget`/`get_IsAbility` |
| 数值 | mult **4.5**、cd **8.0**、mana **25.0**、`Base_Action_Speed=1.0`、`Power_Power_Shot_Pierce_Chance_Percent=0.95` | `disasm_powers.py PowerShot` @ `0x2d21498` |
| 弹体 | `CreateProjectile(projectileSpeed, canChain)`、`HandleForkAndChain`、`LaunchProjectile` | 方法表 |
| 网页当前 | `projectile`, mult 4.5, cd 8.0, mana 25.0 | 数值一致 |
| 差异 | `projectile` 原型未接弹速/穿透（0.95）/分叉；`IsAbility`/ActionSpeed 未接 | C03、C05 |

## 规格：ChainLightning（Mage 主动，链式）

| 项 | 值 | 证据 |
|---|---|---|
| 实现类 | `Game.Skills.ChainLightning` | `IsilDump/.../Game/Skills/ChainLightning.txt` |
| `RequiresTarget` | true | `get_RequiresTarget` |
| 数值 | mult **2.5**、`ChainLightning_Max_Num_Chains=4`、cd **20.0**、mana **25.0** | `disasm_powers.py ChainLightning` @ `0x2d27cd0` |
| 伤害类型 | Lightning（`skill_damage_types.json`） | |
| 网页当前 | `chain`, mult 2.5, cd 20.0, radius 5.5, mana 25.0 | 数值一致 |
| 差异 | 链式跳数/每跳衰减未按客户端重建 | C03、C05 |

## 规格：IceNova（Mage 主动，范围 + 冻结）

| 项 | 值 | 证据 |
|---|---|---|
| 实现类 | `Game.Skills.IceNova` | `IsilDump/.../Game/Skills/IceNova.txt` |
| 数值 | radius **6.0**、mult **0.5**、`Power_Freeze_Duration=2.0`、cd **30.0**、mana **20.0** | `disasm_powers.py IceNova` @ `0x2d2cd04` |
| 网页当前 | `nova`, mult 0.5, cd 30.0, radius 6.0, mana 20.0 | 数值一致 |
| 差异 | 冻结时长（2s）与 AoE 命中判定未接入 | C02、C05 |

## 规格：SummonSkeleton（Necromancer 主动，召唤）

| 项 | 值 | 证据 |
|---|---|---|
| 实现类 | `Game.SummonSkeleton` + `SummonSkeletonMinion` | `IsilDump/.../Game/SummonSkeleton.txt` |
| `RequiresTarget` | true | `get_RequiresTarget` |
| 仆从继承 | WeaponDamage **0.15**、AttackSpeed **0.8**、MoveSpeed **1.1**、Life **0.5**、ForceField **0.3**、ArmorEvasion **0.75**、Resistances **0.75**、CritChance **1.0**、CritDamage **1.0** | `disasm_powers.py SummonSkeleton` @ `0x2a536f8` |
| 数值 | cd **10.0**、mana **20.0**；`Power_Base_Max_Num_Minions`（`0x1A70`）、`Power_Minion_Duration`（`0x2650`） | 同上 |
| 网页当前 | `summon`, cd 10.0, mana 20.0，数值一致 | |
| 差异 | 仆从属性用统一倍率，未使用 `Minion_Inheritance_*` 快照 | P01、C05 |

## 结论

* 5 个代表技能的**数值**与客户端一致（ClientVerified，已进 B04 样本）。
* C05 进展（本轮）：
  * **ChainLightning** 衰减改为每跳 **−25%**（`_DamageReductionPerJump=0.25`，原 0.85 错误）；测试命中 **4** 条链。
  * **IceNova** 冻结接入（`Power_Freeze_Duration=2`），测试敌人 `StunTimer>0`。
  * **PowerShot** 穿透改为按 **0.95** 逐目标 roll（原为无条件 2 个 50%）。
  * **Shatter** 的 `strike` 行为与客户端一致（单体武器攻击）。
* 仍缺：**SummonSkeleton 的盟友仆从**需要 **P01**（玩家召唤物公共模型），不能只加进敌对 `monsters`；`AttackPayload`/`onPostHit`/7 个 Shatter 精通改写未接。
* 通用模式（RequiresTarget + AttackPayload + onPostHit）可作为 C06 批量移植的模板。

## B04 样本

`fidelity_samples.json` 已加入 6 条 `skill_param`（Shatter 倍率/冷却、PowerShot 穿透、ChainLightning 链数、IceNova 冻结、SummonSkeleton 继承生命），回放 **19/19** 通过。
