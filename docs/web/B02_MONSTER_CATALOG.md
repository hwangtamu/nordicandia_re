# B02 怪物目录 — 全量 136（验收分母，兼 NPC/宠物底表）

任务 ID：B02（怪物部分）。原版定义 ID：`gamedata_decrypted/Monsters.json`（136 条）、
`MonsterTypes.json`（30 类型）、`Brains.json`（34）。
日期：2026-10-03。基线：Android 1.9.3，见 [B01](B01_VERSION_BASELINE.md)。
判据：[B02_DEPRECATION_RULES.md](B02_DEPRECATION_RULES.md)。

## 目标版本与证据

136 条全解析：TypeId 全部命中 30 类型、无悬空；重名 0；`Hidden` 0；
`_UNUSED` 命名 0 → **废弃条目 0，全部正式**。

机读目录：`tools/web-content/generated/monster_catalog_full.json`
（生成脚本 `tools/web-content/export_monster_catalog_full.py`，可复现）。

稀有度语义（由数据反推）：0=Normal、1=Magic、2=Rare（普通野怪三档）；
4=Champion（SpiderQueen、Goblin2 等精英变体）；6=Boss（Boss_WolfKing）。
3/5 在数据中未出现。

## 分母

| 类别 | 数量 |
|---|---:|
| 怪物总数 | 136 |
| 野外/地牢生成（含稀有度） | 115 |
| Boss/召唤物/触发器（无稀有度） | 21 |

### 按类型

野怪：Beast 18、Undead 17、Demon 15、Orc 10、Goblin 4、Dragon 3、Golem 3。
季节怪：SpringMonster 12、FallMonster 5、ChristmasMonster 3（季节限定内容）。
特殊：BaseMinion 16（召唤仆从）、GolemTrigger 3（Boss 召唤触发器）。

### NPC 与可交互对象（也在怪物表里，NPC 目录底表）

Blacksmith、Elder、Merchant、PortalMaster、SetItemMerchant、Disassembler、
Offering、Petkeeper、CombatPetkeeper、GuildDefenseMaster、Changer、
GiftStatue、EvilChest、Tutorial 各 1。

### 宠物数据源（已定位，关闭 B02 待核实项）

- 战斗宠物（CombatPet 类型）：CombatPetDurnir、CombatPetEggther、
  CombatPetFossegrim、CombatPetHala、CombatPetHraesvelgr（5）
- 非战斗宠物（Pet 类型）：PetBabyDragon、PetCrow、PetDog、PetEagle、
  PetLarva、PetSnake、PetSnowOxe、PetTurtle（8）
- 相关 NPC：Petkeeper、CombatPetkeeper
- 宠物获取/价格/升级规则不在怪物表内，需另查商店/Catalog 数据（P03 范围）。

## 待核实

- 4 个无 Brain 的怪物：FireGolem、StoneGolem、CrystalGolem、
  HunterMirrorImageMinion（BrainId=None；Golem 系可能由 GolemTrigger 驱动，
  需客户端行为佐证）。
- 21 条无稀有度怪物的逐条可获得性（Boss 战 / 技能召唤 / 剧情触发）。

## 网页接入位置

- `server/Nordicandia.Simulation/MonsterPowerCatalog.cs`（Boss 技能，M3 已接入 6 类效果）。
- `brains_catalog.json`（34 brains，M3 已接入加权动作）。
- 怪物数值/缩放：W01 范围，仍为 Provisional。

## 状态：已定位 / 已还原（目录）/ 待接入（数值、AI 全量）/ 对照未通过

复现：`python3 tools/web-content/export_monster_catalog_full.py`
