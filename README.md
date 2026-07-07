# NitTray

**Adjust Apple display brightness from Windows — straight from the system tray.**

NitTray is a lightweight Windows tray app for controlling the brightness of an
**Studio Display**, **Studio Display XDR**, or **Pro Display XDR** without a
Mac. It talks to the display over the same USB HID feature report macOS uses
internally — no DDC/CI, no kernel driver, and no admin rights for day-to-day
brightness control. (The Pro Display XDR needs a one-time WinUSB driver install,
which NitTray does for you with a single admin prompt.)

![Platform: Windows 10/11](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6?logo=windows&logoColor=white)
![Built with .NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![UI: WPF + Fluent 2](https://img.shields.io/badge/UI-WPF%20%2B%20Fluent%202-2563EB)
![License: GPLv3](https://img.shields.io/badge/license-GPLv3-blue)

> **Supported:** Studio Display (`0x1114`), Studio Display (2nd Generation)
> (`0x1118`), Studio Display XDR (`0x1116`), **Pro Display XDR (`0x9243`)**, plus
> any other Apple HID display that advertises the standard Monitor/Brightness usage.

## Install

Grab the latest [release](https://github.com/LinkaiQi/NitTray/releases) and pick
the build that matches your PC — **x64** for most Windows PCs, **arm64** for
Windows on ARM (Snapdragon X, Surface Pro X, etc.):

- **Installer (recommended)** — `NitTray-<version>-setup-<arch>.exe`. Installs
  per-user (no admin needed), adds a Start Menu shortcut, an optional "start when
  I sign in" checkbox, and an uninstaller.
- **Portable zip** — `NitTray-<version>-win-<arch>.zip`. Extract and run
  `NitTray.exe`; keep `NitTray.DriverSetup.exe` next to it (the Pro Display XDR
  driver setup needs it).

Both bundle the WinUSB helper. Day-to-day brightness control needs no admin; the
one-time Pro Display XDR driver install shows a single UAC prompt.

## Features

- Lives in the system tray. Double-click to show; right-click for Show,
  Refresh, About, diagnostics, or Quit.
- Window lists every connected Apple display with its current brightness.
- Drag the slider — brightness updates in real time over USB HID.
- Closing the main window keeps the app in the tray; pick **Quit** to exit.
- **Self-detects each display's capabilities**: the brightness HID interface,
  report ID, and raw min/max range are all read from the device's HID
  descriptor at enumeration time. No hard-coded `MI_07`, no hard-coded `60000`.
- **One-click Pro Display XDR setup**: when a Pro Display XDR needs the WinUSB
  driver, NitTray shows a **Set up display** button that installs it in-app
  (via [libwdi](https://github.com/pbatard/libwdi), the engine behind Zadig) —
  one UAC prompt, no separate tools to download.
- **Per-display driver uninstall**: a display whose WinUSB driver NitTray
  installed (the Pro Display XDR) gets a **⋯ overflow menu** on its card with an
  **Uninstall driver** action that reverts it to the default Windows driver (one
  UAC prompt). Studio Displays use the Windows in-box HID driver, so they don't
  show the menu.

## How the protocol works

Apple displays do **not** expose DDC/CI. Instead they ship a USB HID control
interface (the Studio Display puts it on USB interface `MI_07`; the Pro
Display XDR exposes it as one of four HID interfaces under PID `0x9243`) that
accepts an Apple-specific feature report on the same USB-C / Thunderbolt cable
that carries video. NitTray sends and reads the same report macOS does.

### USB identification

| Display                          | VID    | PID        | Brightness range (raw) | Interface               |
|----------------------------------|--------|------------|------------------------|-------------------------|
| Studio Display                   | 0x05AC | 0x1114     | 400 – 60000            | `MI_07`                 |
| Studio Display (2nd Generation)  | 0x05AC | 0x1118     | 400 – 60000            | `MI_07`                 |
| Studio Display XDR               | 0x05AC | 0x1116     | 400 – 60000            | `MI_07&col01`           |
| **Pro Display XDR**              | 0x05AC | **0x9243** | **400 – 50000**        | one of 4 HID interfaces |

### HID feature report (Report ID `0x01`)

```
offset  size            field
   0     1              Report ID (0x01)
   1     4              Brightness, little-endian uint32
   5     padding        zeroed; total length = FeatureReportByteLength
```

Same buffer is used for `HidD_GetFeature` (read) and `HidD_SetFeature` (write).

### Detection strategy

Rather than hard-coding interface numbers or brightness ranges, NitTray uses
the HID parser to ask each Apple HID interface what it exposes:

1. Enumerate every HID device whose path contains `vid_05ac`.
2. Open it (`CreateFile` with read/write + shared access — no admin needed).
3. `HidD_GetPreparsedData` → `HidP_GetCaps` → `HidP_GetValueCaps`.
4. Pick the feature value cap that matches one of:
   - **UsagePage `0x0082` (Monitor) + Usage `0x0010` (Brightness)** — the Studio Display family *and* the Pro Display XDR brightness interface
   - **UsagePage `0x8005` + Usage `0x1009`** — an Apple vendor page accepted only as a fallback for a future display
   - Fallback: any 32-bit single-value feature cap with `LogicalMax >= 400`
5. The chosen cap supplies the report ID, raw min/max, and the descriptor
   supplies the feature report length.
6. Read/write that interface's feature report.

The Pro Display XDR is reached differently: Windows' in-box HID driver rejects
its brightness interface (Code 10), so NitTray drives it over **WinUSB** and
finds the brightness interface by the same Monitor/Brightness usage
(`0x0082`/`0x0010`) in its raw HID report descriptor. Its layout differs (several
HID interfaces, max brightness `0xC350` = 50 000), but the descriptor still tells
us everything we need.

### Diagnostics

If no display is detected, right-click the tray icon and choose
**"Open diagnostics log…"**. The log (at
`%LOCALAPPDATA%\NitTray\diagnostic.log`) records every HID device that was
enumerated, every probe attempt, and the full HID capability map for each
Apple-vendor interface. Share it on a GitHub issue and we can usually identify
a new display variant in minutes.

### Cross-references

The protocol was reverse-engineered and proven by several community projects.
NitTray's behaviour matches them byte-for-byte:

- [`2yxh/BrightStudio`](https://github.com/2yxh/BrightStudio) — C# / Windows / Studio Display
- [`juliuszint/asdbctl`](https://github.com/juliuszint/asdbctl) — Rust / Linux / Studio Display
- [`jridgewell/studio-display-control`](https://github.com/jridgewell/studio-display-control) — TypeScript / libusb
- [`michaljach/win-studio-display`](https://github.com/michaljach/win-studio-display) — PowerShell
- [`0xcharly/apdbctl`](https://github.com/0xcharly/apdbctl) — C / hidapi / **Pro Display XDR**
- [`LitteRabbit-37/Studio-Brightness-PlusPlus`](https://github.com/LitteRabbit-37/Studio-Brightness-PlusPlus) — C++ / Windows / all models, full HID map

## Requirements

- Windows 10 21H2 / Windows 11 (x64 or arm64)
- An Apple display connected over USB-C or Thunderbolt
- .NET 10 SDK to build, or the .NET 10 Desktop Runtime to run the framework-dependent build

No admin privileges, no kernel driver, no signed driver shenanigans — the HID
control interface is accessible to a normal user-mode process. (The one-time
Pro Display XDR WinUSB setup is the sole exception: that step prompts for admin
once, then never again — see [Pro Display XDR setup](#pro-display-xdr-setup-windows).)

## Build and run

```powershell
# from the repo root, on Windows
dotnet build -c Release
dotnet run --project src/NitTray
```

> **Pro Display XDR support** also needs the native WinUSB installer helper
> (`NitTray.DriverSetup.exe`). It is built separately — run
> `native/NitTray.DriverSetup/build.ps1` on Windows (Visual Studio 2022 or
> 2026 with the *Desktop development with C++* workload + the **v143 x64**
> toolset; **no WDK or ARM64 tools needed**). The single x64 helper this produces
> runs on x64 Windows; for a **Windows on ARM** release, add the *MSVC v143 - ARM64
> build tools* component and run `build.ps1 -SupportArm64` to produce one universal helper
> that serves both x64 and ARM64 (no per-architecture bundling). The build copies
> the helper next to the app so the **Set up display** button can find it. The
> Studio Display family works without this helper. See
> [`native/NitTray.DriverSetup/README.md`](native/NitTray.DriverSetup/README.md).

Or publish a single-folder framework-dependent build:

```powershell
dotnet publish src/NitTray -c Release -r win-x64 --self-contained false -o publish
.\publish\NitTray.exe
```

For a fully self-contained build (no .NET runtime needed on the target):

```powershell
dotnet publish src/NitTray -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish-standalone
```

The project's `EnableWindowsTargeting=true` lets you also build (but not run)
the app from macOS or Linux for CI.

## Project layout

```
src/NitTray/
  App.xaml / App.xaml.cs          - WPF app entry, owns tray + window lifecycle
  MainWindow.xaml / .cs           - displays + sliders UI
  AboutWindow.xaml / .cs          - About & Support window (version, links, donate)
  Tray/TrayIconHost.cs            - WinForms NotifyIcon wrapper
  ViewModels/
    MainViewModel.cs              - observable list of displays + refresh command
    DisplayViewModel.cs           - per-display brightness with debounced writes
    AboutViewModel.cs             - version + supported-model list for the About window
    RelayCommand.cs               - minimal ICommand
  Services/
    IDisplayService.cs            - abstraction
    AppleDisplayService.cs        - HID + WinUSB enumeration and read/write
                                    (partial class, split across
                                    .Enumeration / .ProXdrProbe / .Devices)
    IDriverInstallService.cs      - abstraction for the WinUSB installer
    WinUsbDriverInstallService.cs - launches the elevated helper (UAC)
    DriverInstallResult.cs        - install status + message
    DriverSetupExitCodes.cs       - exit-code contract shared with the helper
    IconFactory.cs                - loads the app's brand icon for the tray (DPI-aware)
    Native/
      HidNative.cs                - hid.dll + HidP_* parser P/Invoke
      SetupApiNative.cs           - setupapi.dll P/Invoke
      WinUsbNative.cs             - winusb.dll P/Invoke (Pro Display XDR)
      Kernel32Native.cs           - kernel32.dll CreateFile / CloseHandle
      HidDeviceSafeHandle.cs      - SafeHandle wrapper
  Models/
    ConnectedDisplay.cs           - immutable device descriptor + HID caps
    DisplayTransport.cs           - HID vs WinUSB transport enum
    DisplayEnumerationResult.cs   - displays + pending driver setups
    PendingDriverSetup.cs         - a display present but not WinUSB-bound yet
    Displays/                     - per-model catalog (add a file to support a display)
      DisplayCatalog.cs           - registry + Apple vendor id + lookups
      DisplayModel.cs             - a known model's identity + protocol
      BrightnessProtocol.cs       - WinUSB feature-report layout
      StudioDisplay.cs            - 0x1114
      StudioDisplayXdr.cs         - 0x1116
      StudioDisplay2ndGen.cs      - 0x1118
      ProDisplayXdr.cs            - 0x9243 (WinUSB)

native/NitTray.DriverSetup/   - elevated WinUSB installer (C + libwdi),
                                    built separately on Windows (see its README)
```

## Troubleshooting

- **No displays found:** Some USB-C docks and adapters strip the HID interface
  while passing video through. Try a direct USB-C connection from PC to display.
  Then right-click the tray icon → **Open diagnostics log…** and inspect
  (or share) the log at `%LOCALAPPDATA%\NitTray\diagnostic.log`. It lists
  every Apple-vendor HID interface seen and why each one was or wasn't picked
  as the brightness control.
- **Pro Display XDR with a yellow warning (Code 10) in Device Manager:**
  Windows' built-in HID driver doesn't understand Apple's HID descriptor for
  this display, so brightness has to go through a WinUSB driver instead.
  NitTray installs it for you — click **Set up display** in the app (see
  **Pro Display XDR setup** below).
- **Permission denied:** None expected on Windows — `CreateFile` with
  `GENERIC_READ | GENERIC_WRITE` and shared access works for normal users.
- **Pro Display XDR shows up multiple times:** shouldn't happen — NitTray
  deduplicates by serial number, falling back to PID if a serial isn't
  reported. File an issue with the HID device paths if you see duplicates.
- **Slider snaps to an integer percent:** intentional. The raw range is
  per-device (`400..60000` or `400..50000`) and we round to integer percent.
- **"Windows can't confirm who published NitTray.dll" / the app won't launch:**
  NitTray isn't code-signed, so when its files carry Windows' *Mark of the Web*
  (the flag added to anything that arrived via a download, zip extract, or cloud
  sync) SmartScreen and **Smart App Control** can warn about — or block — the
  unsigned `NitTray.dll`. A fresh rebuild has no reputation, so this can appear
  even when a previous build launched fine. Fixes, easiest first:
  1. Remove the Mark of the Web from the whole app folder, then relaunch:
     ```powershell
     Get-ChildItem -Path .\publish -Recurse | Unblock-File
     ```
     (or right-click `NitTray.exe` → **Properties** → tick **Unblock** → **OK**).
  2. If a blue **"Windows protected your PC"** dialog appears, click
     **More info → Run anyway**.
  3. Prefer launching the copy you **built locally** on that same PC — e.g.
     `dotnet run --project src/NitTray` or the `bin\Release\net10.0-windows\`
     output. Locally produced files have no Mark of the Web and usually don't trip
     the warning.
  4. If **Smart App Control** is what's blocking it (Windows Security → *App &
     browser control → Smart App Control*), an unsigned hobby build can't satisfy
     it: keep running a locally-built copy, code-sign the binaries, or — accepting
     the tradeoff — turn Smart App Control off.

## Pro Display XDR setup (Windows)

The Pro Display XDR ships an HID descriptor that Windows' built-in HID
driver (`hidclass.sys`) cannot parse — when you connect the display directly,
Device Manager shows its **USB Input Device** (`VID_05AC` `PID_9243`) with a
yellow warning and **Code 10 ("This device cannot start")**. Until the driver
is replaced, no application on Windows — not just NitTray — can talk to the
brightness interface.

The fix is to bind the Microsoft-provided **WinUSB** driver to the whole device.
NitTray does this for you:

1. Connect the Pro Display XDR. NitTray detects it and shows a banner with a
   **Set up display** button.
2. Click **Set up display** and approve the single Windows permission (UAC)
   prompt.
3. Wait ~10–20 seconds. NitTray installs WinUSB, re-scans, and the display
   appears with a working brightness slider.

Under the hood this runs a small elevated helper
(`NitTray.DriverSetup.exe`) that uses
[libwdi](https://github.com/pbatard/libwdi) — the same engine Zadig uses — to
generate and install a self-signed WinUSB driver package. You only need to do
this once per machine. If the install fails, NitTray shows the error and
writes details to `%LOCALAPPDATA%\NitTray\driver-setup.log`.

The Studio Display family does **not** need this step — its HID descriptor
is well-formed and Windows binds it correctly out of the box.

> **Note:** the helper is a native (C + libwdi) component compiled separately on
> Windows; see
> [`native/NitTray.DriverSetup/README.md`](native/NitTray.DriverSetup/README.md).
> If `NitTray.DriverSetup.exe` isn't bundled next to the app, the **Set up
> display** button reports that the helper is missing.

## License

**GPLv3** — see [`LICENSE`](LICENSE).

NitTray bundles and (statically) links
[libwdi](https://github.com/pbatard/libwdi), which is licensed LGPLv3/GPLv3. To
keep the combined work's licensing clean, the whole project is released under the
GNU General Public License v3.0. You are free to use, study, modify, and
redistribute it under those terms.

## Trademarks

Apple, Studio Display, Pro Display XDR, Mac, and macOS are trademarks of Apple
Inc., registered in the U.S. and other countries. NitTray is an independent,
unofficial project and is **not** affiliated with, endorsed by, or sponsored by
Apple Inc. Product names are used only to describe the hardware NitTray is
compatible with.
