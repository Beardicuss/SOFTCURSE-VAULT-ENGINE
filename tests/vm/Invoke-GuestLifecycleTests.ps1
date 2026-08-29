[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallerPath,
    [Parameter(Mandatory)]
    [string]$ExpectedVersion,
    [string]$PreviousInstallerPath,
    [switch]$ConfirmDisposableVm
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $ConfirmDisposableVm -or $env:SOFTCURSE_DISPOSABLE_VM -ne '1') {
    throw 'Refusing lifecycle tests: use a disposable VM, set SOFTCURSE_DISPOSABLE_VM=1, and pass -ConfirmDisposableVm.'
}
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Guest lifecycle installation tests must run elevated inside the disposable VM.'
}
if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'ExpectedVersion must be major.minor.patch.' }

function Assert-SignedInstaller([string]$Candidate) {
    $resolved = (Resolve-Path -LiteralPath $Candidate).Path
    if ([IO.Path]::GetExtension($resolved) -ne '.exe') { throw "Installer is not an executable: $resolved" }
    $signature = Get-AuthenticodeSignature -LiteralPath $resolved
    if ($signature.Status -ne 'Valid') { throw "Installer signature is not valid: $resolved" }
    return $resolved
}

function Invoke-Installer([string]$Candidate) {
    $process = Start-Process -FilePath $Candidate -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-'
    ) -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installer failed with exit code $($process.ExitCode)." }
}

$installer = Assert-SignedInstaller $InstallerPath
$previousInstaller = if ([string]::IsNullOrWhiteSpace($PreviousInstallerPath)) {
    $null
} else {
    Assert-SignedInstaller $PreviousInstallerPath
}
$installRoot = Join-Path $env:ProgramFiles 'Softcurse Vault Cleaner'
$appPath = Join-Path $installRoot 'Win11 Auto-Clean.exe'

$result = [ordered]@{
    Timestamp = [DateTimeOffset]::UtcNow
    ComputerName = $env:COMPUTERNAME
    Os = [Environment]::OSVersion.VersionString
    SystemDrive = $env:SystemDrive
    Is64BitOperatingSystem = [Environment]::Is64BitOperatingSystem
    WebView2Present = [bool](Get-ItemProperty -Path @(
        'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F1E7E8E8-A1B0-4707-9F65-8A4B3B9C7B74}',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F1E7E8E8-A1B0-4707-9F65-8A4B3B9C7B74}'
    ) -ErrorAction SilentlyContinue)
    PreviousVersionInstalled = $false
    CurrentVersionInstalled = $false
    UninstallRemovedBinaries = $false
}

try {
    if ($null -ne $previousInstaller) {
        Invoke-Installer $previousInstaller
        if (-not (Test-Path -LiteralPath $appPath)) { throw 'Previous installer did not install the application.' }
        $result.PreviousVersionInstalled = $true
    }

    Invoke-Installer $installer
    if (-not (Test-Path -LiteralPath $appPath)) { throw 'Current installer did not install the application.' }
    $installedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($appPath).FileVersion
    if ($installedVersion -ne "$ExpectedVersion.0") {
        throw "Installed FileVersion '$installedVersion' does not match '$ExpectedVersion.0'."
    }
    $result.CurrentVersionInstalled = $true

    $uninstaller = Get-ChildItem -LiteralPath $installRoot -Filter 'unins*.exe' | Select-Object -First 1
    if ($null -eq $uninstaller) { throw 'Uninstaller was not installed.' }
    $uninstall = Start-Process -FilePath $uninstaller.FullName -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
    ) -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) { throw "Uninstaller failed with exit code $($uninstall.ExitCode)." }
    $result.UninstallRemovedBinaries = -not (Test-Path -LiteralPath $appPath)
    if (-not $result.UninstallRemovedBinaries) { throw 'Uninstall left application binaries behind.' }
}
finally {
    $result | ConvertTo-Json -Depth 4 | Write-Output
}
