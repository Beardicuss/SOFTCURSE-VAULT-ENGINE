# Secure Release Procedure

The production update channel is intentionally disabled until a real, publicly trusted Authenticode certificate is provisioned. Do not replace the empty trust values with test or self-signed credentials for a public release.

## One-time trust provisioning

1. Obtain a production RSA Authenticode code-signing certificate that supports private-key export for the CI signing workflow.
2. Export the certificate's RSA SubjectPublicKeyInfo as Base64 and place it in `UpdateTrust.MetadataPublicKeySpkiBase64`.
3. Export the certificate's SHA-256 certificate hash as 64 hexadecimal characters and place it in `UpdateTrust.InstallerSignerCertificateSha256`.
4. Add the PFX as Base64 to the GitHub Actions secret `SOFTCURSE_SIGNING_PFX_BASE64`.
5. Add its password to `SOFTCURSE_SIGNING_PASSWORD`.

The release script verifies that the supplied PFX matches both compiled trust anchors before it builds or signs anything. CI installs the pinned .NET 10 SDK for the application and the .NET 8 runtime required by the pinned Microsoft SBOM tool.

## Publishing a version

1. Update `VersionPrefix` and related assembly versions in `Directory.Build.props`.
2. Restore in locked mode, build with warnings as errors, run the safety tests, and confirm dependency vulnerability and outdated-package checks are clean.
3. Commit the release, create the exact annotated tag `v<VersionPrefix>`, and push the tag.
4. The pinned GitHub Actions workflow runs `scripts/Release.ps1`, produces the signed installer, portable archive, signed update envelope, SPDX SBOM, SHA-256 checksums, and GitHub artifact provenance, then publishes the GitHub release.

The release script rejects a dirty worktree, a mismatched tag, missing signing material, mismatched trust anchors, unsigned output, timestamp verification failures, browser-profile data, debug symbols, missing SBOM output, or an existing release directory.

## Update verification model

The application fetches metadata only from the fixed GitHub release endpoint. It verifies the RSA signature before parsing the manifest, rejects rollback versions and non-allowlisted download locations, enforces signed size and SHA-256 values while streaming to a temporary file, then requires a valid Windows Authenticode chain and the pinned signer certificate hash. The installer is not launched without explicit user confirmation, and the existing installation remains in place until the installer completes.

## Runtime packaging

Releases target `net10.0-windows` and are self-contained for `win-x64`; end users do not need a separate .NET runtime. The privileged helper is a self-contained single-file executable. WebView2 Evergreen is optional for animated loader content and is not included as user profile data in the release.
