using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Uptime
{
    // App is split across partial-class files:
    //   App.xaml.cs           startup, /status, shared fields, shutdown
    //   App.Native.cs         Win32 P/Invoke declarations, structs and constants
    //   App.Tray.cs           tray icon registration, window messages, periodic uptime update
    //   App.TrayIcon.cs       theme/DPI detection and clock icon rendering
    //   App.Menu.cs           tray context menu and its commands
    //   App.Settings.cs       start-at-login, restart threshold and policy, Fast Startup
    //   App.Notifications.cs  balloon notifications and Event Log warnings
    public partial class App : Application
    {
        private const string TrayWindowClass = "UptimeTrayMsgWindow";
        private const string AlreadyRunningMessageName = "Uptime.AlreadyRunning";

        // Added to the Run entry: a duplicate launch at sign-in (e.g. both an HKCU and HKLM entry) exits quietly,
        // while a launch by the user from Start tells the running copy to show where it is.
        private const string AutostartArgument = "/autostart";

        // Fields
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

        // Shutdown
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
