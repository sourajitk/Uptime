using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;

namespace UptimeTaskbarApp
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

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpWndClass);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool InsertMenu(IntPtr hMenu, uint uPosition, uint uFlags,
            IntPtr uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y,
            IntPtr hWnd, IntPtr lptpm);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

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
        private const uint NIF_SHOWTIP = 0x10;

        private const uint NOTIFYICON_VERSION_4 = 4;

        private const uint WM_APP = 0x8000;
        private const uint WM_TRAYICON = WM_APP + 1;
        private const uint WM_COMMAND = 0x0111;
        private const uint WM_CLOSE = 0x0010;

        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_CONTEXTMENU = 0x007B;
        private const uint WM_SETTINGCHANGE = 0x001A;

        private const uint MF_STRING = 0x00;
        private const uint MF_CHECKED = 0x0008;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_RETURNCMD = 0x0100;

        private const int IDM_EXIT = 1000;
        private const int IDM_STARTUP = 1001;

        // HWND_MESSAGE = (IntPtr)(-3)  — message-only window parent
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        // ── Fields ──
        private IntPtr _messageWindow;
        private NOTIFYICONDATA _nid;
        private IntPtr _iconHandle;
        private DispatcherTimer? _timer;
        private WndProcDelegate? _wndProc; // prevent GC collection of delegate
        private Window? _dummyWindow;
        private static Mutex? _appMutex;
        private bool? _lastIsLightTheme;
        private Icon? _loadedIcon;
        private uint _wmTaskbarCreated;
        private bool _lastShowWarning = false;
        private double _effectiveThresholdDays = 7.0;
        private bool _isPolicyEnforced = false;

        protected override void OnStartup(StartupEventArgs e)
        {
            const string mutexName = "UptimeTaskbarApp_SingleInstanceMutex";
            _appMutex = new Mutex(true, mutexName, out bool createdNew);

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
                0, "UptimeTrayMsgWindow", "UptimeTray", 0,
                0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            _wmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");

            // Load app icon (installer icon)
            bool isLightTheme = IsSystemLightTheme();
            _lastIsLightTheme = isLightTheme;
            _iconHandle = CreateClockIcon(isLightTheme, false);

            // Setup NOTIFYICONDATA — identical to how Task Manager does it
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

            Shell_NotifyIcon(NIM_ADD, ref _nid);

            // Timer to update uptime every 30 seconds
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _timer.Tick += (_, _) => UpdateUptime();
            _timer.Start();

            UpdateUptime();
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_TRAYICON)
            {
                // NOTIFYICON_VERSION_4: low word of lParam = event, high word = icon x/y
                uint eventId = (uint)(lParam.ToInt64() & 0xFFFF);

                if (eventId == WM_RBUTTONUP || eventId == WM_CONTEXTMENU)
                {
                    ShowContextMenu();
                    return IntPtr.Zero;
                }
            }
            else if (_wmTaskbarCreated != 0 && msg == _wmTaskbarCreated)
            {
                ReaddTrayIcon();
            }
            else if (msg == WM_SETTINGCHANGE)
            {
                UpdateTrayIcon(_lastShowWarning);
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void ReaddTrayIcon()
        {
            _nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
            _nid.hIcon = _iconHandle;
            Shell_NotifyIcon(NIM_ADD, ref _nid);
        }

        private void ShowContextMenu()
        {
            UpdateThemeBrushes();

            var menu = new ContextMenu();

            var startItem = new MenuItem
            {
                Header = "Start at Login",
                IsCheckable = true,
                IsChecked = IsStartupEnabled()
            };
            startItem.Click += (s, e) => SetStartup(startItem.IsChecked);

            var thresholdSubMenu = new MenuItem
            {
                Header = _isPolicyEnforced
                    ? $"Restart Threshold ({_effectiveThresholdDays} Days - IT Policy)"
                    : $"Restart Threshold ({(_effectiveThresholdDays == 0 ? "Disabled" : $"{_effectiveThresholdDays} Days")})",
                StaysOpenOnClick = true
            };

            int[] presets = { 7, 14, 30, 0 };
            string[] presetLabels = { "7 Days (Recommended)", "14 Days", "30 Days", "Disabled" };

            for (int idx = 0; idx < presets.Length; idx++)
            {
                int val = presets[idx];
                var item = new MenuItem
                {
                    Header = presetLabels[idx],
                    IsCheckable = true,
                    IsChecked = ((int)_effectiveThresholdDays == val),
                    IsEnabled = !_isPolicyEnforced
                };
                item.Click += (s, e) =>
                {
                    SetUserMaxUptimeDays(val);
                    UpdateUptime();
                };
                thresholdSubMenu.Items.Add(item);
            }

            var versionItem = new MenuItem
            {
                Header = $"Version {GetAppVersion()}",
                IsEnabled = false
            };

            var copyrightItem = new MenuItem
            {
                Header = $"Copyright (c) {DateTime.Now.Year} Sourajit Karmakar",
                IsEnabled = false
            };

            var sourceItem = new MenuItem
            {
                Header = "View the source code on GitHub"
            };
            sourceItem.Click += (s, e) => OpenUrl("https://github.com/sourajitk/Uptime");

            var exitItem = new MenuItem
            {
                Header = "Exit"
            };
            exitItem.Click += (s, e) =>
            {
                Cleanup();
                Dispatcher.BeginInvoke(() => Current.Shutdown());
            };

            menu.Items.Add(startItem);
            menu.Items.Add(thresholdSubMenu);
            menu.Items.Add(new Separator());
            menu.Items.Add(versionItem);
            menu.Items.Add(copyrightItem);
            menu.Items.Add(sourceItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(exitItem);

            // Get mouse cursor position
            GetCursorPos(out POINT pt);

            // Position the dummy window near the mouse
            if (_dummyWindow != null)
            {
                _dummyWindow.Left = pt.X;
                _dummyWindow.Top = pt.Y;
                _dummyWindow.Show();

                // Open the ContextMenu hosted on the dummy window
                menu.PlacementTarget = _dummyWindow;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
                menu.HorizontalOffset = pt.X;
                menu.VerticalOffset = pt.Y;

                // When the menu closes, hide the dummy window
                menu.Closed += (s, e) => _dummyWindow.Hide();

                // Set foreground window to the dummy window so focus transfers properly (dismiss on click away)
                var helper = new WindowInteropHelper(_dummyWindow);
                SetForegroundWindow(helper.Handle);

                menu.IsOpen = true;
            }
        }

        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch { /* Ignore url launch failure */ }
        }

        private void UpdateThemeBrushes()
        {
            bool isLightTheme = true;
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key != null)
                {
                    object? val = key.GetValue("AppsUseLightTheme");
                    if (val is int i)
                    {
                        isLightTheme = i == 1;
                    }
                }
            }
            catch
            {
                // Fallback to light theme if registry read fails
            }

            if (isLightTheme)
            {
                Resources["MenuBackgroundBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0xF3, 0xF3));
                Resources["MenuBorderBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD0, 0xD0, 0xD0));
                Resources["MenuForegroundBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x00, 0x00));
                Resources["MenuHoverBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0xE5, 0xE5));
                Resources["MenuCheckBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4));
            }
            else
            {
                Resources["MenuBackgroundBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20));
                Resources["MenuBorderBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x40, 0x40, 0x40));
                Resources["MenuForegroundBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));
                Resources["MenuHoverBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
                Resources["MenuCheckBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0xCD, 0xFF));
            }
        }

        private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppRegistryName = "UptimeTaskbarApp";

        private bool IsStartupEnabled()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath);
            return key?.GetValue(AppRegistryName) != null;
        }

        private void SetStartup(bool enable)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, true);
            if (key != null)
            {
                if (enable)
                {
                    string? appPath = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(appPath))
                    {
                        appPath = System.IO.Path.Combine(AppContext.BaseDirectory, "UptimeTaskbarApp.exe");
                    }
                    key.SetValue(AppRegistryName, $"\"{appPath}\"");
                }
                else
                {
                    key.DeleteValue(AppRegistryName, false);
                }
            }
        }

        private double GetEffectiveMaxUptimeDays(out bool isPolicyEnforced)
        {
            isPolicyEnforced = false;

            // 1. Check GPO / IT Admin Policy in HKLM\Software\Policies\Uptime
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"Software\Policies\Uptime");
                if (key != null)
                {
                    object? val = key.GetValue("MaxUptimeDays");
                    if (val is int daysInt)
                    {
                        isPolicyEnforced = true;
                        return daysInt;
                    }
                }
            }
            catch { }

            // 2. Check System-wide IT Admin setting in HKLM\Software\UptimeTaskbarApp
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"Software\UptimeTaskbarApp");
                if (key != null)
                {
                    object? val = key.GetValue("MaxUptimeDays");
                    if (val is int daysInt)
                    {
                        isPolicyEnforced = true;
                        return daysInt;
                    }
                }
            }
            catch { }

            // 3. Check User setting in HKCU\Software\UptimeTaskbarApp (default: 7 days)
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\UptimeTaskbarApp");
                if (key != null)
                {
                    object? val = key.GetValue("MaxUptimeDays");
                    if (val is int daysInt)
                    {
                        return daysInt;
                    }
                }
            }
            catch { }

            return 7.0; // Default: 7 Days
        }

        private void SetUserMaxUptimeDays(int days)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.CreateSubKey(@"Software\UptimeTaskbarApp");
                key?.SetValue("MaxUptimeDays", days, RegistryValueKind.DWord);
            }
            catch { }
        }

        private void UpdateUptime()
        {
            long tickCountMs = Environment.TickCount64;
            TimeSpan uptime = TimeSpan.FromMilliseconds(tickCountMs);

            double thresholdDays = GetEffectiveMaxUptimeDays(out bool isPolicyEnforced);
            _isPolicyEnforced = isPolicyEnforced;
            _effectiveThresholdDays = thresholdDays;

            bool showWarning = thresholdDays > 0 && uptime.TotalDays >= thresholdDays;

            string daysStr = uptime.Days == 1 ? "day" : "days";
            string hoursStr = uptime.Hours == 1 ? "hour" : "hours";
            string minsStr = uptime.Minutes == 1 ? "minute" : "minutes";

            string tooltipText = $"Uptime: {uptime.Days} {daysStr}, {uptime.Hours} {hoursStr}, {uptime.Minutes} {minsStr}";
            if (showWarning)
            {
                tooltipText += " (Restart Recommended!)";
            }

            _nid.uFlags = NIF_TIP | NIF_SHOWTIP;
            _nid.szTip = tooltipText;
            Shell_NotifyIcon(NIM_MODIFY, ref _nid);

            UpdateTrayIcon(showWarning);
        }

        private bool IsSystemLightTheme()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key != null)
                {
                    object? val = key.GetValue("SystemUsesLightTheme");
                    if (val is int i)
                    {
                        return i == 1;
                    }
                }
            }
            catch
            {
                // Fallback to dark theme if registry read fails
            }
            return false;
        }

        private void UpdateTrayIcon(bool showWarning = false)
        {
            bool isLightTheme = IsSystemLightTheme();
            if (_lastIsLightTheme == isLightTheme && _lastShowWarning == showWarning)
            {
                return;
            }

            _lastIsLightTheme = isLightTheme;
            _lastShowWarning = showWarning;
            IntPtr oldIcon = _iconHandle;
            _iconHandle = CreateClockIcon(isLightTheme, showWarning);

            _nid.hIcon = _iconHandle;
            _nid.uFlags = NIF_ICON;
            Shell_NotifyIcon(NIM_MODIFY, ref _nid);

            if (oldIcon != IntPtr.Zero)
            {
                DestroyIcon(oldIcon);
            }
        }

        private IntPtr CreateClockIcon(bool isLightTheme, bool showWarning = false)
        {
            Bitmap? baseBmp = null;
            try
            {
                string appDir = AppContext.BaseDirectory;
                string iconPath = System.IO.Path.Combine(appDir, "Assets", "AppIcon.ico");
                if (!System.IO.File.Exists(iconPath))
                {
                    iconPath = System.IO.Path.Combine(appDir, "AppIcon.ico");
                }

                if (System.IO.File.Exists(iconPath))
                {
                    using Icon ico = new Icon(iconPath, 32, 32);
                    baseBmp = ico.ToBitmap();
                }
                else
                {
                    string? procPath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(procPath))
                    {
                        using Icon? procIcon = Icon.ExtractAssociatedIcon(procPath);
                        if (procIcon != null)
                        {
                            baseBmp = procIcon.ToBitmap();
                        }
                    }
                }
            }
            catch { }

            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                if (baseBmp != null)
                {
                    g.DrawImage(baseBmp, 0, 0, 32, 32);
                    baseBmp.Dispose();
                }
                else
                {
                    // Fallback outline clock face if AppIcon.ico is unavailable
                    Color iconColor = isLightTheme ? Color.Black : Color.White;
                    using (Pen pen = new Pen(iconColor, 2f))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        pen.LineJoin = LineJoin.Round;

                        g.DrawEllipse(pen, 3f, 3f, 26f, 26f);
                        g.DrawLine(pen, 16f, 16f, 22.5f, 16f);
                        g.DrawLine(pen, 16f, 16f, 16f, 8.5f);
                    }
                }

                // If uptime exceeds threshold, draw Fluent-style warning badge in bottom-right corner
                if (showWarning)
                {
                    // Triangle vertices: top=(23, 14), bottom-left=(14, 30), bottom-right=(31, 30)
                    PointF p1 = new PointF(23f, 14f);
                    PointF p2 = new PointF(14f, 30f);
                    PointF p3 = new PointF(31f, 30f);
                    PointF[] triangle = { p1, p2, p3 };

                    // 1. Draw a dark cutout/border polygon slightly larger to separate badge from clock outline
                    PointF bp1 = new PointF(23f, 12f);
                    PointF bp2 = new PointF(12f, 31.5f);
                    PointF bp3 = new PointF(32f, 31.5f);
                    PointF[] borderTriangle = { bp1, bp2, bp3 };

                    Color bgCutout = isLightTheme ? Color.White : Color.FromArgb(255, 32, 32, 32);
                    using (SolidBrush bgBrush = new SolidBrush(bgCutout))
                    {
                        g.FillPolygon(bgBrush, borderTriangle);
                    }

                    // 2. Fill amber/yellow warning badge
                    Color amberColor = Color.FromArgb(255, 255, 185, 0); // Vibrant Fluent Amber (#FFB900)
                    using (SolidBrush amberBrush = new SolidBrush(amberColor))
                    {
                        g.FillPolygon(amberBrush, triangle);
                    }

                    // 3. Draw dark outline border for badge definition
                    using (Pen outlinePen = new Pen(Color.FromArgb(255, 40, 40, 40), 1f))
                    {
                        g.DrawPolygon(outlinePen, triangle);
                    }

                    // 4. Draw black exclamation mark (!)
                    using (Pen markPen = new Pen(Color.Black, 2f))
                    {
                        markPen.StartCap = LineCap.Round;
                        markPen.EndCap = LineCap.Round;
                        // Exclamation vertical line
                        g.DrawLine(markPen, 22.5f, 18.5f, 22.5f, 23.5f);
                    }
                    using (SolidBrush dotBrush = new SolidBrush(Color.Black))
                    {
                        // Exclamation dot
                        g.FillEllipse(dotBrush, 21.5f, 25.5f, 2f, 2f);
                    }
                }
            }
            return bmp.GetHicon();
        }

        private IntPtr LoadAppIcon()
        {
            try
            {
                string appDir = AppContext.BaseDirectory;
                string iconPath = System.IO.Path.Combine(appDir, "Assets", "AppIcon.ico");
                if (!System.IO.File.Exists(iconPath))
                {
                    iconPath = System.IO.Path.Combine(appDir, "AppIcon.ico");
                }

                if (System.IO.File.Exists(iconPath))
                {
                    _loadedIcon = new Icon(iconPath, 32, 32);
                    return _loadedIcon.Handle;
                }

                string? procPath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(procPath))
                {
                    _loadedIcon = Icon.ExtractAssociatedIcon(procPath);
                    if (_loadedIcon != null)
                    {
                        return _loadedIcon.Handle;
                    }
                }
            }
            catch
            {
                // Fallback to generated icon
            }

            return CreateClockIcon(IsSystemLightTheme());
        }

        private string GetAppVersion()
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (version != null)
            {
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }
            return "0.0.7";
        }

        private void Cleanup()
        {
            _timer?.Stop();
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            if (_loadedIcon != null)
            {
                _loadedIcon.Dispose();
                _loadedIcon = null;
            }
            else if (_iconHandle != IntPtr.Zero)
            {
                DestroyIcon(_iconHandle);
            }
            if (_messageWindow != IntPtr.Zero) DestroyWindow(_messageWindow);

            if (_dummyWindow != null)
            {
                _dummyWindow.Close();
                _dummyWindow = null;
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
