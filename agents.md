# DesktopBoxes — Agents Guide

This file describes how autonomous agents (and contributors) should work in this repository.
Follow it to keep the architecture clean and the build green.

## Golden rules

- **Never** use scripts to edit project files, use only the Edit Tool
- **Never** put WPF, Win32 P/Invoke, or Shell COM code in the `Core` project folders.
  Core may only define models, interfaces, and pure-.NET service implementations.
- **Never** scatter `DllImport` / P/Invoke declarations outside the shared `WindowsNative`
  project. Add new native APIs there (hand-rolled `[DllImport]`, never `LibraryImport` for structs
  the source generator can't marshal, and never CsWin32 — the repo does not use it). Call them
  from `Win32APIs/Services` (UI) or directly where the engine already does, converting to Core
  geometry types (`RectD`, `PointD`, `SizeD`) at the boundary.
- **Never** create a Window or native handle per `BoxItem`. A Box is one control/window
  holding many items.
- Use **IDispatcher** **Helpers/WpfDispatcher** when needed. IDispatcher registered in App.Services
- Use the term **Box** everywhere (code, comments, UI). Do **not** use "Fence".
- With CommunityToolkit.Mvvm, prefer `public partial` properties over private backing fields
  for `[ObservableProperty]` wherever the generator supports it (it does since 8.4):
  `[ObservableProperty] public partial string Name { get; set; }` instead of
  `[ObservableProperty] private string _name;`.
- `DesktopBoxesUI/app.manifest` declares PerMonitorV2 DPI awareness — do not remove it, or popups
  and geometry misbehave after display scale changes. Do not add a `compatibility` section to it
  either: SxS activation fails on this machine when one is present (verified by bisecting). Container rescaling
  keys off `DesktopResolution` (DIP work area) only — rescaling by the DIP ratio preserves both physical
  size and relative layout across DPI changes, so no physical-pixel special-casing.
- Widgets, containers and the desktop surface live on the **primary display only** — there is no
  multi-monitor positioning support (for now). Rescaling keys off the primary work area alone.
- Keep the NuGet surface small. Don't add packages without a reason. Currently allowed:
  `Microsoft.Extensions.DependencyInjection`,
  `CommunityToolkit.Mvvm`, `Wpf.Ui` (v4.3.0) and `Wpf.Ui.Tray` (v4.3.0) — the user explicitly asked for
  them to provide the modern window chrome, view-model helpers and tray styling. Reference WPF-UI resource
  dictionaries via `ui:ThemesDictionary` / `ui:ControlsDictionary` (`App.xaml` already does); legacy pack URIs
  (`/Wpf.Ui;component/...`) are obsolete. The window type is `Wpf.Ui.Controls.FluentWindow`.
- For WebView2 init use provided userData folder directly as the engine will create the "EBWebView" shared env folder in it
- Build must stay warning-light and succeed.

## Layer responsibilities

- **Core/Models** — add domain types here. Keep them simple and extensible.
- **Core/Interfaces** — define contracts, including platform abstractions. Use Core geometry
  types (`RectD`, `PointD`, `SizeD`) instead of WPF/Win32 coordinate types.
- **Core/Services** — only platform-independent implementations (no IO to OS specifics).
- **Shell/** — Windows Shell behavior. Shell COM native interop itself lives in `WindowsNative`
  (`Shell32`/`ShellCom`); `Shell/` keeps only the calling services.
- **Win32APIs/** — raw Win32 callers. `NativeMethods/Win32Apis.cs` is the UI's thin wrapper over
  `WindowsNative`; `Services` holds the callers. All declarations live in `WindowsNative`.
  Desktop-band glue (ownership, tool-window/minimize styles, activation and z-order pins) lives
  in `Win32APIs/Services/DesktopLayer.cs` — attach new desktop windows through
  `DesktopLayer.Attach` instead of copying the sequence.
- **Views / Controls / Converters / Resources / WPFServices/ AttachedProperties / Animation / App.xaml** — WPF only (ViewModels use `CommunityToolkit.Mvvm` source generators: `[ObservableProperty]`, `[RelayCommand]`).
  - **Views/DebugViews/** Wpf debugging overlays
  - **Views/AppWindows/** wpf main windows like settings and about etc
  - **Views/Containers/** windows/containers that hosts app widgets/controls that is placed in the surface window
  - **Views/HelpersViews/** for overlays, ghost windows etc that is not part of main user direct interactive windows
- **Helpers/** static or sealed (shared) helper classes can have WPF types
- **WPFServices/** Services that access WPF types directly (like ImageSource)
- **WebWidgets/** for built in app widgets
- **DesktopBoxes.WidgetSdk/** (`src/`) — plugin contracts only (`INativeWidget`, base control,
  manifest, folder-kind helper). BCL + WPF framework, no packages. Plugin authors reference
  the built DLL; never move host logic here.
- **Core/Services/NativeWidgetService** — native widget discovery/compile/load (Roslyn +
  collectible ALC, hashed cache). `UserWidgets/<slug>` folders are shared with CSS widgets;
  `WidgetFolder.PeekKind` (WidgetSdk) decides ownership without loading code — keep it so.
- **Views/Containers/NativeWidgetWindow** — native plugin host; shares `WidgetChromeOverlay`
  via `IWidgetChromeOwner` (implement the interface for new chrome owners, don't fork the overlay).
- **NativeWidgets/** (`NativeClock`, `CalendarWidget`) — built-in plugins; `NativeClock` doubles as
  the reference implementation (settings, suspend/resume, theming).
- **docs/** — user-facing guides (`native-widgets.md` = plugin authoring reference).
- **AttachedProperties/** wpf Attached Properties
- **Animations/** wpf Animations

## Dependency injection

- All services are registered in `App.xaml.cs` (`ConfigureServices`). Register new services
  there behind their Core interface. Prefer `AddSingleton` for stateless platform services and
  `AddTransient`/`AddSingleton` for view models as appropriate.
- View models get their dependencies via constructor injection resolved from `App.Services`.

## Adding a new native API (example)

 1. Declare it by hand in the shared `WindowsNative` project (`User32`/`Kernel32`/`Shell32`/… by
    owning DLL; structs in `NativeTypes.cs`, constants in `Win32Constants.cs`, callbacks in
    `NativeDelegates.cs`, desktop-layer core in `Desktop/DesktopLayerHostBase.cs`). Use `[DllImport]`
    (NOT `LibraryImport` — the source generator can't marshal structs like `SHFILEINFOW`), plain
    `IntPtr` handles, and `WindowsNative` geometry types (`RECT`, `POINT`, `SIZE`). `WindowsNative`
    takes no dependencies except Serilog (layer lifecycle logging only).
 2. The thin, named wrapper `Win32APIs/NativeMethods/Win32Apis.cs` re-exposes exactly the APIs
    the UI uses, so every native call in the codebase goes through `Win32Apis` and never P/Invoke
    directly. The engine calls `WindowsNative` directly.
 3. Use `Win32Apis` from a class in `Win32APIs/Services`, converting to/from Core geometry types
    (`RectD`, `PointD`, `SizeD`). Do NOT take native addresses or use `void*` handles.
 4. Win32APIs service classes that call these APIs are marked
    `[SupportedOSPlatform("windows10.0.14393")]` so platform-API analyzers (CA1416) stay quiet;
    keep that attribute in sync when you add newer-API usage. The project suppresses CA1416 globally
    (it is a Windows-only app), but the attribute is still good documentation.
 5. Expose behavior through a Core interface; inject the implementation in `App.xaml.cs`.
 6. Reference `WindowsNative` from the consuming `.csproj` (`DesktopBoxesUI`,
    `DesktopLiveWallPaperEngine` and `WPFShared` already do).

## Verifying changes

After editing:

1. `dotnet restore`
2. `dotnet build` (fix all errors; avoid introducing warnings)
3. If UI changed, run the app and confirm the WPF window opens and "New Box" works.

## AI working files

- Keep all agent-local todos, notes, plans and scratch files under the repo-root `.ai/` directory (e.g. `.ai/todo.md`). Never scatter them across the repo.
- `.ai/` is git-ignored — it never gets committed. Do not force-add it.
- `.ai/todo.md` is a forward-looking list only: open todos, bug/survey tickets, verification steps.
  Mark finished items `[x]` with a one-line how — never log completed-work narratives or round
  histories in it.

## Performance expectations

- The app runs continuously. Avoid polling, timers, and excessive `Dispatcher` usage.
- Use event-driven APIs (monitor/DPI/Explorer/filesystem notifications) wherever possible.
- Cache icons and load them lazily. Don't retain Shell/COM objects longer than needed.
- Avoid per-frame or per-item allocations in hot paths.

## Feature flags & desktop input detection

There are two implementations of "detect a double-click on empty desktop" (the trigger for
**Temp Hide All boxes**). The active one is selected by `GlobalFeaturesSwitches.UseGlobalMouseHookInsteadOfCustomSurface`
(`bool?`, defined in `GlobalFeaturesSwitches.cs`). **It is currently `false`** — the custom surface path is
active and stable; the global mouse hook path is dormant (kept for experiments).

- **Global low-level hook (`true`)** — `DesktopManager` calls `_mouseMonitor.Start()` which spins up
  `MouseMonitor` (`Win32APIs/Services/MouseMonitor.cs`), a `WH_MOUSE_LL` hook on a **dedicated STA background
  thread with its own `Dispatcher` message pump** (so a busy UI thread never delays input to other apps).
  It raises `MouseButtonDown` (the `HWND` under the cursor) and, after its own double-click logic,
  `DesktopDoubleClick`. Double-click confirmation uses `Win32Apis.IsDesktopChild` (descendant of
  `Progman`/`WorkerW`) + `Win32Apis.IsBoxWindow` (our own registered boxes, via `RegisterBoxWindow`/
  `UnregisterBoxWindow`) + `Win32Apis.IsDesktopEmptyPoint` (an `LVM_HITTEST` that confirms the point is
  not on an icon). `DesktopDoubleClick` is wired in `App.xaml.cs` to `DesktopManager.ToggleHideAllBoxes()`.
  **This path only observes input; it never captures or swallows it, so it does not interfere with other
  apps/games.**
- **Custom surface (`false`)** — `DesktopManager.EnsureSurface()` shows `Views/DesktopSurface.xaml`, a
  top-level layered WPF window glued to the desktop root that forwards mouse input to the Explorer
  list-view and detects the double-click itself (`WM_NCHITTEST` + two `WM_LBUTTONDOWN`s — WPF windows
  are created without `CS_DBLCLKS`, so `WM_LBUTTONDBLCLK` is never delivered; the surface adds the
  style itself in `OnLoaded`, and double-click is still confirmed manually from two `WM_LBUTTONDOWN`s).
  Its above-icons layer lifecycle (probe via the engine's probe, attach, WinEvent layer watch,
  re-glue on loss) is owned by `Win32APIs/Services/DesktopWidgetLayerHost.cs`, hosted in
  `DesktopManager` — the counterpart to the engine's `DesktopWallpaperLayerHost` (which sits
  BELOW the icons). Layering facts that must keep holding: the background uses a **non-zero alpha**
  (`#01000000`) so the window stays hit-testable while visually invisible (`WindowFromPoint` skips
  fully-transparent windows); the surface stays a **top-level** window (a `WS_EX_LAYERED` child would
  paint behind the non-layered Explorer list-view — never reparent via `SetParent`); z-order is pinned
  via `WM_WINDOWPOSCHANGING` against the root from `Win32Apis.GetDesktopRootHandle` (which must never
  return `0`); `WS_EX_NOACTIVATE` + `WM_MOUSEACTIVATE` handling must not suppress the activation OLE
  drag/drop needs. Hook hygiene is detach-before-attach (`DetachLayerGlue`, cached `_surfaceHwnd`) —
  `Loaded` refires on every Hide→Show cycle and must never stack hooks.

`ToggleHideAllBoxes` (in `DesktopManager`) toggles `HideAllBoxes`/`ShowAllBoxes`; hides wrap each window
in `Win32Apis.AllowHide` so `MinimizePreventionHook` doesn't re-show it, and the surface/tray stay visible
so the toggle is reversible. This is **session-only** (not persisted).

`GlobalFeaturesSwitches.ShowDebugTree` (currently `false`) enables `DesktopTreeDebugOverlay` — a diagnostic
that draws, at the cursor, the hit-test result, z-order, and the surface's style/ex-style. Use it (and the
`DesktopSurface`'s published `Win32Apis.DesktopSurfaceHandle`) when debugging desktop/surface layering.

## VirtualizingIconPanel

- `Controls/VirtualizingIconPanel.cs` is a custom `VirtualizingPanel` + `IScrollInfo` for the Icons view (`ItemWidth`, `ItemHeight`, `HorizontalSpacing`, `VerticalSpacing`, `CacheRows`). It was created to virtualize the wrapping icon grid but currently has unresolved layout issues (on load `MeasureOverride` returned `PositiveInfinity` → `InvalidOperationException`; icons disappearing; high CPU/memory even for small collections; `ScrollViewer` not syncing and affecting `DetailsGrid` scrolling). **Icons view currently uses the simple `WrapPanel` (`FolderPortalControl.xaml:152` `ItemsControl` + `WrapPanel`) which works reliably.** Keep `VirtualizingIconPanel.cs` (and `IconContainer` `Controls/IconContainer.xaml`) for future work — do not delete — but do not switch Icons back to it without fixing measure/scroll and verifying with `dotnet build` + manual toggle Icons/Details.

## Known issues

- **`BoxContainerWindow` custom tab-drag** (moving a box tab between containers) has two unfixed bugs:
  1. A dropped tab is not visible after drop when the target container is on a different monitor / a
     different `BoxContainerWindow` than the source.
  2. The drag crosshair / drag-image is offset on **multi-DPI** setups because `GetScreenDragPoint()`
     (around `BoxContainerWindow.xaml.cs:824`) uses `PointToScreen` instead of `GetCursorPos` combined with
     per-monitor DPI. `Shcore.GetDpiForMonitorTyped` + `User32.MonitorFromPoint` already live in
     `WindowsNative` but are not yet wrapped in `Win32Apis`.
- **`WindowExceptionHandler` unhandled-exception flow (lives in WPFShared):** `WPFShared.Helpers.ExceptionHandler.Register(app, options)`
  wires `DispatcherUnhandledException` / `AppDomain` / `TaskScheduler` (returns `IDisposable` to unwire);
  `App.xaml.cs:OnStartup` calls it (`#if !DEBUG` gate) with the app-specific ignorable check and log sinks.
  The dialog is a `Wpf.Ui.Controls.FluentWindow` (`WPFShared/Controls/WindowExceptionHandler.xaml`, no host
  assets referenced) bound to `WindowExceptionHandlerViewModel` (`WPFShared/ViewModels`, `CommunityToolkit.Mvvm`:
  `[ObservableProperty]`, `[RelayCommand]`).
  It offers **Continue** (keep app alive, `HasChosenContinue=true`, no shutdown) and **Exit Application**
  (`Danger`), plus **Copy details**; only **Exit Application** shuts down — closing via the X button,
  Alt+F4 or any system close just dismisses the dialog (treated as Continue). If you add new global
  exception sources, keep the `HasChosenContinue` / `RequestClose(bool)` contract intact.
- **WPF transparent hit-test & `WebView2CompositionControl`:** `CssWidgetControl` uses `WebView2CompositionControl` (not `WebView2`/`HwndHost`), which is a WPF-native composition control with no airspace — WPF hit-testing works over it. However, `WebView2CompositionControl.MouseEnter`/`MouseLeave` WPF events do **not** fire reliably (especially when the widget window is not active), so hover detection must **not** depend on them. Instead, all hover/click detection comes from the DOM bridge: inject `document.addEventListener('mouseenter'/'mouseleave'/'click', ()=>chrome.webview.postMessage(...))` via `AddScriptToExecuteOnDocumentCreatedAsync`, handle `WebMessageReceived` in `CssWidgetControl` (`WidgetMouseEnter/Leave/Clicked` events), and forward to `CssWidgetWindow`. `CssWidgetWindow` tracks cursor entering/leaving the widget window bounds via HWND-level `WM_MOUSEMOVE`/`WM_MOUSELEAVE` in `HwndHook` (not WPF `MouseEnter`/`MouseLeave`). `WindowDragController` hit-test already respects `ResizeMode` for non-resizable widgets. **`CssWidgetWindow` chrome:** `HeaderBorder` visibility uses split hover state (`_isWebViewHover` from DOM bridge + `_isWindowHover` from `WM_MOUSEMOVE`/`WM_MOUSELEAVE` in `HwndHook`). `ResizeBorder` (`#60FFFFFF` 1px outline) is **always visible for active resizable windows** (`_isActive && canResize`), not hover-dependent — hover-based toggling fights `WM_NCHITTEST` edge hits (`HTLEFT`/`HTRIGHT`/etc.) which cause rapid `MouseEnter`/`MouseLeave` cycles and visible flicker on inactive windows. `UpdateChrome()` is a no-op while `IsInMoveState` is true (during title-bar drag or native move/resize modal loop).
