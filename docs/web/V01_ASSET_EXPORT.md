# V01 全量资源与正确引用

日期：2026-10-03。对应 [完整复刻任务清单](FULL_FIDELITY_BACKLOG.md) 的 **V01**。
基础结论见 [M0 素材审计](M0_ASSET_AUDIT.md)（3D 环境 kit + 2D 头像 token 才是原作表现）。

## 本轮交付

`tools/web-content/export_assets.py` 扩展为导出**全部世界 kit**并生成**缺失资源报告**：

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

（`world_dungeon`/`world_town`/`world_tutorial` 的 `_assets` 包内无网格——城镇/教程是场景 +
ShaderGraph/材质，网格在别处。）

### 头像（`web/public/assets/avatars/`）

* **320** 张 2D 头像/图标，覆盖 `Monsters.Image` 与 `CharacterRaces.ActorImage` 引用的**全部 138 个图标**。
* 匹配规则：既有的 `AVATAR_TEXTURE_PATTERNS` **加上 gamedata 引用的图标名**（此前只导出通用头像，
  123/138 怪物/种族立绘缺失）。

### 缺失资源报告（`web/public/assets/missing-assets.json`）

`{ referenced: 138, exported: 320, missing: {}, missingCount: 0 }`——gamedata 引用的每个图标均已导出。

### manifest

`web/public/assets/manifest.json`：`contentVersion = v01-1`、`kits.{name}`（13）、
`kit`（= `kits.dungeon_default`，兼容旧客户端）与 `avatars` 清单。

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
当前 web build + smoke 绿；服务端 680 PASS。
