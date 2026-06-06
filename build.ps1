<#
.SYNOPSIS
    Builds, versions, deploys and packages the N.I.N.A. SyncService plugin.

.DESCRIPTION
    1. Stamps the given version into SyncService/Properties/AssemblyInfo.cs
    2. Builds the solution in the chosen configuration
    3. Copies the deployable files into dist\SyncService (+ a versioned .zip) for moving to another PC
    4. Deploys the same files into the local N.I.N.A. plugins folder

.PARAMETER Version
    Four-part plugin version, e.g. 1.1.0.0

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER NinaPluginsRoot
    The N.I.N.A. plugins root that contains the API-version folder.
    Defaults to %LOCALAPPDATA%\NINA\Plugins\3.0.0

.PARAMETER SkipDeploy
    Build and package only; do not copy into the local N.I.N.A. plugins folder.
    Useful when building on a PC where N.I.N.A. is not installed.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build.ps1 1.1.0.0

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build.ps1 1.1.0.0 -SkipDeploy
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Configuration = 'Release',

    [string]$NinaPluginsRoot = (Join-Path $env:LOCALAPPDATA 'NINA\Plugins\3.0.0'),

    [switch]$SkipDeploy
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$pluginName = 'SyncService'

# The files that make up the deployable plugin. Mirrors the DeployLocalPlugin target in
# SyncService/NINA.Plugins.SyncService.csproj - keep both in sync if dependencies change.
$artifacts = @(
    'SyncService.dll',
    'NINA.Plugins.SyncService.Service.dll',
    'Grpc.Core.Api.dll',
    'GrpcDotNetNamedPipes.dll',
    'Google.Protobuf.dll'
)

Write-Host "==> SyncService plugin $Version ($Configuration)" -ForegroundColor Cyan

# 1. Stamp the version into AssemblyInfo.cs (read/write UTF-8 without BOM to preserve the (c) glyph)
$assemblyInfo = Join-Path $repoRoot 'SyncService\Properties\AssemblyInfo.cs'
Write-Host "==> Setting version in AssemblyInfo.cs"
$content = [System.IO.File]::ReadAllText($assemblyInfo)
$content = $content -replace '\[assembly: AssemblyVersion\("[^"]*"\)\]', "[assembly: AssemblyVersion(""$Version"")]"
$content = $content -replace '\[assembly: AssemblyFileVersion\("[^"]*"\)\]', "[assembly: AssemblyFileVersion(""$Version"")]"
[System.IO.File]::WriteAllText($assemblyInfo, $content, (New-Object System.Text.UTF8Encoding($false)))

# 2. Build. This script owns deployment, so disable the in-build copy target.
Write-Host "==> Building solution"
dotnet build (Join-Path $repoRoot 'SyncService.sln') -c $Configuration -p:DeployToNinaLocal=false --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

# 3. Resolve the build output and verify every artifact is present
$outDir = Join-Path $repoRoot "SyncService\bin\$Configuration\net8.0-windows"
$sources = foreach ($a in $artifacts) {
    $p = Join-Path $outDir $a
    if (-not (Test-Path $p)) { throw "Expected build artifact not found: $p" }
    $p
}

function Publish-To([string]$dir) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Copy-Item $sources -Destination $dir -Force
}

# 4. Portable copy folder + versioned zip for moving to another PC
$distDir = Join-Path $repoRoot "dist\$pluginName"
Write-Host "==> Writing portable copy -> $distDir"
Publish-To $distDir
$zipPath = Join-Path $repoRoot "dist\$pluginName-$Version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $distDir '*') -DestinationPath $zipPath
Write-Host "    zip -> $zipPath"

# 5. Deploy into the local N.I.N.A. plugins folder
if ($SkipDeploy) {
    Write-Host "==> Skipping N.I.N.A. deploy (-SkipDeploy)" -ForegroundColor Yellow
} else {
    $deployDir = Join-Path $NinaPluginsRoot $pluginName
    Write-Host "==> Deploying -> $deployDir"
    Publish-To $deployDir
}

Write-Host ""
Write-Host "==> Done. $pluginName $Version built and packaged." -ForegroundColor Green
Write-Host "    Another PC: copy the contents of '$distDir' (or extract '$zipPath')"
Write-Host "    into  %LOCALAPPDATA%\NINA\Plugins\3.0.0\$pluginName  (close N.I.N.A. first)."
