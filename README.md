# Desktop Boxes

<p align="center">
  <!-- Placeholder: replace with a real banner/screenshot -->
  <img src=".github/assets/banner.png" alt="Desktop Boxes — organize your desktop into resizable boxes" width="640" />
</p>

Organize your Windows desktop into **resizable, snap-able boxes** that pin your files, folders and app shortcuts above the desktop — so everything stays where you put it.

---

## ✨ Features

| | |
|---|---|
| 📦 **Boxes** | Group shortcuts, files and folders into resizable containers that live on your desktop. |
| 🖱️ **Drag & Drop** | Drag files from Explorer, the Start Menu or another Box onto empty space to create a new Box automatically. |
| 🎨 **Theming** | Dark / Light / follow-system; per-box transparency, colors and border overrides. |
| 📐 **Snap & Guides** | Boxes snap to each other and to screen edges with visual guide lines. |
| 🔄 **Roll-up** | Roll any box into its title-bar strip (Top / Bottom / Left / Right) to reclaim space. |
| 👁️ **Hide Desktop Icons** | Toggle Windows desktop icons off/on from the tray menu for a clean look. |
| 🏷️ **Tabs** | Multi-tab containers let you group even more items behind a single box. |
| 🚀 **Startup** | Optional launch on Windows startup (tray toggle). |
| 🖥️ **Multi-Monitor** | Primary-monitor surface with DPI-aware rendering. |

## 📸 Screenshots

<!-- Placeholder screenshots — replace with actual captures -->

### Box with items
<p align="center">
  <img src=".github/assets/screenshot-box.png" alt="A box containing app shortcuts on the desktop" width="480" />
</p>

### Marquee → Create New Box
<p align="center">
  <img src=".github/assets/screenshot-marquee.png" alt="Dragging a marquee on empty space shows the Create New Box menu" width="480" />
</p>

### Tray menu
<p align="center">
  <img src=".github/assets/screenshot-tray.png" alt="Tray icon context menu" width="320" />
</p>

### Settings
<p align="center">
  <img src=".github/assets/screenshot-settings.png" alt="Settings window with Appearance tab" width="480" />
</p>

## 🚀 Getting Started

### Prerequisites
- Windows 10 (19041+) or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Build & Run

```powershell
git clone https://github.com/ezhassen/DesktopBoxes.git
cd DesktopBoxes
dotnet run --project src/DesktopBoxesUI
```

### Create the Installer

```powershell
# Publish + build installer (requires Inno Setup 6 in PATH)
.\build.ps1 -Action Publish
```

## 🏷️ Versioning & Releases

Versions are computed automatically from git tags via [MinVer](https://github.com/adamralph/minver):

```bash
# Tag a release
git tag -a v1.2.0 -m "1.2.0"

# Tag a beta from develop
git tag -a v1.2.0-beta.1 -m "beta"

# Or use the helper script
.\build.ps1 -Action Tag -Version 1.2.0-beta.1
```

The resulting installer is named accordingly: `Desktop Boxes 1.2.0-beta.1.exe`

## 🗂️ Project Structure

```
src/
├── DesktopBoxesUI/           # Main WPF application
│   ├── Controls/             # Reusable controls (BoxControl, TrayIconUI)
│   ├── Core/                 # Models, interfaces, pure .NET services
│   ├── Converters/           # Value converters
│   ├── Resources/            # Styles, brushes
│   ├── Services/             # Application services (dialog, icon image)
│   ├── Settings/             # User settings model
│   ├── Shell/                # Shell COM interop & services
│   ├── ViewModels/           # MVVM view models
│   ├── Views/                # Windows (surface, settings, about, …)
│   └── Win32APIs/            # Win32 P/Invoke (CsWin32-generated + manual)
└── DesktopBoxes.slnx         # Solution file
build.ps1                     # Build / publish / tag orchestrator
installer.iss                 # Inno Setup installer script
```

## ⌨️ Shortcuts

| Gesture | Action |
|---|---|
| Double-click empty desktop | Toggle hide/show all boxes |
| Ctrl+Wheel on desktop | Change icon size |
| Right-click box header | Box menu (delete, hide, lock, roll, tabs) |
| Alt+Enter on item | Show properties |

## 🤝 Contributing

1. Fork the repo
2. Create a feature branch (`git checkout -b feature/my-feature`)
3. Commit your changes
4. Push and open a Pull Request
