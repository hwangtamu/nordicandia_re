# 客户端补丁说明：技能升级后重登回到 1 级

> 服务端已上线新接口 `UpgradeCharacterSkillRank`（镜像 `nordicandia-server:stats1`，
> 2026-09-26）。本文说明客户端需要改哪些部分、怎么改。改完后技能升级会持久化到
> 服务器，重登不再回退。

## 1. 根因（已用反汇编实证）

- 客户端技能实例数据是 `Game.LivingPowers` / `Game.LivingPower`，
  等级字段为 `LivingPower.Power_Rank`。
- 升级唯一入口是 `LivingPowers.RankUp(int powerHashSafe)`，
  它只改内存里的 `Power_Rank`，**没有任何网络调用**。
- 客户端唯一上传技能数据的序列化路径 `Character.Serialize` 只写**本地存档**
  （`SaveCharacterToLocalDevice_MessagePack`），从不发往服务器。
- 服务端 `ApplySkillAssignment` 在分配技能时把 `Power_Rank` 写死为 `1`，
  且官方 API 里没有任何上传/升级基础技能等级的接口。
- 因此：本地升级 → 服务器毫不知情 → 重登时服务器下发 `Power_Rank=1`，
  经 `LivingPowers.Deserialize` 覆盖本地等级。

注意：`LevelUpCharacterSkillMastery` 是另一条线（`Power_Masteries`），
**不可复用**，基础 Rank 与 Mastery 是两个字段、两套接口。

## 2. 服务端已提供的新接口（客户端直接调用）

- MagicOnion unary，路径：`ICharacterServiceApi/UpgradeCharacterSkillRank`
  （服务接口名必须一字不差，否则路由不到）。
- 请求 `UpgradeCharacterSkillRankRequest`（`[MessagePackObject(true)]`，key 即属性名）：
  - `Guid CharacterId` —— 当前角色
  - `int PowerHashSafe` —— 与客户端 `RankUp(int powerHashSafe)` 的参数同一值
  - `double NewRank` —— 客户端刚升到的新等级
- 响应 `UpgradeCharacterSkillRankResponse`：
  - `double NewRank` —— 服务器确认并落盘后的等级
- 服务端校验（已实现，可直接信任其错误码）：
  - `InvalidArgument`：NewRank ≤ 1、> 200、NaN/Infinity
  - `FailedPrecondition`：NewRank ≤ 服务器当前等级（必须单调递增）
  - `NotFound`：角色不存在 / 不属于当前账号
  - 服务器没见过的 PowerHashSafe 会自动创建（防御离线习得技能），初始 rank=1 再升级

金币/费用：本次不动。客户端现有的扣费流程保持原样，新接口只同步等级。

## 3. 客户端要改的部分

### 3.1 新增 RPC 定义（必须）

在客户端的 `SharedNet.Api.ICharacterServiceApi` 接口声明中追加：

```csharp
MagicOnion.UnaryResult<SharedNet.Api.UpgradeCharacterSkillRankResponse> UpgradeCharacterSkillRank(SharedNet.Api.UpgradeCharacterSkillRankRequest req);
```

并新增两个 DTO（`[MessagePackObject(true)]`，属性名与服务端一致）：

```csharp
public class UpgradeCharacterSkillRankRequest
{
    public System.Guid CharacterId { get; set; }
    public int PowerHashSafe { get; set; }
    public double NewRank { get; set; }
}
public class UpgradeCharacterSkillRankResponse
{
    public double NewRank { get; set; }
}
```

### 3.2 调用点：`LivingPowers.RankUp` 之后（必须，唯一埋点）

`LivingPowers.RankUp(int powerHashSafe)` 是所有升级路径的 choke point
（手动升级、训练完成都经此；分配技能时的调用也会经过，见 3.4）。
补丁逻辑（伪代码）：

```csharp
// 在 RankUp 返回后：
double before = <升级前的 rank>;               // 若拿不到，至少记录是否变化
powers.RankUp(powerHashSafe);
double after = powers.GetTrainedRank(powerHashSafe);
if (after > before)
{
    var resp = await characterApi.UpgradeCharacterSkillRank(new UpgradeCharacterSkillRankRequest {
        CharacterId = currentCharacterId,     // 客户端当前角色
        PowerHashSafe = powerHashSafe,        // RankUp 的同一个参数
        NewRank = after,
    });
    // 可选：用 resp.NewRank 回填本地，做一致性校验
}
```

- `CharacterId`：客户端当前角色上下文已有（登录/进游戏时确定），直接取。
- `powerHashSafe`：就是 `RankUp` 收到的参数，原样透传。
- `NewRank`：`RankUp` 返回后调 `GetTrainedRank(powerHashSafe)` 读。
- 失败处理建议：乐观本地 + 失败重试一次；仍失败则打日志，
  下次登录以服务器为准（服务器是最终权威）。

### 3.3 训练完成路径（验证项，非新增代码）

`StartTraining` / `HasFinishedTraining` / `ResetTraining` 只是计时器，
真正的升等级仍然走 `RankUp`。补丁作者只需验证一次：
完成一次被动训练，确认新 RPC 被调用且 rank 落盘。
若发现某条训练完成路径绕过 `RankUp` 直接写 `Power_Rank`，
则在该处补同样的调用。

### 3.4 分配技能时的 `RankUp` 调用（注意）

技能分配（`AssignCharacterActiveSkill` 等）的异步处理器内部也会调
`RankUp`（inspector 地址 `0x02758478` 的 `MoveNext`）。
按 3.2 的"rank 发生变化才调用"规则，分配时的调用是无害的：
- 新技能 rank 从 0→1：**不要调用新接口**。服务端 `ApplySkillAssignment`
  本来就会建 rank=1，且服务端会拒绝 `NewRank <= 1`（`InvalidArgument`）。
  按 3.2 规则只在 `after > 1` 时上报，0→1 的情况天然被过滤掉；
- 若客户端对已升级技能重新分配导致本地 rank 变化，
  新接口正好把正确等级同步上去，顺带修复"重新分配掉等级"问题。

### 3.5 不需要改的部分

- `SkillBookEntry.RefreshData` / 等级显示：读的是本地 rank，无需动。
- `LivingPowers.Serialize` / `Deserialize`：本地存档与登录加载逻辑不动，
  服务器数据天然是权威来源。
- 金币扣除：不动。

## 4. 反汇编定位参考（v1.9.3-4，`libil2cpp.so`，ARM64）

> 注意：Il2CppInspectorRedux 给出的地址整体偏了 `0x4000`，
> 真实文件偏移 = 标注地址 − `0x4000`。

| 方法 | 标注地址 | 文件偏移 |
|---|---|---|
| `LivingPowers.RankUp` | `0x02C6C63C` | `0x02C6863C` |
| `LivingPowers.GetTrainedRank` | `0x02C6EA0C` | `0x02C6AA0C` |
| `LivingPowers.HasFinishedTraining` | `0x02C6C128` | `0x02C68128` |
| `LivingPower.Power_Rank` setter | `0x02C6A874` | `0x02C66874` |

## 5. 验证清单

1. 手动升级技能 → 服务器日志出现 `[SKILL] character=... rankup hash=... rank=N`
2. 重登 → 技能等级保持
3. 完成一次被动训练 → 等级 +1 且重登保持
4. 换设备/清本地存档登录 → 等级仍保持（证明走的是服务器）
5. 快速连点升级 → 等级单调递增，无跳级（服务端单调性校验兜底）
