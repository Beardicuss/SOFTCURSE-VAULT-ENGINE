#nullable enable

using Microsoft.VisualBasic.FileIO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SoftcurseVaultCleaner
{
    public enum CleanupTargetOrigin
    {
        BuiltIn,
        UserSelected
    }

    public enum CleanupTargetType
    {
        File,
        DirectoryContents
    }

    public enum CleanupRisk
    {
        Low,
        Moderate,
        High
    }

    public enum CleanupPrivilege
    {
        StandardUser,
        Administrator
    }

    public enum CleanupDeletionMode
    {
        RecycleBin
    }

    public sealed record CleanupTarget(
        string Id,
        string DisplayName,
        string Path,
        string Reason,
        CleanupTargetType Type,
        CleanupTargetOrigin Origin,
        string Category = "General",
        CleanupRisk Risk = CleanupRisk.Moderate,
        CleanupPrivilege RequiredPrivilege = CleanupPrivilege.StandardUser,
        CleanupDeletionMode DeletionMode = CleanupDeletionMode.RecycleBin);

    public sealed record CleanupPlan(
        string Id,
        DateTimeOffset CreatedAt,
        IReadOnlyList<CleanupTarget> Targets)
    {
        public static CleanupPlan Create(string id, IEnumerable<CleanupTarget> targets) =>
            new(id, DateTimeOffset.UtcNow, targets.ToArray());
    }

    public sealed record CleanupPreviewItem(
        CleanupTarget Target,
        string CanonicalPath,
        bool IsAllowed,
        string ValidationMessage,
        long EstimatedBytes);

    public sealed record CleanupItemResult(
        CleanupTarget Target,
        string CanonicalPath,
        bool Succeeded,
        bool WasSkipped,
        long BytesFreed,
        string Message);

    public sealed class CleanupExecutionResult
    {
        public IReadOnlyList<CleanupItemResult> Items { get; init; } = Array.Empty<CleanupItemResult>();
        public long BytesFreed => Items.Sum(item => item.BytesFreed);
        public int SucceededCount => Items.Count(item => item.Succeeded);
        public int FailedCount => Items.Count(item => !item.Succeeded && !item.WasSkipped);
        public int SkippedCount => Items.Count(item => item.WasSkipped);
        public bool WasCancelled { get; init; }
    }

    /// <summary>
    /// Central safety boundary for filesystem cleanup. Phase 1 intentionally permits
    /// recoverable deletion only; permanent deletion requires a future expert workflow.
    /// </summary>
    public sealed class SafeCleanupEngine
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        public CleanupPreviewItem Preview(CleanupTarget target)
        {
            var validation = Validate(target);
            long estimatedBytes = validation.IsAllowed
                ? EstimateBytes(validation.CanonicalPath, target.Type)
                : 0;

            return new CleanupPreviewItem(
                target,
                validation.CanonicalPath,
                validation.IsAllowed,
                validation.Message,
                estimatedBytes);
        }

        public IReadOnlyList<CleanupPreviewItem> Preview(IEnumerable<CleanupTarget> targets) =>
            targets.Select(Preview).ToArray();

        public IReadOnlyList<CleanupPreviewItem> Preview(CleanupPlan plan) =>
            Preview(plan.Targets);

        public Task<CleanupExecutionResult> ExecuteAsync(
            IEnumerable<CleanupTarget> targets,
            CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(CleanupPlan.Create("ad-hoc", targets), cancellationToken);
        }

        public Task<CleanupExecutionResult> ExecuteAsync(
            CleanupPlan plan,
            CancellationToken cancellationToken = default) =>
            Task.Run(() => Execute(plan.Targets, cancellationToken), cancellationToken);

        private CleanupExecutionResult Execute(
            IReadOnlyList<CleanupTarget> targets,
            CancellationToken cancellationToken)
        {
            var results = new List<CleanupItemResult>(targets.Count);
            bool cancelled = false;

            foreach (var target in targets)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                var validation = Validate(target);
                if (!validation.IsAllowed)
                {
                    results.Add(new CleanupItemResult(
                        target, validation.CanonicalPath, false, true, 0,
                        validation.Message));
                    continue;
                }

                try
                {
                    long bytesFreed = target.Type switch
                    {
                        CleanupTargetType.File => DeleteFileRecoverably(validation.CanonicalPath),
                        CleanupTargetType.DirectoryContents => DeleteDirectoryContentsRecoverably(
                            validation.CanonicalPath, cancellationToken),
                        _ => throw new InvalidOperationException("Unsupported cleanup target type.")
                    };

                    results.Add(new CleanupItemResult(
                        target, validation.CanonicalPath, true, false, bytesFreed,
                        "Moved to the Recycle Bin."));
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    results.Add(new CleanupItemResult(
                        target, validation.CanonicalPath, false, false, 0,
                        ex.Message));
                }
            }

            return new CleanupExecutionResult { Items = results, WasCancelled = cancelled };
        }

        private static (bool IsAllowed, string CanonicalPath, string Message) Validate(CleanupTarget target)
        {
            if (target == null || string.IsNullOrWhiteSpace(target.Path))
                return (false, string.Empty, "The cleanup target is empty.");

            if (target.DeletionMode != CleanupDeletionMode.RecycleBin)
                return (false, string.Empty, "Permanent deletion is not available during Phase 1.");

            if (target.RequiredPrivilege == CleanupPrivilege.Administrator)
                return (false, string.Empty,
                    "Administrator filesystem cleanup is not available in the standard-user app. Use Windows maintenance tools instead.");

            string canonicalPath;
            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(target.Path.Trim());
                canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return (false, string.Empty, $"The cleanup path is invalid: {ex.Message}");
            }

            string? root = Path.GetPathRoot(canonicalPath);
            if (string.IsNullOrWhiteSpace(root) || PathComparer.Equals(
                    Path.TrimEndingDirectorySeparator(root), canonicalPath))
            {
                return (false, canonicalPath, "Drive and volume roots cannot be cleanup targets.");
            }

            foreach (string protectedRoot in GetProtectedRoots())
            {
                if (PathComparer.Equals(canonicalPath, protectedRoot) ||
                    IsDescendant(protectedRoot, canonicalPath))
                {
                    return (false, canonicalPath,
                        $"The target would contain protected location '{protectedRoot}'.");
                }

                if (target.Origin == CleanupTargetOrigin.UserSelected &&
                    target.Type == CleanupTargetType.DirectoryContents &&
                    IsDescendant(canonicalPath, protectedRoot) &&
                    !IsUnderApprovedTemporaryRoot(canonicalPath))
                {
                    return (false, canonicalPath,
                        $"User-selected cleanup is not allowed inside protected location '{protectedRoot}'.");
                }
            }

            if (ContainsReparsePoint(canonicalPath))
            {
                return (false, canonicalPath,
                    "The target or one of its parents is a link, junction, or mount point.");
            }

            bool exists = target.Type == CleanupTargetType.File
                ? File.Exists(canonicalPath)
                : Directory.Exists(canonicalPath);

            if (!exists)
                return (false, canonicalPath, "The cleanup target no longer exists.");

            if (target.Type == CleanupTargetType.DirectoryContents &&
                ContainsDescendantReparsePoint(canonicalPath))
            {
                return (false, canonicalPath,
                    "The target contains a link, junction, or mount point.");
            }

            return (true, canonicalPath, "Allowed; deletion will use the Recycle Bin.");
        }

        private static long DeleteFileRecoverably(string path)
        {
            EnsureNotReparsePoint(path);
            long size = new FileInfo(path).Length;
            FileSystem.DeleteFile(
                path,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException);
            return File.Exists(path) ? 0 : size;
        }

        private static long DeleteDirectoryContentsRecoverably(
            string directory,
            CancellationToken cancellationToken)
        {
            EnsureNotReparsePoint(directory);
            long bytesFreed = 0;

            // Snapshot and validate the whole tree before moving anything. This prevents
            // a late-discovered junction from producing an avoidable partial cleanup.
            var files = new List<string>();
            var directories = new List<string>();
            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string current = pending.Pop();
                EnsureNotReparsePoint(current);
                foreach (string file in Directory.EnumerateFiles(current))
                {
                    EnsureDirectChild(current, file);
                    EnsureNotReparsePoint(file);
                    files.Add(file);
                }
                foreach (string child in Directory.EnumerateDirectories(current))
                {
                    EnsureDirectChild(current, child);
                    EnsureNotReparsePoint(child);
                    directories.Add(child);
                    pending.Push(child);
                }
            }

            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytesFreed += DeleteFileRecoverably(file);
            }

            foreach (string childDirectory in directories
                .OrderByDescending(path => path.Count(character => character == Path.DirectorySeparatorChar)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(childDirectory)) continue;
                EnsureNotReparsePoint(childDirectory);
                FileSystem.DeleteDirectory(
                    childDirectory,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin,
                    UICancelOption.ThrowException);
            }

            return bytesFreed;
        }

        private static void EnsureDirectChild(string parent, string child)
        {
            string canonicalParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            string canonicalChild = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
            string? actualParent = Path.GetDirectoryName(canonicalChild);
            if (actualParent == null || !PathComparer.Equals(actualParent, canonicalParent))
                throw new IOException("The cleanup target changed outside its approved parent.");
        }

        private static void EnsureNotReparsePoint(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cleanup refuses links, junctions, and mount points.");
        }

        private static bool ContainsReparsePoint(string path)
        {
            string? current = path;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                string? parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || PathComparer.Equals(parent, current))
                    break;
                current = parent;
            }

            return false;
        }

        private static bool ContainsDescendantReparsePoint(string root)
        {
            try
            {
                var pending = new Stack<string>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    string directory = pending.Pop();
                    foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                            return true;
                        if (Directory.Exists(entry)) pending.Push(entry);
                    }
                }
                return false;
            }
            catch
            {
                // If the tree cannot be inspected completely, it cannot be approved.
                return true;
            }
        }

        private static bool IsDescendant(string candidate, string parent)
        {
            string relative = Path.GetRelativePath(parent, candidate);
            return relative != "." &&
                   !relative.Equals("..", StringComparison.Ordinal) &&
                   !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                   !Path.IsPathRooted(relative);
        }

        private static bool IsUnderApprovedTemporaryRoot(string path)
        {
            string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            return PathComparer.Equals(path, temp) || IsDescendant(path, temp);
        }

        private static IEnumerable<string> GetProtectedRoots()
        {
            var roots = new HashSet<string>(PathComparer);

            void Add(string? path)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    roots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
            }

            Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            Add(Environment.SystemDirectory);
            Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

            return roots;
        }

        private static long EstimateBytes(string path, CleanupTargetType type)
        {
            try
            {
                if (type == CleanupTargetType.File)
                    return File.Exists(path) ? new FileInfo(path).Length : 0;

                long total = 0;
                var pending = new Stack<string>();
                pending.Push(path);
                while (pending.Count > 0)
                {
                    string directory = pending.Pop();
                    EnsureNotReparsePoint(directory);
                    foreach (string file in Directory.EnumerateFiles(directory))
                    {
                        EnsureNotReparsePoint(file);
                        total += new FileInfo(file).Length;
                    }
                    foreach (string child in Directory.EnumerateDirectories(directory))
                    {
                        EnsureNotReparsePoint(child);
                        pending.Push(child);
                    }
                }
                return total;
            }
            catch
            {
                return 0;
            }
        }
    }
}
