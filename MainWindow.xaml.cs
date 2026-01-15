using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;


namespace BorderlandsStorageCleaner
{
    public partial class MainWindow : Window
    {
        // NOTE: Most UI logic has been moved to MainWindowViewModel
        // This code-behind now focuses only on window chrome and essential UI interactions
        
        private readonly string logDir = @"D:\VaultHunterLogs";
        private string logFile;

        // Window resizing variables
        private const int WM_SYSCOMMAND = 0x112;
        private HwndSource hwndSource;
        
        private enum ResizeDirection
        {
            Left = 1,
            Right = 2,
            Top = 3,
            TopLeft = 4,
            TopRight = 5,
            Bottom = 6,
            BottomLeft = 7,
            BottomRight = 8,
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            
            // Wire up ViewModel for MVVM data binding
            DataContext = new MainWindowViewModel();
            
            this.SourceInitialized += MainWindow_SourceInitialized;

            logFile = System.IO.Path.Combine(logDir, $"cleanup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            InitializeBorderlandsUI();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Initialize WebView2 controls
                await loaderStandby.EnsureCoreWebView2Async();
                await loaderActive.EnsureCoreWebView2Async();

                // Disable default context menu and other browser features
                loaderStandby.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                loaderStandby.CoreWebView2.Settings.AreDevToolsEnabled = false;
                loaderStandby.CoreWebView2.Settings.IsStatusBarEnabled = false;
                
                loaderActive.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                loaderActive.CoreWebView2.Settings.AreDevToolsEnabled = false;
                loaderActive.CoreWebView2.Settings.IsStatusBarEnabled = false;

                // Get absolute path to resources
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string standbyPath = Path.Combine(baseDir, "Resources", "loader_standby.html");
                string activePath = Path.Combine(baseDir, "Resources", "loader_active.html");

                // Navigate to local HTML files
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

        private void InitializeBorderlandsUI()
        {
            if (IsAdministrator())
            {
                txtAdminStatus.Text = "[ADMIN PRIVILEGES: ACTIVE]";
                txtAdminStatus.Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 0));
            }
            else
            {
                txtAdminStatus.Text = "[ADMIN PRIVILEGES: INACTIVE]";
                txtAdminStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 0, 0));
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

            Directory.CreateDirectory(logDir);
            File.AppendAllText(logFile, logMessage + Environment.NewLine);

            Dispatcher.Invoke(() =>
            {
                txtLog.AppendText(logMessage + Environment.NewLine);
                txtLog.ScrollToEnd();
            });
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
                try
                {
                    this.DragMove();
                }
                catch (InvalidOperationException) { }
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Reserved for future keyboard shortcuts
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try
                {
                    this.DragMove();
                }
                catch (InvalidOperationException) { }
            }
        }

        private void MinimizeWindow(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void CloseWindow(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void OpenDiskAnalyzer(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Disk Analyzer has been removed.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Legacy event handlers - these delegate to the ViewModel's commands
        
        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = DataContext as MainWindowViewModel;
            if (viewModel?.StartCleaningCommand.CanExecute(null) == true)
            {
                viewModel.StartCleaningCommand.Execute(null);
            }
        }

        private void BtnQuickScan_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = DataContext as MainWindowViewModel;
            if (viewModel?.QuickScanCommand.CanExecute(null) == true)
            {
                viewModel.QuickScanCommand.Execute(null);
            }
        }

        private void BtnAbort_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = DataContext as MainWindowViewModel;
            if (viewModel?.AbortCleaningCommand.CanExecute(null) == true)
            {
                viewModel.AbortCleaningCommand.Execute(null);
            }
        }
    }
}