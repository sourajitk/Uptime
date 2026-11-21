namespace Uptime
{
    public partial class App
    {
        // Tray icon plumbing
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

        // Periodic update
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
    }
}
