# Publishes the tray self-contained for one runtime, checks the folder, and
# zips it: dist/whatdidido-tray-<rid>.zip. CI runs this for each runtime;
# run it locally the same way:
#
#   pwsh scripts/package.ps1 -Rid win-x64
#
# The folder runs as copied: no Windows App SDK runtime installer, no MSIX,
# no administrator rights (docs/TRAY.md T3).
param(
    [Parameter(Mandatory)][ValidateSet("win-x64", "win-arm64")][string]$Rid,
    [string]$Out = "dist"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$platform = @{ "win-x64" = "x64"; "win-arm64" = "ARM64" }[$Rid]
$folder = Join-Path $root "$Out/whatdidido-tray-$Rid"
$zip = Join-Path $root "$Out/whatdidido-tray-$Rid.zip"

if (Test-Path $folder) { Remove-Item -Recurse -Force $folder }
dotnet publish (Join-Path $root "src/WhatDidIDo.Tray") -c Release -r $Rid -p:Platform=$platform -p:RestoreLockedMode=true -o $folder
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# What the tray needs at run time, and what it must not carry: the Windows App
# SDK metapackage's AI, ML and Search components (~100 MB; the tray references
# the WinUI component only).
$required = @("whatdidido-tray.exe", "whatdidido-tray.pri", "Microsoft.ui.xaml.dll", "Microsoft.WindowsAppRuntime.dll",
    "Assets/whatdidido.png", "Assets/whatdidido.ico", "Assets/whatdidido.svg")
$missing = $required | Where-Object { -not (Test-Path (Join-Path $folder $_)) }
if ($missing) { throw "missing from the folder: $($missing -join ', ')" }
$unwanted = Get-ChildItem $folder -Recurse -File | Where-Object { $_.Name -match '^(onnxruntime|DirectML|Microsoft\.Windows\.AI\.|Microsoft\.Windows\.Search)' }
if ($unwanted) { throw "unwanted Windows App SDK components: $($unwanted.Name -join ', ')" }

if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $folder "*") -DestinationPath $zip
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
"$zip ($mb MB, $((Get-ChildItem $folder -Recurse -File).Count) files)"
