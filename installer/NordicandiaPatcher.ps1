#Requires -Version 5.1
<#
.SYNOPSIS
    Builds a Nordicandia desktop client pointed at a private server.

.DESCRIPTION
    Copies an existing Steam installation, redirects the server hostname, disables the
    bundled TLS certificate check (the client's BestHTTP stack ignores the OS trust store),
    and applies the optional gameplay patches:

      * magic find x10  (ItemGenerator.CreateRandomItem)
      * portal drop x3  (contextPortalsWeightMult)

    This patches the files you already own; it does not redistribute any game content.
    Offsets are for Nordicandia 1.9.3 (Metadata v31, GameAssembly image base 0x180000000)
    and are verified by SHA-256 before anything is written.

.PARAMETER Source
    The Steam install folder, e.g. "C:\Program Files (x86)\Steam\steamapps\common\Nordicandia".

.PARAMETER Destination
    Where to create the patched copy. Defaults to %USERPROFILE%\Nordicandia-Private.

.PARAMETER BackendHost
    Private server hostname or IP. The client connects over TLS on port 443.

.PARAMETER NoGameplayPatches
    Skip the magic-find and portal-drop edits.

.PARAMETER Force
    Patch even if the source build hashes do not match the supported 1.9.3 build.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File NordicandiaPatcher.ps1 `
        -Source "C:\Program Files (x86)\Steam\steamapps\common\Nordicandia" `
        -BackendHost 3.140.50.136
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Source,

    [string]$Destination = (Join-Path $env:USERPROFILE 'Nordicandia-Private'),

    [string]$BackendHost = '3.140.50.136',

    [switch]$NoGameplayPatches,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# Supported source build (Nordicandia 1.9.3 desktop).
$ExpectedMetadataSha256 = 'ab73eae2b2bd906287e4092d5e933cb19e8538ef92361b51bb29f32bd54d3bf1'
$ExpectedGameAssemblySha256 = 'a7333524c7bb69df3e6028b484af493416feeb8fe4306096b632313561b61235'

# Patch sites (verified before writing).
$TlsPatchVa = [uint64]0x18109ACB0        # SecureTlsClient::NotifyServerCertificate -> ret
$MagicFindPatchVa = [uint64]0x180D541B0  # ItemGenerator.CreateRandomItem
$MagicFindConstantVa = [uint64]0x1838D6D80
$PortalSites = @([uint64]0x180D53BC5, [uint64]0x180D53BD3)
$PortalConstantVa = [uint64]0x1838E1CD0
$Hosts = @('prod.nordicandia.net', 'staging.nordicandia.net')
$MetadataRelative = 'Nordicandia_Data\il2cpp_data\Metadata\global-metadata.dat'

$MagicFindExpected = [byte[]](0xF2, 0x0F, 0x10, 0xBD, 0xB0, 0x01, 0x00, 0x00)
$PortalExpected = [byte[]](0xF2, 0x0F, 0x10, 0x95, 0xC0, 0x01, 0x00, 0x00)

function Get-Sha256([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant()
}

function Read-Pe([byte[]]$Image) {
    $pe = [BitConverter]::ToUInt32($Image, 0x3C)
    if ([Text.Encoding]::ASCII.GetString($Image, $pe, 4) -ne "PE`0`0") { throw 'GameAssembly.dll is not a PE file' }
    $sectionCount = [BitConverter]::ToUInt16($Image, $pe + 6)
    $optionalSize = [BitConverter]::ToUInt16($Image, $pe + 20)
    $optional = $pe + 24
    if ([BitConverter]::ToUInt16($Image, $optional) -ne 0x20B) { throw 'Expected a 64-bit PE image' }
    $imageBase = [BitConverter]::ToUInt64($Image, $optional + 24)
    $sectionTable = $optional + $optionalSize
    $sections = @()
    for ($i = 0; $i -lt $sectionCount; $i++) {
        $s = $sectionTable + $i * 40
        $sections += [pscustomobject]@{
            VirtualSize    = [BitConverter]::ToUInt32($Image, $s + 8)
            VirtualAddress = [BitConverter]::ToUInt32($Image, $s + 12)
            RawSize        = [BitConverter]::ToUInt32($Image, $s + 16)
            RawOffset      = [BitConverter]::ToUInt32($Image, $s + 20)
        }
    }
    return [pscustomobject]@{ ImageBase = $imageBase; Sections = $sections }
}

function Get-FileOffset([uint64]$ImageBase, $Sections, [uint64]$VirtualAddress) {
    $rva = $VirtualAddress - $ImageBase
    foreach ($section in $Sections) {
        $end = [uint64]$section.VirtualAddress + [Math]::Max($section.VirtualSize, $section.RawSize)
        if ($rva -ge $section.VirtualAddress -and $rva -lt $end) {
            return [int]($section.RawOffset + ($rva - $section.VirtualAddress))
        }
    }
    throw ('Virtual address 0x{0:x} is outside the PE sections' -f $VirtualAddress)
}

function Test-Bytes([byte[]]$Image, [int]$Offset, [byte[]]$Expected) {
    for ($i = 0; $i -lt $Expected.Length; $i++) {
        if ($Image[$Offset + $i] -ne $Expected[$i]) { return $false }
    }
    return $true
}

function Copy-Into([byte[]]$Image, [int]$Offset, [byte[]]$Bytes) {
    [Array]::Copy($Bytes, 0, $Image, $Offset, $Bytes.Length)
}

function New-MovsdRipLoad([uint64]$SiteVa, [uint64]$ConstantVa, [byte]$ModRm) {
    # Movsd xmm<reg>, [rip+disp32]. ModRm is 0x3D for xmm7 (magic find) or 0x15 for xmm2 (portal).
    $bytes = New-Object byte[] 8
    [Array]::Copy([byte[]](0xF2, 0x0F, 0x10, $ModRm), 0, $bytes, 0, 4)
    $rel = [int]([int64]$ConstantVa - [int64]($SiteVa + 8))
    Copy-Into $bytes 4 ([BitConverter]::GetBytes($rel))
    return $bytes
}

function Set-MetadataHost([byte[]]$Metadata, [string[]]$Targets, [string]$Replacement) {
    # 0xFAB11BAF does not fit an Int32, and PowerShell parses 8-digit hex literals as
    # signed Int32 (so 0xFAB11BAF becomes negative). Compare against the decimal value.
    if ([BitConverter]::ToUInt32($Metadata, 0) -ne [uint32]4205910959) { throw 'Unknown IL2CPP metadata format' }
    $table = [BitConverter]::ToUInt32($Metadata, 8)
    $size = [BitConverter]::ToUInt32($Metadata, 12)
    $strings = [BitConverter]::ToUInt32($Metadata, 16)
    $replacementBytes = [Text.Encoding]::UTF8.GetBytes($Replacement)
    $patched = @()
    $relocatedOffset = $null

    for ($offset = $table; $offset -lt $table + $size; $offset += 8) {
        $length = [BitConverter]::ToUInt32($Metadata, $offset)
        $start = $strings + [BitConverter]::ToUInt32($Metadata, $offset + 4)
        if ($start + $length -gt $Metadata.Length) { continue }
        $value = [Text.Encoding]::UTF8.GetString($Metadata, [int]$start, [int]$length)
        if ($Targets -notcontains $value) { continue }

        if ($replacementBytes.Length -le $length) {
            [Array]::Clear($Metadata, [int]$start, [int]$length)
            Copy-Into $Metadata ([int]$start) $replacementBytes
            Copy-Into $Metadata ([int]$offset) ([BitConverter]::GetBytes([uint32]$replacementBytes.Length))
        }
        else {
            # The literal does not fit in its slot: append a copy and repoint both records.
            if ($null -eq $relocatedOffset) {
                $relocatedOffset = $Metadata.Length
                $Metadata = $Metadata + $replacementBytes
                Copy-Into $Metadata 20 ([BitConverter]::GetBytes([uint32]($Metadata.Length - $strings)))
            }
            Copy-Into $Metadata ([int]$offset) ([BitConverter]::GetBytes([uint32]$replacementBytes.Length))
            Copy-Into $Metadata ([int]($offset + 4)) ([BitConverter]::GetBytes([uint32]($relocatedOffset - $strings)))
        }
        $patched += $value
    }

    if ($patched.Count -ne 2 -or ($patched | Select-Object -Unique).Count -ne 2) {
        throw "Expected exactly two server host literals, found $($patched.Count)"
    }
    return $Metadata
}

# ---------------------------------------------------------------------------

if (-not (Test-Path (Join-Path $Source 'Nordicandia.exe'))) {
    throw "Nordicandia.exe was not found in '$Source'. Point -Source at the Steam install."
}
if (-not (Test-Path $Source -PathType Container)) { throw "Source '$Source' is not a directory." }

$sourceFull = (Resolve-Path $Source).Path.TrimEnd('\')
$destinationFull = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
if ($sourceFull -eq $destinationFull) { throw 'Source and Destination must be different folders.' }

$sourceMetadata = Join-Path $sourceFull $MetadataRelative
$sourceAssembly = Join-Path $sourceFull 'GameAssembly.dll'

Write-Host 'Checking source build ...'
$metadataHash = Get-Sha256 $sourceMetadata
$assemblyHash = Get-Sha256 $sourceAssembly
if (-not $Force -and ($metadataHash -ne $ExpectedMetadataSha256 -or $assemblyHash -ne $ExpectedGameAssemblySha256)) {
    throw @"
Unsupported game build.
  global-metadata.dat SHA-256: $metadataHash
  GameAssembly.dll   SHA-256: $assemblyHash
This patcher targets Nordicandia 1.9.3. Update the game, or pass -Force to patch anyway
(patch offsets will be wrong on other versions).
"@
}
Write-Host "  build OK ($assemblyHash)"

Write-Host "Copying to $destinationFull ..."
New-Item -ItemType Directory -Force -Path $destinationFull | Out-Null
$robocopyArgs = @("`"$sourceFull`"", "`"$destinationFull`"", '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/R:1', '/W:1')
$rc = (Start-Process -FilePath 'robocopy.exe' -ArgumentList $robocopyArgs -Wait -PassThru).ExitCode
if ($rc -ge 8) { throw "robocopy failed with exit code $rc" }

Write-Host 'Patching global-metadata.dat ...'
$metadataPath = Join-Path $destinationFull $MetadataRelative
$metadata = [IO.File]::ReadAllBytes($metadataPath)
$metadata = Set-MetadataHost $metadata $Hosts $BackendHost
[IO.File]::WriteAllBytes($metadataPath, $metadata)
Write-Host "  backend -> https://${BackendHost}:443"

Write-Host 'Patching GameAssembly.dll ...'
$assemblyPath = Join-Path $destinationFull 'GameAssembly.dll'
$assembly = [IO.File]::ReadAllBytes($assemblyPath)
$pe = Read-Pe $assembly

$tlsOffset = Get-FileOffset $pe.ImageBase $pe.Sections $TlsPatchVa
if ($assembly[$tlsOffset] -ne 0x48) { throw ('Unexpected opcode at the TLS patch site: 0x{0:x2}' -f $assembly[$tlsOffset]) }
$assembly[$tlsOffset] = 0xC3
Write-Host '  TLS certificate check disabled'

if (-not $NoGameplayPatches) {
    $magicFindOffset = Get-FileOffset $pe.ImageBase $pe.Sections $MagicFindPatchVa
    if (-not (Test-Bytes $assembly $magicFindOffset $MagicFindExpected)) { throw 'Unexpected bytes at the magic-find patch site.' }
    Copy-Into $assembly $magicFindOffset (New-MovsdRipLoad $MagicFindPatchVa $MagicFindConstantVa 0x3D)
    Write-Host '  magic find x10'

    foreach ($site in $PortalSites) {
        $offset = Get-FileOffset $pe.ImageBase $pe.Sections $site
        if (-not (Test-Bytes $assembly $offset $PortalExpected)) { throw ('Unexpected bytes at the portal-drop patch site 0x{0:x}.' -f $site) }
        Copy-Into $assembly $offset (New-MovsdRipLoad $site $PortalConstantVa 0x15)
    }
    Write-Host '  portal drop x3'
}
[IO.File]::WriteAllBytes($assemblyPath, $assembly)

[IO.File]::WriteAllText((Join-Path $destinationFull 'steam_appid.txt'), '1503790' + "`n")
$bootConfig = Join-Path $destinationFull 'Nordicandia_Data\boot.config'
if (Test-Path $bootConfig) {
    $lines = [IO.File]::ReadAllLines($bootConfig) | Where-Object { -not $_.StartsWith('single-instance') }
    [IO.File]::WriteAllLines($bootConfig, $lines)
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
Write-Host "  Client      : $destinationFull"
Write-Host "  Backend     : https://${BackendHost}:443"
Write-Host "  Launch      : SteamAppId=1503790 SteamGameId=1503790 $destinationFull\Nordicandia.exe"
Write-Host "  GameAssembly SHA-256: $(Get-Sha256 $assemblyPath)"