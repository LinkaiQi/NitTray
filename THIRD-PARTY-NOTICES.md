# Third-party notices

NitTray is licensed under the GNU General Public License v3.0 (see `LICENSE`).
It builds on the following open-source components, with thanks:

## libwdi

- Project: https://github.com/pbatard/libwdi
- License: GNU Lesser General Public License v3.0 (LGPL-3.0)
- Used by: the native `NitTray.DriverSetup` helper to install the WinUSB driver
  required by the Apple Pro Display XDR.

libwdi is statically linked into the helper executable. In accordance with the
LGPL-3.0, the corresponding source is available at the project link above, and
the build steps that produce the helper are documented in
`native/NitTray.DriverSetup/`.

## WPF-UI

- Project: https://github.com/lepoco/wpfui
- License: MIT
- Used by: the NitTray desktop app for the Fluent 2 design system (FluentWindow,
  themed controls, Mica backdrop).

## .NET

- Project: https://github.com/dotnet/runtime
- License: MIT
- Used by: the NitTray desktop app (WPF on .NET).

---

Apple, Studio Display, Studio Display XDR, and Pro Display XDR are trademarks of
Apple Inc. NitTray is an independent app and is not affiliated with, endorsed by,
or sponsored by Apple Inc.
