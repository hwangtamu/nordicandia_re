# B02 模式目录（验收分母）

任务 ID：B02（模式部分）。原版定义 ID：客户端协议 `GameMode` 枚举
（`server/Nordicandia.Contracts/GeneratedContracts.cs:3985`，由客户端反汇编生成）。
日期：2026-10-03。基线：Android 1.9.3，见 [B01](B01_VERSION_BASELINE.md)。

## 分母

| 值 | 模式 | 说明 |
|---|---|---|
| 0 | Unknown | 占位 |
| 1 | Normal | 普通模式 |
| 2 | Season | 赛季模式 |
| 3 | Challenge | 挑战模式 |
| 4 | NormalHardcore | 普通硬核（死亡=删档，待核实） |
| 5 | SeasonHardcore | 赛季硬核 |
| 6 | ChallengeHardcore | 挑战硬核 |

`gamedata_decrypted/` 无模式定义文件；模式是账号/赛季维度的服务端概念。

## 与 O01 的边界

- 本目录只记录"协议里定义了哪些模式"。
- 各模式在 1.9.3 是否实际开放、赛季规则/重置语义/排行榜/转出机制
  → O01（在线功能规格核对）范围，本目录不预设。
- 网页服务端已有 `GameModeService`、`LeaderboardService`（私服实现），
  其语义与原版的对应关系待 O01 核对。

## 状态：已定位（定义）/ 待核实（各模式实际开放状态）

注：本目录无独立机读文件，定义即协议枚举本身。
