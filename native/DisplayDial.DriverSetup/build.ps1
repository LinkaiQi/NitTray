<#
.SYNOPSIS
    Builds DisplayDial.DriverSetup.exe (the elevated WinUSB installer helper).

.DESCRIPTION
    1. Clones libwdi (pinned tag) into native/third_party/libwdi if absent.
    2. Patches libwdi to build x64-only and co-installer-free (see NOTES).
    3. Builds the libwdi static library (Release|x64) with MSBuild.
    4. Stages libwdi.h + libwdi.lib into a single folder.
    5. Configures and builds this helper with CMake, linking libwdi.
    6. Copies DisplayDial.DriverSetup.exe next to the DisplayDial app output so
       the tray app can find and launch it.

    MUST be run on Windows from a "Developer PowerShell for VS" prompt (VS 2022 or
    VS 2026), so MSBuild + CMake + the v143 toolset are on PATH.

.PARAMETER Config
    Build configuration (default: Release).

.PARAMETER Platform
    Target platform (default: x64). DisplayDial ships x64 only.

.PARAMETER LibwdiTag
    Git tag of libwdi to build (default: v1.5.1).

.PARAMETER Toolset
    MSVC platform toolset for the CMake helper build (default: v143). This must
    match the toolset libwdi is built with (libwdi pins v143) so the static CRT
    objects link cleanly.

.PARAMETER Generator
    CMake generator override (e.g. "Visual Studio 18 2026"). Leave empty to let
    CMake auto-detect the installed Visual Studio — the most reliable option when
    you run from a Developer PowerShell.

.PARAMETER AppOutputDir
    Where to copy the built helper. Defaults to the DisplayDial Release output
    (..\..\src\DisplayDial\bin\<Config>\net10.0-windows).

.NOTES
    *** No WDK and no ARM64 build tools are required. ***

    Stock libwdi targets x86 + x64 + ARM64 and embeds the legacy WinUSB/WDF
    co-installer DLLs from the Windows Driver Kit. That breaks on a modern
    toolchain for two reasons:

      1. Its static-library project references an ARM64 installer project, so the
         build fails (MSB8020) unless the ARM64 v143 tools are installed.
      2. The modern WDK (Windows 10 1809+) no longer ships the co-installer DLLs
         libwdi tries to embed, so the "embedder" step fails opening them.

    Co-installers have been unnecessary since Windows 10 (WinUSB is in-box), and
    libwdi's own ARM64 path already installs WinUSB inbox with no co-installer.
    Invoke-LibwdiPatch makes the x64 build behave exactly like that ARM64 path:

      * msvc/config.h               -> x64-only (comment out OPT_M32 + OPT_ARM).
                                       WDK_DIR stays *defined* (it is only a
                                       compile-time gate in libwdi.c that keeps
                                       WinUSB a "supported" driver type; its path
                                       value is never read once the embeds below
                                       are removed).
      * libwdi/embedder_files.h      -> drop the 4 WinUSB/WDF co-installer embeds.
      * libwdi/winusb.inf.in         -> blank the NTamd64 CoInstallers +
                                       SourceDisksFiles.amd64 sections (mirrors the
                                       already-shipping arm64 sections).
      * libwdi/.msvc/libwdi_static.vcxproj
                                     -> drop the installer_arm64 + installer_x86
                                       project references so the static lib builds
                                       with x64 tools only.

    winusb.cat.in is intentionally left untouched: libwdi's catalog builder hashes
    only the files actually present in the package dir (and always adds the INF),
    so the now-absent co-installers are simply skipped — exactly as on arm64.

    The patch is applied to a pristine checkout every run (git checkout -- .), so
    re-running build.ps1 is idempotent.
#>
[CmdletBinding()]
param(
    [string]$Config = "Release",
    [string]$Platform = "x64",
    [string]$LibwdiTag = "v1.5.1",
    [string]$Toolset = "v143",
    [string]$Generator = "",
    [string]$AppOutputDir = ""
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$thirdParty = Join-Path $scriptDir "third_party"
$libwdiDir = Join-Path $thirdParty "libwdi"
$stageDir = Join-Path $thirdParty "libwdi-stage"
$buildDir = Join-Path $scriptDir "build"

function Find-MSBuild {
    $cmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $path = & $vswhere -latest -prerelease -requires Microsoft.Component.MSBuild `
            -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
        if ($path) { return $path }
    }
    throw "MSBuild.exe not found. Run this from a 'Developer PowerShell for VS' prompt."
}

function Find-CMake {
    $cmd = Get-Command cmake.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "cmake.exe not found. Install CMake or add it to PATH (the VS C++ workload can provide it)."
}

# Throw a clear error if a patch anchor isn't found exactly as many times as
# expected -- guards against a future libwdi tag changing the file layout.
function Assert-PatchCount {
    param([int]$Actual, [int]$Expected, [string]$What)
    if ($Actual -ne $Expected) {
        throw "libwdi patch verification failed: $What (expected $Expected match(es), found $Actual). " +
              "The pinned libwdi tag '$LibwdiTag' may have changed; update Invoke-LibwdiPatch in build.ps1."
    }
}

# Patch a pristine libwdi checkout to build x64-only and co-installer-free.
# See the NOTES block above for the rationale behind each edit.
function Invoke-LibwdiPatch {
    param([string]$Root)

    $configH   = Join-Path $Root "msvc\config.h"
    $embedderH = Join-Path $Root "libwdi\embedder_files.h"
    $infIn     = Join-Path $Root "libwdi\winusb.inf.in"
    $staticVcx = Join-Path $Root "libwdi\.msvc\libwdi_static.vcxproj"
    foreach ($f in @($configH, $embedderH, $infIn, $staticVcx)) {
        if (-not (Test-Path $f)) { throw "Expected libwdi file is missing: $f" }
    }

    # Reset to pristine so re-running build.ps1 re-patches cleanly.
    if (Test-Path (Join-Path $Root ".git")) {
        & git -C $Root checkout -- . 2>$null | Out-Null
    }

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $utf8Bom   = New-Object System.Text.UTF8Encoding($true)

    Write-Host "==> Patching libwdi (x64-only, co-installer-free)..." -ForegroundColor Cyan

    # (1) config.h -- x64 only: comment out OPT_M32 and OPT_ARM, keep OPT_M64.
    $t = [System.IO.File]::ReadAllText($configH)
    $t = [regex]::Replace($t, '(?m)^#define OPT_M32\b', '//#define OPT_M32')
    $t = [regex]::Replace($t, '(?m)^#define OPT_ARM\b', '//#define OPT_ARM')
    Assert-PatchCount ([regex]::Matches($t, '(?m)^//#define OPT_M32\b').Count) 1 "config.h OPT_M32 disabled"
    Assert-PatchCount ([regex]::Matches($t, '(?m)^//#define OPT_ARM\b').Count) 1 "config.h OPT_ARM disabled"
    Assert-PatchCount ([regex]::Matches($t, '(?m)^#define OPT_M64\b').Count)   1 "config.h OPT_M64 kept"
    [System.IO.File]::WriteAllText($configH, $t, $utf8NoBom)

    # (2) embedder_files.h -- drop the 4 WinUSB/WDF co-installer embed entries.
    $t = [System.IO.File]::ReadAllText($embedderH)
    Assert-PatchCount ([regex]::Matches($t, 'WDK_DIR "\\\\redist').Count) 4 "embedder_files.h co-installer embeds present"
    $t = [regex]::Replace($t, '(?m)^[^\r\n]*WDK_DIR "\\\\redist[^\r\n]*\r?\n', '')
    Assert-PatchCount ([regex]::Matches($t, 'WDK_DIR "\\\\redist').Count) 0 "embedder_files.h co-installer embeds removed"
    [System.IO.File]::WriteAllText($embedderH, $t, $utf8NoBom)

    # (3) winusb.inf.in -- make the amd64 install path co-installer-free
    #     (mirror the arm64 sections that are already shipped that way).
    $t = [System.IO.File]::ReadAllText($infIn)
    $eol = if ($t.Contains("`r`n")) { "`r`n" } else { "`n" }
    $coPat = '(?m)^\[USB_Install\.NTamd64\.CoInstallers\]\r?\nAddReg\s*=\s*CoInstallers_AddReg\r?\nCopyFiles\s*=\s*CoInstallers_CopyFiles\r?\n'
    Assert-PatchCount ([regex]::Matches($t, $coPat).Count) 1 "inf NTamd64.CoInstallers section"
    $t = [regex]::Replace($t, $coPat, "[USB_Install.NTamd64.CoInstallers]$eol;$eol")
    $sdPat = '(?m)^\[SourceDisksFiles\.amd64\]\r?\nWinUSBCoInstaller2\.dll = 1,amd64\r?\nWdfCoInstaller#WDF_VERSION#\.dll = 1,amd64\r?\n'
    Assert-PatchCount ([regex]::Matches($t, $sdPat).Count) 1 "inf SourceDisksFiles.amd64 section"
    $t = [regex]::Replace($t, $sdPat, "[SourceDisksFiles.amd64]$eol;$eol")
    Assert-PatchCount ([regex]::Matches($t, 'WinUSBCoInstaller2\.dll = 1,amd64').Count) 0 "inf amd64 co-installer refs removed"
    [System.IO.File]::WriteAllText($infIn, $t, $utf8NoBom)

    # (4) libwdi_static.vcxproj -- drop the arm64 + x86 installer project refs.
    $t = [System.IO.File]::ReadAllText($staticVcx)
    foreach ($name in @('installer_arm64.vcxproj', 'installer_x86.vcxproj')) {
        $pat = '(?s)[ \t]*<ProjectReference Include="' + [regex]::Escape($name) + '">.*?</ProjectReference>\r?\n'
        Assert-PatchCount ([regex]::Matches($t, $pat).Count) 1 "vcxproj $name reference"
        $t = [regex]::Replace($t, $pat, '')
    }
    Assert-PatchCount ([regex]::Matches($t, 'installer_arm64\.vcxproj').Count) 0 "vcxproj arm64 ref removed"
    Assert-PatchCount ([regex]::Matches($t, 'installer_x86\.vcxproj').Count)   0 "vcxproj x86 ref removed"
    Assert-PatchCount ([regex]::Matches($t, 'installer_x64\.vcxproj').Count)   1 "vcxproj x64 ref kept"
    [System.IO.File]::WriteAllText($staticVcx, $t, $utf8Bom)  # this file ships with a UTF-8 BOM

    Write-Host "    libwdi patched: x64-only, no co-installer, no WDK/ARM64 tools needed." -ForegroundColor DarkGray
}

Write-Host "==> DisplayDial.DriverSetup build" -ForegroundColor Cyan
Write-Host "    Config=$Config Platform=$Platform libwdi=$LibwdiTag toolset=$Toolset"

# --- 1. Fetch libwdi --------------------------------------------------------
New-Item -ItemType Directory -Force -Path $thirdParty | Out-Null
if (-not (Test-Path (Join-Path $libwdiDir "libwdi.sln"))) {
    Write-Host "==> Cloning libwdi $LibwdiTag..." -ForegroundColor Cyan
    git clone --depth 1 --branch $LibwdiTag https://github.com/pbatard/libwdi.git $libwdiDir
    if ($LASTEXITCODE -ne 0) { throw "git clone of libwdi failed." }
} else {
    Write-Host "==> libwdi already present, skipping clone." -ForegroundColor DarkGray
}

# --- 2. Patch + build the libwdi static lib --------------------------------
$msbuild = Find-MSBuild
Invoke-LibwdiPatch -Root $libwdiDir

# Build the static-lib project directly (not the whole .sln) so the dropped
# arm64/x86 installer projects are never touched. The project uses
# $(SolutionDir) for its output path, so pass it explicitly (forward slashes
# avoid a trailing-backslash quoting pitfall when launching MSBuild).
$staticVcx = Join-Path $libwdiDir "libwdi\.msvc\libwdi_static.vcxproj"
$solnDir = ($libwdiDir -replace '\\', '/') + '/'
Write-Host "==> Building libwdi static lib (x64-only, co-installer-free)..." -ForegroundColor Cyan
& $msbuild $staticVcx `
    /p:Configuration=$Config /p:Platform=$Platform "/p:SolutionDir=$solnDir" `
    /m /v:minimal
# Verify the artifact we need was produced (don't hard-fail solely on exit code).

$libwdiLib = Get-ChildItem -Path $libwdiDir -Recurse -Filter "libwdi.lib" `
    -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "\\$Platform\\$Config\\" } |
    Select-Object -First 1
if (-not $libwdiLib) {
    $libwdiLib = Get-ChildItem -Path $libwdiDir -Recurse -Filter "libwdi.lib" `
        -ErrorAction SilentlyContinue | Select-Object -First 1
}
$libwdiHeader = Join-Path $libwdiDir "libwdi\libwdi.h"

if (-not $libwdiLib -or -not (Test-Path $libwdiHeader)) {
    throw "libwdi did not build. Expected libwdi.lib + libwdi\libwdi.h. " +
          "Re-run from a 'Developer PowerShell for VS' with the v143 x64 toolset installed."
}
Write-Host "    libwdi.lib: $($libwdiLib.FullName)" -ForegroundColor DarkGray

# --- 3. Stage headers + lib -------------------------------------------------
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
Copy-Item $libwdiHeader (Join-Path $stageDir "libwdi.h") -Force
Copy-Item $libwdiLib.FullName (Join-Path $stageDir "libwdi.lib") -Force

# --- 4. Configure + build helper with CMake --------------------------------
$cmake = Find-CMake
Write-Host "==> Configuring helper with CMake..." -ForegroundColor Cyan

function Invoke-CMakeConfigure {
    param([string]$Gen)
    $cmArgs = @('-S', $scriptDir, '-B', $buildDir, '-A', $Platform, "-DLIBWDI_ROOT=$stageDir")
    if ($Toolset) { $cmArgs += @('-T', $Toolset) }
    if ($Gen)     { $cmArgs = @('-G', $Gen) + $cmArgs }
    # Pipe to Out-Host so CMake's stdout doesn't leak into the function's return value.
    & $cmake @cmArgs | Out-Host
    return $LASTEXITCODE
}

$rc = Invoke-CMakeConfigure -Gen $Generator
if ($rc -ne 0 -and $Generator) {
    Write-Warning "CMake configure with -G '$Generator' failed (rc=$rc); retrying with auto-detected generator."
    Remove-Item -Recurse -Force $buildDir -ErrorAction SilentlyContinue
    $rc = Invoke-CMakeConfigure -Gen ""
}
if ($rc -ne 0) {
    throw "CMake configure failed. Ensure CMake (from the VS C++ workload) and the '$Toolset' toolset are installed."
}

Write-Host "==> Building helper..." -ForegroundColor Cyan
& $cmake --build $buildDir --config $Config
if ($LASTEXITCODE -ne 0) { throw "CMake build failed." }

$exe = Get-ChildItem -Path $buildDir -Recurse -Filter "DisplayDial.DriverSetup.exe" |
    Select-Object -First 1
if (-not $exe) { throw "Build succeeded but DisplayDial.DriverSetup.exe was not found." }
Write-Host "    Built: $($exe.FullName)" -ForegroundColor Green

# --- 5. Copy next to the app -----------------------------------------------
# Canonical drop location the app's .csproj copies from (gitignored).
$distDir = Join-Path $scriptDir "dist"
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
Copy-Item $exe.FullName (Join-Path $distDir "DisplayDial.DriverSetup.exe") -Force
Write-Host "==> Staged helper to $distDir" -ForegroundColor Green

if ([string]::IsNullOrWhiteSpace($AppOutputDir)) {
    $AppOutputDir = Join-Path $scriptDir "..\..\src\DisplayDial\bin\$Config\net10.0-windows"
}
if (Test-Path $AppOutputDir) {
    Copy-Item $exe.FullName (Join-Path $AppOutputDir "DisplayDial.DriverSetup.exe") -Force
    Write-Host "==> Copied helper to $AppOutputDir" -ForegroundColor Green
} else {
    Write-Warning "App output dir '$AppOutputDir' not found; it will be picked up from dist\ next time you build the app."
}

Write-Host "==> Done." -ForegroundColor Cyan
