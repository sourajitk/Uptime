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
        private bool _isUpgradeFlow = false;
        private string _existingInstallPath = "";
        private readonly InstallerEngine _engine;

        public MainWindow() : this(new InstallerEngine())
        {
        }

        public MainWindow(InstallerEngine engine)
        {
            _engine = engine;
            InitializeComponent();

            if (_engine.IsAllUsers)
            {
                if (RadioAllUsers != null) RadioAllUsers.IsChecked = true;
            }
            else
            {
                if (RadioCurrentUser != null) RadioCurrentUser.IsChecked = true;
            }

            TxtInstallPath.Text = _engine.GetTargetInstallPath();

            DetectExistingInstallation();

            if (_engine.StartPage > 0)
            {
                _currentPage = _engine.StartPage;
            }

            UpdateWizardLayout();

            if (_currentPage == 3)
            {
                if (_engine.IsUninstall)
                {
                    StartUninstallation(_engine.GetTargetInstallPath(), _engine.IsAllUsers);
                }
                else
                {
                    StartInstallation();
                }
            }
        }

        private void InstallScope_Changed(object sender, RoutedEventArgs e)
        {
            if (RadioAllUsers == null || RadioCurrentUser == null || TxtInstallPath == null || _engine == null) return;

            _engine.IsAllUsers = RadioAllUsers.IsChecked == true;
            TxtInstallPath.Text = _engine.GetTargetInstallPath();
        }

        private void DetectExistingInstallation()
        {
            try
            {
                string? detectedPath = null;

                // 1. Check HKCU startup run registry key
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (key != null)
                    {
                        string? val = (key.GetValue("Uptime") ?? key.GetValue("UptimeTaskbarApp")) as string;
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

                // 2. Check HKLM startup run registry key
                if (string.IsNullOrEmpty(detectedPath))
                {
                    using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    {
                        if (key != null)
                        {
                            string? val = (key.GetValue("Uptime") ?? key.GetValue("UptimeTaskbarApp")) as string;
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
                }

                // 3. Fall back to checking default install paths
                if (string.IsNullOrEmpty(detectedPath))
                {
                    string currentUserExe1 = Path.Combine(_engine.DefaultCurrentUserPath, "Uptime.exe");
                    string currentUserExe2 = Path.Combine(_engine.DefaultCurrentUserPath, "UptimeTaskbarApp.exe");
                    string allUsersExe1 = Path.Combine(_engine.DefaultAllUsersPath, "Uptime.exe");
                    string allUsersExe2 = Path.Combine(_engine.DefaultAllUsersPath, "UptimeTaskbarApp.exe");

                    if (File.Exists(currentUserExe1) || File.Exists(currentUserExe2))
                    {
                        detectedPath = _engine.DefaultCurrentUserPath;
                    }
                    else if (File.Exists(allUsersExe1) || File.Exists(allUsersExe2))
                    {
                        detectedPath = _engine.DefaultAllUsersPath;
                        _engine.IsAllUsers = true;
                        if (RadioAllUsers != null) RadioAllUsers.IsChecked = true;
                    }
                }

                // If found, initialize the upgrade flow
                if (!string.IsNullOrEmpty(detectedPath) && Directory.Exists(detectedPath))
                {
                    _isUpgradeFlow = true;
                    _existingInstallPath = detectedPath;
                    TxtInstallPath.Text = detectedPath;

                    WelcomeTitle.Text = "Upgrade Uptime";
                    WelcomeDescription.Text = $"An existing installation of Uptime was detected at:\n{_existingInstallPath}\n\nClick Upgrade to update to the latest version.";
                    WelcomeFooter.Text = "Click Upgrade to continue. If you want to uninstall, press on uninstall";
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

            // Toggle BtnUninstall visibility (only visible on Welcome page in upgrade flow)
            if (BtnUninstall != null)
            {
                BtnUninstall.Visibility = (_currentPage == 0 && _isUpgradeFlow) ? Visibility.Visible : Visibility.Collapsed;
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
            bool isAllUsers = RadioAllUsers.IsChecked == true;

            if (string.IsNullOrEmpty(installPath))
            {
                installPath = _engine.GetTargetInstallPath();
            }

            if ((isAllUsers || InstallerEngine.IsSystemDirectory(installPath)) && !InstallerEngine.IsAdministrator())
            {
                string args = $"/page=3 /dir=\"{installPath}\" {(isAllUsers ? "/allusers" : "/currentuser")} /launch";
                if (InstallerEngine.ElevateSelf(args))
                {
                    Application.Current.Shutdown();
                    return;
                }
                else
                {
                    MessageBox.Show("Administrator privileges are required for system-wide installation to Program Files.",
                        "Elevation Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    _currentPage = 2; // Return to selection
                    UpdateWizardLayout();
                    return;
                }
            }

            if (InstallingTitle != null)
            {
                InstallingTitle.Text = _isUpgradeFlow ? "Upgrading Uptime..." : "Installing Uptime...";
            }

            InstallProgressBar.Value = 0;

            await Task.Run(async () =>
            {
                try
                {
                    UpdateStatus("Closing running instances...", 10);
                    _engine.KillRunningInstances("UptimeTaskbarApp");
                    _engine.KillRunningInstances("Uptime");
                    await Task.Delay(500);

                    UpdateStatus("Preparing target directory...", 30);
                    if (Directory.Exists(installPath))
                    {
                        _engine.CleanDirectory(installPath);
                    }
                    else
                    {
                        Directory.CreateDirectory(installPath);
                    }
                    await Task.Delay(300);

                    UpdateStatus("Extracting files...", 60);
                    string targetExePath = Path.Combine(installPath, "Uptime.exe");
                    _engine.ExtractEmbeddedApp(targetExePath);
                    await Task.Delay(300);

                    UpdateStatus("Creating shortcuts...", 80);
                    _engine.CreateStartMenuShortcut(targetExePath, installPath, isAllUsers);

                    UpdateStatus("Configuring startup settings...", 95);
                    _engine.ConfigureStartup(targetExePath, enableStartup, isAllUsers);
                    await Task.Delay(300);

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

        private void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult confirm = MessageBox.Show(
                "Are you sure you want to completely uninstall Uptime?",
                "Confirm Uninstallation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            string installPath = TxtInstallPath.Text.Trim();
            bool isAllUsers = RadioAllUsers.IsChecked == true;
            if (string.IsNullOrEmpty(installPath))
            {
                installPath = _engine.GetTargetInstallPath();
            }

            if ((isAllUsers || InstallerEngine.IsSystemDirectory(installPath)) && !InstallerEngine.IsAdministrator())
            {
                string args = $"/uninstall /page=3 /dir=\"{installPath}\" {(isAllUsers ? "/allusers" : "/currentuser")}";
                if (InstallerEngine.ElevateSelf(args))
                {
                    Application.Current.Shutdown();
                    return;
                }
            }

            StartUninstallation(installPath, isAllUsers);
        }

        private async void StartUninstallation(string installPath, bool isAllUsers)
        {
            _currentPage = 3;
            UpdateWizardLayout();

            if (InstallingTitle != null)
            {
                InstallingTitle.Text = "Uninstalling Uptime...";
            }

            InstallProgressBar.Value = 0;

            await Task.Run(async () =>
            {
                try
                {
                    UpdateStatus("Closing running instances...", 20);
                    _engine.KillRunningInstances("UptimeTaskbarApp");
                    _engine.KillRunningInstances("Uptime");
                    await Task.Delay(500);

                    UpdateStatus("Removing startup configurations...", 40);
                    _engine.ConfigureStartup("", false, isAllUsers);
                    await Task.Delay(300);

                    UpdateStatus("Removing shortcuts...", 60);
                    _engine.RemoveStartMenuShortcut();
                    await Task.Delay(300);

                    UpdateStatus("Deleting files...", 80);
                    if (Directory.Exists(installPath))
                    {
                        _engine.CleanDirectory(installPath);
                        try
                        {
                            Directory.Delete(installPath, true);
                        }
                        catch { }
                    }
                    await Task.Delay(300);

                    UpdateStatus("Finalizing...", 100);
                    await Task.Delay(300);

                    Dispatcher.Invoke(() =>
                    {
                        CompletedTitle.Text = "Uninstallation Completed";
                        CompletedDescription.Text = "Uptime has been successfully removed from your computer.";
                        ChkLaunchOnFinish.IsChecked = false;
                        ChkLaunchOnFinish.Visibility = Visibility.Collapsed;

                        _currentPage = 4;
                        UpdateWizardLayout();
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"An error occurred during uninstallation:\n\n{ex.Message}",
                            "Uninstallation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                        _currentPage = 0;
                        UpdateWizardLayout();
                    });
                }
            });
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
                        installPath = _engine.GetTargetInstallPath();
                    }
                    string exePath = Path.Combine(installPath, "Uptime.exe");
                    if (!File.Exists(exePath))
                    {
                        exePath = Path.Combine(installPath, "UptimeTaskbarApp.exe");
                    }

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
                catch { }
            }

            this.Close();
        }
    }
}