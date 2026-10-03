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

## 未完成（E04）

* **分解产出量**：`GameStore.DisassembleItems` 仍用网页近似
  `Max(1, floor(Σ(1 + rarity×0.5)))`；客户端的分解语义（`GetDisassemblableAffixes`
  + `GetMergableAffixesIfDisassembled`）疑似**抽取词缀**而非产出 Iron，需进一步反汇编确认。
* **`GetCraftingCost`**：结构为 `概率 × 25 × …`，依赖 `GetSpawnProbabilityForAffixOnItem`
  与多层属性插值，未逐项确认。
* **`GetNumTitansteelSmeltingOutput`**、`GetNumSteelSmeltingOutput` 的精确材料计数与取整。
* 可分解/可合并/可熔炼的静态名称 `HashSet` 未提取。

## 未完成（E05）

* **商店价格**：`MerchantCatalog` 的银币/蛋白石价为手工设定；客户端未见 `GetBuyPrice`/
  `GetSellPrice`，价格疑由 `Items.json.TradeValueMultiplier`（29 条）与商店定义驱动，
  需定位商店数据源。
* **祝福价格**：`Blessings.OpalCost` 200/500/800/2500 来自 `LocalCatalog.cctor`，客户端
  标注为 "TODO-PRICE: seed"，生产数据导出后可能变化（Interim）。
* **商店刷新 / 过滤 / 药水效果**：刷新规则与 `LootFilter` 过滤未核对。
* **经济验证口径**：需用**正常角色**成长曲线测收支，不能用改币账号（清单要求）。
