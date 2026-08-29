# Phase 4 disposable Windows test matrix

Destructive cleanup and installer lifecycle tests must never run on a developer workstation. Use dedicated Hyper-V guests whose names are supplied explicitly and whose clean checkpoint is restored before every run.

## Required VM images

| Image | Account/layout purpose |
| --- | --- |
| Windows 10 x64 | Supported older OS baseline, standard-user application launch |
| Windows 11 x64 | Current OS baseline, standard-user application launch |
| Windows 11 x64 without WebView2 Evergreen | Confirms cleanup remains usable when animated loader content is unavailable |
| Windows 11 with Windows installed on a non-`C:` volume | Detects system-drive assumptions |
| Windows 11 with two standard-user profiles | Confirms per-user settings, WebView2 data, logs, and update staging remain isolated |

Every VM should have a clean checkpoint with PowerShell Direct enabled and a dedicated local administrator used only by the host harness. Create separate standard users for the interactive application checks.

## Automated lifecycle run

Run from an elevated host PowerShell session:

```powershell
$credential = Get-Credential -Message 'Disposable VM administrator'
./tests/vm/Invoke-HyperVPhase4Matrix.ps1 `
  -VmNames 'SVC-WIN10','SVC-WIN11','SVC-NOWEBVIEW','SVC-NONC' `
  -CheckpointName 'Phase4-Clean' `
  -GuestAdministratorCredential $credential `
  -InstallerPath 'C:\releases\SoftcurseVaultCleaner_Setup_v3.0.0.exe' `
  -ExpectedVersion '3.0.0' `
  -ConfirmRestoreCheckpoints
```

The harness refuses to run without the destructive confirmation switch. The guest script independently requires `SOFTCURSE_DISPOSABLE_VM=1`, elevation, a valid Authenticode installer signature, and an explicit disposable-VM confirmation.

Supply `-PreviousInstallerPath` to test an upgrade over the previous signed release. Results are written under ignored `artifacts/vm-results`.

## Interactive checks per image

1. Launch the installed application as each standard user; confirm it does not request elevation at startup.
2. Preview every cleanup category and confirm no operation occurs before confirmation.
3. Cancel before execution, during enumeration, and between targets; confirm cancellation is reported and later targets are untouched.
4. Introduce locked/inaccessible fixture files and verify individual failures are reported without hiding successful independent targets.
5. Add junctions and symbolic links above and below a selected target; confirm preview denies them.
6. On the non-`C:` image, confirm Windows and Program Files locations on the actual system volume are protected.
7. On the no-WebView2 image, confirm the UI remains usable and cleanup does not fail.
8. Interrupt installation and update only after taking an additional checkpoint; restore the checkpoint after recording the result.
9. Uninstall silently and interactively. Silent uninstall must remove binaries without prompting; interactive uninstall must offer removal of per-user settings, logs, staged updates, and WebView2 data.
10. Export event logs, installer logs, application logs, screenshots, and the JSON lifecycle result before restoring the checkpoint.

Production update end-to-end testing remains blocked until a real Authenticode certificate and matching update trust anchors are provisioned. Test keys must not be compiled into public builds.
