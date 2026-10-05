# C04：逐技能行为规格（2026-10-03）

共 93 个技能（主动 51 / 被动 42）。
每个技能的施法条件/目标/资源/冷却/等级曲线/效果时间线/Buff/弹体/召唤/精通改写见 `tools/web-content/generated/skill_specs.json`。

## 反静默映射清单

有 **44** 个技能带有当前原型**未处理**的真实属性或专属参数（behaviorGaps）——这些曾被静默折叠进原型，现逐条显式列出：

| 技能 | 职业 | 原型 | 未处理属性/参数 |
|---|---|---|---|
| BloodCasting | Mage | `haste` | _PercentOfManaCostTakenAsLife [field_0x120] |
| Meditation | Warrior | `haste` | _CooldownReduction [field_0x120] |
| RapidCasting | Mage | `haste` | _MoreSkillManaCostPercent [field_0x128] |
| AccumulatingShadows | Necromancer | `might` | _ShadowBoltDamageIncrease [field_0x120] |
| AncientKnowledge | Mage | `might` | _ExperienceBonus [field_0x120] |
| AxeSpecialization | Warrior | `might` | _DeadlyChance [field_0x120] |
| BeastTracking | Hunter | `might` | _MoreChampionMonsters [field_0x128] |
| Bodyguard | Necromancer | `might` | _ReducedCritDamageTaken [field_0x120] |
| BossHunter | Warrior | `might` | _SpawnIncrease [field_0x120] |
| CheatDeath | Warrior | `might` | _VitalityBonusPercent [field_0x128] |
| Concentrate | Hunter | `might` | Hit_Chance_Bonus_Percent |
| Cripple | Hunter | `might` | _MinimumAttackRangePercent [field_0x120] |
| DarkMagic | Necromancer | `might` | _ManaGainedOnKill [field_0x120] |
| DoubleDraw | Hunter | `might` | _ExtraProjectileChance [field_0x120] |
| EvasiveManeuver | Mage | `might` | _IncreasedDodgeChance [field_0x120] |
| ForceField | Mage | `might` | _MoreForceField [field_0x120] |
| GiftOfTheVampire | Necromancer | `might` | _MinionLeechGainPercent [field_0x120] |
| Hubris | Mage | `might` | _IntelligenceBonus [field_0x120] |
| MaceSpecialization | Warrior | `might` | _ThrowMaceChance [field_0x120] |
| ManaFlow | Mage | `might` | _IncreasedMaximumManaPercent [field_0x120] |
| Overkill | Warrior | `might` | _OverkillChance [field_0x120] |
| PoolOfMana | Hunter | `might` | _ManaBonus [field_0x120] |
| Precision | Hunter | `might` | _AttackRatingBonus [field_0x120] |
| Reanimator | Necromancer | `might` | _ChanceToReanimateFallenMinion [field_0x120] |
| Sharpshooter | Hunter | `might` | _RangeIncrease [field_0x120] |
| SwordSpecialization | Warrior | `might` | _ExtraSwingChance [field_0x120] |
| TastyTreats | Hunter | `might` | Power_SummonWolf_Attack_Speed_Bonus, Power_SummonWolf_Base_Life_Regen |
| WandMaster | Necromancer | `might` | _MoreDamageWithWands [field_0x120] |
| UnholyFocus | Necromancer | `mobility` | Power_Unholy_Focus_Attack_Speed_Bonus_Percent, Power_Unholy_Focus_Death_Attacks_On_Target_Instantly_Kills_Threshold, Power_Unholy_Focus_Demon_Shadowbolt_Weapon_Damage_Multiplier, Power_Unholy_Focus_Movement_Speed_Bonus_Percent, Power_Unholy_Focus_Skeleton_Charge_Cooldown |
| Blizzard | Mage | `nova` | Base_Crit_Chance_Multiplier |
| ElementalSeal | Mage | `nova` | Power_Elemental_Seal_Weapon_Damage_Multiplier |
| Slam | Warrior | `nova` | Slam_Max_Distance |
| PowerShot | Hunter | `projectile` | Base_Action_Speed |
| Tornado | Mage | `projectile` | Power_Duration |
| Backflip | Hunter | `rally` | Power_Backflip_Increased_Backflip_Speed_Percent |
| Teleport | Mage | `rally` | Base_Action_Speed |
| DemonicPresence | Necromancer | `summon` | Base_Power_Radius, Life_Regen_Bonus, Power_Demonic_Presence_Base_ForceField_Bonus_Percent, Power_Demonic_Presence_Base_Life_Bonus_Percent |
| DrainLife | Necromancer | `summon` | Base_Action_Speed, Base_Power_Radius, field_0x158 |
| Sacrifice | Necromancer | `summon` | Power_Life_And_Force_Field_Gained_From_Minion_Percent |
| SummonDeath | Necromancer | `summon` | Minion_Inheritance_Armor_Evasion_Bonus_Percent, Minion_Inheritance_Attack_Speed_Bonus_Percent, Minion_Inheritance_Crit_Chance_Bonus_Percent, Minion_Inheritance_Crit_Damage_Bonus_Percent, Minion_Inheritance_Force_Field_Bonus_Percent, Minion_Inheritance_Life_Bonus_Percent, Minion_Inheritance_Movement_Speed_Bonus_Percent, Minion_Inheritance_Resistances_Bonus_Percent |
| SummonDemon | Necromancer | `summon` | Minion_Inheritance_Armor_Evasion_Bonus_Percent, Minion_Inheritance_Attack_Speed_Bonus_Percent, Minion_Inheritance_Crit_Chance_Bonus_Percent, Minion_Inheritance_Crit_Damage_Bonus_Percent, Minion_Inheritance_Force_Field_Bonus_Percent, Minion_Inheritance_Life_Bonus_Percent, Minion_Inheritance_Movement_Speed_Bonus_Percent, Minion_Inheritance_Resistances_Bonus_Percent, Minion_Inheritance_Weapon_Damage_Bonus_Percent, Power_Minion_Duration |
| SummonSkeleton | Necromancer | `summon` | Minion_Inheritance_Armor_Evasion_Bonus_Percent, Minion_Inheritance_Attack_Speed_Bonus_Percent, Minion_Inheritance_Crit_Chance_Bonus_Percent, Minion_Inheritance_Crit_Damage_Bonus_Percent, Minion_Inheritance_Force_Field_Bonus_Percent, Minion_Inheritance_Life_Bonus_Percent, Minion_Inheritance_Movement_Speed_Bonus_Percent, Minion_Inheritance_Resistances_Bonus_Percent, Minion_Inheritance_Weapon_Damage_Bonus_Percent |
| BoneArmor | Necromancer | `warding` | _PhysicalDamageReductionBonus [field_0x120] |
| SpearSpecialization | Warrior | `warding` | _ArmorPiercing [field_0x120] |

## 说明

- `prototypeConfidence` 来自生成目录的实际映射（`PowerProfiles.generated.cs`），不是分类器推测。
- `behaviorGaps` 同时计入通用原型和 C05/C06 按技能分支；只读取但未产生实际效果的值仍保留为 gap。被动技能附带 `nativePassiveRecovery`，链接原生 Init/Apply/Remove 地址、字段及属性写入；原始 field offset 只在原生 this-store 与 Cpp2IL 字段赋值可配对时命名，不再用桌面 dump.cs 的同偏移字段直接推断。
- 整体技能等级曲线仍未完整恢复；passive sidecar 暴露已解出的初始化字段公式，但不把局部字段公式冒充完整技能成长或已接入运行时。
- 被动与召唤属性不再按 prototype 或字段名通配豁免；未逐项证实的属性（含 field_0x120/field_0x128）保留在 gaps。
- 本清单是 C05（首批 4 技能贯通）与 C06（生产技能池去原型化）的输入。
