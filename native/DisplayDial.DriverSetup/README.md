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

This helper links libwdi statically, so it can **only be built on Windows with
MSVC** — it cannot be cross-compiled from macOS or Linux.

`build.ps1` patches libwdi to build **x64-only and co-installer-free**, so you do
**not** need the WDK or the ARM64 build tools (see
[Why the libwdi patch](#why-the-libwdi-patch) below).

### Prerequisites

- **Visual Studio 2022 or 2026** with the *Desktop development with C++* workload
  (provides MSBuild + CMake + the MSVC toolset).
- The **v143 (VS 2022) x64 build tools** component. libwdi pins the v143 toolset;
  the helper is built with the same toolset so the static libraries link cleanly.
  On VS 2026 add it via *Individual components → MSVC v143 - VS 2022 C++ x64/x86
  build tools*.
- **Git**.

That's it — **no Windows Driver Kit and no ARM64 tools are required.**

### One-shot build

From a **Developer PowerShell for VS** (2022 or 2026):

```powershell
cd native\DisplayDial.DriverSetup
./build.ps1
```

`build.ps1` will:

1. Clone libwdi (`v1.5.1`) into `third_party/libwdi`.
2. Patch it to build x64-only and co-installer-free (idempotent; re-running
   resets the checkout and re-applies the patch).
3. Build the libwdi static library (Release | x64).
4. Stage `libwdi.h` + `libwdi.lib`.
5. Configure + build this helper with CMake (toolset `v143`).
6. Copy `DisplayDial.DriverSetup.exe` next to the DisplayDial app output
   (`src/DisplayDial/bin/Release/net10.0-windows`) so the tray app finds it.

Override defaults if needed:

```powershell
# e.g. force the generator on an unusual CMake, or change the toolset:
./build.ps1 -Generator "Visual Studio 18 2026" -Toolset v143
./build.ps1 -Config Release -Platform x64 -LibwdiTag v1.5.1 -AppOutputDir "C:\path\to\app"
```

If CMake can't auto-detect your Visual Studio, pass `-Generator` explicitly
(`"Visual Studio 17 2022"` or `"Visual Studio 18 2026"`).

### Why the libwdi patch

Stock libwdi targets x86 + x64 + ARM64 and embeds the legacy WinUSB/WDF
*co-installer* DLLs from the WDK. On a modern toolchain that breaks twice:

1. Its static-library project references an **ARM64 installer project**, so the
   build fails with `MSB8020` unless the ARM64 v143 tools are installed.
2. The modern WDK (Windows 10 1809+) **no longer ships those co-installer DLLs**,
   so libwdi's "embedder" step fails trying to open them.

Co-installers have been unnecessary since Windows 10 (WinUSB is in-box), and
libwdi's own ARM64 path already installs WinUSB inbox with no co-installer.
`build.ps1` simply makes the x64 build behave like that ARM64 path: it drops the
x86/ARM64 project references, switches `config.h` to x64-only, removes the
co-installer embeds, and blanks the co-installer sections of the generated INF.
`winusb.cat.in` is left untouched — libwdi's catalog builder hashes only the
files actually present (and always adds the INF), so the absent co-installers are
skipped, exactly as on ARM64. The exact edits are documented in the `.NOTES`
block at the top of `build.ps1`.

> This is the planned seam for a future **INF + CAT** (production-signed) path:
> swap the C# `IDriverInstallService` implementation and replace the libwdi
> install with your signed package — the rest of the app is unaffected.

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
