# Enterprise Deployment Guide for Uptime

This guide provides instructions for IT administrators deploying Uptime across corporate workstations using management tools such as Microsoft Intune, System Center Configuration Manager (SCCM/MCM), PDQ Deploy, or Group Policy (GPO).

---

## Installation Scopes

Uptime supports two installation scopes:

1. **Per-User Installation (Default for non-elevated users)**
   - Target Folder: `%localappdata%\Programs\UptimeTaskbarApp`
   - Startup Registry: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
   - Start Menu Shortcut: `%appdata%\Microsoft\Windows\Start Menu\Programs\Uptime.lnk`

2. **System-Wide / All Users Installation (Recommended for Enterprise Management)**
   - Target Folder: `%programfiles%\UptimeTaskbarApp`
   - Startup Registry: `HKLM\Software\Microsoft\Windows\CurrentVersion\Run`
   - Start Menu Shortcut: `%programdata%\Microsoft\Windows\Start Menu\Programs\Uptime.lnk`
   - Requires administrative privileges during installation.

---

## Group Policy & Registry Configuration

IT Administrators can enforce restart recommendation thresholds across domain endpoints using Group Policy (GPO) or Registry keys.

### Registry Policy Locations

| Setting | Registry Key | Value Name | Type | Value / Description |
| :--- | :--- | :--- | :--- | :--- |
| **Max Uptime Threshold (GPO)** | `HKEY_LOCAL_MACHINE\Software\Policies\Uptime` | `MaxUptimeDays` | `DWORD` | Number of days (e.g. `7`, `14`, `30`). Set `0` to disable warning. |
| **Max Uptime Threshold (System)** | `HKEY_LOCAL_MACHINE\Software\UptimeTaskbarApp` | `MaxUptimeDays` | `DWORD` | Fallback machine policy. |
| **User Threshold Preference** | `HKEY_CURRENT_USER\Software\UptimeTaskbarApp` | `MaxUptimeDays` | `DWORD` | End-user threshold preference (default `7`). |

When an IT policy is configured in `HKLM`, the system tray menu displays `Restart Threshold (X Days - IT Policy)` and locks user modification.

### ADMX Administrative Templates
Group Policy templates are provided in the `policy/` directory:
- Copy `policy/Uptime.admx` to `C:\Windows\PolicyDefinitions\`
- Copy `policy/en-US/Uptime.adml` to `C:\Windows\PolicyDefinitions\en-US\`

Once copied, policies can be managed under `Computer Configuration -> Administrative Templates -> Uptime`.

---

## RMM & SIEM Integration

### 1. Command-Line Status Query (`/status`)
Executing `UptimeTaskbarApp.exe /status` returns JSON machine status to stdout for RMM agents and audit scripts:
```json
{
  "uptime_seconds": 604800,
  "uptime_days": 7.0,
  "uptime_formatted": "7 days, 0 hours, 0 minutes",
  "threshold_days": 7.0,
  "is_threshold_exceeded": true,
  "is_policy_enforced": false,
  "fast_startup_enabled": true
}
```

### 2. Windows Event Log Monitoring
When system uptime crosses the restart threshold, Uptime logs a Warning to the Windows Event Viewer:
- **Log:** `Application`
- **Source:** `Uptime`
- **Event ID:** `1001`
- **Level:** `Warning`

SIEM systems (Splunk, Datadog, Microsoft Sentinel) and RMM tools (NinjaOne, N-able, Datto) can query Event ID `1001` to generate reboot compliance reports.

---

## Command-Line Syntax

The installer `UptimeTaskbarAppSetup-win-x64.exe` supports command-line switches for unattended and automated enterprise deployments.

### Command-Line Options

| Switch | Description |
| :--- | :--- |
| `/silent`, `/s`, `/quiet`, `/q` | Runs the installer headlessly without displaying a GUI window. Returns exit code `0` on success, `1` on failure. |
| `/allusers`, `/all` | Forces system-wide installation to `Program Files` and `HKLM`. |
| `/currentuser`, `/user` | Forces per-user installation to `LocalAppData` and `HKCU`. |
| `/uninstall`, `/u` | Uninstalls Uptime. Can be combined with `/silent`. |
| `/dir="C:\Path"` | Overrides default installation directory. |
| `/launch` | Launches Uptime automatically after silent installation. |
| `/nolaunch` | Default in silent mode. Prevents auto-launching after installation. |

---

## Deployment Examples

### 1. Microsoft Intune / SCCM (System-Wide Silent Install)
- **Install Command:**
  ```cmd
  UptimeTaskbarAppSetup-win-x64.exe /silent /allusers
  ```
- **Uninstall Command:**
  ```cmd
  UptimeTaskbarAppSetup-win-x64.exe /silent /uninstall /allusers
  ```
- **Detection Rule (File):**
  - Path: `C:\Program Files\UptimeTaskbarApp`
  - File: `UptimeTaskbarApp.exe`
- **Detection Rule (Registry):**
  - Key: `HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Run`
  - Value Name: `UptimeTaskbarApp`

### 2. Per-User Silent Install
- **Install Command:**
  ```cmd
  UptimeTaskbarAppSetup-win-x64.exe /silent /currentuser
  ```
- **Uninstall Command:**
  ```cmd
  UptimeTaskbarAppSetup-win-x64.exe /silent /uninstall /currentuser
  ```

---

## Code Signing & Security Policies

When deploying Uptime in environments with strict EDR or Windows Defender Application Control (WDAC) policies:

1. **Digital Signatures:** Enterprise builds can be digitally signed using a corporate or EV Code Signing certificate.
2. **Path Rule Whitelisting:** If using AppLocker or Software Restriction Policies, whitelist by publisher certificate or file path:
   - System-wide: `C:\Program Files\UptimeTaskbarApp\UptimeTaskbarApp.exe`

---

## Licensing

Uptime is open-source software licensed under the **Apache License 2.0**. Commercial use, internal corporate distribution, and automated deployments are permitted.
