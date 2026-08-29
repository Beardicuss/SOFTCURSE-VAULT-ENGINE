[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$TimestampUrl = 'https://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectFile = Join-Path $projectRoot 'Win11 Auto-Clean.csproj'
$helperProject = Join-Path $projectRoot 'PrivilegedMaintenanceHelper\PrivilegedMaintenanceHelper.csproj'
$installerScript = Join-Path $projectRoot 'installer\SoftcurseVaultCleaner.iss'
$artifactsRoot = Join-Path $projectRoot 'artifacts'
$stagingRoot = Join-Path $artifactsRoot ("release-staging-" + [Guid]::NewGuid().ToString('N'))
$appStage = Join-Path $stagingRoot 'app'
$helperStage = Join-Path $stagingRoot 'helper'
$installerStage = Join-Path $stagingRoot 'installer'
$cert = $null
$rsa = $null

function Assert-UnderArtifacts([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetFullPath($artifactsRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release path escaped the artifacts directory: $full"
    }
}

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $candidate = Get-ChildItem $kits -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $candidate) { throw 'SignTool was not found. Install the Windows SDK.' }
    return $candidate.FullName
}

function Invoke-Checked([string]$FileName, [string[]]$Arguments) {
    & $FileName @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FileName failed with exit code $LASTEXITCODE."
    }
}

Assert-UnderArtifacts $stagingRoot
if (Test-Path $stagingRoot) { throw "Fresh staging directory already exists: $stagingRoot" }
New-Item -ItemType Directory -Path $appStage, $helperStage, $installerStage -Force | Out-Null

try {
    $gitStatus = git -C $projectRoot status --porcelain
    if ($gitStatus) { throw 'Release builds require a clean Git worktree.' }
    $tag = git -C $projectRoot describe --tags --exact-match HEAD 2>$null
    if (-not $tag) { throw 'Release builds must run from an exact Git tag.' }

    $pfxPath = $env:SOFTCURSE_SIGNING_PFX
    $pfxPassword = $env:SOFTCURSE_SIGNING_PASSWORD
    if ([string]::IsNullOrWhiteSpace($pfxPath) -or -not (Test-Path -LiteralPath $pfxPath)) {
        throw 'SOFTCURSE_SIGNING_PFX must point to the production code-signing PFX.'
    }
    if ([string]::IsNullOrWhiteSpace($pfxPassword)) {
        throw 'SOFTCURSE_SIGNING_PASSWORD is required.'
    }

    $props = [xml](Get-Content -Raw (Join-Path $projectRoot 'Directory.Build.props'))
    $version = [string]$props.Project.PropertyGroup.VersionPrefix
    if ($tag -ne "v$version") { throw "Tag '$tag' does not match product version v$version." }

    $trustSource = Get-Content -Raw (Join-Path $projectRoot 'UpdateTrust.cs')
    $publicKeyMatch = [regex]::Match($trustSource, 'MetadataPublicKeySpkiBase64\s*=\s*"([^"]*)"')
    $thumbprintMatch = [regex]::Match($trustSource, 'InstallerSignerCertificateSha256\s*=\s*"([A-Fa-f0-9]*)"')
    if (-not $publicKeyMatch.Success -or [string]::IsNullOrWhiteSpace($publicKeyMatch.Groups[1].Value) -or
        -not $thumbprintMatch.Success -or $thumbprintMatch.Groups[1].Value.Length -ne 64) {
        throw 'UpdateTrust.cs must contain production metadata and Authenticode public trust anchors.'
    }

    $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $pfxPath, $pfxPassword,
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    $certThumbprint = $cert.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256)
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
    if (-not $rsa) { throw 'The code-signing certificate does not contain an RSA private key.' }
    $publicKey = [Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo())
    if ($publicKey -ne $publicKeyMatch.Groups[1].Value -or
        $certThumbprint -ne $thumbprintMatch.Groups[1].Value.ToUpperInvariant()) {
        throw 'The signing PFX does not match the trust anchors compiled into the application.'
    }

    Invoke-Checked dotnet @('restore', $projectFile, '--locked-mode', '-r', $RuntimeIdentifier)
    Invoke-Checked dotnet @('restore', $helperProject, '--locked-mode', '-r', $RuntimeIdentifier)
    Invoke-Checked dotnet @('publish', $projectFile, '-c', $Configuration, '-r', $RuntimeIdentifier,
        '--self-contained', 'true', '--no-restore', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $appStage)
    Invoke-Checked dotnet @('publish', $helperProject, '-c', $Configuration, '-r', $RuntimeIdentifier,
        '--self-contained', 'true', '--no-restore', '-p:PublishSingleFile=true', '-p:PublishTrimmed=true',
        '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $helperStage)

    $helperName = 'Softcurse.PrivilegedMaintenanceHelper'
    foreach ($sidecar in @("$helperName.dll", "$helperName.deps.json", "$helperName.runtimeconfig.json")) {
        $sidecarPath = Join-Path $appStage $sidecar
        if (Test-Path $sidecarPath) { Remove-Item -LiteralPath $sidecarPath -Force }
    }
    Copy-Item -LiteralPath (Join-Path $helperStage "$helperName.exe") -Destination $appStage -Force

    $forbiddenNames = @('History', 'Login Data', 'Cookies', 'Web Data', 'Preferences', 'Local State')
    $forbidden = Get-ChildItem $appStage -Recurse -Force | Where-Object {
        $_.Extension -in @('.pdb', '.user') -or $_.Name -in $forbiddenNames -or
        $_.FullName -match '(?i)WebView2[\\/]EBWebView'
    }
    if ($forbidden) {
        throw "Forbidden release content detected:`n$($forbidden.FullName -join "`n")"
    }

    $signTool = Find-SignTool
    $signedFiles = @(
        (Join-Path $appStage 'Win11 Auto-Clean.exe'),
        (Join-Path $appStage 'Win11 Auto-Clean.dll'),
        (Join-Path $appStage "$helperName.exe")
    )
    foreach ($file in $signedFiles) {
        Invoke-Checked $signTool @('sign', '/f', $pfxPath, '/p', $pfxPassword, '/fd', 'SHA256',
            '/tr', $TimestampUrl, '/td', 'SHA256', $file)
        Invoke-Checked $signTool @('verify', '/pa', '/all', '/tw', $file)
    }

    Invoke-Checked dotnet @('tool', 'restore')
    Invoke-Checked dotnet @('tool', 'run', 'sbom-tool', 'generate', '-b', $appStage, '-bc', $projectRoot,
        '-pn', 'Softcurse Vault Cleaner', '-pv', $version, '-ps', 'Softcurse',
        '-nsb', 'https://github.com/Beardicuss/SOFTCURSE-VAULT-ENGINE')
    $sbomPath = Join-Path $appStage '_manifest\spdx_2.2\manifest.spdx.json'
    if (-not (Test-Path -LiteralPath $sbomPath)) { throw 'The release SBOM was not generated.' }
    Copy-Item -LiteralPath $sbomPath -Destination (
        Join-Path $installerStage "SoftcurseVaultCleaner_v$version.spdx.json")

    $iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if (-not $iscc) {
        $defaultIscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
        if (-not (Test-Path $defaultIscc)) { throw 'Inno Setup 6 compiler was not found.' }
        $isccPath = $defaultIscc
    } else { $isccPath = $iscc.Source }
    Invoke-Checked $isccPath @("/DPublishSource=$appStage", "/DReleaseOutputDir=$installerStage", $installerScript)

    $installers = @(Get-ChildItem $installerStage -Filter '*.exe')
    if ($installers.Count -ne 1) { throw "Expected one installer, found $($installers.Count)." }
    $installer = $installers[0]
    Invoke-Checked $signTool @('sign', '/f', $pfxPath, '/p', $pfxPassword, '/fd', 'SHA256',
        '/tr', $TimestampUrl, '/td', 'SHA256', $installer.FullName)
    Invoke-Checked $signTool @('verify', '/pa', '/all', '/tw', $installer.FullName)

    $installerHash = (Get-FileHash $installer.FullName -Algorithm SHA256).Hash
    $manifest = [ordered]@{
        SchemaVersion = 1
        Product = 'Softcurse Vault Cleaner'
        Version = $version
        DownloadUrl = "https://github.com/Beardicuss/SOFTCURSE-VAULT-ENGINE/releases/download/v$version/$($installer.Name)"
        FileName = $installer.Name
        Sha256 = $installerHash
        SizeBytes = $installer.Length
        PublishedAt = [DateTimeOffset]::UtcNow
        Changelog = "See the v$version release notes."
    }
    $payloadBytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Compress))
    $signature = $rsa.SignData($payloadBytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $envelope = [ordered]@{
        Payload = [Convert]::ToBase64String($payloadBytes)
        Signature = [Convert]::ToBase64String($signature)
    }
    $envelope | ConvertTo-Json | Set-Content -Encoding utf8NoBOM (Join-Path $installerStage 'update-envelope.json')

    $zipPath = Join-Path $installerStage "SoftcurseVaultCleaner_Portable_v$version.zip"
    Compress-Archive -Path (Join-Path $appStage '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Get-ChildItem $installerStage -File | ForEach-Object {
        "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name
    } | Set-Content -Encoding ascii (Join-Path $installerStage 'SHA256SUMS.txt')

    $finalRoot = Join-Path $artifactsRoot "v$version"
    Assert-UnderArtifacts $finalRoot
    if (Test-Path $finalRoot) { throw "Release output already exists: $finalRoot" }
    Move-Item -LiteralPath $installerStage -Destination $finalRoot
    Write-Host "Verified release created at $finalRoot"
}
finally {
    if ($null -ne $rsa) { $rsa.Dispose() }
    if ($null -ne $cert) { $cert.Dispose() }
    if (Test-Path $stagingRoot) {
        Assert-UnderArtifacts $stagingRoot
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
