# M0 状态与运行说明（2026-10-02）

M0 是决策门槛：验证素材导出、网页读取测试角色、以及最小规则样本。本文记录已完成内容、验收结果与下一步。

## 交付物

| 区域 | 内容 |
|---|---|
| 内容管线 | `tools/web-content/scan_bundles.py`、`export_assets.py`、`export_content.py` |
| 导出资源 | `web/public/assets/`：25 个环境 GLB、197 张头像 PNG、`content.json`、`manifest.json`（content version `m0-1`） |
| 网页客户端 | `web/src/`：Babylon 3D 地城 + 头像 token、相机、点击移动、自动索敌/攻击、技能、伤害飘字、HUD、在线快照 |
| 战斗规则 | `server/Nordicandia.Simulation/`：`Progression`（已校准）、`CombatModel`（Provisional）、可复现随机数 |
| 网页 API | `server/Nordicandia.Server/WebApi/`：`/api/web/v1` 会话、角色、快照 |
| 测试 | `server/Nordicandia.StoreTests/WebM0Tests.cs`、`web/tests/m0-smoke.mjs`、内容校验脚本 |

审计与规则详情见 [M0_ASSET_AUDIT.md](M0_ASSET_AUDIT.md)、[M0_RULES.md](M0_RULES.md)。

## 运行

### 1. 服务端（网页用 loopback HTTP/1.1:5080）

```bash
cd server
DOTNET_ROOT="$PWD/../.tools/dotnet" \
NORD_WEB_DEV=1 \
NORD_DATA_DIR="$PWD/../tmp/web-m0/server-data" \
NORD_WEB_PORT=5080 \
../.tools/dotnet/dotnet run --project Nordicandia.Server/Nordicandia.Server.csproj
```

* `NORD_WEB_DEV=1` 才开放 `/api/web/v1/dev/session`（本地免密登录），生产不要开启。
* 网页 API 只在 loopback 上监听；原生 gRPC 端口不受影响。

### 2. 网页客户端

```bash
cd web
npm install        # 首次
npm run dev        # http://127.0.0.1:5173/
```

Vite 把 `/api/web` 代理到 `http://127.0.0.1:5080`，会话使用 HttpOnly Cookie（同源），无需 CORS。

### 3. 重新导出资源（改了 gamedata 或需要复现时）

```bash
.tools/web-assets-venv/bin/python tools/web-content/scan_bundles.py
.tools/web-assets-venv/bin/python tools/web-content/export_assets.py
.tools/web-assets-venv/bin/python tools/web-content/export_content.py
```

## 测试

```bash
# 服务端规则/快照（含经验曲线往返、战斗固定样本、web 快照）
cd server
DOTNET_ROOT="$PWD/../.tools/dotnet" ../.tools/dotnet/dotnet run --project Nordicandia.StoreTests/Nordicandia.StoreTests.csproj

# 类型检查 + 生产构建
cd web && npm run build

# 浏览器冒烟（需先启动服务端与 npm run dev）
cd web && npm run smoke
```

`npm run smoke` 会启动无头 Chromium，登录、创建/选择角色、读取快照、渲染世界，并断言无控制台/网络错误；
截图保存到 `tmp/web-m0/web-m0.png`。

## M0 退出条件对照

| 通过条件 | 状态 | 证据 |
|---|---|---|
| 浏览器模型动作正确 | ✅ | 3D 环境 + 头像 token 正常显示与动画；截图 `tmp/web-m0/web-m0.png` |
| 素材来源与转换路径明确 | ✅ | `docs/web/M0_ASSET_AUDIT.md`，UnityPy→OBJ→GLB 可复现 |
| 能完成认证和快照读取 | ✅ | `/api/web/v1/dev/session` + `/characters` + `/snapshot`；`M0_STATUS` 命令可复现 |
| 缺失规则形成清单 | ✅ | `docs/web/M0_RULES.md`，伤害/血量/经验奖励标注 Provisional 与校准计划 |

## 已知限制 / 与计划的差异

* 原作没有骨骼角色模型，网页采用头像 token（与原作一致），不是「带骨骼动作的角色」；后续如需更华丽表现可选第三方模型。
* 网页端战斗结算目前为本地演示，**不写入存档**；权威结算与命令幂等属于 M3。
* GLB 暂不含贴图，环境为程序化着色。
* 客户端 `web/src/combat.ts` 是服务端 `CombatModel` 的镜像，校准后应改为服务端权威计算。

## 下一步（M1 3D 战斗切片）

1. 接入服务器模拟：新增带命令 ID 与存档版本的 `/api/web/v1/commands`，战斗结果由服务端判定并持久化。
2. 校准伤害公式：采集原客户端固定输入样本，替换 `CombatModel` 的 Provisional 规则。
3. 补齐首次登录/创建角色的正式表单与账号密码登录（当前 M0 用本地 dev 登录）。
4. 资源优化：按需分包、贴图烘焙、GLB 压缩，记录首屏传输与 Pixel 9 帧率。
