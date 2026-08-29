#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace SoftcurseVaultCleaner
{
    public sealed record VerifiedDuplicateSet(
        long FileSize,
        string Sha256,
        IReadOnlyList<string> Files);

    /// <summary>
    /// Verifies duplicates by content, not merely name or size. Enumeration failures
    /// are isolated to the inaccessible entry, while cancellation always propagates.
    /// </summary>
    public static class DuplicateFileVerifier
    {
        public static IReadOnlyList<VerifiedDuplicateSet> Find(
            string root,
            long minimumSize,
            Action<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(root);
            ArgumentOutOfRangeException.ThrowIfNegative(minimumSize);

            var bySize = new Dictionary<long, List<string>>();
            foreach (string file in EnumerateFiles(root, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    long size = new FileInfo(file).Length;
                    if (size < minimumSize) continue;
                    if (!bySize.TryGetValue(size, out List<string>? paths))
                    {
                        paths = new List<string>();
                        bySize.Add(size, paths);
                    }
                    paths.Add(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A changing or inaccessible file is not safe to classify as a duplicate.
                }
            }

            var candidates = bySize.Values.Where(paths => paths.Count > 1).ToArray();
            var verified = new List<VerifiedDuplicateSet>();
            for (int index = 0; index < candidates.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var byHash = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (string file in candidates[index])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        string hash = ComputeSha256(file, cancellationToken);
                        if (!byHash.TryGetValue(hash, out List<string>? paths))
                        {
                            paths = new List<string>();
                            byHash.Add(hash, paths);
                        }
                        paths.Add(file);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Exclude files that changed or became inaccessible during verification.
                    }
                }

                foreach ((string hash, List<string> files) in byHash)
                    if (files.Count > 1)
                        verified.Add(new VerifiedDuplicateSet(
                            new FileInfo(files[0]).Length, hash, files.ToArray()));

                progress?.Invoke(candidates.Length == 0
                    ? 100
                    : (int)Math.Round((index + 1) * 100d / candidates.Length));
            }

            return verified
                .OrderByDescending(group => group.FileSize * (group.Files.Count - 1L))
                .ToArray();
        }

        private static IEnumerable<string> EnumerateFiles(
            string root,
            CancellationToken cancellationToken)
        {
            var pending = new Queue<string>();
            pending.Enqueue(Path.GetFullPath(root));
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string directory = pending.Dequeue();

                string[] files;
                try { files = Directory.GetFiles(directory); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { files = Array.Empty<string>(); }
                foreach (string file in files) yield return file;

                string[] directories;
                try { directories = Directory.GetDirectories(directory); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { directories = Array.Empty<string>(); }
                foreach (string child in directories) pending.Enqueue(child);
            }
        }

        private static string ComputeSha256(string path, CancellationToken cancellationToken)
        {
            using var sha256 = SHA256.Create();
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.SequentialScan);
            byte[] buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            }
            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha256.Hash!);
        }
    }
}
