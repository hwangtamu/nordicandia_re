# Nordicandia private server

A **protocol-compatible** MagicOnion backend for `com.IterativeStudios.Nordicandia 1.9.3`,
reconstructed entirely from the APK (no original server source).

## Status

| Component | State |
|---|---|
| Contract library (515 types + 24 service interfaces) | ✅ generated from the client's IL2CPP metadata |
| MagicOnion gRPC host (all 24 services) | ✅ running |
| `ILoginServiceApi` | ✅ real (device/steam/google/gamecenter/email/username/refresh) with accounts + sessions + linked accounts |
| `ICharacterServiceApi` (list/create/enter/delete/allocate) | ✅ real, persisted (attribute deltas accumulated) |
| `ICharacterPowerServiceApi` (active/passive skills, training) | ✅ real, persisted |
| `IGameModeServiceApi` (`GetSeasonInfo`, season characters) | ✅ real (`Season`/`SeasonHardcore` create + +100% exp buff) |
| `IInventoryServiceApi.ItemOperation` | ✅ real, persisted |
| `IVirtualCurrencyServiceApi` | ✅ real, persisted |
| `ILeaderboardServiceApi` | ✅ real, derived from character standings |
| `ISocialServiceApi.InspectCharacter` | ✅ real (equipped gear + stats + world progress) |
| `IUserLinkedAccountServiceApi` | ✅ real (link/unlink/change password) |
| `IOfferingServiceApi.GetCurrentBlessings` | ✅ real (returns no blessing) |
| `ICharacterGameEventServiceApi` (dungeon/niflheim/helheim/odrs/vanaheim/death) | ✅ real, world progression persisted |
| Other services (crafting, guilds, store, pets, …) | valid auto-generated stubs returning default/empty DTOs |
| Realtime WebSocket (`Envelope`/`Message`) | ✅ `/ws` gateway (HTTP/1.1 upgrade or HTTP/2 CONNECT) |
| Persistence | file-backed `GameStore` (`data/world.json`) — accounts, sessions, characters, items, silver, opals, experience, level, attributes, skills, world progression, season bonus |

> **Note on stubs:** `Defaults.Create<T>()` recursively instantiates nested DTO members.
> If a DTO nests a `SerializedBuff`, its `DefinitionIntegerId` defaults to `0` =
> `MightBuff`, and the client will apply it. Hand-write those responses and return `null`
> (see `Services/OfferingService.cs` / `Services/UseItemService.cs`).

Verified end-to-end: login returns a user + tokens, `GetCharacterList` and
`GetRelevantLeaderboards` answer over gRPC/HTTP2.

```
LOGIN OK  UserId=cceec03f... DisplayName=device:test-device-1
CHARACTERS: 0
LEADERBOARDS OK
```

## Why this is compatible

Both ends run **MagicOnion 5.1.8** + **MessagePack 2.x**. The client's shim DLLs
(`DynamicClient.ServiceClientDefinition`, `RawMethodInvoker`, `UnaryResult`) pin the
MagicOnion major version. The wire details recovered from the client:

* gRPC path = **short interface name + method name**, e.g.
  `/ILoginServiceApi/LoginWithStandaloneDeviceIdAsync`,
  `/ICharacterServiceApi/GetCharacterList`.
* DTOs are `[MessagePackObject(true)]` (property-name keys) **or**
  `[MessagePackObject(false)]` with explicit `[Key]` values — either integer
  (`Envelope`: `[Key(0)]`, `[Key(1)]`) or string (`SerializedItem`: `[Key("Id")]`).
* Realtime union tags: `Message` has `[Union(0..22, ...)]`.

## Layout

```
server/
├── Nordicandia.Contracts/        net8.0 class library
│   └── GeneratedContracts.cs     515 DTOs + 24 MagicOnion interfaces
├── Nordicandia.Server/           net10.0 ASP.NET Core + MagicOnion host
│   ├── Program.cs                Kestrel (h2c) on :50051
│   ├── Services/LoginService.cs  hand-written (real logic)
│   ├── Services/Services.Generated.cs  23 auto-generated services
│   └── State/GameStore.cs, Defaults.cs
├── Nordicandia.TestClient/       net10.0 MagicOnion client used to verify
├── ContractGen/                  Cecil-based code generator (regenerates contracts)
└── run.sh
```

## Build & run

```bash
export DOTNET_ROOT=/tmp/retools/dotnet10
export PATH=$DOTNET_ROOT:$PATH

# regenerate contracts from the APK metadata (optional)
cd ContractGen && dotnet run -c Release && cd ..

# build
dotnet build Nordicandia.Server -c Release
dotnet build Nordicandia.TestClient -c Release

# run server (h2c on 50051) + smoke test
cd Nordicandia.Server && dotnet bin/Release/net10.0/Nordicandia.Server.dll &
cd ../Nordicandia.TestClient && dotnet bin/Release/net10.0/Nordicandia.TestClient.dll http://localhost:50051
```

## Implementing a service for real

1. Remove its name from the `handled` set in `ContractGen/Program.cs`
   (or just write the class and delete the generated one).
2. Implement the interface deriving `ServiceBase<IXxxServiceApi>`.
3. Use `GameStore` for accounts/sessions/characters; add repositories as needed.
4. Return `UnaryResult.FromResult(dto)`.

`Defaults.Create<T>()` builds a DTO with empty collections, useful while filling gaps.

## Pointing the real APK at this server

The client connects to `prod.nordicandia.net:443` with TLS and OS trust roots.

1. **Redirect DNS** for `prod.nordicandia.net` to your host (hosts file / local DNS /
   `adb root` + `/system/etc/hosts`).
2. **Serve TLS** with a cert for `prod.nordicandia.net` signed by a CA installed in
   the device's trust store (e.g. mkcert CA + `adb push` to `/system/etc/security/cacerts`),
   or patch the client's cert validation.
3. The client's integrity filters (Play Integrity / App Attest) can simply be ignored —
   this server accepts any login.
4. Alternatively, patch the `prod.nordicandia.net` string literal inside
   `global-metadata.dat` and re-sign the APK.

## Option B — patched + re-signed XAPK (done)

`patch_xapk.py` patches the host string literals in `global-metadata.dat`, re-signs every
split with one key, and repackages an XAPK. Output in **`/local_disk/nordicandia_re/patched/`**:

| File | Notes |
|---|---|
| `Nordicandia_1.9.3_private.xapk` | ready-to-install, 5 entries |
| `com.IterativeStudios.Nordicandia.apk` | patched + signed base |
| `config.armeabi_v7a.apk` | re-signed |
| `UnityDataAssetPack.apk` | re-signed |
| `nordicandia.keystore` | debug signer (`nordpass`, alias `nord`) |

All three splits share one signer (SHA-256 `5f002765a4ea7c9f6b9259b647de3a4d8fc46ada77127aa7d1e5bd5b5303b68b`).

Host literals patched (same length, so no metadata offset surgery):

| Original | Patched | Resolves to |
|---|---|---|
| `prod.nordicandia.net` (20) | `ab.10-0-2-2.sslip.io` | `10.0.2.2` (emulator host alias) |
| `staging.nordicandia.net` (23) | `abcde.10-0-2-2.sslip.io` | `10.0.2.2` |

### Server side (TLS on 443)

```bash
bash gen_cert.sh ab.10-0-2-2.sslip.io abcde.10-0-2-2.sslip.io   # certs/nordicandia-server.pfx
sudo -E NORD_CERT_PFX=$PWD/certs/nordicandia-server.pfx NORD_CERT_PWD=nordpass \
     dotnet Nordicandia.Server/bin/Release/net10.0/Nordicandia.Server.dll
```
Verified: gRPC over **HTTP/2 + TLS** works (self-signed accepted because the client's
`NetTlsClient.NotifyServerCertificate` is a no-op).

### Install

```bash
adb install-multiple com.IterativeStudios.Nordicandia.apk config.armeabi_v7a.apk UnityDataAssetPack.apk
# or
adb install Nordicandia_1.9.3_private.xapk   # if the installer/unpacker supports XAPK
```

### ⚠ ABI caveat (why no on-device test was completed)

This APKPure XAPK ships the **`armeabi-v7a`** split only. The available test target
(Pixel 9 Pro, Android 16) reports `abilist32=` empty / `zygote64` — i.e. **64-bit only** — and
the x86_64 AVD likewise cannot run it. So the patched v7a build installs on a 32-bit-capable
device/emulator only.

To build the arm64 variant once you have the arm64 XAPK (only the config split differs;
base + asset pack are ABI-independent):

```bash
python3 patch_xapk.py --indir /path/to/arm64_xapk \
    --config-apk config.arm64_v8a.apk \
    --prod-host ab.10-0-2-2.sslip.io --staging-host abcde.10-0-2-2.sslip.io
```

For a physical device on the same LAN, pick a 20/23-char name that resolves to the host
(e.g. via your router's DNS or a wildcard DNS service) and re-run with `--prod-host/--staging-host`.
For a USB device, patch to `a.127-0-0-1.sslip.io` / `abcd.127-0-0-1.sslip.io` and use
`adb reverse tcp:443 tcp:443`.

## Pointing the desktop (Steam) client at this server

The Steam build (Unity 6000.2 / IL2CPP, metadata v31) speaks the same MagicOnion
protocol but with two desktop-specific quirks recovered on this machine:

1. **TLS.** Its gRPC/HTTP stack (BestHTTP `TLSSecurity` + `SecureTlsClient`) validates
the server certificate against a **bundled X509 root database**, not the OS store, so a
private-CA/localhost cert is rejected mid-handshake (client sends a `user_canceled`
alert). `prepare-desktop.mjs` now overwrites the first byte of
`SecureTlsClient::NotifyServerCertificate` with `ret` (0xC3), turning validation into a
no-op. (The gRPC target is `GrpcChannelTarget(host, 443, isInsecure:false)`, so the
client always speaks TLS on :443.)
2. **Serialization.** Requests are MessagePack **LZ4BlockArray**-compressed, i.e. the
body is `[ext8(98, uint(uncompressedLen)), bin32(compressed)]`. The server must use the
same options (see `Nordicandia.Server/Program.cs`):

```csharp
builder.Services.AddMagicOnion(options =>
    options.MessageSerializer = MessagePackMagicOnionSerializerProvider.Default
        .WithOptions(MessagePackSerializer.DefaultOptions
            .WithCompression(MessagePackCompression.Lz4BlockArray)));
```

### Run

```bash
# 1. build a patched copy (hosts -> 127.0.0.1 + TLS-validation bypass)
node server/prepare-desktop.mjs \
  "C:/Program Files (x86)/Steam/steamapps/common/Nordicandia" dist/desktop

# 2. server with TLS on :443 (needs a PFX; the patched client no longer checks it)
cd server
export DOTNET_ROOT="$PWD/../.tools/dotnet"
NORD_CERT_PFX="$PWD/certs/nordicandia-server.pfx" NORD_CERT_PWD=nordpass \
  "$DOTNET_ROOT/dotnet" Nordicandia.Server/bin/Release/net10.0/Nordicandia.Server.dll

# 3. launch the client (Steam must be running)
cd ../dist/desktop
SteamAppId=1503790 SteamGameId=1503790 ./Nordicandia.exe
```

The smoke test (`Nordicandia.TestClient`) was updated to use the same LZ4 options, so
`run.sh` still passes. Verified against the real client: `LoginWithSteamAsync`,
`GetUserAccountData`, `SendDeviceInfo`, `GetSeasonInfo` and `GetCatalog` all return
200 over TLS/HTTP2. The client then raises a `NullReferenceException` dialog because
those services are still auto-generated stubs returning empty DTOs — implementing
`ICatalogServiceApi` / `IGameModeServiceApi` / `ILoginServiceApi.GetUserAccountData`
is the next step.

## Next steps

* Persist `GameStore` to PostgreSQL/SQLite.
* Implement `ICharacterServiceApi` (create/enter → `SerializedCharacterData`).
* Implement `IInventoryServiceApi`, `IVirtualCurrencyServiceApi` (wallet), `IGuildServiceApi`.
* Add the realtime WebSocket gateway (`Envelope` + `Message` union 0–22).
* Port semantics from the client's `Offline*` classes (they encode the same rules the
  live server enforces).
