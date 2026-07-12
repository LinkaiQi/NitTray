<div align="center">

<img src="src/app/Assets/AppIcon-128-trimmed.png" width="80" alt="NitTray icon">

# NitTray

**Adjust your Apple display's brightness from Windows.**

[![Download for Windows](https://img.shields.io/badge/⬇%20Download-Windows%2010%20%7C%2011-0078D6?style=for-the-badge&logo=windows&logoColor=white)](#download)

[![Latest release](https://img.shields.io/github/v/release/LinkaiQi/NitTray?sort=semver&label=latest%20release)](https://github.com/LinkaiQi/NitTray/releases/latest)
[![License: GPLv3](https://img.shields.io/badge/license-GPLv3-blue)](LICENSE)

<img src="docs/public/screenshots/light-theme.png" alt="NitTray in light theme" width="360">
&nbsp;&nbsp;
<img src="docs/public/screenshots/dark-theme.png" alt="NitTray in dark theme" width="360">

</div>

---

NitTray is a free, open-source Windows app for adjusting the brightness of
Apple's **Studio Display** and **Pro Display XDR** without a Mac.

## Supported Displays

- Apple **Studio Display** (1st & 2nd generation)
- Apple **Studio Display XDR**
- Apple **Pro Display XDR**

Connect the display to your PC with a **USB-C or Thunderbolt** cable.

## Download

Download the [latest release](https://github.com/LinkaiQi/NitTray/releases/latest):

| Architecture | Download |
|---------|----------|
| **x64** — for most Windows PCs | [![Download the x64 installer][badge-x64]][inst-x64] |
| **Arm64** — for Windows on ARM<br>(Snapdragon X, Surface Pro X, and similar) | [![Download the Arm64 installer][badge-arm64]][inst-arm64] |

[badge-x64]: https://img.shields.io/badge/⬇%20Download-x64%20installer-0078D6?style=for-the-badge&logo=windows&logoColor=white
[badge-arm64]: https://img.shields.io/badge/⬇%20Download-Arm64%20installer-0078D6?style=for-the-badge&logo=windows&logoColor=white
[inst-x64]: https://github.com/LinkaiQi/NitTray/releases/latest/download/NitTray-installer-x64.exe
[inst-arm64]: https://github.com/LinkaiQi/NitTray/releases/latest/download/NitTray-installer-arm64.exe

> [!NOTE]
> NitTray is code-signed. However, because it is a new application and has not yet
> established a Microsoft SmartScreen reputation, Windows may display a
> *"Windows protected your PC"* message the first time you launch it. Select
> **More info**, then **Run anyway**.

## Build from Source

The [`src/app/README.md`](src/app/README.md) explains NitTray's USB/HID communication, display detection process, and how to build the application from source.

## License

NitTray is licensed under the terms described in [LICENSE](LICENSE).

For Pro Display XDR driver setup, NitTray includes [libwdi](https://github.com/pbatard/libwdi), which is licensed under LGPLv3/GPLv3. Additional bundled components and their licenses are listed in [`docs/THIRD-PARTY-NOTICES.md`](docs/THIRD-PARTY-NOTICES.md).

## Support

NitTray is free and developed in my spare time. If you find it useful, please consider supporting its continued development:

[**Buy me a coffee ☕**](https://buymeacoffee.com/nittray)

## Trademarks

Apple, Studio Display, Pro Display XDR, Mac, and macOS are trademarks of Apple
Inc. NitTray is an independent, unofficial project and is not affiliated with,
endorsed by, or sponsored by Apple Inc. Product names are used only to describe
the hardware NitTray works with.
