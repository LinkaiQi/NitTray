# winget package manifests

Manifests for publishing NitTray to the [Windows Package Manager](https://github.com/microsoft/winget-pkgs),
so users can install with:

```
winget install LinkaiQi.NitTray
```

## Files

| File | Purpose |
|------|---------|
| `LinkaiQi.NitTray.yaml` | Version manifest |
| `LinkaiQi.NitTray.installer.yaml` | Installer URLs, hashes, architectures |
| `LinkaiQi.NitTray.locale.en-US.yaml` | Name, publisher, description, tags |

## Submitting (per release)

1. **The repo must be public** — the winget validation pipeline downloads the
   `InstallerUrl` to verify the hash, so the release assets must be publicly
   reachable.
2. Bump `PackageVersion` and the two `InstallerUrl`s to the new tag, and refresh
   both `InstallerSha256` values:
   ```sh
   sha256sum NitTray-installer-x64.exe NitTray-installer-arm64.exe
   ```
   (winget expects uppercase hex.)
3. Validate locally (on Windows, with winget installed):
   ```powershell
   winget validate --manifest packaging/winget
   ```
4. Open a PR against [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs)
   placing the files under
   `manifests/l/LinkaiQi/NitTray/<version>/`, or use
   [`wingetcreate`](https://github.com/microsoft/winget-create):
   ```powershell
   wingetcreate update LinkaiQi.NitTray --version <version> `
     --urls <x64-url> <arm64-url> --submit
   ```

The current manifests target **v0.1.3**.
