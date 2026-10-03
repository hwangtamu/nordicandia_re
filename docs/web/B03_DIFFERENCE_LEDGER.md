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
| D02 | 伤害输入/执行 | Interim | `CombatModel.ResolveBundleAttack` | 结算顺序已按 `AttackPayload.Resolve` 修为 miss→dodge→block→crit，dodge/block 已接入（B04 样本）；公式 ClientVerified；`Inputs=Provisional`、`Execution=Inferred` | 属性来源/合成顺序与 evade 分离未全对照 | C01 |
| D03 | 生命曲线 | Provisional | `CombatModel.MaxHealth` | `150+50*level` | 客户端 `CalculateCombatAttributes` 未采样 | C01、C08 |
| D04 | 击杀经验 | Provisional | `CombatModel.ExperienceReward` | `8*level^1.35+5` | 客户端 `Monster.CalculateAttributes` 未采样 | W01、C01 |
| D05 | 怪物属性缩放 | Provisional | `CombatRegistry.MonsterProfiles` | 5 类通用原型倍率 | 原版怪物各自属性/缩放未接入 | W01 |
| D06 | 元素穿透应用 | Inferred | `CombatModel.ApplyPenetration`、`CharacterRatings` | 属性名 ClientVerified，应用顺序为推断 | 负抗性/免疫边界未对照；`IsEvaded` 与 `ChanceToHit` 的分离未验证 | C01 |
| D07 | Buff 生命周期 | Interim | `CombatInstance` 毒/诅咒 | 毒保留最高 DPS 并刷新；无唯一键/替换/驱散 | 叠加与强弱比较与原版不同 | C02 |
| D08 | 弹体碰撞 | Interim | `CombatInstance.SecondaryTargets` | 分叉/链用"附近敌人"代替运动弹体 | 散射轨迹/视线/穿透未模拟 | C03 |
| D09 | 诅咒幅度/时长 | Provisional | `MonsterPowerCatalog` 诅咒 | 目标属性 ClientVerified；幅度/时长为占位 | 诅咒强度与原版不同 | W02、W03 |
| D10 | Boss Power 数值 | Provisional | `MonsterPowerCatalog` | ctor 常量原样；其余乘数/半径/时长占位 | 部分技能伤害/范围不准 | W03 |
| D11 | 召唤物属性 | Provisional | `CombatInstance.SummonMinions` | 用 gamedata 仆从名，但属性用统一倍率 | 仆从生命/伤害继承未还原 | P01、W03 |
| D12 | AI 条件 | Inferred | `CombatInstance.UpdateBrain` | `ImAboveOrEqualToTier4` 恒真；`IHaveMinions` 按非 Boss 存活数近似 | 原版 tier/仇恨/状态迁移未完全对照 | W02 |
| D13 | 怪物行为分类 | Inferred | `MonsterProfile.Ranged/Brain` | 远程保持距离 + Brains 加权动作 | 寻路/碰撞/目标丢失未还原 | W02 |
| D14 | 宠物/图腾 | 未实现 | 无 | 仅召唤名字；`OdrTotem` 无充能/元素攻击 | 专属召唤/战斗宠物缺失 | P01–P04 |

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

## 特殊流程

| ID | 区域 | 状态 | 位置 | 现状 | 差异/影响 | 关闭 |
|---|---|---|---|---|---|---|
| D22 | 刷怪点 | Interim | `CombatInstance.SpawnZones` | 6 个环形点代替 `DungeonMonsterSpawnArea` 体积 | 刷怪位置分布不同 | W04、W06 |
| D23 | 传送门 boss 包 | 未实现 | `MonsterPowerCatalog`/`CombatInstance` | `Area_Contains_More_Bosses` 分支未接 | 特殊包行为缺失 | W06 |
| D24 | Niflheim 完成规则 | Inferred | `CombatInstance` pack 模式 | 清完即完成/返回 | 失败/返回/世界修饰符未还原 | W06 |
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
