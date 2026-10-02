# 伤害与属性合成（反汇编还原，2026-10-02）

本文记录从 IL2CPP 提取的伤害/属性管线。数据来源：
`tmp/allattrs.asm`（属性脚本字符串）、`tmp/android-metadata.json` + `tmp/apk-libil2cpp.so`（方法反汇编）。
工具：`tools/web-content/extract_attribute_scripts.py` → `tools/web-content/generated/attribute_scripts.json`（226 条属性合成公式）。

## 1. 减伤（ClientVerified）

反汇编 `Game.Calculator.CalculatePhysicalDamageReduction(double armor, double damage, double bonus, double cap)`：

```asm
mov x8, #0x4049000000000000   ; 50.0
fmul d0, damage, d0           ; damage * 50
fadd d0, d0, armor            ; armor + 50*damage
fdiv d0, armor, d0            ; armor / (armor + 50*damage)
fadd d0, d0, bonus
b Math.Min                    ; min(result, cap)
```

```
CalculatePhysicalDamageReduction(armor, damage, bonus, cap)
    = min( armor / (armor + 50 * damage) + bonus, cap )
```

`cap` 取自 `Physical_Damage_Reduction_Max`（默认 0，由难度/区域数据设置）。网页端 `CombatModel.PhysicalDamageReduction` 已按此实现，默认 cap 0.9。

反汇编 `GameCalculator.ApplyDamageReduction(double reduction, double damage)`：

```asm
fmul d0, d0, d1      ; d0 = reduction * damage
fsub d0, d1, d0      ; d0 = damage - reduction*damage
fcsel d0, 0, d0, mi  ; clamp < 0 to 0
```

即：

```
ApplyDamageReduction(reduction, damage) = max(0, damage * (1 - reduction))
```

`reduction` 是**比例**（0..1）。装甲→reduction 的具体曲线由原生代码计算（未在数据脚本中），
所以网页端 `CombatModel.Mitigation(defense, level)` 仍为 Provisional，但**应用方式与此一致**
（`damage * (1 - mitigation)`，下限 1）。

## 1b. 命中与暴击判定（ClientVerified）

反汇编 `Game.Calculator.CalculateChanceToHit(atk, def, bonus, cap)`：

```
chance = clamp( 1.05*atk / (Pow(def*0.5, 0.75) + atk) + bonus , 0.05 , cap )
```
（常数 `1.05`、下限 `0.05` 从字面量池读出。）

反汇编 `Game.Calculator.CalculateChance(chance)`：

```
CalculateChance(chance) = chance > 0 && Shared.Core.Rand.Value <= chance
```

`HitPayload.IsCriticalHit` 选择 `Crit_Chance_AttackSpell_Total`（法术/治疗）或 `Crit_Chance_CurrentHand_Total`，
乘上 payload 的 `CritChanceMultiplier` 后调用 `CalculateChance`；
`IsDodged`/`IsBlocked`/`IsEvaded` 同样通过 `CalculateChance(对应闪避/格挡/命中属性)` 判定。

网页端 `CombatModel.ChanceToHit` / `RollChance` / `ResolveHit` 已按此实现（先判定命中，再判定暴击，再做减伤与浮动）。

## 1c. 闪避 / 格挡 / 被命中（ClientVerified）

`HitPayload` 的其余布尔字段同样走 `CalculateChance`：

```
IsDodged  = CalculateChance( Dodge_Chance_Total | Dodge_Chance_Spell_Total )
IsBlocked = CalculateChance( Block_Chance_Total | Block_Chance_Spell_Total )
IsEvaded  = 攻击方命中失败（见 CalculateChanceToHit）
IsCriticalHit = CalculateChance( Crit_Chance_{CurrentHand|AttackSpell}_Total * CritChanceMultiplier )
```

（属性偏移 `0x1310/0x1338`（闪避）、`0x1278/0x1298`（格挡）已从反汇编确认。）
格挡后的伤害乘 `Blocked_Damage_Taken_Multiplier_Total`。
网页端暂未接入闪避/格挡，因为网页角色的属性表只有 Offense/Defense/Recovery（缺少闪避/格挡来源）；
接入需要先还原 `Character.CalculateCombatAttributes` 的完整属性合成。

## 1d. 属性合成管线（架构已确认）

`Character.CalculateCombatAttributes(...)` 反汇编显示：它不直接算装备属性，而是**读取已经由脚本 getter 合成的
`*_Total` 属性**，再算显示用的 `Estimated_*`：

```
Estimated_Physical_Damage_Reduction = CalculatePhysicalDamageReduction(
        Armor_Total, <参考伤害>, Base_Physical_Damage_Reduction_Bonus, Physical_Damage_Reduction_Max)
Hit_Chance_Cap                      = CalculateChanceToHit(
        AttackRating_Total, <目标闪避>, Hit_Chance_Bonus_Percent, Hit_Chance_Cap)
Estimated_Chance_To_Evade_Attacks   = CalculateChanceToHit(
        Evasion_Total, <攻击方命中>, ...)
Estimated_World_Resist_Penalty      = f(World_Tier, World_Tier_Unlocked, Options_LastMonsterLevel)
```

属性来源链：
```
物品/等级/Buff → Attributes[origin]  →  226 条脚本 getter  →  *_Total
   (Armor_Total = Armor_SubTotal * Armor_Focus_Factor_Constitution * (1+Armor_Bonus_Percent) * Base_Armor_Multiplier, ...)
→ CalculateCombatAttributes → Estimated_* / 战斗结算
```

**结论**：伤害/命中/减伤的公式已全部还原；要在网页端真正跑起来，必须先实现「物品/等级 → 全部属性」的合成
（226 条脚本 getter + 装备属性贡献）。这是下一步的里程碑（实现量明确，不再是“未知”）。

## 1e. 元素抗性 / 减伤应用（ClientVerified）

`Game.Calculator.ApplyDamageReduction(damageReduction, rawDamage)`（`0x02BAF34C`）：
```
return Max(0, rawDamage * (1 - damageReduction))
```
反汇编：`FMUL d0,d0,d1; FSUB d0,d1,d0; FCMP d0,#0; FCSEL d0,#0,d0,MI` —— 即 `raw·(1−red)`，下限 0。

元素抗性在 `GameCalculator.CalculateEffectiveElementalDamagePerSecond`（`0x…`）中先对每类抗性取
`Math.Min(1, resistance)`（上限 1.0），再按上式应用到对应元素伤害。伤害转换另有
`GameCalculator.ConvertWeaponDamage` / `InternalApplyDamageConversion`（把一类伤害按百分比转成
其他元素），与抗性减免相互独立。

## 2. 武器伤害合成（ClientVerified，来自属性脚本）

物理武器伤害（每手）：

```
weapon = (Item_Weapon_Physical_Damage_{Min|Delta}_{MainHand|OffHand} + Base_Weapon_Physical_Damage_{Min|Delta}_Bonus)
       * (1 + (Weapon_Physical_Damage_Strength_Coefficient * Strength_Total) * 0.01)
       * (Physical_Weapon_Damage_Focus_Factor_Strength + (Physical_Weapon_Damage_Focus_Factor_Intelligence - 1))
       * Weapon_Physical_Damage_Percent_Total
       * (1 + Pin(Wand_{Hand}, 0, 1) * Increased_Damage_With_Wands)
  MultiplyWith: Weapon_Physical_Damage_Bonus_Percent_Final
  MultiplyWith: Weapon_Damage_Percent_Bonus_Final
```

技能倍率（描述里的 `{0}% weapon damage`）：

```
Weapon_Physical_Damage_Percent = Base_Weapon_Physical_Damage_Percent * (1 + Weapon_Physical_Damage_Bonus_Percent)
```

元素武器伤害（Fire/Cold/Lightning/Poison）结构相同，系数换成
`Weapon_Elemental_Damage_Intelligence_Coefficient * Intelligence_Total` 与
`Elemental_Weapon_Damage_Focus_Factor_*`。

## 3. 暴击（ClientVerified）

```
crit_chance = (Item_Crit_Chance_{MainHand|OffHand} * (1 + Crit_Chance_Bonus_Percent)) * Base_Crit_Chance_Multiplier
            MultiplyWith: Crit_Chance_Bonus_Percent_Final
crit_damage = Base_Crit_Damage * (1 + Crit_Damage_Bonus_Percent)
            MultiplyWith: Crit_Damage_Bonus_Percent_Final
```

## 4. 护甲（ClientVerified）

```
armor_sub    = (Local_Implicit_Base_Armor + Local_Base_Armor) * (1 + Local_Armor_Bonus_Percent)
armor_total  = (Armor + Flat_Armor_From_Constitution) * (1 + Armor_Factor_Constitution)
constitution = Constitution_Total * Constants.Armor_Per_Constitution_Factor
             + Constants.Flat_Armor_From_Constitution_Base_Value * (1 + Pow(Constitution_Total * Constants.Flat_Armor_Per_Constitution_Factor, ...))
Armor_Total  = Armor_SubTotal * Armor_Focus_Factor_Constitution * (1 + Armor_Bonus_Percent) * Base_Armor_Multiplier
             MultiplyWith: Armor_Bonus_Percent_Final
```

## 5. 技能执行（ClientVerified）

反汇编 `ChainLightning/Perform/MoveNext` 调用图：

```
GetBestMeleeEnemy(TargetList) -> StartCooldown(double) -> GetManaCost()
   -> PowerContext.ConsumeManaForSkillUse(cost) -> 多次 GameAttributeMap.get_Item(...)
```

释放消耗 `GetManaCost()`（真实法力），进入冷却，选最佳近战目标后链式弹射。
链数取 `ChainLightning_Max_Num_Chains_Total`（= `ChainLightning_Max_Num_Chains` 4 + `ChainLightning_Additional_Max_Num_Chains`）。

## 6. 网页端现状与差距

* 已一致：减伤应用方式、武器/技能倍率结构、暴击结构、法力消耗与冷却流程、技能语义（类继承链）。
* 仍 Provisional：装甲→减伤比例的曲线（原生函数未还原）；随机范围/命中率、元素抗性、格挡/闪避、持续伤害等。
* 下一步：反汇编原生“armor→reduction”函数（`HitPayload`/`GameCalculator` 内），以及命中/暴击判定与随机范围。
