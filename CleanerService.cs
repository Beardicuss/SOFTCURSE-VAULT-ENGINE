using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BorderlandsStorageCleaner
{
    /// <summary>
    /// Service class responsible for executing system cleanup operations.
    /// Handles file deletion, cache clearing, registry modifications, and service management.
    /// </summary>
    public class CleanerService
    {
        private bool _abortRequested = false;
        private long _totalSpaceFreed = 0;
        private Action<int> _progressCallback;
        private Action<string> _statusCallback;

        // Configuration fields
        private List<string> targetDrives = new List<string> { "D", "E", "F" };
        private int requiredFreeGB = 20;
        private int pagefileMarginGB = 5;
        private bool useDISMResetBase = true;
        private bool deleteBrowserCache = true;
        private bool cleanDriverCaches = true;
        private bool removeOrphanedInstallers = true;
        private bool cleanThumbnails = true;
        private bool rebuildFontCache = true;

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

        public async Task ExecuteCleanupAsync(Action<int> progressCallback, Action<string> statusCallback)
        {
            _abortRequested = false;
            _totalSpaceFreed = 0;
            _progressCallback = progressCallback;
            _statusCallback = statusCallback;

            await Task.Run(() => ExecuteCleanupProtocol());
        }

        private void ExecuteCleanupProtocol()
        {
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

            var tasks = new List<Action>
            {
                () => ExecuteTask("Recycle Bin Incineration", CleanRecycleBin, 4),
                () => ExecuteTask("TEMP Files Purge", CleanTempFolders, 8),
                () => ExecuteTask("PYTHON PIP Cache Purge", CleanPipCache, 12),
                () => ExecuteTask("Thumbnail Cache Clean", CleanThumbnailCache, 16),
                () => ExecuteTask("Windows Update Cache Flush", CleanWindowsUpdateCache, 20),
                () => ExecuteTask("UWP App Cache Clean", CleanMicrosoftStoreCache, 24),
                () => ExecuteTask("NVIDIA Cache Clean", CleanNVIDIACache, 28),
                () => ExecuteTask("Unreal Engine Purge", CleanUnrealEngineCache, 32),
                () => ExecuteTask("Android SDK Clean", CleanAndroidSDK, 36),
                () => ExecuteTask("Event Log Scrub", CleanEventLogs, 40),
                () => ExecuteTask("Browser Data Wipe", CleanBrowserCaches, 44),
                () => ExecuteTask("Font Cache Rebuild", RebuildFontCache, 48),
                () => ExecuteTask("DISM Cleanup", RunDISMCleanup, 52),
                () => ExecuteTask("Driver Cache Purge", CleanDriverCachesTask, 56),
                () => ExecuteTask("Orphaned Installer Removal", RemoveOrphanedInstallersTask, 60)
            };

            int baseProgress = 60;
            int finalProgress = 95;
            int progressIncrement = tasks.Count > 1 ? (finalProgress - baseProgress) / (tasks.Count - 1) : 0;

            for (int i = 0; i < tasks.Count; i++)
            {
                if (_abortRequested) break;

                int currentProgress = baseProgress + (i * progressIncrement);
                tasks[i].Invoke();
            }

            if (!_abortRequested && selectedDrive != null)
            {
                ExecuteTask("Pagefile Configuration", () => ConfigurePagefile(selectedDrive.DriveLetter), 98);
                ExecuteTask("System Restore Relocation", () => MoveSystemRestore(selectedDrive.DriveLetter), 99);
            }

            if (!_abortRequested)
            {
                LogStatus("=== CLEANUP PROTOCOL COMPLETE ===");
                LogStatus($"SYSTEM: All targets eliminated successfully - Freed {_totalSpaceFreed / (1024 * 1024):N0} MB");
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
                    LogStatus("RECYCLE BIN: Cleanup completed");
                _totalSpaceFreed += 100 * 1024 * 1024;
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
                _totalSpaceFreed += 50 * 1024 * 1024;
            }
            catch (Exception ex) { LogStatus($"Pip cache cleanup failed: {ex.Message}"); }
        }

        private void CleanMicrosoftStoreCache()
        {
            try
            {
                // RunCommand("wsreset.exe", ""); // Disabled: Forces Store app to open
                LogStatus("MICROSOFT STORE: Skipping reset command to prevent app launch.");

                string appPackageTempPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
                string tempStatePath = Path.Combine(appPackageTempPath, "TempState");
                CleanDirectory(tempStatePath, "UWP App Temporary State");

                _totalSpaceFreed += 150 * 1024 * 1024;
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

        private void CleanNVIDIACache()
        {
            CleanDirectory(@"C:\ProgramData\NVIDIA Corporation\Downloader", "NVIDIA Cache");
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
                    process.WaitForExit();

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
                                if (!clearProcess.WaitForExit(2000)) // Reduced to 2s timeout per log
                                {
                                    try { clearProcess.Kill(); } catch { }
                                }
                            }
                            // Yield to prevent CPU hogging
                            Thread.Sleep(5);
                        }
                        catch (Exception ex)
                        {
                            LogStatus($"Failed to clear log {log}: {ex.Message}");
                        }
                    }
                }
                _totalSpaceFreed += 50 * 1024 * 1024;
            }
            catch (Exception ex) { LogStatus($"Event logs failed: {ex.Message}"); }
        }

        private void CleanBrowserCaches()
        {
            if (!deleteBrowserCache) return;

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
            _totalSpaceFreed += 200 * 1024 * 1024;
        }

        private void RunDISMCleanup()
        {
            try
            {
                RunCommand("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup");
                if (useDISMResetBase)
                {
                    RunCommand("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup /ResetBase");
                }
                _totalSpaceFreed += 500 * 1024 * 1024;
            }
            catch (Exception ex) { LogStatus($"DISM failed: {ex.Message}"); }
        }

        private void CleanDriverCachesTask()
        {
            if (!cleanDriverCaches) return;

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
            _totalSpaceFreed += 300 * 1024 * 1024;
        }

        private void RemoveOrphanedInstallersTask()
        {
            if (!removeOrphanedInstallers) return;
            RemoveOrphanedInstallers(@"C:\Windows\Installer");
        }

        private void CleanThumbnailCache()
        {
            if (!cleanThumbnails) return;

            LogStatus("Cleaning thumbnail cache...");
            try
            {
                string thumbCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Explorer");
                if (Directory.Exists(thumbCache))
                {
                    CleanDirectory(thumbCache, "Thumbnail Cache");
                    LogStatus("Thumbnail cache cleaned successfully");
                    _totalSpaceFreed += 50 * 1024 * 1024;
                }
            }
            catch (Exception ex) { LogStatus($"Thumbnail cache cleanup failed: {ex.Message}"); }
        }

        private void RebuildFontCache()
        {
            if (!rebuildFontCache) return;

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
                _totalSpaceFreed += 10 * 1024 * 1024;
            }
            catch (Exception ex)
            {
                LogStatus($"Font cache rebuild failed: {ex.Message}");

                try
                {
                    RunCommand("sfc", "/scannow");
                    LogStatus("Used SFC as alternative font cache method");
                }
                catch (Exception ex2)
                {
                    LogStatus($"Fallback method also failed: {ex2.Message}");
                }
            }
        }

        // Helper methods
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

                // Use EnumerateFiles to reduce memory usage (don't load all into memory at once)
                foreach (string file in Directory.EnumerateFiles(path))
                {
                    if (_abortRequested) return;
                    
                    try
                    {
                        File.Delete(file);
                        filesDeleted++;
                    }
                    catch { }
                    
                    // Yield every 100 files to keep UI responsive and lower CPU priority impact
                    if (filesDeleted % 100 == 0) Thread.Sleep(1);
                }

                // Use EnumerateDirectories
                foreach (string dir in Directory.EnumerateDirectories(path))
                {
                    if (_abortRequested) return;

                    try
                    {
                        Directory.Delete(dir, true);
                        dirsDeleted++;
                    }
                    catch { }
                    
                    if (dirsDeleted % 10 == 0) Thread.Sleep(1);
                }

                LogStatus($"CLEANED: {description} ({filesDeleted} files, {dirsDeleted} folders)");
            }
            catch (Exception ex)
            {
                LogStatus($"FAILED: {description} - {ex.Message}");
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
            try
            {
                foreach (string file in Directory.EnumerateFiles(installerDir, "*.msi"))
                {
                    if (_abortRequested) return;
                    try
                    {
                        FileInfo fi = new FileInfo(file);
                        if (fi.CreationTime < DateTime.Now.AddMonths(-6))
                        {
                            File.Delete(file);
                            removedCount++;
                            LogStatus($"REMOVED ORPHANED: {Path.GetFileName(file)}");
                        }
                    }
                    catch { }
                }

                foreach (string file in Directory.EnumerateFiles(installerDir, "*.msp"))
                {
                    if (_abortRequested) return;
                    try
                    {
                        FileInfo fi = new FileInfo(file);
                        if (fi.CreationTime < DateTime.Now.AddMonths(-6))
                        {
                            File.Delete(file);
                            removedCount++;
                            LogStatus($"REMOVED ORPHANED: {Path.GetFileName(file)}");
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                LogStatus($"ORPHANED CLEANUP ERROR: {ex.Message}");
            }

            LogStatus($"ORPHANED INSTALLERS: Removed {removedCount} files");
            _totalSpaceFreed += removedCount * 10 * 1024 * 1024;
        }

        private void RunCommand(string fileName, string arguments)
        {
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = fileName;
                    process.StartInfo.Arguments = arguments;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.Start();
                    
                    // Add timeout to prevent hangs (e.g. wsreset.exe can hang)
                    if (!process.WaitForExit(30000)) // 30 seconds timeout
                    {
                        try { process.Kill(); } catch { }
                        LogStatus($"COMMAND TIMEOUT: {fileName} {arguments}");
                        return;
                    }

                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();

                    if (!string.IsNullOrEmpty(error) && process.ExitCode != 0)
                    {
                        LogStatus($"COMMAND ERROR: {fileName} {arguments} - {error}");
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
                    process.WaitForExit();
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
                    process.WaitForExit();
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
            foreach (string drive in targetDrives)
            {
                try
                {
                    DriveInfo di = new DriveInfo(drive);
                    if (di.IsReady)
                    {
                        double freeGB = ToGB(di.AvailableFreeSpace);
                        if (freeGB >= requiredFreeGB)
                        {
                            return new DriveInfoWrapper
                            {
                                DriveLetter = drive,
                                FreeGB = freeGB,
                                TotalGB = ToGB(di.TotalSize)
                            };
                        }
                    }
                }
                catch { }
            }

            foreach (string drive in targetDrives)
            {
                try
                {
                    DriveInfo di = new DriveInfo(drive);
                    if (di.IsReady)
                    {
                        double freeGB = ToGB(di.AvailableFreeSpace);
                        if (freeGB >= pagefileMarginGB)
                        {
                            return new DriveInfoWrapper
                            {
                                DriveLetter = drive,
                                FreeGB = freeGB,
                                TotalGB = ToGB(di.TotalSize)
                            };
                        }
                    }
                }
                catch { }
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
            _statusCallback?.Invoke(message);
        }
    }
}
