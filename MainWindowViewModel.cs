using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
        private string _customPaths = "C:\\Temp\n%TEMP%\nD:\\Downloads\\Temp";
        private string _logText = "";

        // Timer for elapsed time tracking
        private DispatcherTimer _cleanupTimer;
        private Stopwatch _cleanupStopwatch;

        public MainWindowViewModel()
        {
            _cleanerService = new CleanerService();
            _status = "STANDBY";
            
            StartCleaningCommand = new RelayCommand(StartCleaning, () => !IsCleaning);
            AbortCleaningCommand = new RelayCommand(AbortCleaning, () => IsCleaning);
            QuickScanCommand = new RelayCommand(QuickScan, () => !IsCleaning);

            // Initialize disk space on startup
            DiskSpace = GetDiskFreeSpace();

            // Setup elapsed time timer
            _cleanupStopwatch = new Stopwatch();
            _cleanupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _cleanupTimer.Tick += (s, e) =>
            {
                var elapsed = _cleanupStopwatch.Elapsed;
                TimeElapsed = $"Time: {elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
                // Update freed space periodically
                long freed = _cleanerService.TotalSpaceFreed;
                double freedMB = freed / (1024.0 * 1024.0);
                if (freedMB >= 1024)
                    SpaceFreed = $"Freed: {freedMB / 1024.0:F1} GB";
                else
                    SpaceFreed = $"Freed: {freedMB:N0} MB";
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
            set
            {
                if (_cleanTempFiles != value)
                {
                    _cleanTempFiles = value;
                    OnPropertyChanged(nameof(CleanTempFiles));
                }
            }
        }

        public bool CleanCache
        {
            get => _cleanCache;
            set
            {
                if (_cleanCache != value)
                {
                    _cleanCache = value;
                    OnPropertyChanged(nameof(CleanCache));
                }
            }
        }

        public bool CleanLogs
        {
            get => _cleanLogs;
            set
            {
                if (_cleanLogs != value)
                {
                    _cleanLogs = value;
                    OnPropertyChanged(nameof(CleanLogs));
                }
            }
        }

        public bool CleanRecycleBin
        {
            get => _cleanRecycleBin;
            set
            {
                if (_cleanRecycleBin != value)
                {
                    _cleanRecycleBin = value;
                    OnPropertyChanged(nameof(CleanRecycleBin));
                }
            }
        }

        public bool CleanPrefetch
        {
            get => _cleanPrefetch;
            set
            {
                if (_cleanPrefetch != value)
                {
                    _cleanPrefetch = value;
                    OnPropertyChanged(nameof(CleanPrefetch));
                }
            }
        }

        public bool DeepScanMode
        {
            get => _deepScanMode;
            set
            {
                if (_deepScanMode != value)
                {
                    _deepScanMode = value;
                    OnPropertyChanged(nameof(DeepScanMode));
                }
            }
        }

        public bool CreateBackup
        {
            get => _createBackup;
            set
            {
                if (_createBackup != value)
                {
                    _createBackup = value;
                    OnPropertyChanged(nameof(CreateBackup));
                }
            }
        }

        public string TimeElapsed
        {
            get => _timeElapsed;
            set
            {
                if (_timeElapsed != value)
                {
                    _timeElapsed = value;
                    OnPropertyChanged(nameof(TimeElapsed));
                }
            }
        }

        public string SpaceFreed
        {
            get => _spaceFreed;
            set
            {
                if (_spaceFreed != value)
                {
                    _spaceFreed = value;
                    OnPropertyChanged(nameof(SpaceFreed));
                }
            }
        }

        public string DiskSpace
        {
            get => _diskSpace;
            set
            {
                if (_diskSpace != value)
                {
                    _diskSpace = value;
                    OnPropertyChanged(nameof(DiskSpace));
                }
            }
        }

        public string CustomPaths
        {
            get => _customPaths;
            set
            {
                if (_customPaths != value)
                {
                    _customPaths = value;
                    OnPropertyChanged(nameof(CustomPaths));
                }
            }
        }

        public string LogText
        {
            get => _logText;
            set
            {
                if (_logText != value)
                {
                    _logText = value;
                    OnPropertyChanged(nameof(LogText));
                }
            }
        }

        public ICommand StartCleaningCommand { get; }
        public ICommand AbortCleaningCommand { get; }
        public ICommand QuickScanCommand { get; }


        private async void StartCleaning()
        {
            IsCleaning = true;
            Status = "INITIATING CLEANUP SEQUENCE";
            Progress = 0;

            // Read initial disk space
            DiskSpace = GetDiskFreeSpace();

            // Start elapsed timer
            _cleanupStopwatch.Reset();
            _cleanupStopwatch.Start();
            _cleanupTimer.Start();

            AddLogMessage("=== CLEANUP PROTOCOL INITIATED ===");

            await _cleanerService.ExecuteCleanupAsync(UpdateProgress, UpdateStatus, AddLogMessage);

            // Stop timer
            _cleanupStopwatch.Stop();
            _cleanupTimer.Stop();

            // Final stats update
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
                
                // Scan temp folders
                UpdateProgress(20);
                UpdateStatus("Scanning TEMP folders...");
                potentialSpace += CalculateDirectorySize(System.IO.Path.GetTempPath());
                potentialSpace += CalculateDirectorySize(@"C:\Windows\Temp");
                
                // Scan browser caches
                UpdateProgress(40);
                UpdateStatus("Scanning browser caches...");
                var browserPaths = new[]
                {
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data", "Default", "Cache"),
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data", "Default", "Cache")
                };
                foreach (var path in browserPaths)
                {
                    potentialSpace += CalculateDirectorySize(path);
                }
                
                // Scan Windows Update cache
                UpdateProgress(60);
                UpdateStatus("Scanning Windows Update cache...");
                potentialSpace += CalculateDirectorySize(@"C:\Windows\SoftwareDistribution\Download");
                
                // Scan thumbnail cache
                UpdateProgress(80);
                UpdateStatus("Scanning thumbnail cache...");
                string thumbCache = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Explorer");
                potentialSpace += CalculateDirectorySize(thumbCache);
                
                UpdateProgress(100);
                double potentialMB = potentialSpace / (1024.0 * 1024.0);
                UpdateStatus($"SCAN COMPLETE: {potentialMB:N0}MB RECOVERABLE");
                
                System.Threading.Thread.Sleep(1000);
            });
            
            IsCleaning = false;
        }
        
        private long CalculateDirectorySize(string path)
        {
            if (!System.IO.Directory.Exists(path))
                return 0;
                
            try
            {
                var dirInfo = new System.IO.DirectoryInfo(path);
                long size = 0;
                
                // Get files in current directory
                foreach (var file in dirInfo.GetFiles())
                {
                    try { size += file.Length; } catch { }
                }
                
                // Get subdirectories
                foreach (var dir in dirInfo.GetDirectories())
                {
                    try { size += CalculateDirectorySize(dir.FullName); } catch { }
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

        private void AddLogMessage(string message)
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
