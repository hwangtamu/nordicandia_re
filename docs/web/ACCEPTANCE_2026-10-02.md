# M0–M2 独立验收（2026-10-02）

验收版本：`6ff3784`。检查开始时工作区干净。本次未修改业务代码；使用独立临时存档启动服务，未连接生产服。

## 结论

**已有可运行原型，但 M1、M2 暂不验收通过。** 服务端权威时间、装备恢复以及命令重试存在可复现缺陷。M0 的资源转换、网页登录和角色读取可有条件通过；表现为 3D 环境 + 头像 token，不能将其描述为完成了骨骼角色动画。

| 范围 | 本次实测 | 判定 |
|---|---|---|
| M0 素材与读取 | 3D 环境、头像 token、测试登录、创建/读取角色可以运行 | 原型通过；贴图和动作还原度有限 |
| M1 战斗与经验 | 自动战斗、技能、击杀、经验和网页刷新恢复可用 | 未通过：操作请求能额外推进模拟时间 |
| M2 装备与成长 | 掉落、穿戴、词缀、5 类敌人与 Boss 循环已有实现，相关现有测试通过 | 未通过：装备恢复重复计入属性，延迟重试可以重新执行 |
| 原版规则还原 | 经验曲线有已还原依据；技能定义和部分常数已提取 | 战斗公式、装备词缀和若干技能行为仍有暂定实现，不能认定完整还原 |

## 已复现的问题

### R1 / P1：恢复实例会重复叠加装备属性

位置：`server/Nordicandia.Server/WebApi/CombatRegistry.cs:76` 与 `:341`。

`FlushLocked` 保存的是已经包含装备加成的 Offense/Defense/Recovery；`GetOrCreate` 又把这些值作为基础数值，再加一次 `EquipmentBonus`。

独立测试：穿戴攻击加成为 94 的武器，继续战斗使状态落盘，随后 `registry.Reset()` 模拟服务重启：

```text
before=301.5, persisted=301.5, after=395.5
unequip-after-reset: off=301.5
```

恢复后凭空多出 94 攻击；卸下武器后仍残留第一次装备加成。实际结果会随测试角色随机种子和升级次数略有变化，但额外增加值始终等于装备加成。

修复要求：明确区分基础属性、等级成长和装备总属性；恢复时只能叠加一次。回归测试覆盖穿戴后击杀落盘、真正关闭重开存储/注册表、卸装、连续两次重启；不仅检查装备槽位保留，还要检查数值完全一致。

### R2 / P1：发送命令能加速战斗，甚至无效命令也会产出经验

位置：`server/Nordicandia.Server/WebApi/CombatRegistry.cs:156`。

每次命令在按真实时间推进后，又无条件调用 `Advance(0.05)`。这个分支包含未知命令及技能冷却等失败请求。于是浏览器发送请求的频率能决定攻击、冷却与结算速度。

独立测试把 `TimeProvider` 固定不动，发送 200 个不同 ID 的 `invalid` 命令：

```text
cooldown 29.9 -> 19.9
kills 0 -> 6
experience 0 -> 127.57188014843278
```

真实时间为零，却额外进行了 10 秒战斗，并写入经验。

修复要求：模拟时间只来自服务器时钟或统一服务器 tick。命令仅修改动作意图，不赠送时间。回归测试应比较相同真实时间下 0/10/1000 条移动、失败技能和未知命令的奖励与冷却。

### R3 / P1：网页注册绕过服务端关闭注册的配置

位置：`server/Nordicandia.Server/WebApi/WebApiEndpoints.cs:40`。

原 `LoginService` 遵守 `NORD_ALLOW_REGISTRATION`，新 `/api/web/v1/session` 直接调用 `RegisterCredential`，没有检查该配置。

本次启动第二个独立服务，设置 `NORD_ALLOW_REGISTRATION=0`、`NORD_WEB_DEV=0`，发送 `createAccount=true`：

```json
{"dev":false,"registrationEnv":"0","status":200,"accountCreated":true}
```

此外，`Program.cs:95` 的路由注册作用于整个 ASP.NET 应用。新增一个 loopback 5080 监听器不代表网页路由仅能从该端口访问；启用现有公网监听器时，这些路由也会被映射到它。此结论来自路由配置检查，未测试生产网络。

修复要求：网页与原生端复用相同注册策略；若网页仍处于开发阶段，显式控制路由启用范围。dev 免密入口应有明确的测试环境边界。

### R4 / P2：超过 64 条命令后，延迟重试重新执行旧操作

位置：`server/Nordicandia.Server/WebApi/CombatRegistry.cs:122`、`:384`。

去重只保存最近 64 条命令；旧版本仅在「大于当前版本」时拒绝，因此已被淘汰的旧命令可以再次执行。

独立复现：穿 A → 穿同槽 B → 发送 64 条其他命令 → 重放原穿 A 命令：

```text
reason=ok, Aequipped=True, Bequipped=False
```

用户明明已选择 B，旧请求重试却把它换回 A。重复技能或精通命令也没有长期去重保证。即使尚不要求跨进程事务，这个问题在同一个运行实例中就会出现。

修复要求：明确可保证的重试窗口和命令序列规则；超出窗口的旧命令必须拒绝，而非当作新命令执行。会消费点数/货币的命令需要持久化幂等记录。

### R5 / P2：失败命令重试被错误报告为成功

位置：`server/Nordicandia.Server/WebApi/CombatRegistry.cs:122`。

缓存只保存快照，丢失原来的 `Applied` 和 `Reason`。任何缓存命中都会返回 `true/duplicate`。

独立测试：

```text
original=False/cooldown
retry=True/duplicate
```

修复要求：缓存并返回完整原始结果；失败重试仍应返回原来的失败原因，客户端不能将重复失败当作成功释放技能。

## 验证记录与边界

- `npm run build --prefix web`：通过。主 bundle 约 6.30 MB，gzip 约 1.38 MB；构建有 chunk 大小提示。
- `.tools/dotnet/dotnet run --project server/Nordicandia.StoreTests`：退出码 0，**179 条 PASS**。现有 MessagePack 依赖仍有 NuGet 已知漏洞提示，非本次新增问题。
- 原有 `npm run smoke --prefix web`：通过；9 kills、179 XP、1 件已穿戴装备、精通由 0 变为 1。
- 独立浏览器刷新：角色 ID 一致，经验 `255.1438 → 268.1438`（期间战斗继续），装备数量 `1 → 1`。这验证了普通刷新保留数据，不代表服务重启后的属性计算正确。
- 目视检查：3D 场景与头像 token 可见，主要环境为无贴图深色材质；同时打开背包和技能树会发生面板重叠、底层文字透出。后者是界面改进项。
- 现有 Boss 测试通过，但采用加强后的测试角色；本次浏览器冒烟没有证明正常新角色完成 Boss 通关。
- 未进行 Pixel 9 真机性能测试、断电事务测试、生产部署、所有职业技能与原客户端逐项对照。它们不应在本次报告中被计为通过。

本机证据（均位于 git 忽略的 `tmp/`）：

- `tmp/web-review-build.log`、`tmp/web-review-tests.log`
- `tmp/web-acceptance/smoke.log`、`smoke.png`、`world.png`、`browser.json`
- `tmp/web-acceptance/probe/Program.cs`：独立回归探针；运行 `.tools/dotnet/dotnet run --project tmp/web-acceptance/probe`。
- `tmp/web-acceptance/probe.log`：R1/R2/R4/R5 复现结果。使用临时存储，结束后自动删除。

建议交回实现 Agent：先修 R1/R2/R3，再修命令去重与结果缓存，补齐上述反例的回归测试后重验 M1–M2。现有测试只覆盖顺序正常路径，不能用「179 PASS」替代边界验收。
