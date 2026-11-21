using System.Globalization;
using Microsoft.Win32;

namespace Uptime
{
    public partial class App
    {
        private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppRegistryName = "Uptime";
        private const string LegacyAppRegistryName = "UptimeTaskbarApp";
        private const string PolicyRegistryPath = @"Software\Policies\Uptime";
        private const string SettingsRegistryPath = @"Software\Uptime";
        private const string LegacySettingsRegistryPath = @"Software\UptimeTaskbarApp";
        private const string MaxUptimeDaysValue = "MaxUptimeDays";
        private const int DefaultThresholdDays = 7;

        // Startup registration
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

        // Threshold / policy
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
    }
}
