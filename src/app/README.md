# NitTray — Technical Design

This document describes how NitTray communicates with Apple displays, how it
detects them, and how to build the project. For downloads and general usage, see
the [main README](../../README.md). The elevated WinUSB installer helper is
documented separately in [`src/driver/README.md`](../driver/README.md).

## Protocol overview

Apple displays do not expose DDC/CI. Instead, they present a USB HID control
interface — the Studio Display exposes it on USB interface `MI_07`, while the Pro
Display XDR exposes it as one of several HID interfaces under PID `0x9243` — that
accepts an Apple-specific feature report over the same USB-C / Thunderbolt cable
that carries video. NitTray issues the same feature reports macOS uses
internally, and requires no DDC/CI, no kernel driver, and no administrator rights
for routine brightness control.

### USB identification

| Display                          | VID    | PID    | Brightness range (raw) | Interface                     |
|----------------------------------|--------|--------|------------------------|-------------------------------|
| Studio Display                   | 0x05AC | 0x1114 | 400 – 60000            | `MI_07`                       |
| Studio Display (2nd generation)  | 0x05AC | 0x1118 | 400 – 60000            | `MI_07`                       |
| Studio Display XDR               | 0x05AC | 0x1116 | 400 – 60000            | `MI_07&col01`                 |
| Pro Display XDR                  | 0x05AC | 0x9243 | 400 – 50000            | one of several HID interfaces |

### HID feature report (Report ID `0x01`)

```
offset  size   field
   0     1     Report ID (0x01)
   1     4     Brightness — uint32, little-endian
   5     ...   remaining bytes, up to FeatureReportByteLength
```

The same buffer is used for `HidD_GetFeature` (read) and `HidD_SetFeature`
(write), and raw values map linearly to the slider percentage across each
device's own minimum/maximum range.

On the Studio Display family (native HID), bytes after the brightness value are
zero-padded on write. On WinUSB models such as the Pro Display XDR, the report is
seven bytes and bytes 5–6 hold a separate volatile value; NitTray preserves it
with a read-modify-write so that setting brightness does not disturb unrelated
device state.

### Detection strategy

Rather than hard-coding interface numbers or brightness ranges, NitTray queries
each Apple HID interface through the HID parser:

1. Enumerate every HID device whose device path contains `vid_05ac`.
2. Open it with `CreateFile` (read/write, shared access — no administrator rights
   required).
3. Retrieve its capabilities: `HidD_GetPreparsedData` → `HidP_GetCaps` →
   `HidP_GetValueCaps`.
4. Select the feature value capability that matches, in order of preference:
   - **Usage Page `0x0082` (Monitor), Usage `0x0010` (Brightness)** — used by the
     Studio Display family and the Pro Display XDR brightness interface.
   - **Usage Page `0x8005`, Usage `0x1009`** — an Apple vendor page accepted as a
     fallback for future displays.
   - Any single-value, 32-bit feature capability whose `LogicalMax` is at least
     400 — a last resort for an unrecognized vendor usage.
5. The selected capability provides the report ID and raw minimum/maximum; the
   descriptor provides the feature-report length.
6. Read from and write to that interface's feature report.

Because the interface, report ID, and raw range are all read from the device
descriptor at enumeration time, no interface number (`MI_07`) or brightness range
(`60000`) is hard-coded.

### Pro Display XDR over WinUSB

The Pro Display XDR is handled differently. Windows' in-box HID driver
(`hidclass.sys`) cannot parse its HID descriptor, so when the display is
connected directly it appears in Device Manager with a yellow warning and
**Code 10 ("This device cannot start")**. Until the driver is replaced, no
Windows application — not only NitTray — can reach its brightness interface.

NitTray binds the Microsoft-provided **WinUSB** driver to the entire composite
device using [libwdi](https://github.com/pbatard/libwdi) (the engine behind
Zadig), then locates the brightness interface by the same Monitor/Brightness
usage (`0x0082` / `0x0010`) within the raw HID report descriptor. Although the
Pro Display XDR's layout differs — several HID interfaces, and a maximum
brightness of `0xC350` (50000) — the descriptor still provides everything
required. The one-time installation runs an elevated helper
(`NitTray.DriverSetup.exe`) behind a single UAC prompt; see
[`src/driver/README.md`](../driver/README.md).

### Diagnostics

When no display is detected, right-click the tray icon and select **Open
Diagnostics Log**. The log (`%LOCALAPPDATA%\NitTray\diagnostic.log`) records every
enumerated HID device, each probe attempt, the full HID capability map for every
Apple-vendor interface, and the initial brightness read (raw value, range, and
resulting percentage). Attaching it to a GitHub issue is usually enough to
identify a new display variant.

## Building

```powershell
# from the repository root, on Windows
dotnet build -c Release
dotnet run --project src/app
```

> **Pro Display XDR support** additionally requires the native WinUSB installer
> helper (`NitTray.DriverSetup.exe`), which is built separately. Run
> `src/driver/build.ps1` on Windows (Visual Studio 2022 or later with the
> *Desktop development with C++* workload and the **v143 x64** toolset; no WDK or
> ARM64 tools are required). For a **Windows on ARM** release, add the *MSVC v143
> - ARM64 build tools* component and run `build.ps1 -SupportArm64` to produce a
> single universal helper that serves both x64 and ARM64. The build copies the
> helper next to the application so the **Set up display** button can locate it.
> The Studio Display family does not require this helper. See
> [`src/driver/README.md`](../driver/README.md).

To produce a self-contained, single-file build (no .NET runtime required on the
target machine):

```powershell
dotnet publish src/app -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish-standalone
```

The version shown in the About window is read from the assembly's
`InformationalVersion`. Local builds default to `0.0.0-local`; release builds set
`-p:Version` from the git tag (see
[`.github/workflows/release.yml`](../../.github/workflows/release.yml)).

`EnableWindowsTargeting=true` allows the Windows-targeted project to be built
(though not run) on macOS or Linux for CI.

## Troubleshooting

- **No displays found.** Some USB-C docks and adapters pass video through while
  stripping the HID interface. Connect the display directly to the PC with a
  USB-C cable, then review the diagnostic log (**tray → Open Diagnostics Log**, or
  `%LOCALAPPDATA%\NitTray\diagnostic.log`), which lists every Apple-vendor HID
  interface and why each was or was not selected as the brightness control.
- **Pro Display XDR shows a yellow warning (Code 10).** Windows' in-box HID driver
  cannot interpret Apple's HID descriptor, so brightness must be driven through
  WinUSB. NitTray installs the driver for you — click **Set up display**.
- **Permission denied.** This is not expected: `CreateFile` with
  `GENERIC_READ | GENERIC_WRITE` and shared access succeeds for standard users.
- **A display appears more than once.** This should not occur — NitTray
  de-duplicates by serial number, falling back to product ID when no serial is
  reported. If you observe duplicates, please file an issue with the HID device
  paths.
- **The slider snaps to whole percentages.** This is intentional. The raw range is
  device-specific (for example `400–60000` or `400–50000`) and is rounded to an
  integer percentage.
- **"Windows protected your PC" on first launch.** NitTray is code-signed (Azure
  Trusted Signing), but a new application has not yet established a Microsoft
  SmartScreen reputation, and downloaded files also carry the *Mark of the Web*,
  so SmartScreen or Smart App Control may still warn. Select **More info → Run
  anyway**, or right-click the download, choose **Properties**, and enable
  **Unblock**. A locally built copy (`dotnet run --project src/app`) carries no
  Mark of the Web and does not trigger the warning.
