# NPC / 制作系统反汇编进展

日期：2026-10-02。目标：核查并继续还原 NPC 相关功能（商人、购买 buff、装备分解/制作/熔炼/镶嵌）。

## 状态总览

| 功能 | 服务端实现 | 客户端逻辑 |
|---|---|---|
| 商人**购买** | ✅ `GameStore.BuyMerchantItem`（`GameStore.cs:836`）+ `CatalogService.cs:151/163` | 数据驱动 |
| 购买祝福 / Aesir buff | ✅ `OfferingService` + `GameStore.MakeOffering`（`:995`）/`GetActiveBlessings`（`:1054`） | — |
| 商人换购 `TradeWithMerchant` / `TradeWithSetItemMerchant` | ❌ 空桩（`CatalogService.cs:254/258`） | 未反汇编 |
| 制作 `CraftRelicItem` / `CraftEssenceItem(2)` | ❌ 空桩（`Services.Generated.cs:92-94`） | 部分（见下） |
| 分解 `DisassembleItems` | ❌ 空桩（`:95`） | 部分（见下） |
| 熔炼 `SmeltItems` | ❌ 空桩（`:98`） | 部分（见下） |
| 镶嵌 `SocketItem` / `ItemAddNewSocket` | ❌ 空桩（`:99-100`） | 未反汇编 |

`Services.Generated.cs` 共 88 个 `Defaults.Create` 空桩，NPC 相关多在其中。

## 客户端方法清单（Android `libil2cpp.so`）

`Game.Items.CraftingUtils`（`dump.cs:103676`）：

| 方法 | Android 地址 |
|---|---|
| `GetMergableSourceItems/Item` | `0x02C7FDCC` / `0x02C7FEE8` |
| `GetDurabilities` | `0x02C7FF6C` |
| `GetMergableAffixes` | `0x02C80288` |
| `GetMergableAffixesIfDisassembled` | `0x02C813FC` |
| `GetDisassemblableItems` | `0x02C822E8` |
| `GetMergablePortals` | `0x02C82A64` |
| `GetSmeltableItems` | `0x02C8311C` |
| `GetEssenceToSteelSmeltingValueFromRarity` | `0x02C834FC` |
| `GetHeatingStoneRequiredTitansteelReagentsForSSCraft` | `0x02C835D4` |
| `GetRequiredTitansteelReagentsForAddingSockets` | `0x02C83644` |
| `GetNumSteelSmeltingOutput` | `0x02C8364C` |
| `GetNumTitansteelSmeltingOutput` | `0x02C84350` |
| `GetDisassemblableAffixes` | `0x02C82770` |
| `GetMergablePortalAffixes` | `0x02C82E28` |
| `GetCraftingCost` | `0x02C85244` |
| `cctor` | `0x02C8568C` |

## 已还原公式（ClientVerified）

### Titansteel 消耗

```
GetHeatingStoneRequiredTitansteelReagentsForSSCraft(numAffixes, overheatSliderValue)
    = numAffixes * Max(1, overheatSliderValue)

GetRequiredTitansteelReagentsForAddingSockets(numExistingSockets)
    = numExistingSockets + 1
```

### 精华 → 钢 熔炼系数（`GetEssenceToSteelSmeltingValueFromRarity`）

cctor 填充的 `Dictionary<Rarity, double>`（`0x02C8568C`）：

| Rarity | 值 |
|---:|---:|
| 2 | 0.05 |
| 3 | 0.1 |
| 4 | 0.15 |
| 5 | 0.2 |
| 6 | 0.25 |
| 7 | 0.5 |
| 8 | 1.0 |
| 9 | 3.0 |
| 10 | 10.0 |

### 钢熔炼产出（`GetNumSteelSmeltingOutput`，部分还原）

对每个 `IsOfType` 的材料取 `HighestRarity()`，累加其精华→钢系数，得到 `sum`；随后
`Min(floor(sum), floor(N / 300.0))` 并 `Max(1, …)`（`N` 与材料数量相关）。
字面量 `300.0` 位于 `0x1387140`。**精确的 `N` 组合与输出取整尚需复核。**

## 尚未还原

- `GetCraftingCost`（`0x02C85244`）：结构为
  `概率 × 25 × 属性(唯一/套装) × 属性 × 世界等级插值 ÷ 10`，再 `Max(…)×(1 + k/Max)`——
  依赖 `ItemGenerator.GetSpawnProbabilityForAffixOnItem`、`Calculator.GetTierAndInterpolatedWorldLevelFromMonsterLevel`
  与多个属性 id，尚未逐项确认。
- `GetDisassemblableItems/Affixes`、`GetMergableAffixes`、`GetSmeltableItems`：判定依赖静态
  `HashSet`（可分解/可合并的名称集合）与 `IsPrefixOrSuffix`、rarity≥2 等规则，集合内容未提取。
- `GetNumTitansteelSmeltingOutput`（`0x02C84350`）。
- 服务端空桩（换购、制作、分解、熔炼、镶嵌）未实现。

## 复现方式

```bash
.tools/web-assets-venv/bin/python - <<'PY'
# 见本文件“已还原公式”一节的地址；用 capstone 反汇编 tmp/apk-libil2cpp.so
PY
```
