# W04 程序化地图生成

日期：2026-10-03。对应 [完整复刻任务清单](FULL_FIDELITY_BACKLOG.md) 的 **W04**。
依赖 V01（世界 kit 已导出，见 [V01_ASSET_EXPORT](V01_ASSET_EXPORT.md)）。

## 原版证据

客户端地图由**第三方程序生成器**产生：场景包（`world_*_scenes_all`）含
`DungeonGridFlowPolygonDungeon`、`PathSeeker`、`DungeonItems`、`DungeonMergedMeshes`
等 GameObject，生成器本体不在 `Assembly-CSharp`（插件）。因此地图**布局不是静态数据**。

可恢复的规则来自 `DungeonMonsterSpawnArea`：每个刷怪区有 `MonsterLevel`、
`ForcedNumMonsters`、`ForcedMonsterRarity`、`MonsterDifficulty`、`PathTypeIndex`、
`IndexOnPath`、`IsLastSpawnArea`——即怪物沿"路径"分布在刷怪区里。

## 本轮交付

### 服务端 `MapLayout`（`Nordicandia.Simulation/MapLayout.cs`）

确定性生成器（SplitMix64 种子）：随机放置不重叠矩形房间，用 L 形走廊连接相邻房间。
输出地板网格、房间中心、刷怪锚点（= 房间中心）与主题名。

* `CombatRegistry` 按角色 tier 生成 21×21、5 房间的布局：
  `MapLayout.Generate(21,21,5, seed ^ MAGIC, ThemeForTier(tier))`。
* `ThemeForTier`：世界 `ThemeId`（1/2/3）→ `dungeon_grass`/`dungeon_sand`/`dungeon_undead`，
  其余 `dungeon_default`（**映射为 Inferred**）。
* `CombatInstance` 新增可选 `layout`：Niflheim 包改用布局的**房间刷怪锚点**
  （`layout.World(anchor)` 映射到 arena 坐标），无布局时回退原 6 环形点。
* DTO：`WebCombatState.Map`（`WebMapLayout`：`width/height/theme/rows/spawnAnchors`）。

### 客户端渲染（`web/src/game.ts`）

`pushState` 检测到 `map` 签名变化时重建地牢：按 `rows` 的 `#` 放置 `Floor_Slab_lrg`
地板砖、在贴地板的空格放置 `MOD_Wall_01_O_straight_large` 墙砖（按 arena 缩放）。
`instantiate` 增加 `scale` 参数。

## 验证

* `W04MapLayoutTests` 7 条：定种子确定性、多房间、锚点在地板上、锚点在 arena 内、
  地板面积、不同种子不同布局、主题携带。
* 服务端 **687 PASS**，回放 58/58；web build + smoke 绿，截图可见石地板网格 + 边界墙。

## W02 碰撞与寻路（已接入）

* **墙体碰撞 + 贴墙滑动**：`CombatInstance.MoveMonster`/`ConstrainMove` 用 `Layout.IsFloor`
  判断目标格；撞墙时沿空闲轴滑动。玩家移动同样受墙限制。
* **A\* 寻路**：`MapLayout.FindPath`（4 连通、地板网格）为追击中的怪物提供路径点，
  每 0.4s 刷新；刷怪位置也约束到地板上。
* `W04MapLayoutTests` 新增 3 条：跨房间有路径、路径格全在地板、墙格无路径。

## 未完成

* 世界主题 kit 的**纹理切换**：客户端仍用默认 kit 几何；切主题需按 theme 重载 GLB。
* 刷怪区体积/密度、`PathTypeIndex`/`IndexOnPath` 的真实路径约束。
* 城镇/WorldTier/checkpoint 推进（W05）、Niflheim `DungeonMonsterSpawnArea`（W06）。
