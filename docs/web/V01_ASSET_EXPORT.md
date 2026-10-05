# V01 全量资源与正确引用

日期：2026-10-03。对应 [完整复刻任务清单](FULL_FIDELITY_BACKLOG.md) 的 **V01**。
基础结论见 [M0 素材审计](M0_ASSET_AUDIT.md)（3D 环境 kit + 2D 头像 token 才是原作表现）。

## 本轮交付

`tools/web-content/export_assets.py` 导出**全部世界 kit**并生成**缺失资源报告**；它同时调用 `export_town_scene.py`，从 `world_town_scenes_all` 还原 Town 静态场景和 SpawnZone：

### 世界 kit（`web/public/assets/kit/<name>/`）

| bundle | kit | meshes | textures |
|---|---|---:|---:|
| `world_dungeon_theme_default` | dungeon_default | 26 | 10 |
| `world_dungeon_theme_grass` | dungeon_grass | 14 | 4 |
| `world_dungeon_theme_sand` | dungeon_sand | 10 | 1 |
| `world_dungeon_theme_undead` | dungeon_undead | 13 | 2 |
| `world_golem` | golem | 13 | 0 |
| `world_helheim` | helheim | 11 | 0 |
| `world_niflheim` | niflheim | 10 | 0 |
| `world_odrstrail` | odrstrail | 11 | 1 |
| `world_vanaheim` | vanaheim | 17 | 4 |
| `world_guilddefense` | guilddefense | 8 | 0 |
| `world_town_scenes_all` | town_scene.glb | 80 unique mesh assets / 510 placed submeshes | scene materials/textures embedded |

`world_town_assets_all` 主要承载材质/纹理；Town 场景几何在 `world_town_scenes_all`。新增导出器读取 Unity Transform 层级和 StaticBatchInfo submesh 范围，生成实际静态场景 `web/public/assets/kit/town/town_scene.glb` + `town_scene.json`（20 个原版 SpawnZone 坐标）。Pet/NPC actor 仍是 2D portrait token，不在静态 Town scene bundle 内。

### 头像（`web/public/assets/avatars/`）

* **320** 张 2D 头像/图标，覆盖 `Monsters.Image` 与 `CharacterRaces.ActorImage` 引用的**全部 138 个图标**。
* 匹配规则：既有的 `AVATAR_TEXTURE_PATTERNS` **加上 gamedata 引用的图标名**（此前只导出通用头像，
  123/138 怪物/种族立绘缺失）。

### 装备与背包图标（`web/public/assets/items/`、`inventory-ui/`）

`tools/web-content/export_inventory_art.py` 按 `Items.json.IntegerId` 对齐原版 `Textures/Items` Sprite，避免同名数字文件误映射；该导出器也已接入 `export_assets.py`。当前 608 条带 Image 的物品定义中有 **607 张原版图标**，仅缺 `IntegerId=122`（InsureCraft）；原 bundle 未找到对应 Image 路径 Sprite，清单记录为缺失，不用相似图标冒充。另导出 packed assets 中 5 张背包/边框相关 UI Sprite。

Web 背包和已装备栏现在显示按 IntegerId 对应的原版物品图标，并使用提取的边框/选中素材；背包网格、装备槽、筛选及操作仍是 Web 重制布局。当前验证覆盖素材对应和交互构建，尚未做 Android 原客户端逐像素截图对照，不宣称 UI 完全一致。`web/public/assets/manifest.json.inventoryArt` 与各资源目录 manifest 记录来源、覆盖数和缺失 ID。

### 缺失资源报告（`web/public/assets/missing-assets.json`）

`{ referenced: 138, exported: 320, missing: {}, missingCount: 0 }`——gamedata 引用的每个图标均已导出。

### manifest

`web/public/assets/manifest.json`：`contentVersion = v01-2`、`kits.{name}`（13）、
`kit`（= `kits.dungeon_default`，兼容旧客户端）、`avatars` 清单及 `townScene` provenance/spawn manifest。

## 对 W 的解锁

* **W01 怪物密度/阶层**、**W06 `DungeonMonsterSpawnArea`**、**W04 地图**需要地图与刷怪区。
* 环境网格已齐备（4 个地下城主题 + 6 个世界），可直接组装房间/走廊。
* 但客户端地图由**第三方程序生成器**产生：场景包含 `DungeonGridFlowPolygonDungeon`、
  `PathSeeker`、`DungeonItems`、`DungeonMergedMeshes` 等 GameObject，生成器本体不在
  `Assembly-CSharp`（插件）。所以地图**布局**不是静态数据，网页需要**自建一个程序化地图生成器**，
  用导出的主题 kit 组装房间/走廊，并沿路径放置刷怪区——这是 V01 解锁后的下一步（W04）。

## 复现

```bash
.tools/web-assets-venv/bin/python tools/web-content/export_assets.py
cd web && npm run build && npm run smoke
```
2026-10-04 背包图标/UI 接线后，web build 与 m2 smoke 通过；当前工作区 StoreTests 为 **903 PASS**、fidelity replay **58/58**。
