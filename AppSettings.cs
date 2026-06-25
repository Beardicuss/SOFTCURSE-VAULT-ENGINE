using System;
using System.IO;
using System.Text.Json;
using System.ComponentModel;

namespace SoftcurseVaultCleaner
{
    /// <summary>
    /// Persistent application settings stored as JSON in %APPDATA%.
    /// </summary>
    public class AppSettings : INotifyPropertyChanged
    {
        private static readonly string SettingsDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoftcurseVaultCleaner");
        private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

        private static AppSettings _instance;
        public static AppSettings Instance => _instance ??= Load();

        // ── General ─────────────────────────────────────────────────────
        private bool _checkUpdatesOnStartup = true;
        private string _defaultDrive = "C:\\";
        private bool _startMinimized = false;

        // ── Cleanup defaults ────────────────────────────────────────────
        private bool _defaultCleanTemp = true;
        private bool _defaultCleanCache = true;
        private bool _defaultCleanLogs = true;
        private bool _defaultCleanRecycleBin = false;
        private bool _defaultCleanPrefetch = true;
        private bool _defaultUseRecycleBin = false;

        private bool _defaultCleanDevTools = false;
        private bool _defaultCleanGaming = false;
        private bool _defaultCleanSystemDumps = true;
        private bool _defaultCleanDNS = false;
        private bool _defaultCleanExtreme = false;
        private bool _defaultEnableAutoClean = false;
        private bool _defaultDeepScanMode = false;


        // ── Appearance ──────────────────────────────────────────────────
        private int _logFontSize = 11;

        // ── Storage ─────────────────────────────────────────────────────
        private int _maxLogLines = 5000;

        // ── License ─────────────────────────────────────────────────────
        private string _licenseKey = "";
        private string _licenseEmail = "";

        // ── Update ──────────────────────────────────────────────────────
        private string _updateUrl = "https://raw.githubusercontent.com/softcurse/vault-cleaner/main/version.json";

        // ── Onboarding ──────────────────────────────────────────────────
        private bool _hasCompletedFirstRun = false;

        // ═══════════════════════════════════════════════════════════════
        //  PROPERTIES (with change notification + auto-save)
        // ═══════════════════════════════════════════════════════════════

        public bool CheckUpdatesOnStartup
        {
            get => _checkUpdatesOnStartup;
            set { if (_checkUpdatesOnStartup != value) { _checkUpdatesOnStartup = value; OnChanged(); } }
        }

        public string DefaultDrive
        {
            get => _defaultDrive;
            set { if (_defaultDrive != value) { _defaultDrive = value; OnChanged(); } }
        }

        public bool StartMinimized
        {
            get => _startMinimized;
            set { if (_startMinimized != value) { _startMinimized = value; OnChanged(); } }
        }

        public bool DefaultCleanTemp
        {
            get => _defaultCleanTemp;
            set { if (_defaultCleanTemp != value) { _defaultCleanTemp = value; OnChanged(); } }
        }

        public bool DefaultCleanCache
        {
            get => _defaultCleanCache;
            set { if (_defaultCleanCache != value) { _defaultCleanCache = value; OnChanged(); } }
        }

        public bool DefaultCleanLogs
        {
            get => _defaultCleanLogs;
            set { if (_defaultCleanLogs != value) { _defaultCleanLogs = value; OnChanged(); } }
        }

        public bool DefaultCleanRecycleBin
        {
            get => _defaultCleanRecycleBin;
            set { if (_defaultCleanRecycleBin != value) { _defaultCleanRecycleBin = value; OnChanged(); } }
        }

        public bool DefaultCleanPrefetch
        {
            get => _defaultCleanPrefetch;
            set { if (_defaultCleanPrefetch != value) { _defaultCleanPrefetch = value; OnChanged(); } }
        }

        public bool DefaultUseRecycleBin
        {
            get => _defaultUseRecycleBin;
            set { if (_defaultUseRecycleBin != value) { _defaultUseRecycleBin = value; OnChanged(); } }
        }

        public bool DefaultCleanDevTools
        {
            get => _defaultCleanDevTools;
            set { if (_defaultCleanDevTools != value) { _defaultCleanDevTools = value; OnChanged(); } }
        }

        public bool DefaultCleanGaming
        {
            get => _defaultCleanGaming;
            set { if (_defaultCleanGaming != value) { _defaultCleanGaming = value; OnChanged(); } }
        }

        public bool DefaultCleanSystemDumps
        {
            get => _defaultCleanSystemDumps;
            set { if (_defaultCleanSystemDumps != value) { _defaultCleanSystemDumps = value; OnChanged(); } }
        }

        public bool DefaultCleanDNS
        {
            get => _defaultCleanDNS;
            set { if (_defaultCleanDNS != value) { _defaultCleanDNS = value; OnChanged(); } }
        }

        public bool DefaultCleanExtreme
        {
            get => _defaultCleanExtreme;
            set { if (_defaultCleanExtreme != value) { _defaultCleanExtreme = value; OnChanged(); } }
        }

        public bool DefaultEnableAutoClean
        {
            get => _defaultEnableAutoClean;
            set { if (_defaultEnableAutoClean != value) { _defaultEnableAutoClean = value; OnChanged(); } }
        }

        public bool DefaultDeepScanMode
        {
            get => _defaultDeepScanMode;
            set { if (_defaultDeepScanMode != value) { _defaultDeepScanMode = value; OnChanged(); } }
        }

        public int LogFontSize
        {
            get => _logFontSize;
            set { int v = Math.Clamp(value, 8, 24); if (_logFontSize != v) { _logFontSize = v; OnChanged(); } }
        }

        public int MaxLogLines
        {
            get => _maxLogLines;
            set { if (_maxLogLines != value) { _maxLogLines = value; OnChanged(); } }
        }

        public string LicenseKey
        {
            get => _licenseKey;
            set { if (_licenseKey != value) { _licenseKey = value; OnChanged(); } }
        }

        public string LicenseEmail
        {
            get => _licenseEmail;
            set { if (_licenseEmail != value) { _licenseEmail = value; OnChanged(); } }
        }

        public string UpdateUrl
        {
            get => _updateUrl;
            set { if (_updateUrl != value) { _updateUrl = value; OnChanged(); } }
        }

        public bool HasCompletedFirstRun
        {
            get => _hasCompletedFirstRun;
            set { if (_hasCompletedFirstRun != value) { _hasCompletedFirstRun = value; OnChanged(); } }
        }

        // ═══════════════════════════════════════════════════════════════
        //  LOAD / SAVE
        // ═══════════════════════════════════════════════════════════════

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string json = File.ReadAllText(SettingsFile);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        _instance = settings;
                        return settings;
                    }
                }
            }
            catch { /* corrupt file — use defaults */ }

            _instance = new AppSettings();
            return _instance;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(SettingsFile, json);
            }
            catch { /* non-critical — settings just won't persist */ }
        }

        public void Reset()
        {
            var fresh = new AppSettings();
            CheckUpdatesOnStartup = fresh.CheckUpdatesOnStartup;
            DefaultDrive = fresh.DefaultDrive;
            StartMinimized = fresh.StartMinimized;
            DefaultCleanTemp = fresh.DefaultCleanTemp;
            DefaultCleanCache = fresh.DefaultCleanCache;
            DefaultCleanLogs = fresh.DefaultCleanLogs;
            DefaultCleanRecycleBin = fresh.DefaultCleanRecycleBin;
            DefaultCleanPrefetch = fresh.DefaultCleanPrefetch;
            DefaultUseRecycleBin = fresh.DefaultUseRecycleBin;
            LogFontSize = fresh.LogFontSize;
            MaxLogLines = fresh.MaxLogLines;
            
            DefaultCleanDevTools = fresh.DefaultCleanDevTools;
            DefaultCleanGaming = fresh.DefaultCleanGaming;
            DefaultCleanSystemDumps = fresh.DefaultCleanSystemDumps;
            DefaultCleanDNS = fresh.DefaultCleanDNS;
            DefaultCleanExtreme = fresh.DefaultCleanExtreme;
            DefaultEnableAutoClean = fresh.DefaultEnableAutoClean;
            DefaultDeepScanMode = fresh.DefaultDeepScanMode;
            
            Save();
        }

        // ═══════════════════════════════════════════════════════════════
        //  INotifyPropertyChanged
        // ═══════════════════════════════════════════════════════════════

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnChanged([System.Runtime.CompilerServices.CallerMemberName] string prop = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
            Save(); // auto-persist on every change
        }
    }
}
