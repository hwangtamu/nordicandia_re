# 原版数据补全：技能初始化与词缀类型

日期：2026-10-02；起点提交 `1250f56`。这是新增的提取成果与实现修正，不代表全游戏还原完成。

## 19 个空技能记录的处理结果

旧 `power_values.json` 中 19 个 `{}` 已逐一检查。新增机器可读补充文件
`tools/web-content/generated/power_parameter_recovery.json`，保留函数地址、指令、常量地址、函数与二进制 SHA-256。

- **11 个技能、20 个参数的等级公式**已恢复。旧脚本不支持等级运算、SIMD 双浮点写入及成对存储，是主要漏提原因。
- **4 个仅转发父类初始化**：UniqueItemFireballOnHit、UniqueItemThrowHammerOnHit → ProjectileSkill；Whirl → Power；KibuSmallLightningNova → Nova。不是缺少四份固定参数表。
- **4 个动态初始化**：Wander、ManaArrowsAbility、ShootExplodingFireArrow、Spitfire。记录随机调用、构造参数字段和角色属性来源，而不是填入虚构常量。

以下 `r` 为技能等级；数值是原始存储单位，尚未经过显示转换、精通或最终属性合成。

| 技能 | 参数与恢复公式 |
|---|---|
| TreasureHunter | MagicFindIncrease = `0.30 + 0.02*(r-1)`；ItemQuantityIncrease = `0.10 + 0.01*(r-1)` |
| FireArmor / ColdArmor / LightningArmor | ResistanceBonus = `0.50 + 0.10*(r-1)`；DamageTakenAsElement = `0.10 + 0.01*(r-1)` |
| RepelMagic | IncreasedMaximumResistances 与 IncreasedMaximumPhysReduction 均为 `0.03 + 0.001*(r-1)` |
| Pillaging | MinionMagicFindIncrease = `0.50 + 0.10*(r-1)`；AdditionalIronDropChance = `0.20 + 0.01*(r-1)` |
| MasterSummoner | MinionLifeBonus = `0.10 + 0.01*(r-1)`；MinionDamageBonus = `0.05 + 0.005*(r-1)` |
| VileTouch | MorePoisonDamage = `0.20 + 0.01*(r-1)`；PoisonChanceOnHit = `0.60 + 0.02*(r-1)` |
| Solitary | Power_More_Weapon_Damage_When_No_Wolf_Is_Active = `0.10 + 0.005*(r-1)`，origin=Power(3) |
| Fork | Projectile_Auto_Attacks_Fork_Chance = `0.10 + 0.005*(r-1)`，origin=Power(3) |
| DeadlyPoison | Poison_Chance_On_Hit = `min(1, 0.15 + 0.01*(r-1))`；Double_Damage_Chance_On_Crit_On_Poisoned_Target = `min(1, 0.80 + 0.01*(r-1))`，origin=Buff(4) |

动态参数的具体来源：

- Wander：Base_Cooldown 来自 `UnityEngine.Random.Range(int,int)`，参数 1、6。
- ManaArrowsAbility：Power_Num_Projectiles=1；MaxStackAmount/CurrentStackAmount 来自 `_NumArrowCharges`。武器倍率、箭矢数量、命中/击杀回蓝由构造参数提供，仍需沿创建调用方还原实际配置。
- ShootExplodingFireArrow：武器倍率读取角色的 `Unique_Auto_Attacks_Are_Fire_Arrows_That_Explode_Weapon_Damage_Percent`；弹速为默认基础移速的 10 倍；本技能穿透/分叉概率初始化为 0。
- Spitfire：武器倍率读取角色的 `Unique_Auto_Attacks_Is_Spitfire_Weapon_Damage_Percent_Per_Second`；OpenDelay 来自 GetActionSpeed 的虚调用。

提取器刻意限定为当前 APK 已审核的函数，函数字节变化就失败，要求重新审核。数值读取 Android ELF；字段和属性命名参考 desktop dump，输出保留原始偏移以供跨版本复核。输出仅覆盖这些函数自身的初始化，父类、构造函数、Buff 应用与施放逻辑仍可能追加参数。**新公式尚未接入网页战斗运行时**，因此保留 `runtimeIntegrated=false`，也不把动态公式塞进只容纳标量的旧 `power_values.json`。先前 126 个非空记录不因此自动升级为逐条验证完成。

复现：

```sh
.tools/web-assets-venv/bin/python tools/web-content/recover_power_parameters.py
.tools/web-assets-venv/bin/python -m unittest discover -s tools/web-content/tests -v
```

## 词缀生成与精华合并修正

### 纠正 AffixType 枚举

`steam_analysis/dump.cs` 中 `Game.Definitions.ItemAffixDefinition.AffixType`：

| 值 | 类型 |
|---:|---|
| -1 | Undefined |
| 0 | Prefix |
| 1 | Suffix |
| 2 | Implicit |
| 3 | Set |
| 4 | Unique |

独立原生证据：`ItemAffixDefinition.IsPrefixOrSuffix` @ **0x02CECFA8**：

```asm
ldr w8, [x0, #0x24]
cmp w8, #2
cset w0, cc   ; unsigned < 2：仅接受 0/1，-1 被排除
ret
```

旧网页实现将 1/2 误作前后缀，会拒绝 Prefix 并接受 Implicit。已统一修正 AffixCatalog、随机词缀候选与精华合并筛选。18 项精选目录中有 16 项是合法前后缀；其余 Implicit/Unique 不再作为随机词缀抽出。既有物品不会重写；旧的无类型记录保留原兼容行为。

### 修复重复候选消耗名额

`CreateItem` 原本循环目标次数，抽中重复名称直接 continue，导致 20,000 个种子中 1,169 次少生成词缀。现从剩余合法候选中选择，选择后移除，直到达到数量或候选耗尽。

原版 `GenerateRandomAffix` 接收 excludedAffixDefinitions；闭包 `<GenerateRandomAffix>b__1` @ **0x02CA8B74** 调用排除列表谓词并反转布尔结果，内层 `b__5` @ **0x02CA900C** 比较定义 Guid。与直接在完整列表中抽取后跳过重复的旧网页实现不同。

这是对精选目录生成器的修复，**不是原版完整词缀生成的等价实现**。现仍按精选名称去重、等概率选候选；原版 Guid/Group、TagData 权重、Domain、前后缀各自上限、Magic Find、Open_Prefix_Slot/Open_Suffix_Slot 尚需继续恢复。

随机流边界：这次保留 `1250f56` 的基础物品随机序列；新物品的词缀选择会变化。不声称兼容 `1250f56` 之前仍共用随机流的生成结果。

## 验证

- 服务端自定义套件：**318 PASS**，包含四职业 fresh-boss；执行记录 `tmp/affix-review/fix-tests.log`。
- 新增 20,000 种子检查：全部实际词缀数等于抽中数，无重复候选，无隐式/套装/独特词缀混入随机池，覆盖 0–6 条。
- 三个固定种子比较 `1250f56` 的定义与基础伤害，未改变。
- 实际 CraftEssenceItem 测试：Prefix/Suffix 可合并；Undefined/Implicit/Set/Unique 来源被拒绝；目标已有隐式词缀与无类型旧记录保持兼容。
- Python 5 项测试：SIMD 两个槽位、等级 1/11、概率上限、origin、继承与动态来源、完整重提取一致性，以及二进制版本拒绝保护。

## 仍需补全，按下一步价值排序

1. 完整词缀目录与选择管线：当前仅精选 18 项。Affixes.json 与 ItemAffixes.json 有同 Guid 不同数值记录，必须先确认数据版本/用途，不能直接拼接后称为完整目录。
2. 这些新技能公式的 Buff 应用及网页运行时接入；ManaArrowsAbility 构造调用方配置。
3. 元素转换/穿透完整执行顺序，DoT、生命偷取。
4. Brain 状态机、地图生成与刷怪配置执行方式。
5. CalculateIdleLevelsGained 离线收益；锻造占位槽、完整费用与祝福过滤。

19 个空记录已解释，不等于所有技能行为或所有原版数据提取完成。

## 服务端可读目录（后续接入）

`power_parameter_recovery.json` 已复制为服务端嵌入资源
`server/Nordicandia.Server/GameData/power_parameter_recovery.json`，并由
`Nordicandia.Server.WebApi.PowerParameterCatalog` 解析：

- `Powers` / `ForPower(name)`：19 个技能的字段与属性条目，含 `kind`/`rank1`/`perRank`/`capMax`。
- `Evaluate(parameter, rank)`：对 `linear_rank`/`constant` 求值（`rank1 + perRank*(rank-1)`，随后应用 `capMax`）。
- `EvaluatePower(name, rank)`：只返回标量条目；`call`/`field`/`user_attribute`/`expression`/`inherited`
  记录来源但不被填成虚构常量。

`PowerParameterTests` 断言 19 条全部存在、四组等级公式、概率上限与“非标量不填零”。
**这些参数仍未进入网页战斗结算**（`runtimeIntegrated=false`）；该目录只是让运行时可按名取值。

## 职业可选池（加载装基础）

新增 `tools/web-content/export_power_pools.py`，把 `CharacterClasses.json` 的职业池与 `powers_full.json`
连接，产出 `power_pools.json`（已嵌入服务端）。`PowerCatalog` 现暴露：

- `MaxActiveSkills=6`、`MaxPassiveSkills=3`（客户端 `SkillSlotRules` 的 6/3 上限）；
- `PoolFor(classId)` / `TryGetPooled(guid)`：每个可玩职业的完整主动/被动池（Warrior 13/11、Hunter 14/11、
  Mage 12/10、Necromancer 12/10），19 个恢复出的被动按职业分布（如 Warrior 有 TreasureHunter/RepelMagic，
  Hunter 有 DeadlyPoison/Fork/Solitary，Mage 有 Fire/Cold/LightningArmor，Necromancer 有 MasterSummoner/
  Pillaging/VileTouch）。

`PowerPoolTests` 断言 4 职业池均 ≥6 主动/≥3 被动、条目带 id/name、恢复被动可按名解析。
下一步是把当前固定的「3 主动 + 1 被动」套件改为从该池中选择的 6/3 装具，并让战斗使用所选被动。

`Affixes.json` 与 `ItemAffixes.json` 的取舍补充证据：Items.json/ItemTypes.json 的 `AffixIds` 共引用
1388/210 个 Guid，**全部存在于 `Affixes.json`**，仅子集在 `ItemAffixes.json`；例如 `FireResistance` 被引用的是
四个 GenerationType=4 的 Guid，而 gen=1 的那条未被任何物品类型引用。故当前精选目录（取自 `Affixes.json`）
与物品定义的引用一致；`ItemAffixes.json` 更像独立的/历史表。仍建议从 `GameWorld` 加载调用方做最终确认。
