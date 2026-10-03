# B03 差异台账

日期：2026-10-03。对应 [完整复刻任务清单](FULL_FIDELITY_BACKLOG.md) 的 **B03：建立差异台账**。
基线见 [FIDELITY_BASELINE.md](FIDELITY_BASELINE.md)。

规则：每条记录**状态**（Provisional 占位 / Interim 临时 / Inferred 推断）、**位置**（代码或文档）、
**现状**、**差异与影响**、**关闭任务**（清单 ID）。状态降级为已还原/对照通过前，不得对外声称一致。

状态口径：`Provisional` < `Inferred` < `已还原` < `对照通过`。

## 战斗与伤害

| ID | 区域 | 状态 | 位置 | 现状 | 差异/影响 | 关闭 |
|---|---|---|---|---|---|---|
| D01 | 技能执行 | Provisional | `PowerProfiles.generated.cs`、`CombatInstance` | 93 个玩家技能有真实名/描述/图标/冷却/数值，但执行是 13 类原型（见 [B02](B02_CONTENT_CATALOG.md)） | 具体施法/弹体/时间线/Buff 未移植 | C04–C06 |
| D02 | 伤害输入/执行 | Interim | `CombatModel.ResolveBundleAttack` | 结算顺序已按 `AttackPayload.Resolve` 修为 miss→dodge→block→crit，dodge/block 已接入（待 B04 样本对照）；公式 ClientVerified；`Inputs=Provisional`、`Execution=Inferred` | 属性来源/合成顺序与 evade 分离未全对照 | C01 |
| D03 | 生命曲线 | Provisional | `CombatModel.MaxHealth` | `150+50*level` | 客户端 `CalculateCombatAttributes` 未采样 | C01、C08 |
| D04 | 击杀经验 | Inferred | `CombatModel.ExperienceReward` | `8*level^1.35+5` | 客户端 `Monster.GetExperience` 已还原：`(level^(1.33*expMult)*0.1+16)*0.88*0.6`（几乎平坦，疑为分段/附加缩放，见 D04_MONSTER_SCALING）；未替换 | W01、C01 |
| D05 | 怪物属性缩放 | 公式已还原 / 运行时未接 | `MonsterScaling.cs`、`CombatRegistry.MonsterProfiles` | 客户端 8 条 `Monster.Get*` 曲线 + 常量池已还原（见 [D04_MONSTER_SCALING](D04_MONSTER_SCALING.md)），B04 样本 level10/60 对照通过；`expMult` 默认 1（Provisional）；稀有度系数 v531/v532 为 Inferred | 现有 archetype 仍用手工 `HpMult/OffenseMult/DefenseMult`，切到真实曲线需独立改动+平衡回归 | W01、D04 |
| D37 | 季节怪未接入 | 未实现 | `gamedata_decrypted/Monsters.json` | SpringMonster 12、FallMonster 5、ChristmasMonster 3 已提取（正式内容），生成条件/活动时间未提取 | 季节内容缺失 | W01 |
| D06 | 元素穿透应用 | Inferred | `CombatModel.ApplyPenetration`、`CharacterRatings` | 属性名 ClientVerified，应用顺序为推断 | 负抗性/免疫边界未对照；`IsEvaded` 与 `ChanceToHit` 的分离未验证 | C01 |
| D07 | Buff 生命周期 | 已还原 | `CombatInstance` 毒/诅咒 | 旧近似（毒最高 DPS+刷新、无唯一键）已移除；BuffManager 按 Stack/IsStrongerThan 反汇编规则执行 | C02 已对齐；跨来源毒精确交互待 S-BUFF-1（已推迟） | C02 |
| D08 | 弹体碰撞 | 已还原 | `CombatInstance.Projectiles` | 旧 `SecondaryTargets`（附近敌人代替）已删除；弹体真实飞行、圆形碰撞、已命中集合；分叉=2 支 0.5x 子弹 ±(45°–90°) 散布、子弹不递归；连锁=同一弹体转向 10 码内最近敌人、全伤害、成功一次后清除几率 | 速度 20y/s、碰撞半径 0.5y、射程 14y 暂定；散射单/双侧约定未完全确认 | C03 |
| D09 | 诅咒幅度/时长 | Provisional | `MonsterPowerCatalog` 诅咒 | 目标属性 ClientVerified；幅度/时长为占位 | 诅咒强度与原版不同 | W02、W03 |
| D10 | Boss Power 数值 | Provisional | `MonsterPowerCatalog` | ctor 常量原样；其余乘数/半径/时长占位 | 部分技能伤害/范围不准 | W03 |
| D11 | 召唤物属性 | Provisional | `CombatInstance.SummonMinions` | 用 gamedata 仆从名，但属性用统一倍率 | 仆从生命/伤害继承未还原 | P01、W03 |
| D12 | AI 条件 | Inferred | `CombatInstance.UpdateBrain` | `ImAboveOrEqualToTier4` 恒真；`IHaveMinions` 按非 Boss 存活数近似 | 原版 tier/仇恨/状态迁移未完全对照 | W02 |
| D13 | 怪物行为分类 | Inferred | `MonsterProfile.Ranged/Brain` | 远程保持距离 + Brains 加权动作 | 寻路/碰撞/目标丢失未还原 | W02 |
| D36 | 无 Brain 怪物 | 待核实 | `gamedata_decrypted/Monsters.json` | FireGolem、StoneGolem、CrystalGolem、HunterMirrorImageMinion 的 `BrainId=None`（34 个 brains 里无对应） | 需客户端行为佐证：Golem 系可能由 GolemTrigger 驱动 | W02、W03 |
| D14 | 宠物/图腾 | 未实现 | 无 | 数据源已定位：`Monsters.json` 内 CombatPet 5（Durnir/Eggther/Fossegrim/Hala/Hraesvelgr）+ Pet 8（BabyDragon/Crow/Dog/Eagle/Larva/Snake/SnowOxe/Turtle），见 [B02 怪物目录](B02_MONSTER_CATALOG.md)；`OdrTotem` 无充能/元素攻击；获取/价格/升级规则未提取 | 专属召唤/战斗宠物缺失 | P01–P04 |

## 掉落与经济

| ID | 区域 | 状态 | 位置 | 现状 | 差异/影响 | 关闭 |
|---|---|---|---|---|---|---|
| D15 | 掉落概率 | Provisional | `CombatInstance.TrashDropChance` | 固定概率 | 原版掉落资格/条件未接入 | E02 |
| D16 | 装备属性权重 | Provisional | `LootTable.StatsFor` / 每槽权重 | 简化公式 | 词缀/隐式/缩放未全量接入 | E01、E02 |
| D17 | 稀有度/词缀数 | 已还原 | `ItemCatalog`、`LootTable.NumAffixesWeights` | MF 饱和曲线/词缀数量已复原 | 边界分布未全对照 | E02 |
| D18 | 开槽容量 | Interim | `LootTable`/`AffixCatalog` | `2 + clamp(rarity/3,0,4)` 近似 | `Open_Prefix/Suffix_Slot` 占位符模型未接 | E03 |
| D19 | 分解材料量 | Provisional | `GameStore.DisassembleItems`、`InventoryService` | 固定产出 Iron | 客户端产出量未还原 | E04 |
| D20 | 祝福价格 | Interim | `Blessings.OpalCost` | 客户端 seed 200/500/800/2500（源码标注 TODO-PRICE） | 生产导出后会变 | E05 |
| D21 | 物品数量取整 | Inferred | `CombatRegistry.RollItemsPerDrop` | 余数进位已接入；`Num_Items_Granted` 等输入为近似 | 边界/上限未对照 | E02 |
| D33 | 掉落池物品缺口 | 未实现 | `GameData/item_catalog.json`（458） | 源 `Items.json` 608 条中有 48 条可掉落正式物品不在池内：戒指/项链/腰带/斗篷、5 件 `Unique_*`、`NiflheimPortal`、`Opal`、制作消耗品（Reinforcement/ReRoll 系列）。证据见 [B02 装备目录](B02_ITEM_CATALOG.md)，机读 `item_catalog_full.json`（`inCurrentCatalog=false`） | 这些物品在原版可掉落，网页永远掉不出 | E01、E02 |
| D35 | 未知类型词缀 | 待核实 | `gamedata_decrypted/Affixes.json` | 14 条 `GenerationType=None`（如 `CastSpeedPercent`、`LocalTwoHandBase*Damage`），看似正式词缀但缺类型标注 | 需反汇编确认默认语义后才能决定进哪个词缀池 | E01 |

## 特殊流程

| ID | 区域 | 状态 | 位置 | 现状 | 差异/影响 | 关闭 |
|---|---|---|---|---|---|---|
| D22 | 刷怪点 | Interim | `CombatInstance.SpawnZones` | 6 个环形点代替 `DungeonMonsterSpawnArea` 体积 | 刷怪位置分布不同 | W04、W06 |
| D23 | 传送门 boss 包 | 未实现 | `MonsterPowerCatalog`/`CombatInstance` | `Area_Contains_More_Bosses` 分支未接 | 特殊包行为缺失 | W06 |
| D24 | Niflheim 完成规则 | Inferred | `CombatInstance` pack 模式 | 清完即完成/返回 | 失败/返回/世界修饰符未还原 | W06 |
| D34 | 地图词缀未接入 | 未实现 | `gamedata_decrypted/Affixes.json` | 138 条授予属性 id ≥ 2700 的区域修饰符词缀已提取（含 20 个 `AreaMonster*`），机读 `affix_catalog_full.json`；世界与修饰符的关联规则未提取 | Niflheim/高阶世界的词缀玩法缺失 | W06 |
| D38 | 任务系统范围 | 范围确认 | `gamedata_decrypted/Quests.json` | 47 个"任务"全是教程触发器（`AllocateAttributes`、`BlacksmithClickMerge`…），此版本无剧情任务系统；66 条对话全挂 Tutorial（4 条 `_UNUSED` 已标废弃）。证据见 [B02 NPC 目录](B02_NPC_CATALOG.md) | 非差异：W05 不安排剧情任务复刻；教程触发器是否复刻待定 | W05 |
| D25 | 离线 tier | Interim | `CombatRegistry.OfflineRate` | 用当前世界阶层代替 `GetSecondHighestReachedWorldCheckpoint` | 离线速率偏离 | W07 |
| D26 | 等级需求宽容带 | Provisional | `CombatRegistry.LevelRequirementGrace=20` | 固定 20 | 客户端精确规则未还原 | C08、E01 |

## 基础与表现

| ID | 区域 | 状态 | 位置 | 现状 | 差异/影响 | 关闭 |
|---|---|---|---|---|---|---|
| D27 | 持久化 | Interim | `GameStore` | 单进程原子 JSON | 无事务/多进程/迁移 | S01 |
| D28 | 网页起始属性 | Provisional | `WebApiEndpoints.Starter*` | 固定 60/40/12 | 与客户端建号不同 | C08 |
| D29 | 属性脚本映射 | Inferred | `PowerParameterCatalog`、`DATA_RECOVERY` | passive → 客户端属性的映射部分按字段名推断 | 少数被动加成可能偏差 | C01、C06 |
| D30 | 美术 | 已豁免范围 | `web/public/assets/avatars` | 3D 环境 + 2D 头像 token | 按 [M0 审计](M0_ASSET_AUDIT.md) 不补骨骼角色；材质/特效/声音待恢复 | V01–V03 |
| D31 | 地图 | 未实现 | `CombatInstance` 单场景 | 1 张手工地牢 | 世界图/场景块/传送未还原 | W04、W05 |
| D32 | 背包交互 | Interim | `web/src/main.ts` 平铺列表 | 无格子/拖拽/堆叠/排序 | UI 与原版差距 | U01 |

## 失效结论清理

* `docs/web/DATA_RECOVERY_2026-10-02.md` 中魔法找到 `*(1+MF)` 的 **Interim** 说明已被
  [LUCK_AND_ON_HIT_RECOVERY](LUCK_AND_ON_HIT_RECOVERY_2026-10-03.md) 的饱和曲线取代。
* `docs/web/DATA_RECOVERY_2026-10-02.md` 中"毒 = 3 秒、主手毒伤一半/秒"已被
  HitPayload 的 1 秒 / 命中总伤害 20% 取代。
* `docs/web/GAME_MECHANICS.md` 中"元素抗性与穿透未还原"已被 D06 的（Inferred 应用）取代。
* `docs/web/GAME_MECHANICS.md` / `M2_STATUS.md` 的"技能效果归类 Provisional"仍成立，对应 D01。
* 旧计划的 M4 手机顺序暂缓，见 [FULL_FIDELITY_BACKLOG](FULL_FIDELITY_BACKLOG.md) 与 S04。

## 维护

* 新增或替换差异时在此登记，并回链代码与证据；关闭时改为"对照通过"并注明样本路径（B04–B05）。
* 本台账是 B03 的起始版本，后续按 B01 版本绑定更新。

| D39 | SetLoadout 后技能冷却数组越界 | 已修复 | `server/Nordicandia.Simulation/CombatInstance.cs` `UpdatePowers` | `UpdatePowers` 更换技能列表后 `skillCooldowns` 长度未同步，`Snapshot()` 按新技能数索引旧数组越界崩溃。已修：按槽位保留已有冷却重建数组。C05 `relogin` 测试暴露 | 对照通过：`C05SkillTests.Relogin` |
| D40 | PoisonCloud 被折叠为瞬时 nova | 已修复 | `tools/web-content/generated/power_values.json`（`Power_Duration`=6.0、`Power_Slow_Effect_Percent`=0.2）、`server/Nordicandia.Simulation/CombatInstance.C05.cs` | 原型曾把 PoisonCloud 当 instant nova（3y 半径一次性 0.9x），丢失持续 6s 毒云 + 20% 减速。C05 实现 `PoisonCloud` 实体：1s tick 0.9x 毒伤 + slow debuff（2s，云内刷新）；怪物移动改用 `EffectiveMonsterSpeed` 吃减速 | 对照通过：`C05SkillTests.PoisonCloud`（7 条）；C04 反静默清单 29→27 |
| D41 | SummonSkeleton 无仆从实体 | 已修复（首技能） | `tools/web-content/generated/power_values.json`（`Minion_Inheritance_*`）、`server/Nordicandia.Simulation/CombatInstance.C05.cs` | summon 原型只给 offense buff，不生成仆从。C05 对 SummonSkeleton 生成 3 只 `PlayerMinion` 实体（伤害/生命/攻速按继承属性，Provisional 公式），寻敌近战、转火。怪物 AI 暂不以仆从为目标；其余 7 个召唤技能仍走旧路径（C06） | 对照通过：`C05SkillTests.SummonSkeleton`（4 条） |

| D42 | ImpalingTrap 被折叠为直接 strike | 已修复 | `tools/web-content/generated/power_values.json`（`Power_Duration`=5.0）、`server/Nordicandia.Simulation/CombatInstance.C06.cs` | 原型把陷阱当单体直伤。C06 实现 `Trap` 实体：放置于目标处，5s 内怪物近距离触发 7.5x（单次消耗），未触发消散 | 对照通过：`C06SkillTests.ImpalingTrap`（4 条） |
| D43 | Teleport 被误分类为 rally | 已修复 | `server/Nordicandia.Simulation/CombatInstance.C06.cs` | rally 分支只给 offense buff。C06 修正为位移：远离最近威胁 blink 10y（C04 可疑映射关闭其一） | 对照通过：`C06SkillTests.Teleport`（2 条） |
| D44 | Whirlwind 被折叠为瞬时 nova | 已修复 | `tools/web-content/generated/power_values.json`（`Power_Duration`=5.0、`Power_Whirlwind_Damage_Frequency`=0.2）、`CombatInstance.C06.cs` | 原型为一次性 0.4x。C06 实现 `Channel`：5s 引导，0.2s tick，0.4x，2y 半径 | 对照通过：`C06SkillTests.Whirlwind`（4 条） |
| D45 | MarkOfTheChosen 被折叠为 rally buff | 已修复 | `power_values.json`（`Power_Mark_Of_The_Chosen_Amplify_Damage_Taken_Percent`=0.1、`Power_Duration`=15.0）、`CombatInstance.C06.cs` | C06 实现怪物 debuff `mark`，`ResolveSkill` 经 C01 易伤管线（`DamageTakenAmplifyPercent`）放大承伤 10%，15s | 对照通过：`C06SkillTests.MarkOfTheChosen`（3 条） |
| D46 | Thorns 被折叠为 rally buff | 已修复 | `power_values.json`（`Reflect_Melee_Damage_Percent`=2.0、`Buff_Duration`=10.0、`Base_Physical_Damage_Reduction_Bonus`=0.1）、`CombatInstance.C06.cs` | C06 实现玩家 buff：`ApplyPlayerDamage` 接 10% 物理减伤 + 200% 近战反射（`damage` 事件 `thorns reflect`） | 对照通过：`C06SkillTests.Thorns`（4 条） |
| D47 | BuffManager Source 键读取遗漏 | 已修复 | `server/Nordicandia.Simulation/CombatInstance.cs`、`CombatInstance.C05.cs` | `MagnitudeOf(id)` 默认 `source=""`，带 Source 的 debuff（slow/mark/thorns）读不到，导致减速/易伤/反射失效。已修三处调用显式传 Source | 对照通过：C06 回归（555 PASS） |

| D48 | Blizzard/ElementalSeal 被折叠为瞬时 nova | 已修复 | `power_values.json`、`CombatInstance.C05.cs`（`SpawnGroundEffect` 泛化） | C06 复用云实体：Blizzard 4s/4y/1.0x Cold；ElementalSeal 10s/2.5y/0.25x（玩家站内 +30% 武器伤待后续） | 对照通过：`C06Batch2Tests`（10 条） |
| D49 | FrozenArrow 缺爆炸 AoE | 已修复 | `power_values.json`（`Base_Power_Radius`=4.0） | C06：1.4x 命中后 4y 爆炸（主目标除外，`projectile explode` 事件） | 对照通过：`C06Batch2Tests.FrozenArrow`（4 条） |
| D50 | Tornado 被折叠为普通弹体 | 已修复 | `power_values.json`（`Base_Power_Radius`=2.0、`Power_Duration`=7.0、`Power_Projectile_Pierce_Chance`=1.0） | C06：2y 胖弹体 + 100% 穿透 + 2.3x（7s 游荡未模拟） | 对照通过：`C06Batch2Tests.Tornado`（3 条） |
| D51 | Slam 被折叠为圆形 nova | 已修复 | `power_values.json`（`Slam_Max_Distance`=15.0） | C06：`HitLine` 15y 直线/1y 半宽判定，替代圆形 | 对照通过：`C06Batch2Tests.Slam`（3 条） |

| D52 | Retaliation/Intimidate/Fade 机制缺失 | 已修复 | `power_values.json`、`CombatInstance.C06b.cs` | Retaliation：12s 反击 buff；Intimidate：6y 内敌人 -10% 伤害；Fade：+100% 闪避 | 对照通过：`C06Batch3Tests` |
| D53 | ManaShield 吸收系数缺失 | 已修复 | `power_values.json`（`Mana_Shield_Damage_Absorbtion_Factor`=0.2） | 20% 伤害转护盾（暂定） | 对照通过：`C06Batch3Tests.ManaShield` |
| D54 | IceBlast/Shadowbolt/InfernalBlast 缺爆炸 | 已修复 | `power_values.json` | 爆炸弹体家族：15y/2.5y/3y 爆炸；InfernalBlast 加 4s 燃烧 | 对照通过：`C06Batch3Tests` |
| D55 | Decay/UnholyFocus/DrainLife 等死灵技能机制缺失 | 已修复 | `power_values.json`、`CombatInstance.C06b.cs` | Decay：8s AoE + 攻速减速；DrainLife：6s 引导吸血；余下见 C06 文档（部分暂定） | 对照通过：`C06Batch3Tests` |
| D56 | ManaArrows/Backflip/AstralWalk 机制缺失 | 已修复 | `power_values.json` | 充能系统（8 发）；镜像仆从；移速 buff | 对照通过：`C06Batch3Tests` |
| D57 | Thunderstrike 无伤害数据 | 部分修复 | `power_values.json`（仅时长/半径） | 4s 风暴结构 + 暂定 1.0x（需客户端研究实际伤害） | 对照通过：`C06Batch3Tests.Thunderstrike`（结构） |
