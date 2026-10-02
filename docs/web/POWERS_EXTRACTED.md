# 技能提取清单（2026-10-02）

回答「技能是否提取完毕」：**数据层能提取的已全部提取；数值层（效果数字）不在数据里，需从 IL2CPP 二进制方法体反汇编。**

生成方式：
```bash
.tools/web-assets-venv/bin/python tools/web-content/export_powers.py
```
产物：

| 文件 | 用途 |
|---|---|
| `web/public/assets/powers.json` | 客户端展示：全职业池 + 选定技能组 |
| `tools/web-content/generated/powers_full.json` | 完整参考：全部技能 + 全部精通 + 实现映射 |
| `server/Nordicandia.Server/WebApi/Powers.generated.cs` | 服务端行为表（每职业 3 主动 + 1 被动） |

## 已提取（ClientVerified）

| 数据 | 数量 | 来源 |
|---|---:|---|
| Powers | 159 | `Powers.json`（名称/描述/图标/类型/标签/所需武器/是否指向施法/是否隐藏） |
| PowerTypes | 26 | `PowerTypes.json`（含父子层级与可装备槽位） |
| PowerTags | 19 | `PowerTags.json` |
| PowerMasteries | 297 | `PowerMasteries.json`（分类/树行/上限/依赖/属性修正） |
| 职业技能池 | Warrior 13+11、Hunter 14+11、Mage 12+10、Necromancer 12+10、Priest 1+0（主动+被动） | `CharacterClasses.json` |
| 实现类映射 | 154 个技能 / 296 个精通 | `steam_analysis/dump.cs` 的 `[HandledPower]` / `[HandledPowerMastery]` |
| 参数字段名 | 见下 | 同上（类型字段，非数值） |

### 选定技能组（每职业）

| 职业 | 主动（真实名） | 被动 |
|---|---|---|
| Warrior | Slam / Might / Pounce | Overkill |
| Hunter | RapidFire / ArrowRain / SummonWolf | Precision |
| Mage | ChainLightning / Teleport / IceNova | Hubris |
| Necromancer | SummonSkeleton / AstralWalk / Shadowbolt | Bodyguard |

### 描述占位符角色（Inferred）

从描述文案推断 `{0}`… 的含义，例如：

* `ChainLightning` → `{0}=damage, {1}=percent(每跳衰减), {2}=count(跳数), {3}=duration(冷却), {4}=mana`
* `IceNova` → `{0}=damage, {1}=radius, {2}=duration(冻结), {3}=duration(冷却), {4}=mana`
* `Might` → `{0}=percent(力量), {1}=duration`

### 从 dump 恢复的参数字段名（节选）

`_WeaponDamageMult`(15)、`_Duration`(14)、`_TimeoutTimer`(12)、`_Effect`(11)、`_MinionHealthPercent`(9)、`_MinionDamagePercent`(9)、`_NumMinions`(4)、`_Radius`(4)、`_BeamThicknessRadius`(5)、`_DamageReductionPerJump`、`_ResistanceBonus`、`_DamageTakenAsElement`。

这些字段名说明每个技能有哪些参数（伤害倍率、持续时间、半径、召唤数量、跳跃衰减等），但**字段初值在方法体里**，dump 只有签名与地址。

## 尚未提取（需要二进制反汇编）

* **具体数值**：技能的基础伤害倍率、冷却、法力消耗、半径/持续时间等，位于 IL2CPP 各 `Power` 子类的 `InitializePowerParameters` / `InternalInitializePowerParameters` 方法体（dump 中仅有地址区间，如 `Slam` 的 `0x…`）。
* **每级/每精通数值公式**：`PowerMasteries` 里 124 个精通带有 `AttributeSpecifierDefinitionList`（属性 id + 初值/增量 + 触发等级），这些可读取；但其余精通与技能基础值仍要反汇编。
* 技能选择/加点规则、法力系统、被动在战斗中的实际结算。

## 与当前网页实现的关系

* 名称/描述/图标/类型/标签/职业归属 = **真实一致**（ClientVerified）。
* 服务器已按职业加载真实技能名与被动名，快照返回每个技能的真实名称与冷却。
* 效果归类（`strike`/`nova`/`rally`）与数值 = **Provisional**，见 [M0_RULES.md](M0_RULES.md)；等数值反汇编后替换。
