# Code signing DisplayDial

DisplayDial is currently **unsigned**, so when users download it Windows
SmartScreen / Smart App Control warns that it "can't confirm the publisher," and
some antivirus engines are extra suspicious because the app installs a USB
(WinUSB) driver. Signing every released binary — and timestamping each signature
— is the single most important step before distributing it on the web.

## What must be signed

A DisplayDial release contains four Authenticode targets:

| Artifact                        | What it is                                   |
| ------------------------------- | -------------------------------------------- |
| `DisplayDial.exe`               | the app launcher (apphost)                   |
| `DisplayDial.dll`               | the actual managed app assembly              |
| `DisplayDial.DriverSetup.exe`   | the elevated native WinUSB installer helper  |
| `Setup.exe` / the installer     | whatever users actually download and run     |

> The WinUSB **driver package** that the helper installs is a *separate*,
> self-signed package that libwdi generates and trusts on the user's machine at
> install time (already implemented). That is unrelated to signing the app itself.

## Certificate options

| Option | Cost | Identity | Hardware token / HSM | SmartScreen at launch | CI-friendly | Public trust |
| ------ | ---- | -------- | -------------------- | --------------------- | ----------- | ------------ |
| **Azure Artifact Signing** (formerly Trusted Signing) | ~$10/mo | Individual **or** org (identity validated) | No (Azure-managed) | Clears quickly | Yes (official Action) | Yes |
| **EV certificate** | $300–700+/yr | Org (business entity) | Yes | **Instant** | Hard (token) / cloud-HSM only | Yes |
| **OV / IV certificate** (e.g. Certum, Sectigo, SSL.com) | ~$70–400/yr | Org (OV) or individual (IV) | Yes (since 2023) | Builds over downloads | Cloud-HSM only | Yes |
| **Self-signed** (`tools/sign.ps1 -SelfSigned`) | Free | n/a | No | n/a | n/a | **No — local only** |

### Recommendation
- **Azure Artifact Signing** (formerly Trusted Signing) is the best modern value:
  ~$10/month, no hardware
  token, integrates cleanly with GitHub Actions, and individuals are eligible
  (verify current requirements for your country when you sign up). Trust clears
  faster than a plain OV cert because Microsoft validates your identity.
- Choose an **EV certificate** only if you have a registered business and want
  *zero* SmartScreen warning from the very first download (and can live with the
  hardware-token / cloud-HSM workflow).
- A **Certum / SSL.com individual (IV)** certificate is a budget alternative, but
  reputation still ramps over downloads and you need their cloud-HSM signing.
- **Self-signed** is only a stopgap for *your* PC (below).

> Even with a valid OV/IV signature, brand-new apps can still show a
> "not commonly downloaded" notice until SmartScreen reputation accrues. Only EV
> (and largely Trusted Signing) avoid that.

## Sign locally right now (stopgap)

This removes the "unknown publisher" warning on **your** machine across rebuilds.
It does **not** help anyone else — do not ship self-signed builds.

```powershell
# Build, then sign the output folder with a local self-signed cert:
dotnet build -c Release
.\tools\sign.ps1 -SelfSigned -Path .\src\DisplayDial\bin\Release\net10.0-windows
```

`sign.ps1` creates the certificate once, trusts it in your CurrentUser stores, and
reuses it afterwards. Verify with:

```powershell
Get-AuthenticodeSignature .\src\DisplayDial\bin\Release\net10.0-windows\DisplayDial.dll | Format-List
```

## Sign a release with a real certificate

```powershell
# From an exported PFX/P12 file (e.g. a Certum IV cert):
.\tools\sign.ps1 -PfxPath .\displaydial.pfx -PfxPassword '<password>' -Path .\publish

# Or from a token/HSM cert already installed in the Windows store:
.\tools\sign.ps1 -Thumbprint <THUMBPRINT> -Path .\publish
```

Both paths timestamp via `http://timestamp.digicert.com` (override with
`-TimestampUrl`).

## Sign in CI with Azure Artifact Signing

> Azure **Trusted Signing** is now called **Azure Artifact Signing**; the GitHub
> Action is [`azure/artifact-signing-action@v2`](https://github.com/Azure/artifact-signing-action).

The release workflow (`.github/workflows/release.yml`) already calls the action.
It is **gated behind the repo variable `ARTIFACT_SIGNING`**, so it builds unsigned
artifacts until you finish this one-time Azure setup and flip the flag:

1. In the Azure portal, create an **Artifact Signing account**, complete
   **identity validation** (an individual identity is fine), and create a
   **certificate profile** (Public Trust).
2. Create a **Microsoft Entra app registration**, and on the signing account grant
   it the **Artifact Signing Certificate Profile Signer** role.
3. Authenticate with **OIDC / federated credentials** (recommended — no stored
   secret): add a *Federated credential* on the app registration for this repo
   (subject e.g. `repo:LinkaiQi/DisplayDial:ref:refs/tags/v*` and/or your
   environment), then add these GitHub repo **secrets**: `AZURE_CLIENT_ID`,
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

> Note: the release workflow currently builds and signs the **app**. The native
> helper (`DisplayDial.DriverSetup.exe`) and the installer are added to the same
> job during the packaging phase; the same signing step then covers them too.

## Verifying signatures

```powershell
Get-AuthenticodeSignature <file> | Format-List          # PowerShell
signtool verify /pa /all <file>                          # Windows SDK
```

A healthy result shows `Status: Valid`, the expected signer, **and** a
timestamp.
