# E04 / E05 制作流程与 NPC·商店·资源经济

日期：2026-10-03。对应 [完整复刻任务清单](FULL_FIDELITY_BACKLOG.md) 的 **E04/E05**。
基线 Android 1.9.3。制作公式明细见 [CRAFTING_EXTRACTED](CRAFTING_EXTRACTED.md) 与
[NPC_TRADE_EXTRACTED](NPC_TRADE_EXTRACTED.md)。

## 已还原并接入（ClientVerified）

| 操作 | 公式/规则 | 证据 |
|---|---|---|
| 精华合成 | 成功率 `overheat×0.1×(rarity>9?1.0:0.25)`；费用 `getCostFromRarity`（1，8→2，9→3，10→10）| `HeatingStone`、`CraftingUtils.GetCraftingCost` |
| 精华→钢熔炼 | 系数表 rarity 2→0.05 … 10→10.0，`Max(1, floor(sum))` | `GetEssenceToSteelSmeltingValueFromRarity` @0x02C834FC |
| 加插槽 | `numExistingSockets + 1` 个 Titansteel | `GetRequiredTitansteelReagentsForAddingSockets` |
| 镶嵌加热 | `numAffixes × Max(1, overheat)` 个 Titansteel | `GetHeatingStoneRequiredTitansteelReagentsForSSCraft` |
| 祝福 | 消耗 Relic，逐词缀 `Affix.Bless` | `RelicOfBlessing.InternalCraft` |
| 分解资格 | 非唯一且非套装 | `GetDisassemblableItems` |
| 套装换购 | `TradeWithSetItemMerchant` 原子交割 | SetItemMerchant |
| 商人 | 银币/蛋白石购买 + 以物易物 | `MerchantCatalog`/`TradeWithMerchant` |

已有回归：`AcceptanceRegressionTests`（`SmeltAndDisassembleConsumeSources`、
`CraftEssenceConsumesIron`、socket add/insert、relic bless）、`ConsumeItemTests` 等，覆盖
**失败/重试不丢失或复制物品**（源物品快照 + 原子变更）。

## 分解与精华（已恢复并接入）

`tools/web-content/recover_essence_disassemble.py`（对齐 affix-rarity 的做法：校验二进制 SHA +
指令段 + 断言）恢复了：

* `CraftingUtils.GetDisassemblableAffixes` @ `0x02C82770` —— **确定性过滤**，无成功率：
  前后缀（`IsPrefixOrSuffix`）、`Rarity >= 2`（谓词 `b__14_0` 的 `cmp w8,#2`）、
  属性含 `IsOpenAffix`；affix 的授予物品读 `GameAttributeMap.Linked`（offset `0x2C8`）。
* `EssenceAffixId`（`ItemDefinitionData` serialized 字段）—— **60 个精华物品**（`Items.json`）
  全部解析到词缀名（0 未解析），导出为 `GameData/essence_affix_catalog.json`。
* `EssenceGrid.CreateEssences` @ `0x02506C7C`（UI 分组）与 `EssenceLootboxSmall.
  InternalOnRequestUse` @ `0x02CD44C4`（唯一随机步骤，`Max_Num_Essence_Added_Affixes_On_Item` 198）。

`EssenceCatalog` 加载目录；`GameStore.DisassembleItems` 改为**按词缀产出精华**（不再产 Iron）。
`E04EssenceTests` 4 条 + `AcceptanceRegression` 更新。

## 未完成（E04）

* **`GetCraftingCost`**：结构为 `概率 × 25 × …`，依赖 `GetSpawnProbabilityForAffixOnItem`
  与多层属性插值，未逐项确认。
* **`GetNumTitansteelSmeltingOutput`**、`GetNumSteelSmeltingOutput` 的精确材料计数与取整。
* 可分解/可合并/可熔炼的静态名称 `HashSet`、`IsOpenAffix` 的具体属性集未逐条导出。

## 未完成（E05）

* **商店价格**：`MerchantCatalog` 的银币/蛋白石价为手工设定；客户端未见 `GetBuyPrice`/
  `GetSellPrice`，价格疑由 `Items.json.TradeValueMultiplier`（29 条）与商店定义驱动，
  需定位商店数据源。
* **祝福价格**：`Blessings.OpalCost` 200/500/800/2500 来自 `LocalCatalog.cctor`，客户端
  标注为 "TODO-PRICE: seed"，生产数据导出后可能变化（Interim）。
* **商店刷新 / 过滤 / 药水效果**：刷新规则与 `LootFilter` 过滤未核对。
* **经济验证口径**：需用**正常角色**成长曲线测收支，不能用改币账号（清单要求）。
