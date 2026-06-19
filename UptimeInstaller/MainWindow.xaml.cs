using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace UptimeInstaller
{
    public partial class MainWindow : Window
    {
        private int _currentPage = 0;
        private string _defaultInstallFolder = "";
        private bool _isUpgradeFlow = false;
        private string _existingInstallPath = "";

        public MainWindow()
        {
            InitializeComponent();

            // Set default installation path to local user programs folder (non-admin friendly)
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _defaultInstallFolder = Path.Combine(localAppData, "Programs", "UptimeTaskbarApp");
            TxtInstallPath.Text = _defaultInstallFolder;

            DetectExistingInstallation();

            UpdateWizardLayout();
        }

        private void DetectExistingInstallation()
        {
            try
            {
                string? detectedPath = null;

                // 1. Check startup run registry key
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (key != null)
                    {
                        string? val = key.GetValue("UptimeTaskbarApp") as string;
                        if (!string.IsNullOrEmpty(val))
                        {
                            string cleanPath = val.Replace("\"", "").Trim();
                            if (File.Exists(cleanPath))
                            {
                                detectedPath = Path.GetDirectoryName(cleanPath);
                            }
                        }
                    }
                }

                // 2. Fall back to checking default install path
                if (string.IsNullOrEmpty(detectedPath))
                {
                    string defaultExePath = Path.Combine(_defaultInstallFolder, "UptimeTaskbarApp.exe");
                    string legacyExePath = Path.Combine(_defaultInstallFolder, "Uptime.exe");
                    if (File.Exists(defaultExePath))
                    {
                        detectedPath = _defaultInstallFolder;
                    }
                    else if (File.Exists(legacyExePath))
                    {
                        detectedPath = _defaultInstallFolder;
                    }
                }

                // If found, initialize the upgrade flow
                if (!string.IsNullOrEmpty(detectedPath) && Directory.Exists(detectedPath))
                {
                    _isUpgradeFlow = true;
                    _existingInstallPath = detectedPath;
                    TxtInstallPath.Text = detectedPath;

                    WelcomeTitle.Text = "Upgrade Uptime";
                    WelcomeDescription.Text = $"An existing installation of Uptime Taskbar App was detected at:\n{_existingInstallPath}\n\nClick Upgrade to update to the latest version.";
                    WelcomeFooter.Text = "Click Upgrade to continue.";
                    BtnNext.Content = "Upgrade";
                }
            }
            catch
            {
                // Silence detection errors to avoid blocking the install
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                this.DragMove();
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage == 3) // PageInstalling
            {
                MessageBox.Show("Please wait until the installation process is complete.", "Installation in Progress", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            this.Close();
        }

        private void ChkAcceptLicense_Changed(object sender, RoutedEventArgs e)
        {
            if (_currentPage == 1) // PageLicense
            {
                BtnNext.IsEnabled = ChkAcceptLicense.IsChecked == true;
            }
        }

        private void UpdateWizardLayout()
        {
            // Toggle Page Panels Visibility
            PageWelcome.Visibility = _currentPage == 0 ? Visibility.Visible : Visibility.Collapsed;
            PageLicense.Visibility = _currentPage == 1 ? Visibility.Visible : Visibility.Collapsed;
            PageFolder.Visibility = _currentPage == 2 ? Visibility.Visible : Visibility.Collapsed;
            PageInstalling.Visibility = _currentPage == 3 ? Visibility.Visible : Visibility.Collapsed;
            PageCompleted.Visibility = _currentPage == 4 ? Visibility.Visible : Visibility.Collapsed;

            // Toggle Navigation Buttons States and Visibility
            if (_currentPage == 0) // Welcome
            {
                BtnBack.Visibility = Visibility.Collapsed;
                BtnCancel.Visibility = Visibility.Visible;
                BtnNext.Visibility = Visibility.Visible;
                BtnNext.Content = _isUpgradeFlow ? "Upgrade" : "Next";
                BtnNext.IsEnabled = true;
            }
            else if (_currentPage == 1) // License
            {
                BtnBack.Visibility = Visibility.Visible;
                BtnCancel.Visibility = Visibility.Visible;
                BtnNext.Visibility = Visibility.Visible;
                BtnNext.Content = "Next";
                BtnNext.IsEnabled = ChkAcceptLicense.IsChecked == true;
            }
            else if (_currentPage == 2) // Folder Select
            {
                BtnBack.Visibility = Visibility.Visible;
                BtnCancel.Visibility = Visibility.Visible;
                BtnNext.Visibility = Visibility.Visible;
                BtnNext.Content = "Install";
                BtnNext.IsEnabled = true;
            }
            else if (_currentPage == 3) // Installing
            {
                BtnBack.Visibility = Visibility.Collapsed;
                BtnCancel.Visibility = Visibility.Collapsed;
                BtnNext.Visibility = Visibility.Collapsed;
            }
            else if (_currentPage == 4) // Completed
            {
                BtnBack.Visibility = Visibility.Collapsed;
                BtnCancel.Visibility = Visibility.Collapsed;
                BtnNext.Visibility = Visibility.Visible;
                BtnNext.Content = "Finish";
                BtnNext.IsEnabled = true;
            }
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage == 0 && _isUpgradeFlow)
            {
                _currentPage = 3;
                UpdateWizardLayout();
                StartInstallation();
            }
            else if (_currentPage == 2) // Moving from Folder -> Installing
            {
                _currentPage = 3;
                UpdateWizardLayout();
                StartInstallation();
            }
            else if (_currentPage == 4) // Finish
            {
                FinishInstallation();
            }
            else
            {
                _currentPage++;
                UpdateWizardLayout();
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 0)
            {
                _currentPage--;
                UpdateWizardLayout();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Installation Folder",
                InitialDirectory = TxtInstallPath.Text
            };
            if (dialog.ShowDialog() == true)
            {
                TxtInstallPath.Text = dialog.FolderName;
            }
        }

        private async void StartInstallation()
        {
            string installPath = TxtInstallPath.Text.Trim();
            bool enableStartup = ChkStartup.IsChecked == true;

            if (string.IsNullOrEmpty(installPath))
            {
                installPath = _defaultInstallFolder;
            }

            InstallProgressBar.Value = 0;

            await Task.Run(async () =>
            {
                try
                {
                    // 1. Terminate running processes
                    UpdateStatus("Closing running instances...", 10);
                    KillRunningInstances("UptimeTaskbarApp");
                    KillRunningInstances("Uptime");
                    await Task.Delay(1000); // Give processes time to exit

                    // 2. Erase old folder completely to guarantee no old Uptime.exe remains
                    UpdateStatus("Removing old files...", 30);
                    if (Directory.Exists(installPath))
                    {
                        CleanDirectory(installPath);
                    }
                    else
                    {
                        Directory.CreateDirectory(installPath);
                    }
                    await Task.Delay(500);

                    // 3. Extract Embedded Executable
                    UpdateStatus("Extracting files...", 60);
                    string targetExePath = Path.Combine(installPath, "UptimeTaskbarApp.exe");
                    ExtractEmbeddedApp(targetExePath);
                    await Task.Delay(500);

                    // 4. Create Shortcuts
                    UpdateStatus("Creating shortcuts...", 80);
                    CreateStartMenuShortcut(targetExePath, installPath);

                    // 5. Configure Windows Startup Registry
                    UpdateStatus("Configuring startup settings...", 95);
                    ConfigureStartup(targetExePath, enableStartup);
                    await Task.Delay(500);

                    // Finish
                    UpdateStatus("Finalizing...", 100);
                    await Task.Delay(300);

                    Dispatcher.Invoke(() =>
                    {
                        _currentPage = 4;
                        UpdateWizardLayout();
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"An error occurred during installation:\n\n{ex.Message}", 
                            "Installation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                        _currentPage = 2; // Return to selection
                        UpdateWizardLayout();
                    });
                }
            });
        }

        private void UpdateStatus(string statusText, double progressValue)
        {
            Dispatcher.Invoke(() =>
            {
                TxtStatus.Text = statusText;
                InstallProgressBar.Value = progressValue;
            });
        }

        private void KillRunningInstances(string processName)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch { /* Ignore access or state errors */ }
            }
        }

        private void CleanDirectory(string path)
        {
            // First delete files
            try
            {
                foreach (string file in Directory.GetFiles(path))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                throw new IOException($"Failed to delete files in the old installation folder: {ex.Message}", ex);
            }

            // Then delete subfolders
            try
            {
                foreach (string subDir in Directory.GetDirectories(path))
                {
                    Directory.Delete(subDir, true);
                }
            }
            catch { /* Ignore subfolder deletion failures if minor */ }
        }

        private void ExtractEmbeddedApp(string targetExePath)
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("UptimeTaskbarApp.exe")) ?? "";

            if (string.IsNullOrEmpty(resourceName))
            {
                throw new Exception("Embedded UptimeTaskbarApp.exe was not found inside the installer resources.");
            }

            using (Stream? resourceStream = assembly.GetManifestResourceStream(resourceName))
            {
                if (resourceStream == null)
                {
                    throw new Exception("Embedded resource stream was empty.");
                }

                using (FileStream fileStream = new FileStream(targetExePath, FileMode.Create, FileAccess.Write))
                {
                    resourceStream.CopyTo(fileStream);
                }
            }
        }

        private void CreateStartMenuShortcut(string exePath, string workingDir)
        {
            try
            {
                string startMenuPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs), 
                    "Uptime Taskbar App.lnk");

                // Use native shell link COM wrapper
                ShellLinkHelper.CreateShortcut(startMenuPath, exePath, "Monitors system uptime in the taskbar", workingDir);
            }
            catch { /* Ignore shortcut failures */ }
        }

        private void ConfigureStartup(string exePath, bool enable)
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        if (enable)
                        {
                            key.SetValue("UptimeTaskbarApp", $"\"{exePath}\"");
                        }
                        else
                        {
                            key.DeleteValue("UptimeTaskbarApp", false);
                        }
                    }
                }
            }
            catch { /* Ignore registry permission errors if run under strict per-user sandbox */ }
        }

        private void FinishInstallation()
        {
            if (ChkLaunchOnFinish.IsChecked == true)
            {
                try
                {
                    string installPath = TxtInstallPath.Text.Trim();
                    if (string.IsNullOrEmpty(installPath))
                    {
                        installPath = _defaultInstallFolder;
                    }
                    string exePath = Path.Combine(installPath, "UptimeTaskbarApp.exe");
                    if (File.Exists(exePath))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exePath,
                            WorkingDirectory = installPath,
                            UseShellExecute = true
                        });
                    }
                }
                catch { /* Ignore launch errors */ }
            }

            this.Close();
        }
    }

    // ── Native Windows COM Shortcut Helper ──
    internal static class ShellLinkHelper
    {
        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        internal class ShellLink { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        internal interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010b-0000-0000-C000-000000000046")]
        internal interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            void IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder ppszFileName);
        }

        public static void CreateShortcut(string shortcutPath, string targetPath, string description, string workingDir)
        {
            IShellLinkW link = (IShellLinkW)new ShellLink();
            link.SetPath(targetPath);
            link.SetDescription(description);
            link.SetWorkingDirectory(workingDir);
            IPersistFile file = (IPersistFile)link;
            file.Save(shortcutPath, false);
        }
    }
}