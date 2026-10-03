# E01 完整物品/词缀目录

日期：2026-10-03。对应 [完整复刻任务清单](FULL_FIDELITY_BACKLOG.md) 的 **E01**。
基线 Android 1.9.3（见 [FIDELITY_BASELINE](FIDELITY_BASELINE.md)）。

## 数据来源（已核对）

`gamedata_decrypted/ItemAffixes.json`（626 条，**物品**词缀定义，带 `TagData`/`ItemAffixDependencies`）
是掉落生成使用的目录；`Affixes.json`（1837 条）是含怪物/区域在内的大表。按 `GenerationType`
（客户端 `AffixType`：Prefix=0、Suffix=1、Implicit=2、Set=3、Unique=4）：

| 来源 | 总数 | Prefix | Suffix | Implicit | Set | Unique | 未知 |
|---|---:|---:|---:|---:|---:|---:|---:|
| ItemAffixes.json | 626 | 60 | 37 | 494 | 12 | 9 | 14 |
| Affixes.json | 1837 | 69 | 43 | 1351 | 43 | 317 | 14 |

随机掉落词缀 = Prefix+Suffix = **97**（ItemAffixes），其中按 `Domain`（0=Item、2=Area、4=Monster）
分为：Item **56**、Monster 27、Area 14。只有 Item 域会滚到装备上。

## 已提取数据

`tools/web-content/export_item_affix_catalog.py` →
`server/Nordicandia.Server/GameData/affix_catalog.json`（嵌入）与
`generated/affix_catalog_report.json`。每条含：

* 身份：`guid`、`integerId`、`name`、`generationType`、`domain`、`group`（AffixGroups.json 名称）。
* 授予属性：每个 `AttributeId` + 名称 + **逐稀有度** `{min,max,valueType}`（多属性词缀逐条保留）。
* 标签权重：`TagData` 的 tag 名称 + `Weight` + `ValueMultiplier`。
* 依赖：`ItemAffixDependencies`。

**属性 id 表补齐**：客户端 int/bool 属性用 `GameAttributeI..ctor`，原提取器只解析
`GameAttributeD..ctor`，漏掉 254 条（含 `Area_Monsters_Fire_Additional_Projectiles` 等）。
修正 `extract_attribute_ids.py` 后 id 表 **927 → 1181**，E01 全部词缀引用 **0 未解析**
（唯一未解析的 tag `18800088-…` 属于 Area 域，`Tags.json` 未定义，已记为已知项）。

## 运行时接入

* `AffixCatalog.cs`：条目改为**多属性**（`Attributes`），带 `Domain`/`Group`/`Guid`/`IntegerId`/
  `Tags`；`RollAll` 逐属性滚值。
* `LootTable.CreateItem`：候选池 = `IsPrefixOrSuffix && Domain==0` 的**全量** Item 词缀；
  每个 affix 的所有属性都写入物品，`SerializedAffix.DefinitionIntegerId` 改用词缀定义 id
  （原先用主属性 id，多个词缀共享主属性时会误判重复）。
* `CharacterAttributeEngine.TryGetName`：新增 id→名称反查，供引用校验。

## 原版对照

* `E01AffixCatalogTests`（10 条）：目录大小 97、Item 域 56、多属性存在、名称唯一、
  全部属性 id 可反查、掉落只用 Prefix/Suffix、affix-definition id 唯一。
* B04 样本：`affix_catalog.LocalBaseFireDamage`（多属性 2513,2514）、`affix_catalog.LifePercent`。
* 服务端 **643 PASS**，回放 **58/58**。

## 槽位资格（E01 收尾 / E02 起步，已接入）

`ItemTypes.json` 的 `TagIds`（tag）与 `AffixIds`（显式词缀）沿 `ParentTypeId` 继承。
导出器为每个 item-domain 词缀计算 `eligibleTypes`：词缀 guid 在类型的 `AffixIds` 中，
或词缀 `TagData` 的 tag 与类型的 tag 相交。56/56 item-domain 词缀都有可滚类型
（如 `LocalArmorPercent` 仅 Heavy* 7 类；`LifePercent` 可滚 Amulet/Chest 等）。
`AffixCatalog.Affix.EligibleFor(itemType)` + `LootTable` 按定义 `Type` 过滤候选池。

## 未完成 / 后续

* `TagData` 的 `Weight`/`ValueMultiplier`（词缀在类型内的权重与数值倍率）已提取但**未用于加权抽取**；
  归 **E02**（掉落统计分布）。
* Implicit/Set/Unique 目录（494/12/9）**未纳入运行时目录**（implicit 已由 `ItemCatalog` 的
  implicit 定义覆盖）——归 **E03**（独特/套装/隐式与开放词缀槽）。
* `Domain` 2/4 的 27/14 条仅作为目录留存，不参与装备掉落。
