# Uptime

Uptime is a lightweight and theme-adaptive Windows utility that displays your system's exact running uptime directly in the system tray.

<p align="center">
  <img src="https://i.imgur.com/y9VLM4b.png" alt="Uptime Screenshot" />
</p>

## Features

- **System Tray Display**: Displays live system uptime in days, hours, and minutes via tray tooltip.
- **Theme Adaptive**: Dynamic clock icon matches Windows Light and Dark themes seamlessly.
- **Lightweight**: Minimal memory footprint and low CPU usage.
- **Explorer Restart Recovery**: Automatically re-registers tray icon if Windows Explorer restarts.
- **Enterprise Ready**: Full support for silent installations, all-users (machine-wide) deployments, and IT management tools (Intune, SCCM, PDQ Deploy, GPO).

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

For detailed corporate deployment flags, detection rules, and code signing guidance, refer to [ENTERPRISE.md](ENTERPRISE.md).

## License

This project is licensed under the [Apache-2.0 License](LICENSE).
Copyright 2026 Sourajit Karmakar.
