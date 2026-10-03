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

## 难度差异系数（以稀有度区分，**ClientVerified**）

`CalculateAttributes`（0x02A6D000）选择 `finalMult`：

```
finalMult = (Rarity == 6) ? [0x13880D8]      // Boss
          : (Rarity == 4) ? table[1]          // Champion
          :                 table[0]          // Normal / Magic / Rare
```

表值直接读自模块常量池（`CalculateAttributes` 0x02A6D0FC–0x02A6D128）：

| 稀有度 | Rarity | finalMult | 模块地址 |
|---|---|---|---|
| Normal / Magic / Rare | 0 / 1 / 2 | **0.8** | table[0] @0x1385BA0（= `get_FinalStatsMult()`） |
| Champion | 4 | **0.88** | table[1] @0x1385BA8 |
| Boss | 6 | **1.2** | @0x13880D8 |

即精英/Boss 相对普通怪物的整体属性倍率为 **1.10x / 1.50x**，非常克制（不是数量级差异）。

### 攻击间隔（同批常量，附带还原）

`CalculateAttributes` 另用一组 min/max 乘子 `v531`/`v532` 生成攻击间隔
`v1518 = Rand.RangeExclusive(minMax(v531,…), min(v532×…, 16))`，写入
`Item_Attack_Speed_MainHand`，并以 `1/v1518` 作为每秒攻击次数。分支常量：

| 稀有度 | v531 | v532 |
|---|---|---|
| Normal | 1.4 | — |
| Rare | 1.6 | 3.4 |
| Champion | 1.8 | — |
| Boss | 1.2 | — |

（tier 缩放：`max(v531*(1+(tier-1)*0.005),1)` … `min(v532*(1+(tier-1)*0.04),16)`。）
该组已解码但未接入网页的攻击间隔，标记 Interim。

## 接入状态

* `server/Nordicandia.Simulation/MonsterScaling.cs` —— 8 条曲线 + `RarityFinalMult`。
* **已接入战斗**：`CombatInstance.CreateMonster` 用 `MonsterScaling` 计算
  `MaxHp`/`Offense`/`Armor`/`Evasion`/`AttackRating`；`CreateBoss` 用 rarity 6。
  `CombatRegistry` 的 archetype 不再有手工 `HpMult/OffenseMult/DefenseMult`（保留字段仅作
  召唤等特例的偏移），只携带客户端本就有的差异：伤害分布/抗性/远程/Brain/稀有度。
  `MonsterStats` 现把 `AttackRating`/`Evasion` 传给 `CombatantStats`（命中判定按客户端）。
* B04 样本：`monster_base_stats.level10/.level60`、`rarity_final_mult.normal/.champion/.boss`。
* 未接入：每 spawn 的 `Rand(0.96,1.01)`/`Rand(0.99,1.01)` 方差（会扰动共享 RNG，暂用均值）；
  攻击间隔 rarity 曲线（Interim）。

## 复现

```bash
.tools/web-assets-venv/bin/python tools/web-content/export_monster_scaling.py   # 生成目录
cd server && DOTNET_ROOT="$PWD/../.tools/dotnet" ../.tools/dotnet/dotnet run --project Nordicandia.StoreTests
```
