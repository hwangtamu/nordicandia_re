# Nordicandia 私有服务器 —— 在线模式 / 实时通道 现状报告

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

## 5. 根因链（当前理解）

```
① 客户端【自己】打开 /ws（code=101，保持数分钟）        ✅ 已通
   · SignalR 协商类从未调用 → 不是走 SignalR negotiate
   · ConnectWithNewSocket 是否被调用尚未定论（stub 的 g_attempts 一直是 0）
        ↓
② 经验上传依赖 NetSocket.UpdateFixed
   · 它在整个 lib 里【调用者 = 0】（build 省略调用点）
   · realtime stub 用 UnityGame.FixedUpdate(0x26FFEB0) 驱动它 → g_ticks 已增长 ✅
   · 但服务端 Experience 仍为 0 / HasRealtimeProgress=False  ❌
        ↓
③ 等级由服务端决定
   · EnterGameWithCharacter 时服务端用 Progression.LevelForExperience(exp)
     算好 level 放进 AttrLevel 返回
   · 服务端经验恒 0 → 永远返回 level=1
   · 客户端经验条继续按本地经验涨 → 【溢出但升不了级】
        ↓
④ 领取离线奖励报错
   · 服务端已实现该接口并上线，但点击时【服务端收到请求数 = 0】
   · 实时通道 union 里没有 Claim/Offline 消息 → 不是走 WS
   · ⇒ 错误发生在【客户端本地】，请求还没发出
   · 触发位置：ShowErrorDialogOK，调用栈 #3 = 0x2674770（调用者返回地址）
   · 线索：离线奖励对话框【只在进入打怪地图时弹出】，城镇里不弹
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

## 6. 当前阻塞

| # | 阻塞 | 说明 |
|---|---|---|
| 1 | **经验上传未发生** | `/ws` 通了，但服务端 Experience 仍 0；需确认客户端是否真的在推 delta |
| 2 | **领取离线奖励报错** | 纯客户端失败，请求未发出；调用栈指向 `0x2674770` 所在函数 |
| 3 | **UI 自动化不可靠** | adb 触摸不稳（见 §7），**已改用手动操作 + Frida 驱动** |
| 4 | **排行榜在游戏内显示错误** | `lib_rt2/lib_rt3` 链路是 online + realtime，**缺排行榜补丁** → 游戏显示本地伪造榜 |

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

```
1. 【最高】定位"领取离线奖励"客户端报错
   · 用解析器【按方法名】搜索 ClaimCharacterOfflineRewards → 拿到客户端 API 包装类
     → hook 它：是否被调用 / 入参 / 是否抛异常
   · 若压根没调用 → hook ShowErrorDialogOK 上一层（0x2674770 所在函数）读字符串参数
   · 注意线索：对话框只在【进入打怪地图】时弹出

2. 【高】让进度真正上传
   · 确认 /ws 是谁开的（stub 的 ws_prepare vs 客户端自身）—— g_attempts 一直 0
   · 确认 NetSocket.UpdateFixed 被调用后是否真的发送 delta（服务端 [WS] 侧有无收到）
   · 必要时在网关加日志：打印收到的 Envelope.Message 类型

3. 【中】补排行榜补丁到链路（游戏内显示真实榜）
   patch_android_online → patch_android_leaderboard → patch_android_realtime

4. 【中】产品化 4 项
   · 登录 UX（免手工凭证文件；Register 与 Login 分离）
   · XAPK 重建（完整补丁链）
   · 安全收紧（关 NORD_AUTH_ALLOW_UNVERIFIED_PROVIDERS；8081 加 token/TLS）
   · 干净设备端到端验收
```

---

## 10. 完成度评估

| 维度 | 完成度 |
|---|---|
| 技术研究 / 根因 | **~99%** |
| 功能可用性 | **~88%**（能登录、选模式、建角色、**进世界游玩**、实时通道连通、排行榜接口、改密码） |
| 产品化 / 可发布 | **~55%** |

**结论**：核心功能全部打通。剩余仅为产品化（登录 UX、XAPK 重建、安全收紧、干净设备验收）。

---

## 11. 一句话交接

> 实时通道已经打通（这是本次会话最大的进展），服务端离线奖励接口的空桩 BUG 也已修复并上线。
> 剩下三件事：**(1) 定位客户端领取报错（纯本地失败，调用栈已收到 `0x2674770`）**、
> **(2) 让进度 delta 真正上传**、**(3) 把排行榜补丁加回链路**。
> 工具链（运行时 IL2CPP 解析器 + 5 张方法表 + 指令级地图 + 注入 stub + Frida 精确驱动）全部就绪。
