# E04 / E05 制作流程与 NPC·商店·资源经济

日期：2026-10-03。Android 1.9.3 / 507033。
本轮代码与反汇编复核详见 [E03–E05修复报告](E03_E05_RECOVERY_2026-10-03.md)。
**两项仍为部分完成**，公式恢复、目录导出和运行时完整接入分开计数。

## 已接入的子项

* 钢：`min(floor(铁/300), floor(精华价值总和))`。
* 钛钢：`min(floor(钛矿/10), floor(钢/100))`。
* 精华按最高稀有度计值，2…11系数0.05/0.1/0.15/0.2/0.25/0.5/1/3/10/50；
  按升序取用并退还可由超额覆盖的低价值精华。
* 产量不足不扣料、不产生物品；部分堆叠和无关源物品保留，重启/重复请求已测。
* 原有加插槽 `sockets+1`、镶嵌入口与合成基础仍在；新词缀只填同类型开放槽。
* 60条 `EssenceAffixId` 已导出，分解有精华输出，但**输出属性与概率未保真**。
* 24条离线商店价格恢复；5种网页药水已使用该目录，替换手填价格。

## E04 已恢复但未形成完整流程

`CraftingUtils.GetCraftingCost @0x02C85244` 的稀有度表和公式在
`CraftingCostCatalog` 中，但实际 `GameStore.CraftEssenceItem` 尚未调用它。
既有铁费用/过热成功率仅覆盖合成子集，不代表完整制作校准完成。

分解必须继续追踪 `TabBlacksmithDisassemble.PerformDisassembleOffline`：
它包含 `Calculator.CalculateChance`、`ExtractAffixEssence @0x029ABFE8` 与
独特/套装输出分支。`GetDisassemblableAffixes` 无随机只说明筛选器确定性，
**不能推出分解保证成功**。

`ExtractAffixEssence` 的**精华重建已接入**（本轮）：`EssenceCatalog.CreateItem` 会按
`new Affix(affixDefinition, targetRarity, source=3)` + `Item.AddAffix` 给精华附着
来源词缀（原代码是固定 C、空 Affixes）；`DisassembleItems` 传入来源词缀定义与稀有度。
**仍未完成**：`PerformDisassembleOffline` 的 `CalculateChance` 概率、`ReduceItemLevelOfEligibleAffixes`
降级开关与独特/套装输出分支。

`RelicOfBlessing.ShouldBless/Affix.Bless` 仍未实现实际数值效果，
网页目前只写标记99010。合成来源资格、reroll/耐久/失败处理仍待校准。

此前所谓“待提取静态名称HashSet”不是已确认的阻塞：
`CraftingUtils.cctor` 实际初始化稀有度系数字典；熔炼资格走 `Item.IsOfType`。
此前 `Max(1, floor(...))` 也是误读中间材料提示，已按真正返回寄存器纠正。

## E05 未完成

* `LocalCatalog` 是离线基线，不是生产服动态目录；生产报价/刷新/过滤需同版服务器响应。
* Niflheim传送门不在恢复的离线商品目录中，其网页报价仍为Provisional。
* 7 种宠物药水（GrowthElixir…LegendaryGrowthElixir、FamiliarReviveElixir，Items 628–634）已按恢复的离线报价接入商人目录（网页现 13 个商品）；其**宠物成长/复活行为**属 P03/P04，尚未实现。
* Aesir既有seed不能当作生产价验证；药水限购/持续效果仍需完整对照。
* 以物易物目前仍依赖请求堆叠数，服务端真实估值待恢复；套装换购生成仍需原版对照。
* 正常角色成长收支验证未完成，改币账号测试不构成经济验收。

## 验证范围

`E03E05RecoveryTests` 覆盖开放槽类型、同定义升级、熔炼临界值/超额退料/最高词缀稀有度、
钛钢配比、实际GameStore余料/重开存档、网页药水运行时价格。
`AcceptanceRegressionTests` 更新为实际材料配方。
这些回归不替代完整原版实机制作/商店对照。
