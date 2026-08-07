using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace UptimeInstaller
{
    public class InstallerEngine
    {
        public bool IsSilent { get; set; }
        public bool IsUninstall { get; set; }
        public bool IsAllUsers { get; set; }
        public bool LaunchApp { get; set; } = true;
        public string? CustomInstallPath { get; set; }

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

        public bool ParseCommandLine(string[] args)
        {
            if (args == null || args.Length == 0) return false;

            bool silentDetected = false;

            foreach (var arg in args)
            {
                string a = arg.Trim().ToLowerInvariant();

                if (a == "/s" || a == "-s" || a == "/silent" || a == "--silent" || a == "/q" || a == "-q" || a == "/quiet" || a == "--quiet")
                {
                    IsSilent = true;
                    silentDetected = true;
                }
                else if (a == "/u" || a == "-u" || a == "/uninstall" || a == "--uninstall")
                {
                    IsUninstall = true;
                }
                else if (a == "/allusers" || a == "/all" || a == "--allusers" || a == "--all")
                {
                    IsAllUsers = true;
                }
                else if (a == "/currentuser" || a == "/user" || a == "--currentuser" || a == "--user")
                {
                    IsAllUsers = false;
                }
                else if (a == "/launch" || a == "--launch")
                {
                    LaunchApp = true;
                }
                else if (a == "/nolaunch" || a == "--nolaunch")
                {
                    LaunchApp = false;
                }
                else if (a.StartsWith("/dir=") || a.StartsWith("--dir="))
                {
                    int idx = a.IndexOf('=');
                    if (idx >= 0 && idx < arg.Length - 1)
                    {
                        CustomInstallPath = arg.Substring(idx + 1).Trim('"');
                    }
                }
                else if (a.StartsWith("/d="))
                {
                    int idx = a.IndexOf('=');
                    if (idx >= 0 && idx < arg.Length - 1)
                    {
                        CustomInstallPath = arg.Substring(idx + 1).Trim('"');
                    }
                }
            }

            // In silent mode, default LaunchApp is false unless /launch was explicitly specified
            if (IsSilent && !args.Any(a => a.Equals("/launch", StringComparison.OrdinalIgnoreCase)))
            {
                LaunchApp = false;
            }

            return silentDetected;
        }

        public int RunSilent()
        {
            try
            {
                string installPath = GetTargetInstallPath();

                if (IsUninstall)
                {
                    PerformUninstall(installPath, IsAllUsers);
                }
                else
                {
                    PerformInstall(installPath, true, IsAllUsers, LaunchApp);
                }
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    Console.Error.WriteLine($"[UptimeInstaller Error] {ex.Message}");
                }
                catch { }
                return 1;
            }
        }

        public void PerformInstall(string installPath, bool enableStartup, bool isAllUsers, bool launchApp)
        {
            // 1. Terminate running processes
            KillRunningInstances("UptimeTaskbarApp");
            KillRunningInstances("Uptime");

            // 2. Prepare directory
            if (Directory.Exists(installPath))
            {
                CleanDirectory(installPath);
            }
            else
            {
                Directory.CreateDirectory(installPath);
            }

            // 3. Extract Embedded Executable
            string targetExePath = Path.Combine(installPath, "Uptime.exe");
            ExtractEmbeddedApp(targetExePath);

            // 4. Create Shortcuts
            CreateStartMenuShortcut(targetExePath, installPath, isAllUsers);

            // 5. Configure Windows Startup Registry
            ConfigureStartup(targetExePath, enableStartup, isAllUsers);

            // 6. Optionally launch app
            if (launchApp && File.Exists(targetExePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetExePath,
                    WorkingDirectory = installPath,
                    UseShellExecute = true
                });
            }
        }

        public void PerformUninstall(string installPath, bool isAllUsers)
        {
            // 1. Terminate running processes
            KillRunningInstances("UptimeTaskbarApp");
            KillRunningInstances("Uptime");

            // 2. Remove registry startup key
            ConfigureStartup("", false, isAllUsers);

            // 3. Remove Start Menu Shortcut
            RemoveStartMenuShortcut();

            // 4. Delete files and folder
            if (Directory.Exists(installPath))
            {
                CleanDirectory(installPath);
                try
                {
                    Directory.Delete(installPath, true);
                }
                catch { }
            }
        }

        public void KillRunningInstances(string processName)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch { }
            }
        }

        public void CleanDirectory(string path)
        {
            if (!Directory.Exists(path)) return;

            foreach (string file in Directory.GetFiles(path))
            {
                try { File.Delete(file); }
                catch (Exception ex)
                {
                    throw new IOException($"Failed to delete file '{file}': {ex.Message}", ex);
                }
            }

            foreach (string subDir in Directory.GetDirectories(path))
            {
                try { Directory.Delete(subDir, true); }
                catch { }
            }
        }

        public void ExtractEmbeddedApp(string targetExePath)
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("Uptime.exe") || n.EndsWith("UptimeTaskbarApp.exe")) ?? "";

            if (string.IsNullOrEmpty(resourceName))
            {
                throw new Exception("Embedded Uptime.exe was not found inside the installer resources.");
            }

            using (Stream? resourceStream = assembly.GetManifestResourceStream(resourceName))
            {
                if (resourceStream == null)
                {
                    throw new Exception("Embedded resource stream was empty.");
                }

                using (FileStream fileStream = new FileStream(targetExePath, FileMode.Create, FileAccess.Write))
                {
                    resourceStream.CopyTo(fileStream);
                }
            }
        }

        public void CreateStartMenuShortcut(string exePath, string workingDir, bool isAllUsers)
        {
            try
            {
                string folder = isAllUsers
                    ? Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
                    : Environment.GetFolderPath(Environment.SpecialFolder.Programs);

                if (string.IsNullOrEmpty(folder))
                {
                    folder = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                }

                string startMenuPath = Path.Combine(folder, "Uptime.lnk");
                ShellLinkHelper.CreateShortcut(startMenuPath, exePath, "Monitor system uptime from the taskbar!", workingDir);
            }
            catch { }
        }

        public void RemoveStartMenuShortcut()
        {
            try
            {
                string userPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    "Uptime.lnk");
                if (File.Exists(userPath)) File.Delete(userPath);
            }
            catch { }

            try
            {
                string commonPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                    "Uptime.lnk");
                if (File.Exists(commonPath)) File.Delete(commonPath);
            }
            catch { }
        }

        public void ConfigureStartup(string exePath, bool enable, bool isAllUsers)
        {
            // Configure HKCU
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        if (enable && !isAllUsers)
                        {
                            key.SetValue("Uptime", $"\"{exePath}\"");
                        }
                        else
                        {
                            key.DeleteValue("Uptime", false);
                        }
                        key.DeleteValue("UptimeTaskbarApp", false);
                    }
                }
            }
            catch { }

            // Configure HKLM
            try
            {
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        if (enable && isAllUsers)
                        {
                            key.SetValue("Uptime", $"\"{exePath}\"");
                        }
                        else
                        {
                            key.DeleteValue("Uptime", false);
                        }
                        key.DeleteValue("UptimeTaskbarApp", false);
                    }
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
            IShellLinkW link = (IShellLinkW)new ShellLink();
            link.SetPath(targetPath);
            link.SetDescription(description);
            link.SetWorkingDirectory(workingDir);
            IPersistFile file = (IPersistFile)link;
            file.Save(shortcutPath, false);
        }
    }
}
