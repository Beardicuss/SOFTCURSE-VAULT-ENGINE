using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace BorderlandsStorageCleaner
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
        public string SafeStr  => Safe ? "Safe" : "Review";
        public string Category { get; set; }
        public string Note     { get; set; }
        public bool   IsFile   { get; set; }
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
            result.Programs = ScanPrograms(Status, token);
            result.Programs.Sort((a, b) => b.Size.CompareTo(a.Size));
            Progress(100); Status("Deep scan complete.");
            return result;
        }

        // ── DELETE JUNK ──────────────────────────────────────────────────────

        public async Task<DeletionResult> DeleteJunkAsync(
            IEnumerable<JunkTarget> items, Action<string> statusCb,
            CancellationToken token = default)
        {
            return await Task.Run(() =>
            {
                var res = new DeletionResult();
                foreach (var item in items)
                {
                    if (token.IsCancellationRequested) break;
                    statusCb?.Invoke($"Deleting: {item.Label}…");
                    try
                    {
                        if (item.IsFile)
                        {
                            if (File.Exists(item.FullPath))
                            {
                                long sz = new FileInfo(item.FullPath).Length;
                                File.Delete(item.FullPath);
                                res.BytesFreed += sz; res.DeletedCount++;
                            }
                        }
                        else
                        {
                            if (Directory.Exists(item.FullPath))
                            {
                                res.BytesFreed += WipeDirectory(item.FullPath, token);
                                res.DeletedCount++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        res.FailedCount++;
                        res.Errors.Add($"{item.Label}: {ex.Message}");
                    }
                }
                return res;
            }, token);
        }

        // ── DELETE LARGE FILES ───────────────────────────────────────────────

        public async Task<DeletionResult> DeleteLargeFilesAsync(
            IEnumerable<LargeFileResult> items, Action<string> statusCb,
            CancellationToken token = default)
        {
            return await Task.Run(() =>
            {
                var res = new DeletionResult();
                foreach (var item in items)
                {
                    if (token.IsCancellationRequested) break;
                    statusCb?.Invoke($"Deleting: {System.IO.Path.GetFileName(item.Path)}…");
                    try
                    {
                        if (File.Exists(item.Path))
                        {
                            long sz = new FileInfo(item.Path).Length;
                            File.Delete(item.Path);
                            res.BytesFreed += sz; res.DeletedCount++;
                        }
                    }
                    catch (Exception ex) { res.FailedCount++; res.Errors.Add($"{item.Path}: {ex.Message}"); }
                }
                return res;
            }, token);
        }

        // ── DELETE DUPLICATES ────────────────────────────────────────────────

        public async Task<DeletionResult> DeleteDupesAsync(
            IEnumerable<DupeRow> rows, Action<string> statusCb,
            CancellationToken token = default)
        {
            return await Task.Run(() =>
            {
                var res = new DeletionResult();
                foreach (var row in rows.Where(r => r.IsDupe && r.IsChecked))
                {
                    if (token.IsCancellationRequested) break;
                    statusCb?.Invoke($"Deleting duplicate: {System.IO.Path.GetFileName(row.FilePath)}…");
                    try
                    {
                        if (File.Exists(row.FilePath))
                        {
                            long sz = new FileInfo(row.FilePath).Length;
                            File.Delete(row.FilePath); res.BytesFreed += sz; res.DeletedCount++;
                        }
                    }
                    catch (Exception ex) { res.FailedCount++; res.Errors.Add(ex.Message); }
                }
                return res;
            }, token);
        }

        // ── DUPLICATE FINDER ─────────────────────────────────────────────────

        public async Task<List<DuplicateGroup>> FindDuplicatesAsync(string rootPath,
            Action<string> statusCb, CancellationToken token)
        {
            return await Task.Run(() => FindDuplicates(rootPath, statusCb, token), token);
        }

        private List<DuplicateGroup> FindDuplicates(string root,
            Action<string> statusCb, CancellationToken token)
        {
            const long MIN_SIZE = 10 * 1024;
            statusCb?.Invoke("Collecting file list…");

            var bySize = new Dictionary<long, List<string>>();
            foreach (var file in EnumerateFilesIterative(root))
            {
                if (token.IsCancellationRequested) return new List<DuplicateGroup>();
                try
                {
                    long sz = new FileInfo(file).Length;
                    if (sz < MIN_SIZE) continue;
                    if (!bySize.ContainsKey(sz)) bySize[sz] = new List<string>();
                    bySize[sz].Add(file);
                }
                catch { }
            }

            var groups = new List<DuplicateGroup>();
            var candidates = bySize.Where(kv => kv.Value.Count > 1).ToList();
            int done = 0, gid = 1;
            foreach (var kv in candidates)
            {
                if (token.IsCancellationRequested) break;
                statusCb?.Invoke($"Hashing {++done}/{candidates.Count} size groups…");
                var byHash = new Dictionary<string, List<string>>();
                foreach (var f in kv.Value)
                {
                    if (token.IsCancellationRequested) break;
                    try
                    {
                        string h = ComputeMD5(f);
                        if (!byHash.ContainsKey(h)) byHash[h] = new List<string>();
                        byHash[h].Add(f);
                    }
                    catch { }
                }
                foreach (var hkv in byHash)
                    if (hkv.Value.Count > 1)
                        groups.Add(new DuplicateGroup
                        {
                            GroupId = gid++, Hash = hkv.Key.Substring(0, 8),
                            FileSize = kv.Key, Files = hkv.Value
                        });
            }
            groups.Sort((a, b) => b.WastedSize.CompareTo(a.WastedSize));
            return groups;
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

        private List<ProgramEntry> ScanPrograms(Action<string> Status, CancellationToken token)
        {
            var results = new List<ProgramEntry>();
            var roots = new[]
            {
                @"C:\Program Files", @"C:\Program Files (x86)",
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

        private long WipeDirectory(string path, CancellationToken token)
        {
            long freed = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(path))
                { if (token.IsCancellationRequested) return freed;
                  try { freed += new FileInfo(file).Length; File.Delete(file); } catch { } }
                foreach (var dir in Directory.EnumerateDirectories(path))
                { if (token.IsCancellationRequested) return freed;
                  try { freed += GetDirSize(dir); Directory.Delete(dir, true); } catch { } }
            }
            catch { }
            return freed;
        }

        public static long GetDirSize(string path)
        {
            long total = 0;
            try { foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    try { total += new FileInfo(f).Length; } catch { } } catch { }
            return total;
        }

        private IEnumerable<string> EnumerateFilesIterative(string root)
        {
            var q = new Queue<string>(); q.Enqueue(root);
            while (q.Count > 0)
            {
                string dir = q.Dequeue();
                string[] files; try { files = Directory.GetFiles(dir); } catch { files = Array.Empty<string>(); }
                foreach (var f in files) yield return f;
                string[] dirs; try { dirs = Directory.GetDirectories(dir); } catch { dirs = Array.Empty<string>(); }
                foreach (var d in dirs) q.Enqueue(d);
            }
        }

        private string ComputeMD5(string path)
        {
            using (var md5 = MD5.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-","").ToLower();
        }

        // ── SUGGESTIONS TEXT ─────────────────────────────────────────────────

        public static string BuildSuggestions(DiskAnalysisResult result)
        {
            long total = 1, free = 0;
            try { var d = new DriveInfo("C"); total = d.TotalSize; free = d.AvailableFreeSpace; } catch { }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("╔══════════════════════════════════════════════════════════════╗");
            sb.AppendLine("║            SOFTCURSE VAULT ENGINE — ANALYSIS REPORT         ║");
            sb.AppendLine("╚══════════════════════════════════════════════════════════════╝");
            sb.AppendLine();
            sb.AppendLine($"  C: Drive  {SizeFormatter.Format(free)} free / {SizeFormatter.Format(total)} total  ({free*100.0/total:F1}% free)");
            sb.AppendLine($"  Safe junk:    {SizeFormatter.Format(result.TotalJunkSafe)}  ← check boxes in Junk Scan tab, then DELETE SELECTED");
            sb.AppendLine($"  Review items: {SizeFormatter.Format(result.TotalJunkReview)}  ← inspect manually before deleting");
            sb.AppendLine();
            sb.AppendLine("══ IMMEDIATE SAFE WINS ════════════════════════════════════════");
            foreach (var j in result.JunkTargets.Where(j => j.Safe).Take(10))
                sb.AppendLine($"  ✅  {j.Label,-35}  {j.SizeStr,10}   {j.Note}");
            sb.AppendLine();
            sb.AppendLine("══ RUN THESE COMMANDS (as Administrator) ══════════════════════");
            sb.AppendLine("  cleanmgr /sageset:1  then  cleanmgr /sagerun:1");
            sb.AppendLine("  DISM /Online /Cleanup-Image /StartComponentCleanup");
            sb.AppendLine("  powercfg -h off          ← frees ~your RAM size");
            sb.AppendLine("  del /s /q %TEMP%\\*");
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
            string u      = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string local  = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            JunkTarget T(string label, string path, string note, bool safe, string cat)
                => new JunkTarget { Label=label, FullPath=path, Note=note, Safe=safe, Category=cat };

            return new List<JunkTarget>
            {
                // System
                T("Windows Temp",         @"C:\Windows\Temp",                                        "OS temp — Windows rebuilds automatically",            true,  "System"),
                T("User Temp (%TEMP%)",   Path.Combine(local,"Temp"),                               "Per-user application leftovers",                     true,  "System"),
                T("Windows Update Cache", @"C:\Windows\SoftwareDistribution\Download",              "Old update packages — safe after updates applied",   true,  "System"),
                T("Prefetch Files",       @"C:\Windows\Prefetch",                                   "App launch cache — Windows rebuilds it",             true,  "System"),
                T("Delivery Opt Cache",   @"C:\Windows\SoftwareDistribution\DeliveryOptimization",  "P2P update chunks",                                  true,  "System"),
                T("Windows Error Reports",Path.Combine(local,"Microsoft","Windows","WER"),          "Crash dump reports",                                 true,  "System"),
                T("CBS Logs",             @"C:\Windows\Logs\CBS",                                   "Component servicing logs",                           true,  "System"),
                T("Minidump Files",       @"C:\Windows\Minidump",                                   "Crash memory dumps",                                 true,  "System"),
                T("Thumbnail Cache",      Path.Combine(local,"Microsoft","Windows","Explorer"),     "Explorer thumbcache files — rebuilt automatically",  true,  "System"),
                T("Recycle Bin",          @"C:\$Recycle.Bin",                                       "Files deleted but not yet purged",                   true,  "System"),
                T("Hibernation File",     @"C:\hiberfil.sys",                                       "Run powercfg -h off to remove (~RAM size freed)",    false, "System"),
                T("WinSxS Store",         @"C:\Windows\WinSxS",                                    "Run: DISM /Online /Cleanup-Image /StartComponentCleanup", false, "System"),
                // Browsers
                T("Chrome Cache",         Path.Combine(local,"Google","Chrome","User Data","Default","Cache"),       "Chrome web cache",         true, "Browsers"),
                T("Chrome Code Cache",    Path.Combine(local,"Google","Chrome","User Data","Default","Code Cache"),  "Chrome JS compiled cache", true, "Browsers"),
                T("Chrome GPU Cache",     Path.Combine(local,"Google","Chrome","User Data","Default","GPUCache"),    "Chrome GPU shader cache",  true, "Browsers"),
                T("Chrome Crashpad",      Path.Combine(local,"Google","Chrome","User Data","Crashpad"),              "Chrome crash reports",     true, "Browsers"),
                T("Edge Cache",           Path.Combine(local,"Microsoft","Edge","User Data","Default","Cache"),      "Edge web cache",           true, "Browsers"),
                T("Edge Code Cache",      Path.Combine(local,"Microsoft","Edge","User Data","Default","Code Cache"), "Edge JS compiled cache",   true, "Browsers"),
                T("Firefox Profiles",     Path.Combine(roaming,"Mozilla","Firefox","Profiles"),                      "Firefox cache and storage",true, "Browsers"),
                T("Brave Cache",          Path.Combine(local,"BraveSoftware","Brave-Browser","User Data","Default","Cache"), "Brave browser cache", true, "Browsers"),
                T("IE/Legacy Cache",      Path.Combine(local,"Microsoft","Windows","INetCache"),                     "Old IE cache",             true, "Browsers"),
                // Developer
                T("npm Cache",            Path.Combine(roaming,"npm-cache"),                         "Node package manager cache",    true,  "Developer"),
                T("yarn Cache",           Path.Combine(local,"Yarn","Cache"),                        "Yarn package cache",            true,  "Developer"),
                T("pip Cache",            Path.Combine(local,"pip","Cache"),                         "Python pip cache",              true,  "Developer"),
                T("Gradle Cache",         Path.Combine(local,"Gradle"),                              "Android/Java build cache",      true,  "Developer"),
                T("Maven Repository",     Path.Combine(u,".m2"),                                     "Maven local repo",              true,  "Developer"),
                T("NuGet Cache",          Path.Combine(local,"NuGet","Cache"),                       "NuGet package cache",           true,  "Developer"),
                T(".cargo Registry",      Path.Combine(u,".cargo","registry"),                       "Rust Cargo registry",           true,  "Developer"),
                T("JetBrains Cache",      Path.Combine(local,"JetBrains"),                           "JetBrains IDE caches",          true,  "Developer"),
                T("Visual Studio Cache",  Path.Combine(local,"Microsoft","VisualStudio"),            "Visual Studio IDE caches",      true,  "Developer"),
                T("Android SDK",          Path.Combine(local,"Android","Sdk"),                       "Remove unused API levels",      false, "Developer"),
                // Apps
                T("Discord Cache",        Path.Combine(roaming,"discord","Cache"),                   "Discord media cache",           true, "Apps"),
                T("Discord Code Cache",   Path.Combine(roaming,"discord","Code Cache"),              "Discord JS cache",              true, "Apps"),
                T("Teams Cache",          Path.Combine(roaming,"Microsoft","Teams","Cache"),         "Teams media cache",             true, "Apps"),
                T("Teams SW Cache",       Path.Combine(roaming,"Microsoft","Teams","Service Worker","CacheStorage"), "Teams service worker cache", true, "Apps"),
                T("Slack Cache",          Path.Combine(roaming,"Slack","Cache"),                     "Slack media cache",             true, "Apps"),
                T("Zoom Cache",           Path.Combine(roaming,"Zoom","data"),                       "Zoom data cache",               true, "Apps"),
                T("Spotify Cache",        Path.Combine(local,"Spotify","Data"),                      "Spotify offline cache",         true, "Apps"),
                T("Steam AppCache",       @"C:\Program Files (x86)\Steam\appcache",                 "Steam depot cache",             true, "Apps"),
                T("Steam Games",          @"C:\Program Files (x86)\Steam\steamapps\common",         "Installed Steam games",         false,"Apps"),
                T("Epic Launcher Cache",  Path.Combine(local,"EpicGamesLauncher","Saved","webcache"),"Epic web cache",               true, "Apps"),
                T("Battle.net Cache",     Path.Combine(local,"Battle.net","Cache"),                  "Battle.net launcher cache",     true, "Apps"),
                T("Adobe Cache",          Path.Combine(local,"Adobe"),                               "Adobe app caches",              true, "Apps"),
                T("NVIDIA DXCache",       Path.Combine(local,"NVIDIA","DXCache"),                    "NVIDIA shader cache",           true, "Apps"),
                T("AMD DXCache",          Path.Combine(local,"AMD","DXCache"),                       "AMD shader cache",              true, "Apps"),
                T("Office Cache",         Path.Combine(local,"Microsoft","Office"),                  "Office telemetry/doc caches",   true, "Apps"),
                // User Data
                T("Downloads Folder",     Path.Combine(u,"Downloads"),                              "Your downloads — review first!", false,"User Data"),
                T("Desktop Files",        Path.Combine(u,"Desktop"),                                "Files on your desktop",         false,"User Data"),
                T("Videos Folder",        Path.Combine(u,"Videos"),                                 "Recorded/downloaded videos",    false,"User Data"),
                T("OneDrive Local",       Path.Combine(u,"OneDrive"),                               "Move to cloud-only first",      false,"User Data"),
            };
        }
    }
}
