# Privacy

Softcurse Vault Cleaner is a local Windows desktop application. It does not include advertising, analytics, telemetry, cloud accounts, or a licensing service.

## Data processed locally

The application can inspect file and directory metadata needed for previews, cleanup plans, disk analysis, and duplicate detection. Depending on the feature selected, this can include paths, names, sizes, timestamps, file attributes, and SHA-256 content hashes used to verify duplicate files. Cleanup selections and results may appear in local application logs.

The application stores:

- preferences in `%APPDATA%\SoftcurseVaultCleaner\settings.json`;
- cleanup and error logs in `%LOCALAPPDATA%\SoftcurseVaultCleaner\Logs`; and
- WebView2 runtime data in `%LOCALAPPDATA%\SoftcurseVaultCleaner\WebView2` when the optional animated loaders are available.

WebView2 is used to display HTML files bundled with the application. Those loaders are not intended to browse remote websites. If WebView2 is unavailable, the application continues without the animations.

The application does not automatically upload scanned paths, hashes, settings, logs, file contents, or cleanup results.

## Network access

Update checks are disabled by default. When a user enables or manually starts an update check, the application requests signed update metadata and, after explicit user action, a release installer from the fixed Softcurse Vault Cleaner GitHub release locations over HTTPS. GitHub and the network provider may process ordinary connection information such as IP address, request time, and user agent under their own policies.

The production update channel is currently disabled until production signing keys and trust anchors are configured.

## Deletion and retention

Settings and logs remain on the computer until the user or uninstaller removes them. WebView2 data remains local until removed by the user or uninstaller. Files approved through the standard cleanup engine are sent to the Windows Recycle Bin; supported maintenance commands that Windows defines as non-recoverable are identified separately before confirmation.

Before sharing diagnostics, review them and remove usernames, personal paths, filenames, and other sensitive information.

## Changes

Material changes to data collection or network behavior must update this document in the same release.
