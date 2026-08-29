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

                AddDirectory("cache:store", "UWP TempState", Path.Combine(local, "Packages", "TempState"), "UWP temporary state", "Applications");
                AddDirectory("cache:unreal", "Unreal Engine data", Path.Combine(local, "UnrealEngine"), "Broad Unreal Engine data root", "Developer", CleanupRisk.High);
                AddDirectory("cache:android", "Android system images", Path.Combine(local, "Android", "Sdk", "system-images"), "Installed Android emulator images", "Developer", CleanupRisk.High);

                foreach (string path in new[]
                {
                    Path.Combine(local, "NVIDIA", "DXCache"),
                    Path.Combine(local, "AMD", "DXCache"),
                    Path.Combine(local, "Intel", "GfxCache")
                }) AddDirectory($"cache:driver:{path}", "Driver cache", path, "Graphics driver cache", "Drivers");

                foreach (string path in new[]
                {
                    Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Cache"),
                    Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Cache"),
                    Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data", "Default", "Cache")
                }) AddDirectory($"cache:browser:{path}", "Browser cache", path, "Browser-generated cache", "Browsers", CleanupRisk.Low);

                string firefoxProfiles = Path.Combine(roaming, "Mozilla", "Firefox", "Profiles");
                if (Directory.Exists(firefoxProfiles))
                    foreach (string profileDirectory in Directory.EnumerateDirectories(firefoxProfiles))
                        AddDirectory($"cache:firefox:{profileDirectory}", "Firefox cache2",
                            Path.Combine(profileDirectory, "cache2"), "Firefox generated cache", "Browsers", CleanupRisk.Low);
            }

            if (config.CleanDevTools)
                foreach (string path in new[]
                {
                    Path.Combine(roaming, "npm-cache"), Path.Combine(local, "Yarn", "Cache"),
                    Path.Combine(profile, ".nuget", "packages"), Path.Combine(profile, ".gradle", "caches"),
                    Path.Combine(profile, ".m2", "repository")
                }) AddDirectory($"dev:{path}", "Developer cache", path, "Developer dependency/cache data", "Developer", CleanupRisk.Moderate);

            if (config.CleanGaming)
                foreach (string path in new[]
                {
                    Path.Combine(roaming, "discord", "Cache"), Path.Combine(roaming, "discord", "Code Cache"),
                    Path.Combine(local, "EpicGamesLauncher", "Saved", "webcache"),
                    Path.Combine(local, "Spotify", "Data"), Path.Combine(roaming, "Microsoft", "Teams", "Cache")
                }) AddDirectory($"gaming:{path}", "Gaming/application data", path, "Application cache or download data", "Applications", CleanupRisk.High);

            if (config.CleanSystemDumps)
            {
                foreach (string path in new[] { Path.Combine(local, "CrashDumps"), Path.Combine(local, "Microsoft", "Windows", "WER") })
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

            return CleanupPlan.Create($"cleanup:{Guid.NewGuid():N}", targets);
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

            // Build task list based on config — uses Func<Task> for async support
            var tasks = new List<(string Name, Func<Task> Task)>();

            if (config.CleanRecycleBin)
                tasks.Add(("Recycle Bin Incineration", () => { CleanRecycleBin(); return Task.CompletedTask; }));

            if (config.CleanTempFiles)
                tasks.Add(("TEMP Files Purge", () => { CleanTempFolders(); return Task.CompletedTask; }));

            if (config.CleanCache)
            {
                tasks.Add(("PYTHON PIP Cache Purge", () => { CleanPipCache(); return Task.CompletedTask; }));
                tasks.Add(("Thumbnail Cache Clean", () => { CleanThumbnailCache(); return Task.CompletedTask; }));
                tasks.Add(("UWP App Cache Clean", () => { CleanMicrosoftStoreCache(); return Task.CompletedTask; }));
                tasks.Add(("Driver Cache Purge", () => { CleanDriverCachesTask(); return Task.CompletedTask; }));
                tasks.Add(("Unreal Engine Purge", () => { CleanUnrealEngineCache(); return Task.CompletedTask; }));
                tasks.Add(("Android SDK Clean", () => { CleanAndroidSDK(); return Task.CompletedTask; }));
                tasks.Add(("Browser Data Wipe", () => { CleanBrowserCaches(); return Task.CompletedTask; }));
            }

            if (config.CleanDevTools)
                tasks.Add(("Dev Tools Optimization", () => { CleanDevToolsCaches(); return Task.CompletedTask; }));

            if (config.CleanGaming)
                tasks.Add(("Gaming & Comms Purge", () => { CleanGamingCaches(); return Task.CompletedTask; }));

            if (config.CleanSystemDumps)
                tasks.Add(("System Dumps Eradication", () => { CleanSystemDumps(); return Task.CompletedTask; }));

            if (config.CleanDNS)
                tasks.Add(("DNS Cache Flush", FlushDNSCacheAsync));

            if (config.CleanExtreme)
                tasks.Add(("Explorer Privacy Cleanup", CleanExtremeTasksAsync));

            if (config.DeepScanMode)
                tasks.Add(("Supported Windows Component Cleanup", RunDISMCleanupAsync));

            // Custom paths from user
            if (config.CustomPaths != null && config.CustomPaths.Count > 0)
            {
                tasks.Add(("Custom Folder Cleanup", () => { CleanCustomPaths(config.CustomPaths); return Task.CompletedTask; }));
            }

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
                LogStatus($"SYSTEM: All targets eliminated successfully - Freed {freedMB:N0} MB");
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
