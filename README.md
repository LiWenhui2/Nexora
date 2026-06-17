# Nexora

[![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D4)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-net10.0--windows-512BD4)](https://dotnet.microsoft.com/)
[![Installer](https://img.shields.io/badge/installer-Inno%20Setup-blue)](https://jrsoftware.org/isinfo.php)

Nexora is a Windows desktop proxy client built around Xray Core, with a WPF interface for node management, subscription import, routing modes, system proxy control, TUN forwarding, traffic statistics, tray operation, in-app update checks, and optional account login.

The project is designed as a practical, self-contained Windows client: it bundles the runtime files needed by the application, stores user configuration locally, and generates Core configuration dynamically from the selected node and routing policy.

> Nexora is an independent project. Xray Core, sing-box, Wintun, and other third-party components are owned by their respective projects.

## Table of Contents

- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Subscription Import](#subscription-import)
- [Routing Modes](#routing-modes)
- [Local Data](#local-data)
- [Architecture](#architecture)
- [Development](#development)
- [Runtime Files](#runtime-files)
- [Troubleshooting](#troubleshooting)
- [Security and Privacy](#security-and-privacy)
- [Roadmap](#roadmap)
- [License](#license)

## Features

### Node Management

- Import and manage proxy nodes from share links.
- Supported node protocols include:
  - VMess
  - VLESS
  - Trojan
  - Shadowsocks
  - SOCKS
  - HTTP / HTTPS proxy nodes
- Add nodes manually from the desktop UI.
- Import nodes from clipboard text, local text files, QR code images, and subscription content.
- Export node share links and QR codes.
- Group imported nodes by subscription source.
- Filter nodes by protocol, region, availability, and subscription.
- Run TCP latency checks automatically on startup and manually from the UI.
- Remove unavailable nodes and remove duplicated nodes.
- Display subscription remaining traffic when the subscription server provides traffic metadata.
- Show region labels with localized names, including `中国·香港` and `中国·台湾`.

### Website Connectivity Test

- Test common websites through the local HTTP proxy after the proxy is running.
- Built-in targets include GitHub, ChatGPT, YouTube, Google, Twitter, Wikipedia, Discord, and Telegram.
- Display per-site latency, HTTP status, and pass/fail state in an adaptive card grid.

### Proxy Runtime

- Start and stop the bundled Core process from the UI or tray menu.
- Generate Xray-compatible configuration automatically.
- Provide local HTTP and SOCKS inbound ports.
- Verify proxy availability after startup by testing `www.google.com`.
- Record proxy verification failures in diagnostic logs.
- Keep proxy state, UI toggle state, and sidebar status synchronized.

### System Integration

- Configure Windows system proxy automatically.
- Support PAC mode with a generated local PAC file.
- Clear system proxy settings when the proxy stops or the app exits.
- Run in the Windows notification area after the main window is closed.
- Optional startup with Windows from the settings page.
- Tray menu includes:
  - Current node status
  - Runtime status
  - System proxy status
  - Upload/download speed
  - Start/stop proxy actions
  - System proxy toggle
  - Node switching
  - Routing mode switching
  - Open main window
  - Open logs
  - Exit application

### Account Login

- Optional email/password login and registration against a configurable auth API.
- Persist auth session locally for automatic sign-in on next launch.
- Show account status in the sidebar and sign out from the UI.

### TUN Mode

- Start a sing-box based TUN runtime.
- Route system traffic through the local SOCKS inbound.
- Use Wintun for the Windows virtual network adapter.
- Request administrator privileges when TUN mode requires elevation.
- Stop TUN automatically when the proxy stops.

### Routing Rules

- Built-in routing modes:
  - Global proxy
  - Bypass China
  - Bypass LAN
  - Direct
  - Custom rules
- Custom routing supports:
  - Proxy domains
  - Direct domains
  - Blocked domains
  - Proxy IP/CIDR rules
  - Direct IP/CIDR rules
  - Blocked IP/CIDR rules
- Custom domain rules are normalized to Xray rule syntax where appropriate.

### Traffic and Diagnostics

- Display real-time upload and download speed.
- Persist daily and total traffic counters across app restarts.
- Display subscription remaining traffic when available.
- Store application, startup, crash, Core, and access logs under the user profile.
- Open log directory directly from the sidebar or tray menu.

### Updates and Packaging

- Check GitHub Releases from inside the desktop client.
- Download update installers directly in the client.
- Show download progress, current downloaded size, and package size.
- Build a self-contained Windows x64 publish output.
- Package the application with Inno Setup.

## Requirements

### Runtime

- Windows 10 or Windows 11, x64
- Xray Core runtime in `cores/xray.exe`
- For TUN mode:
  - `cores/sing-box.exe`
  - `cores/wintun.dll`
  - Administrator privileges

### Development

- .NET SDK compatible with `net10.0-windows`
- Windows desktop development workload
- Inno Setup 6 for installer builds

## Installation

Download the latest installer from the GitHub Releases page and run:

```text
Nexora-Setup-<version>.exe
```

The installer deploys the desktop application, bundled assets, runtime files, and optional desktop shortcuts.

## Quick Start

1. Install and launch Nexora.
2. Import nodes from the node import page or subscription import page.
3. Select a node from the node list.
4. Choose a system proxy mode. The default mode is automatic system proxy configuration.
5. Click the proxy switch to start the Core runtime.
6. Wait for the availability check result. If the verification fails, open the log page to inspect the failure reason.
7. Optional: run website connectivity tests from the node test page after the proxy is running.
8. Optional: enable TUN mode if full system traffic routing is required.
9. Optional: sign in from the sidebar if your deployment provides an auth API.

## Subscription Import

Nexora accepts a subscription URL or raw subscription content. Supported input forms include:

- HTTP/HTTPS subscription URL
- Base64 encoded subscription body
- Multi-line share links
- QR code image
- Clipboard QR code image

When a subscription contains multiple nodes, Nexora imports them as a group and shows the group in the node list. The group can be expanded or collapsed, and the node list can be filtered by subscription source.

If the subscription response contains a standard `subscription-userinfo` header, Nexora extracts:

- Uploaded traffic
- Downloaded traffic
- Total traffic
- Remaining traffic

## Routing Modes

| Mode | Description |
| --- | --- |
| Global | Route traffic through the selected proxy node by default. |
| Bypass China | Direct private IP ranges, `geoip:cn`, and `geosite:cn`; proxy the rest. |
| Bypass LAN | Direct private and local network ranges; proxy the rest. |
| Direct | Route all traffic directly. |
| Custom | Apply user-defined proxy, direct, and block rules. |

Custom rules support common Xray domain formats:

```text
example.com
domain:example.com
full:example.com
regexp:^.*\.example\.com$
geosite:category-ads-all
```

IP rules support single IP addresses, CIDR ranges, and GeoIP rules:

```text
1.2.3.4
1.2.3.0/24
geoip:cn
```

## Local Data

Nexora stores user data under:

```text
%APPDATA%\Nexora
```

Common files include:

| File | Purpose |
| --- | --- |
| `settings.json` | User settings, node profiles, selected node, traffic counters. |
| `auth-session.json` | Saved auth session when account login is used. |
| `config.json` | Generated Xray configuration. |
| `sing-box-tun.json` | Generated sing-box TUN configuration. |
| `proxy.pac` | Generated PAC file for PAC system proxy mode. |
| `logs\app.log` | Application log. |
| `logs\startup.log` | Startup diagnostics. |
| `logs\crash.log` | Crash diagnostics. |
| `logs\core-error.log` | Core error log. |
| `logs\access.log` | Core access log. |

## Architecture

```text
Nexora
|-- MainWindow.xaml / MainWindow.xaml.cs     # WPF shell and interaction logic
|-- Dialogs                                  # Node, import, and routing dialogs
|-- Models                                   # App settings, profiles, traffic models
|-- Services
|   |-- CoreConfigBuilder.cs                 # Xray config generation
|   |-- CoreService.cs                       # Core lifecycle management
|   |-- SystemProxyService.cs                # Windows proxy registry integration
|   |-- TunService.cs                        # sing-box TUN lifecycle
|   |-- SubscriptionImportService.cs         # Subscription and node import
|   |-- TrafficStatsService.cs               # Core stats query
|   |-- LatencyTestService.cs                # Node latency checks
|   |-- WebsiteConnectivityTestService.cs    # Website connectivity checks
|   |-- StartupService.cs                    # Windows startup registry integration
|   |-- AuthService.cs                       # Account login and session storage
|   |-- NodeRegionHelper.cs                  # Region label formatting
|   `-- DiagnosticLogService.cs              # App diagnostics and log paths
|-- assets                                   # Application icons and sidebar icons
|-- cores                                    # Bundled proxy runtime files
|-- installer                                # Inno Setup script
`-- docs                                     # User-facing documentation
```

The C# root namespace remains `NaiwaProxy` for source compatibility, while the published executable and installer use the Nexora product name.

## Development

Restore and build:

```powershell
dotnet build
```

Run locally:

```powershell
dotnet run
```

Publish a self-contained Windows x64 build:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\publish\win-x64
```

Build the installer with Inno Setup:

```powershell
iscc .\installer\NaiwaProxy.iss
```

The installer is written to:

```text
dist\
```

## Runtime Files

The `cores` directory is copied to the publish output and installer package.

Expected files:

```text
cores/
|-- xray.exe
|-- geoip.dat
|-- geosite.dat
|-- sing-box.exe
|-- wintun.dll
`-- libcronet.dll
```

`xray.exe` is required for proxy operation. `sing-box.exe` and `wintun.dll` are required for TUN mode.

## Troubleshooting

### The update check fails with `release-assets.githubusercontent.com:443`

This usually means the current network cannot connect to GitHub Release asset hosts directly. Start the proxy first, confirm that the proxy verification succeeds, and then run the update check again. Nexora will use the local proxy for update checks when the Core runtime is already running.

### TUN mode requires administrator privileges

TUN mode needs elevated permissions to create and manage a virtual network adapter. When the app is not running as administrator, Nexora requests elevation and relaunches itself.

### The proxy switch is on but the network is unavailable

Nexora starts the Core process first, applies the selected system proxy mode, and then verifies access to `www.google.com`. If the verification fails, check:

- Whether the selected node is valid.
- Whether the node can be reached from the current network.
- Whether Windows system proxy settings were modified by another application.
- Whether the generated Core configuration is valid.
- The logs under `%APPDATA%\Nexora\logs`.

### Traffic remains at zero

Traffic counters are based on Core statistics. Make sure the Core runtime is running and that traffic is actually passing through the local HTTP/SOCKS inbound or TUN path.

### Website connectivity tests fail while the proxy appears healthy

Website tests run through the local HTTP inbound. Confirm that the proxy is running, the selected node is reachable, and the target site is not blocked by the current routing mode.

## Security and Privacy

- Node profiles and settings are stored locally.
- Nexora changes Windows proxy settings only for the current user.
- Auth sessions are stored locally when account login is enabled.
- Logs may contain runtime errors, endpoint information, and diagnostic details.
- Do not publish logs without reviewing sensitive information first.
- Use only nodes and subscriptions that you are authorized to access.

## Roadmap

Planned areas include:

- More complete subscription management.
- More routing rule templates.
- Additional protocol-specific configuration fields.
- Core update management.
- Backup and restore.
- Dark theme and localization.
- Extended diagnostics and log viewer.

## License

Project license information is not finalized in this repository yet.

Third-party components may use their own licenses:

- Xray Core: MPL-2.0
- sing-box: GPL-3.0-or-later
- Wintun: GPL-2.0
- Inno Setup: Inno Setup License

Please review the upstream licenses before redistributing packaged builds.
