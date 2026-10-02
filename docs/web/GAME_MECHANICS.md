# Nordicandia 机制还原清单（2026-10-02）

本文汇总从数据文件与 IL2CPP 二进制反汇编还原的游戏机制，标注可信度。
可信度：**ClientVerified**（已从二进制/数据逐字还原）、**Inferred**（由数据/脚本推断）、**Provisional**（占位，待还原）。

> **分层标注**：代码不再把整条伤害管线标成单一 `ClientVerified`。`DamageResult.Confidence` 现为
> `DamageConfidence`，分 `Formula`（命中/暴击/减伤公式，ClientVerified）、`Inputs`（属性来源，当前 Provisional）、
> `Execution`（合成/顺序，当前 Inferred）三层，并带 `AssumedReductionCap`/`AssumedVariance`/`AssumedMinDamage`
> 标记；`Overall` 取最弱一层。属性引擎（226 条公式作为实际来源）尚未实现。

生成物（`tools/web-content/generated/`）：

| 文件 | 内容 |
|---|---|
| `attribute_ids.json` | **927** 个 `GameAttributes` 数值 id → 名称（从 `allattrs.asm` 的 ctor 还原） |
| `attribute_scripts.json` | **226** 条属性合成公式字符串（Base→Total、暴击、护甲、武器伤害等） |
| `scripted_attribute_funcs.json` | **247** 个脚本属性 getter 的地址（`NordicandiaScriptedGameAttributeFuncs.Get_*`） |
| `power_values.json` | **126/145** 个技能 `InternalInitializePowerParameters` 的真实数值（含尾调用） |
| `powers_full.json` | 159 技能 + 297 精通 + 实现类/继承链/方法/参数 |

## 已还原（ClientVerified / 可直接使用）

### 经验与等级
`ExperienceForLevel(L)=350+20·L^1.7`，`LevelForExperience` 闭式反解（见 `M0_RULES.md`）。

### 减伤应用
`GameCalculator.ApplyDamageReduction(reduction, damage) = max(0, damage·(1−reduction))`
（反汇编确认；`reduction` 为 0..1 比例）。

### 武器伤害合成（属性脚本）
物理每手：
`(Item_Weapon_Physical_Damage_{Min|Delta}_Hand + Base_…_Bonus) · (1 + Weapon_Physical_Damage_Strength_Coefficient·Strength_Total·0.01) · 焦点系数 · Weapon_Physical_Damage_Percent_Total · (1+法杖加成) → ×Bonus_Final`
技能倍率：`Weapon_Physical_Damage_Percent = Base_Weapon_Physical_Damage_Percent·(1+Weapon_Physical_Damage_Bonus_Percent)`
元素（Fire/Cold/Lightning/Poison）结构相同，系数换 `Intelligence_Total`。

### 暴击 / 护甲 / 格挡 / 闪避（属性脚本）
```
crit_chance = (Item_Crit_Chance_Hand · (1+Crit_Chance_Bonus_Percent)) · Base_Crit_Chance_Multiplier → ×Bonus_Final
crit_damage = Base_Crit_Damage · (1+Crit_Damage_Bonus_Percent) → ×Bonus_Final
armor_total = Armor_SubTotal · Armor_Focus_Factor_Constitution · (1+Armor_Bonus_Percent) · Base_Armor_Multiplier → ×Bonus_Final
flat_armor_from_constitution = Constants.Flat_Armor_From_Constitution_Base_Value · (1 + Pow(Constitution_Total·Constants.Flat_Armor_Per_Constitution_Factor, …))
block/dodge/resistance 均有对应的 Total 公式（见 attribute_scripts.json）
```

### 技能
159 个技能定义（名称/描述/图标/类型/标签/职业）、297 个精通（属性修正，`GetAttributeSpecifierValue` 公式已还原）、
四职业技能的真实倍率/冷却/法力/半径/链数，类继承链语义（`Nova`/`ProjectileSkill`/`PowerScript`）。
释放流程：`GetBestMeleeEnemy → StartCooldown → GetManaCost → ConsumeManaForSkillUse`。

### 掉落
稀有度权重取自 `Droprates.json`；掉落概率为 Provisional。

## 怪物 / 物品缩放（数据已提取，公式 Inferred）

`gamedata_decrypted/GameBalance.json` 的 `ScalingFunctions`（38 条）定义了等级/世界层级缩放，字段为
`OwnerLevelMultiplier / OwnerLevelExponent / OwnerLevelThreshold / OwnerTierMultiplier / OwnerTierExponent / FinalMult`。
物品/怪物属性按 `AttributeSpecifierDefinition.ScalingFunctionId` 引用它。

推断公式（字段结构来自 `dump.cs`，等反汇编确认）：

```
scaled = ( OwnerLevelMultiplier * Pow(ownerLevel - OwnerLevelThreshold, OwnerLevelExponent)
         + OwnerTierMultiplier  * Pow(ownerTier, OwnerTierExponent) ) * FinalMult
```

关键项：`ImplicitFlatLifeScaling`（1.3, 1.26, 0.05）、`ImplicitFlatDamageScaling`（0.005, 0.37, 0.3）、
`ImplicitFlatArmorScaling`（0.002, 0.37, 2.0）、`WeaponFlatDamageScaling`、`ArmorFlatArmorScaling` 等。
`MonsterTypes.json`（30 条）给出怪物类型继承与 `Size`。

## 仍为 Provisional / 需继续反汇编

| 机制 | 现状 | 需做什么 |
|---|---|---|
| ~~护甲→减伤曲线~~ | ✅ 已还原 `CalculatePhysicalDamageReduction = min(armor/(armor+50·damage)+bonus, cap)` | — |
| ~~命中/暴击判定~~ | ✅ `CalculateChanceToHit`；`CalculateChance = chance>0 && Rand<=chance` | — |
| ~~闪避/格挡判定~~ | ✅ `CalculateChance(Dodge_Chance_*_Total)` / `(Block_Chance_*_Total)`（网页端缺少属性来源，未接入） | 还原 `Character.CalculateCombatAttributes` 属性合成 |
| **元素抗性与穿透** | 属性 getter 已定位，应用未还原 | 反汇编 `InternalApplyDamageConversion` |
| **怪物属性/AI** | 5 类原型为占位 | 反汇编 `Monster.CalculateAttributes` / `Brain` |
| **持续伤害/DoT、生命偷取** | 字段名已恢复 | 反汇编对应 `Buff` |
| **离线收益** | 未还原 | `CalculateIdleLevelsGained` / `ClaimOfflineRewards` |

## 复现

```bash
.tools/web-assets-venv/bin/python tools/web-content/extract_attribute_ids.py      # 927 属性 id
.tools/web-assets-venv/bin/python tools/web-content/extract_attribute_scripts.py  # 226 公式
.tools/web-assets-venv/bin/python tools/web-content/export_powers.py              # 技能/精通
.tools/web-assets-venv/bin/python tools/web-content/disasm_powers.py --all        # 技能数值
```

（依赖 git 忽略的 `tmp/apk-libil2cpp.so`、`tmp/android-metadata.json`、`steam_analysis/dump.cs`、`tmp/allattrs.asm`。）
