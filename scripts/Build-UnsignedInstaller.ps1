[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectFile = Join-Path $projectRoot 'Win11 Auto-Clean.csproj'
$helperProject = Join-Path $projectRoot 'PrivilegedMaintenanceHelper\PrivilegedMaintenanceHelper.csproj'
$installerScript = Join-Path $projectRoot 'installer\SoftcurseVaultCleaner.iss'
$artifactsRoot = Join-Path $projectRoot 'artifacts'
$stagingRoot = Join-Path $artifactsRoot ('unsigned-staging-' + [Guid]::NewGuid().ToString('N'))
$appStage = Join-Path $stagingRoot 'app'
$helperStage = Join-Path $stagingRoot 'helper'
$installerStage = Join-Path $stagingRoot 'installer'

function Assert-UnderArtifacts([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetFullPath($artifactsRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsigned package path escaped the artifacts directory: $full"
    }
}

function Invoke-Checked([string]$FileName, [string[]]$Arguments) {
    & $FileName @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FileName failed with exit code $LASTEXITCODE."
    }
}

Assert-UnderArtifacts $stagingRoot
New-Item -ItemType Directory -Path $appStage, $helperStage, $installerStage -Force | Out-Null

try {
    $props = [xml](Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'Directory.Build.props'))
    $version = [string]$props.Project.PropertyGroup.VersionPrefix
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid release version '$version'." }

    $finalRoot = Join-Path $artifactsRoot "unsigned-v$version"
    Assert-UnderArtifacts $finalRoot
    if (Test-Path -LiteralPath $finalRoot) {
        throw "Unsigned output already exists: $finalRoot"
    }

    Invoke-Checked dotnet @('restore', $projectFile, '--locked-mode', '-r', $RuntimeIdentifier)
    Invoke-Checked dotnet @('restore', $helperProject, '--locked-mode', '-r', $RuntimeIdentifier)
    Invoke-Checked dotnet @('publish', $projectFile, '-c', $Configuration, '-r', $RuntimeIdentifier,
        '--self-contained', 'true', '--no-restore', '-p:DebugType=None', '-p:DebugSymbols=false',
        '-o', $appStage)
    Invoke-Checked dotnet @('publish', $helperProject, '-c', $Configuration, '-r', $RuntimeIdentifier,
        '--self-contained', 'true', '--no-restore', '-p:PublishSingleFile=true',
        '-p:PublishTrimmed=true', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $helperStage)

    $helperName = 'Softcurse.PrivilegedMaintenanceHelper'
    foreach ($sidecar in @("$helperName.dll", "$helperName.deps.json", "$helperName.runtimeconfig.json")) {
        $sidecarPath = Join-Path $appStage $sidecar
        if (Test-Path -LiteralPath $sidecarPath) { Remove-Item -LiteralPath $sidecarPath -Force }
    }
    Copy-Item -LiteralPath (Join-Path $helperStage "$helperName.exe") -Destination $appStage -Force

    & (Join-Path $projectRoot 'scripts\Test-PackageContents.ps1') -Path $appStage

    Invoke-Checked dotnet @('tool', 'restore')
    Invoke-Checked dotnet @('tool', 'run', 'sbom-tool', 'generate', '-b', $appStage,
        '-bc', $projectRoot, '-pn', 'Softcurse Vault Cleaner', '-pv', $version,
        '-ps', 'Softcurse Systems', '-nsb',
        'https://github.com/Beardicuss/SOFTCURSE-VAULT-ENGINE')
    $sbomPath = Join-Path $appStage '_manifest\spdx_2.2\manifest.spdx.json'
    if (-not (Test-Path -LiteralPath $sbomPath)) { throw 'The unsigned package SBOM was not generated.' }

    $iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if (-not $iscc) {
        $defaultIscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
        if (-not (Test-Path -LiteralPath $defaultIscc)) {
            throw 'Inno Setup 6 compiler was not found.'
        }
        $isccPath = $defaultIscc
    }
    else { $isccPath = $iscc.Source }

    Invoke-Checked $isccPath @(
        "/DPublishSource=$appStage",
        "/DReleaseOutputDir=$installerStage",
        "/DMyAppVersion=$version",
        '/DReleaseChannelSuffix=_UNSIGNED',
        $installerScript)

    $installers = @(Get-ChildItem -LiteralPath $installerStage -Filter '*_UNSIGNED.exe' -File)
    if ($installers.Count -ne 1) { throw "Expected one unsigned installer, found $($installers.Count)." }
    $installer = $installers[0]
    $signature = Get-AuthenticodeSignature -LiteralPath $installer.FullName
    if ($signature.Status -ne 'NotSigned') {
        throw "Unsigned build unexpectedly has Authenticode status '$($signature.Status)'."
    }

    Copy-Item -LiteralPath $sbomPath -Destination (
        Join-Path $installerStage "SoftcurseVaultCleaner_v$version.spdx.json")
    @(
        'UNSIGNED RELEASE CANDIDATE'
        ''
        'This installer is not Authenticode signed. Windows will identify the publisher as unknown.'
        'Use it for owner testing or publish it explicitly as a GitHub pre-release.'
        'Do not describe it as a signed production release.'
        ''
        'Product: https://softcursesystems.pages.dev/lab/vault'
        'Source: https://github.com/Beardicuss/SOFTCURSE-VAULT-ENGINE'
    ) | Set-Content -LiteralPath (Join-Path $installerStage 'UNSIGNED-README.txt') -Encoding utf8

    Get-ChildItem -LiteralPath $installerStage -File | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.Name
    } | Set-Content -LiteralPath (Join-Path $installerStage 'SHA256SUMS.txt') -Encoding ascii

    Move-Item -LiteralPath $installerStage -Destination $finalRoot
    Write-Output "Unsigned release candidate created at $finalRoot"
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Assert-UnderArtifacts $stagingRoot
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
