using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SoftcurseVaultCleaner
{
    public sealed record PrivilegedMaintenanceResult(bool Succeeded, bool Cancelled, string Message);

    public sealed class PrivilegedMaintenanceService
    {
        private const string HelperFileName = "Softcurse.PrivilegedMaintenanceHelper.exe";

        public async Task<PrivilegedMaintenanceResult> RunComponentCleanupAsync(CancellationToken token = default)
        {
            string helperPath = Path.Combine(AppContext.BaseDirectory, HelperFileName);
            if (!File.Exists(helperPath))
                return new(false, false, "The privileged maintenance helper is missing from the application directory.");

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = helperPath,
                        Arguments = "component-cleanup",
                        WorkingDirectory = AppContext.BaseDirectory,
                        UseShellExecute = true,
                        Verb = "runas"
                    }
                };

                process.Start();
                await process.WaitForExitAsync(token);
                return process.ExitCode == 0
                    ? new(true, false, "Windows component cleanup completed.")
                    : new(false, false, $"The maintenance helper exited with code {process.ExitCode}.");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return new(false, true, "Administrator approval was cancelled; no privileged maintenance was performed.");
            }
            catch (OperationCanceledException)
            {
                return new(false, true, "The maintenance request was cancelled.");
            }
            catch (Exception ex)
            {
                return new(false, false, $"Unable to start privileged maintenance: {ex.Message}");
            }
        }
    }
}
