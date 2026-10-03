# C01 伤害管线校准

日期：2026-10-03。对应 [完整复刻任务清单](FULL_FIDELITY_BACKLOG.md) 的 **C01**（校准完整伤害管线）。
基线 Android 1.9.3。差异登记见 [B03](B03_DIFFERENCE_LEDGER.md) D02/D06。

## 客户端结算顺序（`AttackPayload.Resolve`）

```
Miss（MissPayload） → IsEvaded（命中/闪避判定） → IsDodged → IsBlocked → IsCriticalHit
→ HitPayload.Apply（转换 → 护甲/抗性减伤 → 穿透/放大 → 结算 → 死亡触发）
```

证据：`AttackPayload.txt` `IsEvaded`@2640、`IsDodged`@3802、`IsBlocked`@4141、`IsCriticalHit`@4480；
`Resolve` 中按此顺序调用。`HitPayload..ctor` 带 `ignoreArmor/ignoreResistances/ignoreForceField` 标志。

## 属性（已复原）

| 机制 | 属性 | 偏移 |
|---|---|---|
| 命中率 | `AttackRating_Total` / `Evasion_Total` / `Hit_Chance_Bonus_Percent` / `Hit_Chance_Cap` | 0x610/0x770/0x608/0x680 |
| 必中 | `Always_Hits` / `Always_Hits_Global` | 0x5C8/0x5D0 |
| 闪避 | `Dodge_Chance_Total` / `Dodge_Chance_Spell_Total` | 0x1310/0x1338 |
| 格挡 | `Block_Chance_Total` / `Block_Chance_Spell_Total` | 0x1278/0x1298 |
| 格挡减伤 | `Blocked_Damage_Taken_Multiplier_Total`（基线默认 0.5） | 0x12D8 |
| 暴击免疫 | `Ignores_Critical_Hits` | 0x1168 |
| 元素穿透 | `*_Resistance_Penetration_Total` / `Armor_Piercing_Percent_Total` | 98/99/113/114, 420 |

## 已接入（本轮）

* `ResolveBundleAttack` 按 **miss → dodge → block → crit** 结算（`CombatantStats` 新增 `DodgeChance`/`BlockChance`/`BlockedDamageMultiplier`）。
* `IsEvaded` 使用 `Hit_Chance_Bonus_Percent` 与 `Hit_Chance_Cap`；`Always_Hits`/`Always_Hits_Global` 跳过命中判定。
* `Ignores_Critical_Hits` 阻止暴击。
* `AttackProfile` 新增 `IgnoreArmor`/`IgnoreResistances`/`IgnoreForceField`，结算时按标志绕过护甲/抗性。
* 元素转换/穿透（前几轮已接）。
* B04 样本：`damage_order.dodge`、`damage_order.block`；测试覆盖 dodge/block/crit 免疫/必中/ignore 标志。

## 未完成

* **Evade 与 `ChanceToHit` 的命名**：网页把 `IsEvaded` 当作"命中率"，未单独验证 `MissPayload`（距离/视线）与 `IsEvaded` 是否还有第二层；`Always_Hits` 已接。
* **Deadly strike**：已接入。`Deadly_Strike_Chance_Total`（1205 = `Pin(AxeAny_CurrentHand,0,1) * Deadly_Strike_Chance_With_Axes`）在暴击后 roll，成功则伤害 ×`Constants.Deadly_Strike_Crit_Multiplier`=**2.0**。测试：150→300。
* **Glancing hit**：是 `DebuffGlancingHit`（一个可叠加的 **DoT debuff**，`_AddInstance(dps, damageType, duration)`，`Base_Glancing_Hit_Debuff_Duration` 580），由特定 power/buff 施加，不是通用攻击机制；当前网页内容没有任何 power 创建它，因此标记为**当前内容不适用**（待相关 power 移植时再补，属 W03/P02）。
* **Force field**：`IgnoreForceField` 标志已加但网页护盾在 `ApplyPlayerDamage` 单独处理，未按标志绕过。
* **受伤方的 `IsEvaded`/`IsDodged` vs 攻方的 `IgnoreArmor`**：只做了单次命中判定，未逐技能验证。

## 复现

```bash
cd server
DOTNET_ROOT="$PWD/../.tools/dotnet" ../.tools/dotnet/dotnet run --project Nordicandia.StoreTests
```
当前 **479 PASS**；回放 21/21。

---

## C01 第二轮（2026-10-03）：管线顺序定死 + 放大接入

### 完整管线顺序（`CombatModel.ResolveBundleAttack`）

| # | 步骤 | 证据等级 | 说明 |
|---|---|---|---|
| 1 | 命中掷骰 | ClientVerified | `AlwaysHits` 或 `CalculateChanceToHit`（1.05·atk/(pow(def/2,0.75)+atk)，钳制 [0.05,1]，`Hit_Chance_Cap`） |
| 2 | 闪避 | ClientVerified | `Dodge_Chance_Total`（0x1310） |
| 3 | 格挡掷骰 | ClientVerified | `Block_Chance_Total`（0x1278）；倍率后乘（`Blocked_Damage_Taken_Multiplier_Total` 0x12D8） |
| 4 | 暴击 | ClientVerified | `Ignores_Critical_Hits`（0x1168）免疫；致命打击（0x1C90，×2）仅暴击后掷骰 |
| 5 | 技能/暴击倍率 | ClientVerified | skillMultiplier × crit × deadly，作用于转换前 |
| 6 | 伤害转换 | ClientVerified | 0x02BABF3C+0x02BAC1F8；总量超 1 时归一化 |
| 7 | 穿透 | ClientVerified | 攻击方穿透减防御方抗性；可为负（易伤增伤） |
| 8 | 护甲减伤 | ClientVerified | `armor/(armor+50·damage)`，上限 0.9；护甲穿透降低有效护甲 |
| 9 | 元素减伤 | ClientVerified | 抗性先钳制 1.0，再 `ApplyDamageReduction`（0x02BAF34C）；**无取整**（反汇编 6 条指令，无 frint/lrint） |
| 10 | 承受放大 | **Inferred** | `Amplify_Damage_Taken_Percent`（attribute 6，如 Mark of the Chosen）；防御方，减伤后乘区，位置为推断 |
| 11 | 格挡倍率 | Inferred | 步骤 3 的掷骰结果在此应用；与方差的先后为推断 |
| 12 | 方差 | Provisional | `1 ± variance` 均匀抖动 |
| 13 | 最低下限 | Provisional | `Max(1.0, …)`；客户端下限待反汇编确认 |

### 本轮变更

- `CombatantStats` 新增 `DamageTakenAmplifyPercent`（attribute 6）。
- 步骤 10 落子：`mitigated × (1+amplify/100)`；`DamageConfidence` 新增
  `AssumedAmplifyPlacement=true`。
- 吸血：客户端是完整 Buff 子系统（`LifeLeechBuff`/`LeechInstance`，属性 313–317
  按伤害类型、`Base_Life_Leech_Rate` 等），**未实现**，归 C02（Buff 生命周期）。

### 新增样本（39/39 pass）

| 样本 | 覆盖 |
|---|---|
| `dmg.amplify-taken` | 放大：+50% 承受 → 100→150 |
| `dmg.zero-floor` | 零伤害：0.5 伤害经护甲 → 下限 1.0（注：物理减伤上限 0.9，故用小额伤害触发下限） |
| `dmg.no-rounding` | 取整：97.5 伤害经 33% 抗性 = 65.32499999999999（无取整） |
