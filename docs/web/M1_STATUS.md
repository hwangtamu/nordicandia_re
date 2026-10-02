# M1 状态 — 服务端权威战斗切片（2026-10-02）

M1 目标：移动、相机、索敌、普攻、1 技能、1 怪物、死亡与经验；**服务端模拟接通**，
连续战斗可运行、固定规则样本可重复、刷新后恢复经验。

## 交付物

| 区域 | 内容 |
|---|---|
| 服务端模拟 | `server/Nordicandia.Simulation/CombatInstance.cs`：固定步长 0.05 s 的确定性战斗实例（玩家、1 类怪物 `Draugr`、索敌、普攻、技能、怪物重生、玩家死亡/重生、经验） |
| 权威注册表 | `server/Nordicandia.Server/WebApi/CombatRegistry.cs`：按角色持有实例，惰性按真实时间推进，命令幂等，进度回写 `GameStore` |
| 网页 API | `GET /api/web/v1/characters/{id}/state`、`POST .../commands`（命令 ID + 预期版本） |
| 浏览器 | `web/src/game.ts` 改为渲染服务端状态并插值；点击发送 `move`，技能发送 `skill`；伤害/击杀由快照差分推导 |
| 测试 | `server/Nordicandia.StoreTests/CombatInstanceTests.cs`（15 项）、`web/tests/smoke.mjs` |

## 权威性与幂等

* 每个会改变存档的命令携带 **唯一 commandId** 与 **expectedVersion**。
  - 相同 commandId 重放返回**原快照**，不会二次结算（测试：`duplicate returns same version`）。
  - expectedVersion 大于当前版本（客户端来自未来）被拒绝（`future_version`）。
* 经验、击杀、Offense/Defense/Recovery 通过 `GameStore.SaveRealtimeProgress` 原子落盘；
  等级仍由累计经验纯函数派生。
* 浏览器不再计算伤害/经验，只发送意图并渲染。`web/src/combat.ts` 现为可供人读的公式镜像，非运行路径。

## 运行与测试

见 [M0_STATUS.md](M0_STATUS.md) 的启动命令，M1 相同。额外：

```bash
# 服务端模拟/幂等/持久化
cd server
DOTNET_ROOT="$PWD/../.tools/dotnet" ../.tools/dotnet/dotnet run --project Nordicandia.StoreTests/Nordicandia.StoreTests.csproj

# 浏览器冒烟（会真实触发移动、普攻与技能）
cd web && npm run smoke
```

API 手工验证（已实测）：

```bash
# state → move → 等待 → state（kills/xp 增长）→ skill 两次（第二次 duplicate）→ snapshot（经验已落盘）
curl -s -b cookies http://127.0.0.1:5080/api/web/v1/characters/$CID/state
```

## M1 退出条件对照

| 通过条件 | 状态 | 证据 |
|---|---|---|
| 连续战斗可运行 | ✅ | 冒烟：`6 kills`、怪物重生、玩家 HP 109/140 存活；截图 `tmp/web-m1/smoke.png` |
| 固定规则样本可重复验证 | ✅ | `FixedStepIsDeterministic`（同种子/同步长序列化完全一致）；`M0_RULES` 固定 `seed=1` 样本 |
| 刷新后恢复经验 | ✅ | `CommandsAreIdempotentAndPersist`：重置内存实例后重新播种，经验/击杀与落盘一致 |

## 已知限制 / 与计划的差异

* 仅 1 类怪物、无掉落与装备影响（属 M2）。
* 玩家位置不持久化；重登回到 (0,0)。经验/货币/击杀持久化。
* 伤害公式仍为 **Provisional**（见 [M0_RULES.md](M0_RULES.md)）；服务端权威结构已就位，待客户端样本校准。
* 客户端渲染为状态插值，无预测回滚；高延迟下会有轻微滞后。

## 下一步（M2 装备与成长循环）

1. 掉落：`Droprates`/`ItemGenerator` 规则接入服务端，击杀产出物品并持久化。
2. 背包装备：`GameStore.ApplyItemOperations` 已有；网页 UI 穿戴并让装备属性进入 `Offense/Defense`。
3. 补齐技能/敌人/Boss 与一个完整地下城循环；奖励与消耗幂等。
