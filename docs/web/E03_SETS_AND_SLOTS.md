# E03 独特/套装/隐式与开放词缀槽

日期：2026-10-03。对应 [完整复刻任务清单](FULL_FIDELITY_BACKLOG.md) 的 **E03**。
依赖 E01、C02。基线 Android 1.9.3。

## 套装（已核对并修复）

`ItemSets.json`（11 套）为每套定义按**装备件数**的 breakpoint（`NumItems` = 2/4/6/9），
每个 breakpoint 引用若干套装词缀（`Affixes.json` 中 `GenerationType=3`），其
`DefaultValueRange` 为固定加成值。

* 之前 `set_catalog.json` 有 16 个属性名为 null（属性表漏采）。修正 `extract_attribute_ids.py`
  解析 `GameAttributeDA..ctor` 与 `r8` 载入的名称后，属性表 **1181 → 1242**，套装 51 条加成
  **全部解析**（0 未解析）。
* `tools/web-content/export_set_catalog.py` 现为正式导出器（套装词缀取自 `Affixes.json`，不是
  `ItemAffixes.json`），输出 `generated/set_catalog.json` 与嵌入的 `GameData/set_catalog.json`。
* `SetCatalog.ActiveBonuses(setId, pieces)` 在 breakpoint 处叠加；`GameStore.GetAttributeMap`
  **每次从已装备物品重算**，所以脱装后无残留加成。

## 验证

* `E03SetAndSlotsTests`（5 条）：11 套、set 0 的 2/4/6/9 断点、只在断点生效、高阶叠加、
  51 条加成名称全解析。
* `AcceptanceRegressionTests.SetBonusesAndEquipRequirements` 新增：2 件套生效、**脱装后加成消失**。
* 服务端 **662 PASS**，回放 58/58。

## 未完成（开放词缀槽 / prefix-suffix 容量）

* **开放词缀槽**：`Open_Prefix_Slot`(389)/`Open_Suffix_Slot`(390) 已定位；客户端
  `GameAttributes.IsOpenAffix(gameAttribute)` 用一个静态列表判定，`ItemType.get_AddOpenAffix()`
  返回 `[+0x1F0]` 字段，`Item.FillOpenAffixSlotWith` 填槽。该机制与符文/镶嵌类制作相关，
  **未接入**。
* **prefix/suffix 容量**：网页在精华合成里仍用 `2 + clamp(rarity/3,0,4)` 近似
  (`GameStore.CraftEssenceItem`)；客户端的真实容量规则（`GetMergableAffixes` 的
  `IsPrefixOrSuffix + IsOpenAffix`）尚未完全恢复。
* **独特触发效果**：9 条独特词缀（`GenerationType=4`）已作为 implicit 进入物品属性，但
  `ChanceToBleed` 等**触发效果**（Proc）未实现。
