using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Uptime
{
    public partial class App : Application
    {
        // ── Win32 P/Invoke ──
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        // Must be the W variant: the window class is registered as Unicode, and DefWindowProcA would mangle its text (title became "U").
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpWndClass);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // ── Structures ──
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        // ── Constants ──
        private const uint NIM_ADD = 0x00;
        private const uint NIM_MODIFY = 0x01;
        private const uint NIM_DELETE = 0x02;
        private const uint NIM_SETVERSION = 0x04;

        private const uint NIF_MESSAGE = 0x01;
        private const uint NIF_ICON = 0x02;
        private const uint NIF_TIP = 0x04;
        private const uint NIF_INFO = 0x10;
        private const uint NIF_SHOWTIP = 0x80;
        private const uint NIIF_WARNING = 0x02;

        private const uint NOTIFYICON_VERSION_4 = 4;

        private const int ATTACH_PARENT_PROCESS = -1;
        private const int SM_CXSMICON = 49;
        private const uint MONITOR_DEFAULTTOPRIMARY = 1;
        private const int MDT_EFFECTIVE_DPI = 0;

        private const uint WM_APP = 0x8000;
        private const uint WM_TRAYICON = WM_APP + 1;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_CONTEXTMENU = 0x007B;
        private const uint WM_SETTINGCHANGE = 0x001A;

        private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppRegistryName = "Uptime";
        private const string LegacyAppRegistryName = "UptimeTaskbarApp";
        private const string PolicyRegistryPath = @"Software\Policies\Uptime";
        private const string SettingsRegistryPath = @"Software\Uptime";
        private const string LegacySettingsRegistryPath = @"Software\UptimeTaskbarApp";
        private const string MaxUptimeDaysValue = "MaxUptimeDays";
        private const int DefaultThresholdDays = 7;
        private const string RepositoryUrl = "https://github.com/sourajitk/Uptime";

        // Preset restart-reminder choices offered in the tray menu (0 = never).
        private static readonly int[] ThresholdChoices = { 3, 7, 14, 30 };

        // ── Fields ──
        private IntPtr _messageWindow;
        private NOTIFYICONDATA _nid;
        private IntPtr _iconHandle;
        private DispatcherTimer? _timer;
        private WndProcDelegate? _wndProc; // prevent GC collection of delegate
        private Window? _dummyWindow;
        private static Mutex? _appMutex;
        private bool? _lastIsLightTheme;
        private int _lastIconSize;
        private uint _wmTaskbarCreated;
        private bool _lastShowWarning;
        private bool _trayUsesVersion4;
        private bool _cleanedUp;
        private int _effectiveThresholdDays = DefaultThresholdDays;
        private bool _isPolicyEnforced;
        private string _lastTooltipText = "";
        private DateTime _lastNotificationTime = DateTime.MinValue;

        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args.Any(a => a.Equals("/status", StringComparison.OrdinalIgnoreCase) || a.Equals("--status", StringComparison.OrdinalIgnoreCase)))
            {
                PrintStatusAndExit();
                return;
            }

            _appMutex = new Mutex(true, "Uptime_SingleInstanceMutex", out bool createdNew);
            if (!createdNew)
            {
                // Another instance is already running, exit immediately
                _appMutex.Dispose();
                _appMutex = null;
                Environment.Exit(0);
                return;
            }

            base.OnStartup(e);

            // Create a dummy hidden window for hosting the WPF ContextMenu
            _dummyWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Width = 1,
                Height = 1,
                ShowInTaskbar = false,
                Top = -100,
                Left = -100
            };
            _dummyWindow.Show();
            _dummyWindow.Hide(); // Create window handle but keep it invisible

            // Register window class
            _wndProc = WndProc;
            var hInstance = GetModuleHandle(null);

            var wcex = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = _wndProc,
                hInstance = hInstance,
                lpszClassName = "UptimeTrayMsgWindow"
            };
            RegisterClassEx(ref wcex);

            // Create a hidden top-level window (parent = IntPtr.Zero)
            // This is required because Shell_NotifyIcon tooltips do not work with message-only (HWND_MESSAGE) windows
            _messageWindow = CreateWindowEx(
                0, "UptimeTrayMsgWindow", "Uptime", 0,
                0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            _wmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");

            bool isLightTheme = IsSystemLightTheme();
            _lastIsLightTheme = isLightTheme;
            _lastIconSize = GetTrayIconSize();
            _iconHandle = CreateClockIcon(isLightTheme, false, _lastIconSize);

            _nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _messageWindow,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
                uCallbackMessage = WM_TRAYICON,
                hIcon = _iconHandle,
                szTip = "Loading...",
                szInfo = "",
                szInfoTitle = ""
            };

            AddTrayIcon();

            // Timer to update uptime every 30 seconds
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _timer.Tick += (_, _) => UpdateUptime();
            _timer.Start();

            UpdateUptime();
        }

        /// <summary>
        /// Handles `Uptime.exe /status`: prints machine status as JSON for RMM / audit scripts.
        /// </summary>
        private void PrintStatusAndExit()
        {
            AttachConsole(ATTACH_PARENT_PROCESS);

            long tickMs = Environment.TickCount64;
            TimeSpan uptime = TimeSpan.FromMilliseconds(tickMs);
            int threshold = GetEffectiveMaxUptimeDays(out bool policyEnforced);
            bool exceeded = threshold > 0 && uptime.TotalDays >= threshold;

            // Invariant culture: the output must be valid JSON regardless of the machine's locale.
            string json = FormattableString.Invariant($@"{{
  ""uptime_seconds"": {tickMs / 1000},
  ""uptime_days"": {Math.Round(uptime.TotalDays, 2)},
  ""uptime_formatted"": ""{uptime.Days} days, {uptime.Hours} hours, {uptime.Minutes} minutes"",
  ""threshold_days"": {threshold},
  ""is_threshold_exceeded"": {(exceeded ? "true" : "false")},
  ""is_policy_enforced"": {(policyEnforced ? "true" : "false")},
  ""fast_startup_enabled"": {(IsFastStartupEnabled() ? "true" : "false")}
}}");

            Console.WriteLine(json);
            Environment.Exit(0);
        }

        // ── Tray icon plumbing ──
        private void AddTrayIcon()
        {
            // Remove first: TaskbarCreated is also broadcast on DPI changes, when our icon still exists and NIM_ADD would fail.
            Shell_NotifyIcon(NIM_DELETE, ref _nid);

            _nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
            _nid.hIcon = _iconHandle;
            Shell_NotifyIcon(NIM_ADD, ref _nid);

            // Version 4 gives us WM_CONTEXTMENU for both mouse and keyboard (Shift+F10 / Menu key) activation.
            _nid.uVersion = NOTIFYICON_VERSION_4;
            _trayUsesVersion4 = Shell_NotifyIcon(NIM_SETVERSION, ref _nid);
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_TRAYICON)
            {
                // Low word of lParam = notification event (both legacy and version 4 behaviour).
                uint eventId = (uint)(lParam.ToInt64() & 0xFFFF);

                // Version 4 sends WM_CONTEXTMENU for right-click and keyboard; the legacy protocol only sends WM_RBUTTONUP.
                // Reacting to just one of them avoids opening the menu twice.
                uint contextEvent = _trayUsesVersion4 ? WM_CONTEXTMENU : WM_RBUTTONUP;
                if (eventId == contextEvent)
                {
                    ShowContextMenu();
                    return IntPtr.Zero;
                }
            }
            else if (_wmTaskbarCreated != 0 && msg == _wmTaskbarCreated)
            {
                // Explorer restarted or the display scale changed: re-render the icon for the current DPI and re-register it.
                UpdateTrayIcon(_lastShowWarning, force: true);
                AddTrayIcon();
            }
            else if (msg == WM_SETTINGCHANGE)
            {
                UpdateTrayIcon(_lastShowWarning);
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void ShowContextMenu()
        {
            if (_dummyWindow == null) return;

            UpdateThemeBrushes();

            var menu = new ContextMenu();

            // ── Start at login ──
            bool startupManagedSystemWide = IsStartupEnabledSystemWide();
            var startItem = new MenuItem
            {
                Header = startupManagedSystemWide ? "Start at Login (All Users)" : "Start at Login",
                IsCheckable = true,
                IsChecked = startupManagedSystemWide || IsStartupEnabledForUser(),
                // A machine-wide (HKLM) entry can't be toggled from a standard user session.
                IsEnabled = !startupManagedSystemWide
            };
            startItem.Click += (_, _) => SetStartup(startItem.IsChecked);
            menu.Items.Add(startItem);

            // ── Restart reminder threshold ──
            menu.Items.Add(new Separator());
            if (_isPolicyEnforced)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = _effectiveThresholdDays > 0
                        ? $"Restart Reminder: {FormatDays(_effectiveThresholdDays)} (IT Policy)"
                        : "Restart Reminder: Disabled (IT Policy)",
                    IsEnabled = false
                });
            }
            else
            {
                menu.Items.Add(new MenuItem { Header = "Remind me to restart after", IsEnabled = false });

                var choices = new List<int>(ThresholdChoices);
                if (_effectiveThresholdDays > 0 && !choices.Contains(_effectiveThresholdDays))
                {
                    choices.Add(_effectiveThresholdDays); // preserve a custom value configured via registry
                    choices.Sort();
                }

                foreach (int days in choices)
                {
                    int selected = days;
                    var item = new MenuItem { Header = FormatDays(days), IsChecked = _effectiveThresholdDays == days };
                    item.Click += (_, _) => ApplyUserThreshold(selected);
                    menu.Items.Add(item);
                }

                var neverItem = new MenuItem { Header = "Never", IsChecked = _effectiveThresholdDays <= 0 };
                neverItem.Click += (_, _) => ApplyUserThreshold(0);
                menu.Items.Add(neverItem);
            }

            // ── Restart ──
            menu.Items.Add(new Separator());
            var restartItem = new MenuItem { Header = "Restart Computer..." };
            restartItem.Click += (_, _) => PromptAndRestartComputer();
            menu.Items.Add(restartItem);

            // ── About ──
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = $"Version {GetAppVersion()}", IsEnabled = false });
            menu.Items.Add(new MenuItem { Header = "Copyright (c) 2026 Sourajit Karmakar", IsEnabled = false });
            var sourceItem = new MenuItem { Header = "View the source code on GitHub" };
            sourceItem.Click += (_, _) => OpenUrl(RepositoryUrl);
            menu.Items.Add(sourceItem);

            // ── Exit ──
            menu.Items.Add(new Separator());
            var exitItem = new MenuItem { Header = "Exit" };
            exitItem.Click += (_, _) => Dispatcher.BeginInvoke(() => Current.Shutdown());
            menu.Items.Add(exitItem);

            // Position the dummy window at the mouse and host the menu on it
            GetCursorPos(out POINT pt);
            _dummyWindow.Left = pt.X;
            _dummyWindow.Top = pt.Y;
            _dummyWindow.Show();

            menu.PlacementTarget = _dummyWindow;
            menu.Placement = PlacementMode.AbsolutePoint;
            menu.HorizontalOffset = pt.X;
            menu.VerticalOffset = pt.Y;
            menu.Closed += (_, _) => _dummyWindow?.Hide();

            // Foreground the dummy window so the menu dismisses when the user clicks away
            SetForegroundWindow(new WindowInteropHelper(_dummyWindow).Handle);

            menu.IsOpen = true;
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

        private void UpdateThemeBrushes()
        {
            // Menu colours follow the *apps* theme setting (the tray icon follows the *system* theme).
            bool isLightTheme = true;
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int i)
                {
                    isLightTheme = i == 1;
                }
            }
            catch
            {
                // Fallback to light theme if registry read fails
            }

            static System.Windows.Media.SolidColorBrush Rgb(byte r, byte g, byte b) =>
                new(System.Windows.Media.Color.FromRgb(r, g, b));

            if (isLightTheme)
            {
                Resources["MenuBackgroundBrush"] = Rgb(0xF3, 0xF3, 0xF3);
                Resources["MenuBorderBrush"] = Rgb(0xD0, 0xD0, 0xD0);
                Resources["MenuForegroundBrush"] = Rgb(0x00, 0x00, 0x00);
                Resources["MenuHoverBrush"] = Rgb(0xE5, 0xE5, 0xE5);
                Resources["MenuCheckBrush"] = Rgb(0x00, 0x78, 0xD4);
            }
            else
            {
                Resources["MenuBackgroundBrush"] = Rgb(0x20, 0x20, 0x20);
                Resources["MenuBorderBrush"] = Rgb(0x40, 0x40, 0x40);
                Resources["MenuForegroundBrush"] = Rgb(0xFF, 0xFF, 0xFF);
                Resources["MenuHoverBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
                Resources["MenuCheckBrush"] = Rgb(0x60, 0xCD, 0xFF);
            }
        }

        // ── Startup registration ──
        private static bool HasStartupEntry(RegistryKey root)
        {
            try
            {
                using RegistryKey? key = root.OpenSubKey(StartupRegistryPath);
                return key != null && (key.GetValue(AppRegistryName) != null || key.GetValue(LegacyAppRegistryName) != null);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsStartupEnabledForUser() => HasStartupEntry(Registry.CurrentUser);

        private static bool IsStartupEnabledSystemWide() => HasStartupEntry(Registry.LocalMachine);

        private static void SetStartup(bool enable)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath, true);

                if (enable)
                {
                    string appPath = Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "Uptime.exe");
                    key.SetValue(AppRegistryName, $"\"{appPath}\"");
                    key.DeleteValue(LegacyAppRegistryName, false);
                }
                else
                {
                    key.DeleteValue(AppRegistryName, false);
                    key.DeleteValue(LegacyAppRegistryName, false);
                }
            }
            catch { }
        }

        // ── Threshold / policy ──
        private static int? ReadDays(RegistryKey root, params string[] subKeys)
        {
            foreach (string subKey in subKeys)
            {
                try
                {
                    using RegistryKey? key = root.OpenSubKey(subKey);
                    object? val = key?.GetValue(MaxUptimeDaysValue);
                    if (val is int i) return Math.Max(0, i);
                    if (val is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                        return Math.Max(0, parsed);
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Resolves the restart threshold: GPO (HKLM policy) > machine setting (HKLM) > user setting (HKCU) > default.
        /// A value of 0 disables the warning.
        /// </summary>
        private static int GetEffectiveMaxUptimeDays(out bool isPolicyEnforced)
        {
            int? machine = ReadDays(Registry.LocalMachine, PolicyRegistryPath)
                           ?? ReadDays(Registry.LocalMachine, SettingsRegistryPath, LegacySettingsRegistryPath);
            if (machine.HasValue)
            {
                isPolicyEnforced = true;
                return machine.Value;
            }

            isPolicyEnforced = false;
            return ReadDays(Registry.CurrentUser, SettingsRegistryPath, LegacySettingsRegistryPath) ?? DefaultThresholdDays;
        }

        private static void SetUserMaxUptimeDays(int days)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.CreateSubKey(SettingsRegistryPath);
                key?.SetValue(MaxUptimeDaysValue, days, RegistryValueKind.DWord);
            }
            catch { }
        }

        private static bool IsFastStartupEnabled()
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
                return key?.GetValue("HiberbootEnabled") is int i && i == 1;
            }
            catch
            {
                return false;
            }
        }

        // ── Notifications ──
        private DateTime GetLastNotificationTime()
        {
            if (_lastNotificationTime != DateTime.MinValue) return _lastNotificationTime;
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SettingsRegistryPath) ?? Registry.CurrentUser.OpenSubKey(LegacySettingsRegistryPath);
                if (key?.GetValue("LastNotificationTicks") is long ticks)
                {
                    _lastNotificationTime = new DateTime(ticks, DateTimeKind.Utc);
                }
            }
            catch { }
            return _lastNotificationTime;
        }

        private void SetLastNotificationTime(DateTime dt)
        {
            _lastNotificationTime = dt;
            try
            {
                using RegistryKey? key = Registry.CurrentUser.CreateSubKey(SettingsRegistryPath);
                key?.SetValue("LastNotificationTicks", dt.Ticks, RegistryValueKind.QWord);
            }
            catch { }
        }

        private static void LogEventViewerWarning(double uptimeDays, int thresholdDays)
        {
            try
            {
                const string source = "Uptime";
                // The source is registered by the installer (needs admin). Creating it here only succeeds when elevated.
                if (!EventLog.SourceExists(source))
                {
                    EventLog.CreateEventSource(source, "Application");
                }
                EventLog.WriteEntry(source,
                    $"System uptime ({Math.Round(uptimeDays, 1).ToString(CultureInfo.InvariantCulture)} days) has exceeded the configured restart threshold ({thresholdDays} days). A system restart is required to maintain optimal performance and security.",
                    EventLogEntryType.Warning, 1001);
            }
            catch { /* Ignore event log failures if the source isn't registered and we're not admin */ }
        }

        private void ShowBalloonNotification(string title, string message)
        {
            try
            {
                _nid.uFlags = NIF_INFO;
                _nid.szInfoTitle = title;
                _nid.szInfo = message;
                _nid.dwInfoFlags = NIIF_WARNING;
                Shell_NotifyIcon(NIM_MODIFY, ref _nid);
            }
            catch { }
            finally
            {
                // Clear the buffers so later NIM_MODIFY calls for tooltip/icon do NOT re-trigger the toast
                _nid.szInfoTitle = "";
                _nid.szInfo = "";
            }
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

        // ── Periodic update ──
        private void UpdateUptime()
        {
            TimeSpan uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);

            int thresholdDays = GetEffectiveMaxUptimeDays(out bool isPolicyEnforced);
            _isPolicyEnforced = isPolicyEnforced;
            _effectiveThresholdDays = thresholdDays;

            bool showWarning = thresholdDays > 0 && uptime.TotalDays >= thresholdDays;

            string daysStr = uptime.Days == 1 ? "day" : "days";
            string hoursStr = uptime.Hours == 1 ? "hour" : "hours";
            string minsStr = uptime.Minutes == 1 ? "minute" : "minutes";

            string tooltipText = $"Uptime: {uptime.Days} {daysStr}, {uptime.Hours} {hoursStr}, {uptime.Minutes} {minsStr}";
            if (showWarning)
            {
                tooltipText += " (Restart Required)";
                if (IsFastStartupEnabled())
                {
                    tooltipText += "\nFast Startup active. Restart to reset uptime.";
                }

                if ((DateTime.UtcNow - GetLastNotificationTime()).TotalHours >= 24)
                {
                    SetLastNotificationTime(DateTime.UtcNow);
                    ShowBalloonNotification("Restart Required", $"Your computer has been running for {uptime.Days} {daysStr}. A restart is required to maintain optimal performance and security.");
                    LogEventViewerWarning(uptime.TotalDays, thresholdDays);
                }
            }

            if (_lastTooltipText != tooltipText)
            {
                _lastTooltipText = tooltipText;
                _nid.uFlags = NIF_TIP | NIF_SHOWTIP;
                _nid.szTip = tooltipText;
                Shell_NotifyIcon(NIM_MODIFY, ref _nid);
            }

            UpdateTrayIcon(showWarning);
        }

        // ── Icon rendering ──
        private static bool IsSystemLightTheme()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("SystemUsesLightTheme") is int i && i == 1;
            }
            catch
            {
                return false; // Fallback to dark theme if registry read fails
            }
        }

        /// <summary>
        /// Small-icon size at the primary monitor's *current* DPI (where the taskbar notification area lives).
        /// Plain GetSystemMetrics is frozen at the sign-in DPI for per-monitor-aware processes, so it misses scale changes.
        /// </summary>
        private static int GetTrayIconSize()
        {
            int size = 0;
            try
            {
                IntPtr monitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 && dpi > 0)
                {
                    size = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
                }
            }
            catch { }

            if (size <= 0) size = GetSystemMetrics(SM_CXSMICON);
            return size >= 16 ? size : 16;
        }

        private void UpdateTrayIcon(bool showWarning = false, bool force = false)
        {
            bool isLightTheme = IsSystemLightTheme();
            int iconSize = GetTrayIconSize();
            if (!force && _lastIsLightTheme == isLightTheme && _lastShowWarning == showWarning && _lastIconSize == iconSize)
            {
                return;
            }

            _lastIsLightTheme = isLightTheme;
            _lastShowWarning = showWarning;
            _lastIconSize = iconSize;

            IntPtr oldIcon = _iconHandle;
            _iconHandle = CreateClockIcon(isLightTheme, showWarning, iconSize);

            _nid.hIcon = _iconHandle;
            _nid.uFlags = NIF_ICON;
            Shell_NotifyIcon(NIM_MODIFY, ref _nid);

            if (oldIcon != IntPtr.Zero)
            {
                DestroyIcon(oldIcon);
            }
        }

        /// <summary>
        /// Draws the tray icon in a 32x32 design space, scaled to the shell's small-icon size so it stays crisp at any DPI.
        /// The caller owns the returned HICON and must release it with DestroyIcon.
        /// </summary>
        private static IntPtr CreateClockIcon(bool isLightTheme, bool showWarning, int size)
        {
            using var bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(size / 32f, size / 32f);

                // 1. Solid Fluent Blue circle (#0078D4)
                using (var fillBrush = new SolidBrush(Color.FromArgb(255, 0, 120, 212)))
                {
                    g.FillEllipse(fillBrush, 2f, 2f, 28f, 28f);
                }

                // 2. White clock hands (9 o'clock hour hand, 12 o'clock minute hand) around the centre (16, 16)
                using (var handPen = new Pen(Color.White, 2.5f))
                {
                    handPen.StartCap = LineCap.Round;
                    handPen.EndCap = LineCap.Round;
                    handPen.LineJoin = LineJoin.Round;

                    g.DrawLine(handPen, 16f, 16f, 8.5f, 16f);
                    g.DrawLine(handPen, 16f, 16f, 16f, 8.5f);
                }

                // If uptime exceeds the threshold, draw a Fluent-style warning badge in the bottom-right corner
                if (showWarning)
                {
                    PointF[] triangle = { new(23f, 14f), new(14f, 30f), new(31f, 30f) };
                    PointF[] borderTriangle = { new(23f, 12f), new(12f, 31.5f), new(32f, 31.5f) };

                    // Cut-out border separating the badge from the clock
                    Color bgCutout = isLightTheme ? Color.White : Color.FromArgb(255, 32, 32, 32);
                    using (var bgBrush = new SolidBrush(bgCutout))
                    {
                        g.FillPolygon(bgBrush, borderTriangle);
                    }

                    // Amber badge (#FFB900) with a dark outline
                    using (var amberBrush = new SolidBrush(Color.FromArgb(255, 255, 185, 0)))
                    {
                        g.FillPolygon(amberBrush, triangle);
                    }
                    using (var outlinePen = new Pen(Color.FromArgb(255, 40, 40, 40), 1f))
                    {
                        g.DrawPolygon(outlinePen, triangle);
                    }

                    // Exclamation mark
                    using (var markPen = new Pen(Color.Black, 2f))
                    {
                        markPen.StartCap = LineCap.Round;
                        markPen.EndCap = LineCap.Round;
                        g.DrawLine(markPen, 22.5f, 18.5f, 22.5f, 23.5f);
                    }
                    g.FillEllipse(Brushes.Black, 21.5f, 25.5f, 2f, 2f);
                }
            }
            return bmp.GetHicon();
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

        // ── Shutdown ──
        private void Cleanup()
        {
            if (_cleanedUp) return;
            _cleanedUp = true;

            _timer?.Stop();
            Shell_NotifyIcon(NIM_DELETE, ref _nid);

            if (_iconHandle != IntPtr.Zero)
            {
                DestroyIcon(_iconHandle);
                _iconHandle = IntPtr.Zero;
            }

            if (_messageWindow != IntPtr.Zero)
            {
                DestroyWindow(_messageWindow);
                _messageWindow = IntPtr.Zero;
            }

            _dummyWindow?.Close();
            _dummyWindow = null;

            if (_appMutex != null)
            {
                try { _appMutex.ReleaseMutex(); } catch { }
                _appMutex.Dispose();
                _appMutex = null;
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Cleanup();
            base.OnExit(e);
        }
    }
}
