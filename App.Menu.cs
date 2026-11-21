using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;

namespace Uptime
{
    public partial class App
    {
        // Tray menu command IDs. Threshold choices are encoded as CmdThresholdBase + days (0 = never).
        private const uint CmdStartAtLogin = 1;
        private const uint CmdRestart = 2;
        private const uint CmdViewSource = 3;
        private const uint CmdExit = 4;
        private const uint CmdThresholdBase = 1000;

        private const string RepositoryUrl = "https://github.com/sourajitk/Uptime";

        // Preset restart-reminder choices offered in the tray menu (0 = never).
        private static readonly int[] ThresholdChoices = { 3, 7, 14, 30 };

        /// <summary>
        /// Shows the tray menu as a native Win32 popup menu, which Windows 11 renders with its own styling
        /// (rounded corners, Fluent spacing, system font, per-monitor DPI, keyboard and screen-reader support).
        /// </summary>
        private void ShowContextMenu(POINT anchor)
        {
            if (_isMenuOpen) return;

            IntPtr menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) return;

            _isMenuOpen = true;
            try
            {
                // Start at login
                // A machine-wide (HKLM) entry can't be toggled from a standard user session.
                bool startupManagedSystemWide = IsStartupEnabledSystemWide();
                bool startsAtLogin = startupManagedSystemWide || IsStartupEnabledForUser();
                AppendMenu(menu, MF_STRING | (startsAtLogin ? MF_CHECKED : 0) | (startupManagedSystemWide ? MF_GRAYED : 0),
                    (UIntPtr)CmdStartAtLogin, startupManagedSystemWide ? "&Start at login (all users)" : "&Start at login");

                // Restart reminder (submenu with radio choices)
                AppendMenu(menu, MF_POPUP, (UIntPtr)(ulong)BuildThresholdMenu(), "Restart &reminder");

                AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                AppendMenu(menu, MF_STRING, (UIntPtr)CmdRestart, "Restart &computer…");

                // About (submenu)
                AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                IntPtr about = CreatePopupMenu();
                AppendMenu(about, MF_STRING | MF_GRAYED, UIntPtr.Zero, $"Uptime {GetAppVersion()}");
                AppendMenu(about, MF_STRING | MF_GRAYED, UIntPtr.Zero, GetCopyright());
                AppendMenu(about, MF_SEPARATOR, UIntPtr.Zero, null);
                AppendMenu(about, MF_STRING, (UIntPtr)CmdViewSource, "&View source on GitHub");
                AppendMenu(menu, MF_POPUP, (UIntPtr)(ulong)about, "&About Uptime");

                AppendMenu(menu, MF_STRING, (UIntPtr)CmdExit, "E&xit");

                ApplyMenuTheme();

                // Required so the menu closes when the user clicks elsewhere (the shell grants us foreground rights for this click).
                SetForegroundWindow(_messageWindow);

                uint flags = TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY
                             | (GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN);
                uint command = TrackPopupMenuEx(menu, flags, anchor.X, anchor.Y, _messageWindow, IntPtr.Zero);

                // KB135788: forces a task switch so a second right-click doesn't immediately dismiss the next menu.
                PostMessage(_messageWindow, WM_NULL, IntPtr.Zero, IntPtr.Zero);

                // Run the command after the native menu loop has unwound (it may show a dialog or shut down the app).
                if (command != 0)
                {
                    Dispatcher.BeginInvoke(() => ExecuteMenuCommand(command));
                }
            }
            finally
            {
                DestroyMenu(menu); // also destroys the submenus
                _isMenuOpen = false;
            }
        }

        private IntPtr BuildThresholdMenu()
        {
            IntPtr sub = CreatePopupMenu();
            uint itemFlags = _isPolicyEnforced ? MF_STRING | MF_GRAYED : MF_STRING;

            if (_isPolicyEnforced)
            {
                AppendMenu(sub, MF_STRING | MF_GRAYED, UIntPtr.Zero, "Managed by your organization");
                AppendMenu(sub, MF_SEPARATOR, UIntPtr.Zero, null);
            }

            var choices = new List<int>(ThresholdChoices);
            if (_effectiveThresholdDays > 0 && !choices.Contains(_effectiveThresholdDays))
            {
                choices.Add(_effectiveThresholdDays); // keep a custom value configured via registry or policy visible
                choices.Sort();
            }

            foreach (int days in choices)
            {
                AppendMenu(sub, itemFlags, (UIntPtr)ThresholdCommand(days), $"After {FormatDays(days)}");
            }
            AppendMenu(sub, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(sub, itemFlags, (UIntPtr)ThresholdCommand(0), "&Never");

            // Radio bullet on the active choice (a one-item range: the others start unchecked, and the
            // function works on item positions, which don't follow command-ID order here).
            uint active = ThresholdCommand(_effectiveThresholdDays);
            CheckMenuRadioItem(sub, active, active, active, MF_BYCOMMAND);

            return sub;
        }

        private static uint ThresholdCommand(int days) => CmdThresholdBase + (uint)Math.Clamp(days, 0, 60000);

        private void ExecuteMenuCommand(uint command)
        {
            switch (command)
            {
                case CmdStartAtLogin:
                    SetStartup(!IsStartupEnabledForUser());
                    break;
                case CmdRestart:
                    PromptAndRestartComputer();
                    break;
                case CmdViewSource:
                    OpenUrl(RepositoryUrl);
                    break;
                case CmdExit:
                    Current.Shutdown();
                    break;
                case >= CmdThresholdBase when !_isPolicyEnforced:
                    ApplyUserThreshold((int)(command - CmdThresholdBase));
                    break;
            }
        }

        /// <summary>Makes the native menu follow the *apps* light/dark setting (the tray icon follows the *system* setting).</summary>
        private static void ApplyMenuTheme()
        {
            if (Environment.OSVersion.Version.Build < 18362) return; // ordinal 135 had a different signature before 1903

            bool isLightTheme = true;
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int i)
                {
                    isLightTheme = i == 1;
                }
            }
            catch { }

            try
            {
                SetPreferredAppMode(isLightTheme ? PreferredAppModeForceLight : PreferredAppModeForceDark);
                FlushMenuThemes();
            }
            catch { /* Entry point missing: the menu simply stays light */ }
        }

        private static string FormatDays(int days) => days == 1 ? "1 day" : $"{days} days";

        private void ApplyUserThreshold(int days)
        {
            SetUserMaxUptimeDays(days);
            UpdateUptime();
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch { /* Ignore url launch failure */ }
        }

        private static void PromptAndRestartComputer()
        {
            var result = MessageBox.Show(
                "Are you sure you want to restart your computer now?\n\nPlease save all open work before proceeding.",
                "Confirm System Restart",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "/r /t 0",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initiate restart:\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string GetAppVersion()
        {
            try
            {
                string? ver = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrEmpty(ver))
                {
                    int plusIdx = ver.IndexOf('+'); // strip source-link build metadata
                    return plusIdx > 0 ? ver[..plusIdx] : ver;
                }

                Version? v = typeof(App).Assembly.GetName().Version;
                if (v != null) return $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch { }

            return "unknown";
        }

        private static string GetCopyright()
        {
            string? text = typeof(App).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;
            return string.IsNullOrWhiteSpace(text) ? "Sourajit Karmakar" : text.Replace("Copyright (c)", "©");
        }
    }
}
