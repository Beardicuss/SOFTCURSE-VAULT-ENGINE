<p align="center">
  <a href="https://softcursesystems.pages.dev/lab/vault">
    <img src="Resources/vault.png" alt="Softcurse Vault Cleaner" width="360">
  </a>
</p>

<h1 align="center">Softcurse Vault Cleaner</h1>

<p align="center">
  A Windows cleanup and disk-analysis application created by
  <a href="https://softcursesystems.pages.dev">Softcurse Systems</a>.
</p>

<p align="center">
  <a href="https://softcursesystems.pages.dev/lab/vault">Application page</a> ·
  <a href="https://github.com/Beardicuss/SOFTCURSE-VAULT-ENGINE/releases">Downloads</a> ·
  <a href="LICENSE">Apache-2.0 license</a>
</p>

Softcurse Vault Cleaner is a dark-neon WPF utility for reviewing and reclaiming storage on Windows. Version `1.0.0` combines a safety-focused cleanup engine, multi-drive disk analysis, duplicate and large-file discovery, and read-only startup and registry inspection.

## Current features

### Vault Cleaner

- Cleans selected user temporary files, browser caches, thumbnail caches, developer caches, gaming and communications caches, and user crash reports.
- Supports approved custom folders after protected-path validation.
- Shows a categorized confirmation preview before making changes.
- Blocks drive roots, Windows and application directories, unsafe profile roots, links, junctions, and mount points.
- Sends allowed filesystem targets to the Recycle Bin.
- Clearly separates irreversible operations such as emptying the Recycle Bin and flushing the DNS resolver cache.
- Offers optional Windows component cleanup through a fixed, separately elevated helper. It does not use `ResetBase` or delete the Windows Installer cache, shadow copies, or pagefile configuration.
- Keeps a persistent operation log in `%LOCALAPPDATA%\SoftcurseVaultCleaner\Logs`.

### Disk Analyzer

- Detects ready local fixed and removable drives.
- Lets the user choose the disk to analyze and refresh the drive list.
- Defaults to the Windows system drive without assuming it is `C:`.
- Provides disk overview, cleanup suggestions, large-file discovery, exact duplicate detection, and report export.
- Supports cancellation and tolerates inaccessible folders without stopping the entire scan.

### Startup Manager

- Lists programs configured to start when the user signs in.
- Reports inspected registry entries whose referenced executable path is missing.
- Is currently inspection-only. Delete and repair controls remain disabled until backup, confirmation, and rollback are implemented.

### Settings and help

- Saves general, cleanup-default, and log-appearance preferences per user.
- Exposes defaults for every current cleaner category.
- Resets immediately to safe, opt-in defaults.
- Includes an in-app FAQ covering cleanup safety, multi-drive analysis, Startup Manager, recovery, and troubleshooting.

## Safety model

- Cleanup choices are opt-in; nothing is preselected on a fresh installation.
- Every cleanup request is rebuilt as a safety-validated execution plan.
- Unsafe custom paths fail closed.
- Filesystem deletion is recoverable through the Recycle Bin unless the user explicitly empties it.
- The main application runs as a standard user. Only the allowlisted Windows component-cleanup helper requests UAC.
- Startup and registry scans do not modify the system.

Always read the preview before confirming a cleanup. Closing browsers and other active applications first improves cache-cleaning results.

## Requirements

- 64-bit Windows 10 or Windows 11
- Approximately 250 MB of available disk space
- Microsoft Edge WebView2 Evergreen Runtime for animated loaders; core cleanup remains available without it

The release is self-contained and does not require a separate .NET installation.

## Install

Download `SoftcurseVaultCleaner_Setup_v1.0.0.exe` from the [GitHub Releases page](https://github.com/Beardicuss/SOFTCURSE-VAULT-ENGINE/releases), run it, and launch the app normally as a standard user.

If an older package was installed with a higher experimental version number, uninstall it before installing `1.0.0`.

## Build from source

Prerequisites:

- .NET SDK `10.0.400`, selected by `global.json`
- Windows x64
- Inno Setup 6 only when producing the installer locally

```powershell
dotnet restore "Win11 Auto-Clean.sln" --locked-mode
dotnet build "Win11 Auto-Clean.sln" --configuration Release --no-restore -warnaserror
dotnet run --project "SafeCleanupEngine.Tests/SafeCleanupEngine.Tests.csproj" --configuration Release --no-build
./scripts/Test-ReleaseIntegrity.ps1
```

Package versions are centralized in `Directory.Packages.props`, and committed lock files make restores repeatable. GitHub Actions performs the full Windows build, test, integrity, packaging, and artifact checks.

## Testing

Automated tests cover path normalization, protected locations, non-`C:` Windows layouts, junction and link rejection, cancellation, partial failures, duplicate detection, recoverable deletion, and update verification. Routine tests operate on randomized temporary fixtures and injected system-operation fakes rather than real Windows data.

The disposable-VM harness in [tests/vm/README.md](tests/vm/README.md) is optional release-hardening infrastructure. It must never be run against a normal workstation.

## Project layout

```text
Win11 Auto-Clean/
├── MainWindow.xaml                 Main WPF interface
├── MainWindowViewModel.cs          Cleaner and application state
├── CleanerService.cs               Cleanup target catalog and execution
├── SafeCleanupEngine.cs            Path policy, preview, and safe deletion
├── DiskAnalyzerService.cs          Disk scanning and file analysis
├── DiskAnalyzerViewModel.cs        Multi-drive analyzer UI logic
├── AutoTuneViewModel.cs            Read-only startup/registry inspection
├── PrivilegedMaintenanceHelper/    Allowlisted elevated maintenance helper
├── SafeCleanupEngine.Tests/        Automated safety and regression tests
├── installer/                      Inno Setup definition
├── scripts/                        Build and release-integrity tooling
└── tests/vm/                       Optional disposable-VM harness
```

## Releases and updates

The development-installer workflow produces the current `1.0.0` installer and checksum artifact. The production release and automatic-update channel intentionally remain fail-closed until production signing and update trust material are provisioned. See [RELEASE.md](RELEASE.md) for that future release procedure.

Security and local-data handling are documented in [SECURITY.md](SECURITY.md) and [PRIVACY.md](PRIVACY.md).

## License

Copyright 2026 Softcurse Systems.

Licensed under the [Apache License 2.0](LICENSE). See [NOTICE](NOTICE) for attribution information.
