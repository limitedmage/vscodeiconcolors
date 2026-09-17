# Workspace Color Icons

**ALL VIBE CODED, USE AT YOUR OWN RISK**

![Screenshot of multiple VS Code taskbar icons in multiple colors](./screenshot.png)

A Windows tray utility that gives each VS Code window a deterministic
color-tinted VS Code icon based on its workspace identity. For remote windows,
the identity includes the Codespace, Dev Container, SSH, WSL, or Tunnel name, so
two remote workspaces opened on folders with the same name receive different
colors.

Icons are extracted from the installed VS Code executable at the current
window's native DPI. Colors come from a curated eight-color categorical palette.
Each workspace uses a primary hue plus a blend toward a neighboring hue on the
light and dark faces of the VS Code logo.

Use in combination with utilities that let you ungroup taskbar buttons, such as
[Start11](https://www.stardock.com/products/start11/) and
[Windhawk disable grouping mod](https://windhawk.net/mods/taskbar-grouping).

## Install

Download or build `WorkspaceColorIcons.exe`, then run:

```powershell
.\WorkspaceColorIcons.exe --install
```

This starts the utility and adds shortcuts to the current user's Startup folder
and Start menu. Only one instance can run at a time.

The custom multicolor tray icon provides commands to stop or restart icon
coloring, or to exit the utility. Double-clicking it also toggles coloring.

To remove the shortcuts and stop the running instance:

```powershell
.\WorkspaceColorIcons.exe --uninstall
```

## Sleep and hibernation behavior

The utility listens for native Windows power broadcasts. Before suspend, it
restores the original VS Code icons and stops scanning. After resume, it waits
three seconds before scanning again.

All messages sent to VS Code windows use a 250 ms timeout. An unresponsive
window therefore cannot block the utility indefinitely during a sleep,
hibernate, shutdown, or resume transition.

The utility does not continuously poll the desktop. It uses native Windows
accessibility event hooks to react when top-level windows appear, disappear,
become active, or change title. Bursts of related events are combined into one
scan after a short debounce.

Detached Microsoft Edge DevTools windows receive a silver wrench badge over the
normal Edge icon. Regular Edge browser windows are not changed.

The utility should not normally be able to turn sleep or hibernation into a
shutdown. If the problem continues with the utility exited, check Windows Event
Viewer and `powercfg /systemsleepdiagnostics` for driver, firmware, or power
policy failures.

## Build

Install the .NET 8 SDK, then publish one-file executables without external
NuGet packages:

```powershell
dotnet publish .\src\WorkspaceColorIcons\WorkspaceColorIcons.csproj `
  --configuration Release `
  --runtime win-arm64 `
  --self-contained false `
  --output .\dist\win-arm64 `
  -p:PublishSingleFile=true `
  -p:EnableSingleFileAnalyzer=false `
  -p:DebugType=None `
  -p:DebugSymbols=false

dotnet publish .\src\WorkspaceColorIcons\WorkspaceColorIcons.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained false `
  --output .\dist\win-x64 `
  -p:PublishSingleFile=true `
  -p:EnableSingleFileAnalyzer=false `
  -p:DebugType=None `
  -p:DebugSymbols=false
```

Each output directory contains only `WorkspaceColorIcons.exe`. Windows
executables are architecture-specific, so ARM64 and x64 use separate files.
These builds still use the installed .NET 8 Windows Desktop runtime.

A self-contained single-file build would also bundle the runtime, but requires
the architecture-specific Microsoft runtime packages from NuGet.
