using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.ComponentModel;

namespace SoftcurseVaultCleaner
{
    /// <summary>
    /// Manages subscription license validation and persistence.
    /// License keys are validated against a remote API and cached locally.
    /// </summary>
    public class LicenseService : INotifyPropertyChanged
    {
        private static readonly string LicenseDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoftcurseVaultCleaner");
        private static readonly string LicenseFile = Path.Combine(LicenseDir, "license.dat");

        private static LicenseService _instance;
        public static LicenseService Instance => _instance ??= new LicenseService();

        public class LicenseInfo
        {
            public string Key { get; set; } = "";
            public string Email { get; set; } = "";
            public string Plan { get; set; } = "Free";
            public DateTime? ExpiresAt { get; set; }
            public bool IsValid { get; set; }
            public DateTime ActivatedAt { get; set; }
        }

        private LicenseInfo _license = new LicenseInfo();
        private bool _isProUser;
        private string _statusMessage = "Free Plan";

        public bool IsProUser
        {
            get => _isProUser;
            private set { _isProUser = value; OnPropertyChanged(nameof(IsProUser)); OnPropertyChanged(nameof(PlanDisplayName)); }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set { _statusMessage = value; OnPropertyChanged(nameof(StatusMessage)); }
        }

        public string PlanDisplayName => IsProUser ? $"PRO ({_license.Plan})" : "FREE";

        public LicenseInfo CurrentLicense => _license;

        public LicenseService()
        {
            LoadStoredLicense();
        }

        /// <summary>
        /// Attempt to activate a license key.
        /// For now uses offline validation; replace with API call later.
        /// </summary>
        public async Task<(bool Success, string Message)> ActivateAsync(string key, string email)
        {
            if (string.IsNullOrWhiteSpace(key))
                return (false, "Please enter a license key.");

            // TODO: Replace with actual API validation
            // Example: var response = await httpClient.PostAsync("https://api.softcurse.com/validate", ...);
            
            // Offline validation placeholder — accepts keys matching pattern
            await Task.Delay(500); // Simulate network call

            bool valid = !string.IsNullOrWhiteSpace(key) && key.Length >= 16;

            if (valid)
            {
                _license = new LicenseInfo
                {
                    Key = key,
                    Email = email,
                    Plan = "Pro",
                    IsValid = true,
                    ActivatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddYears(1)
                };

                SaveLicense();
                IsProUser = true;
                StatusMessage = $"Pro Plan — Expires {_license.ExpiresAt:MMM dd, yyyy}";
                AppSettings.Instance.LicenseKey = key;
                AppSettings.Instance.LicenseEmail = email;
                return (true, "License activated successfully! 🎉");
            }
            else
            {
                StatusMessage = "Invalid license key";
                return (false, "Invalid license key. Please check and try again.");
            }
        }

        public void Deactivate()
        {
            _license = new LicenseInfo();
            IsProUser = false;
            StatusMessage = "Free Plan";
            AppSettings.Instance.LicenseKey = "";
            AppSettings.Instance.LicenseEmail = "";
            try { if (File.Exists(LicenseFile)) File.Delete(LicenseFile); } catch { }
        }

        private void LoadStoredLicense()
        {
            try
            {
                if (File.Exists(LicenseFile))
                {
                    string json = File.ReadAllText(LicenseFile);
                    var stored = JsonSerializer.Deserialize<LicenseInfo>(json);
                    if (stored != null && stored.IsValid)
                    {
                        // Check expiration
                        if (stored.ExpiresAt.HasValue && stored.ExpiresAt.Value > DateTime.UtcNow)
                        {
                            _license = stored;
                            IsProUser = true;
                            StatusMessage = $"Pro Plan — Expires {stored.ExpiresAt:MMM dd, yyyy}";
                        }
                        else
                        {
                            StatusMessage = "License expired. Please renew.";
                        }
                    }
                }
            }
            catch { }
        }

        private void SaveLicense()
        {
            try
            {
                Directory.CreateDirectory(LicenseDir);
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_license, options);
                File.WriteAllText(LicenseFile, json);
            }
            catch { }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
