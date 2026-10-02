# 技能数值反汇编提取（2026-10-02）

回答「具体数值能不能提取」：**能，已提取。** 数值不在解密 JSON 里，而在 IL2CPP 原生代码的
`Power.InternalInitializePowerParameters` 方法体中。本仓库同时具备：

* `tmp/apk-libil2cpp.so`（Android arm64 原生模块）
* `tmp/android-metadata.json`（Il2CppInspector 地址表，含 17 万方法地址）
* `steam_analysis/dump.cs`（类型/字段布局，含 `GameAttributes` 字段偏移）

## 方法

客户端每个技能在 `InternalInitializePowerParameters` 里调用：

```
GameAttributeMap.set_Item(AttributeOrigin, GameAttribute, double)
    x0 = map, w1 = origin(3=Power), x2 = GameAttribute*, d0 = value
```

`x2` 从静态字段 `[staticFields + offset]` 载入，offset 与 `dump.cs` 的 `GameAttributes`
字段表一一对应（如 `0x13c8 = Base_Power_Weapon_Damage_Multiplier`、`0x1340 = Base_Cooldown`、
`0x1398 = Base_Mana_Cost`、`0x1418 = Base_Power_Radius`）。因此可以：

1. 用 capstone 反汇编方法体；
2. 跟踪 `fmov` / 字面量池 `ldr dN` 得到 `d0`；
3. 跟踪 `ldr x2,[x8,#off]` 得到属性名；
4. 在 `bl set_Item` 处记录 `属性 = 数值`。

工具：`tools/web-content/disasm_powers.py`
（`--all` 覆盖全部技能，输出 `tools/web-content/generated/power_values.json`）。

## 覆盖

145 个技能方法中 **126 个**可恢复至少一个数值；分布：

| 属性 | 数量 |
|---|---:|
| Base_Cooldown | 72+ |
| Base_Mana_Cost | 34+ |
| Base_Power_Weapon_Damage_Multiplier | 26+ |
| Base_Power_Radius | 21+ |
| Buff_Duration / Power_Duration / Power_Freeze_Duration | 30+ |
| 其他（Minion 继承、Action_Speed、Slam_Max_Distance、Max_Num_Chains…） | 多项 |

被动技能（`field_0x120`）也恢复了基础值。

## 选定技能组数值（ClientVerified）

| 职业 | 技能 | 效果 | 倍率 | 冷却 | 半径 | 法力 | 其他 |
|---|---|---|---:|---:|---:|---:|---|
| Warrior | Slam | strike | 550% | 15s | — | 35 | 距离 15 |
| Warrior | Might | rally | — | 30s | — | — | +75% 力量，10s |
| Warrior | Pounce | strike | 25% | 10s | — | 15 | |
| Hunter | RapidFire | rally | — | 25s | — | 30 | |
| Hunter | ArrowRain | nova | 140% | 25s | 3 | 20 | |
| Hunter | SummonWolf | rally | — | 45s | — | 45 | |
| Mage | ChainLightning | nova/chain | 250% | 20s | — | — | 最多 4 跳 |
| Mage | Teleport | move | — | 12s | — | 20 | |
| Mage | IceNova | nova | 50% | 30s | 6 | — | 冻结 2s |
| Necro | SummonSkeleton | summon | — | 10s | — | — | 随从继承 |
| Necro | AstralWalk | move | — | 16s | — | — | 移速 +10 |
| Necro | Shadowbolt | nova | 300%/50% | 15s | 2.5 | 30 | |

被动基础值：Overkill 1.0、Hubris 0.3、Precision 0.5、Bodyguard 0.1。

这些数值已写入 `Powers.generated.cs`，服务端冷却/倍率/半径/法力均使用真实值；客户端按钮显示真实技能名与法力。

## 效果语义（类继承链为准，权威）

从 `dump.cs` 提取每个技能实现类的**继承链**，比属性启发式更可靠：

| 继承链 | 语义 | 例 |
|---|---|---|
| `< Nova` | `nova`（范围） | IceNova |
| `< ProjectileSkill` | `projectile`（远程单体/穿透） | Shadowbolt |
| `< Skill < PowerScript`（非 ActionTimedSkill） | `summon` | SummonSkeleton / SummonWolf |
| `< Aura...` | `aura` | — |
| 恢复出 `Max_Num_Chains` | `chain` | ChainLightning |
| `Mana_Shield` / `Life_Leech` / `Movement_Speed` | `shield`/`leech`/`mobility` | |
| 其余 ActionTimedSkill | `strike`/`nova`/`rally` 按半径与伤害 | Slam/Pounce/Might |

服务端已实现 `projectile`（远程 + 穿透）、`chain`、`nova`、`rally`、`summon`、`mobility`、`shield`。

## 效果语义（旧启发式说明，已由继承链取代）

不再用文案启发式，而是**根据恢复出的属性集**判定语义：

| 恢复到的属性 | 语义 |
|---|---|
| `*_Max_Num_Chains` | `chain`（链式，最多 N 跳） |
| `Minion_Inheritance_*` / `Minion_Duration` | `summon`（召唤） |
| `Mana_Shield_*` | `shield` |
| `*_Life_Leech*` | `leech` |
| `*_Movement_Speed_*`（无伤害） | `mobility` |
| `Base_Power_Radius > 0` | `nova`（范围） |
| `*_Pierce*` / `*_Num_Charges*` | `projectile` |
| 有伤害倍率 | `strike` |
| 无伤害但有 Buff/力量 | `rally` |

服务端已实现：`chain`（按距离取 N 个目标、逐跳衰减 15%）、`nova`、`rally`、`summon`（近似为攻击 Buff）、`mobility`（移速 Buff）、`shield`（近似治疗）；每个技能快照带 `special` / `chains`。

典型结果：ChainLightning=`chain`(4)、IceNova=`nova`/`freeze`、SummonSkeleton=`summon`、AstralWalk=`mobility`、RapidFire/Might=`rally`。

## 精通（PowerMasteries）

* 297 条精通、124 条带属性修正已全部提取，保存在 `tools/web-content/generated/powers_full.json` 的 `masteriesByPower`。
* `powers.json` 里每个选定技能附带其精通列表（名称/树行/上限/属性修正），客户端 tooltip 显示精通条数。
* **尚未接入战斗**：真正生效需要技能树/加点系统与每点属性修正计算，属于后续里程碑。

## 法力系统（本次实现）

* `CombatInstance` 增加法力池：`MaxMana = 40 + 10*等级`（Provisional），每秒回复 `4+等级`。
* 释放技能先检查 `Mana >= ManaCost`，不足返回 `no_mana`；成功释放扣除法力（真实法力消耗来自反汇编）。
* 快照返回 `playerMana/maxMana`，客户端 HUD 显示蓝色法力条；法力不足提示“Not enough mana”。

## 更细语义（本次实现）

* **chain**：按距离取最近 `Max_Num_Chains`(4) 个目标，逐跳衰减 15%。
* **freeze / stun**：IceNova / WarStomp 等命中后给怪物 `StunTimer`（真实 `Power_Freeze_Duration=2s` / `Power_War_Stomp_Stun_Duration`），怪物在眩晕期间不移动不攻击。
* **shield**：ManaShield 按 `Mana_Shield_Life_Factor` 产生护盾，怪物伤害先扣护盾再扣血；HUD 显示 `(+护盾)`。
* **mobility / summon**：移速 Buff / 攻击 Buff（计时器）。

## 精通系统（本次实现）

属性 id 映射由 `tools/web-content/extract_attribute_ids.py` 从 `allattrs.asm` 恢复（927 个），例如：
`58=Base_Power_Weapon_Damage_Multiplier`、`79=Base_Cooldown`、`80=Base_Mana_Cost`、`143=ChainLightning_Max_Num_Chains`、`166=Buff_Duration`、`193=Base_Power_Radius`、`205=Power_Freeze_Duration`。

* 生成物 `Powers.generated.cs` 增加 `MasteriesByPower`（技能 → 精通 → 属性修正）。
* **公式（Provisional）**：某精通第 `r` 点贡献 `StartValue + Value * r` 到对应属性；基础值加总后重算倍率/冷却/法力/半径/链数。
  例：`MasteryChainLightningChains` 每点 `ChainLightning_Max_Num_Chains +1`；`MasterySlamWideSlam` 每点 `Base_Power_Radius +0.1`。
* **点数预算（Provisional）**：`3 + (等级-1)`，已花 = 各精通等级之和。
* 精通等级持久化在 `SavedCharacter.MasteryRanks`，重登/重启后重新叠加并生效（测试验证 4→7 链）。
* 网页 API：`GET /characters/{id}/powers`；命令 `mastery`（携带 `masteryId`，幂等）。客户端 `P` 打开技能面板，可加点并实时看到属性修正。

尾部属性赋值通过**尾调用**（`b` 而非 `bl`）实现；补上尾调用捕获后，多恢复出法力等属性：
ChainLightning 法力 25、IceNova 法力 20、Might 法力 20、Slam 半径 1.0、ArrowRain 伤害 180% 等。

## 精通公式（已从反汇编还原，权威）

反汇编 `PowerMasteryDefinition.GetAttributeSpecifierValue` 得到精确语义（枚举 `AttributeOperators{Add=0,Subtract=1,Multiply=2}`、`AttributeModifierTypes{PerLevel=0,SpecificLevel=1}`）：

```
start = StartValue ?? 0
若 PerLevel(0)     : contribution = start + Value * rank
若 SpecificLevel(1) : contribution = rank >= ModifierForSpecificLevel ? (start + Value) : 0
若 Operator == Subtract(1) : contribution = -contribution
（Operator 0/2 都返回 +contribution；客户端 ApplyMasteryValue 是加和到属性上）
```

已实现为 `MasterySpec.ContributionForRank(rank)`，`CombatRegistry.EffectivePowers` 用它叠加；新增测试 `MasteryFormulaMatchesClient`（PerLevel/Subtract/SpecificLevel 阈值）。数据分布：Add 211、Subtract 46、Multiply 1；PerLevel 237、SpecificLevel 21（阈值多为 1，少数 50）。

## 旗舰技能执行（ChainLightning）

反汇编 `ChainLightning/Perform/MoveNext` 的调用图：

```
GetBestMeleeEnemy(TargetList) -> StartCooldown(double) -> GetManaCost()
  -> PowerContext.ConsumeManaForSkillUse(cost) -> [多次] GameAttributeMap.get_Item(...)
```

确认：释放时消耗 `GetManaCost()`（= `Base_Mana_Cost` 25）并进入冷却，先选最佳近战目标，再链式弹射到额外目标；链数取 `ChainLightning_Max_Num_Chains_Total`（属性 573 = 143 基础 4 + 572 额外）。

## 仍然 Provisional 的部分

* **效果归类**已改为以类继承链为准（见上）；但每个技能的具体执行流程（链子弹射、引导次数、随从实体）仍需继续读方法体（已恢复的字段名如 `_DamageReductionPerJump`、`_NumMinions`、`AstralWalk_Movement_Speed_Bonus_Percent` 是起点）。
* 描述占位符的角色推断偶尔把「冻结持续」与「冷却」都归为 duration（例如 IceNova 的 `{2}/{3}`）。
* 未恢复：技能等级/精通数值（`PowerMasteries` 的 124 条属性修正已提取，公式未接）、法力系统运行、技能加点。

## 复现

```bash
# 需要 tmp/apk-libil2cpp.so、tmp/android-metadata.json、steam_analysis/dump.cs
.tools/web-assets-venv/bin/python tools/web-content/disasm_powers.py --all
.tools/web-assets-venv/bin/python tools/web-content/export_powers.py   # 合并进 powers.json / Powers.generated.cs
```
