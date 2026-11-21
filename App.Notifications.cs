using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace Uptime
{
    public partial class App
    {
        // Notifications
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
    }
}
