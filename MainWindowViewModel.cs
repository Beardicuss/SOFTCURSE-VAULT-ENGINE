using System;
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

        // ── Services ────────────────────────────────────────────────────
        public AppSettings Settings => AppSettings.Instance;
        public LicenseService License => LicenseService.Instance;

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

        // ── License UI state ────────────────────────────────────────────
        private string _licenseKeyInput = "";
        private string _licenseEmailInput = "";
        private string _licenseMessage = "";

        public string LicenseKeyInput
        {
            get => _licenseKeyInput;
            set { if (_licenseKeyInput != value) { _licenseKeyInput = value; OnPropertyChanged(nameof(LicenseKeyInput)); } }
        }
        public string LicenseEmailInput
        {
            get => _licenseEmailInput;
            set { if (_licenseEmailInput != value) { _licenseEmailInput = value; OnPropertyChanged(nameof(LicenseEmailInput)); } }
        }
        public string LicenseMessage
        {
            get => _licenseMessage;
            set { if (_licenseMessage != value) { _licenseMessage = value; OnPropertyChanged(nameof(LicenseMessage)); } }
        }
        private bool _isCleaning;
        private int _progress;
        private string _status;

        // Checkbox properties for cleanup options
        private bool _cleanTempFiles = true;
        private bool _cleanCache = true;
        private bool _cleanLogs = true;
        private bool _cleanRecycleBin = false;
        private bool _cleanPrefetch = true;
        private bool _deepScanMode = false;
        private bool _useRecycleBin = false;

        // Timer and stats properties
        private string _timeElapsed = "Time: 00:00";
        private string _spaceFreed = "Freed: 0 MB";
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

        public MainWindowViewModel()
        {
            _cleanerService = new CleanerService();
            _status = "STANDBY";
            _customFolders = new ObservableCollection<string>();
            DiskAnalyzer = new DiskAnalyzerViewModel();
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
            ActivateLicenseCommand = new RelayCommand(async () => await ActivateLicenseAsync());
            DeactivateLicenseCommand = new RelayCommand(() => { License.Deactivate(); LicenseMessage = "License deactivated."; });
            ResetSettingsCommand = new RelayCommand(() => Settings.Reset());
            OpenDownloadCommand = new RelayCommand(() =>
            {
                if (_latestUpdate?.DownloadUrl != null)
                    Process.Start(new ProcessStartInfo(_latestUpdate.DownloadUrl) { UseShellExecute = true });
            });

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
                    SpaceFreed = $"Freed: {freedMB / 1024.0:F1} GB";
                else
                    SpaceFreed = $"Freed: {freedMB:N0} MB";

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
        public ICommand ActivateLicenseCommand { get; }
        public ICommand DeactivateLicenseCommand { get; }
        public ICommand ResetSettingsCommand { get; }
        public ICommand OpenDownloadCommand { get; }

        /// <summary>
        /// Adds a folder path to the custom folders list (called from code-behind after folder dialog).
        /// </summary>
        public void AddCustomFolder(string folderPath)
        {
            if (!string.IsNullOrWhiteSpace(folderPath) && !CustomFolders.Contains(folderPath))
            {
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
                UseRecycleBin = UseRecycleBin,
                CustomPaths = CustomFolders.ToList()
            };
        }

        private async void StartCleaning()
        {
            IsCleaning = true;
            Status = "INITIATING CLEANUP SEQUENCE";
            Progress = 0;

            DiskSpace = GetDiskFreeSpace();

            _cleanupStopwatch.Reset();
            _cleanupStopwatch.Start();
            _cleanupTimer.Start();

            AddLogMessage("=== CLEANUP PROTOCOL INITIATED ===");

            var config = BuildConfig();
            await _cleanerService.ExecuteCleanupAsync(UpdateProgress, SetStatus, AddLogMessage, config);

            _cleanupStopwatch.Stop();
            _cleanupTimer.Stop();

            var elapsed = _cleanupStopwatch.Elapsed;
            TimeElapsed = $"Time: {elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
            DiskSpace = GetDiskFreeSpace();

            long freed = _cleanerService.TotalSpaceFreed;
            double freedMB = freed / (1024.0 * 1024.0);
            if (freedMB >= 1024)
                SpaceFreed = $"Freed: {freedMB / 1024.0:F1} GB";
            else
                SpaceFreed = $"Freed: {freedMB:N0} MB";

            IsCleaning = false;
            Status = "CLEANUP PROTOCOL COMPLETE";
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
                long potentialSpace = 0;

                string sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
                UpdateProgress(20);
                SetStatus("Scanning TEMP folders...");
                potentialSpace += CalculateDirectorySize(Path.GetTempPath());
                potentialSpace += CalculateDirectorySize(Path.Combine(sysRoot, "Windows", "Temp"));

                UpdateProgress(40);
                SetStatus("Scanning browser caches...");
                var browserPaths = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data", "Default", "Cache"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data", "Default", "Cache")
                };
                foreach (var path in browserPaths)
                {
                    potentialSpace += CalculateDirectorySize(path);
                }

                UpdateProgress(60);
                SetStatus("Scanning Windows Update cache...");
                potentialSpace += CalculateDirectorySize(Path.Combine(sysRoot, "Windows", "SoftwareDistribution", "Download"));

                UpdateProgress(80);
                SetStatus("Scanning thumbnail cache...");
                string thumbCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Explorer");
                potentialSpace += CalculateDirectorySize(thumbCache);

                UpdateProgress(100);
                double potentialMB = potentialSpace / (1024.0 * 1024.0);
                if (potentialMB >= 1024)
                    SetStatus($"SCAN COMPLETE: {potentialMB / 1024.0:F1} GB RECOVERABLE");
                else
                    SetStatus($"SCAN COMPLETE: {potentialMB:N0} MB RECOVERABLE");

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
        //  UPDATE & LICENSE
        // ═══════════════════════════════════════════════════════════════

        private async Task CheckForUpdatesAsync()
        {
            UpdateStatus = "Checking for updates…";
            var result = await UpdateService.CheckForUpdateAsync();
            _latestUpdate = result;
            OnPropertyChanged(nameof(LatestUpdate));

            if (result.Error != null)
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

        private async Task ActivateLicenseAsync()
        {
            LicenseMessage = "Validating…";
            var (success, message) = await License.ActivateAsync(LicenseKeyInput, LicenseEmailInput);
            LicenseMessage = message;
            if (success)
                AddLogMessage("[LICENSE] Pro license activated successfully.");
        }

        private void LoadSettingsDefaults()
        {
            CleanTempFiles = Settings.DefaultCleanTemp;
            CleanCache = Settings.DefaultCleanCache;
            CleanLogs = Settings.DefaultCleanLogs;
            CleanRecycleBin = Settings.DefaultCleanRecycleBin;
            CleanPrefetch = Settings.DefaultCleanPrefetch;
            UseRecycleBin = Settings.DefaultUseRecycleBin;
        }

        public string AppVersion => UpdateService.GetCurrentVersion();

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
