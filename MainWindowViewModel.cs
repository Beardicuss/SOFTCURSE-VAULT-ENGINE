using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;

namespace SoftcurseVaultCleaner
{
    /// <summary>
    /// ViewModel for the main application window.
    /// Implements MVVM pattern with INotifyPropertyChanged for data binding.
    /// </summary>
    public class MainWindowViewModel : INotifyPropertyChanged
    {
        private readonly CleanerService _cleanerService;
        private readonly SafeCleanupEngine _safetyEngine;
        private readonly Action<string> _persistentLog;

        // ── Services ────────────────────────────────────────────────────
        public AppSettings Settings => AppSettings.Instance;
        // ── Update state ────────────────────────────────────────────────
        private string _updateStatus = "";
        private bool _updateAvailable;
        private UpdateService.UpdateInfo _latestUpdate;

        public string UpdateStatus
        {
            get => _updateStatus;
            set { if (_updateStatus != value) { _updateStatus = value; OnPropertyChanged(nameof(UpdateStatus)); } }
        }
        public bool UpdateAvailable
        {
            get => _updateAvailable;
            set { if (_updateAvailable != value) { _updateAvailable = value; OnPropertyChanged(nameof(UpdateAvailable)); } }
        }
        public UpdateService.UpdateInfo LatestUpdate => _latestUpdate;

        private bool _isCleaning;
        private int _progress;
        private string _status;

        // Checkbox properties for cleanup options
        private bool _cleanTempFiles = false;
        private bool _cleanCache = false;
        private bool _cleanLogs = false;
        private bool _cleanRecycleBin = false;
        private bool _cleanPrefetch = false;
        private bool _deepScanMode = false;
        private bool _useRecycleBin = true;
        
        // Advanced cleanup properties
        private bool _cleanDevTools = false;
        private bool _cleanGaming = false;
        private bool _cleanSystemDumps = false;
        private bool _cleanDNS = false;
        private bool _cleanExtreme = false;

        // Timer and stats properties
        private string _timeElapsed = "Time: 00:00";
        private string _spaceFreed = "Moved: 0 MB";
        private string _diskSpace = "C: 0.0GB FREE";
        private string _logText = "";
        private readonly System.Text.StringBuilder _logBuilder = new System.Text.StringBuilder();

        // Custom folder list (replaces old string CustomPaths)
        private ObservableCollection<string> _customFolders;
        private string _selectedCustomFolder;

        // Timer for elapsed time tracking
        private DispatcherTimer _cleanupTimer;
        private Stopwatch _cleanupStopwatch;

        // ── Disk Analyzer sub-ViewModel ───────────────────────────────────────
        public DiskAnalyzerViewModel DiskAnalyzer { get; }
        public AutoTuneViewModel AutoTune { get; }

        public MainWindowViewModel(Action<string> persistentLog = null)
        {
            _persistentLog = persistentLog;
            _cleanerService = new CleanerService();
            _safetyEngine = new SafeCleanupEngine();
            _status = "STANDBY";
            _customFolders = new ObservableCollection<string>();
            Settings.ApplyPhase1SafetyMigration();
            DiskAnalyzer = new DiskAnalyzerViewModel();
            AutoTune = new AutoTuneViewModel();
            // Wire "Send to Vault" callback: adds paths into CustomFolders list
            DiskAnalyzer.SendPathsToVaultCallback = paths =>
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var path in paths)
                        AddCustomFolder(path);
                });
            };

            StartCleaningCommand = new RelayCommand(StartCleaning, () => !IsCleaning);
            AbortCleaningCommand = new RelayCommand(AbortCleaning, () => IsCleaning);
            QuickScanCommand = new RelayCommand(QuickScan, () => !IsCleaning);
            RemoveFolderCommand = new RelayCommand(RemoveSelectedFolder, () => SelectedCustomFolder != null);
            CheckForUpdatesCommand = new RelayCommand(async () => await CheckForUpdatesAsync());
            ResetSettingsCommand = new RelayCommand(() => Settings.Reset());
            DownloadUpdateCommand = new RelayCommand(async () => await DownloadVerifiedUpdateAsync());

            // Load cleanup defaults from saved settings
            LoadSettingsDefaults();

            // Initialize disk space on startup
            DiskSpace = GetDiskFreeSpace();

            // Check for updates on startup (fire-and-forget)
            if (Settings.CheckUpdatesOnStartup)
                _ = CheckForUpdatesAsync();

            // Setup elapsed time timer
            _cleanupStopwatch = new Stopwatch();
            _cleanupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _cleanupTimer.Tick += (s, e) =>
            {
                var elapsed = _cleanupStopwatch.Elapsed;
                TimeElapsed = $"Time: {elapsed.Minutes:D2}:{elapsed.Seconds:D2}";

                // Update freed space periodically from actual tracked bytes
                long freed = _cleanerService.TotalSpaceFreed;
                double freedMB = freed / (1024.0 * 1024.0);
                if (freedMB >= 1024)
                    SpaceFreed = $"Moved: {freedMB / 1024.0:F1} GB";
                else
                    SpaceFreed = $"Moved: {freedMB:N0} MB";

                // Refresh disk space during cleanup
                DiskSpace = GetDiskFreeSpace();
            };

        }

        public bool IsCleaning
        {
            get => _isCleaning;
            private set
            {
                if (_isCleaning != value)
                {
                    _isCleaning = value;
                    OnPropertyChanged(nameof(IsCleaning));
                    ((RelayCommand)StartCleaningCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)AbortCleaningCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)QuickScanCommand).RaiseCanExecuteChanged();
                }
            }
        }

        public int Progress
        {
            get => _progress;
            private set
            {
                if (_progress != value)
                {
                    _progress = value;
                    OnPropertyChanged(nameof(Progress));
                }
            }
        }

        public string Status
        {
            get => _status;
            private set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged(nameof(Status));
                }
            }
        }

        // Checkbox properties
        public bool CleanTempFiles
        {
            get => _cleanTempFiles;
            set { if (_cleanTempFiles != value) { _cleanTempFiles = value; OnPropertyChanged(nameof(CleanTempFiles)); } }
        }

        public bool CleanCache
        {
            get => _cleanCache;
            set { if (_cleanCache != value) { _cleanCache = value; OnPropertyChanged(nameof(CleanCache)); } }
        }

        public bool CleanLogs
        {
            get => _cleanLogs;
            set { if (_cleanLogs != value) { _cleanLogs = value; OnPropertyChanged(nameof(CleanLogs)); } }
        }

        public bool CleanRecycleBin
        {
            get => _cleanRecycleBin;
            set { if (_cleanRecycleBin != value) { _cleanRecycleBin = value; OnPropertyChanged(nameof(CleanRecycleBin)); } }
        }

        public bool CleanPrefetch
        {
            get => _cleanPrefetch;
            set { if (_cleanPrefetch != value) { _cleanPrefetch = value; OnPropertyChanged(nameof(CleanPrefetch)); } }
        }

        public bool DeepScanMode
        {
            get => _deepScanMode;
            set { if (_deepScanMode != value) { _deepScanMode = value; OnPropertyChanged(nameof(DeepScanMode)); } }
        }

        public bool UseRecycleBin
        {
            get => _useRecycleBin;
            set { if (_useRecycleBin != value) { _useRecycleBin = value; OnPropertyChanged(nameof(UseRecycleBin)); } }
        }

        public bool CleanDevTools
        {
            get => _cleanDevTools;
            set { if (_cleanDevTools != value) { _cleanDevTools = value; OnPropertyChanged(nameof(CleanDevTools)); } }
        }

        public bool CleanGaming
        {
            get => _cleanGaming;
            set { if (_cleanGaming != value) { _cleanGaming = value; OnPropertyChanged(nameof(CleanGaming)); } }
        }

        public bool CleanSystemDumps
        {
            get => _cleanSystemDumps;
            set { if (_cleanSystemDumps != value) { _cleanSystemDumps = value; OnPropertyChanged(nameof(CleanSystemDumps)); } }
        }

        public bool CleanDNS
        {
            get => _cleanDNS;
            set { if (_cleanDNS != value) { _cleanDNS = value; OnPropertyChanged(nameof(CleanDNS)); } }
        }
        public bool CleanExtreme
        {
            get => _cleanExtreme;
            set { if (_cleanExtreme != value) { _cleanExtreme = value; OnPropertyChanged(nameof(CleanExtreme)); } }
        }

        public string TimeElapsed
        {
            get => _timeElapsed;
            set { if (_timeElapsed != value) { _timeElapsed = value; OnPropertyChanged(nameof(TimeElapsed)); } }
        }

        public string SpaceFreed
        {
            get => _spaceFreed;
            set { if (_spaceFreed != value) { _spaceFreed = value; OnPropertyChanged(nameof(SpaceFreed)); } }
        }

        public string DiskSpace
        {
            get => _diskSpace;
            set { if (_diskSpace != value) { _diskSpace = value; OnPropertyChanged(nameof(DiskSpace)); } }
        }

        public string LogText
        {
            get => _logText;
            set { if (_logText != value) { _logText = value; OnPropertyChanged(nameof(LogText)); } }
        }

        // Custom folder list for Task 2
        public ObservableCollection<string> CustomFolders
        {
            get => _customFolders;
            set { if (_customFolders != value) { _customFolders = value; OnPropertyChanged(nameof(CustomFolders)); } }
        }

        public string SelectedCustomFolder
        {
            get => _selectedCustomFolder;
            set
            {
                if (_selectedCustomFolder != value)
                {
                    _selectedCustomFolder = value;
                    OnPropertyChanged(nameof(SelectedCustomFolder));
                    ((RelayCommand)RemoveFolderCommand).RaiseCanExecuteChanged();
                }
            }
        }

        public ICommand StartCleaningCommand { get; }
        public ICommand AbortCleaningCommand { get; }
        public ICommand QuickScanCommand { get; }
        public ICommand RemoveFolderCommand { get; }
        public ICommand CheckForUpdatesCommand { get; }
        public ICommand ResetSettingsCommand { get; }
        public ICommand DownloadUpdateCommand { get; }

        /// <summary>
        /// Adds a folder path to the custom folders list (called from code-behind after folder dialog).
        /// </summary>
        public void AddCustomFolder(string folderPath)
        {
            if (!string.IsNullOrWhiteSpace(folderPath) && !CustomFolders.Contains(folderPath))
            {
                var target = new CleanupTarget(
                    $"custom:{folderPath}", "Custom folder", folderPath,
                    "User-selected cleanup folder", CleanupTargetType.DirectoryContents,
                    CleanupTargetOrigin.UserSelected);
                var preview = _safetyEngine.Preview(target);
                if (!preview.IsAllowed)
                {
                    System.Windows.MessageBox.Show(
                        $"This folder was blocked by the cleanup safety policy:\n\n{preview.CanonicalPath}\n\n{preview.ValidationMessage}",
                        "Unsafe Cleanup Target",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                CustomFolders.Add(folderPath);
            }
        }

        private void RemoveSelectedFolder()
        {
            if (SelectedCustomFolder != null && CustomFolders.Contains(SelectedCustomFolder))
            {
                CustomFolders.Remove(SelectedCustomFolder);
                SelectedCustomFolder = null;
            }
        }

        /// <summary>
        /// Builds a CleanupConfig from the current checkbox states and custom folders.
        /// </summary>
        private CleanupConfig BuildConfig()
        {
            return new CleanupConfig
            {
                CleanTempFiles = CleanTempFiles,
                CleanCache = CleanCache,
                CleanLogs = CleanLogs,
                CleanRecycleBin = CleanRecycleBin,
                CleanPrefetch = CleanPrefetch,
                DeepScanMode = DeepScanMode,
                // Phase 1 safety invariant: filesystem deletion is recoverable only.
                UseRecycleBin = true,
                CleanDevTools = CleanDevTools,
                CleanGaming = CleanGaming,
                CleanSystemDumps = CleanSystemDumps,
                CleanDNS = CleanDNS,
                CleanExtreme = CleanExtreme,
                CustomPaths = CustomFolders.ToList()
            };
        }

        private async void StartCleaning()
        {
            var config = BuildConfig();
            IsCleaning = true;
            Status = "BUILDING CLEANUP PREVIEW";

            CleanupPlan plan;
            IReadOnlyList<CleanupPreviewItem> preview;
            try
            {
                var prepared = await Task.Run(() =>
                {
                    CleanupPlan builtPlan = _cleanerService.CreateCleanupPlan(config);
                    return (Plan: builtPlan, Preview: _safetyEngine.Preview(builtPlan));
                });
                plan = prepared.Plan;
                preview = prepared.Preview;
            }
            catch (Exception ex)
            {
                AddLogMessage($"[SAFETY] Could not build cleanup preview: {ex.Message}");
                Status = "CLEANUP PREVIEW FAILED";
                IsCleaning = false;
                return;
            }

            if (!ConfirmCleanup(config, preview))
            {
                Status = "CLEANUP CANCELLED BEFORE EXECUTION";
                IsCleaning = false;
                return;
            }

            Status = "INITIATING CLEANUP SEQUENCE";
            Progress = 0;

            DiskSpace = GetDiskFreeSpace();

            _cleanupStopwatch.Reset();
            _cleanupStopwatch.Start();
            _cleanupTimer.Start();

            AddLogMessage("=== CLEANUP PROTOCOL INITIATED ===");

            await _cleanerService.ExecuteCleanupAsync(UpdateProgress, SetStatus, AddLogMessage, config, plan);

            _cleanupStopwatch.Stop();
            _cleanupTimer.Stop();

            var elapsed = _cleanupStopwatch.Elapsed;
            TimeElapsed = $"Time: {elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
            DiskSpace = GetDiskFreeSpace();

            long freed = _cleanerService.TotalSpaceFreed;
            double freedMB = freed / (1024.0 * 1024.0);
            if (freedMB >= 1024)
                SpaceFreed = $"{(config.CleanRecycleBin ? "Reclaimed" : "Moved")}: {freedMB / 1024.0:F1} GB";
            else
                SpaceFreed = $"{(config.CleanRecycleBin ? "Reclaimed" : "Moved")}: {freedMB:N0} MB";

            IsCleaning = false;
            Status = "CLEANUP PROTOCOL COMPLETE";
        }

        private bool ConfirmCleanup(
            CleanupConfig config,
            IReadOnlyList<CleanupPreviewItem> preview)
        {
            var operations = new System.Collections.Generic.List<string>();
            if (config.CleanRecycleBin) operations.Add("Empty Recycle Bin (not recoverable)");
            if (config.CleanDNS) operations.Add("Command: flush the current DNS resolver cache");
            if (config.CleanExtreme) operations.Add("Explorer privacy data: recent items and icon cache (recoverable)");
            if (config.DeepScanMode)
                operations.Add("Elevated helper: supported DISM component cleanup without ResetBase (UAC required)");

            var blockedCustom = preview.Where(item => !item.IsAllowed &&
                item.Target.Origin == CleanupTargetOrigin.UserSelected).ToList();
            if (blockedCustom.Count > 0)
            {
                System.Windows.MessageBox.Show(
                    "Cleanup cannot start because custom targets were blocked:\n\n" +
                    string.Join("\n", blockedCustom.Select(item => $"• {item.CanonicalPath}: {item.ValidationMessage}")),
                    "Cleanup Blocked",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return false;
            }

            var allowedFiles = preview.Where(item => item.IsAllowed).ToList();
            var blockedFiles = preview.Where(item => !item.IsAllowed).ToList();
            if (operations.Count == 0 && allowedFiles.Count == 0 && blockedFiles.Count == 0)
            {
                Status = "No cleanup operations selected.";
                return false;
            }

            long estimatedBytes = allowedFiles.Sum(item => item.EstimatedBytes);
            string message = $"FILESYSTEM PLAN: {allowedFiles.Count} allowed target(s), approximately {SizeFormatter.Format(estimatedBytes)}\n\n" +
                             string.Join("\n", allowedFiles.Take(20).Select(item =>
                                 $"• [{item.Target.Risk}] {item.Target.DisplayName}\n  {item.CanonicalPath}")) +
                             (allowedFiles.Count > 20 ? $"\n• … and {allowedFiles.Count - 20} more target(s)" : "");

            if (blockedFiles.Count > 0)
                message += $"\n\nBLOCKED BY SAFETY POLICY: {blockedFiles.Count}\n" +
                           string.Join("\n", blockedFiles.Take(8).Select(item =>
                               $"• {item.Target.DisplayName}: {item.ValidationMessage}"));

            if (operations.Count > 0)
                message += "\n\nNON-FILESYSTEM OPERATIONS:\n" +
                           string.Join("\n", operations.Select(operation => $"• {operation}"));

            message += "\n\nAllowed filesystem targets are forced through the Recycle Bin. " +
                       "Commands and settings explicitly marked not recoverable are outside that protection.\n\nContinue?";
            return System.Windows.MessageBox.Show(
                message,
                "Cleanup Preview",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;
        }

        private void AbortCleaning()
        {
            _cleanerService.RequestAbort();
            Status = "Abort requested";
        }

        private async void QuickScan()
        {
            IsCleaning = true;
            Status = "SCANNING SYSTEM...";
            Progress = 0;

            await Task.Run(() =>
            {
                UpdateProgress(25);
                SetStatus("Scanning safe cleanup catalog...");
                var scanConfig = new CleanupConfig
                {
                    CleanTempFiles = true,
                    CleanCache = true,
                    CleanDevTools = true,
                    CleanGaming = true,
                    CleanSystemDumps = true
                };
                CleanupPlan scanPlan = _cleanerService.CreateCleanupPlan(scanConfig);
                UpdateProgress(65);
                IReadOnlyList<CleanupPreviewItem> preview = _safetyEngine.Preview(scanPlan);
                long potentialSpace = preview.Where(item => item.IsAllowed).Sum(item => item.EstimatedBytes);

                UpdateProgress(100);
                double potentialMB = potentialSpace / (1024.0 * 1024.0);
                if (potentialMB >= 1024)
                    SetStatus($"SCAN COMPLETE: {potentialMB / 1024.0:F1} GB SAFE TARGETS");
                else
                    SetStatus($"SCAN COMPLETE: {potentialMB:N0} MB SAFE TARGETS");

                System.Threading.Thread.Sleep(1000);
            });

            IsCleaning = false;
        }

        private long CalculateDirectorySize(string path)
        {
            if (!Directory.Exists(path))
                return 0;

            try
            {
                long size = 0;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { size += new FileInfo(file).Length; } catch { }
                }
                return size;
            }
            catch
            {
                return 0;
            }
        }

        private void UpdateProgress(int percent)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                Progress = percent;
            });
        }

        private void SetStatus(string message)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                Status = message;
            });
        }

        public void AddLogMessage(string message)
        {
            try { _persistentLog?.Invoke(message); } catch { }
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string logLine = $"[{timestamp}] {message}";
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _logBuilder.AppendLine(logLine);
                LogText = _logBuilder.ToString();
            });
        }

        private string GetDiskFreeSpace()
        {
            try
            {
                string sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
                string driveLetter = sysRoot.Substring(0, 1);
                var drive = new DriveInfo(driveLetter);
                double freeGB = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                return $"{driveLetter}: {freeGB:F1}GB FREE";
            }
            catch
            {
                return "--GB FREE";
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  SECURE UPDATE CHANNEL
        // ═══════════════════════════════════════════════════════════════

        private async Task CheckForUpdatesAsync()
        {
            UpdateAvailable = false;
            UpdateStatus = "Checking for updates…";
            var result = await UpdateService.CheckForUpdateAsync();
            _latestUpdate = result;
            OnPropertyChanged(nameof(LatestUpdate));

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                UpdateStatus = result.Error;
            }
            else if (result.IsAvailable)
            {
                UpdateAvailable = true;
                UpdateStatus = $"Update available: v{result.NewVersion}";
                AddLogMessage($"[UPDATE] New version {result.NewVersion} available!");
            }
            else
            {
                UpdateStatus = $"You're up to date (v{result.CurrentVersion})";
            }
        }

        private async Task DownloadVerifiedUpdateAsync()
        {
            if (_latestUpdate == null || !_latestUpdate.IsAvailable)
                return;

            UpdateStatus = "Downloading and verifying signed update…";
            var progress = new Progress<int>(percent =>
                UpdateStatus = $"Downloading and verifying signed update… {percent}%");
            UpdateService.DownloadResult result = await UpdateService.DownloadAndVerifyAsync(
                _latestUpdate, progress);
            if (!result.Succeeded)
            {
                UpdateStatus = result.Error;
                AddLogMessage($"[UPDATE] {result.Error}");
                return;
            }

            UpdateStatus = "Verified installer staged. The current installation remains unchanged.";
            var choice = System.Windows.MessageBox.Show(
                "The update passed signed metadata, SHA-256, and Authenticode verification.\n\n" +
                "Launch the installer now? The current installation is retained until the installer completes.",
                "Verified Update Ready",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Information);
            if (choice == System.Windows.MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(result.InstallerPath) { UseShellExecute = true });
        }

        private void LoadSettingsDefaults()
        {
            CleanTempFiles = Settings.DefaultCleanTemp;
            CleanCache = Settings.DefaultCleanCache;
            CleanLogs = Settings.DefaultCleanLogs;
            CleanRecycleBin = Settings.DefaultCleanRecycleBin;
            CleanPrefetch = Settings.DefaultCleanPrefetch;
            UseRecycleBin = true;
            
            CleanDevTools = Settings.DefaultCleanDevTools;
            CleanGaming = Settings.DefaultCleanGaming;
            CleanSystemDumps = Settings.DefaultCleanSystemDumps;
            CleanDNS = Settings.DefaultCleanDNS;
            CleanExtreme = Settings.DefaultCleanExtreme;
        }

        public string AppVersion => UpdateService.GetCurrentVersion();

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
