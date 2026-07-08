# Code signing NitTray

NitTray is **unsigned** until the one-time Azure setup below is completed, so
downloaded builds make Windows SmartScreen / Smart App Control warn that they
"can't confirm the publisher," and some antivirus engines are extra suspicious
because the app installs a USB (WinUSB) driver. Signing every released binary —
and timestamping each signature — is the single most important step before
distributing it on the web.

NitTray signs its releases with **Azure Artifact Signing** (formerly Trusted
Signing) directly from the GitHub Actions release workflow.

## What gets signed

A NitTray release contains four Authenticode targets:

| Artifact                        | What it is                                   |
| ------------------------------- | -------------------------------------------- |
| `NitTray.exe`               | the app launcher (apphost)                   |
| `NitTray.dll`               | the actual managed app assembly              |
| `NitTray.DriverSetup.exe`   | the elevated native WinUSB installer helper  |
| `Setup.exe` / the installer     | whatever users actually download and run     |

> The WinUSB **driver package** that the helper installs is a *separate*,
> self-signed package that libwdi generates and trusts on the user's machine at
> install time (already implemented). That is unrelated to signing the app itself.

## Sign in CI with Azure Artifact Signing

> Azure **Trusted Signing** is now called **Azure Artifact Signing**; the GitHub
> Action is [`azure/artifact-signing-action@v2`](https://github.com/Azure/artifact-signing-action).

The release workflow (`.github/workflows/release.yml`) already calls the action.
It is **gated behind the repo variable `ARTIFACT_SIGNING`**, so it builds unsigned
artifacts until you finish this one-time Azure setup and flip the flag:

1. In the Azure portal, create an **Artifact Signing account**. Before you can
   start **identity validation**, assign your own Azure user the
   **Artifact Signing Identity Verifier** role on the account (Access control
   (IAM) → Add role assignment — older portals call it *Trusted Signing Identity
   Verifier*; assigning roles needs Owner / User Access Administrator). Then
   complete **identity validation** (an individual identity is fine) and create a
   **certificate profile** (Public Trust).
2. Create a **Microsoft Entra app registration** (this is the identity GitHub
   Actions logs in as). Note its **Application (client) ID** and **Directory
   (tenant) ID**. On the signing account, grant this app the **Artifact Signing
   Certificate Profile Signer** role (Access control (IAM) → Add role assignment →
   assign to the app registration).
3. Authenticate with **OIDC / federated credentials** (recommended — no stored
   secret). The release workflow runs in a GitHub **Environment** named `release`,
   which gives a stable token subject regardless of the tag version. On the app
   registration → *Certificates & secrets* → *Federated credentials* → *Add* →
   scenario **GitHub Actions deploying Azure resources**:
   - Organization: `LinkaiQi`, Repository: `NitTray`
   - Entity type: **Environment**, name: `release`
   - This produces subject `repo:LinkaiQi/NitTray:environment:release`.

   Lock the environment down so only release tags can use it (*Settings →
   Environments → release → Deployment branches and tags → Selected branches and
   tags*): add a **tag** rule with pattern `v*`. Now a run can only reach the
   signing credentials from a `v*` tag ref — a manual `workflow_dispatch` from a
   branch like `main` is blocked at the environment gate (pick a `v*` tag in the
   *Use workflow from* dropdown to dispatch manually).

   Then add these GitHub repo **secrets**: `AZURE_CLIENT_ID` (the app's client ID),
   `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`.
4. Add these GitHub repo **variables**:
   - `ARTIFACT_SIGNING` = `true`  (the on/off flag the workflow checks)
   - `ARTIFACT_SIGNING_ENDPOINT` = your region endpoint, e.g.
     `https://eus.codesigning.azure.net/`
   - `ARTIFACT_SIGNING_ACCOUNT` = your signing account name
   - `ARTIFACT_SIGNING_PROFILE` = your certificate profile name
5. Push a tag (`git tag v0.1.0 && git push --tags`) or run the workflow manually.
   The action signs the published `.exe`(s) and timestamps via
   `http://timestamp.acs.microsoft.com`.

> Note: the release workflow builds NitTray for **x64 and ARM64**. The native
> WinUSB helper (`NitTray.DriverSetup.exe`, built via `build.ps1 -SupportArm64`)
> is a single x64 binary that installs the correct driver on both architectures
> (it runs under x64 emulation on Windows on ARM); it is bundled into each
> per-arch publish and signed alongside the app. Each architecture then ships two
> ways: an **Inno Setup installer** (`NitTray-<tag>-setup-<arch>.exe`, built from
> `installer/NitTray.iss` and signed after the app binaries) and a **portable
> zip** (`NitTray-<tag>-win-<arch>.zip`). The signing steps cover the app exes,
> the bundled helper, and the installer.

## Verifying signatures

```powershell
Get-AuthenticodeSignature <file> | Format-List          # PowerShell
signtool verify /pa /all <file>                          # Windows SDK
```

A healthy result shows `Status: Valid`, the expected signer, **and** a
timestamp.
