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

## 开放槽填充（本轮已接）

`OpenAffixSlots` 已替换 `CraftEssenceItem` 的虚构容量：按定义1076/1077、属性389/390
识别开放前后缀槽，新词缀替换同类型槽位，同定义更高稀有度可升级。无空槽不能增加条数。
证据与验证见 [E03–E05反汇编复核](E03_E05_RECOVERY_2026-10-03.md)。

## 未完成

* 掉落中的开放槽生成（`forceOpenAffix/canRollOpenAffix`、`AddOpenAffix`）尚未接入。
* 独特词缀已作为隐式属性进入物品，但 `ChanceToBleed` 等 Proc 未完成。
* 合成完整资格、重投数值、耐久和失败处理仍待按原版调用链校准。
