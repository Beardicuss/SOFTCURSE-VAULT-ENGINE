#nullable enable

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

Assert(!engine.Preview((CleanupTarget)null!).IsAllowed, "blocks null cleanup target");
Assert(!engine.Preview(DirectoryTarget("empty", "  ", CleanupTargetOrigin.UserSelected)).IsAllowed,
    "blocks empty cleanup path");

string fixtureRoot = Path.Combine(temp, "softcurse-safety-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureRoot);
string canonicalDirectory = Path.Combine(fixtureRoot, "canonical");
string nestedDirectory = Path.Combine(canonicalDirectory, "nested");
Directory.CreateDirectory(nestedDirectory);
File.WriteAllBytes(Path.Combine(canonicalDirectory, "one.bin"), new byte[17]);
File.WriteAllBytes(Path.Combine(nestedDirectory, "two.bin"), new byte[31]);

var isolatedEngine = new SafeCleanupEngine(
    Array.Empty<string>(), fixtureRoot, _ => false,
    path => new FileInfo(path).Length,
    (path, _) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .Sum(file => new FileInfo(file).Length));
var normalizedPreview = isolatedEngine.Preview(DirectoryTarget(
    "normalized", Path.Combine(fixtureRoot, "canonical", "..", "canonical"),
    CleanupTargetOrigin.UserSelected));
Assert(normalizedPreview.IsAllowed, "allows normalized approved temporary path",
    normalizedPreview.ValidationMessage);
Assert(string.Equals(normalizedPreview.CanonicalPath, canonicalDirectory,
        StringComparison.OrdinalIgnoreCase),
    "canonicalizes dot segments");
Assert(normalizedPreview.EstimatedBytes == 48, "estimates nested directory bytes exactly",
    normalizedPreview.EstimatedBytes.ToString());

Assert(SafeCleanupEngine.IsPathDescendant(@"D:\Windows\Temp", @"D:\Windows"),
    "recognizes descendant on non-C system layout");
Assert(!SafeCleanupEngine.IsPathDescendant(@"D:\Windows.old", @"D:\Windows"),
    "rejects non-C protected-path lookalike");
Assert(!SafeCleanupEngine.IsPathDescendant(@"E:\Windows\Temp", @"D:\Windows"),
    "does not cross volume boundaries");

var syntheticLayoutEngine = new SafeCleanupEngine(
    new[] { @"D:\Windows", @"D:\Program Files", @"E:\Profiles\Alice" },
    fixtureRoot, _ => false);
Assert(!syntheticLayoutEngine.Preview(DirectoryTarget(
        "contains-protected", @"D:\", CleanupTargetOrigin.UserSelected)).IsAllowed,
    "blocks a non-C volume root before existence checks");

string linkedTarget = Path.Combine(fixtureRoot, "linked-target");
Directory.CreateDirectory(linkedTarget);
var linkedEngine = new SafeCleanupEngine(
    Array.Empty<string>(), fixtureRoot,
    path => string.Equals(path, linkedTarget, StringComparison.OrdinalIgnoreCase));
Assert(!linkedEngine.Preview(DirectoryTarget(
        "synthetic-link", linkedTarget, CleanupTargetOrigin.BuiltIn)).IsAllowed,
    "blocks target reported as a junction or symbolic link");

string descendantLink = Path.Combine(canonicalDirectory, "synthetic-link-child");
Directory.CreateDirectory(descendantLink);
var descendantLinkEngine = new SafeCleanupEngine(
    Array.Empty<string>(), fixtureRoot,
    path => string.Equals(path, descendantLink, StringComparison.OrdinalIgnoreCase));
Assert(!descendantLinkEngine.Preview(DirectoryTarget(
        "descendant-link", canonicalDirectory, CleanupTargetOrigin.BuiltIn)).IsAllowed,
    "blocks directory containing a junction or symbolic link");

var allCategoriesConfig = new CleanupConfig
{
    CleanTempFiles = true,
    CleanCache = true,
    CleanLogs = true,
    CleanRecycleBin = true,
    CleanPrefetch = true,
    DeepScanMode = true,
    UseRecycleBin = false,
    CleanDevTools = true,
    CleanGaming = true,
    CleanSystemDumps = true,
    CleanDNS = true,
    CleanExtreme = true,
    CustomPaths = new List<string> { canonicalDirectory }
};
CleanupPlan completePlan = new CleanerService().CreateCleanupPlan(allCategoriesConfig);
Assert(completePlan.Targets.Count > 0, "builds a plan when every cleanup category is selected");
Assert(completePlan.Targets.Select(target => target.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
       completePlan.Targets.Count, "assigns a unique identity to every discovered cleanup target");
Assert(completePlan.Targets.All(target => target.DeletionMode == CleanupDeletionMode.RecycleBin),
    "forces every filesystem cleanup target to recoverable deletion mode");
Assert(completePlan.Targets.All(target => !string.IsNullOrWhiteSpace(target.Reason) &&
                                          !string.IsNullOrWhiteSpace(target.Category)),
    "gives every cleanup target a reason and category");
Assert(completePlan.Targets.All(target => !target.Path.EndsWith(
        Path.Combine("Android", "Sdk", "system-images"), StringComparison.OrdinalIgnoreCase)),
    "never treats installed Android emulator images as cache");
Assert(completePlan.Targets.All(target => !target.Path.EndsWith(
        Path.Combine("AppData", "Local", "UnrealEngine"), StringComparison.OrdinalIgnoreCase)),
    "never targets the broad Unreal Engine data root");
Assert(completePlan.Targets.All(target => !target.Path.EndsWith(
        Path.Combine("Spotify", "Data"), StringComparison.OrdinalIgnoreCase)),
    "never removes Spotify offline/application data as cache");
Assert(completePlan.Targets.Any(target =>
        target.Origin == CleanupTargetOrigin.UserSelected &&
        string.Equals(Path.GetFullPath(target.Path), canonicalDirectory,
            StringComparison.OrdinalIgnoreCase)),
    "includes an existing custom path as an explicitly user-selected target");

CleanupPlan emptyPlan = new CleanerService().CreateCleanupPlan(new CleanupConfig());
Assert(emptyPlan.Targets.Count == 0,
    "creates no filesystem targets when every cleanup category is disabled");

CleanupPlan duplicateCustomPlan = new CleanerService().CreateCleanupPlan(new CleanupConfig
{
    CustomPaths = new List<string>
    {
        canonicalDirectory,
        canonicalDirectory + Path.DirectorySeparatorChar,
        Path.Combine(canonicalDirectory, ".")
    }
});
Assert(duplicateCustomPlan.Targets.Count == 1,
    "deduplicates equivalent custom paths after canonicalization",
    $"Found {duplicateCustomPlan.Targets.Count} targets.");

bool nullConfigRejected = false;
try
{
    _ = new CleanerService().CreateCleanupPlan(null!);
}
catch (ArgumentNullException)
{
    nullConfigRejected = true;
}
Assert(nullConfigRejected, "rejects a null cleanup configuration");

using var preCancelled = new CancellationTokenSource();
preCancelled.Cancel();
CleanupExecutionResult preCancelledResult = await isolatedEngine.ExecuteAsync(
    new[] { DirectoryTarget("cancelled", canonicalDirectory, CleanupTargetOrigin.BuiltIn) },
    preCancelled.Token);
Assert(preCancelledResult.WasCancelled && preCancelledResult.Items.Count == 0,
    "returns explicit cancellation before execution");

string failureFile = Path.Combine(fixtureRoot, "failure.bin");
string successFile = Path.Combine(fixtureRoot, "success.bin");
File.WriteAllBytes(failureFile, new byte[13]);
File.WriteAllBytes(successFile, new byte[19]);
var failureEngine = new SafeCleanupEngine(
    Array.Empty<string>(), fixtureRoot, _ => false,
    path => string.Equals(path, failureFile, StringComparison.OrdinalIgnoreCase)
        ? throw new IOException("Synthetic deletion failure.")
        : new FileInfo(path).Length);
CleanupExecutionResult partialResult = await failureEngine.ExecuteAsync(new[]
{
    new CleanupTarget("failure", "failure", failureFile, "test", CleanupTargetType.File,
        CleanupTargetOrigin.BuiltIn),
    new CleanupTarget("success", "success", successFile, "test", CleanupTargetType.File,
        CleanupTargetOrigin.BuiltIn)
});
Assert(partialResult.FailedCount == 1 && partialResult.SucceededCount == 1,
    "propagates one failure and continues independent targets");
Assert(partialResult.Items[0].Message.Contains("Synthetic deletion failure", StringComparison.Ordinal),
    "preserves actionable deletion failure message");

using var midCancellation = new CancellationTokenSource();
int deletionCalls = 0;
var cancellationEngine = new SafeCleanupEngine(
    Array.Empty<string>(), fixtureRoot, _ => false,
    path =>
    {
        deletionCalls++;
        midCancellation.Cancel();
        return new FileInfo(path).Length;
    });
CleanupExecutionResult midCancelledResult = await cancellationEngine.ExecuteAsync(new[]
{
    new CleanupTarget("first", "first", failureFile, "test", CleanupTargetType.File,
        CleanupTargetOrigin.BuiltIn),
    new CleanupTarget("second", "second", successFile, "test", CleanupTargetType.File,
        CleanupTargetOrigin.BuiltIn)
}, midCancellation.Token);
Assert(midCancelledResult.WasCancelled && deletionCalls == 1 && midCancelledResult.Items.Count == 1,
    "stops between targets when cancellation is requested");

int deniedDeletionCalls = 0;
var denyBackendEngine = new SafeCleanupEngine(
    new[] { canonicalDirectory }, fixtureRoot, _ => false,
    path => { deniedDeletionCalls++; return 0; },
    (path, token) => { deniedDeletionCalls++; return 0; });
CleanupExecutionResult deniedResult = await denyBackendEngine.ExecuteAsync(new[]
{
    DirectoryTarget("denied", canonicalDirectory, CleanupTargetOrigin.BuiltIn)
});
Assert(deniedResult.SkippedCount == 1 && deniedDeletionCalls == 0,
    "never invokes deletion backend for denied target");

string duplicateRoot = Path.Combine(fixtureRoot, "duplicates");
Directory.CreateDirectory(duplicateRoot);
byte[] duplicateBytes = Enumerable.Repeat((byte)0x5A, 16 * 1024).ToArray();
File.WriteAllBytes(Path.Combine(duplicateRoot, "a.bin"), duplicateBytes);
File.WriteAllBytes(Path.Combine(duplicateRoot, "b.bin"), duplicateBytes);
File.WriteAllBytes(Path.Combine(duplicateRoot, "same-size-different.bin"),
    Enumerable.Repeat((byte)0xA5, duplicateBytes.Length).ToArray());
IReadOnlyList<VerifiedDuplicateSet> duplicateSets = DuplicateFileVerifier.Find(
    duplicateRoot, 10 * 1024);
Assert(duplicateSets.Count == 1 && duplicateSets[0].Files.Count == 2,
    "verifies duplicates by SHA-256 content instead of size alone");
Assert(duplicateSets[0].Sha256.Length == 64,
    "records full SHA-256 duplicate fingerprint");
using var duplicateCancellation = new CancellationTokenSource();
duplicateCancellation.Cancel();
bool duplicateCancelled = false;
try
{
    DuplicateFileVerifier.Find(duplicateRoot, 0, cancellationToken: duplicateCancellation.Token);
}
catch (OperationCanceledException)
{
    duplicateCancelled = true;
}
Assert(duplicateCancelled, "propagates duplicate-scan cancellation");

using RSA updateSigningKey = RSA.Create(3072);
string updatePublicKey = Convert.ToBase64String(updateSigningKey.ExportSubjectPublicKeyInfo());
byte[] SignManifest(UpdateManifest manifest)
{
    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest);
    byte[] signature = updateSigningKey.SignData(
        payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    return JsonSerializer.SerializeToUtf8Bytes(new
    {
        Payload = Convert.ToBase64String(payload),
        Signature = Convert.ToBase64String(signature)
    });
}

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
byte[] validEnvelope = SignManifest(validManifest);

UpdateManifestResult validUpdate = UpdateManifestVerifier.Verify(
    validEnvelope, updatePublicKey, new Version(3, 0, 0));
Assert(validUpdate.Succeeded, "accepts valid signed update metadata", validUpdate.Error);

var currentManifest = validManifest with { Version = "1.0.0" };
byte[] currentEnvelope = SignManifest(currentManifest);
UpdateManifestResult currentUpdate = UpdateManifestVerifier.Verify(
    currentEnvelope, updatePublicKey, new Version(3, 0, 0));
Assert(currentUpdate.Succeeded, "accepts signed metadata for the current version", currentUpdate.Error);

var rollbackManifest = validManifest with { Version = "2.9.0" };
byte[] rollbackEnvelope = SignManifest(rollbackManifest);
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

foreach ((string name, UpdateManifest invalidManifest) in new[]
{
    ("wrong schema", validManifest with { SchemaVersion = 2 }),
    ("wrong product", validManifest with { Product = "Lookalike Cleaner" }),
    ("non-executable filename", validManifest with { FileName = "update.zip" }),
    ("traversal filename", validManifest with { FileName = "..\\update.exe" }),
    ("invalid hash", validManifest with { Sha256 = "XYZ" }),
    ("zero package size", validManifest with { SizeBytes = 0 }),
    ("oversized package", validManifest with { SizeBytes = UpdateManifestVerifier.MaximumInstallerBytes + 1 }),
    ("future timestamp", validManifest with { PublishedAt = DateTimeOffset.UtcNow.AddHours(1) }),
    ("untrusted package host", validManifest with { DownloadUrl = "https://example.com/update.exe" })
})
{
    UpdateManifestResult invalidResult = UpdateManifestVerifier.Verify(
        SignManifest(invalidManifest), updatePublicKey, new Version(3, 0, 0));
    Assert(!invalidResult.Succeeded, $"rejects signed manifest with {name}", invalidResult.Error);
}

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

string fixtureFullPath = Path.GetFullPath(fixtureRoot);
string tempFullPath = Path.GetFullPath(temp).TrimEnd(Path.DirectorySeparatorChar) +
    Path.DirectorySeparatorChar;
if (!fixtureFullPath.StartsWith(tempFullPath, StringComparison.OrdinalIgnoreCase) ||
    !Path.GetFileName(fixtureFullPath).StartsWith("softcurse-safety-tests-", StringComparison.Ordinal))
    throw new InvalidOperationException("Refusing to remove an unexpected test fixture path.");
Directory.Delete(fixtureFullPath, recursive: true);

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} safety policy test(s) failed.");
    return 1;
}

Console.WriteLine("All cleanup safety policy tests passed.");
return 0;
