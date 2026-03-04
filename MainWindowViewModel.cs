using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;

namespace BorderlandsStorageCleaner
{
    /// <summary>
    /// ViewModel for the main application window.
    /// Implements MVVM pattern with INotifyPropertyChanged for data binding.
    /// </summary>
    public class MainWindowViewModel : INotifyPropertyChanged
    {
        private readonly CleanerService _cleanerService;
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
        private bool _createBackup = false;

        // Timer and stats properties
        private string _timeElapsed = "Time: 00:00";
        private string _spaceFreed = "Freed: 0 MB";
        private string _diskSpace = "C: 0.0GB FREE";
        private string _logText = "";

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
            _customFolders = new ObservableCollection<string>();

            StartCleaningCommand = new RelayCommand(StartCleaning, () => !IsCleaning);
            AbortCleaningCommand = new RelayCommand(AbortCleaning, () => IsCleaning);
            QuickScanCommand = new RelayCommand(QuickScan, () => !IsCleaning);
            RemoveFolderCommand = new RelayCommand(RemoveSelectedFolder, () => SelectedCustomFolder != null);

            // Initialize disk space on startup
            DiskSpace = GetDiskFreeSpace();

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

        public bool CreateBackup
        {
            get => _createBackup;
            set { if (_createBackup != value) { _createBackup = value; OnPropertyChanged(nameof(CreateBackup)); } }
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
                CreateBackup = CreateBackup,
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
            await _cleanerService.ExecuteCleanupAsync(UpdateProgress, UpdateStatus, AddLogMessage, config);

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

                UpdateProgress(20);
                UpdateStatus("Scanning TEMP folders...");
                potentialSpace += CalculateDirectorySize(Path.GetTempPath());
                potentialSpace += CalculateDirectorySize(@"C:\Windows\Temp");

                UpdateProgress(40);
                UpdateStatus("Scanning browser caches...");
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
                UpdateStatus("Scanning Windows Update cache...");
                potentialSpace += CalculateDirectorySize(@"C:\Windows\SoftwareDistribution\Download");

                UpdateProgress(80);
                UpdateStatus("Scanning thumbnail cache...");
                string thumbCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Explorer");
                potentialSpace += CalculateDirectorySize(thumbCache);

                UpdateProgress(100);
                double potentialMB = potentialSpace / (1024.0 * 1024.0);
                if (potentialMB >= 1024)
                    UpdateStatus($"SCAN COMPLETE: {potentialMB / 1024.0:F1} GB RECOVERABLE");
                else
                    UpdateStatus($"SCAN COMPLETE: {potentialMB:N0} MB RECOVERABLE");

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

        private void UpdateStatus(string message)
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
                LogText += logLine + Environment.NewLine;
            });
        }

        private string GetDiskFreeSpace()
        {
            try
            {
                var drive = new DriveInfo("C");
                double freeGB = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                return $"C: {freeGB:F1}GB FREE";
            }
            catch
            {
                return "C: --GB FREE";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // Simple RelayCommand implementation
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;
        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }
        public bool CanExecute(object parameter) => _canExecute == null || _canExecute();
        public void Execute(object parameter) => _execute();
        public event EventHandler CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
