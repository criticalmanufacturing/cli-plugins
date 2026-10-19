#Requires -Version 5.1
<#
.SYNOPSIS
Checks the packaged binaries and tests a local Windows installation of cmf-codereview.
.DESCRIPTION
Run npm run build:bundle and npm run pack first. The script defaults to the
archive for the current npm/package.json version. It installs into a unique
temporary directory, runs the real npm postinstall hook, and checks CLI help.
The temporary installation is kept for inspection. Nothing is published or
installed globally, and the caller's working directory is restored.
.PARAMETER ArchivePath
Optional path to an existing npm .tgz archive.
.EXAMPLE
.\scripts\Test-Package.ps1
.EXAMPLE
.\scripts\Test-Package.ps1 -ArchivePath .\npm\packs\criticalmanufacturing-codereview-1.0.0-alpha.tgz
#>
[CmdletBinding()]
param(
    [string]$ArchivePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw 'This script tests the Windows installer. Run it in Windows PowerShell or PowerShell on Windows.'
}

$tarCommand = Get-Command tar.exe -CommandType Application -ErrorAction Stop
$npmCommand = Get-Command npm.cmd -CommandType Application -ErrorAction Stop
$nodeCommand = Get-Command node.exe -CommandType Application -ErrorAction Stop
$nodePlatform = & $nodeCommand.Source -p '[process.platform,process.arch].join(String.fromCharCode(47))'
if ($LASTEXITCODE -ne 0) { throw 'Unable to determine the Node.js platform.' }
if ($nodePlatform -ne 'win32/x64') {
    throw "The archive contains x64 binaries. Expected win32/x64, found $nodePlatform."
}

$pluginRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    $manifestPath = Join-Path $pluginRoot 'npm/package.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $archiveBaseName = $manifest.name.TrimStart('@').Replace('/', '-')
    $archiveFileName = '{0}-{1}.tgz' -f $archiveBaseName, $manifest.version
    $ArchivePath = Join-Path (Join-Path $pluginRoot 'npm/packs') $archiveFileName
}

if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
    throw "Archive not found: $ArchivePath. Run npm run build:bundle and npm run pack from the plugin directory first."
}
$resolvedArchive = (Resolve-Path -LiteralPath $ArchivePath).Path
Write-Host "Checking archive: $resolvedArchive"

$archiveEntries = @(& $tarCommand.Source -tzf $resolvedArchive)
if ($LASTEXITCODE -ne 0) { throw 'Unable to list the npm archive.' }
$requiredEntries = @(
    'package/package.json',
    'package/run.js',
    'package/postinstall.js',
    'package/utils.js',
    'package/dist/linux-x64/cmf-codereview',
    'package/dist/win-x64/cmf-codereview.exe',
    'package/dist/osx-x64/cmf-codereview'
)
foreach ($requiredEntry in $requiredEntries) {
    if ($archiveEntries -cnotcontains $requiredEntry) {
        throw "Required archive entry is missing: $requiredEntry"
    }
}
Write-Host '[OK] Wrapper files and all three platform executables are packaged.'

$archivedManifestLines = @(& $tarCommand.Source -xOf $resolvedArchive 'package/package.json')
if ($LASTEXITCODE -ne 0) { throw 'Unable to read package.json from the archive.' }
$archivedManifest = ($archivedManifestLines -join [Environment]::NewLine) | ConvertFrom-Json
if ($archivedManifest.name -ne '@criticalmanufacturing/codereview') {
    throw 'The archive is not the @criticalmanufacturing/codereview package.'
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('cmf-codereview-smoke-' + [guid]::NewGuid().ToString('N'))
$installDirectory = Join-Path $tempRoot 'smoke-install'
New-Item -ItemType Directory -Path $installDirectory | Out-Null
Write-Host "Temporary installation: $installDirectory"

Push-Location -LiteralPath $installDirectory
try {
    & $npmCommand.Source init --yes
    if ($LASTEXITCODE -ne 0) { throw 'npm init failed.' }

    & $npmCommand.Source install --no-audit --no-fund --foreground-scripts --ignore-scripts=false --bin-links=true $resolvedArchive
    if ($LASTEXITCODE -ne 0) { throw 'Installing the archive or running its postinstall hook failed.' }

    $installedManifestPath = Join-Path $installDirectory 'node_modules/@criticalmanufacturing/codereview/package.json'
    $installedManifest = Get-Content -LiteralPath $installedManifestPath -Raw | ConvertFrom-Json
    if ($installedManifest.version -ne $archivedManifest.version) {
        throw 'The installed package version does not match the archive.'
    }

    $cliPath = Join-Path $installDirectory 'node_modules/.bin/cmf-codereview.cmd'
    if (-not (Test-Path -LiteralPath $cliPath -PathType Leaf)) {
        throw 'npm did not create the cmf-codereview command shim.'
    }

    & $cliPath --help
    if ($LASTEXITCODE -ne 0) { throw 'The installed CLI failed its startup check.' }
    foreach ($subcommand in @('scan', 'review', 'report')) {
        & $cliPath $subcommand --help
        if ($LASTEXITCODE -ne 0) { throw "The installed CLI failed the $subcommand help check." }
    }
}
finally {
    Pop-Location
}

Write-Host '[OK] Archive verification and Windows installation checks passed.'
Write-Host "Temporary installation kept at: $installDirectory"
