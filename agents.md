# DesktopBoxes — Agents Guide

This file describes how autonomous agents (and contributors) should work in this repository.
Follow it to keep the architecture clean and the build green.

## Golden rules
- **Never** put WPF, Win32 P/Invoke, or Shell COM code in the `Core` project folders.
  Core may only define models, interfaces, and pure-.NET service implementations.
- **Never** scatter `DllImport` / P/Invoke declarations outside `Win32/NativeMethods`.
  Add new native APIs to `Win32/NativeMethods/NativeMethods.txt`; CsWin32 generates them
  into `Win32/NativeMethods/Win32Apis`. Call them only from `Win32/Services`.
- **Never** create a Window or native handle per `BoxItem`. A Box is one control/window
  holding many items.
- Use the term **Box** everywhere (code, comments, UI). Do **not** use "Fence".
- Keep the NuGet surface small. Don't add packages without a reason. Currently allowed:
  `Microsoft.Extensions.DependencyInjection` and `Microsoft.Windows.CsWin32`.
  `Wpf.Ui` (v3.4.2.7) is approved too — the user explicitly asked for it to provide the modern
  window chrome and context-menu styling. Reference its resource dictionaries via pack URIs
  (`/Wpf.Ui;component/wpfui.xaml`, `/Wpf.Ui;component/themes/theme.xaml`,
  `/Wpf.Ui;component/themes/brushes.xaml`); this build predates the `ThemesDictionary`/
  `ControlsDictionary` markup extensions, so do not use those. The window type is `WPF.UI.WPFUIWindow`.
- Build must stay warning-light and succeed in both Debug and Release.

## Layer responsibilities
- **Core/Models** — add domain types here. Keep them simple and extensible.
- **Core/Interfaces** — define contracts, including platform abstractions. Use Core geometry
  types (`RectD`, `PointD`, `SizeD`) instead of WPF/Win32 coordinate types.
- **Core/Services** — only platform-independent implementations (no IO to OS specifics).
- **Shell/** — Windows Shell behavior. Put Shell COM native interop in `Shell/Interop`.
- **Win32/** — raw Win32. `NativeMethods` for declarations, `Services` for the callers.
- **Views / Controls / Converters / Resources / App.xaml** — WPF only.

## Dependency injection
- All services are registered in `App.xaml.cs` (`ConfigureServices`). Register new services
  there behind their Core interface. Prefer `AddSingleton` for stateless platform services and
  `AddTransient`/`AddSingleton` for view models as appropriate.
- View models get their dependencies via constructor injection resolved from `App.Services`.

## Adding a new native API (example)
 1. Add the API name to `Win32/NativeMethods/NativeMethods.txt` (CsWin32 only auto-discovers this
    file at the project root, so it is also registered as an `AdditionalFiles` item in the csproj).
 2. Leave the rest to CsWin32: it generates the real P/Invoke into its own `Windows.Win32.PInvoke`
    class. The thin, named wrapper `Win32/NativeMethods/Win32Apis.cs` re-exposes exactly the APIs
    we use, so every native call in the codebase goes through `Win32Apis` and never `PInvoke` directly.
 3. Use `Win32Apis` from a class in `Win32/Services`, converting to/from Core geometry types
    (`RectD`, `PointD`, `SizeD`). Do NOT take `Win32Apis` members' addresses or use `void*` handles
    (e.g. prefer the implicit `HWND`→`IntPtr` conversion over `.Value`).
 4. Win32 service classes that call these APIs are marked
    `[SupportedOSPlatform("windows10.0.14393")]` so platform-API analyzers (CA1416) stay quiet;
    keep that attribute in sync when you add newer-API usage. The project suppresses CA1416 globally
    (it is a Windows-only app), but the attribute is still good documentation.
 5. Expose behavior through a Core interface; inject the implementation in `App.xaml.cs`.

### When CsWin32 won't emit an API (WPF AnyCPU wpftmp trap)
 The WPF build compiles our code twice: once in the real project and once in a generated
 `DesktopBoxesUI_*` "temporary target assembly" (`wpftmp`) project that is ALWAYS `AnyCPU`.
 CsWin32 refuses to generate **architecture-specific** APIs under AnyCPU (e.g. `SHGetFileInfo`,
 `SetWinEventHook` → `PInvoke005`/silently skipped), so those symbols are missing when the `wpftmp`
 compiles `Win32Apis.cs`. Symptoms: build errors only about the new types, not the older APIs.
 Do NOT "fix" this by setting `<Platform>/<PlatformTarget>` to x64 — that makes CsWin32 emit nothing
 at all for this project. Instead:
   - Keep CsWin32 for every normal API (leave it in `NativeMethods.txt`).
   - For the architecture-specific few, declare them manually in
     `Win32/NativeMethods/ManualApis.cs` via `[DllImport]` (NOT `LibraryImport` — the source
     generator can't marshal structs like `SHFILEINFOW`). Re-expose them through `Win32Apis` so all
     native calls still flow through the one wrapper. This keeps raw P/Invoke inside `Win32/NativeMethods`.

## Verifying changes
After editing:
1. `dotnet restore`
2. `dotnet build` (fix all errors; avoid introducing warnings)
3. If UI changed, run the app and confirm the WPF window opens and "New Box" works.

## Performance expectations
- The app runs continuously. Avoid polling, timers, and excessive `Dispatcher` usage.
- Use event-driven APIs (monitor/DPI/Explorer/filesystem notifications) wherever possible.
- Cache icons and load them lazily. Don't retain Shell/COM objects longer than needed.
- Avoid per-frame or per-item allocations in hot paths.
