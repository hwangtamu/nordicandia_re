# 被动技能反汇编恢复台账（自动索引）

该文件将 4 个职业池中的每个被动技能，与 Android 基线的 `InternalInitializePowerParameters`、`Apply`、`Remove` 及 Cpp2IL 子类/嵌套类方法证据关联。它是逐项人工语义恢复的输入，**方法被索引不等于效果已完全还原**。

2026-10-03 后续运行时接入：Meditation 的 cooldown modifier（属性 96，`min(0.1+0.01*(rank-1),1)`）按存档 Power_Rank 计算并缩短 active skill cooldown；`Brain_OnSkillStarted` 读取属性 176、执行 CalculateChance 并在成功时移除对应 cooldown，网页显示 reset 事件。基线 reset chance 为 −0.1，单独装备时不会 reset；其他来源对该属性的合并和 Remove 生命周期仍待实现。rank-5 loadout/重建测试通过。下表全行为验收计数仍为 0。

- 被动职业池行数：42；存在对应 IL2CPP 类源文件：41；全行为已验收：0。
- 自动恢复线性等级公式字段：48；事件/时序 hook 入口：28（尚未接入运行时）。
- 原生二进制 SHA-256：`529bd257af0cd6e953fb50f51a69857f42f04eb70acab0586f8413d1f97afd1d`。
- 逐技能的 native 方法地址/hash、字段名/偏移、参数、Buff attribute 写入、嵌套类与事件触发钩子见 `tools/web-content/generated/passive_skill_recovery.json`。

| 职业 | 被动 | Native 类源 | dump.cs 字段索引（非 Android 偏移证明） | Apply 写入属性 | 参数恢复状态 |
|---|---|---|---|---|---|
| Warrior | Overkill | ✓ | _OverkillChance@0x120 | Overkill_Chance | 1 affine field formulas |
| Warrior | TreasureHunter | ✓ | _MagicFindIncrease@0x120, _ItemQuantityIncrease@0x128 | Item_Quantity_Bonus_Percent, Magic_Find_Bonus_Percent | rank_formulas |
| Warrior | AxeSpecialization | ✓ | _DeadlyChance@0x120 | Deadly_Strike_Chance_With_Axes | 1 affine field formulas |
| Warrior | SwordSpecialization | ✓ | _ExtraSwingChance@0x120, _MaxAdditionalSwings@0x128 | Extra_Swing_Reproc_Chance_Swords | 2 affine field formulas |
| Warrior | MaceSpecialization | ✓ | _ThrowMaceChance@0x120, _ThrowRadius@0x128 | MaceSpecialization_Throw_Mace_Chance, MaceSpecialization_Throw_Mace_Radius | 2 affine field formulas |
| Warrior | SpearSpecialization | ✓ | _ArmorPiercing@0x120 | Armor_Piercing_Percent_With_Spears | 1 affine field formulas |
| Warrior | DaggerSpecialization | missing | — | — | initializers not classified |
| Warrior | CheatDeath | ✓ | _CooldownTime@0x120, _FatalDamageNewLifePercent@0x128, _VitalityBonusPercent@0x130 | Base_Cooldown, Fatal_Damage_New_Life_Percent, Vitality_Bonus_Percent_Final | 3 affine field formulas |
| Warrior | BossHunter | ✓ | _SpawnIncrease@0x120 | UniqueMonster_Find_Bonus_Percent | 1 affine field formulas |
| Warrior | Meditation | ✓ | _CooldownReduction@0x120, _CooldownResetChance@0x128 | Power_Cooldown_Reset_Chance | 2 affine field formulas |
| Warrior | RepelMagic | ✓ | _IncreasedMaximumResistances@0x120, _IncreasedMaximumPhysReduction@0x128 | Physical_Damage_Reduction_Max, Resistance_Max_Bonus | rank_formulas |
| Hunter | Precision | ✓ | _AttackRatingBonus@0x120, _MaxStacks@0x128 | AttackRating_Bonus_Percent, MaxStackAmount | 2 affine field formulas |
| Hunter | Sharpshooter | ✓ | _RangeIncrease@0x120 | Attack_Range_Bonus_Percent | 1 affine field formulas |
| Hunter | BeastTracking | ✓ | _MoreBeastDamage@0x120, _MoreChampionMonsters@0x128 | ChampionMonster_Find_Bonus_Percent, More_Damage_To_Beasts | 2 affine field formulas |
| Hunter | DoubleDraw | ✓ | _ExtraProjectileChance@0x120 | Abilities_More_Projectiles_Chance_Array | 1 affine field formulas |
| Hunter | PoolOfMana | ✓ | _ManaBonus@0x120 | Mana_Bonus_Percent_Final | 1 affine field formulas |
| Hunter | Concentrate | ✓ | — | Hit_Chance_Bonus_Percent, Mana_Cost_Increase_Percent_Final | initializers not classified |
| Hunter | Cripple | ✓ | _MinimumAttackRangePercent@0x120, _SlowPercent@0x128, _SlowDuration@0x130 | Buff_Duration, Minimum_Attack_Range_Percent, Movement_Speed_Bonus_Percent_Final | 3 affine field formulas |
| Hunter | DeadlyPoison | ✓ | — | Double_Damage_Chance_On_Crit_On_Poisoned_Target, Poison_Chance_On_Hit | rank_formulas |
| Hunter | TastyTreats | ✓ | — | Power_SummonWolf_Attack_Speed_Bonus, Power_SummonWolf_Base_Life_Regen | initializers not classified |
| Hunter | Solitary | ✓ | — | Power_More_Weapon_Damage_When_No_Wolf_Is_Active | rank_formulas |
| Hunter | Fork | ✓ | — | Projectile_Auto_Attacks_Fork_Chance | rank_formulas |
| Mage | Hubris | ✓ | _IntelligenceBonus@0x120 | Intelligence_Bonus_Percent | 1 affine field formulas |
| Mage | ManaFlow | ✓ | _IncreasedMaximumManaPercent@0x120, _IncreasedManaRegeneration@0x128 | Mana_Bonus_Percent, Mana_Regen_Bonus_Percent | 2 affine field formulas |
| Mage | RapidCasting | ✓ | _MoreAttackSpeedPercent@0x120, _MoreSkillManaCostPercent@0x128 | Attack_Speed_Bonus_Percent_Final, Mana_Cost_Increase_Percent_Final | 2 affine field formulas |
| Mage | AncientKnowledge | ✓ | _ExperienceBonus@0x120 | Experience_Bonus_Percent | 1 affine field formulas |
| Mage | BloodCasting | ✓ | _PercentOfManaCostTakenAsLife@0x120 | Percent_Of_Mana_Cost_Taken_As_Life | 1 affine field formulas |
| Mage | FireArmor | ✓ | _ResistanceBonus@0x120, _DamageTakenAsElement@0x128 | Damage_Taken_Converted_To_Fire_Percent, Resistance_Fire | rank_formulas |
| Mage | ColdArmor | ✓ | _ResistanceBonus@0x120, _DamageTakenAsElement@0x128 | Damage_Taken_Converted_To_Cold_Percent, Resistance_Cold | rank_formulas |
| Mage | LightningArmor | ✓ | _ResistanceBonus@0x120, _DamageTakenAsElement@0x128 | Damage_Taken_Converted_To_Lightning_Percent, Resistance_Lightning | rank_formulas |
| Mage | EvasiveManeuver | ✓ | _IncreasedDodgeChance@0x120, _ChanceToReduceTeleportCooldownWhenHit@0x128, _TeleportCooldownReductionTime@0x130, _TeleportCooldownReductionProcCooldown@0x138 | Chance_To_Reduce_Teleport_Cooldown_When_Hit, Dodge_Chance_Bonus_Percent, Teleport_Cooldown_Reduction_Proc_Cooldown, Teleport_Cooldown_Reduction_Time | 2 affine field formulas |
| Mage | ForceField | ✓ | _MoreForceField@0x120 | ForceField_Bonus_Percent_Final | 1 affine field formulas |
| Necromancer | Bodyguard | ✓ | _ReducedCritDamageTaken@0x120 | Reduced_Crit_Damage_Taken | 1 affine field formulas |
| Necromancer | AccumulatingShadows | ✓ | _MaxStacks@0x120, _ShadowBoltDamageIncrease@0x128, _ShadowBoltTimeWindow@0x130 | Power_Accumulating_Shadows_Buff_Duration, Power_Accumulating_Shadows_Max_Stacks, Shadowbolt_Damage_Bonus_Percent | 3 affine field formulas |
| Necromancer | Pillaging | ✓ | _MinionMagicFindIncrease@0x120, _AdditionalIronDropChance@0x128 | Minion_Kill_Magic_Find_Bonus_Percent, Set_Chance_To_Find_Additional_Iron_On_Drop | rank_formulas |
| Necromancer | BoneArmor | ✓ | _PhysicalDamageReductionBonus@0x120, _DamageTakenAsPhysical@0x128 | Base_Physical_Damage_Reduction_Bonus, Damage_Taken_Converted_To_Physical_Percent | 2 affine field formulas |
| Necromancer | DarkMagic | ✓ | _ManaGainedOnKill@0x120, _ManaCostReduction@0x128 | Mana_Cost_Increase_Percent_Final, Mana_Gained_On_Kill | 2 affine field formulas |
| Necromancer | GiftOfTheVampire | ✓ | _MinionLeechGainPercent@0x120, _LifeLeechBonus@0x128 | Cold_Life_Leech, Fire_Life_Leech, Lightning_Life_Leech, Minion_Inheritance_Life_Leech_Bonus_Percent, Physical_Life_Leech, Poison_Life_Leech | 2 affine field formulas |
| Necromancer | MasterSummoner | ✓ | _MinionLifeBonus@0x120, _MinionDamageBonus@0x128 | Minion_Inheritance_Life_Bonus_Percent, Minion_Inheritance_Weapon_Damage_Bonus_Percent | rank_formulas |
| Necromancer | Reanimator | ✓ | _ChanceToReanimateFallenMinion@0x120, _ReanimatedMinionLifePercent@0x128 | Minion_Chance_To_Reanimate_On_Killing_Blow, Reanimated_Minion_Life_Percent | 2 affine field formulas |
| Necromancer | VileTouch | ✓ | _MorePoisonDamage@0x120, _PoisonChanceOnHit@0x128 | Poison_Chance_On_Hit, Weapon_Poison_Damage_Bonus_Percent_Final | rank_formulas |
| Necromancer | WandMaster | ✓ | _MoreDamageWithWands@0x120 | Increased_Damage_With_Wands | 1 affine field formulas |

## 解释边界

- `resolvedRawFieldValues` 仅将旧抽取器的 raw offset 与已证明的 Android `this` 写入/Cpp2IL 字段赋值配对；值仍标作 raw store，不假称 rank-1 最终参数。真正的 rank 曲线见 `initializerFormulas` 或版本门控 `parameterRecovery`。
- 极小次正规数（当前 EvasiveManeuver 的 cooldown 字段）留在 `suspiciousInitializerValues`，未当作已恢复参数。桌面 `dump.cs` 的字段偏移不单独作为 Android 字段映射证据。
- `applyAttributes` 由 `Buff.SetAttribute`/`GameAttributeMap.set_Item` 的静态 GameAttribute 偏移映射得到；继承效果、Buff 事件处理、动态目标、概率时序及 Remove 对称性仍需人工审阅原生指令与完整 ISIL。
- `triggerHooks` 提示需要继续追踪的 OnPayload、OnMeleeSwingHit、BuffManager、Brain 和 world 事件；仅索引钩子入口，不代表其条件/时间线已经复刻。
- `parameterRecovery` 仅引用已有的版本门控恢复结果；未恢复的参数保留 raw 方法证据，不填猜测值。
- DaggerSpecialization 若没有类方法源，会明确记录为缺失，需检查版本内容、父类继承或未命名 IL2CPP 类型，不能把缺少的方法当作无效果。

## 复现

```bash
.tools/web-assets-venv/bin/python tools/web-content/recover_passive_skills.py
```
