# M0 素材审计（2026-10-02）

目标：确认「能否导出并展示一套真正可用的 3D 角色/怪物/场景资源」，并给出可维护的转换路径。
结论先写在前面，证据与命令附后。

## 结论

1. **可用的 3D 场景资源存在**：地下城主题包内含地板、墙、柱子、门、火盆、火把、道具等网格，可直接转成 glTF/GLB。
2. **可用的「带动作角色/怪物模型」不存在**：全部 54 个 Addressables bundle 中只有 2 个 `SkinnedMeshRenderer`，且都属于 `worldobjects` 里的 VFX 翅膀（`chibang`/`VFX_Model8`）。
3. **原作的战斗角色/怪物是 2D 头像标记（token）**：`CharacterRaces.ActorImage`、`Monsters.Image` 以及 `MonstersAvatarIcons_*` 都是 256×256 的 2D 立绘/图标。Steam 商店截图证实：3D 石地板 + 圆形头像 token 才是原作的呈现方式，而不是骨骼角色。
4. 因此网页首版采用 **3D 环境素材 + 头像 token**，与原作者表现一致；不存在「缺骨骼模型」的阻塞。若后续要更高表现，再引入第三方授权骨骼模型作为可选升级。

## 证据

### 包结构

* `dist/android-arm64-src/UnityDataAssetPack.apk`：54 个 `assets/aa/Android/*.bundle`。
* `assets/aa/settings.json`：`m_DisableCatalogUpdateOnStart: true`，**没有远端内容更新**，即所有资源都在包内，不存在「角色模型从 CDN 下载」的可能。
* 基线 APK `assets/bin/Data/sharedassets0.assets`（461 MB）以 UI 为主：21624 GameObject、21515 RectTransform、18351 CanvasRenderer、692 Sprite、**2 Mesh、0 SkinnedMeshRenderer**。

### 资源类型统计（54 个 bundle 合计）

| 类型 | 数量 | 说明 |
|---|---:|---|
| Mesh | 1317 | 环境网格 |
| SkinnedMeshRenderer | 2 | 均在 `worldobjects`，为 VFX |
| Avatar | 51 | 多数为 `Rock_/Door/Wall` 等环境枢轴 |
| AnimationClip | 42 | 门开合、火光、VFX 序列 |
| Animator / AnimatorController | 268 / 18 | 环境动画 |
| Sprite | 6243 | UI / 图标 / 立绘 |
| Texture2D | 5817 | 贴图 |

含 `SkinnedMeshRenderer`/`Animator`/`Avatar` 的 bundle 全部是环境包（`world_dungeon_theme_*`、`world_golem`、`world_*`）与 `worldobjects`。没有任何角色/怪物骨骼。

### 原作表现

Steam 商店截图（app `1503790`）显示：俯视 3D 石地板、墙体、火把，角色与怪物为地面上的圆形头像 token（含血条、伤害飘字、掉落文字）。这与导出的 2D 头像和 3D 环境完全吻合。

## 转换路径

```
UnityPy.load(bundle)
  Mesh.export()            -> OBJ 文本 (v/vt/vn/f)
  trimesh.load(obj_text)   -> 三角网格
  tm.export(file_type=glb) -> GLB
  Texture2D.image          -> PNG （ASTC/ETC 由 UnityPy 解码）
```

脚本：

* `tools/web-content/scan_bundles.py` — 生成 `tmp/web-m0/bundle-inventory.json` 资源清单（可缓存、可复查）。
* `tools/web-content/export_assets.py` — 导出环境 GLB + 头像 PNG + `web/public/assets/manifest.json`（含源 bundle 的 SHA-256 与内容版本）。
* `tools/web-content/export_content.py` — 从 `gamedata_decrypted/` 导出 `web/public/assets/content.json`，并校验头像引用是否都能在导出结果中找到。

## 本次导出结果（content version `m0-1`）

* `web/public/assets/kit/dungeon_default/*.glb` — 25 个环境网格，共约 248 KiB。
* `web/public/assets/avatars/*.png` — 197 张头像/图标（玩家种族、怪物立绘、`MonstersAvatarIcons_*`）。
* 合计约 11 MB，符合「首场景传输 ~20 MB」预算的初步预期（尚未压缩、尚未按需分包）。

## 已确认的缺口

* **无骨骼角色/怪物模型**（按上述设计不作为首版阻塞）。
* **网格未带贴图**：M0 的 GLB 仅几何，运行时用 Babylon 材质着色；后续可按 Renderer→Material→Texture 关系把贴图烘焙进 GLB。
* **Wood Elf / Satyr 缺失 `ActorImage` 图标**（`Human_02_nobg 1`、`69`），`export_content.py` 会报告，客户端回退到 `RaceHuman`。
