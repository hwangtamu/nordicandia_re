# 宠物与战斗宠物机制恢复（Android 1.9.3 / versionCode 507033）

状态：**目录、价格、购买/选择存档已接；战斗运行时与若干非付费获取分支未接。**
本页把原生证据、数据恢复和网页实现分开记录，不代表宠物系统已整体验收。

## 原生调用链

- `Character.SetupPets(clear)` 清理旧宠物后，从 `SerializedPets`/`SerializedCombatPets` 的当前定义 ID 与已拥有列表重建角色宠物。
- `Character.ChangePet(definition, apply)` 对已有宠物检查 `AvailablePets`，销毁/替换旧 `PetMinion`；为新宠创建 `PetMinion`、重建宠物背包格、应用装备并进入世界。`Character.SpawnPet()` 只在选中宠物仍存活时重建其世界位置。
- `Character.ChangeCombatPet(definition, apply)` 管理当前 `CombatPetMinion`；`Character.SpawnPet()` 的战斗宠物分支同样受已拥有/存活状态控制。
- Town NPC `CombatPetkeeper` 的交互在 `WindowInGame.InteractiveNpc_OnInteraction` 中有专用分支，打开 `WindowCombatPetkeeper`。该窗口存在 `LoadCombatPets`、选择/接受、移除、解锁流程；普通 `Petkeeper.OnEnter` 则调用 `Actor.OnEnter` 和 `SpawnPets`，通过多处 `SpawnPet(minionDefinition)` 生成环境宠物。普通 Petkeeper 的点击是否打开 `WindowPetkeeper` 尚未从 1.9.3 全局触发链确认。
- `WindowPetkeeper`/`WindowCombatPetkeeper` 的 `PetRow.Present()` 遍历 `MonsterDefinition.AllAffixes` 并读取 `ItemAttributeSpecifierDefinition.GetMinMaxValues(1, Rarity=1, 1)`。它将 attribute **421 (`OpalCost`)** 显示为 OP 价格，将 **968 (`SilverCost`)** 显示为 SL 价格。没有这两个属性的行没有货币解锁按钮；获取来源未知时不自行提供免费购买。
- `WindowCombatPet`、`CombatPetMinion` 还包含升级参数、属性、死亡、30 分钟可复活时间及 rewarded-ad 复活路径。服务器契约已有状态/死亡/广告/复活/经验方法，但网页战斗并没有实际生成并运行 `CombatPetMinion`。

## 1.9.3 价目（数据可重建）

由 `tools/web-content/export_pet_catalog.py` 从 `Monsters.json` 的 `AffixIds` 联接 `Affixes.json` 后按 `PetRow.Present` 的等级/稀有度参数生成；金额不依据名称猜测。

| 类别 | 定义 | 货币 | 价格 | 价格来源属性 |
|---|---|---:|---:|---:|
| 普通宠物 | PetBabyDragon、PetCrow、PetEagle、PetLarva、PetTurtle | OP | 600 | 421 |
| 战斗宠物 | CombatPetDurnir、CombatPetEggther | SL | 6,000,000 | 968，Rarity 1 值 |
| 战斗宠物 | CombatPetHala、CombatPetFossegrim | OP | 1,000 | 421 |
| 战斗宠物 | CombatPetHraesvelgr | OP | 1,200 | 421 |

PetDog、PetSnowOxe、PetSnake 与价格属性缺失的 NPC/宠物定义不在购买表内。已导出的其他可见宠物效果仅作目录信息展示，尚未全部接入战斗/奖励公式。

## 网页实现

- `server/Nordicandia.Server/GameData/pet_catalog.json` 是导出的运行时目录；复现命令：`python3 tools/web-content/export_pet_catalog.py`。
- `PetCatalog` 为 `/api/web/v1/characters/{id}/pets` 提供定义、价格、affix 信息与存档拥有状态。
- `/pets/unlock`、`/combat-pets/unlock` 不接受客户端价格；服务端按目录验证宠物类型、指定货币和精确金额，再检查余额并扣款。直接 `GameStore.UnlockPet/UnlockCombatPet` 也拒绝伪造价格与余额不足。解锁是幂等的，已拥有宠物不会二次扣款。
- `/pets/select`、`/combat-pets/select` 只允许选择存档已解锁定义；当前宠物 ID、CombatPet 等级/经验/死亡状态均从现有序列化状态回读。
- StoreTests 覆盖全价目、币种、付费/不足余额拒绝和宠物选择/CombatPet 进度重启持久化；`npc-smoke` 覆盖两个 Town 宠物列表及价格展示。

## 尚待完成

1. 将选中普通宠物的 PetMinion 跟随/宠物背包/拾取，以及环境 Petkeeper 的 SpawnPets 接入网页状态。
2. 以 `CombatPetEvolutionParameters` 和 `CombatPetMinion` 原生逻辑实现战斗宠物攻击、等级/经验、特定 affix bonus、死亡与冷却/广告复活；目前只持久化/展示相应状态，没有战斗实体。
3. 还原无 OpalCost/SilverCost 的宠物获取来源和普通宠物所有具体 affix 效果，再做 Android 1.9.3 数值/流程对照。
