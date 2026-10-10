# Architecture

Orayo has a Windows desktop application and a platform-independent `Orayo.Core` library. Each project owns its source files under `src/`; the desktop project references the core library through a project reference. The core library has no WinUI or Windows App SDK dependency.

## Repository layout

```text
src/
  Orayo/                 WinUI application, Windows services, bundled assets
  Orayo.Core/            Models, application logic, storage, protocols, resources
tests/
  Orayo.Core.Tests/      Core behavior tests
docs/                    Architecture and documentation assets
.github/workflows/       Build and release automation
Orayo.slnx               Solution entry point
```

Build the Windows application from the repository root with `dotnet build src/Orayo/Orayo.csproj -c Release -r win-x64`. Runtime assets still deploy to `Assets/` beside the executable; relocating source files does not change configuration or portable data locations. Generated output belongs in ignored `bin/`, `obj/`, and `artifacts/` directories.

## Responsibilities

| Area | Responsibility |
| --- | --- |
| `src/Orayo.Core/Models/` | Serializable node configuration, settings, and persisted runtime state. No brushes, visibility values, or active-node display state. |
| `src/Orayo.Core/Application/` | `AppSession` shares settings and state; `ServerCatalog` owns imports and node edits; `IAppStore` defines persistence. |
| `src/Orayo.Core/Infrastructure/Storage/` | `AppPaths` chooses locations; `AppStore` maps documents to files; `JsonFileStore` serializes writes, uses temporary files, and recovers valid backups. |
| `src/Orayo/ViewModels/` | `ServerItemViewModel` wraps a node with active and latency display state. |
| `src/Orayo.Core/Services/` | Share-link parsing, configuration building, DNS/routing presets, and latency probes. |
| `src/Orayo/Services/` | Windows integration, runtime process management, TUN IPC, and update operations. |
| `src/Orayo/MainWindow`, `src/Orayo/Views/` | Window lifetime, dialogs, clipboard access, UI events, and connection interactions. |

`src/Orayo/Application/AppServices.cs` is the Windows composition root. It creates one shared session/store and explicitly configures runtime and update services. Windows receive these dependencies rather than creating independent stores. `AppSession.LoadAsync` runs once at startup, then the UI culture is selected and presets are normalized before constructing the windows.

Node mutations are serialized by `ServerCatalog`. A changed list is published only after it has been saved, preventing failed or overlapping writes from publishing unsaved nodes. Persisted node IDs remain stable when editing.

Window interaction logic remains in code-behind. This is an incremental separation of concerns, not a claim that the whole application uses MVVM. Future connection view models can be extracted independently of storage and node management.

## Storage policy

The composition root reads the existing Velopack locator after `VelopackApp.Build().Run()`. A portable package uses `<RootAppDir>/data`; an installed or development build uses `%LocalAppData%/Orayo`. Portable data lives outside `current` so replacing application files does not replace configuration. No automatic migration from AppData is performed. To reuse settings from an older Portable release, exit Orayo and copy `servers.json`, `settings.json`, `runtime_state.json`, and their `.bak` files from `%LocalAppData%/Orayo` into the portable package root's `data` directory before launching it.

`AppPaths` also supplies the normal-mode Xray configuration, crash log, pending-update directory, and update staging directory. Pending core updates reference their file within the selected data directory, so relocating a portable package does not invalidate its update manifest. The TUN helper keeps its session-specific configuration in the system temporary directory and deletes it on stop.

Velopack owns application update packages and their cache lifetime; the Xray updater does not delete that cache. Core and Geo update files are managed by `CoreUpdateService`, while `AppUpdateService` creates the application update manager and its proxy-aware downloader.

`JsonFileStore` coordinates file operations through the shared store instance, writes a temporary file before replacing the primary, and preserves the previous valid document as `.bak`. A corrupt primary cannot overwrite a valid backup. Subscription metadata is neither read nor deleted.

## Validation

Run `dotnet test tests/Orayo.Core.Tests/Orayo.Core.Tests.csproj -c Release` on any supported .NET 10 host. Tests cover portable relocation, installed-path compatibility, corrupt-file recovery, legacy JSON compatibility, overlapping node edits, failed saves, language resources, configuration generation, portable pending updates, and share-link round trips for all five supported protocols. Share-link tests include Unicode names, escaped credentials and paths, IPv6, REALITY fields, and invalid or unsupported links.

The test project compiles the production `src/Orayo/Services/CoreUpdateService.cs` and `src/Orayo/Services/XrayService.cs` directly because the Windows app cannot be referenced from the platform-independent test project. On Windows, it copies the bundled Xray executable and runs lifecycle integration tests against real loopback listeners: start/stop, restart, shutdown, persistent versus ephemeral configuration, and recovery after invalid configuration or missing inbounds. These tests need no administrator privileges or external server; they are explicitly skipped on other operating systems.

The Windows build workflow runs the tests before compiling the WinUI application. The release workflow also runs the tests before packaging. UI/XAML compilation, TUN authorization, and packaged update behavior require separate Windows validation.

The root `.editorconfig` defines indentation, encoding, line endings, and C# formatting. Bundled third-party assets keep their upstream formatting, and generated designer files are identified as generated code. Existing source files are not reformatted as part of directory maintenance.
