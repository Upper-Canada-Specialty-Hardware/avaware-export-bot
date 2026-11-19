# Checks a published exe before it can ship: it must be a Windows GUI program (PE subsystem 2; 3 is a console
# program, which opens a console window beside the app) and carry the expected version resource.
# The SDK finishes the exe this way only on Windows (NETSDK1074 elsewhere), so a wrong runner fails here.
#   pwsh .github/scripts/check-exe.ps1 -Exe publish/AvawareExportBot.exe -Version 1.2.3
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $Version
)
$ErrorActionPreference = 'Stop'

$path = (Resolve-Path $Exe).Path
$bytes = [IO.File]::ReadAllBytes($path)
$problems = @()

$subsystem = $null
if ($bytes.Length -ge 64 -and $bytes[0] -eq 0x4D -and $bytes[1] -eq 0x5A) {
    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    # The subsystem is 68 bytes into the optional header, which follows the 4-byte signature and 20-byte file header.
    if ($pe -gt 0 -and $pe + 24 + 70 -le $bytes.Length -and [Text.Encoding]::ASCII.GetString($bytes, $pe, 2) -eq 'PE') {
        $subsystem = [BitConverter]::ToUInt16($bytes, $pe + 24 + 68)
    }
}
if ($null -eq $subsystem) { $problems += 'not a PE file' }
elseif ($subsystem -ne 2) { $problems += "subsystem is $subsystem, not 2 (Windows GUI)$(if ($subsystem -eq 3) { '; it is a console program' })" }

$info = (Get-Item $path).VersionInfo
$fileVersion = "$($info.FileVersion)"
$productVersion = "$($info.ProductVersion)"
if ($fileVersion -ne "$Version.0") { $problems += "file version is '$fileVersion', not '$Version.0'" }
if ($productVersion -ne $Version -and -not $productVersion.StartsWith("$Version+")) { $problems += "product version is '$productVersion', not '$Version'" }

"exe:             $path"
"subsystem:       $subsystem"
"file version:    $fileVersion"
"product version: $productVersion"
if ($problems.Count -gt 0) {
    foreach ($p in $problems) { Write-Output "::error::$(Split-Path -Leaf $path): $p" }
    exit 1
}
'exe check passed'
