# M0–M2 修复复验与机制接入检查

日期：2026-10-02。最终检查基线：`e2e65f6`，包含修复提交 `9766a2f`。本次未修改业务实现，仅使用独立临时存档验证。

## 结论

**R1、R2、R3、R5 的原复现路径已通过；R4 尚未完整修复，且新增服务重启后的命令版本冲突。M1–M2 暂不整体签收。**

| 原问题 | 独立复验结果 |
|---|---|
| R1 装备重复计入 | 攻击加成 94；落盘基础 206，恢复前后均为 300，卸装后为 206。原问题不再复现 |
| R2 命令额外推进时间 | 冻结时间发送 200 条无效命令：冷却 30→30，击杀 0→0，经验 0→0 |
| R3 禁止注册失效 | 实际 HTTP：关闭注册后 `/session` 返回 403 `registration_disabled`；关闭 dev 后 `/dev/session` 返回 404 |
| R4 延迟重试 | 64 条窗口问题改善，但新 256 条窗口与版本判断仍可被正常格式的旧请求绕过，见下文 |
| R5 失败重试误报成功 | 首次和重试都返回 `false/cooldown` |

R1 此处确认的是新格式存档的恢复路径，不代表旧版本已经写入的膨胀属性得到迁移纠正。

## 仍需修复

### P1：持久化命令版本与重建实例版本冲突，服务重启后新操作被拒绝

位置：`server/Nordicandia.Server/WebApi/CombatRegistry.cs:123`、`server/Nordicandia.Server/State/GameStore.cs:767`、`server/Nordicandia.Simulation/CombatInstance.cs:181`。

命令日志已经持久化，但 `CombatInstance.Version` 重建时仍从 1 开始。服务重启后，客户端读取到的新快照版本会小于旧日志中保留的 `ExpectedVersion`，于是携带正确新版本的新命令也返回 `stale_command`。

独立复现：发送 300 条移动命令，每次使用最新返回版本 → Dispose `GameStore` → 从相同目录新建 `GameStore` 和 `CombatRegistry` → 读取新快照 → 发送全新 ID 的移动命令。

```text
retained-version-floor: 48, before-restart-version=304
fresh command after real reopen: observedVersion=1, result=False/stale_command
```

这不是旧请求重放，而是新客户端按服务器刚返回的版本操作。重新同步仍会得到低版本，无法立即解决。直到战斗版本超过旧门槛之前，技能、移动、穿戴等操作都会受影响。

修复建议：为实例引入持久化的单调版本，或引入实例 epoch 并显式区分旧 epoch 与新 epoch 的命令；不可将跨重启日志版本直接与从 1 重置的实例版本比较。

### P2：同版本旧命令被淘汰后仍会重新执行

位置：`server/Nordicandia.Server/WebApi/CombatRegistry.cs:123`、`server/Nordicandia.Server/State/GameStore.cs:777`。

实现将“最旧保留记录的客户端 ExpectedVersion”作为淘汰边界，但该字段由客户端携带，可以相同，也不保证按接收顺序严格增长。当前接口允许落后版本继续执行，因此不能据此判断某个命令 ID 是否已经被处理。

独立复现（**全部使用非零版本 1**）：穿 A → 穿 B → 发送 266 条不同 ID 的无效命令，仍使用版本 1 → 重放原穿 A 请求。

```text
R4 equal-version eviction: True/ok, Aequipped=True
```

此复现不依赖 `expectedVersion=0` 的特殊豁免。原请求与窗口边界相等，绕过 `< oldestExpected` 判断，B 被旧请求换回 A。

修复建议：命令去重使用独立的命令序列/有效期及服务端记录，状态版本仅负责状态并发；超出可重试窗口的命令需要能够可靠拒绝。明确拒绝空命令 ID 和不合法序列；增加同版本、乱序版本及真实重启的测试。

## 对最新反汇编进展的核查

已核对生成物数量：927 个属性 ID 映射、226 条公式字符串、247 个 getter 地址映射、145 个技能参数记录。此检查验证文件内容与接入关系，**没有重新逐条反汇编并独立校准全部公式**。

当前应区分三层：

1. **提取结果**：定义、常数、公式字符串、getter 地址及反汇编文档已大量补齐。
2. **函数实现**：`CombatModel.ChanceToHit`、`PhysicalDamageReduction`、`RollChance`，以及精通贡献计算已有对应实现与测试。
3. **完整战斗接入**：尚未完成。`CombatantStats` 仍仅包含 Offense/Defense/Recovery/Level；`ResolveHit` 把 Offense/Defense 同时当作命中/闪避评分和伤害/护甲输入，攻击暴击率仍由 `AttackProfile` 的常数传入。226 条属性合成公式尚未作为实际属性来源，闪避/格挡也尚未接入。

此外，`CombatModel.ResolveHit` 目前对整个 `DamageResult` 标记 `ClientVerified`，但组合流程仍包含假定的减伤上限、随机幅度和最低伤害 1。这个标签不能证明网页战斗整体与原客户端一致，建议把可信度细分到公式、输入来源和执行顺序。

`GAME_MECHANICS.md` 对缩放公式明确标注 Inferred，对元素抗性/穿透、DoT、AI、离线收益仍列为缺口。因此“提取取得了大量进展”成立；“只剩很小的确认工作”尚缺少端到端对照证据。

建议下一阶段：先修复上述命令边界，再实现一个职业的最小属性依赖链（基础属性、单件武器、护甲、命中、暴击），用原客户端固定样本验证从装备输入到最终伤害的全过程，然后再扩展到元素抗性与其余 getter。不要把 226 条公式的存在当作属性引擎已经实现。

## 验证记录

- 最新服务端套件：210 条 PASS；包含四职业 fresh-boss 检查。
- 最新网页生产构建：通过，仍有 bundle 大小提示。
- 最新浏览器冒烟：通过，10 kills、155 XP、1 件穿戴装备、精通 0→1；记录在 `tmp/web-recheck/latest-smoke.log` 与 `latest-smoke.png`。
- 独立原问题探针：`tmp/web-recheck/latest-original-probe.log`。
- 两项新边界探针：`tmp/web-recheck/probe/Program.cs`，运行 `.tools/dotnet/dotnet run --project tmp/web-recheck/probe`；输出 `tmp/web-recheck/latest-edge.log`。
- HTTP 注册策略实测使用独立 loopback 服务和临时存档，未访问生产账号。

完整原版一致性与 Pixel 9 性能不在此次复验通过范围内。

---

## 修复记录（后续提交）

针对本复验的 P1、P2 与可信度问题已修复：

### P1 修复：持久化单调版本

- `SavedCharacter` 新增 `CombatVersion`（持久化单调计数）；`CombatInstance` 构造函数新增
  `initialVersion`，`CombatRegistry.GetOrCreate` 用 `store.GetCombatVersion` 恢复。
- `FlushLocked` 在版本变化时也回调 `SaveRealtimeProgress(..., instance.Version)`，命令追加时
  经 `AppendCommand(..., combatVersion)` 抬升。重启后版本不再回退到 1。
- 测试 `CombatVersionSurvivesRestart`：真实 `Dispose` + 重开同目录 `GameStore`，新快照版本
  `>= beforeRestart`，全新命令被接受。

### P2 修复：服务端 stale floor

- `CommandRecord` 新增 `ProcessedVersion`（处理时的服务端版本）；`SavedCharacter` 新增
  `StaleFloorVersion`。
- 仅当命令记录**被淘汰**时，用其 `ProcessedVersion` 抬升 `StaleFloorVersion`；`LookupCommand`
  返回该 floor。淘汰后 ID 已不存在且 `expectedVersion < floor` 的命令被可靠拒绝。
- 保留同版本连发的宽限（floor 不随每条命令上涨），因此原有合法序列不受影响。
- 新增拒绝空命令 ID（`missing_command_id`）。
- 测试：`StaleCommandsAreRejected`（原路径）、`EqualVersionEvictedCommandIsRejected`
  （复验的等版本复现，如今被拒）、`MissingCommandIdIsRejected`。

### 可信度细分

- `DamageResult.Confidence` 由单一 `RuleConfidence` 改为 `DamageConfidence`，分层标注
  `Formula`（ClientVerified）/`Inputs`（Provisional）/`Execution`（Inferred），并显式
  `AssumedReductionCap`/`AssumedVariance`/`AssumedMinDamage`；`Overall` 取三者最弱。
- 因此不再把整个网页战斗标成 `ClientVerified`。测试断言公式层为 ClientVerified、输入层为
  Provisional、`Overall != ClientVerified`。

验证：服务端套件 **219 PASS**（含新增检查）。

### 仍未完成

- 属性→公式映射已完成：`extract_attribute_formulas.py` 产出全部 **226** 条（id/名称/公式），见
  `generated/attribute_formulas.json` 与 `GAME_MECHANICS.md`。仍缺基础属性来源与表达式求值器，
  即最小依赖链的输入侧。
- 元素抗性/穿透应用、DoT、AI、离线收益仍为缺口。
