# B02 世界目录 — 全量 36（验收分母）

任务 ID：B02（世界部分）。原版定义 ID：`gamedata_decrypted/Worlds.json`（36 条）。
日期：2026-10-03。基线：Android 1.9.3，见 [B01](B01_VERSION_BASELINE.md)。
判据：[B02_DEPRECATION_RULES.md](B02_DEPRECATION_RULES.md)。

## 目标版本与证据

36 条全解析：Tier 1–35 各 1 个世界 + Town（Tier=None）；35 个阶层世界
全部指定 Boss，BossMonsterId 全部命中 Monsters.json，无悬空；
重名 0 → **废弃条目 0，全部正式**。

机读目录：`tools/web-content/generated/world_catalog_full.json`
（生成脚本 `tools/web-content/export_world_catalog_full.py`，可复现）。
每条含阶层、主题、Boss 名、怪物类型生成权重。

## 分母

| 类别 | 数量 |
|---|---:|
| 世界总数 | 36 |
| 阶层世界（Tier 1–35） | 35 |
| 城镇（Town） | 1 |

阶层 1–30 为命名区域（Grasshill、Borgash、Krilaqar…），31–35 为
`The Void_12/16/17/18/19`（终局虚空阶层）。Boss 在阶层世界间复用
（如 Boss_WolfKing 同时是 Tier 1 和 Tier 8 的 Boss）。

## 网页接入位置

- 网页当前只有 1 张手工地牢（M0）；36 世界的图结构、场景块、刷怪区
  均未接入 → W04 范围。
- 世界修饰符（138 地图词缀，见 [B02_AFFIX_CATALOG.md](B02_AFFIX_CATALOG.md)）
  与世界的关联规则未提取 → W06 范围。

## 尚存差异

- 各世界解锁条件、checkpoint/WorldTier/WorldWaypoint 语义未提取（W05）。
- Town 静态场景已从 `world_town_scenes_all` 导出；20 个原版 SpawnZone 位置进入 `town_scene.json` 并在网页映射当前可用站点。动态 NPC/动画、具体解锁/传送门流程仍未提取（W05/U02/P03）。

## 状态：已定位 / 已还原（目录）/ 待接入 / 对照未通过

复现：`python3 tools/web-content/export_world_catalog_full.py`
