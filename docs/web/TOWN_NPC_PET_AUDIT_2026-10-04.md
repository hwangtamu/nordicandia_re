# Town / NPC / Pet 最新工作区核对

2026-10-04。核对包含未提交改动的当前工作区，不把资源导出或 smoke 通过等同于原版完整复现。

| 范围 | 确认已接入 | 未通过完整验收的原因 |
|---|---|---|
| Town | 原版 bundle 静态 GLB、SpawnZone；安全区；进入/返回；城镇位置标记持久化 | 实际浏览器截图仍有大量碎片状几何与密集标记，尚未通过视觉验收；服务端移动是 OpenArea 全开放网格，不是原版场景碰撞 |
| NPC | 10 类站点/窗口路由；铁匠/商人/祝福/世界/传送门/宠物窗口 | 动态 NPC 和完整原版交互尚未复现；Elder、Changer、GuildDefense、Helheim 等路径仍缺。NPC smoke 部分通过 DOM click 验证路由，不证明所有站点可自然走到并点击 |
| Pet | 8 种普通宠物、5 种 CombatPet 目录；有价格宠物的购买、选择、持久化；余额/价格校验 | 普通宠物无跟随/拾取实体；CombatPet 无实际战斗实体/成长/死亡复活；affix 仅展示，未接战斗/收益；无价格宠物获取渠道未知 |

## 本次确认并修复

* Town → Portal 曾继承 Town 地图；世界切换也保留旧坐标、旧移动路径。现统一目的地地图生成和安全落点，清理旧移动及待生成包状态。
* 初次登录和返回世界采用不同地图种子组合。现统一 `WorldLayout`，同一角色/Tier 的地图在重建后保持一致。
* 旅行前结算旧场景的时间和掉落，避免城镇停留时间在新世界被补算成战斗；Niflheim 最后一包掉落在自动回城前入库，命令路径也执行完成回城。
* 回城按钮、T 键长按、传送门和选世界操作共用旅行互斥；旅行期间不发送新移动/技能命令。
* **购买后的扣款被战斗刷新覆盖**：新增测试在修复前明确失败（`TownReturn: town purchase: combat polling preserves pet purchase debits in runtime and storage`）。根因是独立 NPC/Pet 事务修改存档，但旧 CombatInstance 随后按绝对余额覆盖它。现 `FlushLocked` 在 GameStore 锁内合并战斗货币增量，并将合并余额同步回实例；NPC、普通宠物和 CombatPet 扣款均有回归覆盖。

## 验证

* 服务端当前工作区：871 PASS，回放 58/58；含 `PetRosterTests`、`TownReturnTests`、M3 最后一包奖励及 W05 往返测试。
* `npm run build` 通过；`npm run npc-smoke`、`npm run smoke` 通过。
* NPC smoke 覆盖 10 类路由、8/5 宠物目录及价格显示；未实际验证宠物战斗效果。
* 浏览器截图：`tmp/web-town/town.png`。有场景加载成功的证据，但画面仍不合格，不能将 Town 标为完整复现。

## 下一步缺口

1. 校准 Town 静态批处理网格及层级变换、材质/光照，并与原版同场景截图对照；再恢复碰撞和真实 NPC 模型/布局。
2. 接入普通 Pet 的跟随/拾取以及 CombatPet 的生成/攻击/成长/死亡复活，应用已提取的 affix。
3. 后续 Niflheim 恢复工作已使 `GetOrCreate` 重建已消费门户的包数、清包进度、当前怪物和随机数状态；旧存档只有 active 标记时无法反推出原包数，会从最低 2 包重建。详情见 [Niflheim 恢复报告](NIFLHEIM_RECOVERY_2026-10-04.md)。
