# Orayo

<table>
  <tr>
    <td valign="middle">
      <img alt="Orayo icon" src="./docs/assets/banner-icon.png" height="100">
    </td>
    <td valign="middle">
      <picture>
        <source media="(prefers-color-scheme: dark)" srcset="./docs/assets/banner-name-dark.svg">
        <source media="(prefers-color-scheme: light)" srcset="./docs/assets/banner-name-light.svg">
        <img alt="Orayo" src="./docs/assets/banner-name-light.svg" height="64">
      </picture>
    </td>
  </tr>
</table>

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
	<td><img src="docs/assets/screenshot-1.png" /></td>
	<td><img src="docs/assets/screenshot-2.png" /></td>
  </tr>
  <tr>
	<td><img src="docs/assets/screenshot-3.png" /></td>
	<td><img src="docs/assets/screenshot-4.png" /></td>
  </tr>
</table>

## Build

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
