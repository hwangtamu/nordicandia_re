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

145 个技能方法中 **115 个**可恢复至少一个数值；分布：

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

## 效果语义（本次完善）

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

## 仍然 Provisional 的部分

* **效果归类**：把每个技能映射到 `strike/nova/rally` 是启发式；真实语义（链式、传送、召唤、持续引导）需要继续读方法体（已恢复的字段名如 `_DamageReductionPerJump`、`_NumMinions`、`AstralWalk_Movement_Speed_Bonus_Percent` 是起点）。
* 描述占位符的角色推断偶尔把「冻结持续」与「冷却」都归为 duration（例如 IceNova 的 `{2}/{3}`）。
* 未恢复：技能等级/精通数值（`PowerMasteries` 的 124 条属性修正已提取，公式未接）、法力系统运行、技能加点。

## 复现

```bash
# 需要 tmp/apk-libil2cpp.so、tmp/android-metadata.json、steam_analysis/dump.cs
.tools/web-assets-venv/bin/python tools/web-content/disasm_powers.py --all
.tools/web-assets-venv/bin/python tools/web-content/export_powers.py   # 合并进 powers.json / Powers.generated.cs
```
