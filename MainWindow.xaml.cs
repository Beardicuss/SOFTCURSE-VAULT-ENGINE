using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace BorderlandsStorageCleaner
{
    public partial class MainWindow : Window
    {
        // Log directory uses %LOCALAPPDATA% instead of hardcoded D: drive
        private readonly string logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoftcurseVaultCleaner", "Logs");
        private string logFile;

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
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await loaderStandby.EnsureCoreWebView2Async();
                await loaderActive.EnsureCoreWebView2Async();

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
            }
            else
            {
                txtAdminStatus.Text = "[ADMIN PRIVILEGES: INACTIVE]";
                txtAdminStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(255, 0, 0));
            }

            LogMessage("=== SOFTCURSE VAULT CLEANER v2.2 INITIALIZED ===");
            LogMessage("SYSTEM: MVVM architecture active");
            LogMessage("READY: Awaiting cleanup protocol initiation");
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

        private void MinimizeWindow(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void CloseWindow(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
    }
}