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

Because NitTray installs per-user, that helper sits in a folder the signed-in user
can write to without elevating. Before launching it, NitTray opens the helper with
write and delete sharing denied so it cannot be swapped while the UAC prompt is up,
then checks it carries a valid Authenticode signature whose public key matches the
one that signed NitTray. The key is read at startup, and the key rather than the
certificate name is compared because a name can be forged. Builds from source are
unsigned and have no key to match, so setup continues with a warning in the log.

This raises the bar against a replaced helper rather than sealing the folder.
Rewriting `NitTray.exe` itself, or planting a DLL the helper imports, stays possible
until the app no longer installs an auto-elevating binary somewhere user-writable.

### Diagnostics

When no display is detected, right-click the tray icon and select **Open
Diagnostics Log**. The log (`%LOCALAPPDATA%\NitTray\diagnostic.log`) records every
enumerated HID device, each probe attempt, the full HID capability map for every
Apple-vendor interface, and the initial brightness read (raw value, range, and
resulting percentage). Attaching it to a GitHub issue is usually enough to
identify a new display variant.

The log is rewritten from scratch at the start of each scan, so it always describes
the most recent one. Writes between scans (driver setup, hotkey registration,
brightness failures) are capped at 1 MB — past that the file restarts rather than
growing without limit.

## Global brightness shortcuts

NitTray registers two system-wide shortcuts that step every connected display by
10%. They are opt-in (**tray → Settings**) and stored in
`%LOCALAPPDATA%\NitTray\settings.json` beside the diagnostic log:

```json
{
  "BrightnessHotKeysEnabled": true,
  "BrightnessUpHotKey": "Win+Ctrl+Up",
  "BrightnessDownHotKey": "Win+Ctrl+Down"
}
```

### How it works

`RegisterHotKey` binds the combinations to the main window's HWND — the same
window that already receives `WM_DEVICECHANGE`, so the shortcuts keep working
while the window is hidden in the tray. `WM_HOTKEY` is dispatched through an
`HwndSource` hook, exactly like the device-change watch.

- **`MOD_NOREPEAT` is set**, so one press is one 10% step. Without it a held key
  auto-repeats around 30 times a second, which at this step size would cross the
  whole range in about a third of a second. Rapid distinct presses are still safe:
  `DisplayViewModel` coalesces writes to the most recent value, so the device never
  sees a backlog of feature reports.
- **Registration failures are surfaced, not swallowed.** `RegisterHotKey`
  returning `ERROR_HOTKEY_ALREADY_REGISTERED` (1409) becomes an inline warning
  naming the combination; anything else is logged with its Win32 error.
- **Recording a shortcut suspends the live ones.** Windows delivers a registered
  combination as `WM_HOTKEY`, so it would never arrive at the settings window as a
  key press — the registrations are released while the user types and restored
  afterwards.
- **The overlay is required, not decorative.** With the window in the tray there
  is no other feedback, so a click-through, never-activated window
  (`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT`) reports the new
  level on the primary monitor and fades out.

### The settings window

`SettingsWindow` is the app's only secondary window. It hosts a `NavigationView`
in `Top` mode — two tabs, **Shortcuts** and **Support**, both in `MenuItems`. A
left rail would reserve a fixed column of mostly empty space for two entries, and
`FooterMenuItems` would strand Support at the opposite end of the bar rather than
beside its sibling. The tray's **Settings** item and the main window's footer
link both open it on Shortcuts; Support is a tab away, so neither the tray nor the
window needs its own entry for it.

Two things about `Top` mode are easy to miss, and both come straight from
[`NavigationViewTop.xaml`](https://github.com/lepoco/wpfui/blob/4.3.0/src/Wpf.Ui/Controls/NavigationView/NavigationViewTop.xaml):
it insets content with `Padding`, ignoring the `FrameMargin` the `Left` template
uses, and it does not hide the back button — only `LeftFluent` does that
automatically, so `IsBackButtonVisible` has to be set explicitly.

It is deliberately **not** an owned window. `Owner` would give free
centre-on-parent placement, but an owned window sits permanently above its owner
in the z-order, so settings could never be pushed behind the main window even
when the main window was the one clicked. `App.PlaceSettingsWindow` centres it by
hand instead, and it keeps a taskbar button so there is a way back to it once it
is behind something.

Pages live in [`Pages/`](Pages) and set their own `DataContext`, because
`NavigationView` constructs them itself and cannot be handed an instance.
`ShortcutsPage` therefore pulls the shared `SettingsViewModel` off `App` — it has
to be the same instance the window's key recorder drives, since that one owns the
live registrations. Navigation deliberately passes no `dataContext`;
WPF-UI's activator only overwrites `DataContext` when it is given a non-null one.

Both pages use the same layout: a centred column capped at the old About
window's content width, with rules between sections rather than cards.
`AboutPage` — the **Support** tab, still named for the window it replaced — kept
that layout when it stopped being a window, and Shortcuts follows it so the two
read as one window instead of two. The window is sized around that column,
not the other way round.

Neither page wraps itself in a `ScrollViewer`.
[`NavigationViewContentPresenter`](https://github.com/lepoco/wpfui/blob/4.3.0/src/Wpf.Ui/Controls/NavigationView/NavigationViewContentPresenter.cs)
already hosts every `Page` inside a `DynamicScrollViewer`, and nesting a plain
`ScrollViewer` inside it breaks the mouse wheel: WPF's `ScrollViewer` marks the
wheel event handled even when it has nothing to scroll, so the outer one never
receives it. The scrollbar still drags, which makes it look like a wheel problem
rather than a layout one. WPF-UI's `PassiveScrollViewer` exists to work around
exactly this, and the presenter's scroller derives from it.

The key recorder stays on the window rather than the page so it fires wherever
focus sits, and capture is cancelled on page change, deactivation, and close so
it can never strand the suspended registrations.

### Choosing defaults

`Win + Ctrl + Up` / `Win + Ctrl + Down` are the defaults because Windows leaves
them unassigned — `Win + Ctrl + Left/Right` are the virtual-desktop switches, but
the vertical pair is free. Combinations that were ruled out:

| Combination | Already means |
|-------------|---------------|
| `Win + ↑/↓` | Maximize / minimize |
| `Win + Shift + ↑/↓` | Stretch vertically / restore |
| `Win + Alt + ↑/↓` | Snap to top / bottom half (Windows 11 22H2+) |
| `Ctrl + Alt + arrows` | Intel Graphics screen rotation |
| `Ctrl + Shift + arrows` | Text selection in practically every editor |
| `Win + Plus/Minus` | Magnifier zoom |
| `Win + letter`, `Win + Alt + letter` | Reserved by Windows / Xbox Game Bar |

If you rebind, prefer a combination with at least two modifiers. NitTray requires
`Ctrl`, `Alt` or `Win` to be part of the shortcut — a bare key, or `Shift` plus a
key, would swallow ordinary typing and text selection system-wide.

Note that WPF's `Keyboard.Modifiers` never reports the Windows key, so the
recorder probes `Key.LWin`/`Key.RWin` directly; using `ModifierKeys.Windows` would
silently record `Win + Ctrl + Up` as plain `Ctrl + Up`.

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

The version shown on the Support tab is read from the assembly's
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
- **A brightness shortcut does nothing.** Windows does not deliver global
  shortcuts to ordinary applications while a window running as administrator (or
  an exclusive-fullscreen game) has focus; NitTray runs `asInvoker`, so this is
  expected. If a shortcut never works, open **tray → Settings** — a combination
  another application already owns is reported there, and the diagnostic log
  records every registration attempt.
- **"Windows protected your PC" on first launch.** NitTray is code-signed (Azure
  Trusted Signing), but a new application has not yet established a Microsoft
  SmartScreen reputation, and downloaded files also carry the *Mark of the Web*,
  so SmartScreen or Smart App Control may still warn. Select **More info → Run
  anyway**, or right-click the download, choose **Properties**, and enable
  **Unblock**. A locally built copy (`dotnet run --project src/app`) carries no
  Mark of the Web and does not trigger the warning.
