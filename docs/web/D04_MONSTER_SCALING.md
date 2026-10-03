# D04 怪物基础数值与成长公式

日期：2026-10-03。对应 [完整复刻任务清单](FULL_FIDELITY_BACKLOG.md) 的 **D04**（怪物成长），
替换 `CombatRegistry` 中手工调参的 `HpMult/OffenseMult/DefenseMult`。
基线 Android 1.9.3（见 [FIDELITY_BASELINE](FIDELITY_BASELINE.md)）。

证据：`tmp/cpp2il-isil2/IsilDump/Assembly-CSharp/Game/Monster.txt` 的 `Get*` 方法；
所有数值常量直接从 `tmp/apk-libil2cpp.so` 的常量池读出（首段 `vaddr==offset==0`，VA 即文件偏移）。

## 总览

客户端怪物没有任何 per-monster 基础数值（`gamedata_decrypted/Monsters.json` 只有
`TypeId/Name/AvailableRarities/BrainId/Size/DamageType`，`MonsterTypes.json` 只有 `Size`）。
所有战斗数值都由**等级曲线**生成：

```
stat = (a*level + b*level^(e*expMult) + c) * finalMult [* k]
```

* `expMult` —— 世界/全局"经验/难度"倍率，来自一个运行时配置字段（未能静态解析），
  以参数形式暴露，默认 **1**（Provisional）。
* `finalMult` —— `Monster.FinalStatsMult`，按稀有度选择。
* `level` —— 生成点的怪物等级。

## 逐项公式（ClientVerified）

| 属性 | 方法 | 地址 | 公式 |
|---|---|---|---|
| Life | `GetBaseLife` | 0x02A7A780 | `rand(0.99,1.01) * (level*0.41 + level^(1.315*expMult)*0.12 + 8) * 2 * finalMult` |
| Armor | `GetArmor` | 0x02A7A884 | `(level*150 + level^(1.40*expMult)*0.01 + 80) * finalMult` |
| Evasion | `GetEvasion` | 0x02A7A930 | `(level*8 + level^(1.28*expMult)*0.006 + 4) * finalMult * 0.25` |
| MinAttackRating | `GetMinAttackRating` | 0x02A7A584 | `(level*1.2 + level^(1.20*expMult)*0.005 + 100) * finalMult * 0.5` |
| MaxAttackRating | `GetMaxAttackRating` | 0x02A7A630 | `(level*1.2 + level^(1.20*expMult)*0.005 + 100) * finalMult * 0.8` |
| WeaponDamage（min/max 同底） | `GetMinWeaponDamage`/`GetMaxWeaponDamage` | 0x02A7A424 / 0x02A7A4D4 | `(level*0.35 + level^(1.24*expMult)*0.01) * finalMult * 2` |
| Experience | `GetExperience` | 0x02A7A6E0 | `(level^(1.33*expMult)*0.1 + 16) * 0.88 * 0.6`（**无** finalMult） |
| ForceField | `GetForceField` | 0x02A7A9DC | `rand(0.99,1.01) * (level*0.4 + level^(1.30*expMult)*0.2 + 50) * 2 * finalMult` |
| Resistance | `GetResistance` | 0x02A7AAD4 | 常数 **0.05** |
| MaxPhysicalDamageReduction | `GetMaxPhysicalDamageReduction` | 0x02A7AAE0 | 常数 **0.9** |
| FinalStatsMult（默认） | `get_FinalStatsMult` | 0x02A770E0 | 常数 **0.8** |

> `GetMinWeaponDamage` 与 `GetMaxWeaponDamage` 在反汇编里除末尾字面量外**完全一致**；
> 两者末尾字面量在常量池里都是 `2.0`，因此实际相同。伤害区间由调用方
> `CalculateAttributes` 里的 `Rand` 决定，不是这两个方法本身。
>
> `GetForceField` 的地址以后续精确反汇编为准（与 `GetEvasion` 相邻）。

### 常量池（`tmp/apk-libil2cpp.so` 中读出的 double）

| 地址 | 值 | 用途 |
|---|---|---|
| 0x1387430 | 1.315 | life 指数倍率 |
| 0x1387D18 | 0.41 | life 线性系数 |
| 0x1387220 | 0.12 | life 幂系数 |
| 0x1387918 | 1.4 | armor 指数倍率 |
| 0x1387878 | 150.0 | armor 线性系数 |
| 0x1386FE0 | 0.01 | armor/weapon 幂系数 |
| 0x1387920 | 1.28 | evasion 指数倍率 |
| 0x1387210 | 0.006 | evasion 幂系数 |
| 0x1387698 | 1.2 | attack-rating 指数倍率 |
| 0x13885F0 | 0.005 | attack-rating 幂系数 |
| 0x1386DE0 | 1.24 | weapon-damage 指数倍率 |
| 0x1387148 | 0.35 | weapon-damage 线性系数 |
| 0x1387BA0 | 1.33 | experience 指数倍率 |
| 0x1387838 | 0.1 | experience 幂系数 |
| 0x13876A0 | 0.88 | experience 乘子 |
| 0x13874D0 | 0.6 | experience 乘子 |
| 0x1388008 | 1.3 | force-field 指数倍率 |
| 0x1387678 | 0.2 | force-field 幂系数 |
| 0x1387368 | 0.4 | force-field 线性系数 |
| 0x13875D8 / 0x1387390 | 0.99 / 1.01 | life/force-field 方差区间 |

## 难度差异系数（以稀有度区分，**Inferred**）

`CalculateAttributes`（0x02A6D000，ISIL 951 行）按 `Rarity` 分支设置一组 min/max 乘子
`v531`/`v532`，随后按 tier 缩放：

```
tier = Calculator.GetTierFromMonsterLevel(level)
minMult = max(v531 * (1 + (tier-1)*0.005), 1)
maxMult = min(v532 * (1 + (tier-1)*0.04), 16)
value   = Rand.RangeExclusive(minMult, maxMult)     // 某条属性的随机区间
```

已解析的 `v531`/`v532` 候选（分支映射尚未完全确认，标记 **Inferred**）：

| 稀有度 | Rarity | v531 | v532 | 备注 |
|---|---|---|---|---|
| Normal | 0 | 1.4 | — | 默认分支 |
| Magic | 1 | ？（分支未定） | ？ | |
| Rare | 2 | 1.6 | 3.4 | |
| Champion | 4 | 1.8 | — | 另有 `IsCaster` 时 ×0.5 |
| Boss | 6 | 1.2 | ？（`FinalStatsMult` 另表） | |

> 分支到稀有度的精确映射、以及 `v531`/`v532` 各自对应的属性（life vs damage）**尚未确认**，
> 因此**不接入运行时**。要接入必须先逐条确认，再补 B04 样本。这也符合清单规则：
> 不推测实现未还原的逻辑。

`finalMult` 的选择（反汇编 0x02A6D10C–0x02A6D128）：
```
finalMult = (Rarity == 6) ? BossTable
          : (Rarity == 4) ? table[1]
          :                 table[0]
```
`get_FinalStatsMult()` 本身返回 **0.8**（默认/非 Boss 分支的近似），Boss/Champion 表值待解。

## 接入状态

* `server/Nordicandia.Simulation/MonsterScaling.cs` —— 上述 8 条曲线 + 常数，供运行时调用。
* B04 样本 `monster_base_stats.level10` / `.level60`（回放对照通过）。
* **未接入** `CombatRegistry`：现有 archetype 仍用手工 `HpMult/OffenseMult/DefenseMult`。
  用真实曲线替换会影响战斗平衡与 smoke，需作为独立改动 + 平衡回归。

## 复现

```bash
.tools/web-assets-venv/bin/python tools/web-content/export_monster_scaling.py   # 生成目录
cd server && DOTNET_ROOT="$PWD/../.tools/dotnet" ../.tools/dotnet/dotnet run --project Nordicandia.StoreTests
```
