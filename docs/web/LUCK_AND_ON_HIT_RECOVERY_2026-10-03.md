# 魔法找到、毒与分叉校正（2026-10-03）

基于 Android `tmp/apk-libil2cpp.so` 的实际 ARM64 指令校正 `640bc88` 的实现。
Cpp2IL 的 annotated dump 在这些函数中错误解码了浮点立即数；不能仅依赖其文本数值。
可复现证据由 `tools/web-content/recover_luck_hit_rules.py` 导出到
`tools/web-content/generated/luck_hit_rules.json`，包含二进制与指令段 SHA-256、反汇编、常量和公式。
这是经人工核对的数据流证据报告，不是通用反编译器。

## 魔法找到

`ItemGenerator.InternalInitializeSetOrUniqueItemRarityTypes` @ `0x02CA0534`。
设 `p = 100 * magicFind`，`f = magicFindFactorMultiplier`（网页调用默认 1）：

```text
Unique = roundToEven(baseUnique * (1 + .01*p*225*f / (.5*p + 3*225*f)))
Set    = roundToEven(baseSet    * (1 + .01*p*150*f / (.6*p + 4.5*150*f)))
Normal = roundToEven(baseNormal / (1 + .01*p*f / (p + f)))
```

`magicFind=.3` 表示 +30%。基础权重为 Normal=10000000、Unique=500、Set=375。
网页对负 MF 取 0；f=0 时保留基础权重，避免原始有理式的 0/0。

关键证据：

* `0x02CA0BF4` 是 `fmov d9, #3`，不是旧报告的 2。
* `0x02CA0BE4` 是 `fmov d9, #4.5`，不是旧报告的 4。
* `0x02CA0DB8` 是 `fdiv d9, d0, d11`：Normal 权重除以增益因子。
* `0x02CA0CB8` 起处理小数部分和整数奇偶，最终权重采用中点取偶。

`ItemCatalog.RarityWeightsFor` 已按此实现，`RollRarityType` 使用其权重。
基础权重独立懒加载，修复“首次直接抽稀有度时尚未访问 Definitions，误用 1/0/0 权重”。

## 中毒

`HitPayload.Apply` @ `0x02C23F7C` 读取 `Poison_On_Hit`，否则按
`Poison_Chance_On_Hit` 判定；随后创建 `DebuffPoisoned`：

* `0x02C24ABC`：`Buff_Duration = 1` 秒。
* `0x02C24B08` 读取 `HitPayload.TotalDamage`（偏移 `0x20`）；常量 `0x1387678 = .2`。
* `Tick_Damage_Per_Second = TotalDamage * .2`。
* `DebuffPoisoned.DoWork` @ `0x02B2F480` 以 `DPS * deltaTime` 产生 Poison 类型伤害。

网页已替换“3 秒、主手毒伤的一半/秒”的近似，物理命中也可通过毒概率施毒。
毒伤经过目标毒抗性；最后一帧只计算剩余时长；死亡清空状态；不使用直接命中的最低 1 点伤害，
也不再次触发命中、暴击、中毒或分叉。保证施毒属性已接入 `CharacterRatings`。

完整 BuffManager 的替换/叠加规则仍未移植：网页目前保留最高 DPS 并刷新持续时间。
毒目标暴击翻倍仍沿用现有网页命中分类；原版完整 Power 类型过滤未在本轮恢复。

## 分叉与链

`ShootRangedProjectile.HandleForkAndChain` @ `0x02A031A4` 读取弹体的
`Power_Projectile_Fork_Chance` / `Power_Projectile_Chain_Chance`。网页的自动攻击概率来源为
角色的 `Projectile_Auto_Attacks_Fork_Chance` / `Projectile_Auto_Attacks_Chain_Chance`。

* 原版在 `0x02A0355C` 与 `0x02A035CC` 各创建一个弹体，总共 **2 个**。
* 两次调用的 `d4` 均为 **0.5**；子弹体的 canFork/canChain 均为 false。
* 两种判定均成功时分叉优先；链分支重定向原弹体，没有旧网页的 0.75 伤害折扣。

网页已接入两次半倍率次级命中，各自按目标护甲/抗性判定，允许施毒但不递归分叉/链。
仅装备弓/弩时启用这条自动攻击路径，换装立即更新。
**碰撞仍是近似**：网页选择主目标附近的敌人，尚未模拟原版两条散射运动轨迹；主动技能专属分叉、
完整弹体穿透/碰撞以及其他武器的自动攻击路径不由此实现保证。

## 离线 killsPerMinute 的额外发现

`WindowWelcomeBack.Start` 状态机：

* `0x02678C10` 调用 `Character.GetSecondHighestReachedWorldCheckpoint` @ `0x02B7D9C4`，取其首个 out 参数 tier。
* `0x02678CB8` 的静态字段偏移 `0x200` 对应 **Offline_Battle_Efficiency_Multiplier (565)**。
* `0x02678D1C` 和 `0x02678D24` 的立即数分别为 **1.5、10**；上限常量 `0x13872D8 = 45`。
* `0x02678D54` 的 `fcvtzs` 向零截断，然后写入 `_KillsPerMinute`。

```text
killsPerMinute = truncate(efficiency * clamp(1.5 * secondHighestReachedTier, 10, 45))
```

字段映射及公式已恢复；尚未把原版检查点/阶层进度映射到网页。因此运行时仍用 30 次/分钟，
不能将此项标为完全接入。

## Niflheim pack 布局（后续补充）

`NiflheimPortalGameMode.InternalStart` @ `0x02BD9xxx`：`TotalPacks = Math.Max(world.Attributes[Num_Monster_Packs(133)], 2)`。
`GameWorld.GetRandomPackSize(2, 4)` @ `0x02BDDB30`：`size = floor(_PackSizeRemainder + Rand.RangeExclusive(2*mult, 4*mult))`，
`mult` 来自 `Area_Pack_Size_Bonus_Percent_Final`（偏移 `0x2390`），余数进位到下一包。
包按清理顺序依次刷新，末包清完完成 run。网页已接入（`CombatInstance.packMode`），
仅“每包 0.1s 逐个生成/随机刷怪区”与 `Area_Contains_More_Bosses` 分支仍为近似。

`NiflheimPortalGameMode` 的 pack 布局仍未解决的旧说法作废。
“M3 仅剩两项”只描述该里程碑的范围，不代表整个复刻的其余机制都已完成。

## 验证与未完成项

服务端自定义套件 **420 PASS**（含四职业 fresh-boss）；新增检查覆盖 MF 权重首次加载、
7 组固定输入权重、200000 次固定种子抽样、毒伤/抗性/末段时长/死亡/未命中、分叉两次半伤、
次级目标独立减伤、无递归、近战禁用和实际装备切换。执行记录：`tmp/luck-hit-recovery/tests.log`。

```sh
.tools/web-assets-venv/bin/python tools/web-content/recover_luck_hit_rules.py
.tools/dotnet/dotnet run --project server/Nordicandia.StoreTests
```

物品数量仍使用 `1 + floor(itemQuantity)`、最多 5 次重抽，**其精确取整未恢复**。
此轮没有恢复完整元素转换/穿透、AI、Niflheim pack 布局，也没有部署线上服务。

## 后续补充（同日）

本轮修复之后又补齐了：

* **Niflheim pack 布局**：`NiflheimPortalGameMode` 的 `TotalPacks`、`GetRandomPackSize`、0.1s 逐个生成、刷怪区与顺序刷新已接入网页（详见 `M3_STATUS.md`）。
* **元素转换/穿透**：`ApplyWeaponDamageConversion` / `InternalApplyDamageConversion` 与 `*_Resistance_Penetration_Total` / `Armor_Piercing_Percent_Total` 已接入伤害管线。
* **物品数量取整**：`floor(remainder + Num_Items_Granted × (Item_Quantity_Final_Multiplier + 1))` 余数累积。
* **怪物 AI**：`Brains.json` 的完整加权动作树已接入——40 个 `MonsterBrainCondition` 条件名逐一对上 `StringHashHelper.HashNameSafe` 哈希（`tools/web-content/export_brains.py`），`BrainCatalog` 按条件筛选后加权抽取，`CombatInstance.UpdateBrain` 执行攻击/游荡/逃跑；原型指定对应 brain。boss 专属技能效果仍需各自 Power 逻辑。

服务端套件 **438 PASS**，smoke/npc-smoke 通过。
