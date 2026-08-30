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
        private bool _checkUpdatesOnStartup = false;
        private bool _startMinimized = false;

        // ── Cleanup defaults ────────────────────────────────────────────
        private bool _defaultCleanTemp = false;
        private bool _defaultCleanCache = false;
        private bool _defaultCleanRecycleBin = false;

        private bool _defaultCleanDevTools = false;
        private bool _defaultCleanGaming = false;
        private bool _defaultCleanSystemDumps = false;
        private bool _defaultCleanDNS = false;
        private bool _defaultCleanExtreme = false;
        // ── Appearance ──────────────────────────────────────────────────
        private int _logFontSize = 11;

        // ── Onboarding ──────────────────────────────────────────────────
        private bool _hasCompletedFirstRun = false;
        private bool _phase1SafetyDefaultsMigrated = false;

        // ═══════════════════════════════════════════════════════════════
        //  PROPERTIES (with change notification + auto-save)
        // ═══════════════════════════════════════════════════════════════

        public bool CheckUpdatesOnStartup
        {
            get => _checkUpdatesOnStartup;
            set { if (_checkUpdatesOnStartup != value) { _checkUpdatesOnStartup = value; OnChanged(); } }
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

        public bool DefaultCleanRecycleBin
        {
            get => _defaultCleanRecycleBin;
            set { if (_defaultCleanRecycleBin != value) { _defaultCleanRecycleBin = value; OnChanged(); } }
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

        public int LogFontSize
        {
            get => _logFontSize;
            set { int v = Math.Clamp(value, 8, 24); if (_logFontSize != v) { _logFontSize = v; OnChanged(); } }
        }

        public bool HasCompletedFirstRun
        {
            get => _hasCompletedFirstRun;
            set { if (_hasCompletedFirstRun != value) { _hasCompletedFirstRun = value; OnChanged(); } }
        }

        public bool Phase1SafetyDefaultsMigrated
        {
            get => _phase1SafetyDefaultsMigrated;
            set { if (_phase1SafetyDefaultsMigrated != value) { _phase1SafetyDefaultsMigrated = value; OnChanged(); } }
        }

        // ═══════════════════════════════════════════════════════════════
        //  LOAD / SAVE
        // ═══════════════════════════════════════════════════════════════

        public static AppSettings Load()
        {
            RemoveLegacyPlaintextLicense();
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

        private static void RemoveLegacyPlaintextLicense()
        {
            try
            {
                string legacyLicense = Path.Combine(SettingsDir, "license.dat");
                if (File.Exists(legacyLicense))
                    File.Delete(legacyLicense);
            }
            catch
            {
                // The obsolete license is never trusted, even if cleanup is blocked by the OS.
            }
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
            StartMinimized = fresh.StartMinimized;
            DefaultCleanTemp = fresh.DefaultCleanTemp;
            DefaultCleanCache = fresh.DefaultCleanCache;
            DefaultCleanRecycleBin = fresh.DefaultCleanRecycleBin;
            LogFontSize = fresh.LogFontSize;
            
            DefaultCleanDevTools = fresh.DefaultCleanDevTools;
            DefaultCleanGaming = fresh.DefaultCleanGaming;
            DefaultCleanSystemDumps = fresh.DefaultCleanSystemDumps;
            DefaultCleanDNS = fresh.DefaultCleanDNS;
            DefaultCleanExtreme = fresh.DefaultCleanExtreme;
            Phase1SafetyDefaultsMigrated = true;
            
            Save();
        }

        public void ApplyPhase1SafetyMigration()
        {
            if (_phase1SafetyDefaultsMigrated) return;

            _defaultCleanTemp = false;
            _defaultCleanCache = false;
            _defaultCleanRecycleBin = false;
            _defaultCleanDevTools = false;
            _defaultCleanGaming = false;
            _defaultCleanSystemDumps = false;
            _defaultCleanDNS = false;
            _defaultCleanExtreme = false;
            _phase1SafetyDefaultsMigrated = true;
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
