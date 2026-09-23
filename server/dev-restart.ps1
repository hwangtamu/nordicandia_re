param([switch]$LaunchClient)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = [IO.Path]::GetFullPath((Join-Path $root '.tools/dotnet/dotnet.exe'))
$assembly = Join-Path $PSScriptRoot 'Nordicandia.Server/bin/Release/net10.0/Nordicandia.Server.dll'
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | Where-Object { $_.CommandLine -match 'Nordicandia.Server[/\\]bin[/\\]Release[/\\]net10.0[/\\]Nordicandia.Server.dll' -and $_.ExecutablePath -eq $dotnet } | ForEach-Object { Stop-Process -Id $_.ProcessId }
& $dotnet build (Join-Path $PSScriptRoot 'Nordicandia.Server') -c Release --no-restore --nologo -v q *> (Join-Path $root 'steam_analysis/server-build.log')
if ($LASTEXITCODE -ne 0) { throw 'Build failed; see steam_analysis/server-build.log' }
$env:NORD_CERT_PFX = Join-Path $PSScriptRoot 'certs/desktop.pfx'
$env:NORD_CERT_PWD = ''
$env:NORD_DATA_DIR = Join-Path $PSScriptRoot 'data'
$env:NORD_DUMP_BODY = '0'
$process = Start-Process -FilePath $dotnet -ArgumentList $assembly -WorkingDirectory $root -RedirectStandardOutput (Join-Path $root 'steam_analysis/server-current.log') -RedirectStandardError (Join-Path $root 'steam_analysis/server-error.log') -WindowStyle Hidden -PassThru
Write-Output "Server PID $($process.Id)"
if ($LaunchClient) {
    $exe = [IO.Path]::GetFullPath((Join-Path $root 'dist/desktop/Nordicandia.exe'))
    Get-CimInstance Win32_Process -Filter "Name='Nordicandia.exe'" | Where-Object ExecutablePath -eq $exe | ForEach-Object { Stop-Process -Id $_.ProcessId }
    $env:SteamAppId = '1503790'; $env:SteamGameId = '1503790'
    Start-Process -FilePath $exe -ArgumentList "-screen-fullscreen 0 -screen-width 1280 -screen-height 800 -logFile `"$root/steam_analysis/current-player.log`"" -WorkingDirectory (Split-Path $exe -Parent)
}

