# 验收缺陷修复记录（2026-10-02）

针对 [ACCEPTANCE_2026-10-02.md](ACCEPTANCE_2026-10-02.md) 的 R1–R5，全部已修复并有回归测试。

| 编号 | 缺陷 | 修复 | 回归测试 |
|---|---|---|---|
| R1/P1 | 恢复实例重复叠加装备属性 | 落盘改为**基础属性**（`effective - 装备加成`），恢复时再叠加一次装备；升级成长仍包含在基础里 | `EquipmentStatsSurviveRestartWithoutDoubleCount`（重开后数值一致、卸装恰好扣除加成） |
| R2/P1 | 发送命令额外推进模拟时间，无效命令也产出经验 | 删除 `ApplyCommand` 里无条件 `Advance(0.05)`；模拟时间只来自服务器时钟 `AdvanceLocked` | `CommandsDoNotAdvanceSimulationTime`（冻结时钟下 200 条 invalid/move：经验、击杀、冷却均不变） |
| R3/P1 | 网页注册绕过 `NORD_ALLOW_REGISTRATION`；路由作用于所有监听器 | `/session` 复用同一注册策略（`NORD_ALLOW_REGISTRATION`）；`/dev/session` 额外要求**回环地址**；新增 `NORD_WEB_API=0` 显式关闭网页路由 | `RegistrationPolicyIsShared` + 实测（`NORD_ALLOW_REGISTRATION=0` → 403，`NORD_WEB_DEV=0` → dev 404） |
| R4/P2 | 超过 64 条后旧命令被当新命令执行 | 命令记录**持久化**在 `SavedCharacter.CommandLog`（256 条）；记录 `ExpectedVersion`；`expectedVersion>0` 且早于最旧记录则拒绝为 `stale_command` | `StaleCommandsAreRejected`（300 条后重放旧装备命令被拒，B 仍装备） |
| R5/P2 | 失败命令重试被报告为成功 | 命令记录保存 `Applied`/`Reason`；重复命中返回**原始结果**，不再是 `true/duplicate` | `FailedCommandRetryKeepsFailure`（失败重试仍返回 `cooldown`） |

## 附带改进

* 客户端收到 `stale_command` 显示 "Resyncing…" 并写入服务器快照。
* 背包与技能面板互斥显示，避免重叠。
* **正常新角色可通关 Boss**：调整平衡（起始属性 60/40/12、玩家生命 `150+50×等级`、怪物与 Boss 生命/攻击下调），四职业新角色均在 22–29 秒内通关并存活；新增测试 `R-fresh` 逐职业验证。
* **环境贴图**：导出 10 张地城基础色贴图，客户端按网格名映射叠加（地面/墙柱/木门/金属），不再是无贴图深色材质。

## 验证

* 服务端测试 **192 PASS**（新增 `AcceptanceRegressionTests`，含 R1–R5 与注册策略）。
* `tsc` 通过；`npm run smoke` PASS（掉落、穿戴、精通加点、法力条）。
* R3 实测：`NORD_ALLOW_REGISTRATION=0` 时 `createAccount=true` 返回 **403**；`NORD_WEB_DEV=0` 时 `/dev/session` 返回 **404**。

## 放宽/说明

* 命令去重窗口为 256 条；`expectedVersion=0` 视为「未使用乐观并发」而不判为过期（网页客户端始终发送当前版本，非 0）。
* 装备基础属性与等级成长合并持久化；`CombatStats` 现在保存**基础值**，有效值 = 基础 + 装备。
