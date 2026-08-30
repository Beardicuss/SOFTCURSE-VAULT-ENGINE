using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SoftcurseVaultCleaner
{
    /// <summary>
    /// Configuration passed from ViewModel checkboxes to control which cleanup tasks run.
    /// </summary>
    public class CleanupConfig
    {
        public bool CleanTempFiles { get; set; } = false;
        public bool CleanCache { get; set; } = false;
        public bool CleanLogs { get; set; } = false;
        public bool CleanRecycleBin { get; set; } = false;
        public bool CleanPrefetch { get; set; } = false;
        public bool DeepScanMode { get; set; } = false;
        public bool UseRecycleBin { get; set; } = true;
        
        // New advanced categories
        public bool CleanDevTools { get; set; } = false;
        public bool CleanGaming { get; set; } = false;
        public bool CleanSystemDumps { get; set; } = false;
        public bool CleanDNS { get; set; } = false;
        public bool CleanExtreme { get; set; } = false;

        public List<string> CustomPaths { get; set; } = new List<string>();

    }

    /// <summary>
    /// Service class responsible for executing system cleanup operations.
    /// Handles file deletion, cache clearing, registry modifications, and service management.
    /// </summary>
    public class CleanerService
    {
        private readonly SafeCleanupEngine _cleanupEngine = new SafeCleanupEngine();
        private readonly PrivilegedMaintenanceService _privilegedMaintenance = new PrivilegedMaintenanceService();
        private CleanupPlan _approvedPlan;
        private volatile bool _abortRequested = false;
        private long _totalSpaceFreed = 0;
        private Action<int> _progressCallback;
        private Action<string> _statusCallback;
        private Action<string> _logCallback;

        public long TotalSpaceFreed => _totalSpaceFreed;

        // DLL imports
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(
            System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [System.Runtime.InteropServices.DllImport("Shell32.dll", EntryPoint = "SHEmptyRecycleBinW",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
        static extern uint SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);
        private const uint SHERB_NOCONFIRMATION = 0x00000001;
        private const uint SHERB_NOPROGRESSUI = 0x00000002;
        private const uint SHERB_NOSOUND = 0x00000004;

        public void RequestAbort()
        {
            _abortRequested = true;
        }

        public CleanupPlan CreateCleanupPlan(CleanupConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            var targets = new List<CleanupTarget>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            void AddDirectory(string id, string name, string path, string reason, string category,
                CleanupRisk risk = CleanupRisk.Moderate,
                CleanupPrivilege privilege = CleanupPrivilege.StandardUser,
                CleanupTargetOrigin origin = CleanupTargetOrigin.BuiltIn)
            {
                if (Directory.Exists(path))
                    targets.Add(new CleanupTarget(id, name, path, reason,
                        CleanupTargetType.DirectoryContents, origin, category, risk, privilege));
            }

            void AddFile(string id, string name, string path, string reason, string category,
                CleanupRisk risk = CleanupRisk.Moderate,
                CleanupPrivilege privilege = CleanupPrivilege.StandardUser)
            {
                if (File.Exists(path))
                    targets.Add(new CleanupTarget(id, name, path, reason,
                        CleanupTargetType.File, CleanupTargetOrigin.BuiltIn, category, risk, privilege));
            }

            void AddChromiumCaches(string browser, string userDataRoot)
            {
                if (!Directory.Exists(userDataRoot)) return;
                foreach (string profileDirectory in Directory.EnumerateDirectories(userDataRoot)
                    .Where(path => Path.GetFileName(path).Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                                   Path.GetFileName(path).StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)))
                {
                    string profileName = Path.GetFileName(profileDirectory);
                    foreach (string cacheName in new[] { "Cache", "Code Cache", "GPUCache", "GrShaderCache", "ShaderCache" })
                        AddDirectory($"cache:browser:{browser}:{profileName}:{cacheName}",
                            $"{browser} {profileName} {cacheName}", Path.Combine(profileDirectory, cacheName),
                            "Browser-generated data that is recreated as needed", "Browsers", CleanupRisk.Low);
                }
            }

            if (config.CleanTempFiles)
            {
                AddDirectory("temp:user", "User TEMP", Path.GetTempPath(), "Per-user temporary files", "Temporary", CleanupRisk.Low);
            }

            if (config.CleanCache)
            {
                string pip = Path.Combine(local, "pip", "Cache");
                if (!Directory.Exists(pip)) pip = Path.Combine(roaming, "pip", "Cache");
                AddDirectory("cache:pip", "Python pip cache", pip, "Package download cache", "Developer", CleanupRisk.Low);

                string explorer = Path.Combine(local, "Microsoft", "Windows", "Explorer");
                if (Directory.Exists(explorer))
                    foreach (string pattern in new[] { "thumbcache_*.db", "iconcache_*.db" })
                        foreach (string file in Directory.EnumerateFiles(explorer, pattern))
                            AddFile($"cache:thumbnail:{file}", "Thumbnail cache", file, "Explorer generated cache", "System", CleanupRisk.Low);

                string packages = Path.Combine(local, "Packages");
                if (Directory.Exists(packages))
                    foreach (string package in Directory.EnumerateDirectories(packages))
                        AddDirectory($"cache:uwp:{Path.GetFileName(package)}", $"{Path.GetFileName(package)} TempState",
                            Path.Combine(package, "TempState"), "Per-app temporary state", "Applications", CleanupRisk.Low);

                AddDirectory("cache:unreal:common-ddc", "Unreal shared derived-data cache",
                    Path.Combine(local, "UnrealEngine", "Common", "DerivedDataCache"),
                    "Generated shaders and derived assets; projects are not removed", "Developer", CleanupRisk.Moderate);
                AddDirectory("cache:android:build", "Android build cache", Path.Combine(profile, ".android", "build-cache"),
                    "Generated Android build artifacts; SDK images are not removed", "Developer", CleanupRisk.Moderate);

                foreach (string path in new[]
                {
                    Path.Combine(local, "NVIDIA", "DXCache"),
                    Path.Combine(local, "AMD", "DXCache"),
                    Path.Combine(local, "Intel", "GfxCache")
                }) AddDirectory($"cache:driver:{path}", "Driver cache", path, "Graphics driver cache", "Drivers");

                AddChromiumCaches("Chrome", Path.Combine(local, "Google", "Chrome", "User Data"));
                AddChromiumCaches("Edge", Path.Combine(local, "Microsoft", "Edge", "User Data"));
                AddChromiumCaches("Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"));
                AddDirectory("cache:inet", "Windows Internet cache", Path.Combine(local, "Microsoft", "Windows", "INetCache"),
                    "Generated web cache", "Browsers", CleanupRisk.Low);

                string firefoxProfiles = Path.Combine(roaming, "Mozilla", "Firefox", "Profiles");
                if (Directory.Exists(firefoxProfiles))
                    foreach (string profileDirectory in Directory.EnumerateDirectories(firefoxProfiles))
                        AddDirectory($"cache:firefox:{profileDirectory}", "Firefox cache2",
                            Path.Combine(profileDirectory, "cache2"), "Firefox generated cache", "Browsers", CleanupRisk.Low);
                if (Directory.Exists(firefoxProfiles))
                    foreach (string profileDirectory in Directory.EnumerateDirectories(firefoxProfiles))
                        AddDirectory($"cache:firefox-startup:{profileDirectory}", "Firefox startup cache",
                            Path.Combine(profileDirectory, "startupCache"), "Firefox generated startup cache", "Browsers", CleanupRisk.Low);
            }

            if (config.CleanDevTools)
                foreach (string path in new[]
                {
                    Path.Combine(roaming, "npm-cache"), Path.Combine(local, "Yarn", "Cache"),
                    Path.Combine(profile, ".nuget", "packages"), Path.Combine(profile, ".gradle", "caches"),
                    Path.Combine(profile, ".m2", "repository")
                }) AddDirectory($"dev:{path}", "Developer cache", path, "Developer dependency/cache data", "Developer", CleanupRisk.Moderate);

            if (config.CleanGaming)
            {
                var applicationCaches = new (string Name, string Path)[]
                {
                    ("Discord cache", Path.Combine(roaming, "discord", "Cache")),
                    ("Discord code cache", Path.Combine(roaming, "discord", "Code Cache")),
                    ("Discord GPU cache", Path.Combine(roaming, "discord", "GPUCache")),
                    ("Teams cache", Path.Combine(roaming, "Microsoft", "Teams", "Cache")),
                    ("Teams code cache", Path.Combine(roaming, "Microsoft", "Teams", "Code Cache")),
                    ("Teams GPU cache", Path.Combine(roaming, "Microsoft", "Teams", "GPUCache")),
                    ("Teams service-worker cache", Path.Combine(roaming, "Microsoft", "Teams", "Service Worker", "CacheStorage")),
                    ("Slack cache", Path.Combine(roaming, "Slack", "Cache")),
                    ("Slack code cache", Path.Combine(roaming, "Slack", "Code Cache")),
                    ("Slack GPU cache", Path.Combine(roaming, "Slack", "GPUCache")),
                    ("Battle.net cache", Path.Combine(local, "Battle.net", "Cache")),
                    ("Steam HTML cache", Path.Combine(local, "Steam", "htmlcache")),
                    ("Steam app HTTP cache", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "appcache", "httpcache"))
                };
                foreach (var item in applicationCaches)
                    AddDirectory($"application:{item.Name}:{item.Path}", item.Name, item.Path,
                        "Generated application cache; account and installed game data are retained", "Applications", CleanupRisk.Moderate);

                string epicSaved = Path.Combine(local, "EpicGamesLauncher", "Saved");
                if (Directory.Exists(epicSaved))
                    foreach (string webCache in Directory.EnumerateDirectories(epicSaved, "webcache*"))
                        AddDirectory($"application:epic:{webCache}", "Epic Launcher web cache", webCache,
                            "Generated launcher web cache", "Applications", CleanupRisk.Moderate);
            }

            if (config.CleanSystemDumps)
            {
                foreach (string path in new[] { Path.Combine(local, "CrashDumps"),
                    Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive"),
                    Path.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue") })
                    AddDirectory($"dump:{path}", "System dump directory", path, "Crash diagnostic data", "Diagnostics", CleanupRisk.Moderate);
            }

            if (config.CleanExtreme)
            {
                AddDirectory("extreme:recent", "Explorer recent files", Path.Combine(roaming, "Microsoft", "Windows", "Recent"), "Recent-item history", "Extreme", CleanupRisk.High);
                AddFile("extreme:icon", "IconCache.db", Path.Combine(local, "IconCache.db"), "Explorer icon cache", "Extreme");
            }

            foreach (string path in config.CustomPaths ?? new List<string>())
                AddDirectory($"custom:{path}", "Custom folder", Environment.ExpandEnvironmentVariables(path.Trim()),
                    "User-selected cleanup folder", "Custom", CleanupRisk.High,
                    CleanupPrivilege.StandardUser, CleanupTargetOrigin.UserSelected);

            // A repeated custom path or overlapping category must never produce duplicate
            // preview/execution entries. All discovered targets exist at this point, so
            // canonicalization is deterministic and cannot turn a missing path into work.
            CleanupTarget[] uniqueTargets = targets
                .GroupBy(
                    target => $"{target.Type}\0{Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Path))}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();

            return CleanupPlan.Create($"cleanup:{Guid.NewGuid():N}", uniqueTargets);
        }

        public async Task ExecuteCleanupAsync(
            Action<int> progressCallback,
            Action<string> statusCallback,
            Action<string> logCallback,
            CleanupConfig config,
            CleanupPlan approvedPlan,
            CancellationToken token = default)
        {
            _abortRequested = false;
            _totalSpaceFreed = 0;
            _progressCallback = progressCallback;
            _statusCallback = statusCallback;
            _logCallback = logCallback;
            _approvedPlan = approvedPlan ?? throw new ArgumentNullException(nameof(approvedPlan));

            await Task.Run(async () => await ExecuteCleanupProtocol(config, token), token);
        }

        private async Task ExecuteCleanupProtocol(CleanupConfig config, CancellationToken token = default)
        {
            bool ShouldStop() => _abortRequested || token.IsCancellationRequested;
            LogStatus("=== INITIATING CLEANUP PROTOCOL ===");
            UpdateStatus("INITIATING CLEANUP SEQUENCE");

            // Execute the exact immutable plan the user previewed. Keeping discovery and
            // execution in one catalog prevents target drift and duplicate cleanup logic.
            var tasks = new List<(string Name, Func<Task> Task)>();
            if (_approvedPlan.Targets.Count > 0)
                tasks.Add(("Confirmed filesystem cleanup", async () =>
                {
                    CleanupExecutionResult result = await _cleanupEngine.ExecuteAsync(_approvedPlan, token);
                    foreach (CleanupItemResult item in result.Items)
                    {
                        if (item.Succeeded)
                        {
                            Interlocked.Add(ref _totalSpaceFreed, item.BytesFreed);
                            LogStatus($"MOVED: {item.Target.DisplayName} ({item.BytesFreed / (1024.0 * 1024.0):N1} MB to Recycle Bin)");
                        }
                        else
                            LogStatus($"{(item.WasSkipped ? "SKIPPED" : "FAILED")}: {item.Target.DisplayName} - {item.Message}");
                    }
                }));

            if (config.CleanDNS)
                tasks.Add(("DNS Cache Flush", FlushDNSCacheAsync));

            if (config.DeepScanMode)
                tasks.Add(("Supported Windows Component Cleanup", RunDISMCleanupAsync));

            // Empty last so files moved during this run are actually reclaimed.
            if (config.CleanRecycleBin)
                tasks.Add(("Empty Recycle Bin", () => { CleanRecycleBin(); return Task.CompletedTask; }));

            // Execute tasks with evenly distributed progress (5% to 95%)
            int totalTasks = tasks.Count;
            for (int i = 0; i < totalTasks; i++)
            {
                if (ShouldStop()) break;

                int progress = totalTasks > 1
                    ? 5 + (int)((i / (double)(totalTasks - 1)) * 90)
                    : 50;

                await ExecuteTaskAsync(tasks[i].Name, tasks[i].Task, progress);
            }

            if (!ShouldStop())
            {
                double freedMB = _totalSpaceFreed / (1024.0 * 1024.0);
                LogStatus("=== CLEANUP PROTOCOL COMPLETE ===");
                LogStatus(config.CleanRecycleBin
                    ? $"SYSTEM: {freedMB:N0} MB moved, then Recycle Bin emptied"
                    : $"SYSTEM: {freedMB:N0} MB moved to Recycle Bin (recoverable; disk space is reclaimed when it is emptied)");
                UpdateStatus("CLEANUP PROTOCOL SUCCESSFUL");
                UpdateProgress(100);
            }
            else
            {
                LogStatus("=== CLEANUP PROTOCOL ABORTED ===");
                UpdateStatus("MISSION ABORTED BY USER");
            }
        }

        private async Task ExecuteTaskAsync(string taskName, Func<Task> task, int progress)
        {
            if (_abortRequested) return;

            UpdateProgress(progress);
            UpdateStatus($"EXECUTING: {taskName}");
            LogStatus($"EXECUTING: {taskName}");
            try
            {
                await task();
                LogStatus($"COMPLETED: {taskName}");
            }
            catch (Exception ex)
            {
                LogStatus($"FAILED: {taskName} - {ex.Message}");
            }
        }

        // Cleanup methods
        private void CleanRecycleBin()
        {
            try
            {
                uint result = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI);
                if (result == 0)
                    LogStatus("RECYCLE BIN: Emptied successfully");
                else
                    LogStatus("RECYCLE BIN: Cleanup completed (may have been empty)");
            }
            catch (Exception ex)
            {
                LogStatus($"RECYCLE BIN: Failed - {ex.Message}");
            }
        }

        private void CleanTempFolders()
        {
            CleanDirectory(Path.GetTempPath(), "User TEMP");
        }

        private void CleanPipCache()
        {
            try
            {
                string pipCachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pip", "Cache");
                if (!Directory.Exists(pipCachePath))
                {
                    pipCachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pip", "Cache");
                }
                CleanDirectory(pipCachePath, "Python (pip) Cache");
            }
            catch (Exception ex) { LogStatus($"Pip cache cleanup failed: {ex.Message}"); }
        }

        private void CleanMicrosoftStoreCache()
        {
            try
            {
                LogStatus("MICROSOFT STORE: Cleaning UWP temp caches directly.");

                string appPackageTempPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
                string tempStatePath = Path.Combine(appPackageTempPath, "TempState");
                CleanDirectory(tempStatePath, "UWP App Temporary State");
            }
            catch (Exception ex) { LogStatus($"Microsoft Store cache failed: {ex.Message}"); }
        }

        private void CleanUnrealEngineCache()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnrealEngine");
            CleanDirectory(path, "Unreal Engine Cache");
        }

        private void CleanAndroidSDK()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "system-images");
            CleanDirectory(path, "Android SDK");
        }

        private void CleanBrowserCaches()
        {
            var browserCaches = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data", "Default", "Cache"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data", "Default", "Cache"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BraveSoftware", "Brave-Browser", "User Data", "Default", "Cache")
            };

            foreach (string cache in browserCaches)
            {
                CleanDirectory(cache, $"Browser cache: {Path.GetFileName(Path.GetDirectoryName(cache))}");
            }

            string firefoxPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox", "Profiles");
            if (Directory.Exists(firefoxPath))
            {
                foreach (string profile in Directory.GetDirectories(firefoxPath))
                {
                    string cache2 = Path.Combine(profile, "cache2");
                    if (Directory.Exists(cache2))
                    {
                        CleanDirectory(cache2, $"Firefox cache: {Path.GetFileName(profile)}");
                    }
                }
            }
        }

        private async Task RunDISMCleanupAsync()
        {
            PrivilegedMaintenanceResult result = await _privilegedMaintenance.RunComponentCleanupAsync();
            LogStatus(result.Message);
            if (!result.Succeeded && !result.Cancelled)
                throw new InvalidOperationException(result.Message);
        }

        private void CleanDriverCachesTask()
        {
            var paths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA", "DXCache"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMD", "DXCache"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Intel", "GfxCache")
            };

            foreach (string path in paths)
            {
                CleanDirectory(path, $"Driver cache: {Path.GetFileName(path)}");
            }
        }

        private void CleanThumbnailCache()
        {
            LogStatus("Cleaning thumbnail cache...");
            try
            {
                string thumbCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Explorer");
                if (Directory.Exists(thumbCache))
                {
                    int deleted = 0;
                    long bytesFreed = 0;
                    var patterns = new[] { "thumbcache_*.db", "iconcache_*.db" };
                    foreach (string pattern in patterns)
                    {
                        foreach (string file in Directory.EnumerateFiles(thumbCache, pattern))
                        {
                            if (_abortRequested) return;
                            try
                            {
                                long freed = DeleteFileSafely(file, "Thumbnail cache file");
                                if (freed > 0) { deleted++; bytesFreed += freed; }
                            }
                            catch { }
                        }
                    }
                    double freedMB = bytesFreed / (1024.0 * 1024.0);
                    LogStatus($"Thumbnail cache: deleted {deleted} files ({freedMB:N1} MB)");
                }
            }
            catch (Exception ex) { LogStatus($"Thumbnail cache cleanup failed: {ex.Message}"); }
        }

        private void CleanDevToolsCaches()
        {
            var paths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm-cache"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yarn", "Cache"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gradle", "caches"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".m2", "repository")
            };

            foreach (string path in paths)
            {
                CleanDirectory(path, $"DevTools cache: {Path.GetFileName(path)}");
            }
        }

        private void CleanGamingCaches()
        {
            var paths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discord", "Cache"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discord", "Code Cache"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved", "webcache"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spotify", "Data"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Teams", "Cache")
            };

            foreach (string path in paths)
            {
                CleanDirectory(path, $"Gaming/App cache: {Path.GetFileName(path)}");
            }
        }

        private void CleanSystemDumps()
        {
            var paths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "WER")
            };

            foreach (string path in paths)
            {
                CleanDirectory(path, $"System Dump: {Path.GetFileName(path)}");
            }
        }

        private async Task FlushDNSCacheAsync()
        {
            try
            {
                await RunCommandAsync("ipconfig.exe", "/flushdns");
                LogStatus("DNS resolver cache flushed successfully");
            }
            catch (Exception ex) { LogStatus($"DNS flush failed: {ex.Message}"); }
        }

        private void CleanCustomPaths(List<string> paths)
        {
            foreach (string path in paths)
            {
                if (_abortRequested) return;

                string expandedPath = Environment.ExpandEnvironmentVariables(path.Trim());
                if (Directory.Exists(expandedPath))
                {
                    CleanDirectory(expandedPath, $"Custom: {expandedPath}", CleanupTargetOrigin.UserSelected);
                }
                else
                {
                    LogStatus($"SKIPPED: Custom path not found - {expandedPath}");
                }
            }
        }

        // Helper methods

        /// <summary>
        /// Routes directory cleanup through the centralized Phase 1 safety boundary.
        /// Contents are moved to the Recycle Bin and protected/reparse targets fail closed.
        /// </summary>
        private void CleanDirectory(
            string path,
            string description,
            CleanupTargetOrigin origin = CleanupTargetOrigin.BuiltIn)
        {
            if (!Directory.Exists(path))
            {
                LogStatus($"SKIPPED: {description} - Path not found");
                return;
            }

            if (_abortRequested) return;

            var target = new CleanupTarget(
                $"cleaner:{description}", description, path, description,
                CleanupTargetType.DirectoryContents, origin);
            if (!IsInApprovedPlan(target))
            {
                LogStatus($"BLOCKED: {description} was not present in the confirmed cleanup plan.");
                return;
            }
            var result = _cleanupEngine.ExecuteAsync(new[] { target }).GetAwaiter().GetResult();
            var item = result.Items.FirstOrDefault();
            if (item?.Succeeded == true)
            {
                Interlocked.Add(ref _totalSpaceFreed, item.BytesFreed);
                LogStatus($"CLEANED: {description} ({item.BytesFreed / (1024.0 * 1024.0):N1} MB moved to Recycle Bin)");
            }
            else
            {
                LogStatus($"BLOCKED/FAILED: {description} - {item?.Message ?? "No result returned"}");
            }
        }

        private long DeleteFileSafely(
            string path,
            string description,
            CleanupTargetOrigin origin = CleanupTargetOrigin.BuiltIn)
        {
            var target = new CleanupTarget(
                $"cleaner-file:{description}:{path}", description, path, description,
                CleanupTargetType.File, origin);
            if (!IsInApprovedPlan(target))
            {
                LogStatus($"BLOCKED: {description} was not present in the confirmed cleanup plan.");
                return 0;
            }
            var result = _cleanupEngine.ExecuteAsync(new[] { target }).GetAwaiter().GetResult();
            var item = result.Items.FirstOrDefault();
            if (item?.Succeeded == true)
            {
                Interlocked.Add(ref _totalSpaceFreed, item.BytesFreed);
                return item.BytesFreed;
            }

            LogStatus($"BLOCKED/FAILED: {description} - {item?.Message ?? "No result returned"}");
            return 0;
        }

        private bool IsInApprovedPlan(CleanupTarget target)
        {
            if (_approvedPlan == null) return false;
            string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(target.Path)));
            return _approvedPlan.Targets.Any(approved =>
                approved.Type == target.Type &&
                string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                        Environment.ExpandEnvironmentVariables(approved.Path))),
                    canonical,
                    StringComparison.OrdinalIgnoreCase));
        }

        private long CalculateDirectorySize(string path)
        {
            return CalculateDirectorySizeSafe(new DirectoryInfo(path));
        }

        private long CalculateDirectorySizeSafe(DirectoryInfo dir)
        {
            long size = 0;
            try
            {
                // Process current directory files
                FileInfo[] files = dir.GetFiles();
                foreach (FileInfo fi in files)
                {
                    try { size += fi.Length; } catch { }
                }

                // Recurse into subdirectories
                DirectoryInfo[] dirs = dir.GetDirectories();
                foreach (DirectoryInfo subDir in dirs)
                {
                    // Ignore reparse points (symlinks/junctions) to avoid infinite loops
                    if ((subDir.Attributes & FileAttributes.ReparsePoint) != FileAttributes.ReparsePoint)
                    {
                        size += CalculateDirectorySizeSafe(subDir);
                    }
                }
            }
            catch
            {
                // If unauthorized access to this specific dir, ignore and continue tree
            }
            return size;
        }

        private Task CleanExtremeTasksAsync()
        {
            LogStatus("EXPLORER PRIVACY CLEANUP INITIALIZED...");
            string recentFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Recent");
            if (Directory.Exists(recentFiles))
            {
                CleanDirectory(recentFiles, "Explorer Recent Files");
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string iconCacheFile = Path.Combine(localAppData, "IconCache.db");
            if (File.Exists(iconCacheFile))
            {
                try
                {
                    long freed = DeleteFileSafely(iconCacheFile, "IconCache.db");
                    if (freed > 0) LogStatus("IconCache.db moved to Recycle Bin");
                }
                catch { }
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Runs an external command with proper async output handling to prevent deadlocks.
        /// </summary>
        private async Task RunCommandAsync(string fileName, string arguments)
        {
            try
            {
                using (Process process = new Process())
                {
                    var stdout = new StringBuilder();
                    var stderr = new StringBuilder();

                    process.StartInfo.FileName = fileName;
                    process.StartInfo.Arguments = arguments;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;

                    process.OutputDataReceived += (s, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    // Use async wait with timeout via CancellationTokenSource
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    try
                    {
                        await process.WaitForExitAsync(timeoutCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        try { process.Kill(); } catch { }
                        LogStatus($"COMMAND TIMEOUT: {fileName} {arguments}");
                        return;
                    }

                    if (stderr.Length > 0 && process.ExitCode != 0)
                    {
                        LogStatus($"COMMAND ERROR: {fileName} - {stderr.ToString().Trim()}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogStatus($"COMMAND FAILED: {fileName} {arguments} - {ex.Message}");
            }
        }

        private void UpdateProgress(int percent)
        {
            _progressCallback?.Invoke(percent);
        }

        private void UpdateStatus(string message)
        {
            _statusCallback?.Invoke(message);
        }

        private void LogStatus(string message)
        {
            if (_logCallback != null)
                _logCallback.Invoke(message);
            else
                _statusCallback?.Invoke(message);
        }
    }
}
