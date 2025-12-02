using System;
using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using BorderlandsStorageCleaner.WinStat.Models;
using BorderlandsStorageCleaner.WinStat.Services;

namespace BorderlandsStorageCleaner.WinStat
{
    /// <summary>
    /// ViewModel for WinStat disk analyzer window.
    /// </summary>
    public class WinStatViewModel : INotifyPropertyChanged
    {
        private readonly WinStatService _service;
        private CancellationTokenSource _cancellationTokenSource;

        // Properties
        private string _selectedPath = "C:\\";
        private bool _isScanning;
        private double _progress;
        private string _status = "Ready";
        private string _currentFile;
        private ScanResult _scanResult;

        // Summary stats
        private string _totalSize = "0 GB";
        private string _fileCount = "0";
        private string _directoryCount = "0";
        private string _scanDuration = "00:00";

        // Tab collections
        public ObservableCollection<FSNode> TopFiles { get; set; }
        public ObservableCollection<CategoryStats> Categories { get; set; }
        public ObservableCollection<FSNode> AgedFiles { get; set; }
        public ObservableCollection<DuplicateGroup> Duplicates { get; set; }
        public ObservableCollection<Recommendation> Recommendations { get; set; }

        public WinStatViewModel()
        {
            _service = new WinStatService();

            // Initialize collections
            TopFiles = new ObservableCollection<FSNode>();
            Categories = new ObservableCollection<CategoryStats>();
            AgedFiles = new ObservableCollection<FSNode>();
            Duplicates = new ObservableCollection<DuplicateGroup>();
            Recommendations = new ObservableCollection<Recommendation>();

            // Commands
            ScanCommand = new RelayCommand(StartScan, () => !IsScanning);
            DeepScanCommand = new RelayCommand(StartDeepScan, () => !IsScanning);
            CancelCommand = new RelayCommand(CancelScan, () => IsScanning);
            BrowseCommand = new RelayCommand(BrowseFolder, () => !IsScanning);
        }

        public ICommand BrowseCommand { get; }

        private void BrowseFolder()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Folder to Analyze",
                InitialDirectory = Directory.Exists(SelectedPath) ? SelectedPath : "C:\\"
            };

            if (dialog.ShowDialog() == true)
            {
                SelectedPath = dialog.FolderName;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        // Properties
        public string SelectedPath
        {
            get => _selectedPath;
            set
            {
                if (_selectedPath != value)
                {
                    _selectedPath = value;
                    OnPropertyChanged(nameof(SelectedPath));
                }
            }
        }

        public bool IsScanning
        {
            get => _isScanning;
            set
            {
                if (_isScanning != value)
                {
                    _isScanning = value;
                    OnPropertyChanged(nameof(IsScanning));
                    ((RelayCommand)ScanCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)DeepScanCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
                }
            }
        }

        public double Progress
        {
            get => _progress;
            set
            {
                if (Math.Abs(_progress - value) > 0.01)
                {
                    _progress = value;
                    OnPropertyChanged(nameof(Progress));
                }
            }
        }

        public string Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged(nameof(Status));
                }
            }
        }

        public string CurrentFile
        {
            get => _currentFile;
            set
            {
                if (_currentFile != value)
                {
                    _currentFile = value;
                    OnPropertyChanged(nameof(CurrentFile));
                }
            }
        }

        public ScanResult ScanResult
        {
            get => _scanResult;
            set
            {
                if (_scanResult != value)
                {
                    _scanResult = value;
                    OnPropertyChanged(nameof(ScanResult));
                    UpdateSummaryStats();
                }
            }
        }

        // Summary stats
        public string TotalSize
        {
            get => _totalSize;
            set { _totalSize = value; OnPropertyChanged(nameof(TotalSize)); }
        }

        public string FileCount
        {
            get => _fileCount;
            set { _fileCount = value; OnPropertyChanged(nameof(FileCount)); }
        }

        public string DirectoryCount
        {
            get => _directoryCount;
            set { _directoryCount = value; OnPropertyChanged(nameof(DirectoryCount)); }
        }

        public string ScanDuration
        {
            get => _scanDuration;
            set { _scanDuration = value; OnPropertyChanged(nameof(ScanDuration)); }
        }

        // Commands
        public ICommand ScanCommand { get; }
        public ICommand DeepScanCommand { get; }
        public ICommand CancelCommand { get; }

        private async void StartScan()
        {
            await ExecuteScan(false);
        }

        private async void StartDeepScan()
        {
            await ExecuteScan(true);
        }

        private async Task ExecuteScan(bool deepScan)
        {
            IsScanning = true;
            Progress = 0;
            Status = deepScan ? "DEEP SCAN IN PROGRESS..." : "SCANNING SYSTEM...";
            _cancellationTokenSource = new CancellationTokenSource();

            var progress = new Progress<ScanProgress>(p =>
            {
                CurrentFile = p.CurrentPath;
                
                // Show dynamic progress since we don't know total
                // Cycle 0-100% every 10,000 files to show activity
                var fileProgress = (p.ScannedFiles % 10000) / 100.0;
                Progress = fileProgress;
                
                Status = $"Scanned: {p.ScannedFiles:N0} files, {p.ScannedDirectories:N0} dirs, {FormatBytes(p.ScannedBytes)}";
            });

            try
            {
                var result = deepScan
                    ? await _service.DeepScanAsync(SelectedPath, progress, _cancellationTokenSource.Token)
                    : await _service.QuickScanAsync(SelectedPath, progress, _cancellationTokenSource.Token);

                if (result != null)
                {
                    ScanResult = result;
                    Progress = 100;
                    Status = $"SCAN COMPLETE - {FormatBytes(result.TotalSize)} analyzed";
                }
                else
                {
                    Status = "SCAN CANCELLED";
                }
            }
            catch (OperationCanceledException)
            {
                Status = "SCAN CANCELLED BY USER";
            }
            catch (Exception ex)
            {
                Status = $"ERROR: {ex.Message}";
            }
            finally
            {
                IsScanning = false;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        private void CancelScan()
        {
            _cancellationTokenSource?.Cancel();
            Status = "CANCELLING SCAN...";
        }

        private void UpdateSummaryStats()
        {
            if (ScanResult == null)
                return;

            TotalSize = FormatBytes(ScanResult.TotalSize);
            FileCount = ScanResult.TotalFiles.ToString("N0");
            DirectoryCount = ScanResult.TotalDirectories.ToString("N0");
            ScanDuration = ScanResult.ScanDuration.ToString(@"mm\:ss");

            // Update tab collections
            UpdateTabCollections();
        }

        private void UpdateTabCollections()
        {
            if (ScanResult == null)
                return;

            // Update Top Files
            TopFiles.Clear();
            foreach (var file in ScanResult.TopFiles)
                TopFiles.Add(file);

            // Update Categories
            Categories.Clear();
            foreach (var category in ScanResult.CategoryBreakdown.Values)
                Categories.Add(category);

            // Update Aged Files
            AgedFiles.Clear();
            foreach (var file in ScanResult.OldFiles)
                AgedFiles.Add(file);

            // Update Duplicates
            Duplicates.Clear();
            foreach (var dup in ScanResult.Duplicates)
                Duplicates.Add(dup);

            // Update Recommendations
            Recommendations.Clear();
            foreach (var rec in ScanResult.Recommendations)
                Recommendations.Add(rec);
        }

        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
