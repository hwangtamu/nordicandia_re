# Niflheim 机制恢复（2026-10-04）

基线：Android 1.9.3（507033），`libil2cpp.so` SHA-256 `529bd257af0cd6e953fb50f51a69857f42f04eb70acab0586f8413d1f97afd1d`。可重跑的指令核对在 `tools/web-content/recover_niflheim.py`，输出 `tools/web-content/generated/niflheim_recovery.json`。

## 已核对的原生规则

| 规则 | 证据 | 网页实现 |
|---|---|---|
| 总包数至少 2 | `NiflheimPortalGameMode.InternalStart` 读取 `Num_Monster_Packs`（属性 133），取与 2 的最大值 | 物品上的包数决定本轮 `TotalPacks` |
| 每包基线大小 | `CheckIfPackShouldBeSpawned` 在 `0x02BDAC14–0x02BDAC20` 传 `d0=2, d1=5` 调用 `GameWorld.GetRandomPackSize`；后者调用 `RangeExclusive`、向下取整并把小数余量存回 `GameWorld+0x230` | 均匀取 `[2,5)`，余量跨包保留；因此单包可出现 2–5 只 |
| 完成银币 | `AddSilverForFinish.MoveNext` 在 `0x02BDC7F0` 写入 `0x1388`（5000）并调用 `GainSilver` | 完成时 5000 silver |
| 最后战斗包 | 剩余计数为 2 时，包成员数改为 `1 + Area_Contains_More_Bosses`；实际 `SpawnMonster` 调用传 `canRollBoss=false`，未强制 Boss 稀有度 | 从消耗的传送门物品读取属性 2717，生成 `1 + 属性值` 名池选怪物；数量与当前包一起存档 |
| 最终出口 | 剩余计数为 1 时，`CheckIfPackShouldBeSpawned` 创建 TownPortal；之后有 `CalculateChance(0.2)` 的 LootChest 分支 | 最后战斗包清空后生成可点击的 3D 出口，使用后才结算并回城 |
| 出口宝箱 | 20% 分支生成 LootChest；`RangeInclusive(0,100) < 5` 选 Large，否则 Medium，`canSpawnMimic=false` | 按大小生成可点击宝箱，打开一次后状态持久化 |
| 宝箱基础掉落数 | `LootChest.OpenChest` 对 Medium 调用 `RangeInclusive(60,70)`，Large 调用 `RangeInclusive(100,125)`，随后调用 `QueueRandomLoot` | 已接对应基础次数；数量/魔法找到加成及物品池仍待精确核对 |

原生函数还读取 `Area_Pack_Size_Bonus_Percent` 和 `Area_Pack_Size_Bonus_Percent_Final`，并有更多 Champion、Rare 与远程/元素怪筛选分支。网页已接末包的 `Area_Contains_More_Bosses` 数量，普通包仍只用了基线倍率 1；不把其他世界修饰符当作已恢复。原生 `TotalPacks` 计数包含最后的出口事件；网页现按 `TotalPacks−1` 生成战斗包，HUD 也显示战斗包数。先前依据属性名将末包称为“必定 Boss 包”是误判；原生调用参数明确不支持这种结论。

## 已实现的运行与存档

* 进入门户时，在一个 `GameStore` 事务内减少一个门户道具并保存初始 Niflheim 运行快照。重复进入不会重复消费。
* 每次权威战斗刷新，运行快照随进度保存：包数、已清包数、下一包余量/计时、随机数状态、玩家血/蓝/位置/冷却/Buff、当前怪物血量/稀有度/Buff。`GameStore` 关闭再重开后会恢复同一包，避免已消费门户后从普通地牢或新包开始。
* 最后一战斗包清空后，出口与可选宝箱写入运行快照；玩家可打开宝箱一次。宝箱领取、掉落入库及已开启标记同一事务；使用出口时，5000 silver、完成次数、Niflheim 状态清除与城镇状态同一事务。提前返回清除当前快照，不增加完成次数。
* 旧存档只有 active 标记、没有运行快照时，无法反推出门户的原始包数；恢复为最低 2 包。这是旧数据迁移的保守退化行为。

## 仍待原版对照

1. 世界修饰符如何改变普通包大小、Champion/Rare 的比例及远程/元素筛选。末包的 `1 + Area_Contains_More_Bosses` 数量已接；原生末包没有强制 Boss 的证据。
2. `DungeonMonsterSpawnArea` 的真实刷怪体积和出口/宝箱坐标；网页目前在玩家附近放置 3D 出口与宝箱，地点是交互用近似。
3. 宝箱 `QueueRandomLoot` 的数量加成、魔法找到及物品池细节；基础掉落次数已经接入，具体装备分布仍按网页现有 LootTable。
4. 快照尚未保存飞行中的弹体、地面陷阱和持续区域；恢复时会清除这些瞬时实体，再从当前怪物与玩家状态继续。

服务端回归：899 PASS，fidelity replay 58/58；web build、smoke、npc-smoke 通过。测试覆盖原子消费、跨进程恢复、逐包进度、传送门词缀 2717 的末包数量与跨重启恢复、实际末包战斗、出口等待、Medium/Large 宝箱一次性领取与磁盘记录、出口状态跨真实 GameStore 重启、最终奖励与回城一致性。
