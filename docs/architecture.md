# Architecture

Orayo has a Windows desktop application and a platform-independent `Orayo.Core` library. The core project explicitly links the shared source files so their existing repository paths remain stable. It has no WinUI or Windows App SDK dependency.

## Responsibilities

| Area | Responsibility |
| --- | --- |
| `Models/` | Serializable node configuration, settings, and persisted runtime state. No brushes, visibility values, or active-node display state. |
| `Application/` | `AppSession` shares settings and state; `ServerCatalog` owns imports and node edits; `IAppStore` defines persistence. |
| `Infrastructure/Storage/` | `AppPaths` chooses locations; `AppStore` maps documents to files; `JsonFileStore` serializes writes, uses temporary files, and recovers valid backups. |
| `ViewModels/` | `ServerItemViewModel` wraps a node with active and latency display state. |
| `Services/` | Share-link parsing, configuration building, DNS/routing presets, runtime process management, TUN IPC, and update operations. |
| `MainWindow`, `Views/` | Window lifetime, dialogs, clipboard access, UI events, and connection interactions. |

`Application/AppServices.cs` is the Windows composition root. It creates one shared session/store and explicitly configures runtime and update services. Windows receive these dependencies rather than creating independent stores. `AppSession.LoadAsync` runs once at startup, then the UI culture is selected and presets are normalized before constructing the windows.

Node mutations are serialized by `ServerCatalog`. A changed list is published only after it has been saved, preventing failed or overlapping writes from publishing unsaved nodes. Persisted node IDs remain stable when editing.

Window interaction logic remains in code-behind. This is an incremental separation of concerns, not a claim that the whole application uses MVVM. Future connection view models can be extracted independently of storage and node management.

## Storage policy

The composition root reads the existing Velopack locator after `VelopackApp.Build().Run()`. A portable package uses `<RootAppDir>/data`; an installed or development build uses `%LocalAppData%/Orayo`. Portable data lives outside `current` so replacing application files does not replace configuration. No automatic migration from AppData is performed; both READMEs describe copying existing settings.

`AppPaths` also supplies the normal-mode Xray configuration, crash log, pending-update directory, and update staging directory. Pending core updates reference their file within the selected data directory, so relocating a portable package does not invalidate its update manifest. The TUN helper keeps its session-specific configuration in the system temporary directory and deletes it on stop.

Velopack owns application update packages and their cache lifetime; the Xray updater does not delete that cache. Core and Geo update files are managed by `CoreUpdateService`, while `AppUpdateService` creates the application update manager and its proxy-aware downloader.

`JsonFileStore` coordinates file operations through the shared store instance, writes a temporary file before replacing the primary, and preserves the previous valid document as `.bak`. A corrupt primary cannot overwrite a valid backup. Subscription metadata is neither read nor deleted.

## Validation

Run `dotnet test tests/Orayo.Core.Tests/Orayo.Core.Tests.csproj -c Release` on any supported .NET 10 host. Tests cover portable relocation, installed-path compatibility, corrupt-file recovery, legacy JSON compatibility, overlapping node edits, failed saves, language resources, configuration generation, and portable pending updates.

The pending-update test compiles the production `CoreUpdateService.cs` directly because the Windows app cannot be referenced from the platform-independent test project. The Windows build workflow runs core tests before compiling the WinUI application. The release workflow also runs the tests before packaging. UI/XAML compilation, TUN authorization, and packaged update behavior require Windows validation.
