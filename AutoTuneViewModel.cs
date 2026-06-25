using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace SoftcurseVaultCleaner
{
    public class StartupProgItem : INotifyPropertyChanged
    {
        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked != value) { _isChecked = value; OnPropertyChanged(nameof(IsChecked)); } }
        }

        public string Name { get; set; }
        public string Path { get; set; }
        public string Location { get; set; } // "HKCU", "HKLM", "Startup Folder"

        // For deletion
        public string RegistryKey { get; set; }
        public string RegistryValueName { get; set; }
        public string ShortcutPath { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    public class RegistryIssueItem : INotifyPropertyChanged
    {
        private bool _isChecked = true; // Auto-check for deletion
        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked != value) { _isChecked = value; OnPropertyChanged(nameof(IsChecked)); } }
        }

        public string Type { get; set; }        // e.g. "MUICache", "Run Key"
        public string ProblemData { get; set; } // The missing file path
        
        // For deletion
        public string RegistryKey { get; set; }
        public string RegistryValueName { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    public class AutoTuneViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<StartupProgItem> StartupItems { get; set; } = new ObservableCollection<StartupProgItem>();
        public ObservableCollection<RegistryIssueItem> RegistryIssues { get; set; } = new ObservableCollection<RegistryIssueItem>();

        private string _status = "Standby";
        public string Status
        {
            get => _status;
            set { if (_status != value) { _status = value; OnPropertyChanged(nameof(Status)); } }
        }

        private bool _isScanning = false;
        public bool IsScanning
        {
            get => _isScanning;
            set { if (_isScanning != value) { _isScanning = value; OnPropertyChanged(nameof(IsScanning)); } }
        }

        // AutoClean properties synced with AppSettings
        public bool EnableAutoClean
        {
            get => AppSettings.Instance.DefaultEnableAutoClean;
            set 
            { 
                if (AppSettings.Instance.DefaultEnableAutoClean != value) 
                { 
                    AppSettings.Instance.DefaultEnableAutoClean = value; 
                    OnPropertyChanged(nameof(EnableAutoClean)); 
                } 
            }
        }

        // Commands
        public ICommand ScanStartupCommand { get; }
        public ICommand DeleteSelectedStartupCommand { get; }
        public ICommand ScanRegistryCommand { get; }
        public ICommand FixSelectedRegistryCommand { get; }

        public AutoTuneViewModel()
        {
            ScanStartupCommand = new RelayCommand(async () => await ScanStartupAsync(), () => !IsScanning);
            DeleteSelectedStartupCommand = new RelayCommand(() => DeleteSelectedStartup(), () => !IsScanning);
            
            ScanRegistryCommand = new RelayCommand(async () => await ScanRegistryAsync(), () => !IsScanning);
            FixSelectedRegistryCommand = new RelayCommand(() => FixSelectedRegistry(), () => !IsScanning);
        }

        // ═══════════════════════════════════════════════════════════════
        //  STARTUP MANAGER
        // ═══════════════════════════════════════════════════════════════
        private async Task ScanStartupAsync()
        {
            IsScanning = true;
            Status = "Scanning startup locations...";
            StartupItems.Clear();

            await Task.Run(() =>
            {
                // 1. HKCU Run
                ScanRegistryRunKey(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKCU");
                
                // 2. HKLM Run
                ScanRegistryRunKey(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKLM");

                // 3. User Startup Folder
                ScanStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "User Startup Folder");
                
                // 4. Common Startup Folder
                ScanStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "All Users Startup");
            });

            Status = $"Scanned {StartupItems.Count} startup programs.";
            IsScanning = false;
            CommandManager.InvalidateRequerySuggested();
        }

        private void ScanRegistryRunKey(RegistryKey root, string subKeyPath, string locationName)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(subKeyPath, false))
                {
                    if (key != null)
                    {
                        foreach (string valueName in key.GetValueNames())
                        {
                            string rawPath = key.GetValue(valueName)?.ToString() ?? "";
                            App.Current.Dispatcher.Invoke(() =>
                            {
                                StartupItems.Add(new StartupProgItem
                                {
                                    Name = valueName,
                                    Path = rawPath,
                                    Location = locationName,
                                    RegistryKey = $@"{root.Name}\{subKeyPath}",
                                    RegistryValueName = valueName
                                });
                            });
                        }
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"Error scanning {root.Name}: {ex.Message}"); }
        }

        private void ScanStartupFolder(string folderPath, string locationName)
        {
            try
            {
                if (Directory.Exists(folderPath))
                {
                    foreach (string file in Directory.GetFiles(folderPath, "*.*"))
                    {
                        if (file.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) continue;

                        App.Current.Dispatcher.Invoke(() =>
                        {
                            StartupItems.Add(new StartupProgItem
                            {
                                Name = Path.GetFileNameWithoutExtension(file),
                                Path = file,
                                Location = locationName,
                                ShortcutPath = file
                            });
                        });
                    }
                }
            }
            catch { }
        }

        private void DeleteSelectedStartup()
        {
            var toDelete = StartupItems.Where(i => i.IsChecked).ToList();
            if (toDelete.Count == 0) return;

            int deletedAccount = 0;
            foreach (var item in toDelete)
            {
                try
                {
                    if (!string.IsNullOrEmpty(item.ShortcutPath) && File.Exists(item.ShortcutPath))
                    {
                        File.Delete(item.ShortcutPath);
                        deletedAccount++;
                        StartupItems.Remove(item);
                    }
                    else if (!string.IsNullOrEmpty(item.RegistryKey) && !string.IsNullOrEmpty(item.RegistryValueName))
                    {
                        string[] parts = item.RegistryKey.Split('\\', 2);
                        RegistryKey root = parts[0] == "HKEY_LOCAL_MACHINE" ? Registry.LocalMachine : Registry.CurrentUser;
                        using (RegistryKey key = root.OpenSubKey(parts[1], true))
                        {
                            if (key != null)
                            {
                                key.DeleteValue(item.RegistryValueName, false);
                                deletedAccount++;
                                App.Current.Dispatcher.Invoke(() => StartupItems.Remove(item));
                            }
                        }
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"Failed to delete startup {item.Name}: {ex.Message}"); }
            }
            Status = $"Deleted {deletedAccount} startup items.";
        }

        // ═══════════════════════════════════════════════════════════════
        //  REGISTRY SWEEPER (Orphaned File Paths)
        // ═══════════════════════════════════════════════════════════════
        private async Task ScanRegistryAsync()
        {
            IsScanning = true;
            Status = "Scanning registry for missing files...";
            RegistryIssues.Clear();

            await Task.Run(() =>
            {
                // MUICache - Very safe to clean
                ScanMissingPathsInKey(Registry.ClassesRoot, @"Local Settings\Software\Microsoft\Windows\Shell\MuiCache", "MUICache");

                // HKCU Run Key (orphaned runners)
                ScanMissingPathsInKey(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKCU Run Key");
            });

            Status = $"Found {RegistryIssues.Count} invalid registry items.";
            IsScanning = false;
            CommandManager.InvalidateRequerySuggested();
        }

        private void ScanMissingPathsInKey(RegistryKey root, string subKeyPath, string typeName)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(subKeyPath, false))
                {
                    if (key != null)
                    {
                        foreach (string valName in key.GetValueNames())
                        {
                            if (valName == "LanguageChoice" || valName.Contains("@")) continue; // Skip non-paths

                            // MUICache keys literally are the paths. Run keys have paths in the value.
                            string possiblePath = valName; 
                            if (typeName.Contains("Run Key"))
                            {
                                possiblePath = key.GetValue(valName)?.ToString() ?? "";
                                // Extract first quoted string or raw path
                                if (possiblePath.StartsWith("\""))
                                {
                                    int end = possiblePath.IndexOf('\"', 1);
                                    if (end > 0) possiblePath = possiblePath.Substring(1, end - 1);
                                }
                                else
                                {
                                    possiblePath = possiblePath.Split(' ')[0]; // crude space split
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(possiblePath) && (possiblePath.Contains(":\\") || possiblePath.Contains(":\\\\")))
                            {
                                // Remove trailing .FriendlyAppName if it's MUICache
                                if (possiblePath.EndsWith(".FriendlyAppName"))
                                    possiblePath = possiblePath.Replace(".FriendlyAppName", "");
                                if (possiblePath.EndsWith(".ApplicationCompany"))
                                    possiblePath = possiblePath.Replace(".ApplicationCompany", "");

                                if (!File.Exists(possiblePath) && !Directory.Exists(possiblePath))
                                {
                                    App.Current.Dispatcher.Invoke(() =>
                                    {
                                        RegistryIssues.Add(new RegistryIssueItem
                                        {
                                            Type = typeName,
                                            ProblemData = possiblePath,
                                            RegistryKey = $@"{root.Name}\{subKeyPath}",
                                            RegistryValueName = valName
                                        });
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"Error scanning Registry {typeName}: {ex.Message}"); }
        }

        private void FixSelectedRegistry()
        {
            var toFix = RegistryIssues.Where(i => i.IsChecked).ToList();
            if (toFix.Count == 0) return;

            int fixedCount = 0;
            foreach (var item in toFix)
            {
                try
                {
                    string[] parts = item.RegistryKey.Split('\\', 2);
                    RegistryKey root = Registry.CurrentUser;
                    if (parts[0] == "HKEY_LOCAL_MACHINE") root = Registry.LocalMachine;
                    if (parts[0] == "HKEY_CLASSES_ROOT") root = Registry.ClassesRoot;

                    using (RegistryKey key = root.OpenSubKey(parts[1], true))
                    {
                        if (key != null)
                        {
                            key.DeleteValue(item.RegistryValueName, false);
                            fixedCount++;
                            App.Current.Dispatcher.Invoke(() => RegistryIssues.Remove(item));
                        }
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"Failed to fix registry key {item.RegistryValueName}: {ex.Message}"); }
            }
            Status = $"Fixed {fixedCount} invalid registry items.";
        }

        // ═══════════════════════════════════════════════════════════════
        //  INotifyPropertyChanged
        // ═══════════════════════════════════════════════════════════════
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
