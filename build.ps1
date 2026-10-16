# Builds iRacing Discord Presence.
#
#   .\build.ps1             dist\iRacingPresence.exe (~0.3 MB, needs the .NET 9 Desktop Runtime)
#   .\build.ps1 -Release    release\ with the two files to share:
#                             iRacingPresence-Setup.exe       (~48 MB, runs on any 64-bit Windows PC)
#                             iRacingPresence-Setup-lite.exe  (~0.3 MB, Windows offers to install .NET 9 first)
#
# The exe is its own installer: double-click it in File Explorer and it asks to install or update.
param([switch]$Release)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src\iRacingPresence\iRacingPresence.csproj"

function Publish([string]$out, [bool]$standalone) {
    $publishArgs = @("publish", $project, "-c", "Release", "-r", "win-x64", "-o", $out, "-nologo",
        "-p:PublishSingleFile=true", "-p:DebugType=none")
    if ($standalone) {
        $publishArgs += @("--self-contained", "true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true", "-p:Flavor=full")
    } else {
        $publishArgs += @("--self-contained", "false", "-p:Flavor=lite")
    }
    dotnet @publishArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    Join-Path $out "iRacingPresence.exe"
}

if (-not $Release) {
    $exe = Publish (Join-Path $PSScriptRoot "dist") $false
    Write-Host "Built $exe"
    return
}

$releaseDir = Join-Path $PSScriptRoot "release"
New-Item -ItemType Directory -Force $releaseDir | Out-Null
$staging = Join-Path ([IO.Path]::GetTempPath()) "irp-release"

$full = Publish (Join-Path $staging "full") $true
Copy-Item $full (Join-Path $releaseDir "iRacingPresence-Setup.exe") -Force
$lite = Publish (Join-Path $staging "lite") $false
Copy-Item $lite (Join-Path $releaseDir "iRacingPresence-Setup-lite.exe") -Force
Remove-Item -Recurse -Force $staging

Get-ChildItem $releaseDir -Filter *.exe | ForEach-Object { "{0,-34} {1,6:N1} MB" -f $_.Name, ($_.Length / 1MB) }
