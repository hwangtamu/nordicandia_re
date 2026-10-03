# E02 完整掉落生成管线

日期：2026-10-03。对应 [完整复刻任务清单](FULL_FIDELITY_BACKLOG.md) 的 **E02**。
依赖 C01、E01、B05。基线 Android 1.9.3。

## 管线顺序（已核对）

客户端 `ItemGenerator.CreateRandomItem → CreateItem → CreateItemWithType → GenerateRandomAffix`：

1. 掉落数量（`RollItemsPerDrop`，数量余数，已接入 abe00c4）。
2. **物品稀有度类型**（Normal/Unique/Set，`Droprates.ItemRarityTypeWeights` + MF 饱和，已接入）。
3. **物品类型**（`LootTables` 的 `ItemTypeWeights`，按角色职业乘 `ItemTypeCharacterClassWeightMultipliers`）。
4. **词缀数量**（`NumAffixesRatio`，已接入）。
5. **词缀抽取**：候选 = 该物品类型可滚的词缀（`GetSpawnableAffixesOnItem`），按 **TagData 权重**加权（`GetSpawnWeightsForItemTypesAndForAffixWithDomainAndAffixTypeAndTag`）。
6. **词缀稀有度**（`InitializeAffixRarityPool` + `Droprates.AffixRarityRatio`）决定取值区间。
7. 取值（`ValueRangeByRarityList` 按稀有度）× `TagData.ValueMultiplier`。

## 已提取数据

`Droprates.json`（5 段）经 `export_item_affix_catalog.py` 导出到
`generated/affix_rarity.json`：

* `ItemRarityTypeWeights` = Normal 10000000 / Unique 500 / Set 375（已接入）。
* `NumAffixesRatio` = 0:7000 1:1400 2:1400 3:600 4:600 5:300 6:300（已接入）。
* `AffixRarityRatio` = F:10000 D:10000 C:3000 B:1500 A:750 AA:250 AAA:100 AAAA:40 AAAAA:10 E:1100000 S:3。
* `ItemTypeCharacterClassWeightMultipliers`（type×职业）。
* `LootTables`：Default / Helheim / RegularBoss / Niflheim 的 `ItemTypeWeights`。

## 已接入（本轮）

* **Item-type 词缀资格**：`ItemTypes.TagIds`/`AffixIds`（含 `ParentTypeId` 继承）→ 每个词缀的
  `eligibleTypes`；`AffixCatalog.EligibleFor` + `LootTable` 按物品定义 `Type` 过滤。
* **加权抽取**：`AffixCatalog.Affix.SpawnWeight(itemTags)` 用 TagData 权重（best-match，
  无匹配回退 1000）；`LootTable` 改为按权重轮盘抽取。
* **ValueMultiplier**：`Affix.Affix.ValueMultiplier(itemTags)`（匹配 tag 的最大值）乘到滚出的数值。
* `item_type_tags.json`（type→继承 tag）嵌入。

## 原版对照

* `E02DropPipelineTests`（13 条）：tag 继承、资格 gating、TagData 权重（`Agility_Percent_Bonus`→500、
  `LocalArmorPercent`→1000）、无匹配回退 1000、ValueMultiplier、amulet 掉落 500 seed 全部词缀合格且带属性。
* 服务端 **656 PASS**，回放 58/58。

## 未完成 / 待办

* **词缀稀有度池**（`AffixRarityRatio` + MF）：`InternalInitializeAffixRarityPool` 的 MF/`affixNumber`
  公式尚未完全解码（已定位常量 `magicFind*0.1`、`affixNumber*0.025`、`magicFind*100` 与多处
  `weight*(1+…)/(p*slope+divisor*c)` 形态）；当前仍用**物品稀有度**近似词缀取值区间（Interim）。
* **TagData 权重聚合**：`GetSpawnWeights…ByTag` 的逐 tag 聚合（sum vs max）未确认，当前 best-match（Interim）。
* **物品类型抽取**：`LootTables.ItemTypeWeights` 与职业乘子已提取但未接入（当前用 `ItemCatalog` 的槽位类型均分）。
* **怪物/容器掉落资格**：`LootTables`/`MonsterDefinition` 的掉落表绑定未恢复。
* **统计分布对照**：需按固定种子对比词缀数量/稀有度/类型分布（B05 分布样本）。
