#nullable enable

using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SoftcurseVaultCleaner
{
    public sealed record UpdateManifest(
        int SchemaVersion,
        string Product,
        string Version,
        string DownloadUrl,
        string FileName,
        string Sha256,
        long SizeBytes,
        DateTimeOffset PublishedAt,
        string Changelog);

    public sealed record UpdateManifestResult(bool Succeeded, UpdateManifest? Manifest, string Error);

    public static class UpdateManifestVerifier
    {
        private const int MaxEnvelopeBytes = 256 * 1024;
        private const long MaxInstallerBytes = 512L * 1024 * 1024;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = false
        };

        private sealed record SignedEnvelope(string Payload, string Signature);

        public static int MaximumEnvelopeBytes => MaxEnvelopeBytes;
        public static long MaximumInstallerBytes => MaxInstallerBytes;

        public static UpdateManifestResult Verify(
            ReadOnlySpan<byte> envelopeBytes,
            string publicKeySpkiBase64,
            Version currentVersion)
        {
            if (string.IsNullOrWhiteSpace(publicKeySpkiBase64))
                return new(false, null, "The update channel is disabled until a production signing key is configured.");
            if (envelopeBytes.Length == 0 || envelopeBytes.Length > MaxEnvelopeBytes)
                return new(false, null, "The signed update envelope has an invalid size.");

            try
            {
                SignedEnvelope? envelope = JsonSerializer.Deserialize<SignedEnvelope>(envelopeBytes, JsonOptions);
                if (envelope is null || string.IsNullOrWhiteSpace(envelope.Payload) ||
                    string.IsNullOrWhiteSpace(envelope.Signature))
                    return new(false, null, "The signed update envelope is incomplete.");

                byte[] payload = Convert.FromBase64String(envelope.Payload);
                byte[] signature = Convert.FromBase64String(envelope.Signature);
                if (payload.Length == 0 || payload.Length > MaxEnvelopeBytes)
                    return new(false, null, "The signed update payload has an invalid size.");

                using RSA rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out int bytesRead);
                if (bytesRead == 0)
                    return new(false, null, "The update metadata public key is invalid.");
                if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    return new(false, null, "The update metadata signature is invalid.");

                UpdateManifest? manifest = JsonSerializer.Deserialize<UpdateManifest>(payload, JsonOptions);
                string? error = ValidateManifest(manifest, currentVersion);
                return error is null
                    ? new(true, manifest, string.Empty)
                    : new(false, null, error);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException)
            {
                return new(false, null, "The signed update metadata could not be validated.");
            }
        }

        public static bool IsAllowedPackageUri(Uri uri)
        {
            if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443)
                return false;

            if (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                return uri.AbsolutePath.StartsWith(
                    "/Beardicuss/SOFTCURSE-VAULT-ENGINE/releases/download/",
                    StringComparison.Ordinal);

            return string.Equals(uri.Host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(uri.Host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsAllowedManifestUri(Uri uri)
        {
            if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443)
                return false;
            if (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                return string.Equals(uri.AbsolutePath,
                    "/Beardicuss/SOFTCURSE-VAULT-ENGINE/releases/latest/download/update-envelope.json",
                    StringComparison.Ordinal);
            return string.Equals(uri.Host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(uri.Host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }

        private static string? ValidateManifest(UpdateManifest? manifest, Version currentVersion)
        {
            if (manifest is null || manifest.SchemaVersion != 1 ||
                !string.Equals(manifest.Product, "Softcurse Vault Cleaner", StringComparison.Ordinal))
                return "The update manifest schema or product identity is invalid.";
            if (!Version.TryParse(manifest.Version, out Version? version))
                return "The update version is invalid.";
            if (version < currentVersion)
                return "The signed update manifest attempts to roll back this application.";
            if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out Uri? uri) || !IsAllowedPackageUri(uri))
                return "The update package URL is not on the fixed release allowlist.";
            if (string.IsNullOrWhiteSpace(manifest.FileName) ||
                !string.Equals(Path.GetFileName(manifest.FileName), manifest.FileName, StringComparison.Ordinal) ||
                !manifest.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return "The update package filename is invalid.";
            if (manifest.Sha256.Length != 64 || !IsHex(manifest.Sha256))
                return "The update package SHA-256 value is invalid.";
            if (manifest.SizeBytes <= 0 || manifest.SizeBytes > MaxInstallerBytes)
                return "The update package size is invalid.";
            if (manifest.PublishedAt > DateTimeOffset.UtcNow.AddMinutes(10))
                return "The update publication timestamp is in the future.";
            return null;
        }

        private static bool IsHex(string value)
        {
            foreach (char character in value)
                if (!Uri.IsHexDigit(character)) return false;
            return true;
        }
    }

    public sealed class UpdateService
    {
        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly Uri ManifestUri = new(UpdateTrust.ManifestUrl);

        public sealed class UpdateInfo
        {
            public bool IsAvailable { get; init; }
            public string CurrentVersion { get; init; } = "0.0.0";
            public string NewVersion { get; init; } = "";
            public string Changelog { get; init; } = "";
            public string Error { get; init; } = "";
            internal UpdateManifest? Manifest { get; init; }
        }

        public sealed record DownloadResult(bool Succeeded, string InstallerPath, string Error);

        public static string GetCurrentVersion()
        {
            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }

        public static async Task<UpdateInfo> CheckForUpdateAsync(CancellationToken token = default)
        {
            string currentText = GetCurrentVersion();
            if (!UpdateTrust.IsConfigured)
                return new UpdateInfo
                {
                    CurrentVersion = currentText,
                    Error = "Secure updates are disabled until production signing trust is configured."
                };

            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, ManifestUri);
                using HttpResponseMessage response = await Http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                Uri? finalUri = response.RequestMessage?.RequestUri;
                if (finalUri is null || !UpdateManifestVerifier.IsAllowedManifestUri(finalUri))
                    throw new InvalidDataException("The update manifest redirected to an untrusted host.");
                if (response.Content.Headers.ContentLength is long length &&
                    length > UpdateManifestVerifier.MaximumEnvelopeBytes)
                    throw new InvalidDataException("The update envelope exceeds the size limit.");

                byte[] envelope = await ReadLimitedAsync(
                    await response.Content.ReadAsStreamAsync(token),
                    UpdateManifestVerifier.MaximumEnvelopeBytes, token);
                UpdateManifestResult verified = UpdateManifestVerifier.Verify(
                    envelope, UpdateTrust.MetadataPublicKeySpkiBase64, Version.Parse(currentText));
                if (!verified.Succeeded || verified.Manifest is null)
                    return new UpdateInfo { CurrentVersion = currentText, Error = verified.Error };

                Version availableVersion = Version.Parse(verified.Manifest.Version);
                return new UpdateInfo
                {
                    IsAvailable = availableVersion > Version.Parse(currentText),
                    CurrentVersion = currentText,
                    NewVersion = verified.Manifest.Version,
                    Changelog = verified.Manifest.Changelog,
                    Manifest = verified.Manifest
                };
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return new UpdateInfo { CurrentVersion = currentText, Error = "Update check timed out." };
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                return new UpdateInfo { CurrentVersion = currentText, Error = $"Secure update check failed: {ex.Message}" };
            }
        }

        public static async Task<DownloadResult> DownloadAndVerifyAsync(
            UpdateInfo update,
            IProgress<int>? progress = null,
            CancellationToken token = default)
        {
            if (!UpdateTrust.IsConfigured || update.Manifest is null)
                return new(false, string.Empty, "No verified update manifest is available.");

            UpdateManifest manifest = update.Manifest;
            Uri initialUri = new(manifest.DownloadUrl);
            if (!UpdateManifestVerifier.IsAllowedPackageUri(initialUri))
                return new(false, string.Empty, "The update package URL is not allowed.");

            string updateDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SoftcurseVaultCleaner", "Updates", manifest.Version);
            Directory.CreateDirectory(updateDirectory);
            string finalPath = Path.Combine(updateDirectory, manifest.FileName);
            string partialPath = finalPath + ".partial";

            try
            {
                if (File.Exists(partialPath)) File.Delete(partialPath);
                using HttpRequestMessage request = new(HttpMethod.Get, initialUri);
                using HttpResponseMessage response = await Http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                Uri? finalUri = response.RequestMessage?.RequestUri;
                if (finalUri is null || !UpdateManifestVerifier.IsAllowedPackageUri(finalUri))
                    throw new InvalidDataException("The update download redirected to an untrusted host.");
                if (response.Content.Headers.ContentLength is long length && length != manifest.SizeBytes)
                    throw new InvalidDataException("The update package size does not match signed metadata.");

                await using Stream input = await response.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(
                    partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                long total = 0;
                try
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) > 0)
                    {
                        total += read;
                        if (total > manifest.SizeBytes || total > UpdateManifestVerifier.MaximumInstallerBytes)
                            throw new InvalidDataException("The update package exceeded its signed size.");
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                        hash.AppendData(buffer, 0, read);
                        progress?.Report((int)Math.Min(100, total * 100 / manifest.SizeBytes));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }

                await output.FlushAsync(token);
                if (total != manifest.SizeBytes)
                    throw new InvalidDataException("The downloaded package is incomplete.");
                byte[] expectedHash = Convert.FromHexString(manifest.Sha256);
                byte[] actualHash = hash.GetHashAndReset();
                if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                    throw new InvalidDataException("The update package SHA-256 hash does not match signed metadata.");
                if (!AuthenticodeVerifier.IsTrustedSignedFile(
                    partialPath, UpdateTrust.InstallerSignerCertificateSha256, out string signatureError))
                    throw new InvalidDataException(signatureError);

                File.Move(partialPath, finalPath, overwrite: true);
                return new(true, finalPath, string.Empty);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or CryptographicException)
            {
                try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { }
                return new(false, string.Empty, $"Update download rejected: {ex.Message}");
            }
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
                CheckCertificateRevocationList = true
            })
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SoftcurseVaultCleaner/3");
            return client;
        }

        private static async Task<byte[]> ReadLimitedAsync(Stream stream, int limit, CancellationToken token)
        {
            using var output = new MemoryStream();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) > 0)
                {
                    if (output.Length + read > limit)
                        throw new InvalidDataException("The update response exceeded the size limit.");
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }
    }

    internal static class AuthenticodeVerifier
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static bool IsTrustedSignedFile(string path, string expectedSignerSha256, out string error)
        {
            error = string.Empty;
            if (expectedSignerSha256.Length != 64)
            {
                error = "The production Authenticode signer is not configured.";
                return false;
            }

            IntPtr fileInfoPointer = IntPtr.Zero;
            IntPtr trustDataPointer = IntPtr.Zero;
            try
            {
                var fileInfo = new WinTrustFileInfo(path);
                fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
                var trustData = new WinTrustData(fileInfoPointer);
                trustDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
                Marshal.StructureToPtr(trustData, trustDataPointer, false);

                int status = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, trustDataPointer);
                if (status != 0)
                {
                    error = $"Authenticode verification failed with status 0x{status:X8}.";
                    return false;
                }

#pragma warning disable SYSLIB0057 // WinVerifyTrust validates the PE first; this API only extracts its signer certificate for pinning.
                using X509Certificate certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
                using var certificate2 = new X509Certificate2(certificate);
                string actual = certificate2.GetCertHashString(HashAlgorithmName.SHA256);
                if (!string.Equals(actual, expectedSignerSha256, StringComparison.OrdinalIgnoreCase))
                {
                    error = "The Authenticode signer does not match the pinned production certificate.";
                    return false;
                }
                return true;
            }
            catch (CryptographicException)
            {
                error = "The update package is not Authenticode signed.";
                return false;
            }
            finally
            {
                if (trustDataPointer != IntPtr.Zero)
                {
                    var closeData = Marshal.PtrToStructure<WinTrustData>(trustDataPointer);
                    closeData.StateAction = 2;
                    Marshal.StructureToPtr(closeData, trustDataPointer, true);
                    int closeResult = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, trustDataPointer);
                    GC.KeepAlive(closeResult);
                    Marshal.FreeHGlobal(trustDataPointer);
                }
                if (fileInfoPointer != IntPtr.Zero)
                    Marshal.FreeHGlobal(fileInfoPointer);
            }
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint StructSize;
            [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;

            public WinTrustFileInfo(string path)
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
                FilePath = path;
                FileHandle = IntPtr.Zero;
                KnownSubject = IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public uint StructSize;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;

            public WinTrustData(IntPtr fileInfo)
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>();
                PolicyCallbackData = IntPtr.Zero;
                SipClientData = IntPtr.Zero;
                UiChoice = 2;
                RevocationChecks = 1;
                UnionChoice = 1;
                FileInfo = fileInfo;
                StateAction = 1;
                StateData = IntPtr.Zero;
                UrlReference = IntPtr.Zero;
                ProviderFlags = 0x40;
                UiContext = 0;
            }
        }
    }
}
