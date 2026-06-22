# DisplayDial.DriverSetup

A tiny **elevated** console helper that installs Microsoft's in-box **WinUSB**
driver onto an Apple display that Windows' generic HID driver refuses — namely
the **Apple Pro Display XDR** (`VID_05AC` / `PID_9243`).

The DisplayDial tray app launches this helper (raising a single UAC prompt) when
it detects a Pro Display XDR on the USB bus that isn't WinUSB-bound yet. It is
the in-app replacement for manually running Zadig.

It is a thin wrapper around **[libwdi](https://github.com/pbatard/libwdi)** — the
same driver-installation engine that powers Zadig.

## What it does

```
DisplayDial.DriverSetup.exe install <VID-hex> <PID-hex>
# e.g.
DisplayDial.DriverSetup.exe install 05AC 9243
```

Internally it:

1. `wdi_create_list(...)` — enumerate USB devices (including composite parents).
2. Confirm the target VID/PID is present and pick the **composite-parent** node
   (hardware id without an `MI_` suffix). Binding WinUSB to the *whole* device —
   not a single interface — is what lets the app open one WinUSB handle and reach
   the brightness interface via `WinUsb_GetAssociatedInterface`.
3. `wdi_prepare_driver(...)` — generate a WinUSB INF, self-sign a catalog, and
   prepare a self-signed certificate.
4. `wdi_install_driver(...)` — install the certificate into the Trusted Publisher
   store and bind WinUSB to the device.

The result is reported **only through the process exit code** (no stdout parsing):

| Exit code | Meaning                         | C# `DriverSetupExitCodes` |
|-----------|---------------------------------|---------------------------|
| `0`       | Success                         | `Success`                 |
| `1`       | Generic/unexpected error        | `GenericError`            |
| `2`       | Bad arguments                   | `BadArguments`            |
| `3`       | Target device not present       | `DeviceNotFound`          |
| `4`       | `wdi_prepare_driver` failed     | `PrepareFailed`           |
| `5`       | `wdi_install_driver` failed     | `InstallFailed`           |

> ⚠️ Keep this table in sync with
> [`src/DisplayDial/Services/DriverSetupExitCodes.cs`](../../src/DisplayDial/Services/DriverSetupExitCodes.cs).

Detailed progress is appended to `%LOCALAPPDATA%\DisplayDial\driver-setup.log`.

## Building (Windows only)

This helper links libwdi statically and embeds the WinUSB co-installer, so it can
**only be built on Windows with MSVC** — it cannot be cross-compiled from macOS or
Linux.

### Prerequisites

- **Visual Studio 2022** with the *Desktop development with C++* workload
  (provides MSBuild + the MSVC toolset).
- **CMake** 3.20+ (the VS installer can add it, or install separately).
- **Git**.
- **Windows Driver Kit (WDK).** libwdi embeds the WinUSB co-installer from the WDK
  redistributables. Install it from
  <https://learn.microsoft.com/windows-hardware/drivers/download-the-wdk>.
  If libwdi can't find the kit, edit `third_party/libwdi/msvc/config.h` and set:

  ```c
  #define WDK_DIR "C:/Program Files (x86)/Windows Kits/10"
  ```

### One-shot build

From a **Developer PowerShell for VS 2022**:

```powershell
cd native\DisplayDial.DriverSetup
./build.ps1
```

`build.ps1` will:

1. Clone libwdi (`v1.5.1`) into `third_party/libwdi`.
2. Build the libwdi static library (Release | x64).
3. Stage `libwdi.h` + `libwdi.lib`.
4. Configure + build this helper with CMake.
5. Copy `DisplayDial.DriverSetup.exe` next to the DisplayDial app output
   (`src/DisplayDial/bin/Release/net10.0-windows`) so the tray app finds it.

Override defaults if needed:

```powershell
./build.ps1 -Config Release -Platform x64 -LibwdiTag v1.5.1 -AppOutputDir "C:\path\to\app"
```

### Manual build (if you prefer)

```powershell
# 1. Build libwdi (Release|x64) — open libwdi.sln in VS once if MSBuild struggles.
msbuild third_party\libwdi\libwdi.sln /p:Configuration=Release /p:Platform=x64 /m

# 2. Stage the outputs into one folder:
#      libwdi.h   <- third_party\libwdi\libwdi\libwdi.h
#      libwdi.lib <- third_party\libwdi\x64\Release\lib\libwdi.lib

# 3. Build this helper:
cmake -S . -B build -G "Visual Studio 17 2022" -A x64 -DLIBWDI_ROOT="<stage folder>"
cmake --build build --config Release
```

## How the tray app uses it

`WinUsbDriverInstallService` (in the main app) resolves
`DisplayDial.DriverSetup.exe` next to `DisplayDial.exe` and starts it with
`UseShellExecute = true`, `Verb = "runas"` so Windows shows the UAC prompt. The
embedded `requireAdministrator` manifest (see `DisplayDial.DriverSetup.manifest`,
wired through `resources.rc`) guarantees elevation even if the helper is ever run
directly.

## Licensing

libwdi is **LGPLv3 / GPLv3**. Because this helper statically links libwdi and is
distributed with DisplayDial, the project is released under the **GPLv3** — see
[`LICENSE`](../../LICENSE) at the repository root.
