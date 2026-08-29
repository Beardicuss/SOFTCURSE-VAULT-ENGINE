[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$VmNames,
    [Parameter(Mandatory)]
    [string]$CheckpointName,
    [Parameter(Mandatory)]
    [pscredential]$GuestAdministratorCredential,
    [Parameter(Mandatory)]
    [string]$InstallerPath,
    [Parameter(Mandatory)]
    [string]$ExpectedVersion,
    [string]$PreviousInstallerPath,
    [switch]$ConfirmRestoreCheckpoints
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $ConfirmRestoreCheckpoints) {
    throw 'Checkpoint restoration discards VM state. Pass -ConfirmRestoreCheckpoints only for explicitly disposable test VMs.'
}
if ($VmNames.Count -eq 0 -or $VmNames | Where-Object { [string]::IsNullOrWhiteSpace($_) }) {
    throw 'Every VM must be named explicitly.'
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$guestScript = Join-Path $PSScriptRoot 'Invoke-GuestLifecycleTests.ps1'
$resolvedInstaller = (Resolve-Path -LiteralPath $InstallerPath).Path
$resolvedPrevious = if ([string]::IsNullOrWhiteSpace($PreviousInstallerPath)) {
    $null
} else {
    (Resolve-Path -LiteralPath $PreviousInstallerPath).Path
}
$resultsRoot = Join-Path $repoRoot 'artifacts\vm-results'
New-Item -ItemType Directory -Path $resultsRoot -Force | Out-Null

foreach ($vmName in $VmNames) {
    $vm = Get-VM -Name $vmName -ErrorAction Stop
    $checkpoint = Get-VMSnapshot -VM $vm -Name $CheckpointName -ErrorAction Stop
    if ($vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force }
    Restore-VMSnapshot -VMSnapshot $checkpoint -Confirm:$false
    Start-VM -VM $vm | Out-Null

    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(3)
    do {
        Start-Sleep -Seconds 3
        $heartbeat = Get-VMIntegrationService -VMName $vmName -Name 'Heartbeat'
    } while ($heartbeat.PrimaryStatusDescription -ne 'OK' -and [DateTimeOffset]::UtcNow -lt $deadline)
    if ($heartbeat.PrimaryStatusDescription -ne 'OK') { throw "VM '$vmName' did not become ready." }

    $session = New-PSSession -VMName $vmName -Credential $GuestAdministratorCredential
    try {
        $guestRoot = 'C:\SoftcursePhase4'
        Invoke-Command -Session $session -ScriptBlock {
            param($Path)
            New-Item -ItemType Directory -Path $Path -Force | Out-Null
        } -ArgumentList $guestRoot
        Copy-Item -ToSession $session -LiteralPath $guestScript -Destination $guestRoot
        Copy-Item -ToSession $session -LiteralPath $resolvedInstaller -Destination $guestRoot
        if ($null -ne $resolvedPrevious) {
            Copy-Item -ToSession $session -LiteralPath $resolvedPrevious -Destination $guestRoot
        }

        $guestResult = Invoke-Command -Session $session -ScriptBlock {
            param($Root, $InstallerName, $Version, $PreviousName)
            $env:SOFTCURSE_DISPOSABLE_VM = '1'
            $arguments = @{
                InstallerPath = Join-Path $Root $InstallerName
                ExpectedVersion = $Version
                ConfirmDisposableVm = $true
            }
            if ($PreviousName) { $arguments.PreviousInstallerPath = Join-Path $Root $PreviousName }
            & (Join-Path $Root 'Invoke-GuestLifecycleTests.ps1') @arguments
        } -ArgumentList $guestRoot, ([IO.Path]::GetFileName($resolvedInstaller)), $ExpectedVersion,
            $(if ($resolvedPrevious) { [IO.Path]::GetFileName($resolvedPrevious) } else { $null })
        $guestResult | Set-Content -LiteralPath (Join-Path $resultsRoot "$vmName.json")
    }
    finally {
        if ($null -ne $session) { Remove-PSSession $session }
        Stop-VM -Name $vmName -TurnOff -Force
    }
}

Write-Output "Disposable VM results written to $resultsRoot"
