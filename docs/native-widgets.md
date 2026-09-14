# Native widget plugins

Native widgets are window widgets written in C# (WPF) instead of HTML/CSS/JS.
They live side by side with web widgets in `%LocalAppData%\DesktopBoxes\UserWidgets\<slug>\`
— the **folder name is the widget identity**, there is no id field. A folder containing
`nwidget.json` belongs to the native loader, anything else to the web loader; detection
never loads code. Built-in widgets ship next to the app in `NativeWidgets/` and appear in
the gallery with an `App` badge. The gallery lists all kinds with a `Web` / `Native`
badge; placement, chrome (move/resize/title/lock/menu), fullscreen auto-suspend and theme
switching work the same for all.

> **Full trust, explicit consent.** A plugin is arbitrary .NET code running inside the app
> with your privileges — there is no in-process sandbox on modern .NET, and none is
> possible without moving plugins out of process. The control in place is consent plus
> fail-closed identity (§6): placing a user widget asks once per content hash, and edited
> content returns to untrusted until re-placed. Only install folders you trust.

## 1. Folder layout

```text
UserWidgets\MyClock\          (or NativeWidgets\MyClock\ for built-ins)
  nwidget.json     manifest (presence of this file IS the native marker)
  MyClock.cs       one or more .cs sources  — or —
  MyClock.dll      a single precompiled assembly
  icon.xaml        optional loose XAML (embedded automatically, see §5)
  thumbnail.png    optional gallery image
```

`nwidget.json` for a native widget:

```json
{
  "name": "My Clock",
  "author": "you",
  "description": "What it does.",
  "version": "1.0",
  "width": 300,
  "height": 300,
  "resizable": true,
  "supportsTheme": true
}
```

Optional disambiguation keys (omit when unambiguous):

| key | meaning |
|---|---|
| `assembly` | DLL file name. Default: the single `*.dll` in the folder. Required when several exist. Ignored in source mode. |
| `type` | Full type name implementing `INativeWidget`. Default: the single such type found. Required when zero (error) or several exist. |
| `thumbnail` | Image file. Default `thumbnail.png`. |

How the host decides, in order: explicit `assembly` → any `*.cs` sources (compile) → single `*.dll` → error. Folders starting with `.` (e.g. caches) are skipped by both loaders.

## 2. Assembly mode (single DLL)

1. Create a .NET class library (`net10.0-windows`, WPF allowed).
2. Reference `DesktopBoxes.WidgetSdk.dll` (shipped next to the app; match its version — the host reports the expected version in load errors).
3. Implement `INativeWidget` (easiest: derive `NativeWidgetControl`) with a **parameterless constructor**.
4. Drop the DLL (+ its private dependencies) into the folder with `nwidget.json`.
5. Gallery → Refresh → Place (user widgets ask a one-time trust consent, see §6).

Private `*.dll` dependencies in the same folder resolve automatically. Framework and
already-loaded assemblies (including WidgetSdk itself) unify with the host — do **not**
ship your own copy of `DesktopBoxes.WidgetSdk.dll`; a version mismatch fails load with
an explicit error telling you which version to reference.

## 3. Source mode (`.cs` + optional `.xaml`)

Drop sources in the folder; the host compiles them **once** with Roslyn (Release) and
caches the output under `%LocalAppData%\DesktopBoxes\NativeCache\<slug>\<sha256>\`
(portable PDB included, so you can attach a debugger and step through your sources).
**Edit any source → hash changes → automatic recompile** on next load (gallery Refresh
unloads the old assembly; open windows keep their version until closed).

Rules:

- One or more top-level `*.cs` files. No top-level statements — every file must only
  contain type declarations (the host looks for an `INativeWidget` implementation).
  SDK-style implicit usings (`System`, `System.Linq`, `System.Windows`, …) are
  injected automatically, so sources read like a normal project file.
- References available: the .NET + WPF framework surface and `DesktopBoxes.WidgetSdk`.
  Need another library? Ship its DLL next to the sources (assembly mode) instead.
- `*.xaml` files are embedded as manifest resources named `<slug>/<file>.xaml`.
  Load them with the SDK helper (loose XAML has no `x:Class` wiring — use `FindName`):

```csharp
public sealed class MyWidget : NativeWidgetControl
{
    public MyWidget()
    {
        LoadXamlResource(typeof(MyWidget).Assembly, "MyClock/icon.xaml");
        if (FindName("TitleText") is TextBlock t) t.Text = "hi";
    }
}
```

### Sample

`samples/NativeClockWidget/` is a complete source-mode plugin (analog clock, pure code,
no XAML): copy it to `UserWidgets/NativeClock/` (folder name is the identity) or place
the built-in `NativeClock` (shipped in `NativeWidgets/`, same code) straight from the
gallery. It demonstrates `Suspend`/`Resume` (timer control), `ApplyTheme` (dark/light
faces) and the discovery contract. Place it from the gallery — it carries the `Native`
badge (`App` source badge for the built-in).

## 4. `INativeWidget` reference

One instance is created per placed container, on the UI thread:

| member | contract |
|---|---|
| `Visual` | Root `FrameworkElement`, stable for the instance. Hosted in the widget window. |
| `Suspend()` / `Resume()` / `IsSuspended` | Stop/restart timers, animation, rendering. Idempotent; `Suspend` must be safe before first show. Called when the window hides, minimizes, is covered by fullscreen, on session lock / battery saver / remote session (if enabled in settings), and from the performance monitor. |
| `ApplyTheme(theme)` | `"dark"`, `"light"` or `null`. Only called when `supportsTheme` is true: after load, on app-theme change, on resume. |
| `Dispose()` | Unsubscribe, stop timers, release. Called before the window closes. |
| `Entered/Left/Clicked/Pressed/Focused/Unfocused` | **Optional** interaction events for content the host cannot see (HWND airspace). Pure WPF visuals need none of these — the host detects mouse/focus itself and merges both sources. |

Throwing from any member disables that instance (logged with full stack) but never
the host. Keep handlers fast; never block the UI thread (compile/load already happens
off-thread). Plugin constructors run on the UI thread and must return fast — defer
heavy work (timers are fine to *create*, just don't fetch the internet in a ctor).

## 5. Lifecycle

```text
gallery lists manifest → Place asks trust consent (user widgets, once per hash)
  → container created → window opens → trust re-checked, fail closed
  → manifest-only resolve → async CreateInstance (compile if needed)
  → Visual hosted → ApplyTheme → live
  → Suspend ⇄ Resume (visibility, cover, lock, monitor tools)
  → Dispose on close
```

If creation fails the window shows the reason as a placeholder (same for missing
folders and broken manifests in the gallery, which blocks placement). An untrusted
or edited-since-trust plugin shows an "untrusted" placeholder and loads nothing —
re-place it from the gallery to review and re-trust.

## 6. Trust model (user widgets)

`nwidget.json` has no signature, and .NET has no in-process sandbox, so consent is
keyed on content identity:

- Placing a user widget prompts once: name, folder, and the full-trust warning.
  Accepting records `slug → content hash` in `%AppData%\DesktopBoxes\NativeTrust.json`
  (source hash, or DLL bytes; deleted with the widget).
- Every window open re-checks the hash **before compiling or loading anything**.
  Edited content no longer matches → placeholder, no code runs, until re-placed.
- Built-in (`App`) widgets ship with the app and are implicitly trusted — same trust
  you already place in the install itself.

## 7. Troubleshooting

| symptom | cause / fix |
|---|---|
| Folder missing from gallery | Not under `UserWidgets/`, or name starts with `.`. No `nwidget.json` means the web loader owns it. |
| `No INativeWidget implementation found` | Reference the matching `DesktopBoxes.WidgetSdk` version; implement the interface or derive `NativeWidgetControl`; no top-level statements. |
| `Ambiguous … types` / `Ambiguous … DLLs` | Set `type` / `assembly` in `nwidget.json`. |
| `Compile failed: …` | First errors shown in logs + placeholder. Fix sources, gallery Refresh. |
| Stale code after editing sources | Refresh the gallery (unloads), then close/reopen placed windows. |
| "Untrusted plugin" placeholder | Content changed since consent (or never consented) — re-place from the gallery. |
| Hover/click dead over HWND content | WPF can't see through airspace — raise the opt-in interaction events. |
| Breakpoints not hit | Attach to `DesktopBoxesUI`, enable portable PDBs (already emitted); source path = your folder. |

## 8. Limits (v1)

- No per-widget persisted settings yet (use the folder for static assets).
- Open windows keep their loaded version until closed; Refresh only affects new placements.
- WebView2 inside a plugin is discouraged — its lifetime fights the host's teardown.
- One UI thread for everything: all members run on it; heavy work belongs on your own threads.
