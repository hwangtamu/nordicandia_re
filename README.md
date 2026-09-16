# Nordicandia — private-server reverse engineering

A from-scratch, **protocol-compatible private server** for *Nordicandia* (the semi-idle
RPG, Steam app `1503790`, package `com.IterativeStudios.Nordicandia`), reconstructed by
reverse-engineering the shipped clients. No original server source was available.

The backend stack was rebuilt from IL2CPP metadata as a **MagicOnion 5.1.8 + MessagePack**
gRPC service and verified against the real game clients.

* Deep dive (targets, tooling, encryption, infrastructure): [`REPORT.md`](REPORT.md)
* Server internals / Android client: [`server/README.md`](server/README.md)

---

## Status

| Component | State |
|---|---|
| Contract library (515 DTOs + 24 service interfaces) | ✅ generated from IL2CPP metadata |
| MagicOnion gRPC host (all 24 services) | ✅ running |
| `ILoginServiceApi` | ✅ real (device/steam/google/gamecenter/email/username/refresh) |
| `ICharacterServiceApi` | ✅ list/create/enter/delete + attribute allocation (accumulated, persisted) |
| `ICharacterPowerServiceApi` | ✅ active/passive skill assignment + training persisted |
| `IGameModeServiceApi` | ✅ `GetSeasonInfo` (deterministic season wheel) + season character creation |
| `IInventoryServiceApi.ItemOperation` | ✅ item journal replayed into the character |
| `IVirtualCurrencyServiceApi` | ✅ silver/opal gain/spend |
| `ILeaderboardServiceApi` | ✅ level leaderboards (`character_level_overall_{mode}`, class, helheim) |
| `ISocialServiceApi.InspectCharacter` | ✅ equipped gear (slots 0–13) + stats + world progress |
| `IUserLinkedAccountServiceApi` | ✅ Steam/Google/GameCenter/email linking (`GetUserAccountData.LinkedAccounts`) |
| `IOfferingServiceApi.GetCurrentBlessings` | ✅ returns no blessing (see gotchas) |
| `ICharacterGameEventServiceApi` | ✅ dungeon/niflheim/helheim/odrs-trail/vanaheim/death progression persisted |
| Other services | valid auto-generated stubs returning default/empty DTOs |
| Realtime WebSocket (`Envelope`/`Message` union 0–22) | ✅ `/ws` gateway (HTTP/1.1 upgrade or HTTP/2 CONNECT) |
| Serialization | ✅ MessagePack + LZ4BlockArray, `[Union]` realtime contracts |
| Persistence | file-backed `GameStore` (`data/world.json`): accounts, sessions, characters,
|  | items, currency, experience, level, attributes, skills, world progression, season bonus |
| **Desktop (Steam) client** | ✅ connects — login + 4 follow-up RPCs verified (see below) |
| **Android client** | ✅ patched XAPK pipeline (see `server/README.md`) |

---

## Layout

```
nordicandia_re/
├── README.md                     <- this file
├── REPORT.md                     <- reverse-engineering report
├── decrypt_nordicandia.py        <- TripleDES game-data / save decryptor
├── gamedata/                     <- 37 raw (encrypted) definition TextAssets
├── gamedata_decrypted/           <- 37 decrypted definition JSON files
├── frida/                        <- Frida probe scripts
├── patched_arm64/                <- Android signing keystore
├── server/                       <- private server (see server/README.md)
│   ├── Nordicandia.Contracts/    <- generated DTOs + service interfaces
│   ├── Nordicandia.Server/       <- ASP.NET Core + MagicOnion host
│   ├── Nordicandia.TestClient/   <- smoke-test gRPC client
│   ├── ContractGen/              <- Cecil-based contract generator
│   ├── prepare-desktop.mjs       <- build a patched desktop client copy
│   ├── patch_xapk.py             <- patch + re-sign the Android XAPK
│   ├── gen_cert.sh               <- TLS cert generation
│   ├── nginx-desktop.conf        <- optional OpenSSL TLS front end on :443
│   ├── run.sh                    <- build + run + smoke-test
├── dist/                         <- working copies of clients (git-ignored)
│   └── desktop/                  <- patched Steam client
└── steam_analysis/               <- IL2CPP dump / logs / probes (git-ignored)
```

Only source is tracked in git — game binaries, dumps, certs and build outputs are
ignored (`.gitignore`).

---

## Quick start — server + smoke test

The .NET 10 SDK is vendored under `.tools/dotnet`.

```bash
cd server
export DOTNET_ROOT="$PWD/../.tools/dotnet"

# build
"$DOTNET_ROOT/dotnet" build Nordicandia.Server -c Release
"$DOTNET_ROOT/dotnet" build Nordicandia.TestClient -c Release

# run (plaintext HTTP/2 on :50051) + smoke test
"$DOTNET_ROOT/dotnet" Nordicandia.Server/bin/Release/net10.0/Nordicandia.Server.dll &
"$DOTNET_ROOT/dotnet" Nordicandia.TestClient/bin/Release/net10.0/Nordicandia.TestClient.dll http://localhost:50051
```

Expected output:

```
LOGIN OK
  UserId      : ...
  DisplayName : device:test-device-1
  AuthToken   : ...
CHARACTERS: 0
LEADERBOARDS OK
```

### Server configuration

| Variable | Default | Meaning |
|---|---|---|
| `NORD_H2C_PORT` | `50051` | plaintext HTTP/2 listener (test client) |
| `NORD_HTTPS_PORT` | `443` | TLS + HTTP/2 listener (real clients) |
| `NORD_CERT_PFX` | – | PKCS#12 cert; TLS listener starts only if set |
| `NORD_CERT_PWD` | – | PKCS#12 password |
| `NORD_DUMP_BODY` | – | set `1` to hex-dump login request bodies |

> The **client always uses TLS on :443**, so real clients need `NORD_CERT_PFX`.

---

## Connecting the desktop (Steam) client

The Steam build (Unity 6000.2 / IL2CPP, metadata v31) talks the same MagicOnion protocol
with two desktop-specific quirks:

1. **TLS target** — `GrpcChannelTarget(host, 443, isInsecure:false)`: TLS gRPC on :443.
2. **Certificate validation** — its BestHTTP/`TLSSecurity` stack validates against a
   **bundled X509 root database**, not the OS store, so a dev/self-signed cert is
   rejected mid-handshake (client sends a `user_canceled` alert).
3. **Serialization** — requests are MessagePack **LZ4BlockArray**-compressed.

All three are handled by `server/prepare-desktop.mjs` (host literals → `127.0.0.1`,
plus a one-byte `ret` patch of `SecureTlsClient::NotifyServerCertificate`) and by the
server's serializer configuration.

### Steps

```bash
# 1. Build a patched copy of the Steam client (needs the Steam install).
node server/prepare-desktop.mjs \
  "C:/Program Files (x86)/Steam/steamapps/common/Nordicandia" dist/desktop

# 2. Run the server with TLS on :443.
cd server
export DOTNET_ROOT="$PWD/../.tools/dotnet"
NORD_CERT_PFX="$PWD/certs/nordicandia-server.pfx" NORD_CERT_PWD=nordpass \
  "$DOTNET_ROOT/dotnet" Nordicandia.Server/bin/Release/net10.0/Nordicandia.Server.dll

# 3. Launch the client (Steam running).
cd ../dist/desktop
SteamAppId=1503790 SteamGameId=1503790 ./Nordicandia.exe
```

If no PFX exists yet:

```bash
bash server/gen_cert.sh ab.10-0-2-2.sslip.io abcde.10-0-2-2.sslip.io   # -> certs/nordicandia-server.pfx (nordpass)
```

### Verified against the real client

Over TLS/HTTP2 on `https://localhost`:

```
ILoginServiceApi/LoginWithSteamAsync
ILoginServiceApi/GetUserAccountData
IUserServiceApi/SendDeviceInfo
IGameModeServiceApi/GetSeasonInfo
ICatalogServiceApi/GetCatalog
```

The client then enters the game and opens the realtime socket at
`wss://localhost/ws?status=True&characterId=…` (`Authorization: Bearer <token>`). The full
flow — login → character select/enter → `/ws` → idle-progress sync — is verified, and
inventory/currency/experience/level/attribute/world progression changes are persisted (see
**Progression persistence** below).

The patched copy records what was changed in `dist/desktop/private-build.json`.

---

## Connecting the Android client

The APK pipeline patches the host literals in `global-metadata.dat`, re-signs all splits
with one key and repackages an XAPK. The client's `NetTlsClient` certificate callback is a
no-op, so a self-signed cert is accepted.

```bash
python3 server/patch_xapk.py --indir /path/to/xapk \
    --prod-host ab.10-0-2-2.sslip.io --staging-host abcde.10-0-2-2.sslip.io

adb install-multiple com.IterativeStudios.Nordicandia.apk config.armeabi_v7a.apk UnityDataAssetPack.apk
# or: adb install Nordicandia_1.9.3_private.xapk
```

For USB devices, patch to `a.127-0-0-1.sslip.io` and use `adb reverse tcp:443 tcp:443`.
See `server/README.md` for the ABI caveat (`armeabi-v7a`-only XAPK).

---

## Protocol notes

Facts recovered from the clients that any compatible server must match:

* **Framework:** MagicOnion **5.1.8** + MessagePack 2.x (both ends), gRPC over HTTP/2.
* **gRPC path:** short interface name + method, e.g.
  `/ILoginServiceApi/LoginWithSteamAsync` (no `SharedNet.Api.` namespace).
* **MessagePack:** DTOs use `[MessagePackObject(true)]` (property-name keys / maps) or
  `[MessagePackObject(false)]` + explicit `[Key]` (int or string).
* **Compression:** the Unity clients serialize with
  `MessagePackCompression.Lz4BlockArray` — wire form
  `[ext8(98, uint(uncompressedLen)), bin32(compressed)]`. Configure it on the server:

  ```csharp
  builder.Services.AddMagicOnion(options =>
      options.MessageSerializer = MessagePackMagicOnionSerializerProvider.Default
          .WithOptions(MessagePackSerializer.DefaultOptions
              .WithCompression(MessagePackCompression.Lz4BlockArray)));
  ```

* **TLS:** real clients connect to `host:443`, TLS required, ALPN `h2`.
  Kestrel/Schannel can be strict about the Unity ClientHello; `nginx-desktop.conf`
  (OpenSSL) is a drop-in front end if needed.
* **Realtime:** separate WebSocket (`Envelope` + `Message` union `0..22`) at `/ws`
  (HTTP/1.1 upgrade or HTTP/2 CONNECT), bearer-authenticated. The server implements
  metadata/idle sync, chat, party, presence and season-buff pushes.

---

## Game data & saves

All game definitions shipped as `Base64(3DES(json))` with a hard-coded key:

```
TripleDES / ECB / PKCS7,  key = MD5(UTF8("iloveburgers"))   (16 bytes)
```

* `gamedata/` — 37 raw definition assets
* `gamedata_decrypted/` — 37 decrypted JSON files (Items, Affixes, Monsters, Worlds,
  CharacterClasses, Quests, …)
* `decrypt_nordicandia.py` — decrypt/encrypt utility:

```bash
python3 decrypt_nordicandia.py gamedata/Items -o items.json
python3 decrypt_nordicandia.py --encrypt items.json -o gamedata/Items
```

Saves use the same cipher directly on bytes.

---

## Reverse-engineering artifacts

* `REPORT.md` — full write-up (package layout, toolchain, IL2CPP dump, encryption,
  monetization/analytics, hosting/infrastructure).
* `steam_analysis/` — desktop IL2CPP dump (`dump.cs`, `metadata.json`), shim DLLs,
  Frida probes and handshake/body captures.
* `frida/` — Frida instrumentation scripts.
* `server/ContractGen` — regenerates `GeneratedContracts.cs` from the client's metadata.

---

## Toolchain

| Tool | Version / location |
|---|---|
| .NET SDK | 10.0.401 (`./.tools/dotnet`) |
| Python (optional) | `.venv` (created with `uv`) |
| nginx (optional) | `./.tools/nginx` |
| Il2CppInspectorRedux | metadata v31/v39 support |
| apktool / jadx | Android package analysis |

---

## Next steps

* Replace the remaining auto-generated stubs with real logic (crafting, guilds, store,
  pets, season rewards).
* Port the remaining gameplay rules from the client's `Offline*` classes.
* Make the client actually render the season trophy `SeasonBuff` (it currently instantiates
  but never adds it) — cosmetic only, the +100% exp already applies via the character
  attribute.
* Persist `GameStore` to PostgreSQL/SQLite for multi-process hosting.
* Add realtime party/social pushes beyond the current presence/chat mirror.

## Progression persistence

The Unity client is the source of truth for its local state but **replaces** it with
`EnterGameWithCharacterResponse.Character` on every login, so the private server must
replay every incremental update it receives back into the stored character. Implemented:

| Client call | Persisted into |
|---|---|
| `ItemOperation` (add/delete/move/consume) | `SerializedData.Items` |
| `GainSilver` / `SpendSilver` / `GainVirtualCurrency` / `SpendVirtualCurrency` | per-character silver/opals |
| `UpdateCharacterMetadataMessage` (WebSocket, idle XP) | total experience → level |
| `AllocateCharacterAttributes` | `SerializedData.Attributes` ids `105–108`, `126–128` (accumulated) |
| `OnDungeonRunStarted/Completed`, `OnNiflheim*`, `OnHelheim*`, `OnOdrsTrail*`, `OnVanaheim*`, `OnCharacterDied` | `World_Tier_Unlocked` + `SerializedData.Waypoints` |
| `AssignActiveSkill` / `AssignPassiveSkill` / `AssignPassiveSkillTraining` | `SerializedData.Skills` + `Powers` (via `PowerCatalog.HashNameSafe`) |
| `LinkSteamAccount` / `RegisterGameAccount` (+ login auto-link) | linked accounts in `GetUserAccountData.LinkedAccounts` |
| `CreateCharacter` (`Season` / `SeasonHardcore`) | `GameMode`, `SeasonBuff` + `Experience_Bonus_Percent` (361) = +100% |
| `UpdateCharacterMetadataMessage` (WebSocket) | also refreshes `IdleProgress.LastActiveEpoch` |

The level curve is the client's own `Game.Calculator`:
`xpForLevel(n) = n <= 1 ? 0 : 350 + 20 * n^1.7` (see `State/Progression.cs`). Attribute
ids are recovered from the client's `GameAttributes` static constructor via
`steam_analysis` / the `dump` mode of the test client.

## Gotchas discovered while matching the real server

These cost the most debugging time; keep them in mind when implementing more services.

* **`Defaults.Create<T>()` on a DTO with nested `SerializedBuff` fields is dangerous.** It
  recursively instantiates reference members, and `SerializedBuff.DefinitionIntegerId`
  defaults to `0`, which is `MightBuff`. The client's `WindowInGame.UpdateBlessings`
  applies any non-null blessing, so `GetCurrentBlessings` silently gave *every* character
  of every class a permanent Might buff. Return explicit `null`s instead
  (`Services/OfferingService.cs`). `UseItemWithBuff` had the same trap.
* **Attribute allocation is a *delta*, not an absolute.** `TabPlayerBaseAttributes
  .AssignAttributes` seeds `_AllocatedValues[attr] = 0` and adds the click delta, and the
  confirm handler clears the dictionary. So a confirm carries only the pending clicks and
  the server must **accumulate** onto the stored allocation — overwriting wiped every
  previous allocation (`GameStore.ApplyAllocatedAttributes`). `AttributePoints` in the
  response is the *remaining* pool, and `TotalAllocated*` are the accumulated totals.
* **Offline progress = `CurrentEpoch - IdleProgress.LastActiveEpoch`.** The client never
  sends its locally-updated value back, so the server must stamp it on create, on enter
  (stored copy only, so the login still reports the real gap) and on every realtime
  metadata sync (`GameStore.SetLastActiveEpoch`).
* **Level ≠ area level.** Character level is a pure function of total experience; the area
  level lives in `World_Tier` / `SerializedData.Waypoints` and only arrives via
  game-event RPCs.
* **The client replaces its local character with the server's on every login**, so the
  server is authoritative and must replay every incremental update it receives.

## Legal

For personal research / interoperability only. Game assets and code remain the property
of their respective owners.