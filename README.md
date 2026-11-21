# Uptime

Uptime is a lightweight and theme-adaptive Windows utility that displays your system's exact running uptime directly in the system tray.

<p align="center">
  <img src="https://i.imgur.com/LnJTkIE.png" alt="Uptime Screenshot" />
</p>

## Features

- **System Tray Display**: Displays live system uptime in days, hours, and minutes via tray tooltip.
- **Restart Warning Badge**: Overlays a Fluent-style yellow warning badge when system uptime crosses a threshold you pick from the tray menu (3, 7, 14, 30 days or never; default 7), or one enforced by IT policy.
- **Fast Startup Awareness**: Detects Windows Fast Startup and informs users why shutting down doesn't reset uptime.
- **Native Windows 11 Tray Menu**: The right-click menu is a real Windows menu, so it matches the system's look, light/dark mode, display scaling, keyboard navigation and screen readers.
- **Direct Computer Restart**: 1-click "Restart computer…" menu option with safety confirmation prompt.
- **Windows Event Log Monitoring**: Writes Event Log Warning (Event ID 1001, Source `Uptime`) when threshold is reached for SIEM & RMM monitoring.
- **RMM Command-Line Status Query**: Executing `Uptime.exe /status` returns JSON machine status to stdout for automated IT audit scripts.
- **Explorer Restart Recovery**: Automatically re-registers tray icon if Windows Explorer restarts.
- **Group Policy & Enterprise Ready**: Full support for ADMX templates, Group Policy GPO, silent CLI installations, and IT management tools (Intune, SCCM, PDQ Deploy, GPO).

## Installation

Download the latest installer from the [Releases](https://github.com/sourajitk/Uptime/releases) page.

### Interactive GUI Installation
Run `Uptime-vX.Y.Z-Setup-win-x64.exe` (or `-win-arm64.exe`) and follow the wizard to select per-user (`LocalAppData`) or system-wide (`Program Files`) installation.

To uninstall, open **Settings › Apps › Installed apps**, find **Uptime** and select **Uninstall**.

### Portable
`Uptime-vX.Y.Z-Portable-win-x64.exe` (or `-win-arm64.exe`) runs without installing anything, and has the .NET runtime built in. Put it anywhere and run it.

### Silent Enterprise Installation
For mass deployment via IT administration software (Intune, SCCM, PDQ Deploy, GPO):

- **System-Wide Silent Install (All Users):**
  ```cmd
  Uptime-vX.Y.Z-Setup-win-x64.exe /silent /allusers
  ```
- **System-Wide Silent Uninstall:**
  ```cmd
  Uptime-vX.Y.Z-Setup-win-x64.exe /silent /uninstall /allusers
  ```
- **Per-User Silent Install:**
  ```cmd
  Uptime-vX.Y.Z-Setup-win-x64.exe /silent /currentuser
  ```

For detailed corporate deployment flags, GPO ADMX templates, detection rules, and code signing guidance, refer to [ENTERPRISE.md](ENTERPRISE.md).

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). The installer embeds the published app, so one command builds both:

```cmd
dotnet publish UptimeInstaller/UptimeInstaller.csproj -c Release -r win-x64
```

The installer is written to `UptimeInstaller/bin/Release/net10.0-windows/win-x64/publish/`. Uptime and its installer require the **.NET 10 Desktop Runtime**.

The version number for both the app and the installer is set in one place, [Directory.Build.props](Directory.Build.props). Bump it there before tagging a release (`vX.Y.Z`); CI fails the release if the tag and the file disagree.

## License

This project is licensed under the [Apache-2.0 License](LICENSE).
Copyright (c) 2026 Sourajit Karmakar.
