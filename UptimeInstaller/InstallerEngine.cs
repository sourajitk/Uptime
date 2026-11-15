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
        private const string EventSource = "Uptime";
        private const string Publisher = "Sourajit Karmakar";
        private const string RepositoryUrl = "https://github.com/sourajitk/Uptime";

        // Settings > Apps entry (HKCU for per-user installs, HKLM for all users)
        private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Uptime";

        // A copy of this installer kept in the install folder, so Uptime can be removed from Settings > Apps
        public const string UninstallerFileName = "Uninstall.exe";

        // Passed by the Run entry so a duplicate launch at sign-in stays quiet (see the app's single-instance handling)
        private const string AutostartArgument = "/autostart";

        // Only these files are ever deleted from an install folder. The folder may be user-chosen (e.g. an
        // existing directory), so it must never be wiped wholesale.
        private static readonly string[] InstalledFileNames = { "Uptime.exe", "UptimeTaskbarApp.exe", UninstallerFileName };

        // Set when this process is the installed Uninstall.exe and removed everything else: it deletes itself on exit
        private string? _pendingSelfDeleteFolder;

        public bool IsSilent { get; set; }
        public bool IsUninstall { get; set; }
        public bool IsAllUsers { get; set; }
        /// <summary>True when /allusers, /currentuser or /dir was given, so detection of an existing install must not override it.</summary>
        public bool IsScopeExplicit { get; private set; }
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
                        IsScopeExplicit = true;
                        continue;
                    case "/currentuser": case "/user": case "--currentuser": case "--user":
                        IsAllUsers = false;
                        IsScopeExplicit = true;
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
                    IsScopeExplicit = true;
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
            var sb = new StringBuilder();
            if (silent) sb.Append("/silent ");
            else sb.Append("/page=3 ");
            if (uninstall) sb.Append("/uninstall ");
            sb.Append(DirArgument(installPath)).Append(' ');
            sb.Append(isAllUsers ? "/allusers " : "/currentuser ");
            sb.Append(enableStartup ? "/startup " : "/nostartup ");
            if (silent) sb.Append(launch ? "/launch" : "/nolaunch");
            return sb.ToString().TrimEnd();
        }

        /// <summary>Builds a quoted /dir="..." switch.</summary>
        private static string DirArgument(string installPath)
        {
            // A trailing backslash would escape the closing quote, so double it ("C:\" -> "C:\\", which parses back to C:\)
            string dir = installPath.TrimEnd('\\');
            if (dir.Length < installPath.Length) dir += @"\\";
            return $"/dir=\"{dir}\"";
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

            progress?.Invoke("Extracting files...", 55);
            string targetExePath = Path.Combine(installPath, "Uptime.exe");
            ExtractEmbeddedApp(targetExePath);
            CopyUninstaller(installPath);

            progress?.Invoke("Creating shortcuts...", 70);
            RemoveStartMenuShortcut(); // clears a stale shortcut from the other scope
            CreateStartMenuShortcut(targetExePath, installPath, isAllUsers);

            progress?.Invoke("Configuring startup settings...", 80);
            ConfigureStartup(targetExePath, enableStartup, isAllUsers);
            RegisterEventLogSource();

            progress?.Invoke("Registering with Windows...", 90);
            RegisterUninstallEntry(installPath, isAllUsers);

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

                // Started from Settings > Apps: this process *is* the installed Uninstall.exe and can't delete itself yet
                if (IsRunningFrom(installPath))
                {
                    _pendingSelfDeleteFolder = installPath;
                }
            }
            if (isAllUsers)
            {
                UnregisterEventLogSource();
            }

            UnregisterUninstallEntry(isAllUsers);

            progress?.Invoke("Finalizing...", 100);
        }

        // ── Settings > Apps registration ──
        private static string? RunningExePath
        {
            get
            {
                try { return Environment.ProcessPath is { } p ? Path.GetFullPath(p) : null; }
                catch { return null; }
            }
        }

        private static bool IsRunningFrom(string folder)
        {
            string? exe = RunningExePath;
            if (exe == null) return false;
            try
            {
                return string.Equals(Path.GetDirectoryName(exe), Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Keeps a copy of this installer in the install folder; Settings > Apps runs it to uninstall.</summary>
        private static void CopyUninstaller(string installPath)
        {
            string? source = RunningExePath;
            if (source == null) throw new InvalidOperationException("Could not determine the installer's own path.");

            string target = Path.Combine(installPath, UninstallerFileName);
            if (string.Equals(source, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                return; // Re-installing from the installed Uninstall.exe itself
            }
            File.Copy(source, target, overwrite: true);
        }

        private static void RegisterUninstallEntry(string installPath, bool isAllUsers)
        {
            string appExe = Path.Combine(installPath, "Uptime.exe");
            string uninstaller = Path.Combine(installPath, UninstallerFileName);
            string scope = isAllUsers ? "/allusers" : "/currentuser";
            string uninstallCommand = $"\"{uninstaller}\" /uninstall {scope} {DirArgument(installPath)}";

            long sizeBytes = 0;
            foreach (string file in new[] { appExe, uninstaller })
            {
                try { sizeBytes += new FileInfo(file).Length; } catch { }
            }

            // An entry in the other scope would be a stale duplicate (e.g. after switching from per-user to all users)
            UnregisterUninstallEntry(!isAllUsers);

            RegistryKey root = isAllUsers ? Registry.LocalMachine : Registry.CurrentUser;
            using RegistryKey key = root.CreateSubKey(UninstallKeyPath, true);
            key.SetValue("DisplayName", "Uptime");
            key.SetValue("DisplayVersion", GetProductVersion(appExe));
            key.SetValue("Publisher", Publisher);
            key.SetValue("DisplayIcon", $"{appExe},0");
            key.SetValue("InstallLocation", installPath);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
            key.SetValue("UninstallString", uninstallCommand);
            key.SetValue("QuietUninstallString", uninstallCommand + " /silent");
            key.SetValue("URLInfoAbout", RepositoryUrl);
            key.SetValue("HelpLink", RepositoryUrl);
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeBytes / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        private static void UnregisterUninstallEntry(bool isAllUsers)
        {
            try
            {
                (isAllUsers ? Registry.LocalMachine : Registry.CurrentUser).DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
            }
            catch { /* e.g. HKLM without administrator rights */ }
        }

        private static string GetProductVersion(string exePath)
        {
            try
            {
                string? ver = FileVersionInfo.GetVersionInfo(exePath).ProductVersion;
                if (!string.IsNullOrWhiteSpace(ver))
                {
                    int plus = ver.IndexOf('+'); // strip source revision metadata
                    return plus > 0 ? ver[..plus] : ver;
                }
            }
            catch { }
            return typeof(InstallerEngine).Assembly.GetName().Version?.ToString(3) ?? "";
        }

        /// <summary>
        /// Call when the process is about to exit. If this process is the installed Uninstall.exe and just uninstalled
        /// Uptime, a hidden command deletes it (and the then-empty folder) once the process has exited and released the file.
        /// </summary>
        public void RunPendingSelfDelete()
        {
            string? folder = _pendingSelfDeleteFolder;
            string? exe = RunningExePath;
            _pendingSelfDeleteFolder = null;
            if (folder == null || exe == null) return;

            // Only ever this one known file, and the folder only if it is empty (rd without /s)
            if (!string.Equals(Path.GetFileName(exe), UninstallerFileName, StringComparison.OrdinalIgnoreCase)) return;
            string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

            // Retry for up to ~30 s: ping provides a one-second pause without needing a console (unlike timeout.exe).
            string command = $"/d /c (for /l %i in (1,1,30) do @if exist \"{exe}\" (ping -n 2 127.0.0.1 >nul & del /f /q \"{exe}\" 2>nul)) & rd \"{dir}\" 2>nul";
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    Arguments = command,
                    WorkingDirectory = Environment.SystemDirectory, // must not be the folder being removed
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch { /* Best effort: at worst Uninstall.exe is left behind */ }
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
            string? runningExe = RunningExePath;
            foreach (string name in InstalledFileNames)
            {
                string file = Path.Combine(path, name);
                if (!File.Exists(file)) continue;

                // The running Uninstall.exe can't be deleted while in use; it is replaced by CopyUninstaller or self-deleted on exit
                if (runningExe != null && string.Equals(Path.GetFullPath(file), runningExe, StringComparison.OrdinalIgnoreCase)) continue;

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

                // Startup was disabled: use the Settings > Apps registration, then the default locations
                foreach ((RegistryKey root, bool allUsers) in new[] { (Registry.CurrentUser, false), (Registry.LocalMachine, true) })
                {
                    using RegistryKey? key = root.OpenSubKey(UninstallKeyPath);
                    if (key?.GetValue("InstallLocation") is string dir && File.Exists(Path.Combine(dir, "Uptime.exe")))
                    {
                        return new ExistingInstall(dir, allUsers, StartsAtLogin: false);
                    }
                }

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

        // ── Registry / Event Log ──
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
                    key.SetValue(AppName, $"\"{exePath}\" {AutostartArgument}");
                }
                else
                {
                    key.DeleteValue(AppName, false);
                }
                key.DeleteValue(LegacyAppName, false);
            }
            catch { /* e.g. HKLM without administrator rights */ }
        }

        /// <summary>Registers the "Uptime" Event Log source so the app can write Event ID 1001 without administrator rights.</summary>
        private static void RegisterEventLogSource()
        {
            if (!IsAdministrator()) return;
            try
            {
                if (!EventLog.SourceExists(EventSource))
                {
                    EventLog.CreateEventSource(EventSource, "Application");
                }
            }
            catch { }
        }

        private static void UnregisterEventLogSource()
        {
            if (!IsAdministrator()) return;
            try
            {
                if (EventLog.SourceExists(EventSource))
                {
                    EventLog.DeleteEventSource(EventSource);
                }
            }
            catch { }
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
