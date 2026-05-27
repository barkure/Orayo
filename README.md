[English](README.md) | [简体中文](README.zh-Hans.md)

# Orayo

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="./docs/assets/banner-dark.webp">
  <source media="(prefers-color-scheme: light)" srcset="./docs/assets/banner-light.webp">
  <img alt="Orayo banner" src="./docs/assets/banner-light.webp" width="250">
</picture>

Orayo is a modern Windows Xray client built with WinUI 3.

## Features

- Xray-core integration
- Node list with import, add, edit, delete, and share
- TUN mode and system proxy
- Routing and DNS settings
- Geo data file updates

## Screenshots
<table>
  <tr>
	<td><img src="docs/assets/screenshot-en-01.png" /></td>
	<td><img src="docs/assets/screenshot-en-02.png" /></td>
  </tr>
  <tr>
	<td><img src="docs/assets/screenshot-en-03.png" /></td>
	<td><img src="docs/assets/screenshot-en-04.png" /></td>
  </tr>
</table>

## Installation

### WinGet

```bash
winget install barkure.Orayo
```

### Release

[Latest Release](https://github.com/barkure/Orayo/releases/latest): Setup (requires installation) and Portable (run directly, no installation needed).

## Build Instructions

Requires .NET 8 SDK and Windows 10 1809 or later. Windows 10 2004 or later is recommended.

```bash
dotnet build -c Release
```

## Open Source Projects Used

- [Xray-core](https://github.com/XTLS/Xray-core)
- [Wintun](https://www.wintun.net/)
- [Loyalsoldier/v2ray-rules-dat](https://github.com/Loyalsoldier/v2ray-rules-dat)
- [Monaco Editor](https://github.com/microsoft/monaco-editor)

## License

GPL-3.0
