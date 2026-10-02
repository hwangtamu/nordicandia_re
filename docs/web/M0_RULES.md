# M0 规则可信度（2026-10-02）

本项目要求：规则数据必须标注来源可信度，不能因为 JSON 里存在定义就认为效果已还原。
可信度分级：**ClientVerified**（客户端已观测并逐字复原）、**Inferred**（由数据/文档推断，未校准）、**Provisional**（占位实现，待校准）。

## 经验曲线 — ClientVerified

来源：`docs/CLIENT_XP_DROP_RATES.md` 与客户端 `Game.Calculator` / `GameAttributes.Constants`。

```
ExperienceForLevel(L) = L <= 1 ? 0 : 350 + 20 * L^1.7      // 累计经验
LevelForExperience(x) = floor(round(((x - 350) / 20)^(1/1.7), 6))   // 闭式反解，无等级上限
```

实现（单一实现，服务端与客户端共用语义）：

* 服务端：`server/Nordicandia.Simulation/CombatModel.cs` 的 `Progression`；`server/Nordicandia.Server/State/Progression.cs` 现在只是转发。
* 客户端：`web/src/combat.ts` 的 `experienceForLevel` / `levelForExperience`。
* 测试：`server/Nordicandia.StoreTests/WebM0Tests.cs` 对 2–500 级做往返校验。

## 战斗伤害 — Provisional

客户端真正的伤害管线尚未复原（`Monster.CalculateAttributes` / 属性表达式仍未逐条校准）。M0 先用客户端可见的
`CombatStats.Offense / Defense / Recovery` 建一个**可复现**的最小样本，供后续对照：

```
Mitigation(def, atkLevel) = def / (def + 50 + 10 * max(1, atkLevel))
ExpectedDamage            = max(1, Offense * skillMult * (1 - Mitigation)) * critFactor
Damage                    = max(1, mitigated * jitter * critFactor)      // hit
MaxHealth(level)          = (100 + 40 * max(1, level)) * (1 + 0.10 passive)  // Provisional
ExperienceReward(level)   = (8 * level^1.35 + 5) * multiplier              // Provisional
Skills (M2)               = 0 Strike ×2.4 cd4 | 1 Nova AoE r5.5 ×1.5 cd8 | 2 Rally +30%hp & +25%off 6s cd20
Passive (M2)              = +10% Offense, +10% MaxHealth
Rarity weights (M2)       = Droprates.json 真实权重 F..SS（ClientVerified）
```

* 实现：`server/Nordicandia.Simulation/CombatModel.cs`（权威）与 `web/src/combat.ts`（仅供人读的镜像）。
* M1 起战斗由服务端 `CombatInstance` 以固定步长（0.05 s）模拟并持久化，浏览器只发送意图、渲染状态；`web/src/combat.ts` 不再是运行路径。
* 随机数：`CombatRandom`（SplitMix64，固定种子可复现）。固定样本见下。

### 固定样本（回归基线）

* 输入：`Offense=100, Defense=0, Level=10` 攻击 `Defense=50, Level=10`，
  `AttackProfile(1.0, critChance=0.25, critMult=2.0, variance=0.10)`，`seed=1`。
* 输出：`damage=75.998424, crit=false, confidence=Provisional`。
* 该样本在 `WebM0Tests` 中打印；公式一旦改动会改变输出，需同步更新文档与校准记录。

### 校准计划（M0 之后）

1. 采集原客户端固定输入下的 `-伤害` 飘字与 `Offense/Defense/Recovery`。
2. 反推优质/命中/暴击/减伤公式，将本文件的置信度从 Provisional 提升为 Inferred/ClientVerified。
3. 只有校准后，服务端才可对战斗结算与奖励负责；M0 客户端结算仅用于演示，不写入存档。

## 经验奖励归属

服务端始终以持久化的累计经验为权威（`GameStore.SaveRealtimeProgress` / `ProjectProgress`），
等级是纯函数、不单独存储。网页端 M0 的经验增长仅在浏览器内演示；接入存档写入属于 M1/M3。

## 内容版本

客户端 `web/public/assets/content.json`、导出清单 `manifest.json` 与服务端 `/api/web/v1/health`
的 `contentVersion` 均为 `m0-1`；网页启动时会比对三者，不一致时在 HUD 上提示。
