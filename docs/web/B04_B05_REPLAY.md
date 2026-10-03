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

## 现状与后续

* 已覆盖命中之外的基础规则；**技能/精通、Buff 叠加/驱散、掉落与词缀、地图生成、怪物缩放** 尚无样本。
* 这些是 C01–C06、E01–E02、W01–W04 的下一步：每移植一个规则/技能，先落样本再改实现。
* 无法控制原版随机种子的规则（掉落、词缀、地图），回放比较**条件分布/边界**而非逐次抽签；
  当前样本只收确定性规则，分布类样本待补。
