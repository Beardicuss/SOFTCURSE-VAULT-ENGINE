[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$propsPath = Join-Path $projectRoot 'Directory.Build.props'
$packagesPath = Join-Path $projectRoot 'Directory.Packages.props'
$projectPath = Join-Path $projectRoot 'Win11 Auto-Clean.csproj'
$installerPath = Join-Path $projectRoot 'installer\SoftcurseVaultCleaner.iss'
$trustPath = Join-Path $projectRoot 'UpdateTrust.cs'
$releaseScriptPath = Join-Path $projectRoot 'scripts\Release.ps1'
$workflowPath = Join-Path $projectRoot '.github\workflows\release.yml'

$props = [xml](Get-Content -Raw -LiteralPath $propsPath)
$project = [xml](Get-Content -Raw -LiteralPath $projectPath)
$packages = [xml](Get-Content -Raw -LiteralPath $packagesPath)
$version = [string]$props.Project.PropertyGroup.VersionPrefix
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid VersionPrefix '$version'." }
if ([string]$props.Project.PropertyGroup.Version -ne '$(VersionPrefix)') {
    throw 'Version must be derived from VersionPrefix.'
}
if ([string]$props.Project.PropertyGroup.AssemblyVersion -ne '$(VersionPrefix).0' -or
    [string]$props.Project.PropertyGroup.FileVersion -ne '$(VersionPrefix).0') {
    throw 'AssemblyVersion and FileVersion must be derived from VersionPrefix.'
}

$projectGroup = $project.Project.PropertyGroup | Select-Object -First 1
if ([string]$projectGroup.TargetFramework -ne 'net10.0-windows') {
    throw 'The application target framework is not net10.0-windows.'
}
if (([string]$projectGroup.RuntimeIdentifiers -split ';') -notcontains 'win-x64') {
    throw 'The application does not declare the win-x64 release runtime.'
}

$webViewVersion = [string]($packages.Project.ItemGroup.PackageVersion |
    Where-Object Include -eq 'Microsoft.Web.WebView2').Version
if ([string]::IsNullOrWhiteSpace($webViewVersion)) { throw 'WebView2 is not centrally versioned.' }
$lock = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'packages.lock.json') | ConvertFrom-Json
foreach ($framework in $lock.dependencies.PSObject.Properties) {
    $locked = $framework.Value.'Microsoft.Web.WebView2'
    if ($null -ne $locked -and $locked.resolved -ne $webViewVersion) {
        throw "WebView2 lock mismatch in $($framework.Name): $($locked.resolved) != $webViewVersion."
    }
}

$installer = Get-Content -Raw -LiteralPath $installerPath
if ($installer -notmatch '#define MyAppVersion GetFileVersion\(PublishSource') {
    throw 'The installer version is not derived from the published executable.'
}
if ($installer -notmatch 'AppId=\{\{A3F7B2C1-8D4E-4F6A-9E2B-1C3D5F7A8B9E\}\}') {
    throw 'The stable installer AppId changed.'
}

$trust = Get-Content -Raw -LiteralPath $trustPath
if ($trust -notmatch [regex]::Escape('https://github.com/Beardicuss/SOFTCURSE-VAULT-ENGINE/releases/latest/download/update-envelope.json')) {
    throw 'The compiled update manifest endpoint is inconsistent.'
}
$releaseScript = Get-Content -Raw -LiteralPath $releaseScriptPath
foreach ($requiredGate in @('signtool', 'sbom-tool', 'SHA256SUMS.txt', 'UpdateTrust.cs', 'status --porcelain')) {
    if ($releaseScript -notmatch [regex]::Escape($requiredGate)) {
        throw "Release script is missing required gate '$requiredGate'."
    }
}

$workflow = Get-Content -Raw -LiteralPath $workflowPath
$actionUses = [regex]::Matches($workflow, '(?m)^\s*uses:\s*([^\s#]+)')
foreach ($actionUse in $actionUses) {
    if ($actionUse.Groups[1].Value -notmatch '@[0-9a-f]{40}$') {
        throw "GitHub Action is not pinned to a full commit: $($actionUse.Groups[1].Value)"
    }
}

$trackedAudits = git -C $projectRoot ls-files -- 'security_best_practices*.md'
if ($trackedAudits) { throw "Security audit reports must remain non-public: $trackedAudits" }
$machinePaths = git -C $projectRoot grep -n -I -E 'C:\\Users\\[^\\]+' -- '*.cs' '*.xaml' '*.ps1' '*.json' '*.props' '*.csproj' 2>$null
if ($LASTEXITCODE -eq 0 -and $machinePaths) { throw "Machine-specific user path detected:`n$machinePaths" }
if ($LASTEXITCODE -gt 1) { throw 'Machine-path source scan failed.' }

$global:LASTEXITCODE = 0
Write-Output "Release integrity checks passed for version $version and WebView2 $webViewVersion."
