# STATUS 代码与进度核对（2026-10-03）

核对提交：`31faaa3fedf5da5797bd7a72152e9617d2a6e918`，本地 `main`，开始时工作区干净。
入口为 [STATUS.md](STATUS.md)（仓库实际文件名为大写），验收要求沿用 [完整清单](FULL_FIDELITY_BACKLOG.md)。
本次核对当前工作区，没有再次拉取远端；修改仅为本报告和进度总表，没有修改游戏代码或部署。

## 结论

**F01 修复有效，测试数字可复现；STATUS 对多项工作的完成程度仍有高估。**
前版 11 个 ✅ 的任务均有未完成范围，现改为 ⏳，保留已经实现的子项。
P01、S02 已有原型基础，应为部分完成，不能全部写“未开始”。
这是代码/接线/回归核对，不是全量原版实机行为验收；不据此给出百分比还原度。

## 本次验证

| 项目 | 实际结果 | 能证明的范围 |
|---|---|---|
| StoreTests | **695 个 PASS 行，exit 0** | 已有断言通过，其中包含下列 58 个回放样本 |
| FidelityReplay | **58/58** | 公式与本地场景回归，不是 58 个原版实机技能对照 |
| `npm run build` | 通过 | TypeScript/Vite；仍有大 bundle 提示 |
| `npm run smoke` | 通过 | 已有登录/战斗/装备/精通/重登流程 |
| `npm run npc-smoke` | 通过 | 已有 NPC/属性/祝福/传送门/装配窗口路径 |
| 原 B01–C07 探针重跑 | F01 字段正确保留；F02/F04/F05/F06 仍可复现 | 实例与有效技能路径 |
| 新增世界池探针 | Boss 进入普通池、世界 Boss 未切换 | 反射调用当前实际生成入口，并实例化怪物 |
| 浏览器模块探针 | 新快照单位不创建实体；种族/怪物仍回退头像 | 在 Chromium 执行当前 `World.pushState`/头像函数；不是原版截图对照 |
| 目录/资源核对 | 93 规格、29 gaps、93 未恢复等级曲线；97 随机词缀、1242 属性 ID；138 头像引用文件存在 | 检查当前产物与引用，没有本轮全量重新反汇编或重新导出 |

日志、探针源码和截图：`tmp/status-audit/`。测试服务使用该目录下的独立存档，验收后停止。

## 主要问题（按影响排序）

### R01 · P1 · C07：精通和洗点仍不能关闭验收

代码：`server/Nordicandia.Server/WebApi/CombatRegistry.cs` 的 `EffectivePowers`（约 577 行），
`Services/CharacterPowerService.cs` 的 `ResetSkillMastery`（35 行）。

* Might 的精通令 `Strength_Bonus_Percent` 从 **0.75 → 0.78**，实际 offense Buff 仍为 **0.75**。
  `EffectivePowers` 改了 Values/Multiplier/Cooldown/Radius/ManaCost，却没有刷新 BuffBonus/BuffSeconds。
* `Multiply` 仍通过 `cur + delta` 应用；倍率为 0 的冷却精通没有乘掉冷却，探针仍为 **30 秒**。
* 297 个精通中 **173 个 Specs 为空**。空 Specs 不能作为“原版无效果”的证据，需逐项恢复行为。
* 单技能洗点仍清全部精通；费用接受请求值，余额/扣款/清点不在同一事务；WebApi 没有洗点流程。

对应此前 F02/F07，未修复。

### R02 · P1 · C02/C03：毒、换图和弹体仍有运行时差异

代码：`Simulation/BuffManager.cs:59`、`CombatInstance.cs:1310`、`CombatInstance.Projectiles.cs:72`。

* 毒仍共用“剩余时长、层数”的比较，未实现原版 `DebuffPoisoned.IsStrongerThan` override。
  本地容器中旧毒 DPS=100/剩余 0.5 秒会被新毒 DPS=1/1 秒替换；原版完整比较方向仍需恢复。
* `SetWorld` 后 **1 个 cloud / 3 个 skeleton** 仍保留，换图清理并未贯通。
* `PowerShot` 在任何 `Advance` 之前已对 5 码外目标造成 **693.045781** 伤害；弹体在施法内部同步跑完。
  地图已有墙体，但 `FirstProjectileCollision` 仍只检查怪物，不检查墙。
* `InfernalBlast` 创建 poison 类型的 burn，tick 仍取 Poison 抗性（`CombatInstance.cs:1248/1600`）。

对应 F05/F06/F08。容器/几何算法存在不等于生命周期和飞行时间正确。

### R03 · P1 · C05/V03：新效果没有送到网页

代码：`CombatInstance.Effects.cs:22`、`CombatInstance.cs:1765`、`Server/WebApi/WebDtos.cs:27`。

`DrainEvents` 仍没有生产 WebApi 调用者。CombatSnapshot/WebCombatState 没有 Events、Clouds、
PlayerMinions、Projectiles。网页飘字来自相邻快照的 HP 差值，不是消费这套战斗事件。
因此不能称四技能“服务端 → 前端表现全链路贯通”。对应 F03。

### R04 · P1 · V02/W03/W06：动态生成的单位不会创建渲染实体

代码：`web/src/game.ts:339` 的 `buildMonsters` 仅在初始化调用；`pushState:414` 对未知 index 直接 `continue`。

Chromium 中调用实际 `World.prototype.pushState`，给空渲染实体集合送入含 `Boss_WolfKing` 的快照：
快照保留该怪物，**renderEntities 仍为 0**。初始快照不包含后来才出现的 Boss；逐个生成的新 pack 单位、
Boss 召唤物也有相同路径。索引被复用为另一怪物时不会更新头像；从快照消失的旧单位也没有统一删除流程。

这不被当前 smoke 捕获；应按快照做新增、更新、移除，并验证实体身份/重用和换图行为。
上述探针验证的是真实同步方法的逻辑，并未声称完成所有场景的可视化复测。

### R05 · P1 · W01/W03/W06：真实目录接入时丢失了刷怪规则

代码：`Server/WebApi/CombatRegistry.cs:91`、`MonsterCatalog.cs:77`、`Simulation/CombatInstance.cs:499/545`。

* `ProfilesForWorldTier` 只遍历 `SpawnWeights.Keys`，忽略权重值；生成时使用 `profiles[index % length]`。
  tier 2 的数据权重是 **Orc=1000、EvilChest=1**，当前数组却是 **10 个 Orc + 1 个 EvilChest**，
  并按索引轮换，不是按 1000:1 抽取。普通波次只使用有限索引还会进一步偏置分布。
* `ByType` 只要求 `AvailableRarities.Count > 0`，没有按生成稀有度筛选。
  tier 1 的 `Boss_WolfKing`、tier 2 的 `Boss_TheWarchief`、tier 3 的 `Boss_Bolomahl/Boss_Kibu`
  进入普通池，探针生成结果为 **rarity=0 / IsBoss=false / Champion=false**，却带 Boss Brain。
* `Caster`、允许稀有度等目录字段未传入 profile，真实池全部沿用默认速度/稀有度等参数。
* 关底 `CreateBoss` 固定 **Frostbound Jarl / Boss_WolfKing**。tier 2 的 Boss 应引用 `Boss_TheWarchief`，
  tier 3 应引用 `Boss_Bolomahl`，当前两者都仍用狼王。
* `MonsterScaling` 的经验/ForceField/抗性曲线或常量存在，不代表实际使用：
  击杀仍调用 `CombatModel.ExperienceReward = 8*L^1.35+5`；普通池抗性默认 0、Boss 抗性手写；
  ForceField 未进入怪物模型，攻击间隔仍为固定值，spawn 方差用均值。
  恢复曲线与最终原版属性链的关系尚需确认，不能仅把所有导出值直接替换进去就称完成。

### R06 · P1 · C06：部分召唤仍是原型增益

实际施法结果：

| 技能 | cast | 仆从数 | 玩家攻击增益 |
|---|---|---:|---:|
| SummonWolf | true | 0 | 0.15 |
| SummonDemon | true | 0 | 0.15 |
| SummonDeath | true | 0 | 0.15 |
| SummonSkeleton | true | 3 | 0 |

前三者仍进入 `CombatInstance.Effects.cs:166` 的占位实现。对应 F04。
“51 技能 gaps 清零”也和当前规格产物的 **29 行 gaps** 不一致；93 是职业池行数，去重后 90 个技能。
规格存在旧标记，不能把 29 直接当成精确的未实现技能数；应逐项与实际行为同步。

### R07 · P2 · V01：资源文件齐了，网页引用尚未齐

* manifest 有 **13 个 kit 条目，但只有 10 个有网格**；dungeon/town/tutorial 为空。
* 独立核对 `Monsters.Image` 和 `CharacterRaces.ActorImage`：**138 个引用对应文件均存在**，320 个头像已导出。
  该“0 缺失”只覆盖这两个字段，不能扩展为全游戏材质、技能 VFX、音频等全部齐备。
* 实际网页 `content.json` 仍记载 197 个头像；136 怪物只有 **8 个非空 portrait**；Wood Elf/Satyr icon=null。
  浏览器实际函数返回两者头像为 `RaceHuman.png`，GrayWolf 等返回通用随机映射头像。
* 即使重新运行 `export_content.py`，仍需修两个入口：它未使用导出器的文件名清洗规则
  （`Human_02_nobg 1` 已导出为 `Human_02_nobg_1.png`）；`monsterIcon` 只接受
  `monsterIcons` 中的名字，而该清单仅收录 `MonstersAvatarIcons_*`，会拒绝许多真实怪物头像。
* 前端场景仍固定加载 `dungeon_default` kit；原进度表将主题切换标待办是准确的。

### R08 · P2 · 状态/验收证据需要明确范围

* F01 字段传递修复已由旧探针确认，但新增 F01RatingsTests 主要验证字段保存/快照，
  未独立覆盖装备/被动改变 → CharacterRatings → 实际命中的完整链路。
* B05 报告只输出 actual，缺 expected 与原样本证据等级。现有“重登/幂等”小样本不等于真实 API 路径。
* E01 随机词缀子集数据成立：97 条，其中 Item=56、Monster=27、Area=14；属性表 1242。
  原任务是完整物品/词缀目录，完整可获得性与 B02/E03 的覆盖不能由这 97 条代表。
* C08 新建角色共用 `State/CharacterBaseline.generated.cs`，`GameStore.CreateCharacter` 不按种族选择基础属性。
  种族目录存在与种族效果进入角色是两件事。
* P01 有 C05 骷髅实体/AI 原型；S02 有会话、归属、命令 ID/版本检查基础，应标部分完成。
  原生服务中的宠物/赛季等接口不能直接当作网页 P/O 批次完成。

## 全表核对归纳

| 范围 | 核对判定 |
|---|---|
| B01–B04 | 保留部分完成；版本/目录/证据框架存在，台账和原版实机样本尚不齐 |
| B05–B06 | 改部分完成；报告与公共效果基础已交付，报告内容和占位召唤/事件链路仍缺 |
| C01–C07 | 均改部分完成；F01 可关闭，其余上述运行时缺口仍开着 |
| C08 | 保留部分完成，补种族初始化与操作规则未验收范围 |
| E01 | 改部分完成，明确随机词缀子集完成；全目录验收尚不齐 |
| E02–E05 | 保留部分完成；MF/类型权重/开放槽/制作计价/商店来源等待办合理，本轮未逐条重新反汇编 |
| W01 | 改部分完成，增加权重/资格/属性接线缺口 |
| W02–W07 | 保留部分完成；碰撞/A*、阶段条件、地图/世界池有代码；Boss 接线、checkpoint 与原版布局仍不齐 |
| P01 | 改部分完成；P02–P04 保留网页专项未开始 |
| V01–V03、U01–U03 | 保留部分完成；补资源引用、动态实体、效果事件的实际缺口；UI smoke 不代表完整交互验收 |
| O01–O04、S01、S03–S04、A01 | 保留未开始/暂缓；未发现足以关闭网页专项的实现与验收证据 |
| S02 | 改部分完成，保留现有认证/命令基础，稳定性专项仍待办 |

W_P_BATCH_STATUS、E01 子文档及 FULL_FIDELITY_BACKLOG 的部分历史描述仍比代码旧，
包括已接入的寻路、阶段条件和 TagData 权重；以本轮总表为当前入口，不能叠加旧文档的测试数。

## 建议下一步

先修 **C07 精通/洗点 → C02/C03 生命周期与时间推进 → 前端实体/事件链路 → 世界刷怪资格与 Boss → 头像引用**。
这些是实际玩家可观察的错误。原表列出的 E02 MF、W05/W07 tier/checkpoint、宠物与声音工作仍应继续，
但不能绕过这些断点后宣称此前系统已完整复刻。
