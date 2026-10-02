# Nordicandia 机制还原清单（2026-10-02）

本文汇总从数据文件与 IL2CPP 二进制反汇编还原的游戏机制，标注可信度。
可信度：**ClientVerified**（已从二进制/数据逐字还原）、**Inferred**（由数据/脚本推断）、**Provisional**（占位，待还原）。

> **分层标注**：代码不再把整条伤害管线标成单一 `ClientVerified`。`DamageResult.Confidence` 现为
> `DamageConfidence`，分 `Formula`（命中/暴击/减伤公式，ClientVerified）、`Inputs`（属性来源，当前 Provisional）、
> `Execution`（合成/顺序，当前 Inferred）三层，并带 `AssumedReductionCap`/`AssumedVariance`/`AssumedMinDamage`
> 标记；`Overall` 取最弱一层。属性引擎已实现并接入：`Nordicandia.Simulation.CharacterAttributeEngine` 用嵌入的
308 条公式+常量对角色属性表求值，`CharacterRatings.Apply` 把 AttackRating/Armor/Evasion/CritChance/Life_Max/Mana_Max
喂给 `CombatantStats`，战斗按真实评级结算（无地图/物品时回退到临时值）。

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

## 属性 → 公式映射（已完成，ClientVerified）

`tools/web-content/extract_attribute_formulas.py` 解析桌面 `GameAttributes` 的静态构造，把每个脚本属性的
**enum id → 名称 → 公式** 配对出来（`GameAttributeD/I` 的 `ctor(this, id, default, script, name, ...)`，
公式在 `r9`、名称在 `[rsp+0x20]`）。产出 `tools/web-content/generated/attribute_formulas.json`，共 **308 条**（含像 `Armor_Focus_Factor_Constitution = 1` 这样的常量脚本与标识符脚本）：

```
0x10c Armor_Factor_Constitution => (Constitution_Total * Constants.Armor_Per_Constitution_Factor)
0x10d Armor_SubTotal             => ((Armor + Flat_Armor_From_Constitution) * (1 + Armor_Factor_Constitution))
0x48? Armor_Total                => (Armor_SubTotal * Armor_Focus_Factor_Constitution * (1 + Armor_Bonus_Percent) * Base_Armor_Multiplier)
0x137 Life_Max_Total             => ((Life_Max_SubTotal * (1 + Life_Bonus_Percent)) * Base_Life_Multiplier) MultiplyWith:Life_Bonus_Percent_Final
```

公式语言：中缀 `+ - * /`、括号、`Pin(a,min,max)`（钳制）、`Min(a,b)`、`Pow`、`Constants.X`、
后缀 `MultiplyWith:X`（= 乘 `(1+X)`）、以及三元 `(Cond ? A : B)`（双持/武器类型）。

**常量**：`extract_constants.py` 从 Android `Game.Constants` getter 提取 41 个常量
（`Armor_Per_Constitution_Factor=0.03`、`Attack_Rating_Per_Dexterity=0.32`、`Life_Per_Vitality=0.05` 等），
存于 `generated/constants.json`。**求值器**：`Nordicandia.Simulation.AttributeFormula` 已实现该公式语言。

**最小属性依赖链已验证**：用真实存档的属性表
（`fixtures/character_attributes.json`，来自 `server/data/world.json` 某角色）、308 条公式与常量，
`CharacterAttributeChainTests` 递归求值得到 `Strength_Total=(15+275)=290`、
`Armor_Total≈251.4`、`AttackRating_Total≈19.8`、`Evasion_Total≈40.0`、`Life_Max_Total≈558.8`、
`Mana_Max_Total=125`。基础属性（`Base_Strength` 等）来自存档 origin 0，物品属性来自 origin 1。

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

## 装备系统：物品属性（ClientVerified id，幅度 Provisional）

装备物品携带客户端属性 id，由属性引擎按还原公式结算为角色评级：

| 部位 | 物品属性 | 引擎产出 |
|---|---|---|
| 主手 | `Item_Weapon_Physical_Damage_Min/Delta_MainHand`(507/508)、`Item_Weapon_<element>_Damage_*`(1407/1507/1607/1707)、`Item_Attack_Speed_MainHand`(454)、`Item_Crit_Chance_MainHand`(701)、`Item_Attack_Range_MainHand`(217) | `Weapon_Physical_*_Total` → 伤害包；暴击率；攻速 |
| 副手/盾 | `Armor`(251)、`Item_Attack_Speed_OffHand`(455) | `Armor_Total` |
| 护甲 | `Armor`(251)；靴/披/腕另加 `Evasion`(256) | `Armor_Total`/`Evasion_Total` |
| 饰品/护甲 | `Resistance_All`(1002) | `Resistance_*_Total_Capped` |

实测：装等护甲使 `Armor_Total` 251.4→404.2；主手使伤害包 389.4（物理 268.8 + 元素 120.6）。
id 与公式为 ClientVerified；数值幅度仍为 Provisional（`ItemGenerator` 的基础值生成未逐条还原）。

**词缀**：`AffixCatalog`（18 个真实词缀，来自 `Affixes.json`）授予客户端属性：
`Strength/Dexterity/Intelligence/Vitality/Constitution/Agility_Bonus_Percent`(109-130)、
`Armor_Bonus_Percent`(252)、`Evasion_Bonus_Percent`(257)、`AttackRating_Bonus_Percent`(204)、
`Crit_Chance_Bonus_Percent`(705)、`Crit_Damage_Bonus_Percent`(713)、`Attack_Speed_Bonus_Percent`(451)、
`Resistance_Fire/Cold/Lightning/Poison/All`(1000/1011/1012/1013/1002)，数值按稀有度区间掷取。
`GetAttributeMap` 现在只计入**已装备**物品（含其词缀）的属性。

**套装（`SetCatalog`）**：11 套（`ItemSets.json`），按装备件数触发断点（如 2/4/6/9），
授予的属性并入 `GetAttributeMap`（例：Heavy_TheMarauder 2 件 → `Movement_Speed_Bonus_Percent=20`）。
稀有度 ≥4 的装备随机带套装 id（web 属性 99005）。

**装备等级需求**：物品带 `AttrRequiredLevel`(=掉落等级)；超过角色等级 + 宽容带(99006/20) 时
`equip` 返回 `level_requirement`（宽容带为 Provisional，客户端精确规则未还原）。
