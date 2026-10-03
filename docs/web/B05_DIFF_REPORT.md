# B05 回放与差异报告

任务 ID：B05。日期：2026-10-03。依赖：B04（样本库）。

## 做了什么

1. **回放器重构**（`server/Nordicandia.StoreTests/FidelityReplayTests.cs`）：
   `EvaluateAll()` 返回逐样本 `SampleResult(Id, Kind, Ok, Actual, Error, Source, DiffId)`，
   `Run()` 保留原有控制台行为。每个 kind 的 handler 同时产出实际值字符串。
2. **差异报告生成器**（`server/Nordicandia.StoreTests/FidelityReport.cs`）：
   `dotnet run --project Nordicandia.StoreTests` 后在 `server/fidelity-reports/` 生成
   - `fidelity_report.json`（机器可读：逐样本 id/kind/status/actual/error/source/diffId）
   - `fidelity_report.md`（人工可读：汇总 + 结果表 + 失败样本的 B03 关联）
   
   失败样本优先关联 `diffId`（B03 台账编号），无则回退到 `source` 证据文档。
   报告目录已加入 `.gitignore`（生成物，用 StoreTests 重新生成）。
3. **新增 6 个样本**（36/36 pass），覆盖 B05 验收的四类路径：

| 样本 | 类别 | 内容 |
|---|---|---|
| `edge.resist-cap` | 边界 | 抗性 150% → 按 1.0 上限计，元素伤害为 0 |
| `edge.conv-overflow` | 边界 | 转换总量 150% → 归一化（火 60/冰 40/物理 0） |
| `edge.immune-crit` | 免疫 | `IgnoresCrits` 目标：必暴攻击也不暴击 |
| `edge.immune-dodge` | 免疫 | 闪避 100%：`AlwaysHits` 攻击也被闪避 |
| `edge.replay-deterministic` | 重复命令 | 同种子同输入重放两次，结果完全一致 |
| `edge.relogin-blessing` | 重登 | 祝福到期时间 JSON 落盘/恢复往返一致 |

## 运行

```bash
cd ~/workspace/nordicandia/repo/server
DOTNET_CLI_TELEMETRY_OPTOUT=1 ~/dotnet-sdk/dotnet run --project Nordicandia.StoreTests
# 报告输出到 server/fidelity-reports/
```

## 状态：已完成（36/36 pass，报告框架就绪）
