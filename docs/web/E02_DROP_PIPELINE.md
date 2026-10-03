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

## 词缀稀有度池（已恢复并接入）

`ItemGenerator.InternalInitializeAffixRarityPool`（0x02CA2468）已反汇编恢复，证据脚本
`tools/web-content/recover_affix_rarity.py` 校验二进制 SHA + 指令段 + `FMOV` 常量。曲线：

* F/E：`base/(1+MF)`；D：`base*(1+MF*(factor>=10?.1:1))`
* C–S：`base*(1 + .01*(100*MF)*(coefficient*factor)/((100*MF)*slope+divisor*coefficient*factor))`
  系数 `C(1000,.22,1) B(800,.4,1) A(700,.6,1) AA(600,.65,1) AAA(500,.6,1.4) AAAA(400,.6,1.9) AAAAA(225,.5,3) S(150,.6,4.5)`
* 后续 roll 偏置：低于最高已出稀有度 `/(1+.025*affixNumber)`，≥ 时 `*(1+.02*rarity*affixNumber)`，
  且 highest≥D 时低于者再 `/max(|rarity-highest|*2.25,1)`

`AffixRarityCatalog` 实现该池；`LootTable` **逐词缀**滚自身稀有度（不再用物品稀有度近似）
并据此取 `ValueRangeByRarityList`。同时删除网页自造的 unique pity 与随机 set-id roll。
`EconomyRecoveryTests` 含独立十进制 goldens、D 阈值、2.25 距离惩罚、禁用饱和因子避免 0/0、
30 万次种子分布检验与真实掉落验证。

## 未完成 / 待办

* **TagData 权重聚合**：`GetSpawnWeights…ByTag` 的逐 tag 聚合（sum vs max）未确认，当前 best-match（Interim）。
* **物品类型抽取**：`LootTables.ItemTypeWeights` 与职业乘子已提取但未接入（当前用 `ItemCatalog` 的槽位类型均分）。
* **怪物/容器掉落资格**：`LootTables`/`MonsterDefinition` 的掉落表绑定未恢复。
* **统计分布对照**：词缀稀有度已做种子分布检验；物体类型/怪物掉落分布仍待对照。
