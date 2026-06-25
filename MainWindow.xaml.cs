using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace SoftcurseVaultCleaner
{
    public partial class MainWindow : Window
    {
        // Log directory uses %LOCALAPPDATA% instead of hardcoded D: drive
        private readonly string logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoftcurseVaultCleaner", "Logs");
        private string logFile;

        // System tray icon
        private Forms.NotifyIcon _trayIcon;

        // Window resizing variables
        private const int WM_SYSCOMMAND = 0x112;
        private HwndSource hwndSource;

        private enum ResizeDirection
        {
            Left = 1, Right = 2, Top = 3,
            TopLeft = 4, TopRight = 5,
            Bottom = 6, BottomLeft = 7, BottomRight = 8,
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;

            DataContext = new MainWindowViewModel();

            this.SourceInitialized += MainWindow_SourceInitialized;

            logFile = Path.Combine(logDir, $"cleanup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            InitializeUI();
            InitializeTrayIcon();

            // Start minimized if setting enabled
            if (AppSettings.Instance.StartMinimized)
            {
                this.WindowState = WindowState.Minimized;
                this.ShowInTaskbar = false;
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Fix for Error: We couldn't create the data directory.
                // Redirects WebView2's data folder from the local Program Files dir to LocalAppData.
                string userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoftcurseVaultCleaner", "WebView2");
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userDataFolder);

                await loaderStandby.EnsureCoreWebView2Async(env);
                await loaderActive.EnsureCoreWebView2Async(env);

                loaderStandby.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                loaderStandby.CoreWebView2.Settings.AreDevToolsEnabled = false;
                loaderStandby.CoreWebView2.Settings.IsStatusBarEnabled = false;

                loaderActive.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                loaderActive.CoreWebView2.Settings.AreDevToolsEnabled = false;
                loaderActive.CoreWebView2.Settings.IsStatusBarEnabled = false;

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string standbyPath = Path.Combine(baseDir, "Resources", "loader_standby.html");
                string activePath = Path.Combine(baseDir, "Resources", "loader_active.html");

                if (File.Exists(standbyPath))
                    loaderStandby.CoreWebView2.Navigate(standbyPath);

                if (File.Exists(activePath))
                    loaderActive.CoreWebView2.Navigate(activePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WebView2 Init Failed: {ex.Message}");
                // I10: Gracefully hide WebView panels if runtime is missing
                try { loaderStandby.Visibility = Visibility.Collapsed; } catch { }
                try { loaderActive.Visibility = Visibility.Collapsed; } catch { }
            }
        }

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        }

        private void InitializeUI()
        {
            if (IsAdministrator())
            {
                txtAdminStatus.Text = "[ADMIN PRIVILEGES: ACTIVE]";
                txtAdminStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0, 255, 0));
                if (btnRestartAdmin != null) btnRestartAdmin.Visibility = Visibility.Collapsed;
            }
            else
            {
                txtAdminStatus.Text = "[ADMIN PRIVILEGES: INACTIVE]";
                txtAdminStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(255, 0, 0));
                if (btnRestartAdmin != null) btnRestartAdmin.Visibility = Visibility.Visible;
            }

            LogMessage("=== SOFTCURSE VAULT CLEANER v3.0 INITIALIZED ===");
            LogMessage("SYSTEM: MVVM architecture active");
            LogMessage("READY: Awaiting cleanup protocol initiation");

            // First-run onboarding
            CheckFirstRun();
        }

        private void CheckFirstRun()
        {
            if (!AppSettings.Instance.HasCompletedFirstRun)
            {
                AppSettings.Instance.HasCompletedFirstRun = true;
                MessageBox.Show(
                    "Welcome to Softcurse Vault Cleaner! \u267b\n\n" +
                    "\u2022 \ud83e\uddf9 VAULT CLEANER \u2014 Quick cleanup of temp files, caches, and logs\n" +
                    "\u2022 \ud83d\udcbd DISK ANALYZER \u2014 Deep scan with junk finder, duplicates, and large files\n" +
                    "\u2022 \ud83d\udc51 SUBSCRIPTION \u2014 Unlock Pro features with a license key\n" +
                    "\u2022 \u2753 FAQ \u2014 Common questions and answers\n" +
                    "\u2022 \u2699\ufe0f SETTINGS \u2014 Customize your cleanup preferences\n\n" +
                    "TIP: Run as Administrator for full cleanup access.\n" +
                    "TIP: Close the app to minimize to the system tray.",
                    "Welcome to Vault Cleaner", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void InitializeTrayIcon()
        {
            _trayIcon = new Forms.NotifyIcon
            {
                Text = "Softcurse Vault Cleaner",
                Visible = false
            };

            // Use the application icon — check Resources\ subfolder first (CopyToOutputDirectory layout)
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string iconPath = Path.Combine(baseDir, "Resources", "vault.ico");
                if (!File.Exists(iconPath))
                    iconPath = Path.Combine(baseDir, "vault.ico");
                if (File.Exists(iconPath))
                    _trayIcon.Icon = new System.Drawing.Icon(iconPath);
                else
                    _trayIcon.Icon = System.Drawing.SystemIcons.Application;
            }
            catch { _trayIcon.Icon = System.Drawing.SystemIcons.Application; }

            // Context menu
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("\ud83d\udcc2 Open", null, (s, e) => RestoreFromTray());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("\u274c Exit", null, (s, e) => ExitApplication());
            _trayIcon.ContextMenuStrip = menu;

            // Double-click to restore
            _trayIcon.DoubleClick += (s, e) => RestoreFromTray();
        }

        private void RestoreFromTray()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.ShowInTaskbar = true;
            _trayIcon.Visible = false;
            this.Activate();
        }

        private void ExitApplication()
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            Application.Current.Shutdown();
        }

        private bool IsAdministrator()
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        private void LogMessage(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string logMessage = $"[{timestamp}] {message}";

            try
            {
                Directory.CreateDirectory(logDir);
                File.AppendAllText(logFile, logMessage + Environment.NewLine);
            }
            catch { }

            Dispatcher.Invoke(() =>
            {
                var vm = DataContext as MainWindowViewModel;
                vm?.AddLogMessage(message);
            });
        }

        // Auto-scroll log textbox to bottom when content changes
        private void TxtLog_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                textBox.ScrollToEnd();
            }
        }

        // Folder picker — opens dialog and adds selected folder to ViewModel
        private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select a folder to clean",
                ShowNewFolderButton = false
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var vm = DataContext as MainWindowViewModel;
                vm?.AddCustomFolder(dialog.SelectedPath);
            }
        }

        // Duplicates tab — browse for scan folder
        private void BtnBrowseDupFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select folder to scan for duplicates",
                ShowNewFolderButton = false
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var mainVm = DataContext as MainWindowViewModel;
                var analyzerVm = mainVm?.DiskAnalyzer;
                if (analyzerVm != null)
                    analyzerVm.DupRoot = dialog.SelectedPath;
            }
        }

        // Window chrome event handlers

        private void ResizeWindow(object sender, MouseButtonEventArgs e)
        {
            System.Windows.Shapes.Rectangle rectangle = sender as System.Windows.Shapes.Rectangle;
            if (rectangle != null)
            {
                switch (rectangle.Cursor.ToString())
                {
                    case "SizeNWSE":
                        if (rectangle.HorizontalAlignment == HorizontalAlignment.Left)
                            ResizeWindow(ResizeDirection.BottomLeft);
                        else
                            ResizeWindow(ResizeDirection.BottomRight);
                        break;
                    case "SizeNESW":
                        if (rectangle.HorizontalAlignment == HorizontalAlignment.Left)
                            ResizeWindow(ResizeDirection.TopLeft);
                        else
                            ResizeWindow(ResizeDirection.TopRight);
                        break;
                    case "SizeWE":
                        if (rectangle.HorizontalAlignment == HorizontalAlignment.Left)
                            ResizeWindow(ResizeDirection.Left);
                        else
                            ResizeWindow(ResizeDirection.Right);
                        break;
                    case "SizeNS":
                        if (rectangle.VerticalAlignment == VerticalAlignment.Top)
                            ResizeWindow(ResizeDirection.Top);
                        else
                            ResizeWindow(ResizeDirection.Bottom);
                        break;
                }
            }
        }

        private void ResizeWindow(ResizeDirection direction)
        {
            if (hwndSource != null)
            {
                SendMessage(hwndSource.Handle, WM_SYSCOMMAND, (IntPtr)(61440 + direction), IntPtr.Zero);
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try { this.DragMove(); }
                catch (InvalidOperationException) { }
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Reserved for future keyboard shortcuts
        }

        // I9: Restart application with admin privileges
        private void BtnRestartAsAdmin_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath != null)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = true,
                        Verb = "runas"
                    });
                    Application.Current.Shutdown();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Admin restart failed: {ex.Message}");
            }
        }

        private void MinimizeWindow(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
            this.Hide();
            this.ShowInTaskbar = false;
            _trayIcon.Visible = true;
        }

        private void CloseWindow(object sender, RoutedEventArgs e)
        {
            ExitApplication();
        }

        protected override void OnClosed(EventArgs e)
        {
            _trayIcon?.Dispose();
            base.OnClosed(e);
        }
    }
}