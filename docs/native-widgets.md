# Native widget plugins

Native widgets are window widgets written in C# (WPF) instead of HTML/CSS/JS.
They live side by side with web widgets in `%LocalAppData%\Maktaby\UserWidgets\<slug>\`
— the **folder name is the widget identity**, there is no id field. A folder containing
`nwidget.json` belongs to the native loader, anything else to the web loader; detection
never loads code. Built-in widgets ship next to the app in `NativeWidgets/` and appear in
the gallery with an `App` badge. The gallery lists all kinds with a `Web` / `Native`
badge; placement, chrome (move/resize/lock/menu), fullscreen auto-suspend and theme
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
  langs\           optional translations, one .xlf per language (see §9)
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
  "supportsTheme": true,
  "showTitleBar": false
}
```

Optional disambiguation keys (omit when unambiguous):

| key | meaning |
|---|---|
| `assembly` | DLL file name. Default: the single `*.dll` in the folder. Required when several exist. Ignored in source mode. |
| `type` | Full type name implementing `INativeWidget`. Default: the single such type found. Required when zero (error) or several exist. |
| `thumbnail` | Image file. Default `thumbnail.png`. |
| `showTitleBar` | `false` (default) hides the host's title bar over the widget. Opt in only if the widget draws nothing in its top ~28px — the host already supplies everything that bar provided. See §1a. |
| `autoTheme` | `false` (default). Adds an **Auto** option to the automatic theme setting *and* makes it the default. Only meaningful for a widget that paints a user-choosable background and can derive light/dark from it. Requires `supportsTheme`. |

How the host decides, in order: explicit `assembly` → any `*.cs` sources (compile) → single `*.dll` → error. Folders starting with `.` (e.g. caches) are skipped by both loaders.

## 1a. Chrome: no title bar, drag the widget body

The widget body is the drag surface and the widget fills its own window, so a title bar
drawn over the top would just cover the first row of content. Accordingly:

- **The title bar is collapsed by default** (`showTitleBar: false`). The chrome overlay
  window itself stays up regardless — it owns the resize edges and the selection border —
  only its 28px header strip is collapsed. Resize and the selected-state outline are
  unaffected.
- **The host window background is `#01000000`**, not transparent. Fully transparent
  windows are click-through, which would mean no click-to-focus and no right-click widget
  menu on empty space. Alpha 1/255 is visually identical.
- **A left press on the widget body moves the window.** The host drives the same
  `WindowDragController` the box windows use, so body-drag snaps to screen edges and to other
  containers, commits the new bounds, and — unlike a native `WM_NCLBUTTONDOWN`/caption move —
  **cannot be turned into a double-click maximize**. The host provides this to
  `NativeWidgetControl`; a bare `INativeWidget` implementation does not get it.

If your widget has clickable things in the drag area, keep them out of the drag by tagging
them `no-drag` — checked on the pressed element and every ancestor up to the widget root:

```csharp
tab.Tag = NoDragTag;        // NativeWidgetControl.NoDragTag == "no-drag"
```

WPF's own interactive controls (`Button`, `TextBox`, `Slider`, `ComboBox`) are excluded
automatically, so only hand-built `Border`/`TextBlock` hit targets need the tag. If your
layout has drag surfaces and press targets interleaved in a way tags cannot express, take
over the decision:

```csharp
public MyWidget() => MoveWindowOnMouseDownEnabled = false;   // opt out of the automatic move

private void OnRootMouseDown(object sender, MouseButtonEventArgs e)
{
    if (e.OriginalSource == _root) MoveWindowOnMouseDown(e);  // then call it yourself
}
```

### Knowing whether the widget is being used

`IsHostEngaged` (with the `OnHostInteractionChanged()` override) is pushed by the host whenever
the user starts or stops engaging with the widget. It is the hook for hiding your own chrome
until they are actually interacting, which is usually what you want on a desktop widget: the
resting state should be the data, not the controls.

```csharp
protected override void OnHostInteractionChanged()
    => _controls.Visibility = IsHostEngaged ? Visibility.Visible : Visibility.Collapsed;
```

**"Engaged" is deliberately not "my window is active".** The host's title bar and resize edges
live in a separate chrome-overlay window, so dragging or resizing the widget leaves the widget
window inactive while the user is plainly interacting with it. The host counts chrome-visible
and an in-flight drag or resize as engaged too — without that, your controls would vanish the
instant a drag began, which is exactly when they are being aimed at.

A click anywhere on the widget engages it, so the controls come back on the same press that
the user would have used to click them.

### Theme

`NativeWidgetControl` declares a `theme` setting (`WidgetSettingKind.Theme`) in its **base**
constructor, so it is always the first row and cannot be forgotten. The host renders it only
when the manifest sets `supportsTheme`, and never asks the widget to declare it.

| value | meaning |
|---|---|
| `""` | Follow the app / system theme (the declared default). |
| `light` / `dark` | Pinned. |
| `auto` | Offered (and made the default) when the manifest sets `autoTheme`. The host resolves the app theme as usual and the widget overrides it in `OnApplyTheme` when — and only when — it has a user colour to derive from. |

That last row is the subtle one. The host deliberately does **not** push `auto` through as a
theme string: with no user colour the widget's background is itself theme-derived, so deriving
the theme from it would read back the previously painted card and get stuck on one side
forever. In that case `auto` simply follows the app theme. A widget overrides in
`OnApplyTheme` by checking `ThemeSettingValue` and reading its own background — see
`WeatherWidget.OnApplyTheme`.

`WidgetSetting.OverrideDefault` is how the host promotes `auto` to the default: it moves the
current value only when it still equals the old default, so an explicit user choice is never
overwritten and **Reset** returns to `auto`.

## 10. Developing without a live service

A widget that calls a real service refetches on every window open, every resume and every
refresh tick, which makes layout work impossible to eyeball twice in a row and hammers a
free-tier endpoint. `WidgetDev.SimulateData` lets the widget serve generated data instead.

It is published once by the host at startup from
`GlobalFeaturesSwitches.SimulateNativeWidgetData` — **debug-only, `false` in release** — and
defaults to `false` regardless. A widget must still check it itself before skipping its own
I/O; the hook publishes the decision, it does not perform it.

```csharp
if (Maktaby.WidgetSdk.WidgetDev.SimulateData) { ApplySample(); return; }
```

Check it *before* any staleness logic: the point is that no window open, resume or timer tick
reaches the network at all. `WidgetDev.Reset()` restores the shipping value.

## 2. Assembly mode (single DLL)

1. Create a .NET class library (`net10.0-windows`, WPF allowed).
2. Reference `Maktaby.WidgetSdk.dll` (shipped next to the app; match its version — the host reports the expected version in load errors).
3. Implement `INativeWidget` (easiest: derive `NativeWidgetControl`) with a **parameterless constructor**.
4. Drop the DLL (+ its private dependencies) into the folder with `nwidget.json`.
5. Gallery → Refresh → Place (user widgets ask a one-time trust consent, see §6).

Private `*.dll` dependencies in the same folder resolve automatically. Framework and
already-loaded assemblies (including WidgetSdk itself) unify with the host — do **not**
ship your own copy of `Maktaby.WidgetSdk.dll`; a version mismatch fails load with
an explicit error telling you which version to reference.

## 3. Source mode (`.cs` + optional `.xaml`)

Drop sources in the folder; the host compiles them **once** with Roslyn (Release) and
caches the output under `%LocalAppData%\Maktaby\NativeCache\<slug>\<sha256>\`
(portable PDB included, so you can attach a debugger and step through your sources).
**Edit any source → hash changes → automatic recompile** on next load (gallery Refresh
unloads the old assembly; open windows keep their version until closed).

Rules:

- One or more top-level `*.cs` files. No top-level statements — every file must only
  contain type declarations (the host looks for an `INativeWidget` implementation).
  SDK-style implicit usings (`System`, `System.Linq`, `System.Windows`, …) are
  injected automatically, so sources read like a normal project file.
- References available: the .NET + WPF framework surface and `Maktaby.WidgetSdk`.
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

The built-in `NativeClock` (shipped in `NativeWidgets/`, pure code, no XAML) doubles as the
reference plugin: place it straight from the gallery — it carries the `Native` badge (`App`
source badge for the built-in). It demonstrates `Suspend`/`Resume` (timer control),
`ApplyTheme` (dark/light faces), user settings (`showTicks`, `secondsHandMode`, …) and the
discovery contract. To experiment on a copy, duplicate its folder to
`UserWidgets/NativeClock/` (folder name is the identity) and edit the sources there.

## 4. `INativeWidget` reference

One instance is created per placed container, on the UI thread:

| member | contract |
|---|---|
| `Visual` | Root `FrameworkElement`, stable for the instance. Hosted in the widget window. |
| `Suspend()` / `Resume()` / `IsSuspended` | Stop/restart timers, animation, rendering. Idempotent; `Suspend` must be safe before first show. Called when the window hides, minimizes, is covered by fullscreen, on session lock / battery saver / remote session (if enabled in settings), and from the performance monitor. |
| `ApplyTheme(theme)` | `"dark"`, `"light"` or `null`. Only called when `supportsTheme` is true: after load, on app-theme change, on resume. |
| `Dispose()` | Unsubscribe, stop timers, release. Called before the window closes. |
| `Entered/Left/Clicked/Pressed/Focused/Unfocused` | **Optional** interaction events for content the host cannot see (HWND airspace). Pure WPF visuals need none of these — the host detects mouse/focus itself and merges both sources. |
| *(no member)* | Moving the window, the title bar and the right-click menu are **not** part of the interface. The first two are host-side policy; drag-to-move reaches a `NativeWidgetControl` through a private seam and is documented in §1a. A bare `INativeWidget` gets focus, menu and resize, but not body-drag. |

Throwing from any member disables that instance (logged with full stack) but never
the host. Keep handlers fast; never block the UI thread (compile/load already happens
off-thread). Plugin constructors run on the UI thread and must return fast — defer
heavy work (timers are fine to *create*, just don't fetch the internet in a ctor).

### User settings (optional)

Derive from `NativeWidgetControl` and declare settings in the constructor — the host renders
one editor per setting and writes back through `Value` (persisted per placed widget):

```csharp
public ClockWidget()
{
    // (name, displayName, description, kind, onChanged, default, options)
    DefineSetting("secondsHandMode", "Second hand", "How the second hand behaves",
        WidgetSettingKind.ListOfStrings, () => UpdateHands(), defaultValue: "smooth",
        listOfAvailableStrings: new Dictionary<string, string>
        {
            ["hide"] = "Hide",
            ["smooth"] = "Smooth sweep",
            ["step"] = "Step once per second",
        });
    // ...
}

private void UpdateHands()
{
    string mode = FindSetting("secondsHandMode")!.GetString(); // "hide" | "smooth" | "step"
    // ... (cache the mode in a field — UpdateHands runs at 10 Hz)
}
```

`WidgetSettingKind` is `String` (`string`), `Number` (`double`), `Boolean` (`bool`),
`ListOfStrings` (`string` key from `listOfAvailableStrings`, rendered as a combo box) or
`ColorH` (`string`, rendered as a colour picker and stored as `#AARRGGBB` — alpha first);
mismatched writes throw `ArgumentException`.

A `Number` setting picks its own editor: supply **both** `minimum` and `maximum` and the host
renders a slider over that range; supply neither and it renders a numeric field with spin
buttons. Supplying only one bound is treated as no range — a single bound would pin the
control to a half-range the plugin never asked for. Leave both `null` (the default) for a
free number like a font size, and set them for an ordinal amount like an opacity.

```csharp
DefineSetting("opacity", "Background opacity", "0-100%",
    WidgetSettingKind.Number, OnChanged, defaultValue: 100, minimum: 0, maximum: 100);   // slider
DefineSetting("fontSize", "Font size", "Points",
    WidgetSettingKind.Number, OnChanged, defaultValue: 12);                              // numeric field
```

`ValueChanged` fires on the UI thread for host
edits too; `Reset()` restores `DefaultValue`. `GetColorHex()` normalises a `ColorH` value for
painting, and `WidgetSetting.TryNormalizeColorHex` accepts `#RGB`, `#RRGGBB`, `#ARGB`,
`#AARRGGBB` and named colours. Raw `INativeWidget` implementations (not deriving
from the base) can implement `IWidgetSettingsProvider` directly with their own list.

## 5. Hosting other UI stacks (anything that is a `FrameworkElement`)

`Visual` can wrap more than hand-built WPF — verified against the host:

| stack | how | notes |
|---|---|---|
| WPF | return controls directly | full host hover/focus detection, no plugin events needed |
| WinForms | `System.Windows.Forms.Integration.WindowsFormsHost` + any WinForms control | compile refs included; qualify ambiguous names (`System.Windows.Forms.Button` vs `System.Windows.Controls.Button` — the injected implicit usings cover the WPF side) |
| Raw Win32 HWND | `HwndHost` subclass (`BuildWindowCore`/`DestroyWindowCore`) | works; host WPF hit-testing cannot see through airspace — raise the opt-in interaction events so chrome/activation keep working |
| Direct3D11 | `D3DImage` (WPF) or `HwndHost` + own swapchain | stop presenting in `OnSuspend`; resume in `OnResume` |
| SkiaSharp etc. | ship the NuGet DLLs next to your sources (`SKElement`); folder DLLs are added as compile references automatically (host-owned `Maktaby.*` excluded to force type unification) | same airspace/interaction rules as HWND |
| GTK | not supported | needs its own event loop; cannot be embedded in a WPF visual tree |

HWND-backed content (WinForms, raw HWND, GPU swapchains) is subject to WPF airspace:
the host's hover detection stops at the HWND boundary, so raise `Entered/Left/Clicked`
(and friends) from the plugin — the host merges both sources. The window chrome lives
in a separate top-level overlay window, so it never fights hosted HWNDs for airspace.

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
  Accepting records `slug → content hash` in `%AppData%\Maktaby\NativeTrust.json`
  (source hash, or DLL bytes; deleted with the widget).
- Every window open re-checks the hash **before compiling or loading anything**.
  Edited content no longer matches → placeholder, no code runs, until re-placed.
- Built-in (`App`) widgets ship with the app and are implicitly trusted — same trust
  you already place in the install itself.

## 7. Troubleshooting

| symptom | cause / fix |
|---|---|
| Folder missing from gallery | Not under `UserWidgets/`, or name starts with `.`. No `nwidget.json` means the web loader owns it. |
| `No INativeWidget implementation found` | Reference the matching `Maktaby.WidgetSdk` version; implement the interface or derive `NativeWidgetControl`; no top-level statements. |
| `Ambiguous … types` / `Ambiguous … DLLs` | Set `type` / `assembly` in `nwidget.json`. |
| `Compile failed: …` | First errors shown in logs + placeholder. Fix sources, gallery Refresh. |
| Stale code after editing sources | Refresh the gallery (unloads), then close/reopen placed windows. |
| "Untrusted plugin" placeholder | Content changed since consent (or never consented) — re-place from the gallery. |
| Hover/click dead over HWND content | WPF can't see through airspace — raise the opt-in interaction events. |
| Breakpoints not hit | Attach to `Maktaby`, enable portable PDBs (already emitted); source path = your folder. |

## 8. Limits (v1)

- Per-widget persisted settings exist (see §4): declare them with `DefineSetting` and the
  host renders the editor. They are stored per placed instance, not globally.
- Drag-to-move is a `NativeWidgetControl` feature, not an `INativeWidget` one (§1a), and a
  bare `INativeWidget` has no way to request it.
- Open windows keep their loaded version until closed; Refresh only affects new placements.
- WebView2 inside a plugin is discouraged — its lifetime fights the host's teardown.
- One UI thread for everything: all members run on it; heavy work belongs on your own threads.

## 9. Localization

A widget ships its own translations in a `langs/` subfolder — one Xliff 2.0 `.xlf` per
language, named by culture code (`en.xlf`, `ar.xlf`, …). The host loads them and resolves
strings against the **active app language** (Settings → Language), so a widget follows the
same language the rest of the app uses. The format is identical to the host's own `Langs/`
files — one format to learn.

```text
MyClock\
  langs\
    en.xlf      source / fallback (recommended, not required)
    ar.xlf      Arabic, declares RightToLeft
```

```xml
<?xml version="1.0" encoding="utf-8"?>
<xliff version="2.0" xmlns="urn:oasis:names:tc:xliff:document:2.0" srcLang="en" trgLang="ar">
  <file id="ar">
    <!-- Reserved units -->
    <unit id="meta.flowdirection"><segment><source>RightToLeft</source><target>RightToLeft</target></segment></unit>
    <unit id="meta.name"><segment><source>Arabic</source><target>العربية</target></segment></unit>
    <unit id="GoToToday"><segment><source>Go to today</source><target>الانتقال إلى اليوم</target></segment></unit>
  </file>
</xliff>
```

**Reserved units** — `meta.flowdirection` (`LeftToRight` | `RightToLeft`) sets the widget's
content direction for that language; `meta.name` is the display name. `id` on each `<unit>`
is the key.

### Using it in code

Derive `NativeWidgetControl` and call the protected helpers. **`Get` always takes a default
value** — returned when the key is missing or no `langs/` is attached, so a widget never
shows a blank or a `[key]`:

```csharp
_title.ToolTip = L("GoToToday", "Go to today");                 // args formatted with the current culture
var n = LPlural("Items", count, "{0} items");                   // CLDR plural category for the active language
```

### Using it in loose XAML

```xml
<TextBlock Text="{sdk:Loc GoToToday}" />
```

Add `xmlns:sdk="clr-namespace:Maktaby.WidgetSdk;assembly=Maktaby.WidgetSdk"`. The extension
resolves the widget's localized string and re-translates live when the app language changes.

### FlowDirection (opt-in)

`ContentFlowDirection` returns the widget's direction: its own `meta.flowdirection` when the
active `.xlf` declares one, otherwise the host app language's direction. A widget that wants
its content mirrored applies it to its root visual (the base class does this automatically
for RTL languages). Widgets with no `langs/` simply follow the host language and show their
built-in strings.

### Re-applying code-built strings on a language switch

Strings set in the constructor (tooltips, labels) are resolved once. To re-apply them when
the language changes, override `OnLanguageChanged()`:

```csharp
protected override void OnLanguageChanged()
{
    _title.ToolTip = L("GoToToday", "Go to today");
}
```

### Rules

- Lookup: active language → the widget's `en.xlf` → your default value. A missing language
  or key never blocks loading.
- A widget with no `langs/` at all still loads and follows the host language.
- Built-in reference: `NativeWidgets/CalendarWidget` (on-visual tooltips) and
  `NativeWidgets/NativeClock` (settings labels).
