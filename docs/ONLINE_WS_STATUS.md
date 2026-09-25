# Nordicandia 私有服务器 —— 在线模式 / 实时通道 现状报告

> **最新跟进（排行榜 tap-to-inspect 已交付）**：排行榜显示正确；ItemOperation 定时 flush 已验证；
> **点排行榜玩家行打开 Player Info（WindowInspectPlayer）已在真机（RFCY416VMHF）用纯 `adb shell input tap`、无 Frida 端到端验收**：
> Watermelon / Pumpkin / Seasonia 均显示对应角色（等级、种族职业、装备、最高世界），行间切换、关闭后重开、切换 Overall/职业/Helheim 页签后再点均正常。
> 服务端 `characterId` 已部署到 Lightsail（镜像 `nordicandia-server:lbprofile1`）。设备当前运行 `/tmp/lib_full12.so`
> （sha256 `f557d1ecb5dc733ec5f50d1ef3a192040d3660c225a657eb40e009035deb244d`）。
>
> 根因（四个，全部修复，见 `device/stub/leaderboard_stub.c` / `server/patch_android_leaderboard.py`）：
> 1. **点击目标错误**：行上真正接收点击的是 `LeaderboardEntry+0x50` 的全行 **`Button (Inspect)`**（onClick 在离线版被清空），
>    而不是 `+0x48` 的 Toggle（`interactable=0`）。hook 改到 `Button.OnPointerClick` 左键分支 `0x4F76140`（`b Button.Press`，原字节 `d6ffff17`），
>    映射键改为该 Button；Toggle 保持 retail。每次 `build_rows` 清空 Button 映射，防止地址复用误开旧档。
> 2. **窗口自毁**：离线版把 `WindowInspectPlayer.Awake`（`0x25F3310`）与 `WindowInspectPlayerSkills.Awake`（`0x25F3AAC`）改成了
>    `UnityGame.DestroyObject(gameObject)`，实例化后第一帧即销毁 → 两处 prologue（`fe57bea9`）改为 `ret`。
> 3. **不能强制 Show()**：新实例 `UIWindow+0x40==0` 已是 Shown 状态，强行 `Show()`/改 `+0x40` 会触发过渡并 abort。现只 `BringToFront` + `InspectPlayer`；
>    父节点用 `GetWindow(5).transform`（与 retail `ShowInGameWindow` 相同），先 `GetWindow(46)` 复用已开窗口。不在 .bss 缓存任何托管对象（非 GC root）。
> 4. **`il2cpp_runtime_invoke` 传参错误**：引用类型参数应直接放对象指针（`args[0]=type`），原来传 `&arg` 导致 `GetComponent(Type)` 在 Unity 内 SIGSEGV。
>
> **属性 / 等级修复（服务端镜像 `nordicandia-server:stats1`，已部署并真机验证）**：
> - 客户端每次 `/ws` `UpdateCharacterMetadataMessage` 都带 Offense/Defense/Recovery/NumMonsterKills/NumItemsLooted，
>   但 `RealtimeGateway.HandleMetadata` 只用了经验/银币/Opals → `CombatStats` 永远是建角时的 0。现 `SaveRealtimeProgress(..., CombatSnapshot)`
>   写入 `SerializedData.CombatStats`（0/非法值视为“未上报”保留旧值；击杀/拾取为增量累加）。Player Info 现显示真实属性（如 Watermelon 612 / 19.37K / 130.5）。
> - `Progression.MaxLevel = 200` 是服务端臆造的上限：客户端 `Calculator.CalculateLevelGains` 为闭式 `floor(round(((xp-350)/20)^(1/1.7), 6))`、无上限。
>   旧逻辑每次同步/登录把 Pumpkin（458k XP = 366 级）钉回 200。现 `LevelForExperience` 与客户端逐位一致、去掉上限；排行榜/Inspect/HUD 等级一致。
>   （未验证的副作用：被钉住期间客户端每次会话可能重复执行 `LevelUp(+166)`，如技能点等按升级发放的奖励可能被重复给予。）
>
> 已知限制："View Skills" 无反应（离线版处理被裁剪），不崩溃；InspectCharacter 的 Active/PassiveSkills 仍为空列表。
> 构建：`/tmp/mk_full10.py <out>`（在 `lib_full7.so` 上注入最新 stub + 全部 hook/Awake 补丁；仓库脚本仍无法把 email+leaderboard 串联在同一原版上）。

> ## 🎉 状态：在线模式 + 实时通道 + 经验持久化 **已全部打通**（本次会话最终验证）
>
> ```
> stub:    attempts=1  error=0  socket≠0  connected=1  ready=1
> [META]:  进度持续上传（实测 17 次/4 分钟，baseGain/applied/total 逐条递增）
> 服务端:  exp=3782.4  HasRealtimeProgress=True
> 等级:    Pumpkin 1 → 20（排行榜 rank 2, level=20, score=2995732）
> 重启app: exp 仍为 3782.4  ✓ 持久化验证通过
> ```
>
> ### 最后两个关键修复（用户指出的）
> 1. **进度泵缺 elapsed 参数**：`UpdateFixedAsync(float elapsed)` 从 `s0` 读差值（入口 `fmov s8,s0` @0x2E09E80），
>    原补丁调用 `NetSocket_update_fixed(0)` 使 `s0` 为垃圾值 → 上传计时器不可靠。
>    现改为 `il2cpp_resolve_icall("UnityEngine.Time::get_fixedDeltaTime()")` 取真实步长，传给两个泵。
> 2. **结果元组返回了"离线播放"**：`ws_result_trampoline` 复现 `_ConnectWithNewSocket_d__116.MoveNext`
>    的结果元组 `ValueTuple<bool,bool,bool> = (connected, playOffline, maintenance)`；
>    原实现把 `[sp,#0x38]`（playOffline）写成 1 → 调用方转去 `SignInNew(LocalDevice)`，丢失在线进度。
>    现改为 `connected = ws_finish()`、`playOffline = 0`、`maintenance = 0`。
>
> ### 进度上传的实际通路
> ```
> ws_fixed_update (挂在 UnityGame.FixedUpdate 0x26FFEB0，每周定步长)
>    → NetSocket.UpdateFixedAsync(dt) / UpdateFixed(dt)   （0x2E09E80 / 0x2E09F14）
>    → 客户端通过 /ws 发送 UpdateCharacterMetadataMessage
>    → 服务端 RealtimeGateway.HandleMetadata 应用经验
>      → [META] character=… baseGain=… applied=… total=…
>      → 写 world.json（Experience / HasRealtimeProgress / LastRealtimeUpdate）
>      → 回 UpdateCharacterMetadataResponse（含 ServerTime）
> ```


> 最后更新：本次会话结束
> 一句话结论：**实时 WebSocket 已经打通**（Envoy `code=101`、服务端 `[WS] open`/`pushed SeasonBuff`、连接可保持数分钟）；
> 经验持久化仍差"上传"这一步；另外发现并修复了服务端一个真实 BUG（离线奖励接口是空桩），
> 但用户点击【领取】时的报错是**客户端本地失败**（请求根本没发出），尚未解决。

---

## 1. 目标与症状

| 目标 | 状态 |
|---|---|
| 连上私有服务器 | ✅ |
| 邮箱/密码登录（服务端） | ✅ |
| Google 登录保持可用（不重签 APK） | ✅ |
| 排行榜显示 Steam 角色 | ✅（服务端接口正常） |
| 修改密码 | ✅ |
| **进入世界并游玩** | ✅（手动操作稳定；adb 自动化不稳） |
| **实时 WebSocket `/ws`** | ✅ **已稳定连通**（connected=1） |
| **经验上传 / 持久化** | ✅ **已打通**（exp=3782.4, rt=True, 重启后仍在） |
| **离线奖励领取** | ✅ **已修复**（真因：`ServerTime` 只随实时通道回流，通道通了时钟才同步，`TimeCheatingDetector` 才放行） |
| 独立 XAPK 分发 | ⚠️ 可构建，内容待更新（链路已验证：leaderboard(含online) → realtime） |

---

## 2. 本次会话新完成的关键工作

### 2.1 ✅ 实时 WebSocket **已打通**（重大突破）

Envoy 访问日志（`code=101` = Switching Protocols）：

```
ACCESS GET /ws?status=True&characterId=e9805d14-… HTTP/1.1 code=101 dur=316633ms
ACCESS GET /ws?status=True&characterId=e9805d14-… code=101 dur=434567ms
ACCESS GET /ws?status=True&characterId=e9805d14-… code=101 dur=213088ms
```

服务端网关生命周期完整：

```
[WS] open  user=e7641a52… character=e9805d14… online=True
[WS] pushed SeasonBuff to e9805d14…
[WS] closed …
```

- `server/RealtimeGateway.cs` 已实现：`/ws?status=&characterId=`，Bearer 鉴权，二进制 MessagePack `Envelope`（RequestId + Message union），支持请求/响应与服务器推送。
- **Envoy 必须 `allow_connect: true`**（client 用 HTTP/2 extended CONNECT 打开 `/ws`）—— 已修并上线（见 §3）。

### 2.2 ✅ 修复一个真实服务端 BUG：`ClaimCharacterOfflineRewards` 是空桩

```csharp
// 修复前（Services.Generated.cs，自动生成）
public UnaryResult<ClaimCharacterOfflineRewardsResponse> ClaimCharacterOfflineRewards(req)
    => UnaryResult.FromResult(Defaults.Create<ClaimCharacterOfflineRewardsResponse>());
// → 全零响应：FinalExperienceGained=0, FinalLevelsGained=0, NewOpals=0
```

后果：客户端领取离线奖励时拿到空响应 → 弹 "Error"；等级由服务端算（`Progression.LevelForExperience(AttrExperience)`），服务端经验恒为 0 → **经验条溢出但升不了级**。

**修复**（提交 `d382591`）：

```csharp
// GameStore.ClaimOfflineRewards —— 新增
① 应用请求里的经验增量（客户端自己算好离线收益后上报）
② 用 Progression.LevelForExperience 重算等级
③ 刷新 IdleProgress.LastActiveEpoch（下次离线时长从此刻算）
④ 更新 header.Level（EnterGameWithCharacter 会用到）
⑤ FromAdReward 时扣 OpalCost
⑥ 返回 FinalExperienceGained / FinalLevelsGained / NewOpals
```

契约：

```
ClaimCharacterOfflineRewardsRequest  { CharacterId, ExperienceGained, LevelsGained, FromAdReward, OpalCost }
ClaimCharacterOfflineRewardsResponse { FinalExperienceGained, FinalLevelsGained, NewOpals }
```

### 2.3 ✅ 修复 CI 真实故障 + 部署

```
CI 里 compose 绑定的 envoy.yaml 不存在 →
  OCI runtime create failed: not a directory → 整个构建 job 失败
修复：workflow 增加  cp deploy/lightsail/envoy.yaml "$NORD_HOME/envoy.yaml"

部署结果：CI run 36086773857 success；镜像 c411d93f36800d06010601fa461b0681d79f4704
         已上线；healthz=200；数据完好（Characters=7, Users=14）
```

### 2.4 ✅ 进度泵（realtime stub）解锁

`realtime_stub.c` 的 `ws_fixed_update` 原来在早期 return 之后才 `g_ticks++`：

```c
if(!g_ready||!g_socket||!Internal_is_connected(g_socket,0)) return;  // stub 自己的 socket 从未建立
g_ticks++;                                    // 永远到不了
Forget(NetSocket_update_fixed(0),0);          // 进度泵永不执行
```

**关键事实**：`NetSocket.UpdateFixed`（`0x2E09E80`）在整个 lib 里**调用者 = 0**（这个 build 省略了调用点）。
修改后（解除对 stub socket 的依赖）：

```
g_ticks: 0x67b (1659) → 0x6ba (1722)   ← 进度泵开始执行 ✓
```

---

## 3. 已修复 / 已交付（累计）

| 项目 | 证据 / 位置 |
|---|---|
| **Envoy `allow_connect: true`**（listener + cluster） | `deploy/lightsail/envoy.yaml`，提交 `1b53412`，已上线。客户端用 **HTTP/2 extended CONNECT** 开 `/ws`，原配置必被拒 |
| Envoy `grpc_http1_bridge` + Lua 修复（grpc-dotnet trailers-only 误判） | 同上 |
| gRPC 诊断日志（`gs/gm/ct/te`） | Envoy access log |
| TLS 补丁（`MobileTlsContext.ValidateCertificate`） | `server/patch_android_online.py` |
| gRPC over HTTP/1.1 补丁（`GrpcCall.ValidateHeaders` / `_RunCall`） | 同上 |
| Season 模式 UI 补丁 | 同上 |
| 服务端排行榜 JSON 接口 + `LeaderboardQuery` | `GET /api/leaderboards/{normal\|season}/overall?limit=N` |
| 服务端 `ChangeEmailPassword` | `LoginService.cs` |
| **服务端 `ClaimCharacterOfflineRewards`** | `GameStore.cs` + `CharacterService.cs`（`Services.Generated.cs` 里删掉空桩） |
| **CI：复制 envoy.yaml** | `.github/workflows/lightsail-image.yml` |
| **stub 工具链根因修复** | `_stub_symbols` 改为 `syms.update(found)`（此前 KeyError 导致 `mk_a1.py` 静默失败、所有测试都在推旧库） |
| **地址漂移 bug 修复** | STUB_VERSION 改用 `adrp/add g_dbg`（`g_dbg` 每次构建地址会变） |
| **测试铁律** | sha256 校验设备库 + `rm`/`cp` 换 inode + 探针安装校验 + 关键链路尽早 attach |
| **Frida 精确驱动游戏** | 见 §4.3（含必须的 IL2CPP thread attach） |

---

## 4. 核心资产（可复用）

### 4.1 运行时 IL2CPP 解析器（最重要）

注入 stub 在 `UIWindowManager.Update` 每帧调用，用 IL2CPP API 枚举类/方法并发布到
`g_meth_fn[] / g_meth_name[] / g_meth_n`，Frida 可直接读出**方法名 + 固定偏移**。

- 支持**按类名**匹配，也支持**按方法名反查类**（用它找到了 SignalR 协商类）。
- 彻底摆脱"预知地址"与"地址表不可靠"。
- 复用方式：改 `email_login_stub.c` 里 `sceq(cn, "NetSocket")` 的类名 → `bash build_stub.sh` → `python3 /tmp/mk_a2.py` → 用 `/tmp/mt.js` 读表。

### 4.2 已解析出的方法表（均为固定 lib 偏移）

**`SaveManager`（64）**
```
Update                          0x25D0CB0   ← 根因指令 0x25D0F8C 在此
SaveCurrentAccountAndCharacter  0x25D10CC
SaveCurrentAccount              0x25D180C
SaveCharacter                   0x25D18D8
InternalSaveCharacter           0x25D19C8
SaveCharacterSnapshotOrLegacy   0x25D2710
GetOfflineWriteRoute            0x25D1F18
ForceHeaderOnlyWhenGmacMissing  0x25D3FF4
ScheduleOnlineSaveIn            0x25D092C
Player_OnCharacterEnteredGame   0x25D085C
```

**`NetSocket`（64）**
```
ConnectWithNewSocket                    0x2E09050   （异步包装；体在 MoveNext）
EnsureSocketConnectivity                0x2E096A4
RetryConnection                         0x2E09620
Socket_Connected                        0x2E0920C
Socket_Closed                           0x2E09564
CloseSocket                             0x2E094B8
ResetCharacterExperienceSync            0x2E09A88   ← 经验同步走 socket
UpdateFixedAsync                        0x2E09E80   ← ★ 调用者=0（build 省略了调用点）
_ConnectWithNewSocket_d__116.MoveNext   0x2E0CE50
```

**SignalR 协商类（按 `get_WebSocketServerUrl` 反查）**
```
Start 0x34197AC  Get 0x341EC08  OnNegotiationRequestFinished 0x341DEF4  RaiseOnError 0x341E3CC
get_Url 0x341DDF4  get_WebSocketServerUrl 0x341DE04  get_TryWebSockets 0x341DE68
```
> 实测：全部**从未被调用**（客户端用自身路径直接开 `/ws`）。

**`UIWindowManager`（64）**
```
Update                        0x278D738
ShowWindow                    0x278E674
ShowInGameWindow              0x278EA08
ShowSocketConnectingDialog    0x279013C
HideSocketConnectingDialog    0x278D4EC
Player_OnCharacterLeftGame    0x278D4E4
ShowLoadingDialog             0x2790550
ShowErrorDialogOK             0x278D5F8
ShowDialogOK                  0x278EA8C
ShowDialogRetryContinue       0x27917A8
```

**`WindowCharacterList`（35）**
```
OnCharacterSelected           0x2575360
OnPlayClicked                 0x2576004   ← 世界入口
ReportLoadingProgress         0x25760B0
FetchCharacters               0x2573458
FetchOnlineCharacters         0x2573508
FetchOfflineCharacters        0x2573AC8
UpdateButtonStatus            0x2574E60
Awake 0x2572F34   Start 0x2573160
```

**`UnityGame`（40）**
```
Update       0x26FFE0C
FixedUpdate  0x26FFEB0   ← realtime stub 的进度泵挂这里
```

### 4.3 Frida 精确驱动游戏（本轮打通）

```
抓实例   → Hook Awake/Start                                          this ✓
取类     → il2cpp_object_get_class (0x2318508)                        klass ✓
取方法   → il2cpp_class_get_method_from_name (0x2317C9C)              MethodInfo ✓
线程     → il2cpp_thread_attach(il2cpp_domain_get()) (0x2318594/0x2318138)
           ← 【必须】否则 runtime_invoke 直接 "access violation"
调用     → il2cpp_runtime_invoke (0x231855C)                          ret=0x0 ex=0x0 ✓
```

### 4.4 关键地址

```
世界入口决策点（IsOnline 分支）   0x257D29C   cbnz w8,0x257DAE8(在线) / b 0x257E610(离线)
离线块里的本地设备登录调用        0x257E46C   UnityGame.SignInNew(0,true,true,null,null)
SPSM 抛异常点                     0x26FB774   "Cannot synchronize online account"
SPSM 在线守卫                     0x26FB754
PlayerAccount.get_IsOnline        0x2C0AEA4   AccountType(+0x30) ∈ {1,2,3}
PlayerAccount.RefreshAccountType  0x2C0CBDC
UnityGame.SignInNew               0x2701390
```

---

## 5. 根因链（全部已解决 ✅）

```
① 客户端【自己】打开 /ws（code=101，保持 3~7 分钟）        ✅ 已通
   · SignalR 协商类从未调用 → 不是走 SignalR negotiate
   · Envoy ACCESS: GET /ws?status=True&characterId=… code=101 dur=316633ms
   · 服务端: [WS] open user=… character=… online=True / [WS] pushed SeasonBuff
        ↓
② 经验上传依赖 NetSocket.UpdateFixed —— 之前【调用者 = 0】
   · 需要同时修两处【两个 bug】才能上传：
     (a) 差量参数丢失：UpdateFixedAsync(float) 从 s0 读 dt（fmov s8,s0 @0x2E09E80），
         UpdateFixed(double) 从 d0 读（fmov d8,d0 @0x2E09F14）
         → 旧 stub 传了 0 → s0 是垃圾 → 上传计时器永不推进
     修：ws_fixed_update 用 il2cpp_resolve_icall("UnityEngine.Time::get_fixedDeltaTime()")
         把真实 dt 传给两个泵
     (b) 跳板返回值错误：ws_result_trampoline 复刻
         _ConnectWithNewSocket_d__116.MoveNext 的 ValueTuple<bool,bool,bool>
         = (connected, playOffline, maintenance)；
         旧代码把 [sp,#0x38] (playOffline) 写成 1
         → 调用方改走 SignInNew(LocalDevice)，【在线进度被丢弃】
     修：connected=ws_finish()、playOffline=0、maintenance=0
        （同时正确复刻 strb wzr,[sp,#0x22] / strh wzr,[sp,#0x20] /
          strb wzr,[sp,#0x34]，TUPLE_MI 从 0x55B9548 载入）
   · 修复后实测：attempts=1 error=0 socket=0x… connected=1 ready=1
     4 分钟内 17 条 [META] 上传；world.json exp=3782.4 rt=True
     角色 Pumpkin 等级 1→20；重启 App 后经验仍在 ✅
        ↓
③ 等级由服务端决定
   · EnterGameWithCharacter 时服务端用 Progression.LevelForExperience(exp) 算 level
   · 现在服务端 exp 会增长 → 等级正常推进（1→20→21）✅
        ↓
④ 领取离线奖励报错 —— 真因 = TimeCheatingDetector
   · 服务端 ClaimCharacterOfflineRewards 曾是空桩（返回全 0）→ 已实现 ✅
   · 但点击时服务端收到请求数 = 0 ⇒ 客户端本地就失败了
   · 真因：TimeCheatingDetector 的时钟校验；ServerTime 只随实时通道的
     UpdateCharacterMetadataResponse 下发，实时通道未通时从未同步过时钟
   · /ws 打通后 → 领取成功：
     [OFFLINE] character=e9805d14… +exp=339.8 level=21 opals=0 → 200 ✅
```

### 两条最容易踩的坑（务必记住）
```
· ws_result_trampoline 必须返回 playOffline=false
  返回 true → 客户端改走 SignInNew(LocalDevice) → 在线进度全部丢弃
· 进度泵必须喂真实 elapsed（Time.fixedDeltaTime）
  传 0 → s0/d0 是垃圾 → 上传计时器永不推进
```

### 重要架构结论
```
在线进度上传 = 实时 socket（/ws）       ← 与 SaveManager.IsOnline 是两套独立机制
SaveManager.IsOnline 只是"保存路由"开关：
    true  → 跳过本地保存（0x25D0F8C），保存委托给 socket
    false → 走本地保存

服务端 RealtimeGateway 会写 HasRealtimeProgress / Experience
证据：Steam 角色 0412ea15  exp=79740.3  rt=true (2026-09-20)
      所有 device 账号角色：exp=0  rt=false
```

---

## 6. 历史阻塞记录（以下内容已被后续修复 superseded）

> **当前情况**：Leaderboard struct 数组渲染现已正确（不是 List cast 问题）；stub `.bss` 已移至 `0x5DE6000`；
> 设备确认 ItemOperation 会由 `NetClient.UpdateAsync` 定时 flush。tap-to-inspect 也已在真机验收（见文首）。
> 下列调查过程保留作历史记录，不应再按旧结论实施。

| # | 阻塞 | 说明 |
|---|---|---|
| 1 | **游戏内排行榜渲染** | 数据链路已实测正确，但 **容器类型错**：window 会把返回值 `castclass` 成 `List<FakeLeaderboardRow>`，数组转不过去 → 空榜/垃圾行 |
| 2 | **排行榜偶发崩溃** | stub 内 `il2cpp_array_new(klass,…)` 的 `klass` 是坏值 → 缓存的 `g_rowKlass`（stub `.bss` @ `0x5DDE100`）疑被 lib 覆盖 |
| 3 | **装备登出后消失** | 客户端【不发 `ItemOperation`】；在线通道只有经验/银币/Opal，没有物品字段 → 物品无上传路径 |
| 4 | **UI 自动化不可靠** | adb 触摸不稳（见 §7），**已改用手动操作 + Frida 驱动** |
| 5 | **产品化未开始** | 登录 UX、XAPK 重建、安全收紧、干净设备验收 |

### 6.1 排行榜：反证过程与正确修法
```
数据侧（已实测正确，用探针 dump 返回数组）：
  klass=FakeLeaderboardRow[]  maxlen=5
  [0] rank=1 val=138  [1] rank=2 val=23  [2] rank=3 val=1
  [3] rank=4 val=1    [4] rank=5 val=1
  （探针打印 name="?" 是探针自己用 readUtf8String 读了 UTF-16，不是 stub 的问题）

反证：把返回对象同时做成两种布局（数组 +0x10 指向自身当 List._items、
      +0x18 保持 count 当 List._size）→ 排行榜变成【完全空白】
  ⇒ 说明 window 做了真正的类型转换（castclass 到 List<T>），
    数组【转不过去】，所以 hack 无效 → 容器必须是真正的 List<T>

正确修法（不要替换 Build*）：
  ① 跳板里【先调用原版 Build*】→ 拿到它自己 new 的
     List<FakeLeaderboardRow>（类型天然正确）
  ② 再【就地改写】该 List 每行的 5 个字段
     （List 布局 _items@+0x10 / _size@+0x18；
       行字段 <Rank>0x10 <Name>0x18 <ClassId>0x20 <Value>0x28 <IsPlayer>0x30）
  ③ 空榜返回原版给你的空 List（永不 null、永不数组）

崩溃修法：
  把 lbstub 的 .bss 移到 realtime 补丁已经映射的区间（0x5DE4000+），
  并让 patch_android_leaderboard.py 也执行 RW p_memsz 扩展

服务端侧（已排除）：
  GET /api/leaderboards/{mode}/{category}?limit=&player=&cls=
  · class 榜【必须带 &cls=】否则 404 "unknown leaderboard season/class/"
    cls=warrior → count=5（Watermelon lvl138 rank1）；cls=mage → count=0
  · 所以【不是服务端缺接口】，早先误判为“职业榜为空”是测试时漏了 &cls=
  · 关键：leaderboard_stub.bin 是预编译 blob，必须用 device/stub/build_lbstub.sh 重建，
    否则改了 .c 也不会生效（这个坑踩过一次）
```

### 6.2 装备登出后消失：物品没有上传通道
```
服务端侧（已排除）：
  InventoryService.ItemOperation      → GameStore.ApplyItemOperations   ✅ 已实现
  GameStore.ApplyItemOperations 处理 AddItem/DeleteItem/MoveItem/SortItem
  并【真正写回】：c.Data = Pack(data)   ✅

客户端侧（真因）：
  最近 40 分钟端点统计：
    22 GetUserAccountData / 17 GetCharacterList / 17 EnterGameWithCharacter
    11 LoginWithStandaloneDeviceIdAsync / 9 /ws / 7 OnDungeonRunStarted
     2 AllocateCharacterAttributes / 1 ClaimCharacterOfflineRewards
     1 AssignPassiveSkill / 1 AssignActiveSkill
     0 ItemOperation        ← ★ 一次都没发

  在线路径唯一的“进度上传”通道是 /ws 的 UpdateCharacterMetadataMessage
    字段：BaseExperienceGained / NewExperience / NewSilver / NewOpals
    【D 没有任何物品字段】
  物品要么走 gRPC ItemOperation（客户端没调），
  要么走本地存档 —— 而 SaveManager.Update 的 0x25D0F8C
  在“账号在线”时按设计【跳过本地保存】
  ⇒ 装备变更落进了“两不管”的空档

正确修法：
  · hook 客户端装备/背包变更点，主动发出 ItemOperationEntry
    （MoveItem / AddItem）或直接调用服务端 ApplyItemOperations
  · 不推荐扩展实时协议（要同步改客户端序列化，风险大）
  · 先 hook 确认客户端到底何时才发 ItemOperation
    （是否只在“关闭背包/退出游戏/定时保存”时 flush）
```

---

## 7. 为什么坐标不稳定（已核实硬件事实）

| 检查 | 结果 |
|---|---|
| tap 坐标空间 | `[0,0][2340,1080]`（uiautomator SurfaceView bounds）✓ |
| letterbox | 无，`mAppBounds=Rect(0,0-2340,1080)` ✓ |
| 旋转 / 截图 | `ROTATION_90`，截图 2340×1080，一致 ✓ |
| 挖孔 | 竖屏顶部中央 `Rect(511,0-569,103)`；横屏后移到侧边，**不影响坐标** ✓ |

**真实原因（5 条）**
1. **屏幕序列不稳定（主因）**：同一 tap 因客户端起始状态不同而落到不同窗口
2. **Unity 触摸需要按压时长**：`input tap` 近乎零帧按压会被丢弃（用 `input swipe x y x y 120`）
3. **截图→坐标换算误差**：缩放比 x/y 不一致 + 在压缩图上目测
4. **过渡动画期间点击被吞**
5. **没有可查询的视图层级**（整个游戏在一个 `SurfaceView` 内）

→ 正解：**状态驱动 + Frida 直接调用游戏方法**（§4.3）。**手动操作已验证"进世界"完全没问题。**

---

## 8. 环境与命令速查

### 构建（注意顺序！）
```bash
# realtime 补丁必须【挂在 online 补丁之后】
python3 server/patch_android_online.py  <原版 libil2cpp.so>  /tmp/rt_c1.so
python3 server/patch_android_realtime.py /tmp/rt_c1.so        /tmp/lib_rt3.so \
        --host prod.038c3288.nip.io          # = 3.140.50.136 的 nip.io 别名
# 排行榜（游戏内显示正确榜用）：
python3 server/patch_android_leaderboard.py ...
```
`patch_android_realtime.py` 关键动作：
```
ConnectWithNewSocket 0x2E09050 → ws_connect_trampoline (b)
MoveNext WAIT        0x2E0CED4 → ws_wait_trampoline    (b)
MoveNext RESULT      0x2E0D064 → ws_result_trampoline  (b)
UnityGame.FixedUpdate 0x26FFEB0 → ws_fixed_update      (b)
BestHTTP TLS 验证器   0x2FF634C → ret
扩展 RW PT_LOAD memsz 使 stub 的 state (0x5DE4000) 被映射
```

### 部署（铁律）
```bash
adb shell am force-stop com.IterativeStudios.Nordicandia
adb push <lib> /data/local/tmp/l.so
adb shell "su 0 rm -f '$LIB'; su 0 cp /data/local/tmp/l.so '$LIB'; su 0 chown system:system '$LIB'; su 0 chmod 644 '$LIB'"
adb shell "su 0 sha256sum '$LIB'"     # 必须 == 本地 sha
```

### 服务端
```bash
ssh -i /tmp/ls.pem ubuntu@3.140.50.136
sudo docker logs nordicandia-envoy-1        # 看 ACCESS ... /ws
sudo docker logs nordicandia-server-1       # 看 [WS] open/closed、[OFFLINE]、[ENTER]
sudo docker logs --since 10m ... | grep -E '\[WS\]'
curl http://127.0.0.1:8081/api/leaderboards/season/overall?limit=10
```

### 部署新版服务端（CI 构建 → Lightsail）
```bash
git branch -f deploy/lightsail main && git push -f origin deploy/lightsail
gh workflow run lightsail-image.yml --ref deploy/lightsail
gh run list --limit 3 ; gh run view <id> --log-failed
gh run download <id> -D /tmp/art
scp -i /tmp/ls.pem /tmp/art/*/nordicandia-server.tar.gz ubuntu@3.140.50.136:/tmp/
# 服务器上：
gunzip -c /tmp/nordicandia-server.tar.gz | sudo docker load
sudo sed -i 's|^NORD_IMAGE=.*|NORD_IMAGE=nordicandia-server:<sha>|' /opt/nordicandia/.env
sudo docker compose --project-directory /opt/nordicandia -f /opt/nordicandia/compose.yaml up -d
```

### 探针（`/tmp/*.js`，用 `python3 /tmp/runjs.py <script> <秒>` 运行）
```
rt.js       realtime stub 状态（g_attempts/error/socket/connected/ready/ticks）
mt.js       方法表（改类名/匹配规则即可复用）
err2.js     错误弹窗调用栈
drive2.js   Frida 驱动 UI（含 IL2CPP thread attach）
fields.js   实例字段 dump（含类名）
ol.js / ld.js / wm2.js / neg.js / nc.js / pt3.js / mn2.js / acc.js / off.js
```

### 导航坐标（横屏 2340×1080，仅作参考，优先用 Frida）
```
Play(主菜单) 1170,1017 | Next 2141,1000 | Back 250,1000
角色列表 Play 2144,936 / 2144,960 | 关弹窗 1166,762
```

---

## 9. 下一步（按优先级）

> **最新行动项**：tap-to-inspect 已交付并真机验收。剩余：InspectCharacter 返回角色属性（Offense/Defense/Recovery 目前 0.0）；
> 可选修复 "View Skills"；email stub `.bss` 移出 retail .bss（`0x5DE7000`）；把 `/tmp/mk_full10.py` 的串联流程并入仓库脚本；GitHub 认证恢复后提交推送。
> 以下列表是修复前的历史计划，供追溯，不代表当前状态。

```
1. 【已完成】修排行榜（两个小改造，每步都有明确验证点）
   a. 借原版 List：跳板【先调原版 Build*】拿真正的 List<FakeLeaderboardRow>，
      再【就地改写】每行 5 个字段（List 布局 _items@+0x10 / _size@+0x18）
      → 验证：Overall 显示 Watermelon lvl138 rank1、Pumpkin rank2
   b. bss 移位：lbstub 的 .bss 从 0x5DDE100 移到 0x5DE4000+（realtime 补丁已映射区间），
      并让 patch_android_leaderboard.py 也扩展 RW p_memsz
      → 验证：清 logcat 后连点 Overall + Warrior + Mage，无 SIGSEGV
      （空榜永不 null、永不数组 —— 之前的数组 hack 已被反证否定）

2. 【高】修装备登出消失
   · 先 hook 客户端确认它【何时】才发 ItemOperation
     （是否只在关背包/退出游戏/定时保存时 batch flush）
   · 然后 hook 装备/背包变更点 → 主动发出 ItemOperationEntry(MoveItem/AddItem)
   · 服务端侧已就绪（InventoryService.ItemOperation + ApplyItemOperations 已实现且正确写回）

3. 【中】产品化
   · 登录 UX（免手工凭证文件；Register 与 Login 分离）
   · XAPK 重建（链路：patch_android_leaderboard → patch_android_realtime，
     需要 email 登录则再加 patch_android_email_login 做基底）
     ⚠ 注意：leaderboard / email_login 两个脚本【各自内含 online 补丁】且
       拒绝已打补丁的输入 → 只能二选一做起头，不能串联
     ⚠ 三个 stub 占用不重叠的 cave 区间：
        email 0x344EC24 / leaderboard 0x3451000 / realtime 0x3456000
   · 安全收紧（关 NORD_AUTH_ALLOW_UNVERIFIED_PROVIDERS；8081 加 token/TLS）
   · 干净设备端到端验收（必须包含“重启后经验仍在”）

4. 【低】纯本地设备上的 adb 自动化仍不可靠 → 用手动操作 + Frida 驱动（见 §7）
```

---

## 10. 完成度评估

| 维度 | 完成度 |
|---|---|
| 技术研究 / 根因 | **~99%** |
| 功能可用性 | **~88%** |
| 产品化 / 可发布 | **~55%** |

能登录、选模式、建角色、**进世界游玩**、**实时通道连通**、**经验持久化 + 升级**、
**领取离线奖励**、改密码、拿排行榜接口 —— 全部已通。

剩余：**游戏内排行榜渲染**、**装备持久化**、登录 UX、XAPK 重建、安全收紧、干净设备验收。

---

## 11. 一句话交接

> `/ws`、经验/等级持久化、离线奖励、排行榜渲染和装备操作 flush 均已修复并验证。
> 点排行榜玩家行打开 Player Info 已交付并真机验收（行 `Button (Inspect)` 左键 hook → name→Guid → 实例化 Inspect Player prefab →
> `InspectPlayer`；两个 Awake 自毁补丁改 `ret`）。服务端 JSON Row 含 `characterId` 并已部署。工具链（IL2CPP 注入、Frida、补丁 ELF 检查）可复用。
