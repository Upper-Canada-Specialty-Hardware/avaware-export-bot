# Installs (or reinstalls) the latest Avaware Export Bot release for the current user, with no admin rights:
#   irm https://raw.githubusercontent.com/Upper-Canada-Specialty-Hardware/avaware-export-bot/main/install.ps1 | iex
# It downloads the exe and its SHA-256 from the latest GitHub release, refuses a download that doesn't match,
# puts the exe in %LOCALAPPDATA%\Programs\AvawareExportBot and adds a Start menu shortcut. From then on the app
# updates itself. Exit the app from its tray menu before reinstalling.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Windows PowerShell downloads far slower with the progress bar
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$release = 'https://github.com/Upper-Canada-Specialty-Hardware/avaware-export-bot/releases/latest/download'
$dir = Join-Path $env:LOCALAPPDATA 'Programs\AvawareExportBot'
$exe = Join-Path $dir 'AvawareExportBot.exe'
$download = "$exe.download"
$shaFile = "$exe.download.sha256"

if (Get-Process -Name AvawareExportBot -ErrorAction SilentlyContinue) {
    throw 'Avaware Export Bot is running. Exit it from its tray menu, then run this again.'
}

New-Item -ItemType Directory -Force -Path $dir | Out-Null
try {
    Invoke-WebRequest -UseBasicParsing -Uri "$release/AvawareExportBot.exe" -OutFile $download
    Invoke-WebRequest -UseBasicParsing -Uri "$release/AvawareExportBot.exe.sha256" -OutFile $shaFile
    $expected = ((Get-Content -Raw -Path $shaFile).Trim() -split '\s+')[0]
    $actual = (Get-FileHash -Algorithm SHA256 -Path $download).Hash
    if ($expected -notmatch '^[0-9a-fA-F]{64}$' -or $actual -ne $expected) {
        throw "The download does not match its checksum (expected $expected, got $actual). Nothing was installed."
    }
    Move-Item -Force -Path $download -Destination $exe
}
finally {
    Remove-Item -Force -ErrorAction SilentlyContinue -Path $download, $shaFile
}

$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Avaware Export Bot.lnk'
$link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $dir
$link.Save()

$version = (Get-Item $exe).VersionInfo.ProductVersion
Write-Host "Installed Avaware Export Bot $version to $exe, with a Start menu shortcut."
