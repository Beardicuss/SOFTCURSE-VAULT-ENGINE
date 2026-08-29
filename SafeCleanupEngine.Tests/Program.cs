using SoftcurseVaultCleaner;
using System.Security.Cryptography;
using System.Text.Json;

var engine = new SafeCleanupEngine();
var failures = new List<string>();

void Assert(bool condition, string testName, string? detail = null)
{
    if (condition)
    {
        Console.WriteLine($"PASS {testName}");
        return;
    }

    failures.Add(detail == null ? testName : $"{testName}: {detail}");
    Console.Error.WriteLine($"FAIL {testName}{(detail == null ? string.Empty : $": {detail}")}");
}

CleanupTarget DirectoryTarget(string id, string path, CleanupTargetOrigin origin) =>
    new(id, id, path, "Safety policy test", CleanupTargetType.DirectoryContents, origin);

foreach (DriveInfo drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
{
    var preview = engine.Preview(DirectoryTarget("volume-root", drive.RootDirectory.FullName,
        CleanupTargetOrigin.UserSelected));
    Assert(!preview.IsAllowed, $"blocks volume root {drive.Name}", preview.ValidationMessage);
}

string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var profilePreview = engine.Preview(DirectoryTarget("user-profile", userProfile,
    CleanupTargetOrigin.UserSelected));
Assert(!profilePreview.IsAllowed, "blocks exact user profile", profilePreview.ValidationMessage);

string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
var windowsPreview = engine.Preview(DirectoryTarget("windows", windows,
    CleanupTargetOrigin.UserSelected));
Assert(!windowsPreview.IsAllowed, "blocks exact Windows directory", windowsPreview.ValidationMessage);

string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
var documentsPreview = engine.Preview(DirectoryTarget("documents", documents,
    CleanupTargetOrigin.UserSelected));
Assert(!documentsPreview.IsAllowed, "blocks user Documents directory", documentsPreview.ValidationMessage);

string temp = Path.GetTempPath();
var tempPreview = engine.Preview(DirectoryTarget("temp", temp,
    CleanupTargetOrigin.UserSelected));
Assert(tempPreview.IsAllowed, "allows approved user temporary root", tempPreview.ValidationMessage);

var privilegedPreview = engine.Preview(new CleanupTarget(
    "privileged-temp", "privileged-temp", temp, "Safety policy test",
    CleanupTargetType.DirectoryContents, CleanupTargetOrigin.BuiltIn,
    RequiredPrivilege: CleanupPrivilege.Administrator));
Assert(!privilegedPreview.IsAllowed, "blocks administrator targets in standard-user engine",
    privilegedPreview.ValidationMessage);

var invalidPreview = engine.Preview(DirectoryTarget("invalid", "\0invalid",
    CleanupTargetOrigin.UserSelected));
Assert(!invalidPreview.IsAllowed, "blocks invalid path", invalidPreview.ValidationMessage);

var missingFile = engine.Preview(new CleanupTarget(
    "missing", "missing", Path.Combine(temp, Guid.NewGuid().ToString("N")),
    "Safety policy test", CleanupTargetType.File, CleanupTargetOrigin.UserSelected));
Assert(!missingFile.IsAllowed, "blocks target that no longer exists", missingFile.ValidationMessage);

using RSA updateSigningKey = RSA.Create(3072);
string updatePublicKey = Convert.ToBase64String(updateSigningKey.ExportSubjectPublicKeyInfo());
var validManifest = new UpdateManifest(
    1,
    "Softcurse Vault Cleaner",
    "3.1.0",
    "https://github.com/Beardicuss/SOFTCURSE-VAULT-ENGINE/releases/download/v3.1.0/SoftcurseVaultCleaner_Setup_v3.1.0.exe",
    "SoftcurseVaultCleaner_Setup_v3.1.0.exe",
    new string('A', 64),
    1024,
    DateTimeOffset.UtcNow,
    "Security update");
byte[] validPayload = JsonSerializer.SerializeToUtf8Bytes(validManifest);
byte[] validSignature = updateSigningKey.SignData(
    validPayload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
byte[] validEnvelope = JsonSerializer.SerializeToUtf8Bytes(new
{
    Payload = Convert.ToBase64String(validPayload),
    Signature = Convert.ToBase64String(validSignature)
});

UpdateManifestResult validUpdate = UpdateManifestVerifier.Verify(
    validEnvelope, updatePublicKey, new Version(3, 0, 0));
Assert(validUpdate.Succeeded, "accepts valid signed update metadata", validUpdate.Error);

var currentManifest = validManifest with { Version = "3.0.0" };
byte[] currentPayload = JsonSerializer.SerializeToUtf8Bytes(currentManifest);
byte[] currentEnvelope = JsonSerializer.SerializeToUtf8Bytes(new
{
    Payload = Convert.ToBase64String(currentPayload),
    Signature = Convert.ToBase64String(updateSigningKey.SignData(
        currentPayload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
});
UpdateManifestResult currentUpdate = UpdateManifestVerifier.Verify(
    currentEnvelope, updatePublicKey, new Version(3, 0, 0));
Assert(currentUpdate.Succeeded, "accepts signed metadata for the current version", currentUpdate.Error);

var rollbackManifest = validManifest with { Version = "2.9.0" };
byte[] rollbackPayload = JsonSerializer.SerializeToUtf8Bytes(rollbackManifest);
byte[] rollbackEnvelope = JsonSerializer.SerializeToUtf8Bytes(new
{
    Payload = Convert.ToBase64String(rollbackPayload),
    Signature = Convert.ToBase64String(updateSigningKey.SignData(
        rollbackPayload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
});
UpdateManifestResult rollbackUpdate = UpdateManifestVerifier.Verify(
    rollbackEnvelope, updatePublicKey, new Version(3, 0, 0));
Assert(!rollbackUpdate.Succeeded, "rejects a signed rollback manifest", rollbackUpdate.Error);

validEnvelope[^2] ^= 1;
UpdateManifestResult tamperedUpdate = UpdateManifestVerifier.Verify(
    validEnvelope, updatePublicKey, new Version(3, 0, 0));
Assert(!tamperedUpdate.Succeeded, "rejects tampered update envelope", tamperedUpdate.Error);

UpdateManifestResult disabledUpdate = UpdateManifestVerifier.Verify(
    Array.Empty<byte>(), string.Empty, new Version(3, 0, 0));
Assert(!disabledUpdate.Succeeded, "fails closed without production update key", disabledUpdate.Error);

Assert(!UpdateManifestVerifier.IsAllowedPackageUri(new Uri("http://github.com/Beardicuss/SOFTCURSE-VAULT-ENGINE/releases/download/v3.1.0/a.exe")),
    "rejects non-HTTPS update URL");
Assert(!UpdateManifestVerifier.IsAllowedPackageUri(new Uri("https://example.com/update.exe")),
    "rejects non-allowlisted update host");
Assert(UpdateManifestVerifier.IsAllowedPackageUri(new Uri(validManifest.DownloadUrl)),
    "allows fixed GitHub release path");
Assert(UpdateManifestVerifier.IsAllowedManifestUri(new Uri(
    "https://github.com/Beardicuss/SOFTCURSE-VAULT-ENGINE/releases/latest/download/update-envelope.json")),
    "allows fixed update manifest endpoint");
Assert(!UpdateManifestVerifier.IsAllowedManifestUri(new Uri(
    "https://github.com/other/repository/releases/latest/download/update-envelope.json")),
    "rejects lookalike update manifest endpoint");

bool unsignedAccepted = AuthenticodeVerifier.IsTrustedSignedFile(
    Environment.ProcessPath!, new string('A', 64), out string authenticodeError);
Assert(!unsignedAccepted, "rejects unsigned update package", authenticodeError);

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} safety policy test(s) failed.");
    return 1;
}

Console.WriteLine("All cleanup safety policy tests passed.");
return 0;
