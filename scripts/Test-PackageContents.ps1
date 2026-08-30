[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Path,
    [switch]$RequireSignatures,
    [switch]$RequireSbom
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $Path).Path
$requiredFiles = @(
    'Win11 Auto-Clean.exe',
    'Win11 Auto-Clean.dll',
    'Softcurse.PrivilegedMaintenanceHelper.exe',
    'vault.ico',
    'LICENSE',
    'NOTICE',
    'PRIVACY.md',
    'SECURITY.md'
)
foreach ($name in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $name) -PathType Leaf)) {
        throw "Required package file is missing: $name"
    }
}

$forbiddenNames = @(
    'History', 'Login Data', 'Cookies', 'Web Data', 'Preferences', 'Local State',
    'license.dat', 'settings.json', 'security_best_practices_report.md',
    'security_best_practices_reaudit_2026-08-29.md'
)
$forbiddenExtensions = @('.pdb', '.user', '.suo', '.log', '.tmp', '.dmp')
$forbidden = Get-ChildItem -LiteralPath $root -Recurse -Force | Where-Object {
    $_.Name -in $forbiddenNames -or $_.Extension -in $forbiddenExtensions -or
    $_.FullName -match '(?i)WebView2[\\/]EBWebView|User Data[\\/]Default'
}
if ($forbidden) { throw "Forbidden package content detected:`n$($forbidden.FullName -join "`n")" }

if ($RequireSbom) {
    $sbom = Join-Path $root '_manifest\spdx_2.2\manifest.spdx.json'
    if (-not (Test-Path -LiteralPath $sbom -PathType Leaf)) { throw 'SPDX SBOM is missing.' }
    $document = Get-Content -Raw -LiteralPath $sbom | ConvertFrom-Json
    if ($document.spdxVersion -ne 'SPDX-2.2') { throw 'The package SBOM is not SPDX 2.2.' }
}

if ($RequireSignatures) {
    foreach ($name in @('Win11 Auto-Clean.exe', 'Win11 Auto-Clean.dll',
            'Softcurse.PrivilegedMaintenanceHelper.exe')) {
        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $root $name)
        if ($signature.Status -ne 'Valid') { throw "$name does not have a valid Authenticode signature." }
    }
}

Write-Output "Package content checks passed: $root"
