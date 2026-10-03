# C04：逐技能行为规格（2026-10-03）

共 93 个技能（主动 51 / 被动 42）。
每个技能的施法条件/目标/资源/冷却/等级曲线/效果时间线/Buff/弹体/召唤/精通改写见 `tools/web-content/generated/skill_specs.json`。

## 反静默映射清单

有 **29** 个技能带有当前原型**未实现**的真实属性（behaviorGaps）——这些曾被静默折叠进原型，现逐条显式列出：

| 技能 | 职业 | 原型 | 未实现的属性 |
|---|---|---|---|
| AstralWalk | Necromancer | `mobility` | AstralWalk_Movement_Speed_Bonus_Percent |
| Fade | Hunter | `mobility` | Evasion_Bonus_Percent |
| UnholyFocus | Necromancer | `mobility` | Power_Unholy_Focus_Attack_Speed_Bonus_Percent, Power_Unholy_Focus_Death_Attacks_On_Target_Instantly_Kills_Threshold, Power_Unholy_Focus_Demon_Shadowbolt_Weapon_Damage_Multiplier, Power_Unholy_Focus_Movement_Speed_Bonus_Percent, Power_Unholy_Focus_Skeleton_Charge_Cooldown |
| Blizzard | Mage | `nova` | Base_Crit_Chance_Multiplier, Power_Duration |
| Decay | Necromancer | `nova` | Power_Decay_Target_Reduced_Attack_Speed_Percent, Power_Duration |
| ElementalSeal | Mage | `nova` | Power_Duration, Power_Elemental_Seal_Weapon_Damage_Multiplier |
| Intimidate | Warrior | `nova` | Buff_Duration, Power_Intimidate_Opponent_All_Damage_Reduced_Percent |
| PoisonCloud | Mage | `nova` | Power_Duration, Power_Slow_Effect_Percent |
| Retaliation | Warrior | `nova` | Buff_Duration |
| Slam | Warrior | `nova` | Slam_Max_Distance |
| Thunderstrike | Mage | `nova` | Power_Duration |
| Whirlwind | Warrior | `nova` | Power_Duration, Power_Whirlwind_Damage_Frequency |
| FrozenArrow | Hunter | `projectile` | Base_Power_Radius |
| IceBlast | Mage | `projectile` | Base_Power_Radius, Power_Weapon_Damage_Multiplier_2 |
| InfernalBlast | Mage | `projectile` | Base_Power_Radius, Power_Duration, Power_Weapon_Damage_Multiplier_2 |
| ManaArrows | Hunter | `projectile` | Power_Mana_Arrows_Mana_Per_Hit, Power_Mana_Arrows_Num_Charges |
| PowerShot | Hunter | `projectile` | Base_Action_Speed |
| Shadowbolt | Necromancer | `projectile` | Base_Power_Radius, Power_Weapon_Damage_Multiplier_2 |
| Tornado | Mage | `projectile` | Base_Power_Radius, Power_Duration |
| Backflip | Hunter | `rally` | Power_Backflip_Increased_Backflip_Speed_Percent, Power_Backflip_Mirror_Image_Duration, Power_Backflip_Mirror_Image_Health_Percent |
| MarkOfTheChosen | Hunter | `rally` | Power_Duration, Power_Mark_Of_The_Chosen_Amplify_Damage_Taken_Percent |
| Teleport | Mage | `rally` | Base_Action_Speed |
| Thorns | Hunter | `rally` | Base_Physical_Damage_Reduction_Bonus, Reflect_Melee_Damage_Percent |
| ManaShield | Mage | `shield` | Mana_Shield_Damage_Absorbtion_Factor |
| ImpalingTrap | Hunter | `strike` | Power_Duration |
| BoneLink | Necromancer | `summon` | Amplify_Damage_Taken_Percent, Buff_Duration |
| DemonicPresence | Necromancer | `summon` | Base_Power_Radius, Life_Regen_Bonus, Power_Demonic_Presence_Base_ForceField_Bonus_Percent, Power_Demonic_Presence_Base_Life_Bonus_Percent, Power_Duration |
| DrainLife | Necromancer | `summon` | Base_Action_Speed, Base_Power_Radius, Base_Power_Weapon_Damage_Multiplier, Power_Drain_Life_Percent_Of_Life_Leech_Leeched_As_Life, Power_Duration, field_0x158 |
| Sacrifice | Necromancer | `summon` | Mana_Granted_Percent, Next_Offensive_Spell_Cast_Damage_Increased_Percent, Power_Duration |

## 说明

- `prototypeConfidence` 来自生成目录的实际映射（`PowerProfiles.generated.cs`），不是分类器推测。
- 等级曲线（per-rank scaling）在 IL2CPP 方法体内，93 个技能统一标记为未恢复，不虚构数值。
- 被动技能（might/warding/haste/fortune）的加成走属性引擎，不计入 gaps。
- 本清单是 C05（首批 4 技能贯通）与 C06（生产技能池去原型化）的输入。

## 可疑映射（C06 待修）

- `Teleport`（Mage）当前原型为 `rally`：实为位移技能，应为 `mobility`。
- `ImpalingTrap`（Hunter）当前原型为 `strike`：实为陷阱（`Power_Duration` 未实现），应为独立的陷阱原型。
- `DrainLife` 带有未映射属性 `field_0x158`，已显式保留在 gaps 中，不丢弃。

## C05 修订（2026-10-03）

C05 关闭了 PoisonCloud 的 2 个 gaps（`Power_Duration`、`Power_Slow_Effect_Percent`），反静默清单 29 → 27 技能。
`skill_specs.json` 是生成器快照（重跑会恢复），以本修订和 `C05_SKILLS.md` 为准。
