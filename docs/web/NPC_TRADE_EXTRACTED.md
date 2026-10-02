# NPC / 商人 / 交易 / 分解 / 熔炼（客户端解析）

日期：2026-10-02。用 Cpp2IL（v39）+ `resolve_il2cpp_strings.py`（GOT→重定位→字面量）解析客户端。

## RPC 契约（来自 `dump.cs` 的 DTO）

| RPC | 请求字段 | 响应字段 |
|---|---|---|
| `TradeWithMerchant` | `CharacterId`、`CatalogId`、`CatalogItemId`、`TradePercentage`(double)、`PlayerOfferValue`(int)、`ExpectedNumProductStacks`(int) | `YourOfferItems`、`InventoryItems` |
| `TradeWithSetItemMerchant` | `CharacterId`、`ItemId` | `ResultItem` |
| `GenerateSetItemMerchantOffers` | `CharacterId` | `YourOfferItems`、`OfferedItems` |
| `CraftEssenceItem` | `IronHintEntries`、`CharacterId`、`SliderValue`、`OverheatSliderValue` | `CraftEssenceItemResponse` |
| `CraftEssenceItem2` | `IronHintEntries`、`CharacterId`、`SliderValue`、`OverheatSliderValue` | `CraftEssenceItemResponse2` |
| `CraftRelicItem` | `CharacterId` | `CraftRelicItemResponse` |
| `DisassembleItems` | `CharacterId`、`AdvancedDisassembly`、`AutoDiscardLowRarity`、`ReduceItemLevel` | `DisassembleItemsResponse` |
| `SmeltItems` | `CharacterId` | `SmeltItemsResponse` |
| `SocketItem` / `ItemAddNewSocket` | （见 `dump.cs:58192/58223`） | — |

服务端现状：`TradeWithMerchant`/`TradeWithSetItemMerchant`/`CraftRelicItem`/`CraftEssenceItem(2)`/
`DisassembleItems`/`SmeltItems`/`SocketItem`/`ItemAddNewSocket` 仍是 **`Defaults.Create` 空桩**；
只有 `BuyMerchantItem`（银币/蛋白石购买）与 `OfferingService`（祝福）已实现。

## 客户端流程

- **商人交易**：`WindowTrader.UpdateTradingValue`（`0x…`）遍历 `InventoryYourOffer.Items` 与
  `_ProductItems`；每件物品取 `ItemDefinition.TradeValueMultiplier`，累加到 `_TotalPlayerTradingValue`；
  `TradePercentage = Max(0, total²) / Max(<常量>, productValue)`（`0x2EC…` 段，含 `Math.Max`、平方、除法）；
  `OnTradeClicked`（状态机）调用 `NetClient.TradeWithMerchant(CharacterId, CatalogId, CatalogItemId,
  TradePercentage, PlayerOfferValue, ExpectedNumProductStacks)`。
  - `PlayerOfferValue` = 玩家放入物品的价值总和；`TradePercentage` 由滑条/价值比决定。
- **报价物品槽**：`ItemSlotTypes.YourTrade = 31`、`HisTrade = 32`（`dump.cs:54227`）。玩家放入交易窗的
  物品存于 `YourTrade` 槽，服务端 `TradeWithMerchant` 应据此读取报价；`PlayerOfferValue` 即这些物品
  按 `ItemDefinition.TradeValueMultiplier` 折算的价值之和。
- **分解**：`TabBlacksmithDisassemble.PerformDisassemblyOnline` → `NetClient.DisassembleItems`；
  离线路径 `PerformDisassembleOffline` 用 `CraftingUtils.GetDisassemblableItems`。
- **熔炼**：`TabBlacksmithSmelting.PerformSmeltingOnline` → `NetClient.SmeltItems`；
  离线路径用 `CraftingUtils.GetSmeltableItems` + `GetNumSteelSmeltingOutput` / `GetNumTitansteelSmeltingOutput`。

## 物品筛选规则（`Game.Items.CraftingUtils`，`0x02C822E8` / `0x02C8311C`）

- **可分解 `GetDisassemblableItems`**：物品 `ItemDefinition.IsUnique == false`、`SetId == default`
  （非唯一、非套装），且 `ItemTypeDefinition.MergeCategory` 有效，并且 `GetDisassemblableAffixes` 非空
  （词缀：`Affix.IsPrefixOrSuffix()`、`Rarity >= 2`、不在排除集合内）。
- **可熔炼 `GetSmeltableItems`**：按 `Item.IsOfType(<ItemTypes 常量>)` 判定（静态常量位于
  `Il2CppStaticFields` `+0x21C/0x220/0x224/0x250`）。

## 已还原的制作公式（见 `CRAFTING_EXTRACTED.md`）

- `GetEssenceToSteelSmeltingValueFromRarity`：rarity 2→0.05 … 10→10.0。
- `GetHeatingStoneRequiredTitansteelReagentsForSSCraft = numAffixes * Max(1, overheat)`。
- `GetRequiredTitansteelReagentsForAddingSockets = numExistingSockets + 1`。
- 各制作操作 → 校验理由（`CraftingReason_*`）已逐条解析。

## 尚未完成

- `GetCraftingCost`（`0x02C85244`）逐项常量尚未确认。
- `GetNumSteelSmeltingOutput` / `GetNumTitansteelSmeltingOutput` 的精确取整与材料计数。
- 服务端空桩未实现。
