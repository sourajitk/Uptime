# Uptime

Uptime is a lightweight and theme-adaptive Windows utility that displays your system's exact running uptime directly in the system tray.

<p align="center">
  <img src="https://i.imgur.com/y9VLM4b.png" alt="Uptime Screenshot" />
</p>

## Features

- **System Tray Display**: Displays live system uptime in days, hours, and minutes via tray tooltip.
- **Restart Warning Badge**: Overlays a Fluent-style yellow warning badge when system uptime crosses a configured threshold (7, 14, 30 days or custom).
- **Fast Startup Awareness**: Detects Windows Fast Startup and informs users why shutting down doesn't reset uptime.
- **Direct Computer Restart**: 1-click "Restart Computer..." context menu option with safety confirmation prompt.
- **Windows Event Log Monitoring**: Writes Event Log Warning (Event ID 1001, Source `Uptime`) when threshold is reached for SIEM & RMM monitoring.
- **RMM Command-Line Status Query**: Executing `UptimeTaskbarApp.exe /status` returns JSON machine status to stdout for automated IT audit scripts.
- **Explorer Restart Recovery**: Automatically re-registers tray icon if Windows Explorer restarts.
- **Group Policy & Enterprise Ready**: Full support for ADMX templates, Group Policy GPO, silent CLI installations, and IT management tools (Intune, SCCM, PDQ Deploy, GPO).

## Installation

Download the latest installer from the [Releases](https://github.com/sourajitk/Uptime/releases) page.

### Interactive GUI Installation
Run `UptimeTaskbarAppSetup-win-x64.exe` and follow the wizard to select per-user (`LocalAppData`) or system-wide (`Program Files`) installation.

### Silent Enterprise Installation
For mass deployment via IT administration software (Intune, SCCM, PDQ Deploy, GPO):

- **System-Wide Silent Install (All Users):**
  ```cmd
  UptimeTaskbarAppSetup-win-x64.exe /silent /allusers
  ```
- **System-Wide Silent Uninstall:**
  ```cmd
  UptimeTaskbarAppSetup-win-x64.exe /silent /uninstall /allusers
  ```
- **Per-User Silent Install:**
  ```cmd
  UptimeTaskbarAppSetup-win-x64.exe /silent /currentuser
  ```

For detailed corporate deployment flags, GPO ADMX templates, detection rules, and code signing guidance, refer to [ENTERPRISE.md](ENTERPRISE.md).

## License

This project is licensed under the [Apache-2.0 License](LICENSE).
Copyright (c) 2026 Sourajit Karmakar.
