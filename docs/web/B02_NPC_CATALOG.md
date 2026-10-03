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
- 网页 M3 已有祝福（Offering）/传送门（PortalMaster）UI。
- 教程对话序列、铁匠/分解等 NPC 功能移植 → U02 范围。

## 尚存差异

- 各 NPC 的具体功能与界面流程未逐项核对（U02）。
- 教程触发器的触发条件未提取（暂缓：网页版是否需要复刻新手引导待定）。

## 状态：已定位 / 已还原（目录）/ 待接入 / 对照未通过

复现：`python3 tools/web-content/export_npc_catalog_full.py`
