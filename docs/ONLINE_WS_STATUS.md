# Nordicandia 私有服务器 —— 在线模式 / 实时通道 现状报告

> 最后更新：本次会话结束
> 结论一句话：**登录、选模式、角色列表、本地存档系统、排行榜、ChangePassword 均已打通；唯一未打通的是"在线世界入口 + 实时 socket"，而它正是"经验归零"的根因。**

---

## 1. 目标与症状

| 目标 | 状态 |
|---|---|
| 连上私有服务器 | ✅ |
| 邮箱/密码登录（服务端） | ✅ |
| Google 登录保持可用（不重签 APK） | ✅ |
| 排行榜显示 Steam 角色 | ✅ |
| 修改密码 | ✅ |
| **进入世界并游玩** | ⚠️ 仅"强制离线"路径可进 |
| **经验持久化（不回 0）** | ❌ |
| **实时通道 `/ws`** | ❌ 从未建立 |
| 独立 XAPK 分发 | ⚠️ 可构建，但内容待更新 |

---

## 2. 已修复 / 已交付（可复现）

| 项目 | 证据 / 位置 |
|---|---|
| **Envoy `allow_connect: true`**（listener + cluster） | `deploy/lightsail/envoy.yaml`，提交 `1b53412`，已上线重启。客户端用 **HTTP/2 extended CONNECT** 开 `/ws`，原配置必被拒 |
| Envoy `grpc_http1_bridge` + Lua 修复（grpc-dotnet trailers-only 误判） | `deploy/lightsail/envoy.yaml` |
| gRPC 诊断日志（`gs/gm/ct/te`） | Envoy access log |
| TLS 补丁（`MobileTlsContext.ValidateCertificate`） | `server/patch_android_online.py` |
| gRPC over HTTP/1.1 补丁（`GrpcCall.ValidateHeaders` / `_RunCall`） | 同上 |
| Season 模式 UI 补丁 | 同上 |
| 服务端排行榜 JSON 接口 + `LeaderboardQuery` | `server/.../LeaderboardQuery.cs`、`LeaderboardHttp.cs`；`GET /api/leaderboards/{normal\|season}/overall?limit=N` |
| 服务端 `ChangeEmailPassword` | `LoginService.cs` |
| **stub 工具链根因修复** | `_stub_symbols` 改为 `syms.update(found)`；此前 KeyError 导致 `mk_a1.py` 静默失败、**所有测试都在推旧库** |
| **地址漂移 bug 修复** | STUB_VERSION 改用 `adrp/add g_dbg`（`g_dbg` 每次构建地址会变） |
| **测试铁律** | sha256 校验设备库 + `rm`/`cp` 换 inode + 探针安装校验 + 关键链路尽早 attach |

---

## 3. 核心资产（可复用）

### 3.1 运行时 IL2CPP 解析器（最重要）
注入 stub 在 `UIWindowManager.Update` 每帧调用，使用 IL2CPP API 枚举类/方法并发布到 `g_meth_fn[] / g_meth_name[] / g_meth_n`，Frida 可直接读出**方法名 + 固定偏移**。
- 支持**按类名**匹配，也支持**按方法名**反查类（本次用它找到 SignalR 协商类）。
- 彻底摆脱"预知地址"和"地址表不可靠"的问题。

### 3.2 已解析出的方法表（均为固定 lib 偏移）

**`SaveManager`（64 个）**
```
SaveManager.Update                    0x25D0CB0   ← 根因指令 0x25D0F8C 在此
SaveCurrentAccountAndCharacter        0x25D10CC
SaveCurrentAccount                    0x25D180C
SaveCharacter                         0x25D18D8
InternalSaveCharacter                 0x25D19C8
SaveCharacterSnapshotOrLegacy         0x25D2710
GetOfflineWriteRoute                  0x25D1F18
ForceHeaderOnlyWhenGmacMissing        0x25D3FF4
ScheduleOnlineSaveIn                  0x25D092C
Player_OnCharacterEnteredGame         0x25D085C
```

**`NetSocket`（64 个）**
```
ConnectWithNewSocket                  0x2E09050   （异步包装；体在 MoveNext）
EnsureSocketConnectivity              0x2E096A4
RetryConnection                       0x2E09620
Socket_Connected                      0x2E0920C
Socket_Closed                         0x2E09564
CloseSocket                           0x2E094B8
ResetCharacterExperienceSync          0x2E09A88   ← 经验同步走 socket
UpdateFixedAsync                      0x2E09E80
_ConnectWithNewSocket_d__116.MoveNext 0x2E0CE50
```

**SignalR 协商类（按 `get_WebSocketServerUrl` 反查得到）**
```
Start                                 0x34197AC
Get                                   0x341EC08
OnNegotiationRequestFinished          0x341DEF4
RaiseOnError                          0x341E3CC
get_Url                               0x341DDF4
get_WebSocketServerUrl                0x341DE04
get_TryWebSockets                     0x341DE68
```

**`UIWindowManager`（64 个）**
```
Update                                0x278D738
ShowWindow                            0x278E674
ShowInGameWindow                      0x278EA08
ShowSocketConnectingDialog            0x279013C
HideSocketConnectingDialog            0x278D4EC
Player_OnCharacterLeftGame            0x278D4E4
ShowLoadingDialog                     0x2790550
ShowDialogRetryContinue               0x27917A8
```

### 3.3 关键地址
```
世界入口决策点（IsOnline 分支）      0x257D29C   cbnz w8, 0x257DAE8(在线) / b 0x257E610(离线)
离线块里的本地设备登录调用           0x257E46C   UnityGame.SignInNew(0,true,true,null,null)
SPSM 抛异常点                        0x26FB774   "Cannot synchronize online account"
SPSM 在线守卫                        0x26FB754
PlayerAccount.get_IsOnline           0x2C0AEA4   AccountType(+0x30) ∈ {1,2,3}
PlayerAccount.RefreshAccountType     0x2C0CBDC
UnityGame.SignInNew                  0x2701390
```

---

## 4. 根因链（本次会话最终收敛）

```
① 客户端从未启动实时客户端
   · SignalR 协商类 Start/Get/OnNegotiationRequestFinished 全部从未触发
   · Envoy 侧无 /negotiate、无 /ws
   · ShowSocketConnectingDialog / ShowInGameWindow 从未调用
        ↓
② 为什么没启动？世界入口没走通
   · 在线账号(AccountType=2, IsOnline=1) → 世界入口跑 SPSM
     → 抛 "Cannot synchronize online account" → 错误框 → 退回主菜单
   · 只有【离线块 0x257E610】才初始化加载 UI 并驱动世界入口
        ↓
③ 我们此前的"强制离线"补丁（NOP 0x257D29C + NOP 0x26FB754）
   · 能让客户端进世界，但把客户端锁进本地设备路径
   · 且 IsOnline 仍为 true → SaveManager.Update 按设计跳过本地保存
   · 结果：本地不写、服务器也拿不到 → 经验归零
        ↓
④ 经验上传的真实通道 = 实时 socket
   · 服务端 RealtimeGateway(/ws) 写 HasRealtimeProgress / Experience
   · 证据：Steam 角色 0412ea15  exp=79740.3  rt=true  (2026-09-20)
   · 所有 device 账号角色：exp=0  rt=false
```

### 架构结论（关键）
```
在线进度上传 = 实时 socket（/ws）        ← 与 SaveManager.IsOnline 是两套独立机制
SaveManager.IsOnline 只是"保存路由"开关：
    true  → 跳过本地保存（0x25D0F8C），保存委托给 socket
    false → 走本地保存

客户端要求：IsOnline=false 以通过 SPSM/世界入口
           随后由【世界入口启动的实时 socket】负责上传
```

---

## 5. 本轮验证记录

| 实验 | 结果 |
|---|---|
| 移除强制离线（`FORCE_OFFLINE_WORKAROUND=False`，`lib_online_clean.so`） | 客户端走**在线分支** ✓、首次调用 `ConnectWithNewSocket` ✓、**首次调用 `SaveCurrentAccountAndCharacter`** ✓、并弹出真实错误框 **"Cannot synchronize online account"** |
| 服务端移除 email 链接（账号仅剩 device） | 错误依旧 → 客户端的"在线"判定**不来自 linked-accounts**，来自账号对象创建（登录响应） |
| `get_IsOnline → 0`（`lib_offacc.so`） | ✅ SPSM 异常消失；⚠️ 该轮 UI 落在 Game Mode→Select Race 向导，世界入口链未被触发 |

---

## 6. 当前阻塞：UI 自动化不可靠（非功能问题）

| 卡点 | 现象 |
|---|---|
| Game Mode 卡片点击 | `input tap` / `input swipe` 均无响应（像素分析：NORMAL 红边 1111px vs SEASON 2px） |
| 角色名输入 | `input text` 文字进入输入框，但游戏仍报 "Enter character name"，Create 不提交 |

→ 导致无法用纯 CLI 稳定走到"角色列表 → Play → 进世界"。

---

## 7. 下一步（按优先级）

```
1. 把 UI 路径变确定（三选一）
   A. 用 Frida 直接调用游戏自身 UI 方法（推荐）
      WindowSelectGameMode.OnNextClicked 0x2634174
      UIWindowManager.ShowWindow         0x278E674
      WindowCharacterList 的 Play 入口
   B. 服务端直接创建 Season 角色 → 重启 app → Game Mode 自动预选 SEASON
      → Next → 角色列表（此路径此前已验证可行）
   C. uiautomator 精确坐标（效果有限，Unity 无视图层级）

2. 用 /tmp/off.js（全链钩子）跑一次，判定：
   ShowInGameWindow → ShowSocketConnectingDialog → Negotiation.Start/Get → /ws
   → 服务端 world.json 的 Experience / HasRealtimeProgress 是否更新

3. 若世界入口通但 socket 未启动 → 用同一套钩子继续追 socket 启动的闸门
4. 若 socket 通 → 验证经验增长 + 重启后仍在
5. 最后：产品化 4 项
   · 登录 UX（免手工凭证文件；Register 与 Login 分离）
   · XAPK 重建（patch_android_online → email_login → leaderboard）
   · 安全收紧（关 NORD_AUTH_ALLOW_UNVERIFIED_PROVIDERS；8081 加 token/TLS）
   · 干净设备端到端验收
```

---

## 8. 环境与命令速查

### 构建
```bash
# 在线库（无强制离线，排查 socket 用）
python3 server/patch_android_online.py <原版 libil2cpp.so> /tmp/lib_online_clean.so

# 强制离线（仅用于"能进世界玩"，不可用于验证持久化）
FORCE_OFFLINE_WORKAROUND=True ...

# 账号离线（get_IsOnline→0）
#   0x2C0AEA4: mov w0,#0 ; ret
```

### 部署（铁律）
```bash
adb shell am force-stop com.IterativeStudios.Nordicandia
adb push <lib> /data/local/tmp/l.so
adb shell "su 0 rm -f '$LIB'; su 0 cp /data/local/tmp/l.so '$LIB'; su 0 chown system:system '$LIB'; su 0 chmod 644 '$LIB'"
# 必须校验 sha256 == 本地
adb shell "su 0 sha256sum '$LIB'"
```

### 服务端
```bash
ssh -i /tmp/ls.pem ubuntu@3.140.50.136
sudo docker logs nordicandia-envoy-1
curl http://127.0.0.1:8081/api/leaderboards/season/overall?limit=10
sudo python3 -c "import json;w=json.load(open('/opt/nordicandia/data/world.json'));..."
```

### 探针（`/tmp/*.js`，用 `/tmp/runjs.py` 运行）
```
off.js    全链：世界入口 + socket + 保存
acc.js    账号类型 / IsOnline / SPSM 抛异常
ol.js     在线路径轨迹
ld.js     ShowLoadingDialog 调用栈
wm2.js    窗口切换轨迹
mt.js     方法表（改类名/匹配规则即可复用）
neg.js    SignalR 协商链
nc.js     socket 链
pt3.js    函数内插桩（带安装校验）
mn2.js    MoveNext 状态
```

### 导航坐标（横屏 2340×1080）
```
Play(主菜单) 1170,1017 | Next 2141,1000 | Back 250,1000
角色列表 Play 2080,1017 | 关闭弹窗 1166,762
```

---

## 9. 完成度评估

| 维度 | 完成度 |
|---|---|
| 技术研究 / 根因 | **~99%** |
| 功能可用性 | **~85%**（能登录、选模式、建角色、本地存档系统、排行榜） |
| 产品化 / 可发布 | **~55%** |

**不可发布的原因**：在线世界入口 + 实时 socket 未打通 ⇒ 经验无法持久化。

---

## 10. 一句话交接

> 所有机制都已定位到指令级，工具链（运行时 IL2CPP 解析器 + 方法表 + 指令地图 + 注入 stub）完全就绪。
> 剩下的工作只有两件：**(1) 把 UI 自动化变确定（推荐 Frida 调游戏自身方法）**，**(2) 判定实时 socket 是否随世界入口启动；若未启动，继续用同一套钩子追闸门**。
