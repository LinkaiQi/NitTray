<#
.SYNOPSIS
    Authenticode-signs NitTray's executables, library, native helper and installer.

.DESCRIPTION
    NitTray ships unsigned by default, so Windows SmartScreen / Smart App Control
    warns that it "can't confirm the publisher" (and driver-install tools draw extra
    antivirus scrutiny). This script signs every artifact and timestamps each
    signature so it stays valid after the certificate expires.

    Three certificate sources are supported:

      -SelfSigned         Create (once) and reuse a local self-signed code-signing
                          certificate, trust it in your CurrentUser stores, and sign
                          with it. This is a STOPGAP for your own machine only: it
                          stops the "unknown publisher" warning on THIS PC across
                          rebuilds. It is NOT valid for public distribution — other
                          people's machines do not trust your self-signed cert.

      -PfxPath <file>     Sign with a real certificate from a PFX/P12 file
        [-PfxPassword]    (e.g. a Certum / Sectigo IV or OV certificate export).

      -Thumbprint <hex>   Sign with a certificate already installed in the Windows
                          certificate store (e.g. a hardware-token / cloud-HSM OV/EV
                          certificate). Looks in CurrentUser\My then LocalMachine\My.

    Azure Trusted Signing is signed in CI via azure/trusted-signing-action — see
    docs/SIGNING.md. This script covers local self-signed + PFX/store signing.

.PARAMETER Path
    Folder containing the build/publish output to sign. Defaults to .\publish.

.PARAMETER TimestampUrl
    RFC 3161 timestamp server. Defaults to DigiCert's.

.EXAMPLE
    # Stopgap: sign your local build so THIS PC stops warning.
    .\tools\sign.ps1 -SelfSigned -Path .\src\NitTray\bin\Release\net10.0-windows

.EXAMPLE
    # Release: sign a published folder with a real certificate file.
    .\tools\sign.ps1 -PfxPath .\mycert.pfx -PfxPassword 'secret' -Path .\publish

.EXAMPLE
    # Release: sign with a token/HSM certificate already in the store.
    .\tools\sign.ps1 -Thumbprint A1B2C3... -Path .\publish
#>
[CmdletBinding(DefaultParameterSetName = 'SelfSigned')]
param(
    [Parameter(ParameterSetName = 'SelfSigned')]
    [switch]$SelfSigned,

    [Parameter(ParameterSetName = 'Pfx', Mandatory = $true)]
    [string]$PfxPath,
    [Parameter(ParameterSetName = 'Pfx')]
    [string]$PfxPassword,

    [Parameter(ParameterSetName = 'Store', Mandatory = $true)]
    [string]$Thumbprint,

    [string]$Path = '.\publish',
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$SelfSignedSubject = 'CN=NitTray (self-signed, local testing only)'
)

$ErrorActionPreference = 'Stop'

function Resolve-TargetFiles {
    param([string]$Root)

    if (-not (Test-Path -LiteralPath $Root)) {
        throw "Path not found: $Root. Build or publish first, then point -Path at the output folder."
    }

    # The app apphost, its managed assembly, the elevated native helper, and any
    # generated installer. Glob so this works for both bin\Release and publish folders.
    $patterns = @('NitTray.exe', 'NitTray.dll', 'NitTray.DriverSetup.exe', '*Setup*.exe', '*Install*.exe')
    $found = foreach ($p in $patterns) {
        Get-ChildItem -LiteralPath $Root -Recurse -Filter $p -File -ErrorAction SilentlyContinue
    }

    $found | Sort-Object -Property FullName -Unique
}

function Get-SelfSignedCert {
    param([string]$Subject)

    $existing = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
    if ($existing) {
        Write-Host "Reusing self-signed certificate $($existing.Thumbprint)."
        return $existing
    }

    Write-Host "Creating a new self-signed code-signing certificate ($Subject)..."
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -KeyUsage DigitalSignature `
        -KeyExportPolicy Exportable `
        -KeySpec Signature `
        -NotAfter (Get-Date).AddYears(5) `
        -CertStoreLocation Cert:\CurrentUser\My

    # Trust it for the current user so its signatures validate on THIS machine.
    $tmp = Join-Path $env:TEMP "nittray-selfsign.cer"
    Export-Certificate -Cert $cert -FilePath $tmp | Out-Null
    foreach ($store in @('Cert:\CurrentUser\Root', 'Cert:\CurrentUser\TrustedPublisher')) {
        Import-Certificate -FilePath $tmp -CertStoreLocation $store | Out-Null
    }
    Remove-Item $tmp -ErrorAction SilentlyContinue
    Write-Host "Created and locally trusted certificate $($cert.Thumbprint)."
    return $cert
}

function Get-StoreCert {
    param([string]$Thumbprint)
    foreach ($loc in @('Cert:\CurrentUser\My', 'Cert:\LocalMachine\My')) {
        $c = Get-ChildItem $loc | Where-Object { $_.Thumbprint -eq $Thumbprint }
        if ($c) { return $c }
    }
    throw "No certificate with thumbprint $Thumbprint found in CurrentUser\My or LocalMachine\My."
}

# --- Acquire the signing certificate -----------------------------------------
switch ($PSCmdlet.ParameterSetName) {
    'SelfSigned' {
        Write-Warning 'Self-signed mode: valid ONLY on this machine. Do not distribute self-signed builds.'
        $cert = Get-SelfSignedCert -Subject $SelfSignedSubject
    }
    'Pfx' {
        if (-not (Test-Path -LiteralPath $PfxPath)) { throw "PFX not found: $PfxPath" }
        if ($PfxPassword) {
            $secure = ConvertTo-SecureString -String $PfxPassword -AsPlainText -Force
            $cert = Get-PfxCertificate -FilePath $PfxPath -Password $secure
        } else {
            $cert = Get-PfxCertificate -FilePath $PfxPath
        }
    }
    'Store' {
        $cert = Get-StoreCert -Thumbprint $Thumbprint
    }
}

# --- Sign --------------------------------------------------------------------
$files = Resolve-TargetFiles -Root $Path
if (-not $files) {
    throw "No signable artifacts found under $Path (looked for NitTray.exe/.dll, NitTray.DriverSetup.exe, *Setup*.exe)."
}

Write-Host ""
Write-Host "Signing $($files.Count) file(s) with certificate $($cert.Thumbprint):"
$failed = 0
foreach ($f in $files) {
    $result = Set-AuthenticodeSignature `
        -FilePath $f.FullName `
        -Certificate $cert `
        -HashAlgorithm SHA256 `
        -TimestampServer $TimestampUrl
    $status = $result.Status
    Write-Host ("  [{0,-7}] {1}" -f $status, $f.FullName)
    if ($status -ne 'Valid') { $failed++ }
}

Write-Host ""
if ($failed -gt 0) {
    throw "$failed file(s) failed to sign."
}
Write-Host "Done. Verify any file with: Get-AuthenticodeSignature <file> | Format-List"
if ($PSCmdlet.ParameterSetName -eq 'SelfSigned') {
    Write-Host "Reminder: self-signed signatures are trusted only on this PC. Use a real certificate for public releases (see docs/SIGNING.md)."
}
