# Builds ClipShare.exe: a single self-contained file that needs no .NET install.
#   ./scripts/publish.ps1             -> publish/win-x64/ClipShare.exe
#   ./scripts/publish.ps1 -Arch arm64 -> publish/win-arm64/ClipShare.exe
#   ./scripts/publish.ps1 -Version 1.2.3 -> stamps that version into the exe
#   ./scripts/publish.ps1 -Install    -> also copies it to %LOCALAPPDATA%\Programs\ClipShare and starts it
param(
    [ValidateSet("x64", "arm64")] [string] $Arch = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "arm64" } else { "x64" }),
    [string] $Version,
    [switch] $Install
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "publish/win-$Arch"
# Build as an array: a one-item array returned from an if expression collapses to a string, which
# splats character by character.
[string[]] $versionArgs = @()
if ($Version) { $versionArgs += "-p:Version=$Version" }

dotnet publish (Join-Path $root "src/ClipShare/ClipShare.csproj") -c Release -r "win-$Arch" --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None @versionArgs -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built $out\ClipShare.exe"

if ($Install) {
    $target = Join-Path $env:LOCALAPPDATA "Programs\ClipShare"
    Get-Process ClipShare -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $out "ClipShare.exe") $target -Force
    $shortcut = Join-Path ([Environment]::GetFolderPath("Programs")) "ClipShare.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    $link.TargetPath = Join-Path $target "ClipShare.exe"
    $link.Save()
    Start-Process (Join-Path $target "ClipShare.exe")
    Write-Host "Installed to $target and added ClipShare to the Start menu"
}
