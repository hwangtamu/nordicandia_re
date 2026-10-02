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

## 元数据用法（metadata-usage）解析尝试与结论

- 已从补丁版 APK 提取 `tmp/global-metadata.dat`；`android-metadata.json` 的 `addressMap.stringLiterals`
  给出 36771 条字面量（含上述 55 条 `*Reason_*` 键）及其 `.so` 缓存槽地址（如 `0x58A0A68`）。
- 这些槽内容是 IL2CPP metadata-usage 令牌（`0xA0003AFF` 等，kind=5=StringLiteral）。
- **但代码不直接引用这些槽地址**：全 `.so` 内对相关页 `0x58A0000` 只有 2 处 `adrp`（均属 Firebase/Unity，
  与制作无关）；令牌值也只出现在数据段（`0x56740B0`），代码段内没有立即数引用。
- 结论：该 release 构建通过间接机制（`Il2CppCodeGenModule` 的字符串字面量表）引用，需 Il2CppDumper
  级别的解析器才能把"某指令 → 字面量"。因此**条件 → 理由键的逐条映射暂未完成**；规则条目本身已完整枚举。

## 制作操作 → 校验理由（ClientVerified，已打通字符串解析）

通过 `tools/web-content/resolve_il2cpp_strings.py` 解析：GOT 槽（文件内为 0）→ ELF `.rela.dyn`
重定位 addend → metadata-usage token 地址 → `stringLiterals` 文本。由此把每个制作操作的 `CanCraft`
（基类 `Artifact.CanCraft` + 子类追加条件）解析出它会返回的理由：

| 制作操作（`GetCraftingOperationName`） | 校验理由（`CraftingReason_*`，去掉前缀） |
|---|---|
| `Artifact`（基类） | `TypeNotEligable`、`NotEnoughMaterials`、`CannotCraftOnImbued`、`Ready` |
| `AddAffix` / `AddOpenAffix` | `NumAffixesAlreadyMaxed` |
| `RemoveAffix` | `Remove_NotEnoughAffixes` |
| `ReRollAffixTypes` / `ReRollExplicitAffixValues` | `HasNoNonImplicitAffixes` |
| `ReRollImplicitAffixValues` | `HasNoImplicitAffixes` |
| `ReRollNumAffixes` | `BlacksmithRerollNumAffixesWarning` |
| `Link` | `LinkNotEnoughAffixes`、`TooHighRarity` |
| `Reinforcement`（基础） | `NoImplicits`、`ReinforcementMaxed` |
| `MeleeReinforcement` | `NotMelee` |
| `BowReinforcement` | `NotRanged` |
| `StaffReinforcement` | `NotStaff` |
| `ShieldReinforcement` | `NotShield` |
| `LightReinforcement` | `NotLightArmor` |
| `MediumReinforcement` | `NotMediumArmor` |
| `HeavyReinforcement` | `NotHeavyArmor` |
| `JewelryReinforcement` | `NotJewelry` |
| `HeatingStone` | `CannotApplyLowerRarityAffix`、`CannotUpgradeMaxRarity`、`CanOnlyHaveOneSSRarity`、`CannotUseMultipleHeatingStones`、`MissingMergableItem` |
| `Anvil` | `CannotUseMultipleAnvils` |
| `ImbuingEssence` / `ImbuingEssenceEnchanted` | `BlacksmithImbueWarning` |
| `RelicOfBlessing` | `BlessingMaxed`、`NotEquippable` |
| `RepairDurability` | `ItemHasNoDurability`、`AlreadyRestored` |
| `Rune` | `TypeNotEligable`、`CannotCraftOnImbued`、`Ready` |

制作操作名集合：`AddAffix`、`AddOpenAffix`、`Bless`、`Imbue`、`Link`、`ReforgeItem`、`Reinforce`、
`RemoveAffix`、`Reroll`、`Restore`。

条件逻辑也可读（例：`Artifact.CanCraft` 中 `Item.get_IsImbued(targetItem)` 为真且 `!allowOnImbued`
→ `CannotCraftOnImbued`；否则如前所述）。注解版 ISIL 见
`tmp/cpp2il-annotated/Items/Implementations/*.txt`（用 `--annotate-out` 生成）。

## 精华/加热石制作公式（ClientVerified，`HeatingStone` 实现）

来自 `Game.Items.Implementations.HeatingStone`（`0x02CC…`）：

```
GetHighestChanceToSucceed(highestMergeRarity) = highestMergeRarity < 10 ? 0.25 : 1.0
GetNumIronCost(sourceItems, essence, targetItem, baseCost, qualitySlider, overheatSlider)
    = (int)(overheatSlider * baseCost * 0.5)
```
- `ProcessSuccessRate` 在此基础上再乘 `值 × 常量(0x…838) × 上述几率`（`FMUL`），并在参考实现中设置
  `secondarySuccessRate`。
- `getCostFromRarity(rarity) = 1`，例外：`8→2`、`9→3`、`10→10`（`MOV/CINC/CSEL` 链）。
- `GetNumItemsCost = (目标可合并词缀数相关值) × overheatSliderValue`（`Multiply`；与 Titansteel 同式，
  内部先 `CraftingUtils.GetMergableAffixes` 取可合并词缀数）。

**已实现**：`GameStore.CraftEssenceItem` 消耗 Iron（`GetNumIronCost`）、按 `GetHighestChanceToSucceed` 掷骰、
成功时把源物品词缀并入目标（简化词缀合并，容量 = `2 + rarity/3`）；`InventoryService.CraftEssenceItem(2)` 已接线。

**已实现**：`GameStore.CraftRelicItem`（消耗源遗物 + 祝福目标词缀）；`GameStore.ItemAddNewSocket`
（`GetRequiredTitansteelReagentsForAddingSockets = 现有槽数+1`）；`GameStore.SocketItem`（把源宝石嵌入空槽，
`SerializedSocket.SocketedItem`）；`InventoryService` 三个 RPC 已接线。

**仍缺**（因此 `TradeWithSetItemMerchant`/`GenerateSetItemMerchantOffers`/`MergePortals`/`LockItems` 仍为空桩）：
- 精华制作的成功/失败产出物品与 `ChangedIronInstances`/`Tools` 结构；
- `getCostFromRarity` 的逐稀有度消耗表；
- 遗物制作（`CraftRelicItem`）的目标物品来源与产出；
- 镶嵌（`SocketItem`/`ItemAddNewSocket`）的插槽与消耗规则；
- 套装商人（`TradeWithSetItemMerchant`）的换购规则。

这些需要继续用 Cpp2IL+字面量工具逐操作解出产出与消耗（不建议靠猜实现）。
