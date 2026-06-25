using Microsoft.Win32;
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
        public bool CleanTempFiles { get; set; } = true;
        public bool CleanCache { get; set; } = true;
        public bool CleanLogs { get; set; } = true;
        public bool CleanRecycleBin { get; set; } = false;
        public bool CleanPrefetch { get; set; } = true;
        public bool DeepScanMode { get; set; } = false;
        public bool UseRecycleBin { get; set; } = false;
        
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
        private volatile bool _abortRequested = false;
        private long _totalSpaceFreed = 0;
        private Action<int> _progressCallback;
        private Action<string> _statusCallback;
        private Action<string> _logCallback;

        public long TotalSpaceFreed => _totalSpaceFreed;

        // Configuration fields
        private int requiredFreeGB = 20;
        private int pagefileMarginGB = 5;

        // DLL imports
        [System.Runtime.InteropServices.DllImport("Shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        static extern uint SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);
        private const uint SHERB_NOCONFIRMATION = 0x00000001;
        private const uint SHERB_NOPROGRESSUI = 0x00000002;
        private const uint SHERB_NOSOUND = 0x00000004;

        public void RequestAbort()
        {
            _abortRequested = true;
        }

        public async Task ExecuteCleanupAsync(Action<int> progressCallback, Action<string> statusCallback, Action<string> logCallback, CleanupConfig config, CancellationToken token = default)
        {
            _abortRequested = false;
            _totalSpaceFreed = 0;
            _progressCallback = progressCallback;
            _statusCallback = statusCallback;
            _logCallback = logCallback;

            await Task.Run(async () => await ExecuteCleanupProtocol(config, token), token);
        }

        private async Task ExecuteCleanupProtocol(CleanupConfig config, CancellationToken token = default)
        {
            bool ShouldStop() => _abortRequested || token.IsCancellationRequested;
            LogStatus("=== INITIATING CLEANUP PROTOCOL ===");
            UpdateStatus("INITIATING CLEANUP SEQUENCE");

            var selectedDrive = SelectTargetDrive();
            if (selectedDrive != null)
            {
                LogStatus($"SELECTED TARGET DRIVE: {selectedDrive.DriveLetter}: (Free: {selectedDrive.FreeGB:F2} GB)");
            }
            else
            {
                LogStatus("WARNING: No suitable target drive found. Pagefile/restore relocation skipped.");
            }

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
                tasks.Add(("Windows Update Cache Flush", CleanWindowsUpdateCacheAsync));
                tasks.Add(("UWP App Cache Clean", () => { CleanMicrosoftStoreCache(); return Task.CompletedTask; }));
                tasks.Add(("Driver Cache Purge", () => { CleanDriverCachesTask(); return Task.CompletedTask; }));
                tasks.Add(("Unreal Engine Purge", () => { CleanUnrealEngineCache(); return Task.CompletedTask; }));
                tasks.Add(("Android SDK Clean", () => { CleanAndroidSDK(); return Task.CompletedTask; }));
                tasks.Add(("Browser Data Wipe", () => { CleanBrowserCaches(); return Task.CompletedTask; }));
            }

            if (config.CleanLogs)
                tasks.Add(("Event Log Scrub", () => { CleanEventLogs(); return Task.CompletedTask; }));

            if (config.CleanPrefetch)
            {
                tasks.Add(("Font Cache Rebuild", RebuildFontCacheAsync));
                tasks.Add(("Prefetch Flush", () => { CleanPrefetchFiles(); return Task.CompletedTask; }));
            }

            if (config.CleanDevTools)
                tasks.Add(("Dev Tools Optimization", () => { CleanDevToolsCaches(); return Task.CompletedTask; }));

            if (config.CleanGaming)
                tasks.Add(("Gaming & Comms Purge", () => { CleanGamingCaches(); return Task.CompletedTask; }));

            if (config.CleanSystemDumps)
                tasks.Add(("System Dumps Eradication", () => { CleanSystemDumps(); return Task.CompletedTask; }));

            if (config.CleanDNS)
                tasks.Add(("DNS & Net Cache Flush", FlushDNSCacheAsync));

            if (config.CleanExtreme)
                tasks.Add(("Extreme System Wipe", CleanExtremeTasksAsync));

            if (config.DeepScanMode)
            {
                tasks.Add(("DISM Cleanup", RunDISMCleanupAsync));
                tasks.Add(("Orphaned Installer Removal", () => { RemoveOrphanedInstallersTask(); return Task.CompletedTask; }));
            }

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

            // Pagefile and System Restore (only with deep scan + confirmation already in UI)
            if (!ShouldStop() && selectedDrive != null && config.DeepScanMode)
            {
                await ExecuteTaskAsync("Pagefile Configuration", () => { ConfigurePagefile(selectedDrive.DriveLetter); return Task.CompletedTask; }, 98);
                await ExecuteTaskAsync("System Restore Relocation", () => MoveSystemRestoreAsync(selectedDrive.DriveLetter), 99);
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
            CleanDirectory(@"C:\Windows\Temp", "System TEMP");
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

        private async Task CleanWindowsUpdateCacheAsync()
        {
            try
            {
                await StopServiceAsync("wuauserv");
                await StopServiceAsync("bits");
                await StopServiceAsync("dosvc"); // Delivery Optimization
                
                CleanDirectory(@"C:\Windows\SoftwareDistribution\Download", "Windows Update Download Cache");
                CleanDirectory(@"C:\Windows\SoftwareDistribution\DataStore", "Windows Update DataStore");
                CleanDirectory(@"C:\Windows\ServiceProfiles\LocalService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache", "Delivery Optimization Cache");
                
                // Try to remove upgrade folders if they exist
                if (Directory.Exists(@"C:\$WINDOWS.~BT"))
                {
                    CleanDirectory(@"C:\$WINDOWS.~BT", "Windows Upgrade Artifacts (~BT)");
                    try { Directory.Delete(@"C:\$WINDOWS.~BT", true); } catch { }
                }
                if (Directory.Exists(@"C:\Windows.old"))
                {
                    LogStatus("Found Windows.old directory. Warning: system rollback will no longer be possible once deleted.");
                    // Requires extensive permissions, sometimes taking ownership is needed, but we try standard deletion since we are now Admin
                    CleanDirectory(@"C:\Windows.old", "Windows Old Backup");
                }

                await StartServiceAsync("dosvc");
                await StartServiceAsync("bits");
                await StartServiceAsync("wuauserv");
            }
            catch (Exception ex) { LogStatus($"Windows Update cache failed: {ex.Message}"); }
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

        private void CleanEventLogs()
        {
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = "wevtutil.exe";
                    process.StartInfo.Arguments = "el";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                    process.Start();

                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(15000);

                    foreach (string log in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (_abortRequested) break;

                        try
                        {
                            using (Process clearProcess = new Process())
                            {
                                clearProcess.StartInfo.FileName = "wevtutil.exe";
                                clearProcess.StartInfo.Arguments = $"cl \"{log}\"";
                                clearProcess.StartInfo.UseShellExecute = false;
                                clearProcess.StartInfo.RedirectStandardOutput = true;
                                clearProcess.StartInfo.RedirectStandardError = true;
                                clearProcess.StartInfo.CreateNoWindow = true;
                                clearProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                                clearProcess.Start();
                                if (!clearProcess.WaitForExit(2000))
                                {
                                    try { clearProcess.Kill(); } catch { }
                                }
                            }
                            Thread.Sleep(5);
                        }
                        catch (Exception ex)
                        {
                            LogStatus($"Failed to clear log {log}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex) { LogStatus($"Event logs failed: {ex.Message}"); }
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
            try
            {
                await RunCommandAsync("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup");
                await RunCommandAsync("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup /ResetBase");
            }
            catch (Exception ex) { LogStatus($"DISM failed: {ex.Message}"); }
        }

        private void CleanDriverCachesTask()
        {
            var paths = new[]
            {
                @"C:\ProgramData\NVIDIA Corporation\Downloader",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA", "DXCache"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMD", "DXCache"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Intel", "GfxCache"),
                @"C:\ProgramData\NVIDIA Corporation\NV_Cache"
            };

            foreach (string path in paths)
            {
                CleanDirectory(path, $"Driver cache: {Path.GetFileName(path)}");
            }
        }

        private void RemoveOrphanedInstallersTask()
        {
            RemoveOrphanedInstallers(@"C:\Windows\Installer");
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
                                long sz = new FileInfo(file).Length;
                                File.Delete(file);
                                deleted++;
                                bytesFreed += sz;
                            }
                            catch { }
                        }
                    }
                    Interlocked.Add(ref _totalSpaceFreed, bytesFreed);
                    double freedMB = bytesFreed / (1024.0 * 1024.0);
                    LogStatus($"Thumbnail cache: deleted {deleted} files ({freedMB:N1} MB)");
                }
            }
            catch (Exception ex) { LogStatus($"Thumbnail cache cleanup failed: {ex.Message}"); }
        }

        private async Task RebuildFontCacheAsync()
        {
            LogStatus("Rebuilding font cache...");
            try
            {
                await StopServiceAsync("FontCache");
                await Task.Delay(2000);

                string fontCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "FontCache");
                if (Directory.Exists(fontCache))
                {
                    CleanDirectory(fontCache, "Font Cache");
                }

                await StartServiceAsync("FontCache");
                LogStatus("Font cache rebuilt successfully using service");
            }
            catch (Exception ex)
            {
                LogStatus($"Font cache rebuild failed: {ex.Message}");
                // Note: SFC removed — it's a full system scan, not a font cache tool
                LogStatus("Font cache service unavailable. Cache will rebuild on next reboot.");
            }
        }

        private void CleanPrefetchFiles()
        {
            LogStatus("Cleaning Prefetch files...");
            try
            {
                CleanDirectory(@"C:\Windows\Prefetch", "Windows Prefetch");
            }
            catch (Exception ex) { LogStatus($"Prefetch cleanup failed: {ex.Message}"); }
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
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Teams", "Cache"),
                @"C:\Program Files (x86)\Steam\steamapps\downloading"
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
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "WER"),
                @"C:\Windows\Minidump"
            };

            foreach (string path in paths)
            {
                CleanDirectory(path, $"System Dump: {Path.GetFileName(path)}");
            }
            
            string memDump = @"C:\Windows\MEMORY.DMP";
            if (File.Exists(memDump))
            {
                try
                {
                    long sz = new FileInfo(memDump).Length;
                    File.Delete(memDump);
                    Interlocked.Add(ref _totalSpaceFreed, sz);
                    LogStatus($"CLEANED: System Dump: MEMORY.DMP ({sz / (1024.0 * 1024.0):N1} MB)");
                }
                catch (Exception ex)
                {
                    LogStatus($"FAILED: System Dump: MEMORY.DMP - {ex.Message}");
                }
            }
        }

        private async Task FlushDNSCacheAsync()
        {
            try
            {
                await RunCommandAsync("ipconfig.exe", "/flushdns");
                await RunCommandAsync("netsh.exe", "interface ip delete arpcache");
                LogStatus("DNS and ARP caches flushed successfully");
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
                    CleanDirectory(expandedPath, $"Custom: {expandedPath}");
                }
                else
                {
                    LogStatus($"SKIPPED: Custom path not found - {expandedPath}");
                }
            }
        }

        // Helper methods

        /// <summary>
        /// Cleans a directory by deleting all files and subdirectories.
        /// Tracks actual bytes freed by measuring file sizes before deletion.
        /// </summary>
        private void CleanDirectory(string path, string description)
        {
            if (!Directory.Exists(path))
            {
                LogStatus($"SKIPPED: {description} - Path not found");
                return;
            }

            if (_abortRequested) return;

            try
            {
                int filesDeleted = 0;
                int dirsDeleted = 0;
                long bytesFreed = 0;

                // Use EnumerateFiles to reduce memory usage
                foreach (string file in Directory.EnumerateFiles(path))
                {
                    if (_abortRequested) return;

                    try
                    {
                        long fileSize = new FileInfo(file).Length;
                        File.Delete(file);
                        filesDeleted++;
                        bytesFreed += fileSize;
                    }
                    catch { }

                    if (filesDeleted % 100 == 0) Thread.Sleep(1);
                }

                foreach (string dir in Directory.EnumerateDirectories(path))
                {
                    if (_abortRequested) return;

                    try
                    {
                        long dirSize = CalculateDirectorySize(dir);
                        Directory.Delete(dir, true);
                        dirsDeleted++;
                        bytesFreed += dirSize;
                    }
                    catch { }

                    if (dirsDeleted % 10 == 0) Thread.Sleep(1);
                }

                Interlocked.Add(ref _totalSpaceFreed, bytesFreed);
                double freedMB = bytesFreed / (1024.0 * 1024.0);
                LogStatus($"CLEANED: {description} ({filesDeleted} files, {dirsDeleted} folders, {freedMB:N1} MB)");
            }
            catch (Exception ex)
            {
                LogStatus($"FAILED: {description} - {ex.Message}");
            }
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

        private async Task CleanExtremeTasksAsync()
        {
            LogStatus("EXTREME MODE INITIALIZED...");

            // 1. Docker Prune
            try
            {
                await RunCommandAsync("docker.exe", "system prune -a -f --volumes");
                LogStatus("Docker Prune Complete");
            }
            catch { }

            // 2. Windows Defender History
            string defenderHistory = @"C:\ProgramData\Microsoft\Windows Defender\Scans\History\Service\DetectionHistory";
            if (Directory.Exists(defenderHistory))
            {
                CleanDirectory(defenderHistory, "Defender Scan History");
            }

            // 3. Explorer Privacy
            string recentFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Recent");
            if (Directory.Exists(recentFiles))
            {
                CleanDirectory(recentFiles, "Explorer Recent Files");
            }

            // 4. IconCache
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string iconCacheFile = Path.Combine(localAppData, "IconCache.db");
            if (File.Exists(iconCacheFile))
            {
                try
                {
                    long sz = new FileInfo(iconCacheFile).Length;
                    File.Delete(iconCacheFile);
                    Interlocked.Add(ref _totalSpaceFreed, sz);
                    LogStatus("IconCache.db wiped");
                }
                catch { }
            }
        }

        private void ConfigurePagefile(string driveLetter)
        {
            try
            {
                string regPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
                string pagefileValue = $"{driveLetter}:\\pagefile.sys 0 0";

                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(regPath, true))
                {
                    if (key != null)
                    {
                        key.SetValue("PagingFiles", new string[] { pagefileValue }, RegistryValueKind.MultiString);
                        LogStatus($"PAGEFILE: Configured on {driveLetter}: - Reboot required");
                    }
                }

                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(regPath, true))
                    {
                        key?.SetValue("AutomaticManagedPagefile", 0, RegistryValueKind.DWord);
                    }
                }
                catch (Exception ex)
                {
                    LogStatus($"PAGEFILE REGISTRY: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                LogStatus($"PAGEFILE config failed: {ex.Message}");
            }
        }

        private async Task MoveSystemRestoreAsync(string targetDrive)
        {
            try
            {
                await RunCommandAsync("vssadmin", $"delete shadows /for=C: /all /quiet");
                string maxSize = "10GB";
                await RunCommandAsync("vssadmin", $"Resize ShadowStorage /For=C: /On={targetDrive}: /MaxSize={maxSize}");
                LogStatus($"SYSTEM RESTORE: Moved to {targetDrive}:");
            }
            catch (Exception ex) { LogStatus($"System Restore move failed: {ex.Message}"); }
        }

        private void RemoveOrphanedInstallers(string installerDir)
        {
            if (!Directory.Exists(installerDir))
            {
                LogStatus("ORPHANED INSTALLERS: Directory not found");
                return;
            }

            int removedCount = 0;
            long bytesFreed = 0;
            try
            {
                var patterns = new[] { "*.msi", "*.msp" };
                foreach (string pattern in patterns)
                {
                    foreach (string file in Directory.EnumerateFiles(installerDir, pattern))
                    {
                        if (_abortRequested) return;
                        try
                        {
                            FileInfo fi = new FileInfo(file);
                            if (fi.CreationTime < DateTime.Now.AddMonths(-6))
                            {
                                long size = fi.Length;
                                File.Delete(file);
                                removedCount++;
                                bytesFreed += size;
                                LogStatus($"REMOVED ORPHANED: {Path.GetFileName(file)}");
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                LogStatus($"ORPHANED CLEANUP ERROR: {ex.Message}");
            }

            Interlocked.Add(ref _totalSpaceFreed, bytesFreed);
            double freedMB = bytesFreed / (1024.0 * 1024.0);
            LogStatus($"ORPHANED INSTALLERS: Removed {removedCount} files ({freedMB:N1} MB)");
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

        private async Task StopServiceAsync(string serviceName)
        {
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = "net";
                    process.StartInfo.Arguments = $"stop {serviceName}";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    try { await process.WaitForExitAsync(cts.Token); }
                    catch (OperationCanceledException)
                    {
                        try { process.Kill(); } catch { }
                        LogStatus($"SERVICE STOP TIMEOUT: {serviceName}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogStatus($"SERVICE STOP FAILED: {serviceName} - {ex.Message}");
            }
        }

        private async Task StartServiceAsync(string serviceName)
        {
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = "net";
                    process.StartInfo.Arguments = $"start {serviceName}";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    try { await process.WaitForExitAsync(cts.Token); }
                    catch (OperationCanceledException)
                    {
                        try { process.Kill(); } catch { }
                        LogStatus($"SERVICE START TIMEOUT: {serviceName}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogStatus($"SERVICE START FAILED: {serviceName} - {ex.Message}");
            }
        }

        private class DriveInfoWrapper
        {
            public string DriveLetter { get; set; }
            public double FreeGB { get; set; }
            public double TotalGB { get; set; }
        }

        private DriveInfoWrapper SelectTargetDrive()
        {
            // Dynamically find non-system fixed drives
            string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
            var candidates = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed && !d.Name.StartsWith(systemDrive, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.AvailableFreeSpace)
                .ToList();

            // First pass: require enough free space for pagefile
            foreach (var di in candidates)
            {
                double freeGB = ToGB(di.AvailableFreeSpace);
                if (freeGB >= requiredFreeGB)
                {
                    return new DriveInfoWrapper
                    {
                        DriveLetter = di.Name.Substring(0, 1),
                        FreeGB = freeGB,
                        TotalGB = ToGB(di.TotalSize)
                    };
                }
            }

            // Second pass: lower threshold
            foreach (var di in candidates)
            {
                double freeGB = ToGB(di.AvailableFreeSpace);
                if (freeGB >= pagefileMarginGB)
                {
                    return new DriveInfoWrapper
                    {
                        DriveLetter = di.Name.Substring(0, 1),
                        FreeGB = freeGB,
                        TotalGB = ToGB(di.TotalSize)
                    };
                }
            }

            return null;
        }

        private double ToGB(long bytes)
        {
            return Math.Round(bytes / (1024.0 * 1024.0 * 1024.0), 2);
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
