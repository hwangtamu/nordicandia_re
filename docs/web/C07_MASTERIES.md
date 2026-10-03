# C07：精通系统（Masteries）

**日期**: 2026-10-03
**范围**: 297 个精通的前置、PerLevel/SpecificLevel、加减顺序、重置

## 数据现状

- 总精通: 297（B02 原写 318 系笔误，已修正）
- 有前置依赖: 247
- SpecificLevel (ModifierType=1): 21 个 spec
- Operator: 全部为 0 (Add)，无 Subtract/Multiply
- 互斥: 数据中无互斥字段

## 实现

### 1. 前置条件（Prerequisites）

- `server/Nordicandia.Simulation/MasteryDependencies.cs`: 静态映射
  IntegerId → 依赖 IntegerId[]，从 powers_full.json 生成（247 个有依赖）。
- `CombatRegistry.AllocateMastery`: 分配前检查所有依赖至少有 1 rank，
  否则返回 `missing_prerequisite`。
- **注意**: 曾尝试重新运行 export_powers.py 直接生成带依赖的
  Powers.generated.cs，但脚本输出 provisional 数据破坏了现有 client-verified
  数据，已回滚。改用独立静态映射文件，不碰生成文件。

### 2. PerLevel / SpecificLevel

`MasterySpec.ContributionForRank`（已存在，C07 验证）:
- PerLevel (ModifierType=0): `StartValue + Value * rank`
- SpecificLevel (ModifierType=1): `rank >= level ? StartValue + Value : 0`
- Operator=1 (Subtract) 取负（数据中无实际用例）

验证:
- MasteryShatterOverpower: SpecificLevel 1, rank 1 → 3.0 + 0.25 = 3.25
- 21 个 SpecificLevel spec 全部在 C# 数据中正确标记

### 3. 加减/覆盖顺序

- `EffectivePowers`: `values[attr] = values.GetValueOrDefault(attr) + delta`
- 全部 Additive，无覆盖。同一技能内无属性被多个精通改写（已验证）。
- 不同技能的精通互不干扰（per-skill 独立）。

### 4. 重置

- `GameStore.ResetMasteryRanks(owner, id, masteryIds=null)`:
  null=清空全部，指定 ID=清除特定。
- `SetMasteryRank(..., 0)` 已支持删除单个。
- 重置费用: 客户端请求带 OpalCost 字段，服务端 CharacterPowerService
  的 Reset 接口仍是 stub（未实现扣费），待后续接线。

## 测试

`server/Nordicandia.StoreTests/C07MasteryTests.cs`，12 条：
- prereq: 依赖数据存在性（2 条）
- perlevel/specific: 公式验证（5 条）
- reset: 设置/清除特定/清空全部（3 条）
- 另更新 ManaMasteryTests：分配 Chains 前先分配前置 ImprovedChainLightning

全回归: 626 PASS（+12），fidelity 51/51。

## 遗留（已解决 2026-10-03）

- ~~B02 的 318 vs 实际 297 差异待查~~ → **已解决**：PowerMasteries.json
  实际 297，B02 的 318 系笔误，已修正 B02 文档。
- ~~CharacterPowerService.Reset 的 OpalCost 扣费未实现~~ → **已实现**：
  ResetSkillMastery / ResetAllSkillMasteries 均检查余额、扣 OpalCost、
  清精通，返回 GainedMasteryPoints + NewOpals。余额不足时不扣费不重置。
  （单技能重置暂按全清处理，因 PowerId→精通映射需客户端元数据 GUID。）
- ~~精通 UI 的前置显示（灰显）待前端~~ → **已实现**：MasteryView 新增
  Dependencies 字段；前端 renderPowers 显示"需要: X"，前置未满足时
  按钮禁用 + 行半透明 + tooltip。
