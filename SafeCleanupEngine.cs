#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
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
        string Message,
        bool HadPartialFailure = false);

    public sealed class CleanupExecutionResult
    {
        public IReadOnlyList<CleanupItemResult> Items { get; init; } = Array.Empty<CleanupItemResult>();
        public long BytesFreed => Items.Sum(item => item.BytesFreed);
        public int SucceededCount => Items.Count(item => item.Succeeded);
        public int FailedCount => Items.Count(item =>
            (!item.Succeeded && !item.WasSkipped) || item.HadPartialFailure);
        public int SkippedCount => Items.Count(item => item.WasSkipped);
        public bool WasCancelled { get; init; }
    }

    /// <summary>
    /// Central safety boundary for filesystem cleanup. Phase 1 intentionally permits
    /// recoverable deletion only; permanent deletion requires a future expert workflow.
    /// </summary>
    public sealed class SafeCleanupEngine
    {
        private const uint FoDelete = 0x0003;
        private const ushort FofSilent = 0x0004;
        private const ushort FofNoConfirmation = 0x0010;
        private const ushort FofAllowUndo = 0x0040;
        private const ushort FofNoConfirmMkdir = 0x0200;
        private const ushort FofNoErrorUi = 0x0400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShFileOpStruct
        {
            public IntPtr Hwnd;
            public uint Function;
            [MarshalAs(UnmanagedType.LPWStr)] public string From;
            [MarshalAs(UnmanagedType.LPWStr)] public string? To;
            public ushort Flags;
            [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
            public IntPtr NameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern int SHFileOperation(ref ShFileOpStruct operation);
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
        private readonly IReadOnlyList<string> _protectedRoots;
        private readonly string _approvedTemporaryRoot;
        private readonly Func<string, bool> _isReparsePoint;
        private readonly Func<string, long> _deleteFile;
        private readonly Func<string, CancellationToken, long> _deleteDirectoryContents;
        private readonly Func<bool> _isAdministrator;

        public SafeCleanupEngine()
            : this(
                GetProtectedRoots(),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                IsActualReparsePoint,
                DeleteFileRecoverably,
                DeleteDirectoryContentsRecoverably,
                IsRunningAsAdministrator)
        {
        }

        internal SafeCleanupEngine(
            IEnumerable<string> protectedRoots,
            string approvedTemporaryRoot,
            Func<string, bool>? isReparsePoint = null,
            Func<string, long>? deleteFile = null,
            Func<string, CancellationToken, long>? deleteDirectoryContents = null,
            Func<bool>? isAdministrator = null)
        {
            ArgumentNullException.ThrowIfNull(protectedRoots);
            ArgumentException.ThrowIfNullOrWhiteSpace(approvedTemporaryRoot);

            _protectedRoots = protectedRoots
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
                .Distinct(PathComparer)
                .ToArray();
            _approvedTemporaryRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(approvedTemporaryRoot));
            _isReparsePoint = isReparsePoint ?? IsActualReparsePoint;
            _deleteFile = deleteFile ?? DeleteFileRecoverably;
            _deleteDirectoryContents = deleteDirectoryContents ?? DeleteDirectoryContentsRecoverably;
            _isAdministrator = isAdministrator ?? IsRunningAsAdministrator;
        }

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
            Task.Run(() => Execute(plan.Targets, cancellationToken));

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
                        CleanupTargetType.File => _deleteFile(validation.CanonicalPath),
                        CleanupTargetType.DirectoryContents => _deleteDirectoryContents(
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
                catch (PartialCleanupException ex)
                {
                    results.Add(new CleanupItemResult(
                        target, validation.CanonicalPath, true, false, ex.BytesFreed,
                        ex.Message, HadPartialFailure: true));
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

        private (bool IsAllowed, string CanonicalPath, string Message) Validate(CleanupTarget target)
        {
            if (target == null || string.IsNullOrWhiteSpace(target.Path))
                return (false, string.Empty, "The cleanup target is empty.");

            if (target.DeletionMode != CleanupDeletionMode.RecycleBin)
                return (false, string.Empty, "Permanent deletion is not available during Phase 1.");

            if (target.RequiredPrivilege == CleanupPrivilege.Administrator && !_isAdministrator())
                return (false, string.Empty,
                    "This cleanup target requires an elevated application process.");

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

            foreach (string protectedRoot in _protectedRoots)
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
            if (!File.Exists(path)) return 0;
            EnsureNotReparsePoint(path);
            long size = new FileInfo(path).Length;
            MoveToRecycleBinWithoutUi(path);
            return File.Exists(path) ? 0 : size;
        }

        private static void MoveToRecycleBinWithoutUi(string path)
        {
            const int SharingViolation = 0x20;
            const int LockViolation = 0x21;
            int result = 0;
            bool aborted = false;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (!File.Exists(path) && !Directory.Exists(path)) return;
                result = MovePathsToRecycleBinWithoutUi(new[] { path }, out aborted);
                if (result == 0 && !aborted) return;
                if (!File.Exists(path) && !Directory.Exists(path)) return;
                if (result is not (SharingViolation or LockViolation)) break;
                Thread.Sleep(100 * (attempt + 1));
            }

            if (result is SharingViolation or LockViolation)
                throw new IOException("The item is in use by another process and was left untouched.");
            if (result == 0x7C)
                throw new IOException("The item changed or became unavailable while cleanup was running.");
            if (aborted)
                throw new IOException("Windows cancelled the Recycle Bin operation.");
            throw new IOException($"Windows could not move the item to the Recycle Bin (shell result 0x{result:X}).");
        }

        private static int MovePathsToRecycleBinWithoutUi(
            IReadOnlyList<string> paths,
            out bool aborted)
        {
            var operation = new ShFileOpStruct
            {
                Function = FoDelete,
                From = BuildShellPathList(paths),
                To = null,
                Flags = FofSilent | FofNoConfirmation | FofAllowUndo |
                        FofNoConfirmMkdir | FofNoErrorUi,
                ProgressTitle = null
            };
            int result = SHFileOperation(ref operation);
            aborted = operation.AnyOperationsAborted;
            return result;
        }

        internal static string BuildShellPathList(IEnumerable<string> paths)
        {
            string[] materialized = paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
            if (materialized.Length == 0)
                throw new ArgumentException("At least one cleanup path is required.", nameof(paths));
            if (materialized.Any(path => path.IndexOf('\0') >= 0))
                throw new ArgumentException("Cleanup paths cannot contain null characters.", nameof(paths));
            return string.Join('\0', materialized) + '\0' + '\0';
        }

        private static long DeleteDirectoryContentsRecoverably(
            string directory,
            CancellationToken cancellationToken)
        {
            EnsureNotReparsePoint(directory);
            long bytesFreed = 0;
            var skipped = new List<string>();

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

            const int BatchSize = 64;
            for (int offset = 0; offset < files.Count; offset += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<string> batch = files.Skip(offset).Take(BatchSize)
                    .Where(File.Exists)
                    .ToList();
                if (batch.Count == 0) continue;

                var sizes = new Dictionary<string, long>(PathComparer);
                foreach (string file in batch)
                {
                    try { sizes[file] = new FileInfo(file).Length; }
                    catch (IOException) { sizes[file] = 0; }
                    catch (UnauthorizedAccessException) { sizes[file] = 0; }
                }

                _ = MovePathsToRecycleBinWithoutUi(batch, out _);

                foreach (string file in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!File.Exists(file))
                    {
                        bytesFreed += sizes[file];
                        continue;
                    }

                    try
                    {
                        bytesFreed += DeleteFileRecoverably(file);
                    }
                    catch (IOException ex)
                    {
                        skipped.Add($"{Path.GetFileName(file)}: {ex.Message}");
                    }
                    catch (UnauthorizedAccessException)
                    {
                        skipped.Add($"{Path.GetFileName(file)}: access was denied");
                    }
                }
            }

            foreach (string childDirectory in directories
                .OrderByDescending(path => path.Count(character => character == Path.DirectorySeparatorChar)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(childDirectory)) continue;
                try
                {
                    EnsureNotReparsePoint(childDirectory);
                    if (!Directory.EnumerateFileSystemEntries(childDirectory).Any())
                        MoveToRecycleBinWithoutUi(childDirectory);
                }
                catch (IOException)
                {
                    // A busy or concurrently recreated directory is intentionally retained.
                }
                catch (UnauthorizedAccessException)
                {
                    // An inaccessible empty directory is intentionally retained.
                }
            }

            if (skipped.Count > 0)
                throw new PartialCleanupException(bytesFreed, skipped.Count,
                    $"Moved accessible contents; {skipped.Count} in-use or unavailable item(s) were left untouched. " +
                    string.Join(" ", skipped.Take(3)));

            return bytesFreed;
        }

        internal sealed class PartialCleanupException : IOException
        {
            public PartialCleanupException(long bytesFreed, int skippedCount, string message)
                : base(message)
            {
                BytesFreed = bytesFreed;
                SkippedCount = skippedCount;
            }

            public long BytesFreed { get; }
            public int SkippedCount { get; }
        }

        private static bool IsRunningAsAdministrator()
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
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

        private bool ContainsReparsePoint(string path)
        {
            string? current = path;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    _isReparsePoint(current))
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

        private bool ContainsDescendantReparsePoint(string root)
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
                        if (_isReparsePoint(entry))
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

        internal static bool IsPathDescendant(string candidate, string parent) =>
            IsDescendant(candidate, parent);

        private bool IsUnderApprovedTemporaryRoot(string path)
        {
            return PathComparer.Equals(path, _approvedTemporaryRoot) ||
                   IsDescendant(path, _approvedTemporaryRoot);
        }

        private static HashSet<string> GetProtectedRoots()
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

        private long EstimateBytes(string path, CleanupTargetType type)
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
                    if (_isReparsePoint(directory))
                        throw new IOException("Cleanup refuses links, junctions, and mount points.");
                    foreach (string file in Directory.EnumerateFiles(directory))
                    {
                        if (_isReparsePoint(file))
                            throw new IOException("Cleanup refuses links, junctions, and mount points.");
                        total += new FileInfo(file).Length;
                    }
                    foreach (string child in Directory.EnumerateDirectories(directory))
                    {
                        if (_isReparsePoint(child))
                            throw new IOException("Cleanup refuses links, junctions, and mount points.");
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

        private static bool IsActualReparsePoint(string path) =>
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }
}
