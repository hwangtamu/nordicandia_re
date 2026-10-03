# B02 废弃 / 重复条目判据

日期：2026-10-03。基线：Android 1.9.3（见 [B01](B01_VERSION_BASELINE.md)）。
数据源：`gamedata_decrypted/*.json`（35 个文件，解密自官方客户端）。

## 总则

- 条目状态只有三态：**正式 / 废弃 / 待核实**。没有证据不标废弃。
- **未被引用 ≠ 废弃**：63 个未被职业引用的 Powers 是怪物技能/AI 战术/默认攻击；
  105 个无 DropPool 的 Items 是货币/材料/精华类。它们都是正式内容，
  差异只在"可获得性"标注。
- Guid 悬空引用 → 标**待核实**，不直接判废弃（可能是引用语义理解错，如 Affix.GroupId）。

## 硬性废弃信号（满足任一可直接标废弃）

| 信号 | 实例 |
|---|---|
| `Hidden: True` | 4 个职业：Paladin、Assassin、Barbarian、Priest（技能表全空） |
| 名称含 `_UNUSED` | 4 条教程对话：`Tutorial_Location_UNUSED` 等 |
| 同类条目皆有内容、唯独它引用列表全空 | 同上 4 职业（ActiveSkills/PassiveSkills 全空） |

## 正式但需标注可获得性（不是废弃）

| 类别 | 数量 | 可获得性标注 |
|---|---|---|
| 无 DropPool 的 Items | 105 | 货币/材料/精华：商店购买、任务、合成、分解（逐条核对） |
| 未被职业引用的 Powers | 63 | 怪物技能 / AI 战术（Flee、RunOutOfCombat）/ 默认攻击（DefaultFireball 等）/ 仆从技能 |

## 待核实清单

- 4 个 BrainId 悬空的怪物：FireGolem、StoneGolem、CrystalGolem、HunterMirrorImageMinion
 （Brains.json 34 条里没有它们引用的 brain；需确认是数据缺失还是引用字段理解错）。
- Affixes 1730/1837 的 GroupId 在 AffixGroups.json（60 条）中找不到 → 先查 GroupId 语义，
  不作为废弃信号。
- Worlds.json 36 条中 1 条 Tier=None → 确认是哪个特殊世界（疑似 Niflheim/Helheim）。
- `item_catalog.json` 458 条 vs 源 Items.json 608 条：差值 150 条未逐条对过；
  catalog 按 Name 键、源按 Guid 键，需先统一键再判定。

## 重复条目判据

- 同名条目：Items / Powers / Monsters 普查结果为 0 重复。
- **词缀是例外**：Affixes.json 有 115 个重名（如 `LocalImplicitBasePhysicalDamage`），
  实为按装备类型/稀有度拆分的 implicit 变体（不同 Guid、不同 ValueRange），
  是正式内容不是重复条目。按名键存储时会发生多对一（精选 18 键 ↔ 源 26 Guid），
  使用时以 Guid 为准。
- 同 IntegerId 不同 Guid：待查（IntegerId 是客户端属性/物品 id 体系，需防冲突）。

## 分品类判据速查

| 品类 | 源文件 | 总数 | 废弃判据 | 正式但特殊 |
|---|---|---|---|---|
| 职业 | CharacterClasses.json | 8 | `Hidden=True` → 废弃（4 个） | — |
| 技能 | Powers.json | 159 | 名称 `_UNUSED` / 全空引用 | 未被职业引用 → 怪物/AI 技能（63） |
| 精通 | PowerMasteries.json | 297 | 同上 | — |
| 物品 | Items.json | 608 | 名称 `_UNUSED` | 无 DropPool → 货币/材料（105） |
| 词缀 | Affixes.json | 1837 | 名称 `_UNUSED` | GroupId 悬空先查语义 |
| 套装 | ItemSets.json | 11 | — | — |
| 怪物 | Monsters.json | 136 | 名称 `_UNUSED` | BrainId 悬空 → 待核实（4） |
| 世界 | Worlds.json | 36 | — | Tier=None → 待核实（1） |
| Buff | Buffs.json | 273 | 名称 `_UNUSED` | — |
| 对话/任务 | Conversations/Quests | 66/47 | `_UNUSED`（4 条对话） | — |
| 宠物 | — | — | 数据源未定位，见下 | — |

## 宠物数据源（已定位，2026-10-03）

`Monsters.json` 内 `CombatPet` 类型 5 条（Durnir/Eggther/Fossegrim/Hala/Hraesvelgr）、
`Pet` 类型 8 条（BabyDragon/Crow/Dog/Eagle/Larva/Snake/SnowOxe/Turtle），
另有 Petkeeper / CombatPetkeeper NPC。详见 [B02_MONSTER_CATALOG.md](B02_MONSTER_CATALOG.md)。
宠物获取/价格/升级规则不在怪物表内，需另查商店/Catalog（P03 范围）。

## 状态：已还原（判据）

本判据为 B02 各品类目录的前置规则。应用判据后，条目状态变更需记录证据
（文件、字段、引用链），废弃条目保留在目录中但标废弃，不删除。
