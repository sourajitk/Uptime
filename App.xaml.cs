using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
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

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

        [DllImport("user32.dll")]
        private static extern bool CheckMenuRadioItem(IntPtr hMenu, uint first, uint last, uint check, uint flags);

        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr pChangeFilterStruct);

        // Undocumented but stable since Windows 10 1903 (used by Explorer, Notepad++, PowerToys, ...):
        // lets classic Win32 popup menus render in dark mode.
        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        private static extern int SetPreferredAppMode(int mode);

        [DllImport("uxtheme.dll", EntryPoint = "#136")]
        private static extern void FlushMenuThemes();

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
        private const uint NIIF_INFO = 0x01;
        private const uint NIIF_WARNING = 0x02;
        private const uint MSGFLT_ALLOW = 1;

        private const uint NOTIFYICON_VERSION_4 = 4;

        private const int ATTACH_PARENT_PROCESS = -1;
        private const int SM_CXSMICON = 49;
        private const int SM_MENUDROPALIGNMENT = 40;
        private const uint MONITOR_DEFAULTTOPRIMARY = 1;
        private const int MDT_EFFECTIVE_DPI = 0;

        private const uint WM_NULL = 0x0000;
        private const uint WM_APP = 0x8000;
        private const uint WM_TRAYICON = WM_APP + 1;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_CONTEXTMENU = 0x007B;
        private const uint WM_SETTINGCHANGE = 0x001A;

        private const uint MF_STRING = 0x0000;
        private const uint MF_GRAYED = 0x0001;
        private const uint MF_CHECKED = 0x0008;
        private const uint MF_POPUP = 0x0010;
        private const uint MF_SEPARATOR = 0x0800;
        private const uint MF_BYCOMMAND = 0x0000;

        private const uint TPM_LEFTALIGN = 0x0000;
        private const uint TPM_RIGHTALIGN = 0x0008;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_NONOTIFY = 0x0080;
        private const uint TPM_RETURNCMD = 0x0100;

        private const int PreferredAppModeForceDark = 2;
        private const int PreferredAppModeForceLight = 3;

        // Tray menu command IDs. Threshold choices are encoded as CmdThresholdBase + days (0 = never).
        private const uint CmdStartAtLogin = 1;
        private const uint CmdRestart = 2;
        private const uint CmdViewSource = 3;
        private const uint CmdExit = 4;
        private const uint CmdThresholdBase = 1000;

        private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppRegistryName = "Uptime";
        private const string LegacyAppRegistryName = "UptimeTaskbarApp";
        private const string PolicyRegistryPath = @"Software\Policies\Uptime";
        private const string SettingsRegistryPath = @"Software\Uptime";
        private const string LegacySettingsRegistryPath = @"Software\UptimeTaskbarApp";
        private const string MaxUptimeDaysValue = "MaxUptimeDays";
        private const int DefaultThresholdDays = 7;
        private const string RepositoryUrl = "https://github.com/sourajitk/Uptime";
        private const string TrayWindowClass = "UptimeTrayMsgWindow";
        private const string AlreadyRunningMessageName = "Uptime.AlreadyRunning";

        // Added to the Run entry: a duplicate launch at sign-in (e.g. both an HKCU and HKLM entry) exits quietly,
        // while a launch by the user from Start tells the running copy to show where it is.
        private const string AutostartArgument = "/autostart";

        // Preset restart-reminder choices offered in the tray menu (0 = never).
        private static readonly int[] ThresholdChoices = { 3, 7, 14, 30 };

        // ── Fields ──
        private IntPtr _messageWindow;
        private NOTIFYICONDATA _nid;
        private IntPtr _iconHandle;
        private DispatcherTimer? _timer;
        private WndProcDelegate? _wndProc; // prevent GC collection of delegate
        private bool _isMenuOpen;
        private static Mutex? _appMutex;
        private bool? _lastIsLightTheme;
        private int _lastIconSize;
        private uint _wmTaskbarCreated;
        private uint _wmAlreadyRunning;
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
                // Another instance is already running. Unless this is a duplicate sign-in launch, ask it to tell the
                // user where it is (the tray icon is often tucked away in the overflow area), then exit.
                _appMutex.Dispose();
                _appMutex = null;

                bool isAutostart = e.Args.Any(a => a.Equals(AutostartArgument, StringComparison.OrdinalIgnoreCase)
                                                   || a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
                if (!isAutostart)
                {
                    IntPtr running = FindWindow(TrayWindowClass, null);
                    if (running != IntPtr.Zero)
                    {
                        PostMessage(running, RegisterWindowMessage(AlreadyRunningMessageName), IntPtr.Zero, IntPtr.Zero);
                    }
                }

                Environment.Exit(0);
                return;
            }

            base.OnStartup(e);

            // Register window class
            _wndProc = WndProc;
            var hInstance = GetModuleHandle(null);

            var wcex = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = _wndProc,
                hInstance = hInstance,
                lpszClassName = TrayWindowClass
            };
            RegisterClassEx(ref wcex);

            // Create a hidden top-level window (parent = IntPtr.Zero)
            // This is required because Shell_NotifyIcon tooltips do not work with message-only (HWND_MESSAGE) windows
            _messageWindow = CreateWindowEx(
                0, TrayWindowClass, "Uptime", 0,
                0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            _wmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");
            _wmAlreadyRunning = RegisterWindowMessage(AlreadyRunningMessageName);

            // Let these through even if this instance runs elevated and the sender doesn't (UIPI)
            ChangeWindowMessageFilterEx(_messageWindow, _wmTaskbarCreated, MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(_messageWindow, _wmAlreadyRunning, MSGFLT_ALLOW, IntPtr.Zero);

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
            // An exception escaping a native callback would terminate the process.
            try
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
                        POINT anchor;
                        if (_trayUsesVersion4)
                        {
                            // Version 4 passes the anchor in wParam (the icon itself for keyboard activation), as signed 16-bit screen coordinates.
                            long w = wParam.ToInt64();
                            anchor = new POINT { X = (short)(w & 0xFFFF), Y = (short)((w >> 16) & 0xFFFF) };
                        }
                        else
                        {
                            GetCursorPos(out anchor);
                        }

                        ShowContextMenu(anchor);
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
                else if (_wmAlreadyRunning != 0 && msg == _wmAlreadyRunning)
                {
                    TimeSpan uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
                    ShowBalloonNotification("Uptime is already running",
                        $"Your PC has been up for {FormatUptime(uptime)}. Find the clock icon in the notification area; if it's hidden, select the ^ arrow on the taskbar.",
                        NIIF_INFO);
                    return IntPtr.Zero;
                }
            }
            catch { }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

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
                // ── Start at login ──
                // A machine-wide (HKLM) entry can't be toggled from a standard user session.
                bool startupManagedSystemWide = IsStartupEnabledSystemWide();
                bool startsAtLogin = startupManagedSystemWide || IsStartupEnabledForUser();
                AppendMenu(menu, MF_STRING | (startsAtLogin ? MF_CHECKED : 0) | (startupManagedSystemWide ? MF_GRAYED : 0),
                    (UIntPtr)CmdStartAtLogin, startupManagedSystemWide ? "&Start at login (all users)" : "&Start at login");

                // ── Restart reminder (submenu with radio choices) ──
                AppendMenu(menu, MF_POPUP, (UIntPtr)(ulong)BuildThresholdMenu(), "Restart &reminder");

                AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                AppendMenu(menu, MF_STRING, (UIntPtr)CmdRestart, "Restart &computer…");

                // ── About (submenu) ──
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
                    key.SetValue(AppRegistryName, $"\"{appPath}\" {AutostartArgument}");
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

        private void ShowBalloonNotification(string title, string message, uint iconFlag = NIIF_WARNING)
        {
            try
            {
                _nid.uFlags = NIF_INFO;
                _nid.szInfoTitle = title;
                _nid.szInfo = message;
                _nid.dwInfoFlags = iconFlag;
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

        /// <summary>"3 days, 4 hours" (or "25 minutes" in the first hour).</summary>
        private static string FormatUptime(TimeSpan uptime)
        {
            static string Unit(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

            if (uptime.TotalHours < 1) return Unit(uptime.Minutes, "minute");
            if (uptime.TotalDays < 1) return $"{Unit(uptime.Hours, "hour")}, {Unit(uptime.Minutes, "minute")}";
            return $"{Unit(uptime.Days, "day")}, {Unit(uptime.Hours, "hour")}";
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

        private static string GetCopyright()
        {
            string? text = typeof(App).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;
            return string.IsNullOrWhiteSpace(text) ? "Sourajit Karmakar" : text.Replace("Copyright (c)", "©");
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
