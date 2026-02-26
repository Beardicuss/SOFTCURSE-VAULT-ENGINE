using System;
using System.IO;
using System.Windows;

namespace BorderlandsStorageCleaner
{
    public partial class App : Application
    {
        private readonly string logDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoftcurseVaultCleaner", "Logs");
        
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            // Set up global exception handlers
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            
            Directory.CreateDirectory(logDir);
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogException(e.Exception);
            
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nThe error has been logged to:\n{logDir}",
                "SOFTCURSE VAULT CLEANER - ERROR",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogException(ex);
                
                MessageBox.Show(
                    $"A critical error occurred:\n\n{ex.Message}\n\nThe application will now close.\n\nError log: {logDir}",
                    "SOFTCURSE VAULT CLEANER - CRITICAL ERROR",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void LogException(Exception ex)
        {
            try
            {
                string logFile = Path.Combine(logDir, $"error-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                string errorMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] EXCEPTION\n" +
                                    $"Message: {ex.Message}\n" +
                                    $"Type: {ex.GetType().FullName}\n" +
                                    $"Stack Trace:\n{ex.StackTrace}\n" +
                                    $"Inner Exception: {ex.InnerException?.Message ?? "None"}\n" +
                                    new string('=', 80) + "\n";
                
                File.AppendAllText(logFile, errorMessage);
            }
            catch
            {
                // If we can't log, at least don't crash
            }
        }
    }
}
