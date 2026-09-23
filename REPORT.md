# Nordicandia — Reverse Engineering Report

**Target:** `Nordicandia_+Semi+Idle+RPG_1.9.3_APKPure.xapk`
**Package:** `com.IterativeStudios.Nordicandia`
**Version:** 1.9.3 (versionCode 507033) · minSdk 25 · targetSdk 36
**Engine:** Unity **6000.3.7f1** / **IL2CPP** (ARMv7) · Addressables
**Metadata version:** 39 (`global-metadata.dat`)

---

## 1. Package layout (XAPK)

| Split | Size | Contents |
|---|---|---|
| `com.IterativeStudios.Nordicandia.apk` | 164 MB | base APK: manifest, dex (`classes.dex`…`classes11.dex`), resources, `assets/bin/Data` (Unity engine data + `global-metadata.dat`) |
| `config.armeabi_v7a.apk` | 41 MB | native libs incl. **`libil2cpp.so` (80 MB)** + `libunity.so` |
| `UnityDataAssetPack.apk` | 490 MB | Addressables bundles (game data, localization, textures, audio, dungeon themes) |
| `icon.png`, `manifest.json` | — | XAPK metadata |

---

## 2. Toolchain used

* **apktool 2.11.1** — manifest / resources
* **jadx 1.5.2** — Java/Kotlin (1,645 files)
* **Il2CppInspectorRedux 2026.2 (Legacy CLI)** — metadata v39 support (Il2CppDumper 6.7.46 caps at v31)
* **UnityPy 1.25.3** — Addressables bundle extraction
* **capstone / pycryptodome / pyelftools** — native disasm & crypto
* **.NET 10 runtime** — to run Il2CppInspector

---

## 3. IL2CPP dump artifacts

Generated with `Il2CppInspectorRedux.Legacy.CLI`:

| Artifact | Path | Notes |
|---|---|---|
| C# type/method model | `il2cpp_out/dump.cs` | ~312k lines, all classes/fields/method signatures + RVA ranges |
| Shim assemblies | `il2cpp_out/dll/*.dll` | 171 dlls (~46 MB) for dnSpy/ILSpy type inspection |
| Structured metadata | `il2cpp_out/metadata.json` | address map, 170,518 methods, 37,138 string literals |
| IDA/Ghidra scripts | `il2cpp_out/il2cpp.py`, `il2cpp_out/cpp/` | symbolizers for native method-body analysis |

Main game namespaces:

```
Game  Game.Definitions  Game.Items  Game.Skills  Game.Affixes  Game.AI
Nordicandia(.Client|.Client.Net|.Client.IAP|.Client.Save|.Client.Offline|.Migration|.SaveStore|.UI)
SharedNet.Constants.Game   Client.Net   Common.Types
```

Networking uses **MagicOnion (gRPC/HTTP2)** + **MessagePack**. Services found:

`ILoginServiceApi, ICharacterServiceApi, IInventoryServiceApi, IStoreServiceApi,
ICatalogServiceApi, IGuildServiceApi, IGuildSiegeServiceApi, IPartyServiceApi,
ILeaderboardServiceApi, ITournamentServiceApi, IVirtualCurrencyServiceApi,
IOfferingServiceApi, ICharacterPowerServiceApi, ICharacterGameEventServiceApi,
IGameModeServiceApi, IGameModeUserServiceApi, IModeration/Moderator, ISocial,
IPlayfabServiceApi, IUserServiceApi, IUserLinkedAccountServiceApi,
IUseItemServiceApi, IMigrationServiceApi, IChannelServiceApi`

**Server hosts (string literals):** `prod.nordicandia.net`, `staging.nordicandia.net`, `nordicandia.net`

---

## 4. Recovered encryption / "CryptorEngine"

`Game.Utils.SerializeString/DeserializeString` → `CryptorEngine`:

```
Algorithm : TripleDES (System.Security.Cryptography.TripleDESCryptoServiceProvider)
Mode      : ECB
Padding   : PKCS7
Key       : MD5(UTF8("iloveburgers"))            <-- 16 bytes (2-key 3DES)
useHashing: true (hard-coded)
```

* **Game definitions** are stored as `Base64(3DES(json))` TextAssets.
* **Saves / legacy saves** use the identical cipher (`CryptorEngineSaveCipher`, `CryptorEngineLegacyCipher`, both call `CryptorEngine.*ToBytes(..., true)`).

A standalone tool is at **`decrypt_nordicandia.py`** (decrypt/encrypt).

---

## 5. Decrypted game data (all 37 definition files)

Extracted from `gamedata_definitions_assets_all_*.bundle`, all now valid JSON in
**`gamedata_decrypted/`**:

| File | Count | File | Count |
|---|---|---|---|
| Items | 608 | Affixes | 1,837 |
| ItemAffixes | 626 | ItemTypes | 166 |
| Powers | 159 | PowerMasteries | 297 |
| Monsters | 136 | MonsterTypes | 30 |
| Buffs | 273 | Worlds | 36 |
| CharacterAvatars | 234 | CharacterAvatarFrames | 119 |
| Quests | 47 | Conversations | 66 |
| Achievements | 39 | Brains | 34 |
| ItemSets | 11 | Minions | 5 |
| CharacterClasses | 8 | CharacterRaces | 8 |
| GuildBadges | 77 | LootFilterItemTypes | 114 |
| Tags | 57 | plus Droprates / GameBalance / AffixGroups / GlobalMonsterAffixes … |

Sample (CharacterClasses): `Warrior, Mage, …` with ActiveSkills/PassiveSkills/Tactics GUID lists.
Sample (Items): `HandAxe` (TypeId, NameTranslationKey, Image, AffixIds, DropPool, …).

These definitions are the authoritative game balance data (item generation, affixes, loot tables, monster scaling, skills).

---

## 6. Saves / profile

* **`Nordicandia.SaveStore`** (native lib) + **`ClientSaveStore`** manage profile containers:
  `GameProfileEnvelope`, `CampaignStateDocument`, `PersonalBestStateDocument`,
  `CompleteEditionStateDocument`, `PurchasedProductsStateDocument`, `ProcessedOrdersStateDocument`.
* Legacy import path exists (`ImportLegacyValidated`, `TryLoad*FromContainer`) with an
  `ONLINE_PROFILE_WATERMARK = "deployed-surface-fallback"`.
* Save files can be decrypted with the same `iloveburgers` TripleDES key.

---

## 7. Anti-cheat & client hardening

* **CodeStage Anti-Cheat Toolkit (ACTk.Runtime)** — `ObscuredPrefs`, `ObscuredFile`,
  `ObscuredCheatingDetector`, `SpeedHackDetector`, `InjectionDetector`,
  `SpeedHackProofTime`, `ObscuredTypes*` (int/float/string/etc.).
* **`Nordicandia.Client.EncryptedPlayerPrefs`**.
* Messages: `"[ACTk] Empty key can't be used for string encryption or decryption!"`,
  encrypted-file read guards.

---

## 8. Monetization / analytics / identity

* **IAP:** Unity Purchasing (`com.unity.purchasing 5.4.2`) +
  `ClientIAPManager`, `ClientIAPNetManager`, `ClientReceiptValidatorService`
  (remote receipt validation).
* **Ads:** AppLovin MAX, Unity LevelPlay (ironSource `iads`), Facebook Audience,
  Mintegral (`mbridge`).
* **Identity/messaging:** Firebase (project `nordicandia-68376701`,
  project number `774502078866`, app id `1:774502078866:android:da77c254f547050d69894b`),
  Firebase Messaging/Crashlytics, Google Play Games, Game Center, Facebook SDK.
* **Analytics:** Unity Analytics/Remote Config/Live Content, Firebase, Facebook,
  GameAnalytics, Adjust, AppMetrica.
* **API key (google-services-desktop.json):** `AIzaSyCp-02Q7NJ83WJzFi1yhT8nP5crDz0Gnfs`

---

## 9. Java/Kotlin side

The dex layer is **only third-party SDK glue** (Unity player, Firebase, Facebook,
AppLovin/LevelPlay, Adjust, AppMetrica, Play Core/Integrity). No game logic is in Java —
all gameplay is IL2CPP in `libil2cpp.so`. Decompiled sources: `jadx_out/`.

Entry activity: `com.google.firebase.MessagingUnityPlayerActivity`
(`unityplayer.UnityActivity=true`). Manifest also registers Facebook/Adjust providers,
AppLovin/AdMob ad IDs, Play Integrity.

---

## 10. Where the artifacts live

```
/tmp/nordicandia_re/
├── REPORT.md                      <- this file
├── decrypt_nordicandia.py         <- TripleDES data/save decryptor
├── extracted/                     <- libil2cpp.so, global-metadata.dat, classes.dex
├── il2cpp_out/
│   ├── dump.cs                    <- full C# model (312k lines)
│   ├── metadata.json              <- structured metadata
│   ├── dll/                       <- 171 shim assemblies
│   ├── il2cpp.py, cpp/            <- Ghidra/IDA symbolizers
│   └── il2cpp.py
├── gamedata/                      <- raw (encrypted) TextAssets
├── gamedata_decrypted/            <- 37 decrypted JSON definition files
├── bundles/                       <- Addressables bundles
├── jadx_out/                      <- decompiled Java/Kotlin
└── apktool_out/                   <- decoded manifest/resources
```

## 11. Hosting / infrastructure

### Game backend — `nordicandia.net`

| Item | Value |
|---|---|
| Production | `prod.nordicandia.net` |
| Staging | `staging.nordicandia.net` |
| Port / protocol | **443** — HTTPS / WSS / **gRPC (HTTP/2)** |
| Front | **Cloudflare** (AS13335) — anycast `104.21.62.32`, `172.67.219.104`, `2606:4700:3035::6815:3e20`, `2606:4700:3035::ac43:db68` |
| Nameservers | `grant.ns.cloudflare.com`, `kira.ns.cloudflare.com` |
| Registrar | **Cloudflare, Inc.** (IANA 1910), registered 2022-01-02, expires 2028-01-02 |
| TLS | Cloudflare Universal SSL — issuer *Google Trust Services WE1*, `nordicandia.net` + `*.nordicandia.net` |
| Origin | **hidden by Cloudflare proxy** (403 with `server: cloudflare` to direct HTTP; no origin IP in client) |

Client code has no hard-coded origin IP. The modern client talks to the backend only over
port 443 (`InternalWebSocket(..., port = 443, secure = true)`) and MagicOnion gRPC, so
Cloudflare terminates all public traffic; the real origin cannot be identified from outside.

### Website / community — `nordicandia.com`

| Item | Value |
|---|---|
| Host | `81.177.135.38` — **Jino.ru**, Moscow, RU (`srv34-h-st.jino.ru`, AS8342 JSC RTComm.RU) |
| Nameservers | `ns1..ns4.jino.ru` |
| Registrar | Hostinger operations, UAB; registered 2020-12-15 |
| TLS | Let's Encrypt (`nordicandia.com`, `www.nordicandia.com`) |
| Hosts | `community.nordicandia.com` (forum), `www.nordicandia.com` |

### Other services

* **Firebase / Google Cloud** — project `nordicandia-68376701` (auth, analytics, Crashlytics, storage bucket `nordicandia-68376701.appspot.com`).
* Studio web presence: `nordicandia.com` (Jino, RU); backend domains registered through Cloudflare.

## 12. Reconstructed private server

A protocol-compatible MagicOnion backend was built from the APK metadata and verified
end-to-end. See **`server/README.md`**.

| Piece | Detail |
|---|---|
| Contracts | 515 DTOs + 24 service interfaces auto-generated from the IL2CPP metadata (`server/Nordicandia.Contracts/GeneratedContracts.cs`) via a Cecil generator |
| Host | ASP.NET Core (Kestrel, HTTP/2 h2c) + **MagicOnion.Server 5.1.8** + MessagePack 2.x, port 50051 |
| Implemented | `ILoginServiceApi` (accounts + sessions, all login variants) |
| Stubbed | the other 23 services return valid default DTOs |
| Verified | login, `GetCharacterList`, `GetRelevantLeaderboards` over gRPC |

**Option B (done):** the host literals in `global-metadata.dat` were patched in place
(`prod.nordicandia.net` -> `ab.10-0-2-2.sslip.io`, `staging.nordicandia.net` ->
`abcde.10-0-2-2.sslip.io`), all three splits re-signed with one key, and a ready XAPK
built at `patched/Nordicandia_1.9.3_private.xapk`. The pipeline is ABI-generic
(`server/patch_xapk.py`). Client TLS validation is a no-op, so a self-signed cert works;
signatures verified. Only blocker to on-device testing: the downloaded XAPK is
`armeabi-v7a` and the test device (Pixel 9 Pro) is 64-bit only.

**Critical wire facts recovered** (needed for APK interop):
* gRPC path uses the **short interface name**: `/ILoginServiceApi/LoginWithStandaloneDeviceIdAsync`
  (not the `SharedNet.Api.` namespace).
* DTO keys are either string (`[Key("Id")]`) or int (`[Key(0)]`); both are preserved.
* MagicOnion major version pinned to **5.1.8** by the client's `DynamicClient`/`RawMethodInvoker`.
* `global-metadata.dat` string literals are where client host/version strings live.

### Key takeaways
1. Metadata v39 / Unity 6000.3 required a newer dumper than Il2CppDumper — Il2CppInspectorRedux worked.
2. The entire game data set was recovered in cleartext; only a hard-coded
   `MD5("iloveburgers")` TripleDES-ECB layer protected it.
3. Gameplay logic lives entirely in native IL2CPP; use the generated Ghidra/IDA
   scripts + `dump.cs` to map addresses to C# methods for method-body analysis.

## 13. Android online login — working end-to-end

The genuine Play/APKPure ARM64 build was obtained (`apkeep -a
com.IterativeStudios.Nordicandia -d apk-pure .`) and its `global-metadata.dat`,
`libil2cpp.so` (sha256 `529bd257…`) and signer (`O=Google Inc.`) match the copy in
`dist/`, confirming the client under test is unmodified. On Android the client has
**no HTTP/2 transport**: `Grpc.Shared.HttpHandlerFactory.CreatePrimaryHandler`
constructs `System.Net.Http.HttpClientHandler`, which Unity resolves to
`MonoWebRequestHandler` (HTTP/1.1 only). There is no `SocketsHttpHandler`
implementation and no `GrpcWebHandler`. grpc-dotnet itself ships an Android warning
recommending `SocketsHttpHandler` / `UseNativeHttpHandler=false`, neither of which
this build can use. This is a client limitation, so the fix is a protocol bridge on
the server plus two small client checks bypassed.

### Server side
* **Envoy** (`nordicandia-envoy-1`) terminates TLS and runs
  `envoy.filters.http.grpc_http1_bridge` in front of Kestrel h2, so the HTTP/1.1
  gRPC request is upgraded to HTTP/2 and the h2 response is bridged back to
  HTTP/1.1. `/ws` is routed separately for the realtime WebSocket.

### Client side (`server/patch_android_online.py`)
Applied to the original `libil2cpp.so` (virtual addresses):

| Purpose | VA | Change |
|---|---|---|
| Trust self-signed cert | `0x04326344` | `MobileTlsContext.ValidateCertificate` -> `mov w0,#1 ; ret` |
| Init MagicOnion provider | `0x03E750E0` | branch `GrpcChannelProvider.get_Default` into an on-demand stub at `0x0344EBFC` |
| Accept HTTP/1.1 response | `0x03E58C28` | `GrpcCall.ValidateHeaders`: skip `Version.op_LessThan(version, Http2Version)` |
| Default missing status | `0x03D974E4` | `GrpcCall._RunCall`: force success when the `grpc-status` trailer is absent |

The script now byte-for-byte reproduces the tested library
(sha256 `6163045a2e914248…`). Result on the `galaxy_s25` AVD (arm64, API 35,
host GPU) against `prod.038c3288.nip.io` via Envoy:

```
GetNonce -> LoginWithStandaloneDeviceIdAsync -> SkipMigrationAsync
         -> GetUserAccountData -> GetCharacterList
```

All return HTTP/2 200 `grpc-status: 0`, and the client reaches **Game Mode** and
**Select Race** (character creation). Online login on Android is fully functional.

## 14. Android character creation — Normal vs. Season

Both game modes were exercised on the patched Android client and the result was
verified in the server's persisted `world.json` (MessagePack `GameMode` field).

| Character | Created via | `GameMode` | Meaning |
|---|---|---|---|
| `Androidia` | normal UI (Default = NORMAL) | `1` | Normal |
| `Seasonia`  | forced `_SelectedGameMode = Season` | `2` | Season |
| `S25Phone`  | normal UI on a real S25 | `1` | Normal |
| `SeasonPhone` | normal UI on a real S25, Season card (patch B) | `2` | Season |

`GameMode` enum (`server/Nordicandia.Contracts/GeneratedContracts.cs`):
`Unknown=0, Normal=1, Season=2, Challenge=3, NormalHardcore=4, SeasonHardcore=5,
ChallengeHardcore=6`.

### Why the Season card is hidden in the Android UI
`WindowSelectGameMode` renders `GameModeNormalFrame` (0x28) / `GameModeSeasonFrame`
(0x30) / `GameModeChallengeFrame` (0x38); each frame carries its `GameMode`
(verified live: Normal=1, Season=2, Challenge=3). The retail client never lets the
player pick Season for two independent reasons:

1. **The frame is force-hidden.** `WindowSelectGameMode.Awake` (0x02633458) and
   `Start` (0x026337B4) both call `GameModeSeasonFrame.gameObject.SetActive(false)`.
   Nothing ever re-activates it.
2. **`Next` is disabled for Season.** `UpdateButtonStatus` (0x02633C9C) sets
   `ButtonNext.interactable = _SelectedGameMode.HasValue && !IsSeason && !IsChallenge`.

Season data is never fetched either: a full `bl`-target scan of the library finds
**zero** call sites for `NetClient.GetSeasonInfo` (0x02DA3494),
`GameModeServiceApiClient.GetSeasonInfo` (0x027A7774) and
`OnlineData.set_SeasonInfo` (0x025C9270); a live cold-start Frida trace confirms
none are ever invoked. (Earlier notes claimed `OnLoggedInOnline` awaited a stalled
`SendDeviceInfo`; in fact `_SendDeviceInfo.MoveNext` completes normally,
state -1 -> -2, and `OnLoggedInOnline` simply never calls `GetSeasonInfo`.)
Consequently `OnlineData.SeasonInfo` stays `null` and the footer keeps the
`<Time until Season End>` placeholder.

Season is nevertheless the *default* selection: the Season frame's serialized
`MainToggle.isOn` is `true`, so `Start` sets `_SelectedGameMode` to Season
(0x20001) — but the frame is hidden and `Next` is disabled, so the player is forced
to tap Normal. The Season toggle has **no** `onValueChanged` handler (only Normal,
Challenge and Hardcore toggles are wired in `Start`), so tapping the card cannot
select it.

### Native patch (option B): make Season selectable in the normal UI
Added to `server/patch_android_online.py`:

| VA | meaning | original | patched |
|---|---|---|---|
| `0x026334B4` | `Awake`: `SetActive(SeasonFrame, w1)` | `mov w1, wzr` (`2a1f03e1`) | `mov w1, #1` (`21008052`) |
| `0x0263386C` | `Start`: `SetActive(SeasonFrame, w1)` | `mov w1, wzr` (`2a1f03e1`) | `mov w1, #1` (`21008052`) |
| `0x02633D5C` | `UpdateButtonStatus`: `orr w9, w20, w0` | IsChallenge\|IsSeason | `mov w9, w20` (`e903142a`) = IsChallenge only |

Result: the Game Mode screen now shows the Season card, it is pre-selected, and
`Next` is enabled; Challenge stays disabled. Verified on a real Galaxy S25
(rooted, `com.IterativeStudios.Nordicandia`): `SeasonPhone` was created entirely
through the normal UI and persisted with `GameMode = 2`.

### Runtime proof (pre-patch) Season creation works
Attaching Frida to `WindowSelectGameMode.OnNextClicked` (VA `0x02634174`) and
overwriting the `Nullable<GameMode> _SelectedGameMode` field (4 bytes at object
offset `0x88`; layout `[hasValue:byte][pad][value:short]`) from `0x00010001`
(Normal) to `0x00020001` (Season) also drives the normal New-character flow.

**Conclusion:** the server and the patched Android transport fully support
Season characters; the blocker was purely the hidden/disabled client-side Season
UI, now fixed by the native patch above.

## 15. Online profile sync ("Cannot synchronize online account")

### Symptom
After logging in, selecting **any** character (Normal *or* Season) and pressing
**Play** shows:

> There was an error connecting to the online service (Cannot synchronize online account)
> [Retry] [Play Offline]

Pressing **Retry** re-runs the same flow and fails again. The server-side save is
intact (all five characters still present in `world.json`), and `GetCharacterList`
returns them, so this is **not** a data-loss problem.

### Root cause (Frida + disassembly)
`LoadGame.SynchronizeProfile` (`0x025B770C`, async body
`_SynchronizeProfile_d__31.MoveNext` `0x025C12CC`) unconditionally creates and
awaits `SynchronizeProfileStateMachine.Synchronize` (`0x026FA540`) *before* it
inspects `PlayerAccount.IsOnline` (`0x02C0AEA4`).

`SynchronizeProfileStateMachine` (`dump.cs` TypeDefIndex 243) is the
**device-save ↔ server-save reconciler**. Its Stateless state machine is built in
`SetupStateMachine` (`0x026FA640`):

- States: `Idle → Initialize → LoadDeviceAccountMatchingDeviceId → LoadDeviceCharacters → Finished` (+ `Error`)
- Events: `Start, OfflineAccount, Retry, OperationSuccessful, VersionTooOld,
  PlayOffline, KeepServerAccountSave, KeepDeviceAccountSave,
  PurchasedAtleastOneItem, PurchasedNoItems, OnlineGameAccountFilesDoesNotExist,
  GameAccountIsEqualToServer, GameAccountIsMoreRecentOnServer,
  GameAccountIsMoreRecentOnDevice`

The closure `__c__DisplayClass9_0` fields are `startEvent@0x18`,
`errorState@0x20`, `offlineAccountEvent@0x28`, `operationSuccessful@0x30`,
`retryEvent@0x38`.

Runtime event trace on pressing Retry:
```
b__2 fires: Start
Event.Fire: Start
b__4 fires: OfflineAccount      <-- but b__4 then throws
!! Exception("Cannot synchronize online account")
```

`__c__DisplayClass9_0._SetupStateMachine_b__4` (`0x026FB6C0`) is the entry action
that runs right after `Start`:

```
IsOnline = PlayerAccount.get_IsOnline()          ; w0
tbnz w0,#0, 0x26FB774                            ; online -> throw
0x26FB758: Event.Fire(closure->offlineAccountEvent)   ; offline -> continue
0x26FB774: throw new Exception("Cannot synchronize online account")
```

`get_IsOnline` is `(AccountType-1) < 3`, and the phone login yields
`AccountType = 2 (PlayFabUnregistered)`, i.e. **online = true**, so the offline
entry always throws. The only guard on the machine, `__c._SetupStateMachine_b__9_0`
(`0x026FB4F4`, returns `!IsOnline`), is **never invoked** in this path.

The reconciliation body (`_SetupStateMachine_b__5_d.MoveNext` `0x026FB8F4`) shows
what the offline path does: it loads the **local device account**
(`SaveManager.LoadLocalDeviceAccounts_MessagePack` `0x025D7658`,
`DataUtils.get_PersistentDataPath` `0x0248B81C`, `UnityEngine.SystemInfo.
get_deviceUniqueIdentifier` `0x04E50898`), tries `CloudProfileSync.TryRestoreAsync`
(`0x02E606D4`) and `SilentMigrationAttempt.TryImportAsync` (`0x02E54FEC`), then
`PlayerAccount.Deserialize` (`0x02C10814`) / `Initialize` (`0x02C0E2D4`) /
`Player.ChangeToLocalDeviceAccount` (`0x02C077EC`).

### Why it fails on this private server
On the retail service the reconciler is only supposed to run for an
**offline / local-device** account, or once the "online game account files" exist.
It is a safety assertion (`if (IsOnline) throw`). Against this private server the
account is online, the device-account files do not exist locally, and the cloud /
migration sources are unavailable, so the assertion fires.

Experiments (Frida, real S25):
- NOP the assert at `0x026FB754` → no exception, but the machine takes the
  **offline** branch: device account is empty ⇒ main menu with an empty profile.
- Force the success event (`operationSuccessful`) from `b__4` → self-transition
  loop (Frida re-entrancy abort), then a permanent `Connecting…` hang.

**Conclusion:** the fix is either (A) server-side: provide the online
game-account-files / profile data so the reconciler treats device == server, or
(B) client-side: make `LoadGame.SynchronizeProfile` skip the reconciler when
`PlayerAccount.IsOnline` is true and keep the online profile. A blind `b__4`
patch is **not** a fix (it loses the online characters).

## 16. Account registration / linking (Android client)

Server side is already complete:

| Endpoint | Implementation |
|---|---|
| `ILoginServiceApi.LoginWithEmailAsync` | `LoginService.cs` — PBKDF2 verify / `CreateAccount` register |
| `ILoginServiceApi.LoginWithUsernameAsync` | same, username identity |
| `IUserLinkedAccountServiceApi.RegisterGameAccount` | links the current owner to `email:<addr>` + password |
| `LinkSteam/Google/GameCenter` | verify token (if keys configured) or legacy trust |
| `ChangeLinkedAccountPassword` / `Unlink*` | implemented |

Client side (`dump.cs`):

- `WindowLogin` (TypeDefIndex 862) — `OnLoginClicked`, `OnAdminLoginClicked`,
  `OnSignInWith{Google,Steam,GameCenter}Clicked`, `OnForgotPasswordClicked`.
- `WindowRegisterAccount` (863) — `Username/Email/Password/RepeatPassword`,
  `OnRegisterClicked` → `LoginStateMachineNew.RegisterGameAccount`.
- `WindowSelectGameMode` (1383) has `ButtonSignIn` (`+0x58`) and
  `OnSignInClicked` (`0x026340E0`).

**Finding:** `WindowSelectGameMode.RefreshSignInButton` (`0x02633BD0`) *destroys*
`ButtonSignIn`:

```
ldr x20,[x19,#0x58]!          ; x20 = ButtonSignIn
bl  UnityEngine.Object::op_Inequality
tbz w0,#0, ret                ; if null -> return
Component::get_gameObject
UnityGame::DestroyObject      ; otherwise destroy it
str xzr,[x19]                 ; ButtonSignIn = null
```

Patching `0x02633BD0` to `ret` (`c0 03 5f d6`) makes the **"Have an account?
Sign in"** button appear on the Game Mode screen (verified on the S25). Tapping it
does fire `OnSignInClicked`, **but neither `WindowLogin` nor
`WindowRegisterAccount` is instantiated** (Frida hooks on their
`Awake`/`Start` never hit) — the async body awaits a task that never completes in
this build. More work needed to open the register window.

**Caveat:** registering a real account does not by itself bypass the Section 15
assertion — that fires for *any* online account. Whether a `GameAccount` login
takes a different code path (skipping `LoadDeviceAccountMatchingDeviceId`) still
needs to be tested.

## 17. The Play button is the *offline* profile flow

Tracing the Play button (real S25, Frida) gives a definitive chain:

```
WindowCharacterList.OnPlayClicked  (_OnPlayClicked_d__52.MoveNext = 0x0257CA0C)
  -> NetAuth.Logout(1,1,null)                    (0x025BCFB0)
  -> UnityGame.SignInNew(LocalDevice=0, true, true, null, null)   (0x02701390)
  -> UnityGame.LoadOfflineProfile()              (0x026F6234)
       -> _LoadOfflineProfile_d__58.MoveNext (0x027027F4)
            -> SynchronizeProfileStateMachine.Synchronize (0x026FA540)  <- unconditional
                 -> b__4 (0x026FB6C0): if IsOnline -> throw "Cannot synchronize online account"
```

`WindowCharacterList` has both `FetchOnlineCharacters` (`0x02573508`) and
`FetchOfflineCharacters` (`0x02573AC8`); the list shown is the online one, but
**Play always re-logs in as `LocalDevice` and loads the offline profile.**

Experiments:
- Force `PlayerAccount.get_IsOnline` (`0x02C0AEA4`) to return false:
  the "Cannot synchronize online account" dialog disappears, but the offline
  reconcile cannot build the local device profile and the client shows
  **"Error (4)"** (from `_OnPlayClicked_d__52` -> `ShowErrorDialogOK`
  `0x0278D5F8`; the failing work is
  `CloudProfileSync.TryRestoreAsync` `0x02E606D4` /
  `SilentMigrationAttempt.TryImportAsync` `0x02E54FEC`, both stubbed server-side).
- Switching `SignInNew` login type 0 -> 1 at `0x0257E454` did **not** change the
  path (still the same dialog).

**Implication:** the device-account flow in this client is the *local/offline*
profile flow. To play online with a server-side character the client must either
(a) be patched to fetch/enter through the online profile path, or (b) the server
must supply the device profile / migration package so the offline reconcile
succeeds (option C).

## 18. Server fix deployed; Play flow still offline-centric

Deployed `GameStore.GetAccountData` populating `CharactersByGameMode`
(image tag `nordicandia-server:99ad80ed...`, Lightsail healthy). The client's
"Cannot synchronize online account" is **unchanged**, so the empty map was a real
bug but not the cause.

Fresh-play trace (no dialog, Frida):

```
WindowCharacterList.OnPlayClicked
NetClient.EnterGameWithCharacter        (0x02DA4FE4, called at 0x0257D654)  -> 200
UnityGame.LoadOfflineProfile            (0x026F6234)
SPSM.Synchronize                        (0x026FA540) -> b__4 -> throw
```

`OnlineProfilePuller.PullAsync`, `SilentMigrationAttempt.TryImportAsync`,
`CloudProfileSync.TryRestoreAsync` and `SaveManager.LoadLocalDevice*` are **not
called at all** on this path.

Bypass experiments (all on the real S25, SeasonPhone selected):
- NOP the assert `0x026FB754` -> no error, lands on the title menu (empty offline profile).
- Force `PlayerAccount.get_IsOnline` -> false (`0x02C0AEA4`) -> no error, goes to
  **Select Race** (new-character flow, offline profile empty).
- Replace `UnityGame.LoadOfflineProfile` with `mov x0,#0; mov x1,#0; ret`
  (returns a default/completed UniTask) -> still the title menu.

So the client's device-account "Play" is an offline/local-profile flow; skipping
the assertion just falls back to an empty local profile. Playing the server-side
characters requires either the client to take the true online entry path, or the
account to be reclassified so the client does not run this offline reconcile.

## 19. Registration/link UI is disabled in this Android build

Server side is complete (`RegisterGameAccount`, `LoginWithEmail/Username`,
`Link*`). Client side findings (real S25, Frida):

- Only three `UIWindow`s exist at startup: `Root (1)`, `MainMenu (2)`,
  `Dialog (9)`. `WindowLogin` / `WindowRegisterAccount` are not in the scene and
  are never instantiated on the sign-in path.
- `WindowSelectGameMode.RefreshSignInButton` (`0x02633BD0`) calls
  `DestroyObject(ButtonSignIn)`; patching it to `ret` makes the
  "Have an account? Sign in" button appear.
- Tapping it fires `WindowSelectGameMode.OnSignInClicked` (`0x026340E0`) but
  **nothing else happens**: no `UnityGame.SignInNew`, no `UIWindow.GetWindow` /
  `FocusWindow` / `Show`, no `UIWindowManager.ShowWindow`, no
  `Object.Instantiate`, no `Addressables` load. The async body
  (`_OnSignInClicked_d__26.MoveNext` `0x02634D3C`) reads a 16-byte struct from
  `[singleton+0xB8]→+0x10` and awaits a virtual call that produces no UI.
- `[singleton]` = `Game`; the `[+0xB8]→+0x10` 16-byte value is not a window.

Runtime attempt to drive registration through the client's own API: capture the
`LoginStateMachineNew` instance in its ctor (`0x026F2150`) and call
`RegisterGameAccount(email, pw, pw)` (`0x026F22F0`) from the Unity thread — the
call returns without throwing but **no `RegisterGameAccount` RPC reaches the
server**, so the machine ignored it in its current state.

Conclusion: this build ships the registration/login windows as dead code; the
in-client entry point is gone. Making registration usable requires either
reconstructing the window at runtime or calling the client's authenticated
`IUserLinkedAccountServiceApi` client directly.

## 20. A2 result (link email) + offline-reconcile trace

**A2 (production validation):** added a `Game`/email credential
(`s25prod@nord.local`, PBKDF2-210000) and linked it to the phone's user
`e3830e15-…` in `world.json`. Restart + Play still produced
"Cannot synchronize online account". **Registering/linking does not change the
Play flow** — `OnPlayClicked` is hardcoded to `NetAuth.Logout` →
`UnityGame.SignInNew(LocalDevice)` → `UnityGame.LoadOfflineProfile`.

**Offline reconcile trace (assert `0x026FB754` NOPed so SPSM proceeds):**

```
SPSM b__4          (offline entry)
SPSM b__5_d        LoadDeviceAccountMatchingDeviceId
  SaveManager.LoadLocalDeviceAccounts_MessagePack
  CloudProfileSync.TryRestoreAsync
SPSM b__5_d        (again)
  SilentMigrationAttempt.TryImportAsync
SPSM b__6_d        LoadDeviceCharacters
  SaveManager.LoadLocalDeviceCharacters_MessagePack
```

`OnlineProfilePuller.PullAsync`, `ClientSaveStore.ImportOnlineProfile` and
`SilentMigrationAttempt.WriteOnlineProfile` are **never reached**, so the local
device profile stays empty (=> new-character flow).

=> Making the offline path produce the server characters requires the server to
satisfy `CloudProfileSync` / `SilentMigrationAttempt` (the "online game account
files" / cloud snapshot), i.e. the previously-declined option C; OR the client
must be patched to skip the offline reconcile and take the online entry.

## 21. Original (Google-signed) APK works with the private server; PGS auth confirmed

Ran the **unmodified, Google-signed** APK (`dist/android-arm64-src/`, signer SHA-1
`f162ee2b…`) against the private server **without re-signing**:
- `/system/etc/hosts`: `prod.nordicandia.net` / `staging.nordicandia.net` →
  server IP (overlayfs remount makes `/system` writable).
- Frida applies the same 13 runtime patches to the genuine `libil2cpp.so`
  (`Memory.protect` + `writeByteArray`), so no APK modification is needed.

Result: login chain all 200 (`GetNonce → LoginWithStandaloneDeviceIdAsync →
GetUserAccountData → SkipMigrationAsync → GetCharacterList`), stable process,
character list shown.

Google Play Games on the original signature:
- `PlayGamesPlatform.Authenticate` → **PGS sign-in UI launched**, account
  `smiblecs@gmail.com` recognized. First attempt failed (`SignInOnResult
  success=0`) because the account had no Play Games profile.
- After creating the profile (gamer tag `StrenuousArch149`),
  `PlayGamesPlatform.IsAuthenticated == 1`.

=> The Google/PGS auth problem is **purely caused by re-signing**. The original
signature authenticates fine; the re-signed private build cannot.

Game-side Google login entry: `NetClient.SignInWithGooglePlay(createAccountIfPossible,
manualLogin)` (`0x02DA2398`) installs `GooglePlayAuthenticationFilter`
(`0x02E4ACDC`), whose `GetGooglePlayAuthCodeAsync` (`0x02E4AF40`) does
`PlayGamesPlatform.Authenticate` → `RequestServerSideAccess` and sends the code
to `LoginWithGooglePlayAsync` (server already implements it). The game never
calls it (no UI), and Frida-injected calls into these NetClient async methods do
not observably execute, so finishing this still needs a native patch.

## 22. Native code injection: Google sign-in stub (validated)

`BestHTTP.Examples.TestHubSample.Hub_OnConnected` (`0x344EBFC`, 5.9 KB, unused
sample) provides room to inject code. A stub was written at `0x344EC24`:

```
stp x29,x30,[sp,#-16]!
bl  NetClient.get_Current        ; x0 = NetClient.Current
mov w1,#1                        ; createAccountIfPossible
mov w2,#1                        ; manualLogin
bl  NetClient.SignInWithGooglePlay
ldp x29,x30,[sp],#16
ret
```

and `WindowSelectGameMode.OnSignInClicked` (`0x026340E0`) is patched to
`b 0x344EC24`, so the (re-enabled) Sign In button runs the stub. Applied
together with the standard 13 patches via Frida; the PGS profile now exists, so
`PlayGamesPlatform.IsAuthenticated == 1`.

Runtime trace proves the injected stub runs from the game's own code (Frida
hooks fire):
`GOOGLE STUB → NetClient.SignInWithGooglePlay → GooglePlayAuthenticationFilter
ctor → NetClient.GetUserAccountData`.

Remaining gap: `GooglePlayAuthenticationFilter._SendAsync` only calls
`AuthenticateAsync` when the net session is expiring/invalid
(`NetSession.IsRefreshExpiring` / `IsExpiring`, `_SendAsync_d__2.MoveNext`
`0x02E4D2DC`); while the device session is valid it skips Google auth. So the
stub must invalidate the session first (or the filter must run with no session)
for `AuthenticateAsync` → `GetGooglePlayAuthCodeAsync` → `RequestServerSideAccess`
→ `LoginWithGooglePlayAsync` to execute.

## 23. Google sign-in works end-to-end (native patch)

Final patch set (runtime, original Google-signed APK):

1. Standard 13 patches (device login, TLS, HTTP/1.1 gRPC, season UI, provider).
2. `RefreshSignInButton` (`0x02633BD0`) -> `ret` (keeps the Sign In button).
3. Injected Google stub at `0x344EC24` (unused BestHTTP sample):
   `stp; bl NetClient.get_Current; mov w1,#1; mov w2,#1; bl
   NetClient.SignInWithGooglePlay; ldp; ret`.
4. `WindowSelectGameMode.OnSignInClicked` (`0x026340E0`) -> `b 0x344EC24`.
5. `GooglePlayAuthenticationFilter._SendAsync` only authenticates when the
   session is expiring; NOP the two checks so it always authenticates:
   - `0x2E4D660` `tbz w0,#0,...` -> `nop`
   - `0x2E4D6FC` `tbz w0,#0,...` -> `nop`

Result (real S25, PGS profile created, `IsAuthenticated=1`):
tapping **Sign In** on the Game Mode screen runs PGS
(`RequestServerSideAccess`) and the server receives and answers:

```
ACCESS POST /ILoginServiceApi/LoginWithGooglePlayAsync HTTP/1.1 code=200
```

and created the Google-linked account:

```
Users:          google:422c99c23ada82f8ff05bc6dfd233e3e -> aa47e0ab-…
LinkedAccounts: aa47e0ab-… { Platform: 4 (GooglePlay),
                             PlatformUserId: "google:422c99c2…" }
```

Caveat: because `NORD_GOOGLE_CLIENT_ID/SECRET` are not configured, the server
uses the unverified path and keys the account by device id
(`req.DeviceId ?? req.ServerAuthCode`). Configuring real Google credentials
would let it verify the Play Games server auth code instead.

So the complete chain works on the **original signature**: original APK +
`/system/etc/hosts` redirect + runtime native patches => Google/PGS login that
lands in the private server.

## 24. Entering the world (online account) — the missing link found

### Symptom chain
"Play" for an online account (Normal or Season) ran:

```
WindowCharacterList.OnPlayClicked
  -> NetClient.EnterGameWithCharacter            (200)
  -> UnityGame.SignInNew(LocalDevice)
  -> UnityGame.LoadOfflineProfile
       -> SynchronizeProfileStateMachine (SPSM)
          -> b__4: if (PlayerAccount.IsOnline) throw "Cannot synchronize online account"
```

Tracing after NOPing that guard (`0x026FB754`) showed the offline reconcile
completes (`b__5_d SUCCESS`, `0x26FC034`) but the game just returns to the title
menu and never opens the realtime WebSocket.

### The world-entry code exists but was never scheduled
`Player.EnterGame` is only reached from one place:

```
WindowCharacterList.__c__DisplayClass52_0._OnPlayClicked_b__0      (0x025779F8)
   _OnPlayClicked_b__0_d.MoveNext                                 (0x02577A98)
       -> Player.EnterGame(PlayerGameModeAccount, Character)      (0x02C0AF10)
```

`_OnPlayClicked_b__0` is the "show loading UI -> build GameWorld/Character ->
`Player.EnterGame` -> show in-game window" coroutine. In our path it was **never
invoked** (verified with Frida hooks on both the wrapper and MoveNext).

### Why it is never invoked
`_OnPlayClicked_d__52.MoveNext` classifies the account and branches:

```
0x0257D298  ldrb w8,[x20,#0x20]        ; isOnline
0x0257D29C  cbnz w8, 0x0257DAE8        ; online
0x0257D2A0  b 0x0257E610               ; offline
```

Only the **offline** block (`0x0257E610`) initialises the loading UI on the
closure (`str x1,[x20,#0x30]!` = `loadingWindow`, `str x1,[x20,#0x18]!` =
`loadingComponent`) and drives `_OnPlayClicked_b__0`. The **online** branch never
does, so the world-entry coroutine is never scheduled. Forcing `b__0` manually
(without the loading UI) produced `Error (1)` because
`displayClass+0x18` and `displayClass+0x30` were still null.

### Fix
Two native patches, added to `server/patch_android_online.py`:

| VA | meaning | original (file bytes) | patched |
|---|---|---|---|
| `0x026FB754` | `SPSM b__4`: `tbnz w0,#0,<throw>` (online guard) | `00010037` | `1f2003d5` (nop) |
| `0x0257D29C` | `OnPlayClicked`: `cbnz w8,0x257dae8` (online branch) | `68420035` | `1f2003d5` (nop) |

The second patch forces the account-classification branch to the offline block,
which sets up the loading UI and schedules `_OnPlayClicked_b__0`, which finally
calls `Player.EnterGame`.

Together with an existing on-device local profile
(`files/Profiles/Local_<deviceId>/{manifest.bin,slot-a.snapshot}`, produced by
`device/frida_online_profile_inject.js`), the flow now enters the world.

### Verified
- Real Galaxy S25, **lib patch only, no Frida**: original Google-signed APK +
  replaced `libil2cpp.so` (sha256 `f5645194c0d10e953d5be5ef94039778808e8e22e8c5a5db98e8e810b6a48e50`)
  + `/system/etc/hosts` redirect + local profile.
- Character `Pumpkin` (GameMode = Season) presses **Play** and enters the world:
  loading -> "Skip the Tutorial?" dialog -> live map with health bar, skill bar,
  minimap, chat, monsters; character can be moved.

### Remaining notes
- The local profile is required. It is created once by the injected online
  profile puller (`OnlineProfilePuller.PullAsync` -> `WriteOnlineProfile` ->
  `ClientSaveStore.ImportOnlineProfile`). Building it in-client without Frida
  would need the import call baked in as a native stub.
- Google/PGS login still requires the original signature; a re-signed private
  package needs the email/password native login stub instead (see Sections 16/19).
