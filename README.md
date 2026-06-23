# DisplayDial

A Windows tray app for controlling **Apple display brightness** without needing
a Mac. It talks to the display over the same USB HID feature report that macOS
uses internally — no DDC/CI, no kernel driver, no admin rights for day-to-day
brightness control. (The Pro Display XDR needs a one-time WinUSB driver install,
which DisplayDial does for you with a single admin prompt.)

> **Supported:** Apple Studio Display (`0x1114`), Studio Display Gen 2 (`0x1118`),
> Studio Display XDR (`0x1116`), **Apple Pro Display XDR (`0x9243`)**, plus any
> other Apple HID display that advertises the standard Monitor/Brightness usage.

## Features

- Lives in the system tray (sun icon). Double-click to show, right-click for
  Show / Refresh / Quit.
- Window lists every connected Apple display with its current brightness.
- Drag the slider — brightness updates in real time over USB HID.
- Closing the main window keeps the app in the tray; pick **Quit** to exit.
- **Self-detects each display's capabilities**: the brightness HID interface,
  report ID, and raw min/max range are all read from the device's HID
  descriptor at enumeration time. No hard-coded `MI_07`, no hard-coded `60000`.
- **One-click Pro Display XDR setup**: when a Pro Display XDR needs the WinUSB
  driver, DisplayDial shows a **Set up display** button that installs it in-app
  (via [libwdi](https://github.com/pbatard/libwdi), the engine behind Zadig) —
  one UAC prompt, no separate tools to download.
- **Per-display driver uninstall**: a display whose WinUSB driver DisplayDial
  installed (the Pro Display XDR) gets a **⋯ overflow menu** on its card with an
  **Uninstall driver** action that reverts it to the default Windows driver (one
  UAC prompt). Studio Displays use the Windows in-box HID driver, so they don't
  show the menu.

## How the protocol works

Apple displays do **not** expose DDC/CI. Instead they ship a USB HID control
interface (the Studio Display puts it on USB interface `MI_07`; the Pro
Display XDR exposes it as one of four HID interfaces under PID `0x9243`) that
accepts an Apple-specific feature report on the same USB-C / Thunderbolt cable
that carries video. DisplayDial sends and reads the same report macOS does.

### USB identification

| Display                | VID    | PID     | Brightness range (raw) | Interface             |
|------------------------|--------|---------|------------------------|------------------------|
| Apple Studio Display   | 0x05AC | 0x1114  | 400 – 60000            | `MI_07`               |
| Studio Display Gen 2   | 0x05AC | 0x1118  | 400 – 60000            | `MI_07`               |
| Studio Display XDR     | 0x05AC | 0x1116  | 400 – 60000            | `MI_07&col01`         |
| **Apple Pro Display XDR** | 0x05AC | **0x9243** | **400 – 50000**     | one of 4 HID interfaces |

### HID feature report (Report ID `0x01`)

```
offset  size            field
   0     1              Report ID (0x01)
   1     4              Brightness, little-endian uint32
   5     padding        zeroed; total length = FeatureReportByteLength
```

Same buffer is used for `HidD_GetFeature` (read) and `HidD_SetFeature` (write).

### Detection strategy

Rather than hard-coding interface numbers or brightness ranges, DisplayDial uses
the HID parser to ask each Apple HID interface what it exposes:

1. Enumerate every HID device whose path contains `vid_05ac`.
2. Open it (`CreateFile` with read/write + shared access — no admin needed).
3. `HidD_GetPreparsedData` → `HidP_GetCaps` → `HidP_GetValueCaps`.
4. Pick the feature value cap that matches one of:
   - **UsagePage `0x0082` (Monitor) + Usage `0x0010` (Brightness)** — Studio Display family
   - **UsagePage `0x8005` + Usage `0x1009`** — Apple vendor page used by the **Pro Display XDR**
   - Fallback: any 32-bit single-value feature cap with `LogicalMax >= 400`
5. The chosen cap supplies the report ID, raw min/max, and the descriptor
   supplies the feature report length.
6. Read/write that interface's feature report.

That's why the Pro Display XDR works even though its USB layout (4 HID
interfaces, Apple-vendor usage `0x8005/0x1009`, max brightness `0xC350` =
50 000) is different from the Studio Display family — the descriptor tells us
everything we need.

### Diagnostics

If no display is detected, right-click the tray icon and choose
**"Open diagnostics log…"**. The log (at
`%LOCALAPPDATA%\DisplayDial\diagnostic.log`) records every HID device that was
enumerated, every probe attempt, and the full HID capability map for each
Apple-vendor interface. Share it on a GitHub issue and we can usually identify
a new display variant in minutes.

### Cross-references

The protocol was reverse-engineered and proven by several community projects.
DisplayDial's behaviour matches them byte-for-byte:

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
dotnet run --project src/DisplayDial
```

> **Pro Display XDR support** also needs the native WinUSB installer helper
> (`DisplayDial.DriverSetup.exe`). It is built separately — run
> `native/DisplayDial.DriverSetup/build.ps1` on Windows (Visual Studio 2022 or
> 2026 with the *Desktop development with C++* workload + the **v143 x64**
> toolset; **no WDK or ARM64 tools needed**). The single x64 helper this produces
> runs on x64 Windows; for a **Windows on ARM** release, add the *MSVC v143 - ARM64
> build tools* component and run `build.ps1 -SupportArm64` to produce one universal helper
> that serves both x64 and ARM64 (no per-architecture bundling). The build copies
> the helper next to the app so the **Set up display** button can find it. The
> Studio Display family works without this helper. See
> [`native/DisplayDial.DriverSetup/README.md`](native/DisplayDial.DriverSetup/README.md).

Or publish a single-folder framework-dependent build:

```powershell
dotnet publish src/DisplayDial -c Release -r win-x64 --self-contained false -o publish
.\publish\DisplayDial.exe
```

For a fully self-contained build (no .NET runtime needed on the target):

```powershell
dotnet publish src/DisplayDial -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish-standalone
```

The project's `EnableWindowsTargeting=true` lets you also build (but not run)
the app from macOS or Linux for CI.

## Project layout

```
src/DisplayDial/
  App.xaml / App.xaml.cs          - WPF app entry, owns tray + window lifecycle
  MainWindow.xaml / .cs           - displays + sliders UI
  Tray/TrayIconHost.cs            - WinForms NotifyIcon wrapper
  ViewModels/
    MainViewModel.cs              - observable list of displays + refresh command
    DisplayViewModel.cs           - per-display brightness with debounced writes
    RelayCommand.cs               - minimal ICommand
  Services/
    IDisplayService.cs            - abstraction
    StudioDisplayService.cs       - HID + WinUSB enumeration and read/write
    IDriverInstallService.cs      - abstraction for the WinUSB installer
    WinUsbDriverInstallService.cs - launches the elevated helper (UAC)
    DriverInstallResult.cs        - install status + message
    DriverSetupExitCodes.cs       - exit-code contract shared with the helper
    IconFactory.cs                - generates the sun tray icon at runtime
    Native/
      HidNative.cs                - hid.dll + HidP_* parser P/Invoke
      SetupApiNative.cs           - setupapi.dll P/Invoke
      WinUsbNative.cs             - winusb.dll P/Invoke (Pro Display XDR)
      Kernel32Native.cs           - kernel32.dll CreateFile / CloseHandle
      HidDeviceSafeHandle.cs      - SafeHandle wrapper
  Models/
    StudioDisplayInfo.cs          - immutable device descriptor + HID caps
    DisplayEnumerationResult.cs   - displays + pending driver setups
    PendingDriverSetup.cs         - a display present but not WinUSB-bound yet

native/DisplayDial.DriverSetup/   - elevated WinUSB installer (C + libwdi),
                                    built separately on Windows (see its README)
```

## Troubleshooting

- **No displays found:** Some USB-C docks and adapters strip the HID interface
  while passing video through. Try a direct USB-C connection from PC to display.
  Then right-click the tray icon → **Open diagnostics log…** and inspect
  (or share) the log at `%LOCALAPPDATA%\DisplayDial\diagnostic.log`. It lists
  every Apple-vendor HID interface seen and why each one was or wasn't picked
  as the brightness control.
- **Pro Display XDR with a yellow warning (Code 10) in Device Manager:**
  Windows' built-in HID driver doesn't understand Apple's HID descriptor for
  this display, so brightness has to go through a WinUSB driver instead.
  DisplayDial installs it for you — click **Set up display** in the app (see
  **Pro Display XDR setup** below).
- **Permission denied:** None expected on Windows — `CreateFile` with
  `GENERIC_READ | GENERIC_WRITE` and shared access works for normal users.
- **Pro Display XDR shows up multiple times:** shouldn't happen — DisplayDial
  deduplicates by serial number, falling back to PID if a serial isn't
  reported. File an issue with the HID device paths if you see duplicates.
- **Slider snaps to an integer percent:** intentional. The raw range is
  per-device (`400..60000` or `400..50000`) and we round to integer percent.

## Pro Display XDR setup (Windows)

The Apple Pro Display XDR ships an HID descriptor that Windows' built-in HID
driver (`hidclass.sys`) cannot parse — when you connect the display directly,
Device Manager shows its **USB Input Device** (`VID_05AC` `PID_9243`) with a
yellow warning and **Code 10 ("This device cannot start")**. Until the driver
is replaced, no application on Windows — not just DisplayDial — can talk to the
brightness interface.

The fix is to bind the Microsoft-provided **WinUSB** driver to the whole device.
DisplayDial does this for you:

1. Connect the Pro Display XDR. DisplayDial detects it and shows a banner with a
   **Set up display** button.
2. Click **Set up display** and approve the single Windows permission (UAC)
   prompt.
3. Wait ~10–20 seconds. DisplayDial installs WinUSB, re-scans, and the display
   appears with a working brightness slider.

Under the hood this runs a small elevated helper
(`DisplayDial.DriverSetup.exe`) that uses
[libwdi](https://github.com/pbatard/libwdi) — the same engine Zadig uses — to
generate and install a self-signed WinUSB driver package. You only need to do
this once per machine. If the install fails, DisplayDial shows the error and
writes details to `%LOCALAPPDATA%\DisplayDial\driver-setup.log`.

The Apple Studio Display family does **not** need this step — its HID descriptor
is well-formed and Windows binds it correctly out of the box.

> **Note:** the helper is a native (C + libwdi) component compiled separately on
> Windows; see
> [`native/DisplayDial.DriverSetup/README.md`](native/DisplayDial.DriverSetup/README.md).
> If `DisplayDial.DriverSetup.exe` isn't bundled next to the app, the **Set up
> display** button reports that the helper is missing.

## License

**GPLv3** — see [`LICENSE`](LICENSE).

DisplayDial bundles and (statically) links
[libwdi](https://github.com/pbatard/libwdi), which is licensed LGPLv3/GPLv3. To
keep the combined work's licensing clean, the whole project is released under the
GNU General Public License v3.0. You are free to use, study, modify, and
redistribute it under those terms.
