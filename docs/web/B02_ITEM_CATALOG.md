# B02 装备目录 — 全量 608（验收分母）

任务 ID：B02（装备部分）。原版定义 ID：`gamedata_decrypted/Items.json`（608 条）。
日期：2026-10-03。基线：Android 1.9.3，见 [B01](B01_VERSION_BASELINE.md)。
判据：[B02_DEPRECATION_RULES.md](B02_DEPRECATION_RULES.md)。

## 目标版本与证据

源数据 `Items.json` 608 条（SerializedData 完整：Name / TypeId / AffixIds / DropPool / Image），
类型映射 `ItemTypes.json` 166 条全解析，608 个 TypeId 全部命中、无悬空。
重名 0，`Hidden` 0，`_UNUSED` 命名 0 → **废弃条目 0，全部 608 为正式内容**。

机读目录：`tools/web-content/generated/item_catalog_full.json`
（生成脚本 `tools/web-content/export_item_catalog_full.py`，可复现）。

## 分母

| 类别 | 数量 |
|---|---:|
| 物品定义总数 | 608 |
| 有 DropPool（可掉落） | 503 |
| 无 DropPool（货币/材料/精华/钥匙） | 105 |
| 已在网页 `item_catalog.json`（精选） | 458 |
| 未在精选目录 | 150 |

## 150 条差值分解（精选目录的筛选标准此前无文档，现已查明）

| 桶 | 数量 | 性质 | 处理 |
|---|---|---|---|
| 精华（Essence） | 60 | 制作材料 | 正式；不进装备掉落池，需材料子目录 |
| 符文（xxRune） | 22 | 制作/镶嵌材料 | 正式；同上 |
| 常规装备缺口 | 48 | 见下 | **正式；应补进掉落池** |
| 宠物相关 | 7 | PetExperiencePotion 等 | 正式；待宠物数据源定位后归类 |
| 独特物品（Unique_*） | 5 | 可掉落独特 | **正式；应补进掉落池**（E03 相关） |
| 宝箱（Lootbox） | 4 | 容器 | 正式；可获得性待核实 |
| 洗点（AttributeRespec*） | 2 | 消耗品 | 正式 |
| 制作材料（AddAffix 等） | 2 | 消耗品 | 正式 |

### 48 条常规装备缺口（有掉落、应进池）

含可穿戴装备：`Crystal Ring`、`GoldRing`、`Band`、`Knob`（Ring）、
`Amulet3`、`BarbedStone`、`CrystalAmulet`（Amulet）、
`HeavyBelt`（AffixIds=4）、`LightBelt`、`PlatedBelt`、
`KnightsCloak`（AffixIds=1）、`SwiftCloak`、
`Unique_BleedArrows`、`Unique_TitaniumArrows`（独特箭袋）、
`NiflheimPortal`（AffixIds=1，**网页 M3 已实现传送门玩法但掉落池无此物品**）、
`SantaHat`（AffixIds=3，无掉落，疑似季节限定）、
`Bless`、`Link`、`Opal`（货币，有掉落）、`HelheimKey`、`Iron`/`Steel`/`TitaniumOre`（矿石）、
各类 Reinforcement / ReRoll / RemoveAffix / ResetItem（制作消耗品，有掉落）。

完整 48 条见 `item_catalog_full.json`（`inCurrentCatalog=false` 且桶=常规装备缺口）。

## 网页接入位置

- `server/Nordicandia.Server/WebApi/ItemCatalog.cs` ← `GameData/item_catalog.json`（458 精选，
  掉落生成用，见 `LootTable.cs`）。
- 48 条缺口尚未接入：补进 `item_catalog.json` 即进入掉落池（需先按 E01 提取 implicit 范围）。
- 材料类（精华/符文/消耗品/货币/钥匙）尚未有网页目录位置 → 待 E05/制作目录承接。

## 尚存差异

- 105 条无 DropPool 物品的具体可获得性（商店/任务/合成/分解）尚未逐条核实。
- `SantaHat` 无掉落、疑似季节限定：需客户端活动代码佐证，暂标正式。
- 精华 60 条的合成用途、符文 22 条的镶嵌规则尚未提取（E04 范围）。

## 状态：已定位 / 已还原（目录）/ 待接入（48 条缺口）/ 对照未通过

复现：`python3 tools/web-content/export_item_catalog_full.py`
