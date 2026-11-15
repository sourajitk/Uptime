using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace UptimeInstaller
{
    /// <summary>An Uptime installation discovered on this machine.</summary>
    public sealed record ExistingInstall(string Directory, bool IsAllUsers, bool StartsAtLogin);

    public class InstallerEngine
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "Uptime";
        private const string LegacyAppName = "UptimeTaskbarApp";

        // Only these files are ever deleted from an install folder. The folder may be user-chosen (e.g. an
        // existing directory), so it must never be wiped wholesale.
        private static readonly string[] InstalledFileNames = { "Uptime.exe", "UptimeTaskbarApp.exe" };

        public bool IsSilent { get; set; }
        public bool IsUninstall { get; set; }
        public bool IsAllUsers { get; set; }
        public bool LaunchApp { get; set; } = true;
        public bool EnableStartup { get; set; } = true;
        public string? CustomInstallPath { get; set; }
        public int StartPage { get; set; }

        public string DefaultCurrentUserPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Uptime");

        public string DefaultAllUsersPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Uptime");

        public string GetTargetInstallPath()
        {
            if (!string.IsNullOrWhiteSpace(CustomInstallPath))
            {
                return CustomInstallPath.Trim();
            }
            return IsAllUsers ? DefaultAllUsersPath : DefaultCurrentUserPath;
        }

        // ── Command line ──
        private static bool TryGetValue(string arg, out string value, params string[] prefixes)
        {
            foreach (string prefix in prefixes)
            {
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    value = arg.Substring(prefix.Length).Trim().Trim('"');
                    return value.Length > 0;
                }
            }
            value = "";
            return false;
        }

        /// <summary>Parses installer switches. Returns true when a silent switch was supplied.</summary>
        public bool ParseCommandLine(string[] args)
        {
            if (args == null || args.Length == 0) return false;

            bool explicitLaunch = false;

            foreach (string raw in args)
            {
                string arg = raw.Trim();
                string a = arg.ToLowerInvariant();

                switch (a)
                {
                    case "/s": case "-s": case "/silent": case "--silent":
                    case "/q": case "-q": case "/quiet": case "--quiet":
                        IsSilent = true;
                        continue;
                    case "/u": case "-u": case "/uninstall": case "--uninstall":
                        IsUninstall = true;
                        continue;
                    case "/allusers": case "/all": case "--allusers": case "--all":
                        IsAllUsers = true;
                        continue;
                    case "/currentuser": case "/user": case "--currentuser": case "--user":
                        IsAllUsers = false;
                        continue;
                    case "/launch": case "--launch":
                        LaunchApp = true;
                        explicitLaunch = true;
                        continue;
                    case "/nolaunch": case "--nolaunch":
                        LaunchApp = false;
                        explicitLaunch = true;
                        continue;
                    case "/startup": case "--startup":
                        EnableStartup = true;
                        continue;
                    case "/nostartup": case "--nostartup":
                        EnableStartup = false;
                        continue;
                }

                if (TryGetValue(arg, out string page, "/page=", "--page=") && int.TryParse(page, out int p))
                {
                    StartPage = p;
                }
                else if (TryGetValue(arg, out string dir, "/dir=", "--dir=", "/d="))
                {
                    CustomInstallPath = dir;
                }
            }

            // In silent mode the app is not launched unless /launch was explicitly requested
            if (IsSilent && !explicitLaunch)
            {
                LaunchApp = false;
            }

            return IsSilent;
        }

        /// <summary>Builds the switches needed to repeat the current operation in an elevated copy of the installer.</summary>
        public string BuildElevationArguments(string installPath, bool isAllUsers, bool enableStartup, bool uninstall, bool silent, bool launch)
        {
            // A trailing backslash would escape the closing quote
            string dir = installPath.Length > 3 ? installPath.TrimEnd('\\') : installPath;

            var sb = new StringBuilder();
            if (silent) sb.Append("/silent ");
            else sb.Append("/page=3 ");
            if (uninstall) sb.Append("/uninstall ");
            sb.Append($"/dir=\"{dir}\" ");
            sb.Append(isAllUsers ? "/allusers " : "/currentuser ");
            sb.Append(enableStartup ? "/startup " : "/nostartup ");
            if (silent) sb.Append(launch ? "/launch" : "/nolaunch");
            return sb.ToString().TrimEnd();
        }

        // ── Privileges ──
        public static bool IsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return false;
            string r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return path.Equals(r, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when the path lives under Program Files (needs administrator rights to write).</summary>
        public static bool IsSystemDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                return IsUnder(p, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
                    || IsUnder(p, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            }
            catch
            {
                return false;
            }
        }

        public bool NeedsElevation(string installPath, bool isAllUsers) =>
            (isAllUsers || IsSystemDirectory(installPath)) && !IsAdministrator();

        /// <summary>
        /// Relaunches this installer elevated (UAC prompt). Returns the child's exit code when <paramref name="wait"/> is set,
        /// 0 when it was started without waiting, or null when elevation was declined / failed.
        /// </summary>
        public static int? RunElevated(string arguments, bool wait)
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return null;

                using Process? process = Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    Verb = "runas",
                    UseShellExecute = true
                });

                if (process == null) return null;
                if (!wait) return 0;

                process.WaitForExit();
                return process.ExitCode;
            }
            catch
            {
                return null; // User cancelled the UAC prompt
            }
        }

        // ── Silent mode ──
        public int RunSilent()
        {
            try
            {
                string installPath = GetTargetInstallPath();

                if (NeedsElevation(installPath, IsAllUsers))
                {
                    string args = BuildElevationArguments(installPath, IsAllUsers, EnableStartup, IsUninstall, silent: true, LaunchApp);
                    // Wait for the elevated child so callers (Intune / SCCM / scripts) get the real result.
                    return RunElevated(args, wait: true) ?? 1;
                }

                if (IsUninstall)
                {
                    PerformUninstall(installPath, IsAllUsers);
                }
                else
                {
                    PerformInstall(installPath, EnableStartup, IsAllUsers, LaunchApp);
                }
                return 0;
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine($"[UptimeInstaller Error] {ex.Message}"); } catch { }
                return 1;
            }
        }

        // ── Install / uninstall ──
        public void PerformInstall(string installPath, bool enableStartup, bool isAllUsers, bool launchApp, Action<string, int>? progress = null)
        {
            progress?.Invoke("Closing running instances...", 10);
            KillRunningInstances(AppName);
            KillRunningInstances(LegacyAppName);

            progress?.Invoke("Preparing target directory...", 30);
            CleanupLegacyInstallFolder();
            Directory.CreateDirectory(installPath);
            RemoveInstalledFiles(installPath);

            progress?.Invoke("Extracting files...", 60);
            string targetExePath = Path.Combine(installPath, "Uptime.exe");
            ExtractEmbeddedApp(targetExePath);

            progress?.Invoke("Creating shortcuts...", 80);
            RemoveStartMenuShortcut(); // clears a stale shortcut from the other scope
            CreateStartMenuShortcut(targetExePath, installPath, isAllUsers);

            progress?.Invoke("Configuring startup settings...", 90);
            ConfigureStartup(targetExePath, enableStartup, isAllUsers);

            progress?.Invoke("Finalizing...", 100);

            if (launchApp)
            {
                LaunchApplication(targetExePath);
            }
        }

        public void PerformUninstall(string installPath, bool isAllUsers, Action<string, int>? progress = null)
        {
            progress?.Invoke("Closing running instances...", 20);
            KillRunningInstances(AppName);
            KillRunningInstances(LegacyAppName);

            progress?.Invoke("Removing startup configurations...", 40);
            ConfigureStartup("", false, isAllUsers);

            progress?.Invoke("Removing shortcuts...", 60);
            RemoveStartMenuShortcut();

            progress?.Invoke("Deleting files...", 80);
            if (Directory.Exists(installPath))
            {
                RemoveInstalledFiles(installPath);
                RemoveDirectoryIfEmpty(installPath);
            }

            progress?.Invoke("Finalizing...", 100);
        }

        /// <summary>
        /// Starts the app. When the installer is elevated the app is started through Explorer so it runs
        /// with the user's normal rights instead of inheriting administrator rights.
        /// </summary>
        public static void LaunchApplication(string exePath)
        {
            if (!File.Exists(exePath)) return;

            try
            {
                if (IsAdministrator())
                {
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{exePath}\"", UseShellExecute = false });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        WorkingDirectory = Path.GetDirectoryName(exePath),
                        UseShellExecute = true
                    });
                }
            }
            catch { /* Launching is best-effort */ }
        }

        public void KillRunningInstances(string processName)
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                    catch { }
                }
            }
        }

        /// <summary>Deletes Uptime's own executables from a folder, leaving any other files untouched.</summary>
        public void RemoveInstalledFiles(string path)
        {
            foreach (string name in InstalledFileNames)
            {
                string file = Path.Combine(path, name);
                if (!File.Exists(file)) continue;

                // The process may need a moment to release its image after being killed
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.Delete(file);
                        break;
                    }
                    catch (Exception) when (attempt < 10)
                    {
                        Thread.Sleep(200);
                    }
                    catch (Exception ex)
                    {
                        throw new IOException($"Failed to delete file '{file}': {ex.Message}", ex);
                    }
                }
            }
        }

        private static void RemoveDirectoryIfEmpty(string path)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(path).Any())
                {
                    Directory.Delete(path);
                }
            }
            catch { }
        }

        public void ExtractEmbeddedApp(string targetExePath)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string? resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("Uptime.exe", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                throw new InvalidOperationException("Embedded Uptime.exe was not found inside the installer resources.");
            }

            using Stream resourceStream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("Embedded resource stream was empty.");
            using var fileStream = new FileStream(targetExePath, FileMode.Create, FileAccess.Write);
            resourceStream.CopyTo(fileStream);
        }

        // ── Existing installation ──
        private static string? ReadRunEntry(RegistryKey root)
        {
            try
            {
                using RegistryKey? key = root.OpenSubKey(RunKeyPath);
                string? val = (key?.GetValue(AppName) ?? key?.GetValue(LegacyAppName)) as string;
                if (string.IsNullOrWhiteSpace(val)) return null;

                string exe = val.Trim();
                // Value is normally "C:\path\Uptime.exe" (quoted), optionally followed by arguments
                if (exe.StartsWith('"'))
                {
                    int end = exe.IndexOf('"', 1);
                    exe = end > 0 ? exe.Substring(1, end - 1) : exe.Trim('"');
                }
                return File.Exists(exe) ? Path.GetDirectoryName(exe) : null;
            }
            catch
            {
                return null;
            }
        }

        public ExistingInstall? DetectExistingInstallation()
        {
            try
            {
                string? userDir = ReadRunEntry(Registry.CurrentUser);
                if (userDir != null)
                {
                    return new ExistingInstall(userDir, IsSystemDirectory(userDir), StartsAtLogin: true);
                }

                string? machineDir = ReadRunEntry(Registry.LocalMachine);
                if (machineDir != null)
                {
                    return new ExistingInstall(machineDir, IsAllUsers: true, StartsAtLogin: true);
                }

                // Startup was disabled: fall back to the default locations
                foreach ((string dir, bool allUsers) in new[] { (DefaultCurrentUserPath, false), (DefaultAllUsersPath, true) })
                {
                    if (InstalledFileNames.Any(n => File.Exists(Path.Combine(dir, n))))
                    {
                        return new ExistingInstall(dir, allUsers, StartsAtLogin: false);
                    }
                }
            }
            catch { }

            return null;
        }

        // ── Shortcuts ──
        public void CreateStartMenuShortcut(string exePath, string workingDir, bool isAllUsers)
        {
            try
            {
                string folder = Environment.GetFolderPath(isAllUsers
                    ? Environment.SpecialFolder.CommonPrograms
                    : Environment.SpecialFolder.Programs);

                if (string.IsNullOrEmpty(folder))
                {
                    folder = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                }

                ShellLinkHelper.CreateShortcut(Path.Combine(folder, "Uptime.lnk"), exePath, "Monitor system uptime from the taskbar!", workingDir);
            }
            catch { }
        }

        public void RemoveStartMenuShortcut()
        {
            foreach (string baseFolder in new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
            })
            {
                if (string.IsNullOrEmpty(baseFolder)) continue;
                foreach (string name in new[] { "Uptime.lnk", "UptimeTaskbarApp.lnk" })
                {
                    try
                    {
                        string path = Path.Combine(baseFolder, name);
                        if (File.Exists(path)) File.Delete(path);
                    }
                    catch { }
                }
            }
        }

        private void CleanupLegacyInstallFolder()
        {
            // "UptimeTaskbarApp" folders were created exclusively by earlier versions of this app
            foreach (string root in new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            })
            {
                try
                {
                    string legacyDir = Path.Combine(root, LegacyAppName);
                    if (Directory.Exists(legacyDir))
                    {
                        Directory.Delete(legacyDir, true);
                    }
                }
                catch { }
            }
        }

        // ── Registry ──
        public void ConfigureStartup(string exePath, bool enable, bool isAllUsers)
        {
            SetRunEntry(Registry.CurrentUser, exePath, enable && !isAllUsers);
            SetRunEntry(Registry.LocalMachine, exePath, enable && isAllUsers);
        }

        private static void SetRunEntry(RegistryKey root, string exePath, bool enable)
        {
            try
            {
                using RegistryKey? key = root.OpenSubKey(RunKeyPath, true);
                if (key == null) return;

                if (enable)
                {
                    key.SetValue(AppName, $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue(AppName, false);
                }
                key.DeleteValue(LegacyAppName, false);
            }
            catch { /* e.g. HKLM without administrator rights */ }
        }
    }

    // ── Native Windows COM Shortcut Helper ──
    internal static class ShellLinkHelper
    {
        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        internal class ShellLink { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        internal interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010b-0000-0000-C000-000000000046")]
        internal interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            void IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder ppszFileName);
        }

        public static void CreateShortcut(string shortcutPath, string targetPath, string description, string workingDir)
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(targetPath);
                link.SetDescription(description);
                link.SetWorkingDirectory(workingDir);
                ((IPersistFile)link).Save(shortcutPath, false);
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
    }
}
