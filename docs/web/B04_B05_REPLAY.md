# B04/B05 对照样本库与确定性回放

日期：2026-10-03。对应 [完整复刻任务清单](FULL_FIDELITY_BACKLOG.md) 的 **B04**（原版对照样本库）
与 **B05**（回放与差异报告）。基线见 [FIDELITY_BASELINE.md](FIDELITY_BASELINE.md)。

## 样本库

`server/Nordicandia.StoreTests/samples/fidelity_samples.json`（嵌入测试程序集）。每个样本是：
固定输入 + 期望输出 + 来源 + 状态。已覆盖：

| kind | 样本 | 来源 |
|---|---|---|
| `rarity_weights` | MF 权重 0.3 / 1.0 / 100 / factor ×2 | `ItemGenerator.InternalInitializeSetOrUniqueItemRarityTypes` |
| `damage_conversion` | 20%火+30%冰拆分、>100% 归一化 | `ApplyWeaponDamageConversion` |
| `resistance_penetration` | 0.5 − 0.75 = −0.25 | `*_Resistance_Penetration_Total` |
| `offline_rate` | tier 1/10/30 | `WindowWelcomeBack.Start` |
| `brain_choice` | Standard 战斗/游荡 | `Brains.json` + `StringHashHelper` |
| `item_quantity` | +50% 余数 1,2,1,2 | `DeathPayload.Apply` |

## 回放与差异报告

`server/Nordicandia.StoreTests/FidelityReplayTests.cs`：

* 从嵌入资源加载样本，逐个运行 `Run(kind, sample)`；
* 每个样本打印 `PASS fidelity[<id>]` 或计入失败；
* 末尾汇总 `fidelity replay: N/M pass`，任一失败即让测试套件失败。

运行：

```bash
cd server
DOTNET_ROOT="$PWD/../.tools/dotnet" ../.tools/dotnet/dotnet run --project Nordicandia.StoreTests
```

## 添加样本

1. 在原版固定输入下得到观察输出（录像/手工记录/反汇编常量），记录到 `source`。
2. 在 `samples/fidelity_samples.json` 加一条，`status` 先写 `已还原`。
3. 跑回放；通过后把 `status` 改为 `对照通过`，并在 [B03 差异台账](B03_DIFFERENCE_LEDGER.md) 把对应条目降级为已对照。

## 现状与后续（2026-10-03 更新）

* 样本库 30 条（21 + 新增 9）：`hit_chance`（公式/下限/上限）、`damage_reduction`、
  `poison_dot`（20% 系数；状态`已还原`，行为仍是"最高 DPS 加刷新"近似，见 D07）、
  `blessing`（4 档时长 + 幅度 0.4）。回放 `fidelity replay: 30/30 pass`，全套件 exit 0。
* 样本清单与采集计划见 [B04_SAMPLE_PLAN.md](B04_SAMPLE_PLAN.md)。
* 仍缺（需实机采集，见清单 #13–#17）：分叉/链弹体碰撞、Buff 叠加/替换/驱散、
  掉落与词缀分布（统计类）、地图生成、怪物缩放。
