# M3 状态 — 持久化与特殊流程（2026-10-02）

M3 目标：事务存档、断线恢复、技能槽扩展、Aesir 祝福、Niflheim 传送门。
通过条件：重登/服务重启一致；到期正确；网络重试不会复制道具或多扣款。

## 2026-10-04 Town / NPC 进度补充

- `world_town_scenes_all` 已经 `tools/web-content/export_town_scene.py` 解 static batch 子网格、层级变换，生成 `web/public/assets/kit/town/town_scene.glb`（510 个场景 mesh placements）及20个原版 `SpawnZone` 坐标。`export_assets.py` 会一并重建这两项 Town 资源。
- Town 中 Blacksmith、Disassembler、Merchant、SetItemMerchant、Offering/GiftStatue、PortalMaster、WorldPortal、TownPortal 已接到已有窗口/返回功能；`Petkeeper`/`BattlePetkeeper_1` 打开宠物列表，CombatPetkeeper 已接已恢复价格宠物的购买/选择。价格来自 Pet `OpalCost`/CombatPet `SilverCost`/`OpalCost` 词缀，不从名称推断。
- Niflheim 提前返回会放弃当前运行；最后的特殊怪物包清完后出现可交互出口与概率宝箱，使用出口才领取完成奖励并回到安全 Town。Town->Portal/World 使用目的地地图和安全落点，旅行重试按当前状态幂等。
- 验证：服务端 866 PASS、fidelity 58/58；web build、m2 smoke、npc-smoke 和 `git diff --check` 通过。StoreTests 覆盖 PetRow affix 价格、精确扣款、不足余额拒绝及宠物选择/进度重启持久化；另以 dev-session 实测 web API 成功购买普通/战斗宠物、精确扣 OP/SL，并对余额不足返回 409。原版动态 NPC、Petkeeper 入场生成的环境宠物、CombatPet 的战斗/复活生命周期、无价格普通宠物的获取渠道、Helheim、GuildDefense、Elder/Changer 对照仍未完成。

## 交付物

| 区域 | 内容 | 可信度 |
|---|---|---|
| 事务存档 | `GameStore` 单写者 `lock(gate)` + `world.json.tmp` 原子 `File.Move(...,true)`；命令记录 `world.json` 内一致提交。单机内测级别（未上 SQLite） | ClientVerified 结构 / 单机内测 |
| 断线恢复 | 权威快照 + 递增 `CombatVersion` + 命令 ID 幂等 + `expectedVersion` 边界；`MaxCatchUpSeconds=5` 防止长时间离线被当成实时战斗快进 | ClientVerified 命令契约 / 阈值 Provisional |
| 离线收益 | `GET/POST /characters/{id}/offline[/claim]`；`OfflineRewards`：`(秒/60) * killsPerMinute * 0.15 * 击杀经验`，12h（720min）上限、20000 击杀上限；服务端计算、幂等（第二次领取为 0） | 公式/上限 ClientVerified；killsPerMinute Provisional |
| 技能槽（6 主动/3 被动） | 已有：`GET/POST /loadout` + UI（`SkillSlotRules` 6/3） | ClientVerified |
| Aesir 祝福 | `GET/POST /characters/{id}/blessings`；`MakeOffering`（时长 ClientVerified）+ `Blessings` 效果表（类型/目标/幅度全部 ClientVerified，见下） | ClientVerified |
| Niflheim 传送门 | `GET /portal`、`POST /portal/enter`、`POST /portal/return`、`POST /portal/chest/open`；消耗 `NiflheimPortal`(159) 一次，按物品的 `NumMonsterPacks`(133) 设置包数，并以 `Area_Contains_More_Bosses`(2717) 增加末包怪物数；重试不重复扣道具；活动标记、出口和宝箱状态持久化 | 物品/世界类型/包数及末包数量 ClientVerified；其他缩放/物品池 Provisional |
| 客户端 UI | HUD `Offerings (O)`、`Portal (N)`；离线横幅“Claim”；祝福购买窗口；传送门窗口 | — |

## M3 出口条件对照

| 通过条件 | 状态 | 证据 |
|---|---|---|
| 重登/服务重启一致 | ✅ | `M3Tests`：传送门包数、当前包、怪物血量/稀有度、随机数状态、已清包数跨 GameStore 重建保留；`BlessingPersistenceTests` 已覆盖祝福时长重登不叠加 |
| 到期正确 | ✅ | `M3Tests` 祝福 10 分钟后失效；`BlessingPersistenceTests` 精确到 0.25s |
| 重试不复制道具/多扣款 | ✅ | `M3Tests`：传送门重试返回 `already_in_niflheim` 且道具仍只消耗一次；offline 二次领取为 0 |
| 技能槽扩展 | ✅ | 既有 `PowerPoolTests` + npc-smoke `3/6 skills · 1/3 masteries` |
| Niflheim 消耗/进入/返回一致 | ✅ | `M3Tests`：进入消耗、重试不消耗、返回离场、无道具拒绝、跨重启保留 |

## 验证

* 服务端 **388 PASS**（新增 M3 17 项）。
* `npm run build`、`npm run smoke`、`npm run npc-smoke` 全绿；npc-smoke 断言 `offerings: blessings=4 buttons=16`、`portal: No portal`。
* 局域网地址冒烟同样通过。

## 祝福效果（本轮从源码完全复原）

`WindowAesirOffering.CreateAesirBuffOffline`（Android `0x0255FAE8`）按 `offeringType` 选择目标属性，
统一写入常量 **0.4**（.so `0x1387368`）；`offeringSize` 只改时长（10m/30m/1h/4h）：

| 祝福 | 目标属性（`*_Final` id） | 幅度 |
|---|---|---:|
| Odin | Strength/Dexterity/Intelligence/Vitality_Bonus_Percent_Final（244/245/246/247） | +40% |
| Tyr | Movement_Speed_Bonus_Percent_Final（115） | +40% |
| Frigg | Item_Quantity_Bonus_Percent（367） | +40% |
| Thor | Weapon_Damage_Percent_Bonus_Final（89） | +40% |

`CharacterAttributeEngine` 之前缺这些 `*_Final` 名称→id 映射（`attribute_ids.json` 不含），已改为从
`attribute_formulas.json` 的恒等公式补齐，并把 `script == name` 当透传值处理；因此祝福会真正进入
属性合计（测试：Odin 使 Strength 15→21）。

## Niflheim 包数（本轮复原并接入）

`Affixes.json` 的 `NumMonsterPacks`（IntegerId 747）写属性 id **133**，按稀有度给 11–500 包
（稀有度 F 用默认 11–20；1→21-30、2→31-40、…）。网页商人把包数写进传送门物品，`EnterPortal` 用它
设置原生运行计数：其中 `TotalPacks−1` 次为战斗包，最后一次为出口事件；末次战斗包基线为 1 名怪物，原生 `SpawnMonster` 未强制 Boss。

## 已知限制 / Interim

* **事务存储**：仍是单进程 `world.json` + 原子替换，不是 SQLite；多进程并发写入不支持（见计划：单机内测可接受）。
* **断线恢复**：用快照 + 版本，而非计划里写的递增事件序号；网页端靠轮询 `/state` 对齐。
* **传送门 / Niflheim pack 布局**：按 `NiflheimPortalGameMode` + `GameWorld.GetRandomPackSize` 原生指令复原：`TotalPacks = max(Num_Monster_Packs, 2)`，其中最后一次计数是出口事件；普通包使用 `Rand.RangeExclusive(2, 5)` 并跨包携带小数余量，基线可有 2–5 只。最后战斗包从怪物池抽取 `1 + Area_Contains_More_Bosses` 名怪物，额外数量取自传送门物品属性 2717；原生 `SpawnMonster` 传 `canRollBoss=false`，不能称为必定 Boss。清包后生成可点击 TownPortal，20% 概率另生 Medium/Large 宝箱；宝箱基础掉落次数为 60–70/100–125，打开一次后持久化。使用 TownPortal 才原子结算 5000 silver、完成次数与回城。网页消耗门户与初始快照同一事务，跨进程重启恢复当前怪物、出口和宝箱状态。其他世界修饰符、真实刷怪体积、宝箱物品分布及怪物数值缩放仍待校准。详见 [恢复报告](NIFLHEIM_RECOVERY_2026-10-04.md)。
* **元素转换 / 穿透**：已接入 `GameCalculator.ApplyWeaponDamageConversion` + `InternalApplyDamageConversion`（转换总和 >1 归一化，源伤害按移动量扣减）与 `*_Resistance_Penetration_Total` / `Armor_Piercing_Percent_Total`（穿透降低目标抗性/护甲，负抗性提高伤害）。转换属性用 153-156 + 633-636，穿透应用为 Inferred。
* **物品数量取整**：已按 `DeathPayload.Apply` 复原为 `floor(remainder + Num_Items_Granted × (Item_Quantity_Final_Multiplier + 1))`，分数部分跨掉落累积（`PlayerItemQuantityRemainder`），封顶 `GameParameters.MaxQuantityFromMagicFindMultiplier = 5`。
* **怪物 AI / Brains.json**：已完全复原并接入 `Brains.json` 的加权动作树：`Game.AI.MonsterBrainCondition` 的 40 个条件名 + `StringHashHelper.HashNameSafe`（`export_brains.py` 导出 `brains_catalog.json`），网页 `BrainCatalog` 按条件筛选后加权抽取动作（`WeightedActionBrain` 语义），`CombatInstance.UpdateBrain` 每 0.5-1s 重选并执行：`DefaultAttackProxy`=追击攻击、`Wander`=游荡、`Flee`/`RunOutOfCombat`=逃跑；状态条件（`StateCombat`/`StateWander`/`StateRunOutOfCombat`/`NotOnFullLife`/`LessThan50PercentLife`/`IHaveNoMinions` 等）按网页可用状态求值。怪物原型已指定 gamedata 对应 brain（Bat/DemonOrc=`Standard`、SkeletonArcher=`StandardFleeingWhenClose`、CasterDemon=`StandardCurseSlow`）。怪物快照新增 `Action`。boss 专属技能已接入 `MonsterPowerCatalog`（效果形态来自客户端实现类的参数字段名，直接写在 ctor 的常量原样使用：`WolfKingRoar` 半径 3/伤害×3/移速+0.2/冷却 8s；`WolfKingSummonPack`=3、`BoneDragonSummonSkeleton`=1、`HelSummonPack`=8；`VileDragonNova` 半径 6）。`CombatInstance.ExecuteMonsterPower` 执行 Nova/NovaSequence/Charge/Beam/Summon/TripleStrike 六类效果（命中玩家或召唤；召唤延迟到怪物遍历结束后）。地下城 boss 现用 `Boss_WolfKing` brain，因此会施放这些技能。召唤现在使用 Power 对应的 gamedata 仆从名（`WolfKingMinion`/`BoneDragonSkeleton`/`HelMinion`/`KibuElemental`/`OdrTotem`，头像可解析）。诅咒已建模：`MonsterCurse*` 的目标属性已复原（Slow→Movement_Speed_Bonus_Percent_Final、LowerResistances→Resistance_All、AmplifyDamageTaken→Amplify_Damage_Taken_Percent、ReducedWeaponDamage→Weapon_Damage_Percent_Bonus_Final，冷却 16s），`CombatInstance` 在玩家上施加带时长的 debuff（移速/抗性/受伤放大/输出降低/吸血）并在命中计算中生效；Champion 怪物满足 `ImNotNormalMonster`，`ImAboveOrEqualToTier4` 网页视为 endgame。仍为近似：未直接写入 ctor 的乘数/半径为 Provisional（诅咒幅度/时长来自未复原的 buff 定义）；`MonsterCurseSlowProjectiles` 未单独建模弹速。
* **离线收益**：基础经验公式与常量同前。2026-10-03 直接解码 ELF 后确认 `killsPerMinute = truncate(Offline_Battle_Efficiency_Multiplier · clamp(1.5 · secondHighestReachedTier, 10, 45))`；属性为 **565**（=`Base_Stamina_Multiplier`），阶层来源为 `Character.GetSecondHighestReachedWorldCheckpoint`。现已接入：`CombatRegistry.OfflineRate` 从属性引擎取 `Offline_Battle_Efficiency_Multiplier`，并用角色当前世界阶层（属性 8，默认 1）作为 tier。**仍为近似**：网页不追踪“第二高已到达检查点”历史，tier 输入以当前阶层代替；击杀经验仍取网页 `CombatModel.ExperienceReward(level)`。详见 [恢复报告](LUCK_AND_ON_HIT_RECOVERY_2026-10-03.md)。
* **祝福 opal 价格**：已从 `LocalCatalog.cctor` 提取 Small/Medium/Large/ExtraLarge = **200/500/800/2500** opals（与 `OfflineCatalog.GetOpalPrice` 一致）。客户端源码明确标注为 "TODO-PRICE: seed values, production export pending"，因此这是客户端实际使用的 seed 值，但预计会在生产导出后变动。
