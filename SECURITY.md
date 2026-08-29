# Security Policy

## Supported versions

Softcurse Vault Cleaner is under active pre-release development. Only the latest commit on `main` receives security fixes. No published build should be treated as production-supported until the repository documents a signed public release.

## Reporting a vulnerability

Do not disclose a suspected vulnerability in a public issue before the maintainer has had a reasonable opportunity to investigate it. Use GitHub's private vulnerability reporting feature for this repository when available. If private reporting is unavailable, open a public issue containing only a request for a private security contact; do not include exploit details, personal information, credentials, or affected user data.

Include the following when possible:

- affected commit, version, or file;
- reproduction steps using disposable test data;
- expected and actual behavior;
- security impact and affected Windows versions;
- relevant logs with usernames, paths, tokens, and other personal data removed; and
- whether the issue is already public or actively exploited.

Reports concerning path escape, protected-directory deletion, junction or symbolic-link traversal, privilege-boundary bypass, update-signature verification, installer integrity, or exposure of local logs/settings are treated as security issues.

## Safe testing

Do not test destructive cleanup against a real user profile, Windows installation, shared computer, or third-party system. Use the fixture-based `SafeCleanupEngine.Tests` suite. The Hyper-V lifecycle harness is optional release-hardening infrastructure and may run only inside an explicitly disposable environment.

Never submit real signing keys, PFX files, passwords, browser profiles, WebView2 user-data folders, application logs containing private paths, or other secrets. Test certificates and test update keys must not be compiled into public builds.

## Release status

The production update channel fails closed until public signing infrastructure and production trust anchors are provisioned. Unsigned development builds are not production releases.
