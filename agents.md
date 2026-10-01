# Maktaby — Agents Guide

This file describes how autonomous agents (and contributors) should work in this repository.
Follow it to keep the architecture clean and the build green.

## Golden rules

- **Never** use scripts to edit project files, use only the Edit Tool
- **Never** put WPF, Win32 P/Invoke, or Shell COM code in the `Core` project folders.
  Core may only define models, interfaces, and pure-.NET service implementations.
- **Never** scatter `DllImport` / P/Invoke declarations outside the shared `Maktaby.Native`
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
- `Maktaby/app.manifest` declares PerMonitorV2 DPI awareness — do not remove it, or popups
  and geometry misbehave after display scale changes. Do not add a `compatibility` section to it
  either: SxS activation fails on this machine when one is present (verified by bisecting). Container rescaling
  keys off `DesktopResolution` (DIP work area) only — rescaling by the DIP ratio preserves both physical
  size and relative layout across DPI changes, so no physical-pixel special-casing.
- Widgets, containers and the desktop surface live on the **primary display only** — there is no
  multi-monitor positioning support (for now). Rescaling keys off the primary work area alone.
- Keep the NuGet surface small. Don't add packages without a reason. Currently allowed:
  `Microsoft.Extensions.DependencyInjection`,
  `CommunityToolkit.Mvvm`, `Wpf.Ui` (v4.3.0) and `Wpf.Ui.Tray` (v4.3.0) — the user explicitly asked for
  them to provide the modern window chrome, view-model helpers and tray styling. Plus `AvalonEdit`
  (6.3.1.120) — user-approved for HTML/CSS/JS highlighting in `WidgetDataWindow` only. Reference WPF-UI resource
  dictionaries via `ui:ThemesDictionary` / `ui:ControlsDictionary` (`App.xaml` already does); legacy pack URIs
  (`/Wpf.Ui;component/...`) are obsolete. The window type is `Wpf.Ui.Controls.FluentWindow`.
- Every `ui:FluentWindow` in `Views/AppWindows/` must derive from `AppFluentWindow`
  (`Views/AppWindows/AppFluentWindow.cs`), never directly from `FluentWindow`: the base sets
  `ExtendsContentIntoTitleBar="True"`, `WindowBackdropType="Mica"`,
  `WindowCornerPreference="Round"` plus the app icon, and provides a prebuilt `TitleBar`
  (host it in row 0 via a `ContentControl`, tweak only `ShowMaximize`/`ShowMinimize`).
  Without those props an open window keeps the background/controls of the theme it was created
  with: switching the app theme leaves the window stale (light cards/inputs in dark mode)
  until it is closed and reopened. `LoadingDialog` (global modal loading splash,
  `WPFServices/LoadingDialogService` + `Views/AppWindows/LoadingDialog.xaml`) is exempt
  (plain `Window`, intentionally fixed skin, primary screen only).
- Section cards that collapse belong in `Controls/CollapsibleGroupBox`
  (`HeaderContent` + `Content` body, `IsExpanded`, `BodyPadding`) — never hand-roll
  another chevron/header toggle.
- For WebView2 init use provided userData folder directly as the engine will create the "EBWebView" shared env folder in it
- Build must stay warning-light and succeed.

## Layer responsibilities

- **Core/Models** — add domain types here. Keep them simple and extensible.
- **Core/Interfaces** — define contracts, including platform abstractions. Use Core geometry
  types (`RectD`, `PointD`, `SizeD`) instead of WPF/Win32 coordinate types.
- **Core/Services** — only platform-independent implementations (no IO to OS specifics).
- **Shell/** — Windows Shell behavior. Shell COM native interop itself lives in `Maktaby.Native`
  (`Shell32`/`ShellCom`); `Shell/` keeps only the calling services.
- **Win32APIs/** — raw Win32 callers. `NativeMethods/Win32Apis.cs` is the UI's thin wrapper over
  `Maktaby.Native`; `Services` holds the callers. All declarations live in `Maktaby.Native`.
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
- **Maktaby.WidgetSdk/** (`src/`) — plugin contracts only (`INativeWidget`, base control,
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

 1. Declare it by hand in the shared `Maktaby.Native` project (`User32`/`Kernel32`/`Shell32`/… by
    owning DLL; structs in `NativeTypes.cs`, constants in `Win32Constants.cs`, callbacks in
    `NativeDelegates.cs`, desktop-layer core in `Desktop/DesktopLayerHostBase.cs`). Use `[DllImport]`
    (NOT `LibraryImport` — the source generator can't marshal structs like `SHFILEINFOW`), plain
    `IntPtr` handles, and `Maktaby.Native` geometry types (`RECT`, `POINT`, `SIZE`). `Maktaby.Native`
    takes no dependencies except Serilog (layer lifecycle logging only).
 2. The thin, named wrapper `Win32APIs/NativeMethods/Win32Apis.cs` re-exposes exactly the APIs
    the UI uses, so every native call in the codebase goes through `Win32Apis` and never P/Invoke
    directly. The engine calls `Maktaby.Native` directly.
 3. Use `Win32Apis` from a class in `Win32APIs/Services`, converting to/from Core geometry types
    (`RectD`, `PointD`, `SizeD`). Do NOT take native addresses or use `void*` handles.
 4. Win32APIs service classes that call these APIs are marked
    `[SupportedOSPlatform("windows10.0.14393")]` so platform-API analyzers (CA1416) stay quiet;
    keep that attribute in sync when you add newer-API usage. The project suppresses CA1416 globally
    (it is a Windows-only app), but the attribute is still good documentation.
 5. Expose behavior through a Core interface; inject the implementation in `App.xaml.cs`.
 6. Reference `Maktaby.Native` from the consuming `.csproj` (`Maktaby`,
    `Maktaby.LiveWallpaper` and `Maktaby.Shared` already do).

## Verifying changes

After editing:

1. `dotnet restore`
2. `dotnet build` (fix all errors; avoid introducing warnings)
3. If UI changed, run the app and confirm the WPF window opens and "New Box" works.

CI (`.github/workflows/ci.yml`) builds `src/Maktaby/Maktaby.csproj` on every push/PR to
`main` and `develop`, which pulls in its ProjectReference closure (LiveWallpaper, WidgetSdk,
Native, Shared) — but NOT `Maktaby.WidgetsCreator`. Touching that project means building the
solution locally (`dotnet build Maktaby.slnx`), or nothing will catch a break in it.

## Releasing

Never build or upload an installer by hand. Releases are **tag-driven** and GitHub Actions
does the rest. `docs/releasing.md` is the full reference (versioning scheme, workflow steps,
safeguards); the short version:

```bash
# Beta, from develop
git tag -a v1.0.33-beta.1 -m "1.0.33-beta.1" && git push origin v1.0.33-beta.1

# Stable, from main
git tag -a v1.0.33 -m "1.0.33" && git push origin v1.0.33
```

Or let `build_publish.ps1` derive the version, tag, push and build in one step
(`-DryRun` to preview, `-Version` to force a major/minor cut).

Rules worth knowing before you tag:

- **A `-beta` tag must be reachable from `develop`; a plain tag from `main`.** The workflow
  enforces this and fails the run otherwise — a stable tag on a develop-only commit is the
  mistake it exists to catch.
- **Checkout must be `fetch-depth: 0`.** MinVer derives the assembly version from the nearest
  git tag; a shallow checkout has none, so every build reports `0.0.0-alpha.0.1`.
- **A beta series holds its core still** (`1.0.32-beta`, `1.0.32-beta.1`, … all target
  `1.0.32`), and `main` promotes to that core. Do not bump the patch mid-series.
- **`build.ps1` exits `0` even when `iscc` is missing** (it only warns), so a local
  `-Action Publish` can succeed with no installer. Check `Installer/` actually has the `.exe`.

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
- Looping visuals must stop when not visible: `Visibility.Collapsed` hides but does NOT stop
  animation clocks — an indeterminate `ProgressRing`, `Storyboard` or any looping animation keeps
  invalidating (and re-rendering its whole window) forever. Gate the loop itself on the live
  condition (e.g. bind `IsIndeterminate` to the flag that shows the indicator, stop storyboards
  in `OnSuspend`/`Unloaded`/`Dispose`), never just on visibility. One forgotten ring per box
  window scales into a permanent multi-percent idle baseline.

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

## Shared pause supervision (auto-pause)

`Maktaby.Native/Playback/PauseSupervision.cs` owns **one** process-wide `PlaybackSupervisor`.
Both hosts subscribe to it instead of each newsing their own (they used to: the engine's
`EnsurePlayback` and `DesktopManager.StartWidgetAutoPause` each built a supervisor, so every
WinEvent was delivered twice and every evaluation walked all top-level windows twice).

- `PauseSubscription` = one host's registration: its own `Policy`/`Monitors`/
  `ExtraExcludedWindowClasses`/`OnTransition`, plus its own `SessionLocked`, `DisplayOff`,
  `Suspend`/`Resume`, applied `State` and QUNS `LastInputs`. Dispose to detach.
- `PauseSupervision.Shared.Subscribe(sub)` returns the subscription; the first attach
  constructs the supervisor, the last detach disposes it. Attach/detach logs
  `"Pause supervision: '{Host}' attached (N active, started hooks|reused hooks)"` — that line
  is the way to see the enable/disable condition in a log.
- The hosts deliberately keep **separate persisted policies** (`EngineConfig.Pause` vs
  `UserSettings.PauseWidgetsOn*`), so the merge shares the *capture*, never the policy: one
  `EnumWindows` + one system-flag read feeds N cheap in-memory `PauseDecision` passes. Do not
  "simplify" this into one merged policy — that silently breaks the asymmetric setting.
- The subscriber list is mutated in place, never rebuilt. A rebuild would re-install the hook
  set and start every attached host from empty state, re-firing transitions it already
  reported. Per-subscriber state lives on the subscription so hosts already attached are
  untouched when one joins or leaves.
- Exclusion sets are unioned across subscribers: safe, because an exclusion only ever removes
  a window from consideration and each host's own windows are already excluded by PID.
- `Suspend` is per-subscription, not supervisor-wide. The engine's user pause must not stop
  the widget host's supervision. If every subscriber is suspended the whole evaluation — the
  window capture included — is skipped.
- `extraExcludedWindowClasses` is effectively vestigial for both hosts: the engine is a
  library inside `Maktaby`, so its `WallpaperWindow` already dies at the `pid ==
  OwnPid` filter, and the widget host passes `null`.
- Enable conditions: engine attaches from `UpdatePauseSupervision` (enabled AND a renderer
  that can pause — video/web/animated GIF); the desktop app attaches from
  `DesktopManager.UpdateWidgetAutoPause` (not `IsDisabled` AND at least one Box marked
  visible in the model). The widget side is driven from `AddWindow`/`RemoveWindow` (every
  membership-changing path), not only from Show/HideContainer, and reads the **model's**
  `IsVisible` rather than `Window.IsVisible` so it never races Show/Close ordering.
- **Batched window changes must opt out per call and re-evaluate once at the end.**
  `AddWindow`/`RemoveWindow`/`CloseAll` all take `bool updateWidgetAutoPause = true`. A loop
  over N containers must pass `updateWidgetAutoPause: false` on every call and let the
  batch's single trailing `UpdateWidgetAutoPause()` do the work — otherwise a 50-box startup
  re-resolves the monitor/supervision condition 50 times instead of once. The defaults stay
  `true` so unbatched callers cannot silently forget. Current batched sites:
  `StreamRemainingContainersAsync` (startup streaming), `CloseAll` inside `ResetAsync`,
  `RestoreAsync` and `RestoreBundleAsync` — all three of the latter re-enter `InitializeAsync`,
  which re-evaluates via the stream's `finally`. Note `CloseAll` disposes `_widgetPause`
  unconditionally before its own trailing update, so a batch always detaches; the trailing
  update only re-subscribes if Boxes remain. A forgotten flag shows up as a **stale ref-count**
  (a hook thread that never appears, or never goes away) — no crash, so it can sit unnoticed
  until someone reads the `"Pause supervision: ..."` log lines.
- Anything scheduled as a callback that runs async work must be typed `Func<Task>`, not
  `Action` — an `Action` turns an async lambda into `async void`, whose exceptions escape the
  surrounding `try/catch`/`finally` and land on the dispatcher. `StreamRemainingContainersAsync`
  is typed this way for exactly that reason.

## VirtualizingIconPanel

- `Controls/VirtualizingIconPanel.cs` is a custom `VirtualizingPanel` + `IScrollInfo` for the Icons view (`ItemWidth`, `ItemHeight`, `HorizontalSpacing`, `VerticalSpacing`, `CacheRows`). It was created to virtualize the wrapping icon grid but currently has unresolved layout issues (on load `MeasureOverride` returned `PositiveInfinity` → `InvalidOperationException`; icons disappearing; high CPU/memory even for small collections; `ScrollViewer` not syncing and affecting `DetailsGrid` scrolling). **Icons view currently uses the simple `WrapPanel` (`FolderPortalControl.xaml:152` `ItemsControl` + `WrapPanel`) which works reliably.** Keep `VirtualizingIconPanel.cs` (and `IconContainer` `Controls/IconContainer.xaml`) for future work — do not delete — but do not switch Icons back to it without fixing measure/scroll and verifying with `dotnet build` + manual toggle Icons/Details.

## Known issues

- **`BoxContainerWindow` custom tab-drag** (moving a box tab between containers) has two unfixed bugs:
  1. A dropped tab is not visible after drop when the target container is on a different monitor / a
     different `BoxContainerWindow` than the source.
  2. The drag crosshair / drag-image is offset on **multi-DPI** setups because `GetScreenDragPoint()`
     (around `BoxContainerWindow.xaml.cs:824`) uses `PointToScreen` instead of `GetCursorPos` combined with
     per-monitor DPI. `Shcore.GetDpiForMonitorTyped` + `User32.MonitorFromPoint` already live in
     `Maktaby.Native` but are not yet wrapped in `Win32Apis`.
- **`WindowExceptionHandler` unhandled-exception flow (lives in Maktaby.Shared):** `Maktaby.Shared.Helpers.ExceptionHandler.Register(app, options)`
  wires `DispatcherUnhandledException` / `AppDomain` / `TaskScheduler` (returns `IDisposable` to unwire);
  `App.xaml.cs:OnStartup` calls it (`#if !DEBUG` gate) with the app-specific ignorable check and log sinks.
  The dialog is a `Wpf.Ui.Controls.FluentWindow` (`Maktaby.Shared/Controls/WindowExceptionHandler.xaml`, no host
  assets referenced) bound to `WindowExceptionHandlerViewModel` (`Maktaby.Shared/ViewModels`, `CommunityToolkit.Mvvm`:
  `[ObservableProperty]`, `[RelayCommand]`).
  It offers **Continue** (keep app alive, `HasChosenContinue=true`, no shutdown) and **Exit Application**
  (`Danger`), plus **Copy details**; only **Exit Application** shuts down — closing via the X button,
  Alt+F4 or any system close just dismisses the dialog (treated as Continue). If you add new global
  exception sources, keep the `HasChosenContinue` / `RequestClose(bool)` contract intact.
- **Post-sleep/hibernate native video-memory leak (measured 2026-09-20, mitigated not curable):**
  private bytes climb to 1–3 GB after resume. Dump + ETL diagnosis: destroying an active frame-server
  `MediaPlayer` orphans ~32 MB/monitor of Intel GPU driver video memory (`d3d9 → igd9trinity64 → dxgkrnl`,
  write-combined private mappings, ~6 MB/s in the post-resume state). Managed teardown is complete when this
  happens (zero renderers/players/D3D wrappers left, threads reaped) — the strands are purely native and only
  a driver reset reclaims them, so **every full video-pipeline destroy has a permanent ~32 MB/monitor cost**.
  Consequences, all in `Maktaby.LiveWallpaper/Engine.cs` unless noted: transient notifications (display
  change, TaskbarCreated, unlock) must NEVER run an immediate `ReapplyAll` — they go through
  `OnTopologyMightHaveChanged` (cheap re-glue) + the settled pass, which rebuilds only on real topology
  disagreement; `ReapplyAll` is reserved for genuine layer/device loss. Resume arms a 10 s settle
  (`ResumeSettleDelay`) plus one recycle gated on actually-elevated private bytes (900 MB absolute /
  +350 MB growth, once per resume + 30 min cooldown — `EvaluatePostResumeRecycle`); unlock must not shorten
  a pending resume settle (`OnSessionUnlocked`). Do not add polling memory watchdogs (event-driven checks
  only, per Performance expectations above). Related fixes shipped with it: `VideoRenderer` preload never
  worked until `DataWriter.DetachStream()` was added before disposal (undisposed writer closed the stream →
  `Seek(0)` ODE, silently swallowed → disk streaming forever); preload outcome is logged per upgrade
  (`HIT` / `MISS + filled` / `unavailable`, `VideoRenderer` + GIF path in `ImageRenderer`), and tray actions
  log in `LiveWallpaperManager` so manual toggles are attributable. `VirtualDesktopWallpaper` can only scope
  orphan `Desktops\{guid}` keys via the `VirtualDesktopIDs` value — absent on some builds (then it falls back
  to all subkeys, hence `Saved 155…` log lines); `SetAll`/`Restore` are diff-before-write so steady state
  costs reads only. If private bytes keep climbing with no `Settled topology disagrees` / recycle lines in
  the log, the stranding is happening on live pipelines (driver state) — widen the recycle window, do not
  add more rebuilds.
- **NativeWidgets post-resume native-memory climb (mitigation shipped, root cause NOT confirmed).** Hard to
  reproduce: it needs the display driver to actually degrade to WPF render tier 0 on resume, which does not
  happen on demand. Working hypothesis — the same stranded-native-memory class as the wallpaper-engine leak
  above, reached through WPF/GDI rather than a frame server: a tier flip under a live `BitmapCache` (and
  under every `Effect`, which caches an intermediate surface) can strand those surfaces permanently, and
  `Suspend()` never touched them (it only stops plugin timers/animations — the visual tree stays live and
  keeps being GDI-rasterized). Both widget window types now route through
  `Helpers/SoftwareRenderingFallback` (one instance per window, owned by the window), driven by
  `WidgetWindow.OnRenderTierChanged(bool)` — override that in any new widget window; it is a no-op by
  default, and `App.OnRenderTierChanged` calls it on tier 0 and again on recovery. `Apply(contentVisual,
  overlay, software, reevaluateChrome)` does two things and restores both on recovery: it clears every
  non-null `CacheMode` in the content visual (remembered, so recovery restores exactly what was dropped;
  restore skips elements with no `PresentationSource` so a torn-down plugin is never resurrected and its
  collectible ALC never pinned — hence `ForgetCaches()` on plugin swap / WebView2 teardown), and it freezes
  the `WidgetChromeOverlay` (`IsFrozen` gates `UpdateChrome` in both windows; `SyncSuppressed` gates
  `OwnerPosChanged`/`OverlayPosChanged`, since every Show/Hide and every `SyncFromOwner` re-acquires that
  window's render surface and a resume DPI-remaps it). Root each window on its OWN content visual
  (`PluginVisual` / `WidgetVisual`) — never `Window.Content`, which is host chrome that is never dropped.
  `WebWidgetWindow`'s freeze is the whole story there: `WebWidgetControl.Suspend()` already calls
  `TrySuspendAsync` (the WebView2 browser process really stops) and the WPF side has no `CacheMode`; the
  cache drop is still routed through the shared helper so a future control with one is covered for free.
  A surviving `Effect` count in the census is expected — only `CacheMode` is dropped.
  **2026-09-28 evidence, and why there is now a SECOND, resume-driven mitigation:** after a long
  hibernation, re-showing a hidden NativeClock blew private bytes 849 MB → 3769 MB and GDI 229 → 2362
  with a flat managed heap, sustained ~4.5% CPU — and the log showed `OutOfMemoryException` ×23 inside
  `DUCE.Channel.SyncFlush` ← `HwndTarget.UpdateWindowPos` (the MIL composition channel itself starving,
  not managed code) with **zero tier lines anywhere**: the driver reported tier 2 while stranding
  render-target memory per frame, so the tier-0 pass above never engaged. The trigger fits the channel
  theory exactly: the clock is the only continuously-invalidating visual (10 Hz timer) — hidden, nothing
  invalidates and nothing leaks; re-shown, every tick allocates through the degraded channel. So
  `App.xaml.cs` now ALSO quiesces on the power-resume broadcast (`SystemEvents.PowerModeChanged` → `Resume`,
  the same `PBT_APMRESUMEAUTOMATIC` the engine converges on), independent of tier:
  `OnPowerResumeQuiesce` suspends live widget windows + releases their render resources (the same
  `Suspend()` + `OnRenderTierChanged(true)` pair) and forces `RenderOptions.ProcessRenderMode` to
  `SoftwareOnly` up front (dropping the suspect HW render targets before the post-resume storm of DPI
  remaps / re-glues / overlay re-syncs runs); one one-shot 10 s timer (same settle as the engine, NOT a
  watchdog) runs `RecoverPowerResume`, which hands the pipeline back to `RenderMode.Default` (safe at any
  tier — Default under tier 0 is still software) and resumes exactly what this pass suspended. Composition
  with the tier pass: quiesce skips the widget half while `_tierPauseActive` (tier owns it); recovery while
  tier-0 is active ADOPTS our suspended list into `_tierSuspendedWidgets` instead of resuming behind its
  back (those windows were already suspended when tier-0 arrived, so the tier pass never listed them —
  without adoption nobody would resume them); a second resume inside the settle window recovers first,
  then quiesces fresh, so nothing is orphaned. Both passes log UNGATED `Power resume…` lines with
  `MemoryFragment()` + census (same reasoning as the tier lines). `ClockWidget.Dispose` also unhooks
  `Tick`/`Loaded`/`Unloaded` and clears the face `Effect`, so hide/show cycles (which destroy + recreate
   the host window every toggle) can't pin the old tree + ALC per cycle.
  **Attribution, not proof:** every tier transition logs a census (per-widget `cache`/`fx`/`vis`
  counts — the clock now reads `cache:0,fx:1`, any `cache:1` on it after this date means a cache
  crept back in)
  **2026-09-29 root cause found — it was the `BitmapCache`, and it is gone:** deleting the clock
  stopped the leak, a freshly added clock re-leaked, and the cache-less `CalendarWidget` stays flat —
  so the leak was never a stale window, it was the cache surface itself: allocated on the render device
  present at creation, that device is a zombie after long sleep/hibernation, the cache never hits again,
  and every 10 Hz invalidation re-renders + reallocates it with each allocation stranding driver-side.
  `ClockWidget` no longer sets `staticLayer.CacheMode` (comment at the site explains why); per-tick cost
  without it is just the moved hands + composite via dirty-region tracking, the face/`DropShadowEffect`
  only re-render when actually invalidated. Census expectation for the clock is now `cache:0,fx:1`.
  The tier-0 + resume-quiesce machinery above is RETAINED (other/future widgets may still cache, and the
  `Effect`/overlay handling still applies) — but a clock-only native-memory climb after this date means
  the burner moved elsewhere, not the cache.)
  AND a `private=…MB managed=…MB gdi=… user=…` fragment (`App.MemoryFragment`). Those tier lines are
  deliberately UNGATED — they fire a handful of times per driver degradation, and a native-memory
  incident that only the gated `Health:` line could evidence would leave a release build with no
  record at all. The periodic `Health:` line (also census + memory, once a minute) stays behind
  `GlobalFeaturesSwitches.EnableHealthSnapshots`, which is an investigation tool and must stay off in
  release — do not move the tier-transition data behind it.
  Read these before widening any of this: if private bytes still climb with `cache:0` persisting after
  a tier-0 pass, the BitmapCache hypothesis is dead and the cost is in the window surfaces, not the
  caches. The census deliberately does **not** count `Storyboard`/`DispatcherTimer` (neither is
  reachable from the visual tree and neither is publicly enumerable) — a leaking animation clock
  shows up as climbing CPU with a flat census, not a climbing census. `CalendarWidget` already stops
  its nav storyboard in `OnSuspend`; keep that in any new animated widget.
- **WPF transparent hit-test & `WebView2CompositionControl`:** `CssWidgetControl` uses `WebView2CompositionControl` (not `WebView2`/`HwndHost`), which is a WPF-native composition control with no airspace — WPF hit-testing works over it. However, `WebView2CompositionControl.MouseEnter`/`MouseLeave` WPF events do **not** fire reliably (especially when the widget window is not active), so hover detection must **not** depend on them. Instead, all hover/click detection comes from the DOM bridge: inject `document.addEventListener('mouseenter'/'mouseleave'/'click', ()=>chrome.webview.postMessage(...))` via `AddScriptToExecuteOnDocumentCreatedAsync`, handle `WebMessageReceived` in `CssWidgetControl` (`WidgetMouseEnter/Leave/Clicked` events), and forward to `CssWidgetWindow`. `CssWidgetWindow` tracks cursor entering/leaving the widget window bounds via HWND-level `WM_MOUSEMOVE`/`WM_MOUSELEAVE` in `HwndHook` (not WPF `MouseEnter`/`MouseLeave`). `WindowDragController` hit-test already respects `ResizeMode` for non-resizable widgets. **`CssWidgetWindow` chrome:** `HeaderBorder` visibility uses split hover state (`_isWebViewHover` from DOM bridge + `_isWindowHover` from `WM_MOUSEMOVE`/`WM_MOUSELEAVE` in `HwndHook`). `ResizeBorder` (`#60FFFFFF` 1px outline) is **always visible for active resizable windows** (`_isActive && canResize`), not hover-dependent — hover-based toggling fights `WM_NCHITTEST` edge hits (`HTLEFT`/`HTRIGHT`/etc.) which cause rapid `MouseEnter`/`MouseLeave` cycles and visible flicker on inactive windows. `UpdateChrome()` is a no-op while `IsInMoveState` is true (during title-bar drag or native move/resize modal loop).
