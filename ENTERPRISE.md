# Enterprise Deployment Guide for Uptime

This guide provides instructions for IT administrators deploying Uptime across corporate workstations using management tools such as Microsoft Intune, System Center Configuration Manager (SCCM/MCM), PDQ Deploy, or Group Policy (GPO).

---

## Installation Scopes

Uptime supports two installation scopes:

1. **Per-User Installation (Default for non-elevated users)**
   - Target Folder: `%localappdata%\Programs\Uptime`
   - Startup Registry: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
   - Start Menu Shortcut: `%appdata%\Microsoft\Windows\Start Menu\Programs\Uptime.lnk`
   - Apps & Features Entry: `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Uptime`

2. **System-Wide / All Users Installation (Recommended for Enterprise Management)**
   - Target Folder: `%programfiles%\Uptime`
   - Startup Registry: `HKLM\Software\Microsoft\Windows\CurrentVersion\Run`
   - Start Menu Shortcut: `%programdata%\Microsoft\Windows\Start Menu\Programs\Uptime.lnk`
   - Apps & Features Entry: `HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\Uptime`
   - Requires administrative privileges during installation.

Both scopes install two files: `Uptime.exe` and `Uninstall.exe` (a copy of the setup program, used by **Settings › Apps** to remove Uptime).

The Apps & Features entry records `DisplayVersion`, `InstallLocation`, `UninstallString` and `QuietUninstallString`, so management tools can uninstall Uptime without the original setup file.

---

## Group Policy & Registry Configuration

IT Administrators can enforce restart recommendation thresholds across domain endpoints using Group Policy (GPO) or Registry keys.

### Registry Policy Locations

| Setting | Registry Key | Value Name | Type | Value / Description |
| :--- | :--- | :--- | :--- | :--- |
| **Max Uptime Threshold (GPO)** | `HKEY_LOCAL_MACHINE\Software\Policies\Uptime` | `MaxUptimeDays` | `DWORD` | Number of days (e.g. `7`, `14`, `30`). Set `0` to disable warning. |
| **Max Uptime Threshold (System)** | `HKEY_LOCAL_MACHINE\Software\Uptime` | `MaxUptimeDays` | `DWORD` | Fallback machine policy. |
| **User Threshold Preference** | `HKEY_CURRENT_USER\Software\Uptime` | `MaxUptimeDays` | `DWORD` | End-user threshold preference (default `7`). |

When an IT policy is configured in `HKLM`, the tray menu's **Restart reminder** submenu shows `Managed by your organization` with the enforced value selected and the choices locked. Without a policy, users choose 3, 7, 14, 30 days or Never from that submenu (stored in `HKCU`).

### ADMX Administrative Templates
Group Policy templates are provided in the `policy/` directory:
- Copy `policy/Uptime.admx` to `C:\Windows\PolicyDefinitions\`
- Copy `policy/en-US/Uptime.adml` to `C:\Windows\PolicyDefinitions\en-US\`

Once copied, policies can be managed under `Computer Configuration -> Administrative Templates -> Uptime`.

---

## RMM & SIEM Integration

### 1. Command-Line Status Query (`/status`)
Executing `Uptime.exe /status` returns JSON machine status to stdout for RMM agents and audit scripts:
```json
{
  "uptime_seconds": 604800,
  "uptime_days": 7.0,
  "uptime_formatted": "7 days, 0 hours, 0 minutes",
  "threshold_days": 7,
  "is_threshold_exceeded": true,
  "is_policy_enforced": false,
  "fast_startup_enabled": true
}
```

`Uptime.exe` is a GUI-subsystem executable, so some launchers return before it finishes writing. Wait for it explicitly, for example `Start-Process -Wait -NoNewWindow -RedirectStandardOutput status.json -FilePath Uptime.exe -ArgumentList '/status'`, or pipe it in PowerShell (`& ./Uptime.exe /status | ConvertFrom-Json`).

### 2. Windows Event Log Monitoring
When system uptime crosses the restart threshold, Uptime logs a Warning to the Windows Event Viewer:
- **Log:** `Application`
- **Source:** `Uptime`
- **Event ID:** `1001`
- **Level:** `Warning`

The `Uptime` event source is registered by the installer when it runs elevated (for example, all-users deployments). Without it, the warning is only written if Uptime itself runs as administrator.

SIEM systems (Splunk, Datadog, Microsoft Sentinel) and RMM tools (NinjaOne, N-able, Datto) can query Event ID `1001` to generate reboot compliance reports.

---

## Command-Line Syntax

The installer `Uptime-vX.Y.Z-Setup-win-x64.exe` (or `-win-arm64.exe`; replace `X.Y.Z` with the release version) supports command-line switches for unattended and automated enterprise deployments.

### Command-Line Options

| Switch | Description |
| :--- | :--- |
| `/silent`, `/s`, `/quiet`, `/q` | Runs the installer headlessly without displaying a GUI window. Returns exit code `0` on success, `1` on failure. When elevation is required, the installer waits for the elevated process and returns its exit code. |
| `/allusers`, `/all` | Forces system-wide installation to `Program Files` and `HKLM`. |
| `/currentuser`, `/user` | Forces per-user installation to `LocalAppData` and `HKCU`. |
| `/uninstall`, `/u` | Uninstalls Uptime. Can be combined with `/silent`. |
| `/dir="C:\Path"` | Overrides default installation directory. |
| `/startup`, `/nostartup` | Registers (default) or skips the "run at login" entry. |
| `/launch` | Launches Uptime automatically after silent installation. |
| `/nolaunch` | Default in silent mode. Prevents auto-launching after installation. |

---

## Deployment Examples

### 1. Microsoft Intune / SCCM (System-Wide Silent Install)
- **Install Command:**
  ```cmd
  Uptime-vX.Y.Z-Setup-win-x64.exe /silent /allusers
  ```
- **Uninstall Command:**
  ```cmd
  Uptime-vX.Y.Z-Setup-win-x64.exe /silent /uninstall /allusers
  ```
- **Uninstall Without the Setup File:**
  ```cmd
  "C:\Program Files\Uptime\Uninstall.exe" /silent /uninstall /allusers
  ```
- **Detection Rule (Registry, recommended):**
  - Key: `HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Uninstall\Uptime`
  - Value Name: `DisplayVersion`
  - Detection method: Version comparison, equals `X.Y.Z` (also detects outdated versions for supersedence)
- **Detection Rule (File):**
  - Path: `C:\Program Files\Uptime`
  - File: `Uptime.exe`

The Run entry is not a reliable detection rule: installing with `/nostartup` (or a user turning off **Start at login**) removes it while Uptime stays installed.

### 2. Per-User Silent Install
- **Install Command:**
  ```cmd
  Uptime-vX.Y.Z-Setup-win-x64.exe /silent /currentuser
  ```
- **Uninstall Command:**
  ```cmd
  Uptime-vX.Y.Z-Setup-win-x64.exe /silent /uninstall /currentuser
  ```

---

## Code Signing & Security Policies

When deploying Uptime in environments with strict EDR or Windows Defender Application Control (WDAC) policies:

1. **Digital Signatures:** Enterprise builds can be digitally signed using a corporate or EV Code Signing certificate.
2. **Path Rule Whitelisting:** If using AppLocker or Software Restriction Policies, whitelist by publisher certificate or file path:
   - System-wide: `C:\Program Files\Uptime\Uptime.exe`

---

## Licensing

Uptime is open-source software licensed under the **Apache License 2.0**. Commercial use, internal corporate distribution, and automated deployments are permitted.
