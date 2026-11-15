using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace UptimeInstaller
{
    public partial class MainWindow : Window
    {
        private const int StepWelcome = 0, StepLicense = 1, StepFolder = 2, StepInstalling = 3, StepCompleted = 4;

        private int _currentPage = StepWelcome;
        private bool _isUpgradeFlow;
        private bool _isUninstallFlow; // started with /uninstall, e.g. from Settings > Apps
        private readonly InstallerEngine _engine;

        public MainWindow() : this(new InstallerEngine())
        {
        }

        public MainWindow(InstallerEngine engine)
        {
            _engine = engine;
            InitializeComponent();

            RadioAllUsers.IsChecked = _engine.IsAllUsers;
            RadioCurrentUser.IsChecked = !_engine.IsAllUsers;
            ChkStartup.IsChecked = _engine.EnableStartup;
            TxtInstallPath.Text = _engine.GetTargetInstallPath();

            DetectExistingInstallation();

            if (_engine.IsUninstall && _engine.StartPage == 0)
            {
                ConfigureUninstallFlow();
            }

            if (_engine.StartPage > 0)
            {
                _currentPage = _engine.StartPage;
            }

            UpdateWizardLayout();

            // Entered via the elevated relaunch: run straight away
            if (_currentPage == StepInstalling)
            {
                if (_engine.IsUninstall)
                {
                    StartUninstallation(TxtInstallPath.Text.Trim(), RadioAllUsers.IsChecked == true);
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
            ExistingInstall? existing = _engine.DetectExistingInstallation();
            if (existing == null) return;

            _isUpgradeFlow = true;

            // Keep scope and settings of the existing install so an upgrade never silently changes them
            // (unless the command line chose them, e.g. the Settings > Apps uninstall command or an elevated relaunch)
            if (!_engine.IsScopeExplicit)
            {
                if (existing.IsAllUsers) RadioAllUsers.IsChecked = true; else RadioCurrentUser.IsChecked = true;
                _engine.IsAllUsers = existing.IsAllUsers;
            }

            if (string.IsNullOrWhiteSpace(_engine.CustomInstallPath))
            {
                TxtInstallPath.Text = existing.Directory;
            }
            if (_engine.StartPage == 0)
            {
                ChkStartup.IsChecked = existing.StartsAtLogin;
            }

            WelcomeTitle.Text = "Upgrade Uptime";
            WelcomeDescription.Text = $"An existing installation of Uptime was detected at:\n{existing.Directory}\n\nClick Upgrade to update to the latest version.";
            WelcomeFooter.Text = "Click Upgrade to continue, or Uninstall to remove Uptime.";
            BtnNext.Content = "Upgrade";
        }

        private void ConfigureUninstallFlow()
        {
            _isUninstallFlow = true;
            WelcomeTitle.Text = "Uninstall Uptime";
            WelcomeDescription.Text = $"Uptime will be removed from:\n{ResolveInstallPath()}\n\nYour restart reminder preference is kept in case you reinstall.";
            WelcomeFooter.Text = "Click Uninstall to continue.";
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            try { DragMove(); } catch (InvalidOperationException) { /* button released before the drag started */ }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage == StepInstalling)
            {
                MessageBox.Show("Please wait until the installation process is complete.", "Installation in Progress", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Close();
        }

        private void ChkAcceptLicense_Changed(object sender, RoutedEventArgs e)
        {
            if (_currentPage == StepLicense)
            {
                BtnNext.IsEnabled = ChkAcceptLicense.IsChecked == true;
            }
        }

        private void UpdateWizardLayout()
        {
            PageWelcome.Visibility = _currentPage == StepWelcome ? Visibility.Visible : Visibility.Collapsed;
            PageLicense.Visibility = _currentPage == StepLicense ? Visibility.Visible : Visibility.Collapsed;
            PageFolder.Visibility = _currentPage == StepFolder ? Visibility.Visible : Visibility.Collapsed;
            PageInstalling.Visibility = _currentPage == StepInstalling ? Visibility.Visible : Visibility.Collapsed;
            PageCompleted.Visibility = _currentPage == StepCompleted ? Visibility.Visible : Visibility.Collapsed;

            bool navigable = _currentPage != StepInstalling && _currentPage != StepCompleted;
            BtnBack.Visibility = navigable && _currentPage > StepWelcome ? Visibility.Visible : Visibility.Collapsed;
            BtnCancel.Visibility = navigable ? Visibility.Visible : Visibility.Collapsed;
            BtnNext.Visibility = _currentPage == StepInstalling ? Visibility.Collapsed : Visibility.Visible;
            BtnUninstall.Visibility = _currentPage == StepWelcome && _isUpgradeFlow && !_isUninstallFlow ? Visibility.Visible : Visibility.Collapsed;

            switch (_currentPage)
            {
                case StepWelcome:
                    BtnNext.Content = _isUninstallFlow ? "Uninstall" : _isUpgradeFlow ? "Upgrade" : "Next";
                    BtnNext.IsEnabled = true;
                    break;
                case StepLicense:
                    BtnNext.Content = "Next";
                    BtnNext.IsEnabled = ChkAcceptLicense.IsChecked == true;
                    break;
                case StepFolder:
                    BtnNext.Content = "Install";
                    BtnNext.IsEnabled = true;
                    break;
                case StepCompleted:
                    BtnNext.Content = "Finish";
                    BtnNext.IsEnabled = true;
                    break;
            }
        }

        private void GoToPage(int page)
        {
            _currentPage = page;
            UpdateWizardLayout();
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage == StepWelcome && _isUninstallFlow)
            {
                BeginUninstall(); // the page itself is the confirmation
            }
            else if ((_currentPage == StepWelcome && _isUpgradeFlow) || _currentPage == StepFolder)
            {
                StartInstallation();
            }
            else if (_currentPage == StepCompleted)
            {
                FinishInstallation();
            }
            else
            {
                GoToPage(_currentPage + 1);
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > StepWelcome)
            {
                GoToPage(_currentPage - 1);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

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

        private string ResolveInstallPath()
        {
            string installPath = TxtInstallPath.Text.Trim();
            return string.IsNullOrEmpty(installPath) ? _engine.GetTargetInstallPath() : installPath;
        }

        /// <summary>Runs the engine on a worker thread, reporting progress to the wizard. Returns the error, if any.</summary>
        private async Task<Exception?> RunWithProgressAsync(Action<Action<string, int>> work)
        {
            InstallProgressBar.Value = 0;

            void Report(string status, int percent)
            {
                Dispatcher.Invoke(() =>
                {
                    TxtStatus.Text = status;
                    InstallProgressBar.Value = percent;
                });
                Thread.Sleep(200); // brief pacing so each step is readable
            }

            try
            {
                await Task.Run(() => work(Report));
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private async void StartInstallation()
        {
            string installPath = ResolveInstallPath();
            bool enableStartup = ChkStartup.IsChecked == true;
            bool isAllUsers = RadioAllUsers.IsChecked == true;

            try
            {
                installPath = Path.GetFullPath(installPath);
            }
            catch (Exception)
            {
                MessageBox.Show("The installation folder is not a valid path.", "Invalid Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                GoToPage(StepFolder);
                return;
            }

            if (_engine.NeedsElevation(installPath, isAllUsers))
            {
                string args = _engine.BuildElevationArguments(installPath, isAllUsers, enableStartup, uninstall: false, silent: false, launch: false);
                if (InstallerEngine.RunElevated(args, wait: false) != null)
                {
                    Application.Current.Shutdown();
                }
                else
                {
                    MessageBox.Show("Administrator privileges are required for system-wide installation to Program Files.",
                        "Elevation Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    GoToPage(StepFolder);
                }
                return;
            }

            GoToPage(StepInstalling);
            InstallingTitle.Text = _isUpgradeFlow ? "Upgrading Uptime..." : "Installing Uptime...";

            Exception? error = await RunWithProgressAsync(report =>
                _engine.PerformInstall(installPath, enableStartup, isAllUsers, launchApp: false, report));

            if (error != null)
            {
                MessageBox.Show($"An error occurred during installation:\n\n{error.Message}",
                    "Installation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                GoToPage(_isUpgradeFlow ? StepWelcome : StepFolder);
                return;
            }

            TxtInstallPath.Text = installPath;
            GoToPage(StepCompleted);
        }

        private void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult confirm = MessageBox.Show(
                "Are you sure you want to completely uninstall Uptime?",
                "Confirm Uninstallation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            BeginUninstall();
        }

        private void BeginUninstall()
        {
            string installPath = ResolveInstallPath();
            bool isAllUsers = RadioAllUsers.IsChecked == true;
            bool enableStartup = ChkStartup.IsChecked == true;

            if (_engine.NeedsElevation(installPath, isAllUsers))
            {
                string args = _engine.BuildElevationArguments(installPath, isAllUsers, enableStartup, uninstall: true, silent: false, launch: false);
                if (InstallerEngine.RunElevated(args, wait: false) != null)
                {
                    Application.Current.Shutdown();
                }
                else
                {
                    MessageBox.Show("Administrator privileges are required to uninstall a system-wide installation.",
                        "Elevation Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }

            StartUninstallation(installPath, isAllUsers);
        }

        private async void StartUninstallation(string installPath, bool isAllUsers)
        {
            GoToPage(StepInstalling);
            InstallingTitle.Text = "Uninstalling Uptime...";

            Exception? error = await RunWithProgressAsync(report => _engine.PerformUninstall(installPath, isAllUsers, report));

            if (error != null)
            {
                MessageBox.Show($"An error occurred during uninstallation:\n\n{error.Message}",
                    "Uninstallation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                GoToPage(StepWelcome);
                return;
            }

            CompletedTitle.Text = "Uninstallation Completed";
            CompletedDescription.Text = "Uptime has been successfully removed from your computer.";
            ChkLaunchOnFinish.IsChecked = false;
            ChkLaunchOnFinish.Visibility = Visibility.Collapsed;
            GoToPage(StepCompleted);
        }

        private void FinishInstallation()
        {
            if (ChkLaunchOnFinish.IsChecked == true)
            {
                InstallerEngine.LaunchApplication(Path.Combine(ResolveInstallPath(), "Uptime.exe"));
            }
            Close();
        }
    }
}
