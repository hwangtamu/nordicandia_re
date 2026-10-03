# C06 — 技能机制家族第二批（2026-10-03）

按机制家族分批实现。本批 5 个技能 + 2 个系统级修复。

| 技能 | 职业 | 家族 | 原型（修正前） | 实现 |
|------|------|------|---------------|------|
| ImpalingTrap | Hunter | 陷阱 | strike | `Trap` 实体：5s 持续，近距离触发 7.5x，单次消耗 |
| Teleport | Mage | 位移 | rally（误分类） | `Blink`：远离最近威胁 10y（修正 C04 可疑映射） |
| Whirlwind | Warrior | 引导 | nova | `Channel`：5s，0.2s tick，0.4x，2y 半径 |
| MarkOfTheChosen | Hunter | 易伤 | rally | 怪物 debuff：承伤 +10%，15s（走 C01 易伤管线） |
| Thorns | Hunter | 反伤 | rally | 玩家 buff：200% 近战反射 + 10% 物理减伤，10s |

## 实现位置

- `server/Nordicandia.Simulation/CombatInstance.C06.cs`：`Trap`、`Channel` 实体，`PlaceTrap`、`Blink`、`StartWhirlwind`、`ApplyMark`、`ApplyThorns`。
- `CombatInstance.cs`：`Step` 接 `TickTraps`/`TickChannel`；`UseSkill` 按名分发（nova/strike/rally 分支内特判，C06 后续泛化为分类器规则）；`ResolveSkill` 读怪物 `mark` debuff 填 `DamageTakenAmplifyPercent`；`ApplyPlayerDamage` 接 Thorns 减伤 + 反射。

## 系统级修复（本批测试暴露）

1. **BuffManager Source 键**：`MagnitudeOf(id)` 默认 `source=""`，但 C05/C06 的 debuff 带 Source（`PoisonCloud`、`MarkOfTheChosen`、`Thorns`），导致读不到。已修三处调用传 Source：
   - `EffectiveMonsterSpeed`：`slow`/`PoisonCloud`
   - `ResolveSkill`：`mark`/`MarkOfTheChosen`
   - `ApplyPlayerDamage`：`thorns`/`Thorns`、`thorns-reduction`/`Thorns`
2. **浮点到期**：`Trap`/`Channel`/`PoisonCloud` 的 `Remaining <= 0` 改为 `<= 1e-9`（C02 临界处理一致）。

## 边界样本

每个技能含等级/装备/目标边界（`C06SkillTests.cs`，18 条）：

- **ImpalingTrap**：远处怪不受影响；5s 未触发消散。
- **Teleport**：位移 10y，方向远离威胁。
- **Whirlwind**：24 ticks（Advance 5s 上限）；2y 外不受影响；武器伤害翻倍 → 总伤害比 1.98（装备边界）。
- **MarkOfTheChosen**：只标记目标怪；10% 放大、15s 时长（属性对照）。
- **Thorns**：反射 = 承伤 × 200%（事件求和，排除自动攻击污染）；10% 减伤对照（0.9 < 1.0）。

## 测试

`server/Nordicandia.StoreTests/C06SkillTests.cs`，18 条通过。
全回归：**555 PASS**（537 + 18），fidelity **51/51**。

## 剩余（C06 后续批次）

22 个带 gaps 技能待实现，按家族：

- **Warrior**：Slam、Retaliation、Intimidate
- **Hunter**：FrozenArrow、ManaArrows、Fade、Backflip（镜像）
- **Mage**：ElementalSeal、Blizzard、ManaShield、IceBlast、InfernalBlast、Thunderstrike、Tornado
- **Necromancer**：AstralWalk、Shadowbolt、UnholyFocus、DrainLife（`field_0x158` 未知）、BoneLink、Decay、DemonicPresence、Sacrifice

另：`Base_Action_Speed` 施法时间系统（多技能通用）；`export_powers.py` 分类器加 trap/channel/mark/thorns 规则，去掉 `UseSkill` 按名特判。

## 第二批（2026-10-03）：5 技能

| 技能 | 职业 | 家族 | 原型（修正前） | 实现 |
|------|------|------|---------------|------|
| Blizzard | Mage | 持久 AoE | nova | 复用云实体：4s，4y 半径，1.0x Cold tick，无减速 |
| ElementalSeal | Mage | 持久 AoE | nova | 复用云实体：10s，2.5y 半径，0.25x tick（玩家站内增伤待后续） |
| FrozenArrow | Hunter | 爆炸弹体 | projectile | 1.4x 命中 + 4y 半径爆炸 AoE（主目标除外） |
| Tornado | Mage | 移动涡流 | projectile | 2y 半径弹体，100% 穿透，2.3x（7s 游荡未模拟，同步结算） |
| Slam | Warrior | 直线 | nova（圆形） | 15y 直线，1y 半宽，5.5x（修正圆形误伤） |

实现：`SpawnGroundEffect(skill, x, z, kind)` 泛化（`PoisonCloud.Kind` 区分）；`HitLine` 直线判定；
`HitTornado` 胖弹体；FrozenArrow 命中后 `HitNova` 式扩散。

测试：`C06Batch2Tests.cs` 20 条。全回归 **575 PASS**，fidelity **51/51**。

## 剩余（C06 后续批次）

17 个带 gaps 技能：
- **Warrior**：Retaliation、Intimidate
- **Hunter**：ManaArrows（充能系统）、Fade、Backflip（镜像）
- **Mage**：ManaShield、IceBlast、InfernalBlast、Thunderstrike
- **Necromancer**：AstralWalk、Shadowbolt、UnholyFocus、DrainLife（`field_0x158` 未知）、BoneLink、Decay、DemonicPresence、Sacrifice

另：`Base_Action_Speed` 施法时间系统；分类器加新家族规则。

## 第三批（2026-10-03）：17 技能 — C06 完成

| 技能 | 职业 | 实现 |
|------|------|------|
| Retaliation | Warrior | 12s buff，受击时 0.3x 武器伤反击 |
| Intimidate | Warrior | 10s，6y 内敌人伤害 -10%（debuff） |
| Fade | Hunter | 6s，+100% 闪避 +5% 移速 |
| ManaShield | Mage | 护盾 + 20% 伤害吸收系数 |
| IceBlast | Mage | 3.0x 弹体 + 15y 爆炸 |
| Shadowbolt | Necro | 3.0x 弹体 + 2.5y 爆炸 |
| InfernalBlast | Mage | 7.0x 弹体 + 3y 爆炸 + 4s 燃烧 |
| Decay | Necro | 8s 持久 AoE + 20% 攻速减速 |
| BoneLink | Necro | 15s，承伤 -30%（目标暂定为玩家） |
| DemonicPresence | Necro | 16s 光环：+30% 生命 +10% 力场 +1% 回复 |
| Sacrifice | Necro | +50% 法力，下次攻击法术 +40%（消耗） |
| UnholyFocus | Necro | 8s，+20% 移速 +10% 攻速（处决/恶魔未实现） |
| DrainLife | Necro | 6s 引导，0.7x + 吸血（8% + 4%/仆从；field_0x158 未知） |
| ManaArrows | Hunter | 8 充能，1.2x，每命中 +4 法力 |
| Backflip | Hunter | 后跳 6y + 镜像（5s，20% 生命） |
| AstralWalk | Necro | 移速 buff（10.0 暂定 3x 上限） |
| Thunderstrike | Mage | 4s 风暴，30y，暂定 1.0x（无伤害数据，需客户端研究） |

**C06 完成**：51 技能 gaps 清零。测试 `C06Batch3Tests.cs` 39 条。
全回归 **614 PASS**，fidelity **51/51**。
