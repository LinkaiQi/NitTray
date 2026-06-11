# DisplayDial

A Windows tray app for controlling **Apple Studio Display brightness** without
needing a Mac. It talks to the display over the same USB HID feature report that
macOS uses internally — no DDC/CI, no kernel driver, no admin rights.

![Sun icon in the system tray, with a window listing each connected Studio Display and a brightness slider per display.](docs/preview.png)

> **Status:** built and tested against Apple Studio Display (PID `0x1114`). The
> code also recognises Studio Display XDR (`0x1116`) and the newer Studio
> Display revision (`0x1118`) using the same protocol.

## Features

- Lives in the system tray (sun icon). Double-click to show, right-click for
  Show / Refresh / Quit.
- Window lists every connected Apple Studio Display with its current brightness.
- Drag the slider — brightness updates in real time over USB HID.
- Closing the main window keeps the app in the tray; pick **Quit** to exit.

## How the protocol works

Apple Studio Displays do **not** expose DDC/CI. Instead they ship a USB HID
control interface (USB interface number `7`, `MI_07`) that accepts an
Apple-specific feature report on the same USB-C / Thunderbolt cable that carries
video. DisplayDial sends and reads the same report macOS does.

| Field          | Value                                                            |
|----------------|------------------------------------------------------------------|
| Vendor ID      | `0x05AC` (Apple, Inc.)                                           |
| Product IDs    | `0x1114` Studio Display · `0x1116` Studio Display XDR · `0x1118` |
| Interface      | `MI_07` (USB interface #7)                                       |
| Transport      | USB HID **feature report**                                       |
| Report ID      | `0x01`                                                           |
| Payload size   | 7 bytes                                                          |
| Layout         | `[01] [u32 little-endian brightness] [00] [00]`                  |
| Brightness     | raw `400`–`60000` → mapped linearly to `0–100%`                  |

The same 7-byte buffer is used for both `HidD_GetFeature` (read current value)
and `HidD_SetFeature` (write new value). On Windows the device is enumerated via
the SetupAPI HID class GUID and opened with `CreateFile`. See
[`Services/StudioDisplayService.cs`](src/DisplayDial/Services/StudioDisplayService.cs)
and the P/Invoke surface under
[`Services/Native/`](src/DisplayDial/Services/Native/).

### Cross-references

The protocol was reverse-engineered and proven by several community projects.
DisplayDial's behaviour matches them byte-for-byte:

- [`2yxh/BrightStudio`](https://github.com/2yxh/BrightStudio) — C# / Windows
- [`juliuszint/asdbctl`](https://github.com/juliuszint/asdbctl) — Rust / Linux
- [`jridgewell/studio-display-control`](https://github.com/jridgewell/studio-display-control) — TypeScript / libusb
- [`michaljach/win-studio-display`](https://github.com/michaljach/win-studio-display) — PowerShell

## Requirements

- Windows 10 21H2 / Windows 11 (x64 or arm64)
- An Apple Studio Display connected over USB-C or Thunderbolt
- .NET 8 SDK to build, or the .NET 8 Desktop Runtime to run the framework-dependent build

No admin privileges, no kernel driver, no signed driver shenanigans — the HID
control interface is accessible to a normal user-mode process.

## Build and run

```powershell
# from the repo root, on Windows
dotnet build -c Release
dotnet run --project src/DisplayDial
```

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
  Models/
    StudioDisplayInfo.cs          - immutable device descriptor
  Services/
    IDisplayService.cs            - abstraction
    StudioDisplayService.cs       - HID enumeration + read/write
    IconFactory.cs                - generates the sun tray icon at runtime
    Native/
      HidNative.cs                - hid.dll P/Invoke
      SetupApiNative.cs           - setupapi.dll P/Invoke
      Kernel32Native.cs           - kernel32.dll CreateFile / CloseHandle
      HidDeviceSafeHandle.cs      - SafeHandle wrapper
```

## Troubleshooting

- **No displays found:** Some USB-C docks and adapters strip the HID interface
  while passing video through. Try a direct USB-C connection from PC to display.
- **Permission denied:** None expected on Windows — `CreateFile` with
  `GENERIC_READ | GENERIC_WRITE` and shared access works for normal users.
- **Brightness jumps to wrong value:** the raw range is `400..60000`; rounding
  is intentional so the slider snaps to integer percents.

## License

MIT.
