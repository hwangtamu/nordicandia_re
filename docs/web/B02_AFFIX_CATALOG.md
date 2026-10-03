# B02 词缀目录 — 全量 1837（验收分母）

任务 ID：B02（词缀部分）。原版定义 ID：`gamedata_decrypted/Affixes.json`（1837 条）。
日期：2026-10-03。基线：Android 1.9.3，见 [B01](B01_VERSION_BASELINE.md)。
判据：[B02_DEPRECATION_RULES.md](B02_DEPRECATION_RULES.md)。

## 目标版本与证据

GenerationType 语义经客户端反汇编确认（见 `AffixCatalog.cs` 注释，
`ItemAffixDefinition.IsPrefixOrSuffix` ARM64 `0x02CECFA8`）：
Prefix=0，Suffix=1，Implicit=2，Set=3，Unique=4。

机读目录：`tools/web-content/generated/affix_catalog_full.json`
（生成脚本 `tools/web-content/export_affix_catalog_full.py`，可复现）。

## 分母

| GenerationType | 数量 | 说明 |
|---|---|---|
| Implicit（2） | 1351 | 按装备类型分的隐式词缀变体（大量重名，见下） |
| Unique（4） | 317 | 独特物品词缀 |
| Prefix（0） | 69 | 随机词缀-前缀 |
| Suffix（1） | 43 | 随机词缀-后缀 |
| Set（3） | 43 | 套装词缀 |
| 未知（None） | 14 | GenerationType 缺失，**待核实** |
| **合计** | **1837** | |

废弃条目：0（无 `_UNUSED` 命名；`Hidden` 字段不存在于词缀定义）。

## 重要结构发现

1. **115 个重名**：如 `LocalImplicitBasePhysicalDamage`、`MagicFindPercent`、
   `LifeGranted` 等同名多 Guid，实为按装备类型/稀有度拆分的 implicit 变体
   （不同 ValueRange）。**不是废弃，是同一词缀的多个实现变体**；
   网页 `affix_catalog.json` 按名键存储，18 个精选键对应源中 26 个 Guid。
2. **138 个地图词缀**：授予属性 id ≥ 2700（区域修饰符属性段），如
   `AreaMonsterDamage`、`AreaMonsterMoreBosses` 等 20 个 `AreaMonster*` 命名。
   它们是 W06（Niflheim 世界修饰符）的弹药，不进装备词缀池。
3. **14 个 GenerationType=None**：如 `CastSpeedPercent`、`LocalTwoHandBase*Damage`
   系列，看起来是正式词缀但缺类型标注 → 待核实（需反汇编确认默认值语义）。
4. 精选 18 覆盖：15 Suffix、8 Unique、2 Prefix、1 Implicit（按 Guid 计 26）；
   随机词缀（Prefix/Suffix）全量 112，精选仅 17 → E02 词缀池缺口。

## 网页接入位置

- `server/Nordicandia.Server/WebApi/AffixCatalog.cs` ← `GameData/affix_catalog.json`（18 精选）。
- `LootTable.cs` 用 `IsPrefixOrSuffix`（0/1）判定随机词缀。
- 1351 implicit 变体、138 地图词缀、14 未知类型尚未接入。

## 尚存差异

- Domain 语义（0=1656、1=69、2=67、4=45）未从客户端确认，暂未用作筛选。
- GroupId：1730 条为空，107 条有值但与 `AffixGroups.json`（60 条）对不上 →
  语义待查（疑为互斥组而非外键），不作废弃信号。
- 14 条未知类型的词缀需反汇编确认。

## 状态：已定位 / 已还原（目录）/ 待接入（112 随机词缀缺口、138 地图词缀）/ 对照未通过

复现：`python3 tools/web-content/export_affix_catalog_full.py`
