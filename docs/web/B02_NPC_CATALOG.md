# B02 NPC / 对话 / 任务目录（验收分母）

任务 ID：B02（NPC 部分）。原版定义 ID：`gamedata_decrypted/Conversations.json`（66）、
`Quests.json`（47）、`Monsters.json` 内 NPC 类型。
日期：2026-10-03。基线：Android 1.9.3，见 [B01](B01_VERSION_BASELINE.md)。
判据：[B02_DEPRECATION_RULES.md](B02_DEPRECATION_RULES.md)。

## 目标版本与证据

机读目录：`tools/web-content/generated/npc_catalog_full.json`
（生成脚本 `tools/web-content/export_npc_catalog_full.py`，可复现）。

## 分母与关键发现

| 类别 | 数量 | 说明 |
|---|---|---|
| NPC/可交互类型 | 14 | Blacksmith、Merchant、PortalMaster、SetItemMerchant、Disassembler、Offering、Petkeeper、CombatPetkeeper、Elder、GuildDefenseMaster、Changer、GiftStatue、EvilChest、Tutorial（见 [B02_MONSTER_CATALOG](B02_MONSTER_CATALOG.md)） |
| 对话 | 66 | **全部** PrimaryNpcId 指向 Tutorial（教程对话序列），非按 NPC 分配 |
| 对话-废弃 | 4 | `Tutorial_*_UNUSED`（判据：名称 `_UNUSED`） |
| 任务 | 47 | 全部是教程/目标触发器（`AllocateAttributes`、`BlacksmithClickMerge`…），**无剧情任务系统** |

结论：1.9.3 的"任务" = 新手引导触发器；Steam EA 宣传中的 Questlines 在此版本不存在。
NPC 的实际功能（铁匠合成、商人买卖、传送门）由 UI/客户端逻辑驱动，不在对话表里。

## 网页接入位置

- `NPC_TRADE_EXTRACTED.md`（商人交易提取，已有）。
- 网页 M3 已有祝福（Offering）/传送门（PortalMaster）UI；Town scene 的原版 `SpawnZone_*` 已导出，Blacksmith/Disassembler/Merchant/SetItemMerchant/Offering/PortalMaster 等点击路由至现有窗口。
- `BattlePetkeeper_1` 对应 CombatPetkeeper；`WindowInGame` 有打开 `WindowCombatPetkeeper` 的专用交互分支。普通 Petkeeper marker 是网页宠物列表入口；其与原版 `WindowPetkeeper` 的交互路径仍未确认。服务端读取 `SerializedPets`/`SerializedCombatPets`，已解锁选择写回存档。
- 解锁价由 `tools/web-content/export_pet_catalog.py` 从 `Monsters.json` 的宠物 `AffixIds` 与 `Affixes.json` 的 `OpalCost`(421)/`SilverCost`(968) 导出，严格按 `PetRow.Present → GetMinMaxValues(level=1, rarity=1, multiplier=1)`：PetCrow/Turtle/Eagle/BabyDragon/Larva 各 600 Opals；CombatPetDurnir/Eggther 各 6,000,000 Silver；Hala/Fossegrim 各 1,000 Opals；Hraesvelgr 1,200 Opals。购买 API 服务端从目录取价、校验余额并精确扣款，未信任浏览器传入价格。PetDog/SnowOxe/Snake 没有这两个价格词缀，不伪造付费入口或获取渠道。当前列表展示宠物定义中的 affix 数值，但尚未把这些 passive effects 接到战斗/奖励结算。详见 [PET_RECOVERY](PET_RECOVERY.md)。
- Petkeeper `OnEnter` 的环境 Minion 生成，以及 CombatPet 战斗生成/升级/死亡复活，仍未接入。
- `Changer`、`Elder` 反汇编只有 `InteractiveNpc` 基类构造；`GuildDefenseMaster` 只有构造和 `Actor.OnEnter`；尚未找到这些类自身的专用功能。全局 `InteractiveNpc_OnInteraction` 对话/窗口触发仍需逐支对照，当前不添加猜测操作。Helheim 是 `HelheimPortalInstance`/`HelheimGameMode` 特殊模式而非 NPC 对话，网页入口/模式行为尚未接入。

## 尚存差异

- 已恢复价格的普通宠物/CombatPet 已支持购买和已解锁选择；无价格词缀宠物的获取渠道、CombatPet 战斗召唤/经验/技能/死亡复活、Petkeeper 环境宠物生成仍未完成（P03/P04）。
- GuildDefenseMaster 的 NPC 触发到 GuildSiege 服务/战斗的调用链、Helheim Portal 的启动条件/深度推进，以及 Elder/Changer 的实际 Interaction 分支仍待提取和验证（U02/W06）。
- 教程触发器的触发条件未提取（暂缓：网页版是否需要复刻新手引导待定）。

## 状态：已定位 / 已还原（目录）/ 待接入 / 对照未通过

复现 NPC 目录：`python3 tools/web-content/export_npc_catalog_full.py`；复现宠物价目：`python3 tools/web-content/export_pet_catalog.py`
