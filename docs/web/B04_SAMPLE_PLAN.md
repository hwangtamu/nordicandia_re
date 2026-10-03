# B04 对照样本清单与采集计划

任务 ID：B04。日期：2026-10-03。基线：Android 1.9.3，见 [B01](B01_VERSION_BASELINE.md)。

样本库：`server/Nordicandia.StoreTests/samples/fidelity_samples.json`
（schema：`{id, kind, rule, inputs, expected, source, status}`，顶层 `schemaVersion` + `baseline`）。
回放：`FidelityReplayTests.cs`（`Run(kind, sample)` 分发）。添加流程见 [B04_B05_REPLAY.md](B04_B05_REPLAY.md)。

## 清单（按 backlog B04 优先级：命中、毒、分叉、Buff、掉落、离线）

| # | kind | 样本 | 采集方式 | 状态 |
|---|---|---|---|---|
| 1 | `hit_chance` | ChanceToHit 边界：(100,100) 公式值、(0,100)→0.05 floor、(100000,1)→1.0 cap | 反汇编公式 `Game.Calculator.CalculateChanceToHit`（已有测试） | ✅ 本轮实现 |
| 2 | `damage_reduction` | PhysicalDamageReduction：(100,1)→100/150 | 反汇编公式 `CalculatePhysicalDamageReduction`（已有测试） | ✅ 本轮实现 |
| 3 | `poison_dot` | 毒：命中伤害 20% 转 PoisonDps（`PoisonHitDamageFactor=0.2`）；tick 按秒结算、不暴击不分叉 | 反汇编 `DebuffPoisoned.DoWork` + `HitPayload`（B03 已确认 1s/20%） | ✅ 本轮实现 |
| 4 | `blessing` | 祝福：4 档时长 10m/30m/1h/4h + 幅度 0.4 | 反汇编常量 `WindowAesirOffering.CreateAesirBuffOffline`（M3 已验证） | ✅ 本轮实现 |
| 5 | `rarity_weights` | MF 权重 | 已有 | ✅ 已有 |
| 6 | `damage_conversion` | 伤害转换/归一化 | 已有 | ✅ 已有 |
| 7 | `resistance_penetration` | 穿透 | 已有 | ✅ 已有 |
| 8 | `offline_rate` | 离线 killsPerMinute | 已有 | ✅ 已有 |
| 9 | `brain_choice` | Brain 加权选择 | 已有 | ✅ 已有 |
| 10 | `item_quantity` | 掉落数量余数进位 | 已有 | ✅ 已有 |
| 11 | `skill_param` | 技能数值 | 已有 | ✅ 已有 |
| 12 | `damage_order` | miss→dodge→block→crit 顺序 | 已有 | ✅ 已有 |
| 13 | — | 分叉/链的弹体碰撞 | 需客户端实机行为（录像/手工记录） | ⏳ 待用户采集 |
| 14 | — | Buff 叠加/替换/刷新/驱散/死亡移除 | 需客户端实机行为 | ⏳ 待用户采集 |
| 15 | — | 掉落资格/词缀分布（统计类） | 需大量客户端掉落样本，比较分布与边界（非逐次） | ⏳ 待用户采集 |
| 16 | — | 地图生成约束/分布 | 需客户端多图采样 | ⏳ 待定 |
| 17 | — | 怪物属性缩放 | `Monster.CalculateAttributes` 未采样 | ⏳ 待反汇编 |

## 实机采集需求（待用户）

在原版客户端（Android 1.9.3）中录制或记录：
- 13：带 Fork 被动的 Multishot 对 3+ 怪的分叉落点/伤害数字
- 14：同一毒源重复命中（刷新还是叠加）、驱散/死亡后毒是否清除
- 15：固定 MF 下连续 50 次击杀的掉落件数/稀有度（记数即可）

## 版本绑定

所有样本 `baseline` = `android-1.9.3 (507033)`；跨版本样本需另起文件，
不得混入同一 `fidelity_samples.json`。
