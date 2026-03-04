using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace BorderlandsStorageCleaner
{
    public class DiskAnalyzerViewModel : INotifyPropertyChanged
    {
        private readonly DiskAnalyzerService _svc = new DiskAnalyzerService();
        private CancellationTokenSource _cts;

        // ── Collections ────────────────────────────────────────────────────
        public ObservableCollection<FolderSizeResult> TopFolders   { get; } = new ObservableCollection<FolderSizeResult>();
        public ObservableCollection<JunkTarget>       JunkItems    { get; } = new ObservableCollection<JunkTarget>();
        public ObservableCollection<JunkTarget>       JunkFiltered { get; } = new ObservableCollection<JunkTarget>();
        public ObservableCollection<LargeFileResult>  LargeFiles   { get; } = new ObservableCollection<LargeFileResult>();
        public ObservableCollection<ProgramEntry>     Programs     { get; } = new ObservableCollection<ProgramEntry>();
        public ObservableCollection<DupeRow>          DupeRows     { get; } = new ObservableCollection<DupeRow>();
        // Suggestions tab — mirrors top safe JunkItems after scan
        public ObservableCollection<JunkTarget> SuggestionItems { get; } = new ObservableCollection<JunkTarget>();

        // ── State ──────────────────────────────────────────────────────────
        private bool   _isScanning;
        private int    _progress;
        private string _status      = "Click DEEP SCAN to begin comprehensive analysis";
        private string _driveInfo   = "";
        private string _suggestions = "Run a deep scan first to generate personalised recommendations.";
        private string _junkFilter  = "All";
        private string _dupRoot;
        private string _minFileSizeLabel = "100 MB";
        private string _selectedDrive    = "C:\\";

        // Selected size counters (live, updates as user checks boxes)
        private string _junkSelectedStr  = "0 B selected";
        private string _largeSelectedStr = "0 B selected";
        private string _dupeSelectedStr  = "0 B selected";
        private string _suggSelectedStr  = "0 B selected";

        // Summary badges
        private string _safeTotalStr   = "–";
        private string _reviewTotalStr = "–";
        private string _largeCountStr  = "–";
        private string _progCountStr   = "–";

        // ── Public properties ──────────────────────────────────────────────
        public bool   IsScanning
        {
            get => _isScanning;
            set
            {
                _isScanning = value; RaisePC(nameof(IsScanning)); RaisePC(nameof(IsNotScanning));
                Raise(ScanCommand); Raise(CancelScanCommand); Raise(DupScanCommand);
                Raise(DeleteJunkCommand); Raise(DeleteLargeCommand); Raise(DeleteDupesCommand); Raise(DeleteSuggestionsCommand);
            }
        }
        public bool   IsNotScanning  => !_isScanning;
        public int    Progress       { get => _progress;  set { _progress  = value; RaisePC(nameof(Progress));  } }
        public string Status         { get => _status;    set { _status    = value; RaisePC(nameof(Status));    } }
        public string DriveInfo      { get => _driveInfo; set { _driveInfo = value; RaisePC(nameof(DriveInfo)); } }
        public string Suggestions    { get => _suggestions; set { _suggestions = value; RaisePC(nameof(Suggestions)); } }

        public string JunkFilter
        {
            get => _junkFilter;
            set { _junkFilter = value; RaisePC(nameof(JunkFilter)); ApplyJunkFilter(); }
        }

        public string DupRoot
        {
            get => _dupRoot;
            set { _dupRoot = value; RaisePC(nameof(DupRoot)); }
        }

        public string MinFileSizeLabel
        {
            get => _minFileSizeLabel;
            set { _minFileSizeLabel = value; RaisePC(nameof(MinFileSizeLabel)); }
        }

        public ObservableCollection<string> AvailableDrives { get; } = new ObservableCollection<string>();

        public string SelectedDrive
        {
            get => _selectedDrive;
            set { _selectedDrive = value; RaisePC(nameof(SelectedDrive)); }
        }

        public string JunkSelectedStr  { get => _junkSelectedStr;  set { _junkSelectedStr  = value; RaisePC(nameof(JunkSelectedStr));  } }
        public string LargeSelectedStr { get => _largeSelectedStr; set { _largeSelectedStr = value; RaisePC(nameof(LargeSelectedStr)); } }
        public string DupeSelectedStr  { get => _dupeSelectedStr;  set { _dupeSelectedStr  = value; RaisePC(nameof(DupeSelectedStr));  } }
        public string SuggSelectedStr  { get => _suggSelectedStr;  set { _suggSelectedStr  = value; RaisePC(nameof(SuggSelectedStr));  } }

        public string SafeTotalStr   { get => _safeTotalStr;   set { _safeTotalStr   = value; RaisePC(nameof(SafeTotalStr));   } }
        public string ReviewTotalStr { get => _reviewTotalStr; set { _reviewTotalStr = value; RaisePC(nameof(ReviewTotalStr)); } }
        public string LargeCountStr  { get => _largeCountStr;  set { _largeCountStr  = value; RaisePC(nameof(LargeCountStr));  } }
        public string ProgCountStr   { get => _progCountStr;   set { _progCountStr   = value; RaisePC(nameof(ProgCountStr));   } }

        public string[] JunkCategories { get; } = { "All", "System", "Browsers", "Developer", "Apps", "User Data" };

        // ── Commands ───────────────────────────────────────────────────────
        public ICommand ScanCommand          { get; }
        public ICommand CancelScanCommand    { get; }
        public ICommand DupScanCommand       { get; }

        // Junk selection
        public ICommand SelectAllJunkCommand   { get; }
        public ICommand SelectSafeJunkCommand  { get; }
        public ICommand DeselectAllJunkCommand { get; }
        public ICommand DeleteJunkCommand      { get; }

        // Large files selection
        public ICommand SelectAllLargeCommand   { get; }
        public ICommand DeselectAllLargeCommand { get; }
        public ICommand DeleteLargeCommand      { get; }
        public ICommand OpenFileLocationCommand { get; }

        // Duplicates
        public ICommand SelectAllDupesCommand   { get; }
        public ICommand DeselectAllDupesCommand { get; }
        public ICommand DeleteDupesCommand      { get; }

        // Suggestions tab
        public ICommand SelectAllSuggestionsCommand   { get; }
        public ICommand DeselectAllSuggestionsCommand { get; }
        public ICommand DeleteSuggestionsCommand      { get; }

        // Send to Vault Cleaner
        public ICommand SendJunkToVaultCommand  { get; }
        public ICommand SendLargeToVaultCommand { get; }

        // Callback set by MainWindowViewModel to bridge into Vault Cleaner
        public Action<System.Collections.Generic.IEnumerable<string>> SendPathsToVaultCallback { get; set; }

        // ── Constructor ────────────────────────────────────────────────────
        public DiskAnalyzerViewModel()
        {
            DupRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            ScanCommand       = new RelayCommand(StartDeepScan,   () => !IsScanning);
            CancelScanCommand = new RelayCommand(CancelScan,       () =>  IsScanning);
            DupScanCommand    = new RelayCommand(StartDupScan,    () => !IsScanning);

            SelectAllJunkCommand   = new RelayCommand(() => SetJunkChecked(JunkFiltered, true));
            SelectSafeJunkCommand  = new RelayCommand(() => SetJunkChecked(JunkFiltered.Where(j => j.Safe), true));
            DeselectAllJunkCommand = new RelayCommand(() => SetJunkChecked(JunkFiltered, false));
            DeleteJunkCommand      = new RelayCommand(ExecuteDeleteJunk, () => !IsScanning);

            SelectAllLargeCommand   = new RelayCommand(() => SetChecked(LargeFiles, true));
            DeselectAllLargeCommand = new RelayCommand(() => SetChecked(LargeFiles, false));
            DeleteLargeCommand      = new RelayCommand(ExecuteDeleteLarge, () => !IsScanning);
            OpenFileLocationCommand = new RelayCommand<string>(OpenFileLocation);

            SelectAllDupesCommand   = new RelayCommand(() => SetDupeChecked(true));
            DeselectAllDupesCommand = new RelayCommand(() => SetDupeChecked(false));
            DeleteDupesCommand      = new RelayCommand(ExecuteDeleteDupes, () => !IsScanning);


            SelectAllSuggestionsCommand   = new RelayCommand(() => SetSuggChecked(true));
            DeselectAllSuggestionsCommand = new RelayCommand(() => SetSuggChecked(false));
            DeleteSuggestionsCommand      = new RelayCommand(ExecuteDeleteSuggestions, () => !IsScanning);

            SendJunkToVaultCommand  = new RelayCommand(ExecuteSendJunkToVault,  () => !IsScanning);
            SendLargeToVaultCommand = new RelayCommand(ExecuteSendLargeToVault, () => !IsScanning);

            PopulateAvailableDrives();
            RefreshDriveInfo();
        }

        // ── Deep scan ─────────────────────────────────────────────────────

        private async void StartDeepScan()
        {
            IsScanning = true; Progress = 0;
            ClearAll();
            _cts = new CancellationTokenSource();
            long minBytes = ParseMinSize(MinFileSizeLabel);

            try
            {
                var result = await _svc.RunFullScanAsync(
                    minBytes,
                    SelectedDrive,
                    msg => Dispatch(() => Status = msg),
                    pct => Dispatch(() => Progress = pct),
                    _cts.Token);

                Dispatch(() =>
                {
                    foreach (var r in result.TopFolders)  TopFolders.Add(r);
                    foreach (var r in result.JunkTargets)
                    {
                        JunkItems.Add(r);
                        r.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(JunkTarget.IsChecked)) UpdateJunkSelectedSize(); };
                    }
                    foreach (var r in result.LargeFiles)
                    {
                        LargeFiles.Add(r);
                        r.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(LargeFileResult.IsChecked)) UpdateLargeSelectedSize(); };
                    }
                    foreach (var r in result.Programs) Programs.Add(r);

                    // Suggestions tab: top safe junk items pre-checked, non-safe unchecked
                    SuggestionItems.Clear();
                    foreach (var r in result.JunkTargets)
                    {
                        r.IsChecked = r.Safe;  // pre-check safe items
                        r.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(JunkTarget.IsChecked)) UpdateSuggSelectedSize(); };
                        SuggestionItems.Add(r);
                    }
                    UpdateSuggSelectedSize();

                    ApplyJunkFilter();

                    SafeTotalStr   = SizeFormatter.Format(result.TotalJunkSafe);
                    ReviewTotalStr = SizeFormatter.Format(result.TotalJunkReview);
                    LargeCountStr  = $"{result.LargeFiles.Count} files";
                    ProgCountStr   = $"{result.Programs.Count} apps";

                    Suggestions = DiskAnalyzerService.BuildSuggestions(result);
                    RefreshDriveInfo();
                    Status = $"Scan complete ✓ — {result.JunkTargets.Count} junk locations | {result.LargeFiles.Count} large files | {result.Programs.Count} programs";
                });
            }
            catch (OperationCanceledException) { Dispatch(() => Status = "Scan cancelled."); }
            finally { Dispatch(() => IsScanning = false); }
        }

        private void CancelScan() { _cts?.Cancel(); Status = "Cancelling…"; }

        // ── Duplicate scan ─────────────────────────────────────────────────

        private async void StartDupScan()
        {
            if (!Directory.Exists(DupRoot)) { Status = $"Path not found: {DupRoot}"; return; }
            IsScanning = true; DupeRows.Clear(); Progress = 0;
            _cts = new CancellationTokenSource();
            try
            {
                var groups = await _svc.FindDuplicatesAsync(
                    DupRoot, msg => Dispatch(() => Status = msg), _cts.Token);

                long totalWaste = 0;
                Dispatch(() =>
                {
                    int toggle = 0;
                    foreach (var g in groups)
                    {
                        totalWaste += g.WastedSize;
                        bool alt = (toggle++ % 2 == 0);
                        // First file — keep (uncheck by default, IsHeader=true)
                        var header = new DupeRow { GroupId=g.GroupId, IsHeader=true, IsDupe=false,
                            FilePath=g.Files[0], FileSize=g.FileSize, Hash=g.Hash,
                            WastedInfo=$"Wasted: {g.WastedStr}", IsChecked=false };
                        DupeRows.Add(header);
                        // Remaining files — duplicates (checked by default)
                        foreach (var f in g.Files.Skip(1))
                        {
                            var row = new DupeRow { GroupId=g.GroupId, IsHeader=false, IsDupe=true,
                                FilePath=f, FileSize=g.FileSize, Hash="(duplicate)",
                                WastedInfo="", IsChecked=true };
                            row.PropertyChanged += (s,e) => { if (e.PropertyName==nameof(DupeRow.IsChecked)) UpdateDupeSelectedSize(); };
                            DupeRows.Add(row);
                        }
                    }
                    UpdateDupeSelectedSize();
                    Status = groups.Count > 0
                        ? $"Found {groups.Count} duplicate groups — {SizeFormatter.Format(totalWaste)} wasted. Duplicates are pre-checked."
                        : "No duplicates found.";
                    Progress = 100;
                });
            }
            catch (OperationCanceledException) { Dispatch(() => Status = "Duplicate scan cancelled."); }
            finally { Dispatch(() => IsScanning = false); }
        }

        // ── DELETE: Junk ───────────────────────────────────────────────────

        private async void ExecuteDeleteJunk()
        {
            var selected = JunkFiltered.Where(j => j.IsChecked).ToList();
            if (selected.Count == 0) { Status = "No items checked — tick checkboxes first."; return; }

            long totalSize = selected.Sum(j => j.Size);
            string msg = $"DELETE {selected.Count} item(s) — {SizeFormatter.Format(totalSize)}?\n\n"
                       + string.Join("\n", selected.Take(8).Select(j => $"  • {j.Label}  ({j.SizeStr})"))
                       + (selected.Count > 8 ? $"\n  … and {selected.Count - 8} more" : "")
                       + "\n\nThis CANNOT be undone.";

            if (MessageBox.Show(msg, "Confirm Deletion", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            IsScanning = true;
            _cts = new CancellationTokenSource();
            try
            {
                var result = await _svc.DeleteJunkAsync(selected, m => Dispatch(() => Status = m), _cts.Token);
                Dispatch(() =>
                {
                    // Remove successfully deleted items from both collections
                    var toRemove = JunkItems.Where(j => j.IsChecked).ToList();
                    foreach (var item in toRemove) { JunkItems.Remove(item); }
                    ApplyJunkFilter();
                    UpdateJunkSelectedSize();
                    RefreshDriveInfo();

                    string summary = $"Deleted {result.DeletedCount} item(s) — freed {result.BytesFreedStr}";
                    if (result.FailedCount > 0)
                        summary += $" ({result.FailedCount} failed — may need Admin rights)";
                    Status = summary;
                    MessageBox.Show(summary + (result.Errors.Count > 0
                        ? "\n\nErrors:\n" + string.Join("\n", result.Errors.Take(5))
                        : ""), "Deletion Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
            catch (OperationCanceledException) { Dispatch(() => Status = "Deletion cancelled."); }
            finally { Dispatch(() => IsScanning = false); }
        }

        // ── DELETE: Large files ────────────────────────────────────────────

        private async void ExecuteDeleteLarge()
        {
            var selected = LargeFiles.Where(f => f.IsChecked).ToList();
            if (selected.Count == 0) { Status = "No files checked — tick checkboxes first."; return; }

            long totalSize = selected.Sum(f => f.Size);
            string msg = $"PERMANENTLY DELETE {selected.Count} file(s) — {SizeFormatter.Format(totalSize)}?\n\n"
                       + string.Join("\n", selected.Take(8).Select(f => $"  • {System.IO.Path.GetFileName(f.Path)}  ({f.SizeStr})"))
                       + (selected.Count > 8 ? $"\n  … and {selected.Count - 8} more" : "")
                       + "\n\nThis CANNOT be undone.";

            if (MessageBox.Show(msg, "Confirm File Deletion", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            IsScanning = true;
            _cts = new CancellationTokenSource();
            try
            {
                var result = await _svc.DeleteLargeFilesAsync(selected, m => Dispatch(() => Status = m), _cts.Token);
                Dispatch(() =>
                {
                    var toRemove = LargeFiles.Where(f => f.IsChecked).ToList();
                    foreach (var item in toRemove) LargeFiles.Remove(item);
                    UpdateLargeSelectedSize();
                    RefreshDriveInfo();

                    string summary = $"Deleted {result.DeletedCount} file(s) — freed {result.BytesFreedStr}";
                    if (result.FailedCount > 0) summary += $" ({result.FailedCount} failed)";
                    Status = summary;
                    MessageBox.Show(summary, "Deletion Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
            catch (OperationCanceledException) { Dispatch(() => Status = "Deletion cancelled."); }
            finally { Dispatch(() => IsScanning = false); }
        }

        // ── DELETE: Duplicates ─────────────────────────────────────────────

        private async void ExecuteDeleteDupes()
        {
            var selected = DupeRows.Where(r => r.IsDupe && r.IsChecked).ToList();
            if (selected.Count == 0) { Status = "No duplicate files checked."; return; }

            long totalSize = selected.Sum(r => r.FileSize);
            string msg = $"DELETE {selected.Count} duplicate file(s) — {SizeFormatter.Format(totalSize)}?\n\n"
                       + string.Join("\n", selected.Take(8).Select(r => $"  • {System.IO.Path.GetFileName(r.FilePath)}"))
                       + (selected.Count > 8 ? $"\n  … and {selected.Count - 8} more" : "")
                       + "\n\nThe FIRST file in each group is kept. This CANNOT be undone.";

            if (MessageBox.Show(msg, "Confirm Duplicate Deletion", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            IsScanning = true;
            _cts = new CancellationTokenSource();
            try
            {
                var result = await _svc.DeleteDupesAsync(DupeRows, m => Dispatch(() => Status = m), _cts.Token);
                Dispatch(() =>
                {
                    var toRemove = DupeRows.Where(r => r.IsDupe && r.IsChecked).ToList();
                    foreach (var row in toRemove) DupeRows.Remove(row);
                    UpdateDupeSelectedSize();
                    RefreshDriveInfo();

                    string summary = $"Deleted {result.DeletedCount} duplicate(s) — freed {result.BytesFreedStr}";
                    if (result.FailedCount > 0) summary += $" ({result.FailedCount} failed)";
                    Status = summary;
                    MessageBox.Show(summary, "Deletion Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
            catch (OperationCanceledException) { Dispatch(() => Status = "Deletion cancelled."); }
            finally { Dispatch(() => IsScanning = false); }
        }

        // ── Selection helpers ──────────────────────────────────────────────

        private void SetJunkChecked(System.Collections.Generic.IEnumerable<JunkTarget> items, bool value)
        {
            foreach (var j in items) j.IsChecked = value;
            UpdateJunkSelectedSize();
        }

        private void SetChecked<T>(ObservableCollection<T> col, bool value) where T : SelectableItem
        {
            foreach (var item in col) item.IsChecked = value;
        }

        private void SetDupeChecked(bool value)
        {
            foreach (var row in DupeRows.Where(r => r.IsDupe)) row.IsChecked = value;
            UpdateDupeSelectedSize();
        }

        private void UpdateJunkSelectedSize()
        {
            long total = JunkFiltered.Where(j => j.IsChecked).Sum(j => j.Size);
            int  count = JunkFiltered.Count(j => j.IsChecked);
            JunkSelectedStr = count > 0
                ? $"{count} item(s) checked — {SizeFormatter.Format(total)} will be freed"
                : "0 items selected";
        }

        private void UpdateLargeSelectedSize()
        {
            long total = LargeFiles.Where(f => f.IsChecked).Sum(f => f.Size);
            int  count = LargeFiles.Count(f => f.IsChecked);
            LargeSelectedStr = count > 0
                ? $"{count} file(s) checked — {SizeFormatter.Format(total)} will be freed"
                : "0 files selected";
        }

        private void UpdateDupeSelectedSize()
        {
            long total = DupeRows.Where(r => r.IsDupe && r.IsChecked).Sum(r => r.FileSize);
            int  count = DupeRows.Count(r => r.IsDupe && r.IsChecked);
            DupeSelectedStr = count > 0
                ? $"{count} duplicate(s) checked — {SizeFormatter.Format(total)} will be freed"
                : "0 duplicates selected";
        }

        // ── Filter ────────────────────────────────────────────────────────

        private void ApplyJunkFilter()
        {
            JunkFiltered.Clear();
            foreach (var j in JunkItems)
                if (_junkFilter == "All" || j.Category == _junkFilter)
                    JunkFiltered.Add(j);
            UpdateJunkSelectedSize();
        }

        // ── Misc ──────────────────────────────────────────────────────────

        private void PopulateAvailableDrives()
        {
            AvailableDrives.Clear();
            foreach (var drive in System.IO.DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.IsReady)
                        AvailableDrives.Add(drive.RootDirectory.FullName);
                }
                catch { }
            }
            if (AvailableDrives.Count > 0)
                SelectedDrive = AvailableDrives.Contains("C:\\") ? "C:\\" : AvailableDrives[0];
        }

        private void ExecuteSendJunkToVault()
        {
            var paths = JunkFiltered.Where(j => j.IsChecked).Select(j => j.FullPath).ToList();
            if (paths.Count == 0) { Status = "No items checked — tick checkboxes first."; return; }
            SendPathsToVaultCallback?.Invoke(paths);
            Status = $"{paths.Count} path(s) sent to Vault Cleaner custom folders.";
            MessageBox.Show(
                $"Sent {paths.Count} folder path(s) to the Vault Cleaner tab.\n\nSwitch to 🧹 VAULT CLEANER and run \"INITIATE CLEANUP PROTOCOL\" to delete them.",
                "Sent to Vault Cleaner", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ExecuteSendLargeToVault()
        {
            var paths = LargeFiles.Where(f => f.IsChecked).Select(f => f.Folder).Distinct().ToList();
            if (paths.Count == 0) { Status = "No files checked — tick checkboxes first."; return; }
            SendPathsToVaultCallback?.Invoke(paths);
            Status = $"{paths.Count} folder(s) sent to Vault Cleaner custom folders.";
            MessageBox.Show(
                $"Sent {paths.Count} parent folder(s) to the Vault Cleaner tab.\n\nSwitch to 🧹 VAULT CLEANER and run \"INITIATE CLEANUP PROTOCOL\" to clean them.",
                "Sent to Vault Cleaner", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ── Suggestions tab helpers ──────────────────────────────────────

        private void SetSuggChecked(bool value)
        {
            // Only allow checking safe items
            foreach (var j in SuggestionItems)
                if (j.Safe || !value)  // can always uncheck; only safe items get checked
                    j.IsChecked = value;
            UpdateSuggSelectedSize();
        }

        private void UpdateSuggSelectedSize()
        {
            long total = SuggestionItems.Where(j => j.IsChecked && j.Safe).Sum(j => j.Size);
            int  count = SuggestionItems.Count(j => j.IsChecked && j.Safe);
            SuggSelectedStr = count > 0
                ? $"{count} item(s) checked — {SizeFormatter.Format(total)} will be freed"
                : "0 items selected";
        }

        private async void ExecuteDeleteSuggestions()
        {
            // Only delete safe items that are checked
            var selected = SuggestionItems.Where(j => j.IsChecked && j.Safe).ToList();
            if (selected.Count == 0) { Status = "No safe items checked — only ✅ Safe items can be deleted here."; return; }

            long totalSize = selected.Sum(j => j.Size);
            string msg = $"DELETE {selected.Count} safe item(s) — {SizeFormatter.Format(totalSize)}?\n\n"
                       + string.Join("\n", selected.Take(8).Select(j => $"  • {j.Label}  ({j.SizeStr})"))
                       + (selected.Count > 8 ? $"\n  … and {selected.Count - 8} more" : "")
                       + "\n\nThis CANNOT be undone.";

            if (MessageBox.Show(msg, "Confirm Deletion", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            IsScanning = true;
            _cts = new CancellationTokenSource();
            try
            {
                var result = await _svc.DeleteJunkAsync(selected, m => Dispatch(() => Status = m), _cts.Token);
                Dispatch(() =>
                {
                    // Remove deleted items from both Suggestions and JunkItems/JunkFiltered
                    foreach (var item in selected)
                    {
                        SuggestionItems.Remove(item);
                        JunkItems.Remove(item);
                    }
                    ApplyJunkFilter();
                    UpdateSuggSelectedSize();
                    UpdateJunkSelectedSize();
                    RefreshDriveInfo();

                    string summary = $"Deleted {result.DeletedCount} item(s) — freed {result.BytesFreedStr}";
                    if (result.FailedCount > 0)
                        summary += $" ({result.FailedCount} failed — may need Admin rights)";
                    Status = summary;
                    MessageBox.Show(summary + (result.Errors.Count > 0
                        ? "\n\nErrors:\n" + string.Join("\n", result.Errors.Take(5))
                        : ""), "Deletion Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
            catch (OperationCanceledException) { Dispatch(() => Status = "Deletion cancelled."); }
            finally { Dispatch(() => IsScanning = false); }
        }

                private void ClearAll()
        {
            TopFolders.Clear(); JunkItems.Clear(); JunkFiltered.Clear();
            LargeFiles.Clear(); Programs.Clear(); DupeRows.Clear(); SuggestionItems.Clear();
            JunkSelectedStr  = "0 B selected";
            LargeSelectedStr = "0 B selected";
            DupeSelectedStr  = "0 B selected";
            SuggSelectedStr  = "0 B selected";
        }

        public void RefreshDriveInfo()
        {
            try
            {
                string drive = string.IsNullOrEmpty(_selectedDrive) ? "C:\\" : _selectedDrive;
                var d = new System.IO.DriveInfo(drive);
                double free  = d.AvailableFreeSpace / 1073741824.0;
                double total = d.TotalSize          / 1073741824.0;
                double pct   = (total - free) / total * 100;
                DriveInfo = $"{drive.TrimEnd('\\')}   {free:F1} GB FREE  /  {total:F1} GB TOTAL   ({pct:F1}% USED)";
            }
            catch { DriveInfo = $"{_selectedDrive} — unable to read drive info"; }
        }

        private long ParseMinSize(string label)
        {
            if (label == "10 MB")  return 10L  * 1024 * 1024;
            if (label == "50 MB")  return 50L  * 1024 * 1024;
            if (label == "500 MB") return 500L * 1024 * 1024;
            if (label == "1 GB")   return 1L   * 1024 * 1024 * 1024;
            return 100L * 1024 * 1024;
        }

        private void OpenFileLocation(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
        }

        private void Dispatch(Action a) =>
            System.Windows.Application.Current?.Dispatcher?.Invoke(a);

        private static void Raise(ICommand cmd)
        {
            if (cmd is RelayCommand rc) rc.RaiseCanExecuteChanged();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void RaisePC(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ── Generic RelayCommand<T> ───────────────────────────────────────────
    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T> _execute;
        private readonly Func<T, bool> _canExecute;
        public RelayCommand(Action<T> execute, Func<T, bool> canExecute = null)
        { _execute = execute; _canExecute = canExecute; }
        public bool CanExecute(object p) => _canExecute == null || (p is T t && _canExecute(t));
        public void Execute(object p)    => _execute(p is T t ? t : default);
        public event EventHandler CanExecuteChanged
        {
            add    { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
    }
}
