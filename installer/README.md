# Nordicandia private-server client patcher

Builds a copy of your own Nordicandia (Steam) installation pointed at a private server.
It does **not** contain or redistribute any game content — you must own the game on Steam.

## What it changes

| File | Change |
|---|---|
| `Nordicandia_Data/il2cpp_data/Metadata/global-metadata.dat` | `prod.nordicandia.net` / `staging.nordicandia.net` → your server host |
| `GameAssembly.dll` | `SecureTlsClient::NotifyServerCertificate` → `ret` (the client validates TLS against a bundled root DB, so a self-signed server cert would otherwise be rejected) |
| `GameAssembly.dll` | *optional:* magic find ×10 (`ItemGenerator.CreateRandomItem`) |
| `GameAssembly.dll` | *optional:* portal drop ×3 (`contextPortalsWeightMult`) |
| `steam_appid.txt` | forces app id `1503790` |
| `Nordicandia_Data/boot.config` | removes `single-instance=` so it can run alongside the original |

Every binary edit is length-preserving and is verified against the expected original bytes
before it is written.

## Requirements

- Windows 10/11 with PowerShell 5.1 (built in).
- Nordicandia **1.9.3** installed via Steam. Other builds are refused unless `-Force` is
  passed (the patch offsets are version-specific).
- No game process may be running while patching.

## Usage

Double-click `Install-Nordicandia.cmd` (it prompts for the Steam folder), or from PowerShell:

```powershell
# patched copy with the default backend and all gameplay patches
.\NordicandiaPatcher.ps1 -Source "C:\Program Files (x86)\Steam\steamapps\common\Nordicandia"

# explicit server, no gameplay cheats
.\NordicandiaPatcher.ps1 -Source "C:\...\Nordicandia" -BackendHost play.example.com -NoGameplayPatches

# custom output location
.\NordicandiaPatcher.ps1 -Source "C:\...\Nordicandia" -Destination D:\Nordicandia-Private
```

The patched client is written to `%USERPROFILE%\Nordicandia-Private` by default. Launch it
with Steam running:

```
SteamAppId=1503790 SteamGameId=1503790 %USERPROFILE%\Nordicandia-Private\Nordicandia.exe
```

You can also create a desktop shortcut with that command line.

## Notes

- The patcher in this repository defaults to **`3.140.50.136`** (a specific private server).
  Change `DEFAULT_BACKEND` in `Install-Nordicandia.cmd`, or pass `-BackendHost`, to use another.
- The server enforces authentication. If your server is in strict mode, sign in with the
  in-game email/username login; unverified Steam/device auto-login may be rejected.
- After a game update, re-run the patcher against the updated install. If the build changed,
  it will refuse to patch until the offsets are updated.