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

            await Task.Run(() => ExecuteCleanupProtocol(config, token), token);
        }

        private void ExecuteCleanupProtocol(CleanupConfig config, CancellationToken token = default)
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

            // Build task list based on config
            var tasks = new List<(string Name, Action Task)>();

            if (config.CleanRecycleBin)
                tasks.Add(("Recycle Bin Incineration", CleanRecycleBin));

            if (config.CleanTempFiles)
                tasks.Add(("TEMP Files Purge", CleanTempFolders));

            if (config.CleanCache)
            {
                tasks.Add(("PYTHON PIP Cache Purge", CleanPipCache));
                tasks.Add(("Thumbnail Cache Clean", CleanThumbnailCache));
                tasks.Add(("Windows Update Cache Flush", CleanWindowsUpdateCache));
                tasks.Add(("UWP App Cache Clean", CleanMicrosoftStoreCache));
                tasks.Add(("Driver Cache Purge", CleanDriverCachesTask));
                tasks.Add(("Unreal Engine Purge", CleanUnrealEngineCache));
                tasks.Add(("Android SDK Clean", CleanAndroidSDK));
                tasks.Add(("Browser Data Wipe", CleanBrowserCaches));
            }

            if (config.CleanLogs)
                tasks.Add(("Event Log Scrub", CleanEventLogs));

            if (config.CleanPrefetch)
                tasks.Add(("Font Cache Rebuild", RebuildFontCache));

            if (config.DeepScanMode)
            {
                tasks.Add(("DISM Cleanup", RunDISMCleanup));
                tasks.Add(("Orphaned Installer Removal", RemoveOrphanedInstallersTask));
            }

            // Custom paths from user
            if (config.CustomPaths != null && config.CustomPaths.Count > 0)
            {
                tasks.Add(("Custom Folder Cleanup", () => CleanCustomPaths(config.CustomPaths)));
            }

            // Execute tasks with evenly distributed progress (5% to 95%)
            int totalTasks = tasks.Count;
            for (int i = 0; i < totalTasks; i++)
            {
                if (ShouldStop()) break;

                int progress = totalTasks > 1
                    ? 5 + (int)((i / (double)(totalTasks - 1)) * 90)
                    : 50;

                ExecuteTask(tasks[i].Name, tasks[i].Task, progress);
            }

            // Pagefile and System Restore (only with deep scan + confirmation already in UI)
            if (!ShouldStop() && selectedDrive != null && config.DeepScanMode)
            {
                ExecuteTask("Pagefile Configuration", () => ConfigurePagefile(selectedDrive.DriveLetter), 98);
                ExecuteTask("System Restore Relocation", () => MoveSystemRestore(selectedDrive.DriveLetter), 99);
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

        private void ExecuteTask(string taskName, Action task, int progress)
        {
            if (_abortRequested) return;

            UpdateProgress(progress);
            UpdateStatus($"EXECUTING: {taskName}");
            LogStatus($"EXECUTING: {taskName}");
            try
            {
                task.Invoke();
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

        private void CleanWindowsUpdateCache()
        {
            try
            {
                StopService("wuauserv");
                CleanDirectory(@"C:\Windows\SoftwareDistribution\Download", "Windows Update Cache");
                StartService("wuauserv");
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

        private void RunDISMCleanup()
        {
            try
            {
                RunCommand("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup");
                RunCommand("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup /ResetBase");
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

        private void RebuildFontCache()
        {
            LogStatus("Rebuilding font cache...");
            try
            {
                StopService("FontCache");
                Thread.Sleep(2000);

                string fontCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "FontCache");
                if (Directory.Exists(fontCache))
                {
                    CleanDirectory(fontCache, "Font Cache");
                }

                StartService("FontCache");
                LogStatus("Font cache rebuilt successfully using service");
            }
            catch (Exception ex)
            {
                LogStatus($"Font cache rebuild failed: {ex.Message}");
                // Note: SFC removed — it's a full system scan, not a font cache tool
                LogStatus("Font cache service unavailable. Cache will rebuild on next reboot.");
            }
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
            long size = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { size += new FileInfo(file).Length; } catch { }
                }
            }
            catch { }
            return size;
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

        private void MoveSystemRestore(string targetDrive)
        {
            try
            {
                RunCommand("vssadmin", $"delete shadows /for=C: /all /quiet");
                string maxSize = "10GB";
                RunCommand("vssadmin", $"Resize ShadowStorage /For=C: /On={targetDrive}: /MaxSize={maxSize}");
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
        private void RunCommand(string fileName, string arguments)
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

                    if (!process.WaitForExit(60000)) // 60 seconds timeout
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

        private void StopService(string serviceName)
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
                    if (!process.WaitForExit(15000))
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

        private void StartService(string serviceName)
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
                    if (!process.WaitForExit(15000))
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
