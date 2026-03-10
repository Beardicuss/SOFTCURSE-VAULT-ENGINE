using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace SoftcurseVaultCleaner
{
    /// <summary>
    /// Checks a remote version.json to determine if an update is available.
    /// </summary>
    public class UpdateService
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        public class UpdateInfo
        {
            public bool IsAvailable { get; set; }
            public string CurrentVersion { get; set; }
            public string NewVersion { get; set; }
            public string DownloadUrl { get; set; }
            public string Changelog { get; set; }
            public string Error { get; set; }
        }

        private class VersionPayload
        {
            public string version { get; set; }
            public string downloadUrl { get; set; }
            public string changelog { get; set; }
        }

        public static string GetCurrentVersion()
        {
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "0.0.0";
        }

        public static async Task<UpdateInfo> CheckForUpdateAsync(string url = null)
        {
            string currentVersion = GetCurrentVersion();
            var info = new UpdateInfo { CurrentVersion = currentVersion };

            try
            {
                url ??= AppSettings.Instance.UpdateUrl;
                string json = await _http.GetStringAsync(url);
                var payload = JsonSerializer.Deserialize<VersionPayload>(json);

                if (payload == null || string.IsNullOrEmpty(payload.version))
                {
                    info.Error = "Invalid version data from server.";
                    return info;
                }

                info.NewVersion = payload.version;
                info.DownloadUrl = payload.downloadUrl ?? "";
                info.Changelog = payload.changelog ?? "";

                // Compare versions
                if (Version.TryParse(currentVersion, out var curVer) &&
                    Version.TryParse(payload.version, out var newVer))
                {
                    info.IsAvailable = newVer > curVer;
                }
            }
            catch (HttpRequestException ex)
            {
                info.Error = $"Network error: {ex.Message}";
            }
            catch (TaskCanceledException)
            {
                info.Error = "Update check timed out.";
            }
            catch (Exception ex)
            {
                info.Error = $"Update check failed: {ex.Message}";
            }

            return info;
        }
    }
}
