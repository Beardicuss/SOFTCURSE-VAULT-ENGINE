using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SoftcurseVaultCleaner
{
    // ══════════════════════════════════════════════════════════════════════════
    //  SIZE FORMATTER
    // ══════════════════════════════════════════════════════════════════════════

    public static class SizeFormatter
    {
        public static string Format(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes; int idx = 0;
            while (value >= 1024 && idx < units.Length - 1) { value /= 1024; idx++; }
            return $"{value:F1} {units[idx]}";
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  SELECTABLE BASE
    // ══════════════════════════════════════════════════════════════════════════

    public abstract class SelectableItem : INotifyPropertyChanged
    {
        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked != value) { _isChecked = value; OnPC(nameof(IsChecked)); } }
        }
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPC(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MODELS
    // ══════════════════════════════════════════════════════════════════════════

    public class FolderSizeResult
    {
        public string Name     { get; set; }
        public string FullPath { get; set; }
        public long   Size     { get; set; }
        public string SizeStr  => SizeFormatter.Format(Size);
        public string PctStr   { get; set; }
        public string Note     { get; set; }
    }

    public class JunkTarget : SelectableItem
    {
        public string Label    { get; set; }
        public string FullPath { get; set; }
        public long   Size     { get; set; }
        public string SizeStr  => SizeFormatter.Format(Size);
        public bool   Safe     { get; set; }
        public string SafeStr  => Safe ? "Recommended" : "Review";
        public string Category { get; set; }
        public string Note     { get; set; }
        public bool   IsFile   { get; set; }
        public CleanupPrivilege RequiredPrivilege { get; set; } = CleanupPrivilege.StandardUser;
    }

    public class LargeFileResult : SelectableItem
    {
        public string Path   { get; set; }
        public long   Size   { get; set; }
        public string SizeStr => SizeFormatter.Format(Size);
        public string Ext    { get; set; }
        public string Folder { get; set; }
    }

    public class DupeRow : SelectableItem
    {
        public int    GroupId    { get; set; }
        public bool   IsHeader   { get; set; }
        public bool   IsDupe     { get; set; }
        public string FilePath   { get; set; }
        public long   FileSize   { get; set; }
        public string SizeStr    => SizeFormatter.Format(FileSize);
        public string Hash       { get; set; }
        public string WastedInfo { get; set; }
        public string GroupLabel => IsHeader ? $"#{GroupId}" : "  ↳";
    }

    public class ProgramEntry
    {
        public string Name     { get; set; }
        public string FullPath { get; set; }
        public long   Size     { get; set; }
        public string SizeStr  => SizeFormatter.Format(Size);
    }

    public class DuplicateGroup
    {
        public int    GroupId  { get; set; }
        public string Hash     { get; set; }
        public long   FileSize { get; set; }
        public List<string> Files { get; set; } = new List<string>();
        public long   WastedSize => FileSize * (Files.Count - 1);
        public string WastedStr  => SizeFormatter.Format(WastedSize);
    }

    public class DiskAnalysisResult
    {
        public List<FolderSizeResult> TopFolders  { get; set; } = new List<FolderSizeResult>();
        public List<JunkTarget>       JunkTargets { get; set; } = new List<JunkTarget>();
        public List<LargeFileResult>  LargeFiles  { get; set; } = new List<LargeFileResult>();
        public List<ProgramEntry>     Programs    { get; set; } = new List<ProgramEntry>();
        public long TotalJunkSafe   { get; set; }
        public long TotalJunkReview { get; set; }
    }

    public class DeletionResult
    {
        public int    DeletedCount  { get; set; }
        public int    FailedCount   { get; set; }
        public long   BytesFreed    { get; set; }
        public string BytesFreedStr => SizeFormatter.Format(BytesFreed);
        public List<string> Errors  { get; set; } = new List<string>();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  SERVICE
    // ══════════════════════════════════════════════════════════════════════════

    public class DiskAnalyzerService
    {
        private readonly SafeCleanupEngine _cleanupEngine = new SafeCleanupEngine();

        public void Cancel() { }  // CancellationToken handled by caller

        // ── FULL SCAN ────────────────────────────────────────────────────────

        public async Task<DiskAnalysisResult> RunFullScanAsync(
            long minFileSizeBytes, string rootDrive,
            Action<string> statusCb,
            Action<int> progressCb, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(rootDrive)) rootDrive = "C:\\";
            return await Task.Run(() =>
                RunScan(minFileSizeBytes, rootDrive, statusCb, progressCb, token), token);
        }

        private DiskAnalysisResult RunScan(long minFileSize, string rootDrive,
            Action<string> Status, Action<int> Progress, CancellationToken token)
        {
            var result = new DiskAnalysisResult();

            Status($"Phase 1/4 — Mapping top-level {rootDrive} folders…"); Progress(5);
            result.TopFolders = ScanTopFolders(rootDrive, Status, token);
            Progress(20);
            if (token.IsCancellationRequested) return result;

            Status("Phase 2/4 — Scanning junk & cache locations…");
            var junkList = BuildJunkTargets();
            for (int i = 0; i < junkList.Count; i++)
            {
                if (token.IsCancellationRequested) break;
                var t = junkList[i];
                Status($"Phase 2/4 — Checking: {t.Label}…");
                long sz = MeasurePath(t.FullPath, out bool isFile);
                if (sz > 0)
                {
                    t.Size = sz; t.IsFile = isFile;
                    result.JunkTargets.Add(t);
                    if (t.Safe) result.TotalJunkSafe   += sz;
                    else        result.TotalJunkReview += sz;
                }
                Progress(20 + (int)(i / (double)junkList.Count * 35));
            }
            result.JunkTargets.Sort((a, b) => b.Size.CompareTo(a.Size));
            Progress(55);
            if (token.IsCancellationRequested) return result;

            Status("Phase 3/4 — Finding large files…");
            result.LargeFiles = ScanLargeFiles(rootDrive, minFileSize, token);
            result.LargeFiles.Sort((a, b) => b.Size.CompareTo(a.Size));
            if (result.LargeFiles.Count > 500)
                result.LargeFiles = result.LargeFiles.Take(500).ToList();
            Progress(85);
            if (token.IsCancellationRequested) return result;

            Status("Phase 4/4 — Sizing installed programs…");
            result.Programs = ScanPrograms(rootDrive, Status, token);
            result.Programs.Sort((a, b) => b.Size.CompareTo(a.Size));
            Progress(100); Status("Deep scan complete.");
            return result;
        }

        // ── DELETE JUNK ──────────────────────────────────────────────────────

        public async Task<DeletionResult> DeleteJunkAsync(
            IEnumerable<JunkTarget> items, Action<string> statusCb,
            CancellationToken token = default)
        {
            var selected = items.ToList();
            foreach (var item in selected)
                statusCb?.Invoke($"Preparing recoverable deletion: {item.Label}…");

            var targets = selected.Select(item => new CleanupTarget(
                $"analyzer-junk:{item.Label}", item.Label, item.FullPath, item.Note,
                item.IsFile ? CleanupTargetType.File : CleanupTargetType.DirectoryContents,
                CleanupTargetOrigin.BuiltIn, "Analyzer",
                item.Safe ? CleanupRisk.Low : CleanupRisk.High, item.RequiredPrivilege));
            return ConvertResult(await _cleanupEngine.ExecuteAsync(targets, token));
        }

        // ── DELETE LARGE FILES ───────────────────────────────────────────────

        public async Task<DeletionResult> DeleteLargeFilesAsync(
            IEnumerable<LargeFileResult> items, Action<string> statusCb,
            CancellationToken token = default)
        {
            var selected = items.ToList();
            foreach (var item in selected)
                statusCb?.Invoke($"Preparing recoverable deletion: {System.IO.Path.GetFileName(item.Path)}…");

            var targets = selected.Select(item => new CleanupTarget(
                $"analyzer-large:{item.Path}", System.IO.Path.GetFileName(item.Path), item.Path,
                "User-selected large file", CleanupTargetType.File, CleanupTargetOrigin.UserSelected));
            return ConvertResult(await _cleanupEngine.ExecuteAsync(targets, token));
        }

        // ── DELETE DUPLICATES ────────────────────────────────────────────────

        public async Task<DeletionResult> DeleteDupesAsync(
            IEnumerable<DupeRow> rows, Action<string> statusCb,
            CancellationToken token = default)
        {
            var selected = rows.Where(row => row.IsDupe && row.IsChecked).ToList();
            foreach (var row in selected)
                statusCb?.Invoke($"Preparing recoverable deletion: {System.IO.Path.GetFileName(row.FilePath)}…");

            var targets = selected.Select(row => new CleanupTarget(
                $"analyzer-duplicate:{row.FilePath}", System.IO.Path.GetFileName(row.FilePath),
                row.FilePath, "User-confirmed duplicate", CleanupTargetType.File,
                CleanupTargetOrigin.UserSelected));
            return ConvertResult(await _cleanupEngine.ExecuteAsync(targets, token));
        }

        public IReadOnlyList<CleanupPreviewItem> PreviewJunk(IEnumerable<JunkTarget> items) =>
            _cleanupEngine.Preview(items.Select(item => new CleanupTarget(
                $"analyzer-junk:{item.Label}", item.Label, item.FullPath, item.Note,
                item.IsFile ? CleanupTargetType.File : CleanupTargetType.DirectoryContents,
                CleanupTargetOrigin.BuiltIn, "Analyzer",
                item.Safe ? CleanupRisk.Low : CleanupRisk.High, item.RequiredPrivilege)));

        public IReadOnlyList<CleanupPreviewItem> PreviewFiles(IEnumerable<string> paths, string reason) =>
            _cleanupEngine.Preview(paths.Select(path => new CleanupTarget(
                $"analyzer-file:{path}", System.IO.Path.GetFileName(path), path, reason,
                CleanupTargetType.File, CleanupTargetOrigin.UserSelected)));

        private static DeletionResult ConvertResult(CleanupExecutionResult result)
        {
            var converted = new DeletionResult
            {
                DeletedCount = result.SucceededCount,
                FailedCount = result.FailedCount + result.SkippedCount,
                BytesFreed = result.BytesFreed
            };
            converted.Errors.AddRange(result.Items
                .Where(item => !item.Succeeded)
                .Select(item => $"{item.Target.DisplayName}: {item.Message}"));
            return converted;
        }

        // ── DUPLICATE FINDER ─────────────────────────────────────────────────

        public async Task<List<DuplicateGroup>> FindDuplicatesAsync(string rootPath,
            Action<string> statusCb, Action<int> progressCb, CancellationToken token)
        {
            return await Task.Run(() => FindDuplicates(rootPath, statusCb, progressCb, token), token);
        }

        private List<DuplicateGroup> FindDuplicates(string root,
            Action<string> statusCb, Action<int> progressCb, CancellationToken token)
        {
            const long MIN_SIZE = 10 * 1024;
            statusCb?.Invoke("Collecting and SHA-256 verifying candidate files…");
            int groupId = 1;
            return DuplicateFileVerifier.Find(root, MIN_SIZE,
                    percent => progressCb?.Invoke(30 + (int)(percent * 0.7)), token)
                .Select(group => new DuplicateGroup
                {
                    GroupId = groupId++,
                    Hash = group.Sha256.Substring(0, 8),
                    FileSize = group.FileSize,
                    Files = group.Files.ToList()
                })
                .ToList();
        }

        // ── SCAN HELPERS ─────────────────────────────────────────────────────

        private List<FolderSizeResult> ScanTopFolders(string rootDrive, Action<string> Status, CancellationToken token)
        {
            var results = new List<FolderSizeResult>();
            long driveTotal = 1;
            try { driveTotal = new DriveInfo(rootDrive).TotalSize; } catch { }

            var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Windows"]             = "OS — use DISM to clean WinSxS",
                ["Users"]               = "User data — check Downloads, Desktop, Videos",
                ["Program Files"]       = "64-bit apps — uninstall unused via Settings",
                ["Program Files (x86)"] = "32-bit apps — uninstall unused via Settings",
                ["ProgramData"]         = "App data — grows silently, check with caution",
                ["pagefile.sys"]        = "Virtual memory — resize in Settings, do NOT delete",
                ["hiberfil.sys"]        = "Hibernate — run: powercfg -h off  to reclaim",
                ["swapfile.sys"]        = "Modern app swap file",
            };
            try
            {
                var entries = Directory.GetFileSystemEntries(rootDrive)
                    .Where(e => !System.IO.Path.GetFileName(e).StartsWith("$"))
                    .OrderBy(e => e).ToList();
                foreach (var entry in entries)
                {
                    if (token.IsCancellationRequested) break;
                    string name = System.IO.Path.GetFileName(entry);
                    Status($"Phase 1/4 — Sizing {rootDrive}{name}…");
                    long sz = MeasurePath(entry, out _);
                    notes.TryGetValue(name, out string note);
                    results.Add(new FolderSizeResult
                    {
                        Name = name, FullPath = entry, Size = sz,
                        PctStr = $"{sz / (double)driveTotal * 100:F1}%", Note = note ?? ""
                    });
                }
            }
            catch { }
            results.Sort((a, b) => b.Size.CompareTo(a.Size));
            return results;
        }

        private List<LargeFileResult> ScanLargeFiles(string root, long minSize, CancellationToken token)
        {
            var results = new List<LargeFileResult>();
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "Windows", "$Recycle.Bin", "System Volume Information", "Recovery", "$WinREAgent", "ProgramData" };
            void Recurse(string path, bool isRoot)
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(path))
                        try { long sz = new FileInfo(file).Length; if (sz >= minSize)
                            results.Add(new LargeFileResult
                            { Path = file, Size = sz, Ext = System.IO.Path.GetExtension(file).ToLower(),
                              Folder = System.IO.Path.GetDirectoryName(file) }); } catch { }
                    foreach (var dir in Directory.EnumerateDirectories(path))
                    {
                        if (token.IsCancellationRequested) return;
                        if (isRoot && skip.Contains(System.IO.Path.GetFileName(dir))) continue;
                        Recurse(dir, false);
                    }
                }
                catch { }
            }
            Recurse(root, true);
            return results;
        }

        private List<ProgramEntry> ScanPrograms(string rootDrive, Action<string> Status, CancellationToken token)
        {
            var results = new List<ProgramEntry>();
            string driveRoot = rootDrive.TrimEnd('\\') + "\\";
            var roots = new[]
            {
                Path.Combine(driveRoot, "Program Files"),
                Path.Combine(driveRoot, "Program Files (x86)"),
                System.IO.Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData), "Programs"),
            };
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    foreach (var dir in Directory.GetDirectories(root))
                    {
                        if (token.IsCancellationRequested) return results;
                        string name = System.IO.Path.GetFileName(dir);
                        Status($"Phase 4/4 — Sizing: {name}…");
                        long sz = GetDirSize(dir);
                        if (sz > 0) results.Add(new ProgramEntry { Name = name, FullPath = dir, Size = sz });
                    }
                }
                catch { }
            }
            return results;
        }

        private long MeasurePath(string path, out bool isFile)
        {
            isFile = false;
            try
            {
                if (File.Exists(path))      { isFile = true; return new FileInfo(path).Length; }
                if (Directory.Exists(path)) { return GetDirSize(path); }
            }
            catch { }
            return 0;
        }

        public static long GetDirSize(string path)
        {
            long total = 0;
            try { foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    try { total += new FileInfo(f).Length; } catch { } } catch { }
            return total;
        }

        // ── SUGGESTIONS TEXT ─────────────────────────────────────────────────

        public static string BuildSuggestions(DiskAnalysisResult result, string selectedDrive)
        {
            long total = 1, free = 0;
            string driveLabel = string.IsNullOrWhiteSpace(selectedDrive) ? "System drive" : selectedDrive.TrimEnd('\\');
            try { var d = new DriveInfo(selectedDrive); total = d.TotalSize; free = d.AvailableFreeSpace; } catch { }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("╔══════════════════════════════════════════════════════════════╗");
            sb.AppendLine("║            SOFTCURSE VAULT ENGINE — ANALYSIS REPORT         ║");
            sb.AppendLine("╚══════════════════════════════════════════════════════════════╝");
            sb.AppendLine();
            sb.AppendLine($"  {driveLabel} Drive  {SizeFormatter.Format(free)} free / {SizeFormatter.Format(total)} total  ({free*100.0/total:F1}% free)");
            sb.AppendLine($"  Safe junk:    {SizeFormatter.Format(result.TotalJunkSafe)}  ← check boxes in Junk Scan tab, then DELETE SELECTED");
            sb.AppendLine($"  Review items: {SizeFormatter.Format(result.TotalJunkReview)}  ← inspect manually before deleting");
            sb.AppendLine();
            sb.AppendLine("══ IMMEDIATE SAFE WINS ════════════════════════════════════════");
            foreach (var j in result.JunkTargets.Where(j => j.Safe).Take(10))
                sb.AppendLine($"  ✅  {j.Label,-35}  {j.SizeStr,10}   {j.Note}");
            sb.AppendLine();
            sb.AppendLine("══ SUPPORTED WINDOWS MAINTENANCE ══════════════════════════════");
            sb.AppendLine("  • Settings → System → Storage → Temporary files");
            sb.AppendLine("  • Enable Windows component cleanup in Vault Cleaner (UAC required)");
            sb.AppendLine();
            sb.AppendLine("══ LARGEST FILES (check boxes in Large Files tab → DELETE SELECTED) ══");
            foreach (var f in result.LargeFiles.Take(15))
                sb.AppendLine($"  📦  {f.SizeStr,10}   {f.Path}");
            sb.AppendLine();
            sb.AppendLine("══ LARGEST PROGRAMS (uninstall via Settings → Apps) ═══════════");
            foreach (var p in result.Programs.Take(10))
                sb.AppendLine($"  🖥  {p.SizeStr,10}   {p.Name}");
            sb.AppendLine();
            sb.AppendLine("══ MORE TIPS ═══════════════════════════════════════════════════");
            sb.AppendLine("  • Settings → System → Storage → Storage Sense (auto-clean)");
            sb.AppendLine("  • Move large files to OneDrive / external drive");
            sb.AppendLine("  • OneDrive: right-click files → Free up space (cloud-only)");
            sb.AppendLine("  • Steam: Library → right-click game → Uninstall");
            return sb.ToString();
        }

        // ── JUNK TARGET DEFINITIONS ──────────────────────────────────────────

        private List<JunkTarget> BuildJunkTargets()
        {
            var config = new CleanupConfig
            {
                CleanTempFiles = true,
                CleanCache = true,
                CleanDevTools = true,
                CleanGaming = true,
                CleanSystemDumps = true
            };

            return new CleanerService().CreateCleanupPlan(config).Targets
                .Select(target => new JunkTarget
                {
                    Label = target.DisplayName,
                    FullPath = target.Path,
                    Note = target.Reason,
                    Safe = target.Risk == CleanupRisk.Low,
                    Category = target.Category,
                    IsFile = target.Type == CleanupTargetType.File,
                    RequiredPrivilege = target.RequiredPrivilege
                })
                .ToList();
        }
    }
}
