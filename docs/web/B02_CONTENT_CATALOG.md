# B02 内容目录 — 玩家技能与精通（验收分母）

日期：2026-10-03。对应 [完整复刻任务清单](FULL_FIDELITY_BACKLOG.md) 的 **B02**（内容目录）与 **C04/C06** 的验收分母。
数据由 `tools/web-content/export_fidelity_catalog.py` 生成，输出 `tools/web-content/generated/fidelity_catalog.json`；本页是人工可读汇总。

基线：Android **1.9.3**（versionCode 507033），见 [FIDELITY_BASELINE.md](FIDELITY_BASELINE.md)。

## 分母

| 类别 | 数量 |
|---|---:|
| 职业 | 4 |
| 主动技能 | 51 |
| 被动技能 | 42 |
| **玩家技能合计** | **93** |
| 精通 | 318 |

## 当前网页执行方式（不是逐技能移植）

所有 93 个技能都有**真实的名字/描述/图标/标签/冷却/数值**，但服务端执行仍按 13 类原型：

| 原型 | 技能数 | 说明 |
|---|---:|---|
| `might` | 33 | 被动-进攻 |
| `rally` | 13 | 增益/持续 |
| `nova` | 13 | 范围/爆发 |
| `summon` | 8 | 召唤 |
| `projectile` | 8 | 弹体 |
| `warding` | 5 | 被动-防御 |
| `mobility` | 4 | 位移/加速 |
| `haste` | 3 | 被动-速度 |
| `strike` | 2 | 单体直接攻击 |
| `chain` | 1 | 链式 |
| `shield` | 1 | 护盾 |
| `leech` | 1 | 吸血 |
| `fortune` | 1 | 被动-掉落 |

`C04` 要为每个技能写出施法条件/目标/资源/冷却/等级曲线/效果时间线/Buff/弹体/召唤/精通改写；`C06` 要求生产技能池不再走原型回退。

## 逐职业技能清单（勾选用）

### Hunter（主动 14 / 被动 11）

| 主动技能 | id | 原型 | implementedBy | 精通数 |
|---|---:|---|---|---:|
| RapidFire | 31 | `rally` | RapidFire | 6 |
| ArrowRain | 29 | `nova` | ArrowRain | 6 |
| SummonWolf | 41 | `summon` | SummonWolf | 7 |
| FrozenArrow | 116 | `projectile` | FrozenArrow | 6 |
| PowerShot | 27 | `projectile` | PowerShot | 6 |
| ManaArrows | 100 | `projectile` | ManaArrows | 6 |
| Multishot | 32 | `projectile` | Multishot | 6 |
| Fade | 30 | `mobility` | Fade | 6 |
| ImpalingTrap | 73 | `strike` | ImpalingTrap | 6 |
| Backflip | 28 | `rally` | Backflip | 7 |
| MarkOfTheChosen | 75 | `rally` | MarkOfTheChosen | 6 |
| Thorns | 112 | `rally` | Thorns | 6 |
| BeastWithin | 114 | `rally` | BeastWithin | 6 |
| Mixology | 164 | `rally` | Mixology | 7 |

| 被动技能 | id | 原型 | implementedBy |
|---|---:|---|---|
| Precision | 33 | `might` | Precision |
| Sharpshooter | 34 | `might` | Sharpshooter |
| BeastTracking | 106 | `might` | BeastTracking |
| DoubleDraw | 36 | `might` | DoubleDraw |
| PoolOfMana | 102 | `might` | PoolOfMana |
| Concentrate | 108 | `might` | Concentrate |
| Cripple | 110 | `might` | Cripple |
| DeadlyPoison | 136 | `might` | DeadlyPoison |
| TastyTreats | 138 | `might` | TastyTreats |
| Solitary | 140 | `might` | Solitary |
| Fork | 142 | `might` | Fork |

### Mage（主动 12 / 被动 10）

| 主动技能 | id | 原型 | implementedBy | 精通数 |
|---|---:|---|---|---:|
| ChainLightning | 49 | `chain` | ChainLightning | 6 |
| Teleport | 130 | `rally` | Teleport | 6 |
| IceNova | 45 | `nova` | IceNova | 6 |
| ElementalSeal | 53 | `nova` | ElementalSeal | 6 |
| Blizzard | 48 | `nova` | Blizzard | 6 |
| ManaShield | 52 | `shield` | ManaShield | 6 |
| IceBlast | 46 | `projectile` | IceBlast | 6 |
| InfernalBlast | 47 | `projectile` | InfernalBlast | 6 |
| Thunderstrike | 51 | `nova` | Thunderstrike | 6 |
| Tornado | 77 | `projectile` | Tornado | 6 |
| PoisonCloud | 128 | `nova` | PoisonCloud | 6 |
| Mixology | 164 | `rally` | Mixology | 7 |

| 被动技能 | id | 原型 | implementedBy |
|---|---:|---|---|
| Hubris | 50 | `might` | Hubris |
| ManaFlow | 126 | `might` | ManaFlow |
| RapidCasting | 134 | `haste` | RapidCasting |
| AncientKnowledge | 118 | `might` | AncientKnowledge |
| BloodCasting | 120 | `haste` | BloodCasting |
| FireArmor | 54 | `warding` | FireArmor |
| ColdArmor | 55 | `warding` | ColdArmor |
| LightningArmor | 56 | `warding` | LightningArmor |
| EvasiveManeuver | 122 | `might` | EvasiveManeuver |
| ForceField | 124 | `might` | ForceField |

### Necromancer（主动 12 / 被动 10）

| 主动技能 | id | 原型 | implementedBy | 精通数 |
|---|---:|---|---|---:|
| SummonSkeleton | 213 | `summon` | SummonSkeleton | 6 |
| AstralWalk | 235 | `mobility` | AstralWalk | 6 |
| Shadowbolt | 221 | `projectile` | Shadowbolt | 7 |
| UnholyFocus | 219 | `mobility` | UnholyFocus | 7 |
| DrainLife | 225 | `summon` | DrainLife | 6 |
| SummonDemon | 215 | `summon` | SummonDemon | 6 |
| BoneLink | 233 | `summon` | BoneLink | 6 |
| Decay | 229 | `nova` | Decay | 6 |
| DemonicPresence | 227 | `summon` | DemonicPresence | 6 |
| Sacrifice | 231 | `summon` | Sacrifice | 6 |
| SummonDeath | 217 | `summon` | SummonDeath | 7 |
| Mixology | 164 | `rally` | Mixology | 7 |

| 被动技能 | id | 原型 | implementedBy |
|---|---:|---|---|
| Bodyguard | 245 | `might` | Bodyguard |
| AccumulatingShadows | 243 | `might` | AccumulatingShadows |
| Pillaging | 249 | `might` | Pillaging |
| BoneArmor | 247 | `warding` | BoneArmor |
| DarkMagic | 251 | `might` | DarkMagic |
| GiftOfTheVampire | 253 | `might` | GiftOfTheVampire |
| MasterSummoner | 255 | `might` | MasterSummoner |
| Reanimator | 257 | `might` | Reanimator |
| VileTouch | 259 | `might` | VileTouch |
| WandMaster | 263 | `might` | WandMaster |

### Warrior（主动 13 / 被动 11）

| 主动技能 | id | 原型 | implementedBy | 精通数 |
|---|---:|---|---|---:|
| Slam | 94 | `nova` | Slam | 7 |
| Might | 1 | `rally` | Might | 7 |
| Pounce | 71 | `nova` | Pounce | 6 |
| WarStomp | 11 | `nova` | WarStomp | 6 |
| Shatter | 19 | `strike` | Shatter | 7 |
| Sprint | 12 | `mobility` | Sprint | 6 |
| IncrementalFortress | 10 | `rally` | IncrementalFortress | 6 |
| Retaliation | 17 | `nova` | Retaliation | 6 |
| Frenzy | 67 | `rally` | Frenzy | 6 |
| Intimidate | 69 | `nova` | Intimidate | 6 |
| Hunger | 23 | `leech` | Hunger | 6 |
| Mixology | 164 | `rally` | Mixology | 7 |
| Whirlwind | 265 | `nova` | Whirlwind | 6 |

| 被动技能 | id | 原型 | implementedBy |
|---|---:|---|---|
| Overkill | 15 | `might` | Overkill |
| TreasureHunter | 18 | `fortune` | TreasureHunter |
| AxeSpecialization | 2 | `might` | AxeSpecialization |
| SwordSpecialization | 6 | `might` | SwordSpecialization |
| MaceSpecialization | 4 | `might` | MaceSpecialization |
| SpearSpecialization | 5 | `warding` | SpearSpecialization |
| DaggerSpecialization | 3 | `might` | None |
| CheatDeath | 13 | `might` | CheatDeath |
| BossHunter | 14 | `might` | BossHunter |
| Meditation | 20 | `haste` | Meditation |
| RepelMagic | 160 | `might` | RepelMagic |

## 状态登记（按 [清单] 模板）

每个技能条目需分别记录：原版证据 / 已提取数据 / 运行时接入 / 原版对照结果。当前整体状态：**已定位 + 已提取数据**，运行时接入仅到原型级别，**对照未通过**。

## 待补目录（B02 其余部分）

* 装备/词缀/套装：458 物品定义、18 词缀族、11 套装（`item_catalog.json`/`affix_catalog.json`/`set_catalog.json`）需扩到基线全量并标注来源/部位/等级限制。
* 怪物/精英/Boss：`Monsters.json` 136 条 + 34 `Brains` + Power，需列可获得性与缩放。
* 地图/世界：原版世界图结构、场景块、刷怪区（当前仅 1 张手工地牢）。
* 宠物/召唤/图腾、NPC、模式（Normal/Season/Hardcore 等）。

## 复现

```bash
.tools/web-assets-venv/bin/python tools/web-content/export_fidelity_catalog.py
```
