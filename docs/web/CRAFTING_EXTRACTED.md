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

## 制作校验规则词汇（来自客户端字符串表）

从 `global-metadata.dat` 的字符串表提取（`android-metadata.json`→`stringLiterals`）。这些是客户端
`CraftingReason_*` / `MergeReason_*` / `SmeltingReason_*` 翻译键，直接枚举了制作/合并/熔炼的校验规则。

### 制作（Blacksmith / Craft / Essence / Relic / Socket）
```
CraftingReason_Ready / Crafting_Success / Crafting_Failed / Crafting_InProgress
CraftingOverheat_Success / CraftingOverheat_Failed / CraftingDurabilityDamage
CraftingReason_MissingSource / MissingTarget / MissingEssence / MissingMergableItem
CraftingReason_MissingSocketTarget / MissingTargetEssenceCraft / MultipleCraftingOperations
CraftingReason_NotEnoughMaterials / TooHighLevelRequired / TooHighRarity / TypeMismatch / TypeNotEligable
CraftingReason_NotEquippable / NotMelee / NotRanged / NotStaff / NotShield
CraftingReason_NotLightArmor / NotMediumArmor / NotHeavyArmor / NotJewelry
CraftingReason_HasNoImplicitAffixes / HasNoNonImplicitAffixes / NoImplicits / NumAffixesAlreadyMaxed
CraftingReason_CannotApplyLowerRarityAffix / CannotCraftOnImbued / CannotUpgradeMaxRarity
CraftingReason_CannotUseMultipleAnvils / CannotUseMultipleHeatingStones
CraftingReason_CannotUseReinforcementForEssenceCraft / CannotUseRelicForEssenceCraft
CraftingReason_CannotUseEssenceForRelicCraft / EssenceNotEligableOnType
CraftingReason_SourceTypeNotMergable / LinkNotEnoughAffixes / Remove_NotEnoughAffixes
CraftingReason_NoMatchingOrOpenSlotFound / NoMatchingAndRoomForNewAffixesFound
CraftingReason_TargetItemHasNoFreeSockets / TargetItemHasNoRoomForMoreSockets
CraftingReason_ReinforcementMaxed / AlreadyRestored / BlessingMaxed / CanOnlyHaveOneSSRarity
CraftingReason_ItemHasNoDurability
Crafting_SuccessRate / Crafting_SecondarySuccessRate / Crafting_MaterialUsed(WithTotal)
Crafting_Reagents_Drop_Weight_Bonus_Percent
```

### 合并（Merge，词缀/传送门）
```
MergeReason_Ready / MergeReason_Merging / MergeReason_CannotMergeMultipleTypes
MergeInstructions / MergePortals / DisassembleItemEstimation
```

### 熔炼（Smelting）
```
SmeltingReason_Ready / SmeltingReason_Smelting / SmeltingOutput
```

### 分解（Disassemble）
```
Disassemble / DisassembleItems / DisassembleWarning
Disassembler / Disassembler_Advanced / Disassembler_Auto_Discard
```

## 相关类型字段（用于读反汇编）
- `Game.Affixes.Affix`：`+0x20 = Rarity`、`+0x24 = AffixSource`、`+0x28 = AffixDefinition`、`+0x40 = Attributes`。
- `ItemAffixDefinition`：`+0x28 = GenerationType`、`+0x2C = Domain`、`+0x30 = GroupId`、`+0x48 = Name`、`+0x50 = Hidden`、`+0x51 = IsLocked`、`+0x58 = AttributeSpecifierDefinitionList`。
- 判定 lambda `_GetDisassemblableAffixes_b__14_0`（`0x02C86C40`）：`IsPrefixOrSuffix() && Rarity>=2 && !(集合判定)`；集合/委托来自 `CraftingUtils.<>c` 静态闭包字段（`+0x90` 起）。

## 原始元数据
`global-metadata.dat` 可从 `dist/android-arm64-online-patched/com.IterativeStudios.Nordicandia.apk`
（`assets/bin/Data/Managed/Metadata/`）提取，用于解析 IL2CPP 字符串字面量（`tmp/global-metadata.dat`）。
