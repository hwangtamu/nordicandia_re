# Champion / Rare / Unique 生成概率与接线

2026-10-04；Android 1.9.3（507033）。这是 W01/W02 的已恢复子项，未宣称整个怪物系统完成。

## 原版证据

`MonsterManager.SpawnMonster @0x02A810AC` 的原生 ARM64 指令确认：

| 顺序 | 稀有度枚举 | 启用条件 | 本次条件概率（默认倍率 1） |
|---|---|---|---|
| 1 | Unique = 4 | level ≥ 50 **或** tier ≥ 2 | uniqueSpawnMult / 200（0.5%） |
| 2，前项未中 | Rare = 2 | level ≥ 25 **或** tier ≥ 2 | rareSpawnMult / 100（1%） |
| 3，前项未中 | Champion = 1 | level ≥ 10 **或** tier ≥ 2 | champSpawnMult / 50（2%） |
| 均未中 | Normal = 0 | — | — |

不是一次归一化权重抽样。全部条件满足、倍率均为 1 时，最终抽中概率是 Unique 0.5%、
Rare 0.995%、Champion 1.9701%、Normal 96.5349%。这是稀有度抽签概率；后续定义池为空还会导致本次不生成。
`CalculateChance` 沿用 `chance > 0 && Rand <= chance`，非正概率不消耗随机数。
倍率 0 的分母显式置 0，不发生一次正概率抽签。

`normalOnly` 或显式 `forceRarity` 跳过自动抽签；前者不清除已经指定的稀有度。
其后 `lastLevelAndFirstBossFight && canRollBoss` 直接选择 Boss=6。
**Champion=1，Unique=4**；此前模拟器把 4 注释成 Champion，现已更正。原有 rarity=4 的数值曲线保持不变。
Brain 的 Tier 4 条件属于另一层判断，不是这里的统一生成门槛。

`DungeonMonsterSpawnArea.SpawnMonsters @0x02988B70`：

* `0x02989644` 读取 GameAttributes 静态字段 `+0x88`：UniqueMonster_Find_Bonus_Percent，id 12。
* `0x029896D8` 读取 `+0x90`：ChampionMonster_Find_Bonus_Percent，id 164。
* `0x02989BE4/BE8` 两者加 1；`0x02989E18–E68` 传参：champ = 1 + 属性164、rare = 1、unique = 1 + 属性12。
* `SpawnMonster` 分母构造 `0x02A813D0–140C`，Unique 门槛/判定 `0x02A81B0C–1B6C`，
  Rare/Champion `0x02A820B8–2168`。Unique 的 `FCMP; CCMP ... LT; CSET GT` 表示等级或 Tier 条件成立。
* `<SpawnMonster>b__2 @0x02A83DD0` 检查 `AvailableRarities.Contains(rarity)`，再检查类型 `IsOfType`。

复现证据导出（固定二进制 SHA256，直接解码 ARM64；并非信任 Cpp2IL 的 FMOV 浮点猜测）：

```sh
.tools/web-assets-venv/bin/python tools/web-content/recover_monster_spawn.py
```

产物：[monster_spawn_recovery.json](../../tools/web-content/generated/monster_spawn_recovery.json)。
导出器检查常量、指令片段及字段名映射；分支含义是人工审阅结果，不是通用反编译器的自动证明。

## 网页实际接入

* `MonsterSpawnRules.RollRarity` 实现上述顺序、门槛、倍率、强制值及 Boss 分支。
* `CharacterRatings → CombatantStats → ApplyRatings` 保留属性 12/164，装备和属性更新可影响下一次生成。
* 世界怪物池保留每条定义的 AvailableRarities 和所属抽样类型；按世界类型权重选类型后，
  只选支持抽中稀有度的定义。没有候选返回空，不降级成 Normal、不混入仅支持 Boss 的定义。
* 初次生成与死亡重生均抽签；Rare/Unique/Champion 均满足 `ImNotNormalMonster`。
  显式手工测试原型保留旧行为；网页 Boss 仍由独立关底流程生成。
* 快照传输 `rarity`；网页实体在稀有度变化时重建，使用不同颜色的脚环区分。
  配色是网页呈现方案，未标为原版视觉校准。
* 空候选生成允许跳过；空场景会重试。门户最后一批延迟生成失败时仍能识别已清空的包，
  整包没有任何有效怪物时重试而不发通关奖励。这些重试策略是网页生命周期适配。

## 验证与仍未完成项

`MonsterSpawnRecoveryTests` 覆盖等级/Tier 边界、短路顺序、0/2 倍倍率、强制稀有度、
40 万次固定种子分布、定义资格、非 Boss Champion、属性更新后重生、快照、空场景/空包恢复，
以及真实存档角色 → 属性引擎 → CombatRegistry 世界池的端到端路径。

仍未完成：类型继承的完整 `IsOfType`、archer/caster/melee 等全部角色筛选与特殊刷怪路径、
同类型物种选择的完整校准、原版区域密度/刷新生命周期、Champion/Unique 附加属性及技能/掉落差异的整体对照。
当前类型内均匀选取，不把以上缺口标为 ClientVerified。这里的测试验证代码与已恢复规则，未替代原版实机分布对照。
