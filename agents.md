# DesktopBoxes — Agents Guide

This file describes how autonomous agents (and contributors) should work in this repository.
Follow it to keep the architecture clean and the build green.

## Golden rules
- **Never** put WPF, Win32 P/Invoke, or Shell COM code in the `Core` project folders.
  Core may only define models, interfaces, and pure-.NET service implementations.
- **Never** scatter `DllImport` / P/Invoke declarations outside `Win32APIs/NativeMethods`.
  Add new native APIs to `Win32APIs/NativeMethods/NativeMethods.txt`; CsWin32 generates them
  into `Win32APIs/NativeMethods/Win32Apis`. Call them only from `Win32APIs/Services`.
- **Never** create a Window or native handle per `BoxItem`. A Box is one control/window
  holding many items.
- Use the term **Box** everywhere (code, comments, UI). Do **not** use "Fence".
- Keep the NuGet surface small. Don't add packages without a reason. Currently allowed:
  `Microsoft.Extensions.DependencyInjection`, `Microsoft.Windows.CsWin32`,
  `CommunityToolkit.Mvvm`, `Wpf.Ui` (v4.3.0) and `Wpf.Ui.Tray` (v4.3.0) — the user explicitly asked for
  them to provide the modern window chrome, view-model helpers and tray styling. Reference WPF-UI resource
  dictionaries via `ui:ThemesDictionary` / `ui:ControlsDictionary` (`App.xaml` already does); legacy pack URIs
  (`/Wpf.Ui;component/...`) are obsolete. The window type is `Wpf.Ui.Controls.FluentWindow`.
- Build must stay warning-light and succeed in both Debug and Release.

## Layer responsibilities
- **Core/Models** — add domain types here. Keep them simple and extensible.
- **Core/Interfaces** — define contracts, including platform abstractions. Use Core geometry
  types (`RectD`, `PointD`, `SizeD`) instead of WPF/Win32 coordinate types.
- **Core/Services** — only platform-independent implementations (no IO to OS specifics).
- **Shell/** — Windows Shell behavior. Put Shell COM native interop in `Shell/Interop`.
- **Win32APIs/** — raw Win32. `NativeMethods` for declarations, `Services` for the callers.
- **Views / Controls / Converters / Resources / App.xaml** — WPF only (ViewModels use `CommunityToolkit.Mvvm` source generators: `[ObservableProperty]`, `[RelayCommand]`).

## Dependency injection
- All services are registered in `App.xaml.cs` (`ConfigureServices`). Register new services
  there behind their Core interface. Prefer `AddSingleton` for stateless platform services and
  `AddTransient`/`AddSingleton` for view models as appropriate.
- View models get their dependencies via constructor injection resolved from `App.Services`.

## Adding a new native API (example)
 1. Add the API name to `Win32APIs/NativeMethods/NativeMethods.txt` (CsWin32 only auto-discovers this
    file at the project root, so it is also registered as an `AdditionalFiles` item in the csproj).
 2. Leave the rest to CsWin32: it generates the real P/Invoke into its own `Windows.Win32.PInvoke`
    class. The thin, named wrapper `Win32APIs/NativeMethods/Win32Apis.cs` re-exposes exactly the APIs
    we use, so every native call in the codebase goes through `Win32Apis` and never `PInvoke` directly.
 3. Use `Win32Apis` from a class in `Win32APIs/Services`, converting to/from Core geometry types
    (`RectD`, `PointD`, `SizeD`). Do NOT take `Win32Apis` members' addresses or use `void*` handles
    (e.g. prefer the implicit `HWND`→`IntPtr` conversion over `.Value`).
 4. Win32APIs service classes that call these APIs are marked
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
     `Win32APIs/NativeMethods/ManualApis.cs` via `[DllImport]` (NOT `LibraryImport` — the source
     generator can't marshal structs like `SHFILEINFOW`). Re-expose them through `Win32Apis` so all
     native calls still flow through the one wrapper. This keeps raw P/Invoke inside `Win32APIs/NativeMethods`.

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

## Feature flags & desktop input detection
There are two implementations of "detect a double-click on empty desktop" (the trigger for
**Temp Hide All boxes**). The active one is selected by `GlobalFeaturesSwitches.UseGlobalMouseHookInsteadOfCustomSurface`
(`bool?`, defined in `GlobalFeaturesSwitches.cs`). **It is currently `true`** — the global mouse hook is the
supported path; the custom `DesktopSurface` is experimental and has unresolved issues (see Known issues).

- **Global low-level hook (`true`)** — `DesktopManager` calls `_mouseMonitor.Start()` which spins up
  `MouseMonitor` (`Win32APIs/Services/MouseMonitor.cs`), a `WH_MOUSE_LL` hook on a **dedicated STA background
  thread with its own `Dispatcher` message pump** (so a busy UI thread never delays input to other apps).
  It raises `MouseButtonDown` (the `HWND` under the cursor) and, after its own double-click logic,
  `DesktopDoubleClick`. Double-click confirmation uses `Win32Apis.IsDesktopChild` (descendant of
  `Progman`/`WorkerW`) + `Win32Apis.IsBoxWindow` (our own registered boxes, via `RegisterBoxWindow`/
  `UnregisterBoxWindow`) + `Win32Apis.IsDesktopEmptyPoint` (an `LVM_HITTEST` that confirms the point is
  not on an icon). `DesktopDoubleClick` is wired in `App.xaml.cs` to `DesktopManager.ToggleHideAllBoxes()`.
  **This path only observes input; it never captures or swallows it, so it does not interfere with other
  apps/games.** Prefer this path.
- **Custom surface (`false`)** — `DesktopManager.EnsureSurface()` shows `Views/DesktopSurface.xaml`, a
  top-level layered WPF window glued to the desktop root that forwards mouse input to the Explorer
  list-view and detects the double-click itself (`WM_NCHITTEST` + two `WM_LBUTTONDOWN`s). See Known issues
  before touching this path.

`ToggleHideAllBoxes` (in `DesktopManager`) toggles `HideAllBoxes`/`ShowAllBoxes`; hides wrap each window
in `Win32Apis.AllowHide` so `MinimizePreventionHook` doesn't re-show it, and the surface/tray stay visible
so the toggle is reversible. This is **session-only** (not persisted).

`GlobalFeaturesSwitches.ShowDebugTree` (currently `false`) enables `DesktopTreeDebugOverlay` — a diagnostic
that draws, at the cursor, the hit-test result, z-order, and the surface's style/ex-style. Use it (and the
`DesktopSurface`'s published `Win32Apis.DesktopSurfaceHandle`) when debugging desktop/surface layering.

## Known issues
- **Custom `DesktopSurface` (flag `false`) is a work-in-progress and currently broken.** Recurring,
  hard-to-fix problems from past attempts:
  - A fully transparent (`AllowsTransparency`, alpha `0`) WPF window is skipped by `WindowFromPoint`, so it
    is never the hit target. The background must use a **non-zero alpha** (`#01000000`) to be hit-testable
    while visually invisible.
  - A `WS_EX_LAYERED` **child** window is always painted *behind* its non-layered siblings (the Explorer
    list-view), so the surface must stay a **top-level** window — reparenting it to `Progman`/`WorkerW`
    via `SetParent` does not work.
  - The surface keeps getting raised **above application windows** on click/activation. Mitigations tried:
    `WM_WINDOWPOSCHANGING` z-order override (root must be resolved via `Win32Apis.GetDesktopRootHandle`,
    which must never return `0` or glue silently no-ops), `WS_EX_NOACTIVATE`, and `WM_MOUSEACTIVATE` →
    `MA_NOACTIVATE` (which **broke OLE drag/drop** because it suppressed the activation the drop target
    needs; gating it on `!_dragging` was the next attempt). **Bottom line: keep the global hook enabled;
    do not invest more in the surface unless the user explicitly asks.**
  - WPF windows are created without `CS_DBLCLKS`, so `WM_LBUTTONDBLCLK` is never delivered; double-click is
    detected manually from two `WM_LBUTTONDOWN`s.
- **`BoxContainerWindow` custom tab-drag** (moving a box tab between containers) has two unfixed bugs:
  1. A dropped tab is not visible after drop when the target container is on a different monitor / a
     different `BoxContainerWindow` than the source.
  2. The drag crosshair / drag-image is offset on **multi-DPI** setups because `GetScreenDragPoint()`
     (around `BoxContainerWindow.xaml.cs:824`) uses `PointToScreen` instead of `GetCursorPos` combined with
     per-monitor DPI. `GetDpiForMonitor` + `MonitorFromPoint` are already declared in `NativeMethods.txt`
     but are not yet wrapped in `Win32Apis`.
- **`WindowExceptionHandler` unhandled-exception flow:** The `DispatcherUnhandledException` / `AppDomain` /
  `TaskScheduler` handlers are wired in `App.xaml.cs:OnStartup` (`#if !DEBUG` gate). The dialog is a
  `Wpf.Ui.Controls.FluentWindow` (`Views/WindowExceptionHandler.xaml`) bound to
  `WindowExceptionHandlerViewModel` (`CommunityToolkit.Mvvm`: `[ObservableProperty]`, `[RelayCommand]`).
  It offers **Continue** (keep app alive, `HasChosenContinue=true`, no shutdown) and **Exit Application**
  (`Danger`), plus **Copy details**; only **Exit Application** shuts down — closing via the X button,
  Alt+F4 or any system close just dismisses the dialog (treated as Continue). If you add new global
  exception sources, keep the `HasChosenContinue` / `RequestClose(bool)` contract intact.
- **WPF transparent hit-test & `WebView2` airspace:** A fully transparent (`AllowsTransparency`, `Background="#00000000"` / `Transparent`) WPF window/control is **skipped by hit-testing** (`WindowFromPoint`, `IsMouseOver` never becomes true). Every transparent `CssWidget` / `DesktopSurface` must use a **non-zero alpha** background (`#01000000` — alpha `1/255`, visually invisible) on the hit-test root (`CssWidgetWindow:RootBorder`, `CssWidgetControl` inner `Grid` may stay `Transparent` because `WebView2` HWND itself is hit-testable, but the window chrome around it is not, so `HeaderBorder` hover requires the root to be `#01000000`). `WebView2` is an `HwndHost` with **airspace**: it sits above WPF visuals, steals mouse/keyboard, and prevents WPF `IsMouseOver` / `IsKeyboardFocusWithin` from firing inside the web content. Do **not** poll with a `DispatcherTimer` + `GetCursorPos`/`GetWindowRect` — use the `WebView2` bridge: inject `document.addEventListener('mouseenter'/'mouseleave'/'click', ()=>chrome.webview.postMessage(...))` via `AddScriptToExecuteOnDocumentCreatedAsync`, handle `WebMessageReceived` in `CssWidgetControl` (`WidgetMouseEnter/Leave/Clicked` events), and forward to `CssWidgetWindow` (`HwndHook` `WM_MOUSEACTIVATE→MA_ACTIVATE` + `Activated/Deactivated`). `WindowDragController` hit-test already respects `ResizeMode` for non-resizable widgets.
