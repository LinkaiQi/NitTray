# NitTray — technical design

How the NitTray tray app talks to Apple displays, how it detects them, and how
to build it. For downloads and everyday use, see the [main README](../../README.md).
The elevated WinUSB installer helper has its own [`src/driver/README.md`](../driver/README.md).

## How the protocol works

Apple displays do **not** expose DDC/CI. Instead they ship a USB HID control
interface (the Studio Display puts it on USB interface `MI_07`; the Pro Display
XDR exposes it as one of four HID interfaces under PID `0x9243`) that accepts an
Apple-specific feature report on the same USB-C / Thunderbolt cable that carries
video. NitTray sends and reads the same report macOS does — no DDC/CI, no kernel
driver, and no admin rights for day-to-day brightness control.

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

The same buffer is used for `HidD_GetFeature` (read) and `HidD_SetFeature`
(write). Raw values map to the slider percentage linearly across each device's
own min/max range.

### Detection strategy

Rather than hard-coding interface numbers or brightness ranges, NitTray uses the
HID parser to ask each Apple HID interface what it exposes:

1. Enumerate every HID device whose path contains `vid_05ac`.
2. Open it (`CreateFile` with read/write + shared access — no admin needed).
3. `HidD_GetPreparsedData` → `HidP_GetCaps` → `HidP_GetValueCaps`.
4. Pick the feature value cap that matches one of:
   - **UsagePage `0x0082` (Monitor) + Usage `0x0010` (Brightness)** — the Studio
     Display family *and* the Pro Display XDR brightness interface
   - **UsagePage `0x8005` + Usage `0x1009`** — an Apple vendor page accepted only
     as a fallback for a future display
   - Fallback: any 32-bit single-value feature cap with `LogicalMax >= 400`
5. The chosen cap supplies the report ID and raw min/max; the descriptor supplies
   the feature report length.
6. Read/write that interface's feature report.

So the brightness HID interface, report ID, and raw min/max range are all read
from the device's HID descriptor at enumeration time — no hard-coded `MI_07`, no
hard-coded `60000`.

### Pro Display XDR over WinUSB

The Pro Display XDR is reached differently. Windows' in-box HID driver
(`hidclass.sys`) cannot parse its HID descriptor, so when connected directly it
appears in Device Manager with a yellow warning and **Code 10 ("This device
cannot start")**. Until the driver is replaced, no application on Windows — not
just NitTray — can talk to its brightness interface.

NitTray binds the Microsoft-provided **WinUSB** driver to the whole composite
device (via [libwdi](https://github.com/pbatard/libwdi), the engine behind
Zadig), then finds the brightness interface by the same Monitor/Brightness usage
(`0x0082`/`0x0010`) in its raw HID report descriptor. Its layout differs (several
HID interfaces, max brightness `0xC350` = 50000), but the descriptor still tells
us everything we need. The one-time install runs an elevated helper
(`NitTray.DriverSetup.exe`) behind a single UAC prompt — see
[`src/driver/README.md`](../driver/README.md).

### Diagnostics

If no display is detected, right-click the tray icon and choose **Open
Diagnostics Log**. The log (at `%LOCALAPPDATA%\NitTray\diagnostic.log`) records
every HID device that was enumerated, every probe attempt, the full HID
capability map for each Apple-vendor interface, and the initial brightness read
(raw value, range, and resulting %). Share it on a GitHub issue and a new display
variant can usually be identified in minutes.

## Build and run

```powershell
# from the repo root, on Windows
dotnet build -c Release
dotnet run --project src/app
```

> **Pro Display XDR support** also needs the native WinUSB installer helper
> (`NitTray.DriverSetup.exe`), built separately — run `src/driver/build.ps1` on
> Windows (Visual Studio 2022/2026 with the *Desktop development with C++*
> workload + the **v143 x64** toolset; no WDK or ARM64 tools needed). For a
> **Windows on ARM** release, add the *MSVC v143 - ARM64 build tools* component
> and run `build.ps1 -SupportArm64` to produce one universal helper that serves
> both x64 and ARM64. The build copies the helper next to the app so the **Set up
> display** button can find it. The Studio Display family works without this
> helper. See [`src/driver/README.md`](../driver/README.md).

Publish a self-contained, single-file build (no .NET runtime needed on the
target):

```powershell
dotnet publish src/app -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish-standalone
```

The version shown in the About box comes from the assembly's
`InformationalVersion`. Local builds default to `0.0.0-local`; release builds
pass `-p:Version` derived from the git tag (see
[`.github/workflows/release.yml`](../../.github/workflows/release.yml)).

The project's `EnableWindowsTargeting=true` lets you also build (but not run) the
app from macOS or Linux for CI.

## Project layout

```
src/app/
  App.xaml / App.xaml.cs          - WPF app entry, owns tray + window lifecycle
  MainWindow.xaml / .cs           - displays + sliders UI
  AboutWindow.xaml / .cs          - About & Support window (version, links, donate)
  Tray/
    TrayIconHost.cs               - WinForms NotifyIcon wrapper
    ModernMenuRenderer.cs         - Windows 11 Fluent-style tray menu renderer
  ViewModels/
    MainViewModel.cs              - observable list of displays + refresh command
    DisplayViewModel.cs           - per-display brightness with debounced writes
    AboutViewModel.cs             - version + supported-model list for the About window
    DisplayIdentity.cs            - formats the serial / USB-ID identity line
    RelayCommand.cs               - minimal ICommand
  Services/
    IDisplayService.cs            - abstraction
    AppleDisplayService.cs        - HID + WinUSB enumeration and read/write
                                    (partial class, split across
                                    .Enumeration / .ProXdrProbe / .Devices)
    IDriverInstallService.cs      - abstraction for the WinUSB installer
    WinUsbDriverInstallService.cs - launches the elevated helper (UAC)
    SystemRefreshTrigger.cs       - auto-rescan on device / session / power events
    SingleInstance.cs             - one instance per Windows session
    DiagnosticLog.cs              - verbose enumeration log
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
```

The elevated WinUSB installer (C + libwdi) lives in `src/driver/` and is built
separately on Windows — see its [README](../driver/README.md).

## Troubleshooting

- **No displays found:** Some USB-C docks and adapters strip the HID interface
  while passing video through. Try a direct USB-C connection from PC to display,
  then check the diagnostic log (**tray → Open Diagnostics Log**, or
  `%LOCALAPPDATA%\NitTray\diagnostic.log`). It lists every Apple-vendor HID
  interface seen and why each one was or wasn't picked as the brightness control.
- **Pro Display XDR with a yellow warning (Code 10):** Windows' built-in HID
  driver doesn't understand Apple's HID descriptor, so brightness must go through
  a WinUSB driver. NitTray installs it for you — click **Set up display**.
- **Permission denied:** None expected — `CreateFile` with
  `GENERIC_READ | GENERIC_WRITE` and shared access works for normal users.
- **Pro Display XDR shows up multiple times:** shouldn't happen — NitTray
  deduplicates by serial number, falling back to PID if a serial isn't reported.
  File an issue with the HID device paths if you see duplicates.
- **Slider snaps to an integer percent:** intentional. The raw range is
  per-device (`400..60000` or `400..50000`) and we round to integer percent.
- **"Windows can't confirm who published NitTray" / the app won't launch:** the
  binaries carry Windows' *Mark of the Web* after a download, so SmartScreen or
  Smart App Control can warn about the unsigned app. Right-click the download →
  **Properties** → tick **Unblock**, or on the blue dialog choose **More info →
  Run anyway**. A locally-built copy (`dotnet run --project src/app`) has no Mark
  of the Web and won't trip the warning.

## Cross-references

The protocol was reverse-engineered and proven by several community projects;
NitTray's behaviour matches them byte-for-byte:

- [`2yxh/BrightStudio`](https://github.com/2yxh/BrightStudio) — C# / Windows / Studio Display
- [`juliuszint/asdbctl`](https://github.com/juliuszint/asdbctl) — Rust / Linux / Studio Display
- [`jridgewell/studio-display-control`](https://github.com/jridgewell/studio-display-control) — TypeScript / libusb
- [`michaljach/win-studio-display`](https://github.com/michaljach/win-studio-display) — PowerShell
- [`0xcharly/apdbctl`](https://github.com/0xcharly/apdbctl) — C / hidapi / **Pro Display XDR**
- [`LitteRabbit-37/Studio-Brightness-PlusPlus`](https://github.com/LitteRabbit-37/Studio-Brightness-PlusPlus) — C++ / Windows / all models, full HID map
