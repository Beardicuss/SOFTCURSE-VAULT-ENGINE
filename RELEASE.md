# Secure Release Procedure

The production update channel is intentionally disabled until a real, publicly trusted Authenticode certificate is provisioned. Do not replace the empty trust values with test or self-signed credentials for a public release.

## Signing architecture decision

The current release script supports only an exportable RSA Authenticode PFX and uses that RSA key for both Authenticode and update-metadata signatures. It is intentionally blocked until production credentials are provisioned. Do not purchase a certificate merely to satisfy this implementation assumption: many modern public signing services keep private keys in an HSM and never export a PFX.

Before the first public release, choose and review one of these designs:

1. **Cloud/HSM signing (preferred):** integrate the selected Authenticode service into CI, use a separate RSA key for update metadata, compile only public verification material into the application, and design signer validation to tolerate legitimate certificate rotation without accepting another publisher.
2. **Exportable PFX:** obtain a publicly trusted RSA Authenticode certificate whose terms and key storage permit the existing CI workflow. Export its SubjectPublicKeyInfo and SHA-256 certificate hash into `UpdateTrust`, then store the Base64 PFX and password in protected GitHub release-environment secrets.

For the current PFX path, the release script verifies that the supplied certificate matches both compiled trust anchors before it builds or signs anything. CI installs the pinned .NET 10 SDK for the application and the .NET 8 runtime required by the pinned Microsoft SBOM tool. A cloud-signing migration must replace those PFX-specific checks with equivalent fail-closed service identity, artifact-signature, metadata-signature, and certificate-rotation checks before release tags are enabled.

## Publishing a version

1. Update `VersionPrefix` and related assembly versions in `Directory.Build.props`.
2. Restore in locked mode, build with warnings as errors, run the safety tests, and confirm dependency vulnerability and outdated-package checks are clean.
3. Commit the release, create the exact annotated tag `v<VersionPrefix>`, and push the tag.
4. The pinned GitHub Actions workflow runs `scripts/Release.ps1`, produces the signed installer, portable archive, signed update envelope, SPDX SBOM, SHA-256 checksums, and GitHub artifact provenance, then publishes the GitHub release.

The release script rejects a dirty worktree, a mismatched tag, missing signing material, mismatched trust anchors, unsigned output, timestamp verification failures, browser-profile data, debug symbols, missing SBOM output, or an existing release directory.

## Unsigned owner-test candidate

`scripts/Build-UnsignedInstaller.ps1` and the separate **Build unsigned installer candidate** workflow may produce an explicitly named `_UNSIGNED.exe` for owner testing or a clearly marked GitHub pre-release. This path does not create update metadata and cannot publish a production release. Windows will identify its publisher as unknown. It must never be renamed or represented as a signed production installer.

## Update verification model

The application fetches metadata only from the fixed GitHub release endpoint. It verifies the RSA signature before parsing the manifest, rejects rollback versions and non-allowlisted download locations, enforces signed size and SHA-256 values while streaming to a temporary file, then requires a valid Windows Authenticode chain and the pinned signer certificate hash. The installer is not launched without explicit user confirmation, and the existing installation remains in place until the installer completes.

## Runtime packaging

Releases target `net10.0-windows` and are self-contained for `win-x64`; end users do not need a separate .NET runtime. The privileged helper is a self-contained single-file executable. WebView2 Evergreen is optional for animated loader content and is not included as user profile data in the release.
