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
