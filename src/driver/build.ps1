<#
.SYNOPSIS
    Builds NitTray.DriverSetup.exe (the elevated WinUSB installer helper).

.DESCRIPTION
    1. Clones libwdi (pinned tag) into src/driver/third_party/libwdi if absent.
    2. Patches libwdi to build co-installer-free (see NOTES). By default it also
       trims libwdi to x64-only so no WDK and no ARM64 tools are needed; pass
       -SupportArm64 to additionally embed the ARM64 installer (see "ARM64 support").
    3. Builds the libwdi static library (Release|x64) with MSBuild.
    4. Stages libwdi.h + libwdi.lib into a single folder.
    5. Configures and builds this helper with CMake, linking libwdi.
    6. Copies NitTray.DriverSetup.exe next to the NitTray app output so
       the tray app can find and launch it.

    MUST be run on Windows from a "Developer PowerShell for VS" prompt (VS 2022 or
    VS 2026), so MSBuild + CMake + the v143 toolset are on PATH.

.PARAMETER Config
    Build configuration (default: Release).

.PARAMETER Platform
    Host architecture of the helper exe (default: x64). The helper is always a
    single x64 binary; -SupportArm64 controls which target installers it can run, not
    the helper's own architecture (see "ARM64 support" in NOTES).

.PARAMETER LibwdiTag
    Git tag of libwdi to build (default: v1.5.1).

.PARAMETER Toolset
    MSVC platform toolset for the CMake helper build (default: v143). Must match
    the toolset libwdi is built with (libwdi pins v143) so the static CRT objects
    link cleanly.

.PARAMETER Generator
    CMake generator override (e.g. "Visual Studio 18 2026"). Leave empty to let
    CMake auto-detect the installed Visual Studio.

.PARAMETER SupportArm64
    Also embed the ARM64 WinUSB installer so the (x64) helper works on Windows on
    ARM. Requires the "MSVC v143 - ARM64 build tools" component. See "ARM64
    support" in NOTES.

.PARAMETER AppOutputDir
    Where to copy the built helper. Defaults to the NitTray Release output
    (..\client\bin\<Config>\net10.0-windows).

.NOTES
    *** By default, no WDK and no ARM64 build tools are required. ***

    Stock libwdi targets x86 + x64 + ARM64, supports three driver backends
    (WinUSB + libusb-win32 + libusbK), and embeds the legacy WinUSB/WDF
    co-installer DLLs from the Windows Driver Kit. That breaks on a modern
    toolchain for three reasons:

      1. Its static-library project references an ARM64 installer project, so the
         build fails (MSB8020) unless the ARM64 v143 tools are installed.
      2. The modern WDK (Windows 10 1809+) no longer ships the co-installer DLLs
         libwdi tries to embed, so the "embedder" step fails opening them.
      3. config.h enables the libusb-win32 + libusbK backends and points them at
         the libwdi author's local D:\libusb-win32 / D:\libusbK folders, so the
         embedder also fails opening driver files you don't have (e.g.
         "Could not open file 'D:\libusb-win32\bin\x86\install-filter.exe'").

    We only ever install WinUSB. Co-installers have been unnecessary since Windows
    10 (WinUSB is in-box), and libwdi's own ARM64 path already installs WinUSB
    inbox with no co-installer. Invoke-LibwdiPatch makes every install path behave
    that way:

      * msvc/config.h            -> disable the non-WinUSB backends (comment out
                                    LIBUSB0_DIR + LIBUSBK_DIR). WDK_DIR stays
                                    defined so WinUSB remains a supported driver
                                    type and embedder.h still sees >=1 backend;
                                    libwdi.c guards its libusb code behind
                                    #if defined(LIBUSB0_DIR|LIBUSBK_DIR), so it
                                    compiles out.
      * libwdi/embedder_files.h  -> drop the 4 WinUSB/WDF co-installer embeds.
      * libwdi/winusb.inf.in     -> blank the x86 + amd64 CoInstallers +
                                    SourceDisksFiles sections (mirrors the
                                    already-shipping arm64 sections).

    winusb.cat.in is intentionally left untouched: libwdi's catalog builder hashes
    only the files actually present in the package dir (and always adds the INF),
    so the now-absent co-installers are simply skipped -- exactly as on arm64.

    --- Default (x64-only) build ---------------------------------------------
    Additionally trims libwdi so no WDK / ARM64 tools are needed:
      * msvc/config.h                 -> also comment out OPT_M32 + OPT_ARM
                                         (keep OPT_M64) on top of the backend
                                         disable above. WDK_DIR stays *defined*:
                                         it is only a compile-time gate in
                                         libwdi.c that keeps WinUSB a supported
                                         driver type; its path is never read once
                                         embeds are removed.
      * libwdi/.msvc/libwdi_static.vcxproj
                                      -> drop the embedder + installer_arm64 +
                                         installer_x86 project references.
    The static lib is then built WITHOUT the solution. Because libwdi's "embedder"
    and "detect_64build" are Win32-only *host* build tools, building the static
    .vcxproj directly with /p:Platform=x64 would wrongly force x64 onto them
    (MSB8013). The solution normally pins those host tools to Win32; we reproduce
    that by building, in order: embedder (Win32), installer_x64 (x64), then the
    static lib (x64) with /p:BuildProjectReferences=false so it consumes the
    prebuilt tools instead of re-platforming them. That flag stops the references
    being *built* but not *resolved* -- MSBuild still config-checks each reference
    against Release|x64, and Win32-only embedder.vcxproj fails that check -- so the
    embedder reference is dropped from the .vcxproj above (installer_x64, which has
    a real x64 config, is kept). The static lib's PreBuildEvent still runs the
    prebuilt embedder by path to generate embedded.h.

    --- ARM64 support (-SupportArm64) -----------------------------------------------
    libwdi has no ARM64 *solution* platform; instead the static lib is built for
    the host arch (x64) and EMBEDS an installer exe for each target arch. At
    runtime wdi_install_driver picks installer_<os-arch>.exe based on the native
    OS architecture (libwdi.c GetPlatformArch via IsWow64Process2) -- even when the
    x64 helper runs under emulation on Windows on ARM. So a single x64 helper that
    embeds installer_arm64.exe works on BOTH x64 and ARM64 (this is exactly how
    Zadig ships one binary for every architecture). No separate ARM64 helper and
    no per-RID bundling are required.

    With -SupportArm64 we therefore leave config.h's arch options and the .vcxproj
    stock (so the static lib embeds the x86/x64/arm64 installers; the libusb0/
    libusbK backends are still disabled as above) and build the whole .sln as
    Release|x64. The .sln pins each installer project to its own architecture
    (installer_arm64 -> ARM64, embedder host-tool -> Win32) regardless of the
    solution platform, which is why a .sln build -- not a direct .vcxproj build --
    is used in this mode. This requires the "MSVC v143 - ARM64 build tools"
    component (the x86 tools come with the standard C++ workload). The resulting
    NitTray.DriverSetup.exe is still a single x64 file; ship it as-is for both
    x64 and ARM64 releases.

    The patch is applied to a pristine checkout every run (git checkout -- .), so
    re-running build.ps1 -- including switching -SupportArm64 on/off -- is idempotent.
#>
[CmdletBinding()]
param(
    [string]$Config = "Release",
    [string]$Platform = "x64",
    [string]$LibwdiTag = "v1.5.1",
    [string]$Toolset = "v143",
    [string]$Generator = "",
    [switch]$SupportArm64,
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

    # Visual Studio bundles CMake with the "C++ CMake tools for Windows" component
    # (included in the Desktop C++ workload), but only puts it on PATH inside a
    # "Developer PowerShell for VS". Locate it via vswhere -- same as Find-MSBuild --
    # so build.ps1 works from a plain PowerShell too.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        foreach ($glob in @(
            "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
            "**\CMake\**\bin\cmake.exe")) {
            $path = & $vswhere -latest -prerelease -find $glob | Select-Object -First 1
            if ($path) { return $path }
        }
    }

    # Standalone CMake (cmake.org installer) in its default locations.
    foreach ($p in @(
        (Join-Path $env:ProgramFiles "CMake\bin\cmake.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "CMake\bin\cmake.exe"))) {
        if ($p -and (Test-Path $p)) { return $p }
    }

    throw "cmake.exe not found. Either (a) in the Visual Studio Installer add the " +
          "'C++ CMake tools for Windows' component to your Desktop C++ workload, or " +
          "(b) install standalone CMake from https://cmake.org/download/ and add it to " +
          "PATH. Then re-run build.ps1."
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

# Patch a pristine libwdi checkout to build co-installer-free. By default also
# trims it to x64-only; with -IncludeArm64 the config.h/.vcxproj are left stock so
# the .sln build embeds the x86/x64/arm64 installers. See the NOTES block above.
function Invoke-LibwdiPatch {
    param([string]$Root, [switch]$IncludeArm64)

    $configH   = Join-Path $Root "msvc\config.h"
    $embedderH = Join-Path $Root "libwdi\embedder_files.h"
    $infIn     = Join-Path $Root "libwdi\winusb.inf.in"
    $staticVcx = Join-Path $Root "libwdi\.msvc\libwdi_static.vcxproj"
    foreach ($f in @($configH, $embedderH, $infIn, $staticVcx)) {
        if (-not (Test-Path $f)) { throw "Expected libwdi file is missing: $f" }
    }

    # Reset to pristine so re-running build.ps1 (or switching -SupportArm64) re-patches cleanly.
    if (Test-Path (Join-Path $Root ".git")) {
        & git -C $Root checkout -- . 2>$null | Out-Null
    }

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $utf8Bom   = New-Object System.Text.UTF8Encoding($true)

    $mode = if ($IncludeArm64) { "x64 + ARM64 universal" } else { "x64-only" }
    Write-Host "==> Patching libwdi ($mode, WinUSB-only, co-installer-free)..." -ForegroundColor Cyan

    # (A) config.h --
    #   * ALWAYS disable the non-WinUSB backends. Stock config.h points LIBUSB0_DIR at
    #     "D:/libusb-win32" and LIBUSBK_DIR at "D:/libusbK/bin" -- paths only the libwdi
    #     author has -- so the embedder tries to bundle those driver files and hard-fails
    #     ("Could not open file 'D:\libusb-win32\bin\x86\install-filter.exe'"). We only
    #     ever install WinUSB. WDK_DIR stays defined so WinUSB remains a supported driver
    #     type (and embedder.h still sees >=1 backend dir; libwdi.c guards the libusb0/
    #     libusbK code behind #if defined(LIBUSB0_DIR|LIBUSBK_DIR), so it compiles out).
    #   * x64-only mode additionally comments OPT_M32 + OPT_ARM (keeps OPT_M64);
    #     -SupportArm64 leaves the arch options stock so the .sln embeds x86/x64/arm64.
    $t = [System.IO.File]::ReadAllText($configH)
    foreach ($dir in @('LIBUSB0_DIR', 'LIBUSBK_DIR')) {
        $t = [regex]::Replace($t, "(?m)^#define $dir\b", "//#define $dir")
        Assert-PatchCount ([regex]::Matches($t, "(?m)^//#define $dir\b").Count) 1 "config.h $dir disabled"
    }
    if (-not $IncludeArm64) {
        foreach ($opt in @('OPT_M32', 'OPT_ARM')) {
            $t = [regex]::Replace($t, "(?m)^#define $opt\b", "//#define $opt")
            Assert-PatchCount ([regex]::Matches($t, "(?m)^//#define $opt\b").Count) 1 "config.h $opt disabled"
        }
        Assert-PatchCount ([regex]::Matches($t, '(?m)^#define OPT_M64\b').Count) 1 "config.h OPT_M64 kept"
    }
    [System.IO.File]::WriteAllText($configH, $t, $utf8NoBom)

    # (B) embedder_files.h -- drop the 4 WinUSB/WDF co-installer embeds (always;
    #     the modern WDK no longer ships them and embedder hard-fails on a missing
    #     file). Inbox WinUSB needs no co-installer on Windows 10+.
    $t = [System.IO.File]::ReadAllText($embedderH)
    Assert-PatchCount ([regex]::Matches($t, 'WDK_DIR "\\\\redist').Count) 4 "embedder_files.h co-installer embeds present"
    $t = [regex]::Replace($t, '(?m)^[^\r\n]*WDK_DIR "\\\\redist[^\r\n]*\r?\n', '')
    Assert-PatchCount ([regex]::Matches($t, 'WDK_DIR "\\\\redist').Count) 0 "embedder_files.h co-installer embeds removed"
    [System.IO.File]::WriteAllText($embedderH, $t, $utf8NoBom)

    # (C) winusb.inf.in -- make every per-arch install path co-installer-free
    #     (mirror the arm64 sections that already ship that way).
    $t = [System.IO.File]::ReadAllText($infIn)
    $eol = if ($t.Contains("`r`n")) { "`r`n" } else { "`n" }
    foreach ($a in @('x86', 'amd64')) {
        $coPat = "(?m)^\[USB_Install\.NT$a\.CoInstallers\]\r?\nAddReg\s*=\s*CoInstallers_AddReg\r?\nCopyFiles\s*=\s*CoInstallers_CopyFiles\r?\n"
        Assert-PatchCount ([regex]::Matches($t, $coPat).Count) 1 "inf NT$a.CoInstallers section"
        $t = [regex]::Replace($t, $coPat, "[USB_Install.NT$a.CoInstallers]$eol;$eol")
        $sdPat = "(?m)^\[SourceDisksFiles\.$a\]\r?\nWinUSBCoInstaller2\.dll = 1,$a\r?\nWdfCoInstaller#WDF_VERSION#\.dll = 1,$a\r?\n"
        Assert-PatchCount ([regex]::Matches($t, $sdPat).Count) 1 "inf SourceDisksFiles.$a section"
        $t = [regex]::Replace($t, $sdPat, "[SourceDisksFiles.$a]$eol;$eol")
    }
    Assert-PatchCount ([regex]::Matches($t, 'WinUSBCoInstaller2\.dll = 1,').Count) 0 "inf co-installer source refs removed"
    [System.IO.File]::WriteAllText($infIn, $t, $utf8NoBom)

    if ($IncludeArm64) {
        # Universal build: keep the config.h arch options + the .vcxproj stock so the
        # static lib embeds installer_x86/x64/arm64. The .sln build pins each installer
        # to its own arch, and a single x64 helper serves x64 AND ARM64 (libwdi selects
        # installer_arm64.exe at runtime on ARM64). The libusb0/libusbK backends are
        # still disabled above (we only use WinUSB).
        Write-Host "    libwdi patched: WinUSB-only, co-installer-free; arch options + .vcxproj left stock for the .sln (x86+x64+arm64) build." -ForegroundColor DarkGray
        return
    }

    # (D) libwdi_static.vcxproj -- drop the embedder + arm64 + x86 installer project
    #     references. We build embedder (Win32) and installer_x64 (x64) in separate
    #     MSBuild calls before this, and the static lib's PreBuildEvent invokes
    #     embedder.exe by path -- so these references are only build-order hints.
    #     Critically, /p:BuildProjectReferences=false stops them being *built* but NOT
    #     *resolved*: MSBuild still config-checks each reference against the parent's
    #     Release|x64, and Win32-only embedder.vcxproj then fails with MSB8013. Dropping
    #     the reference avoids that. installer_x64.vcxproj is kept (it has a real x64
    #     config, so it resolves cleanly and is skipped by BuildProjectReferences=false).
    $t = [System.IO.File]::ReadAllText($staticVcx)
    foreach ($name in @('embedder.vcxproj', 'installer_arm64.vcxproj', 'installer_x86.vcxproj')) {
        $pat = '(?s)[ \t]*<ProjectReference Include="' + [regex]::Escape($name) + '">.*?</ProjectReference>\r?\n'
        Assert-PatchCount ([regex]::Matches($t, $pat).Count) 1 "vcxproj $name reference"
        $t = [regex]::Replace($t, $pat, '')
    }
    Assert-PatchCount ([regex]::Matches($t, 'embedder\.vcxproj').Count)        0 "vcxproj embedder ref removed"
    Assert-PatchCount ([regex]::Matches($t, 'installer_arm64\.vcxproj').Count) 0 "vcxproj arm64 ref removed"
    Assert-PatchCount ([regex]::Matches($t, 'installer_x86\.vcxproj').Count)   0 "vcxproj x86 ref removed"
    Assert-PatchCount ([regex]::Matches($t, 'installer_x64\.vcxproj').Count)   1 "vcxproj x64 ref kept"
    [System.IO.File]::WriteAllText($staticVcx, $t, $utf8Bom)  # this file ships with a UTF-8 BOM

    Write-Host "    libwdi patched: x64-only, WinUSB-only, no co-installer, no WDK/ARM64 tools needed." -ForegroundColor DarkGray
}

$buildMode = if ($SupportArm64) { "x64 + ARM64 universal" } else { "x64-only" }
Write-Host "==> NitTray.DriverSetup build" -ForegroundColor Cyan
Write-Host "    Config=$Config Platform=$Platform libwdi=$LibwdiTag toolset=$Toolset mode=$buildMode"

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
Invoke-LibwdiPatch -Root $libwdiDir -IncludeArm64:$SupportArm64

$libwdiSln       = Join-Path $libwdiDir "libwdi.sln"
$staticVcx       = Join-Path $libwdiDir "libwdi\.msvc\libwdi_static.vcxproj"
$embedderVcx     = Join-Path $libwdiDir "libwdi\.msvc\embedder.vcxproj"
$installerX64Vcx = Join-Path $libwdiDir "libwdi\.msvc\installer_x64.vcxproj"

# $(SolutionDir) for direct .vcxproj builds (the libwdi clone root, where the .sln
# lives). Forward slashes avoid a trailing-backslash quoting pitfall, and the value
# must match across the host-tool, installer and static-lib builds so the embedder
# finds the installer exe it bakes in (see the per-step notes below).
$solnDir = ($libwdiDir -replace '\\', '/') + '/'

if ($SupportArm64) {
    # Build the whole .sln so the per-project arch pinning applies
    # (installer_arm64 -> ARM64, embedder host-tool -> Win32). The static lib then
    # embeds the x86/x64/arm64 installers. Unrelated .sln projects may fail in a
    # minimal environment, so verify libwdi.lib below rather than trust exit code.
    Write-Host "==> Building libwdi via solution (embeds x86/x64/ARM64 installers)..." -ForegroundColor Cyan
    Write-Host "    Requires the 'MSVC v143 - ARM64 build tools' component." -ForegroundColor DarkGray
    & $msbuild $libwdiSln /p:Configuration=$Config /p:Platform=x64 /m /v:minimal
} else {
    # x64-only, no solution. libwdi's "embedder" + "detect_64build" are Win32-only
    # HOST build tools; building libwdi_static.vcxproj directly with /p:Platform=x64
    # would wrongly force x64 onto them (MSB8013 "doesn't contain Release|x64"). The
    # libwdi solution normally pins those host tools to Win32 via its config map, so
    # we reproduce that by hand:
    #   1. build embedder (the host tool) as Win32,
    #   2. build installer_x64 (the payload the static lib embeds) as x64,
    #   3. build the static lib as x64 with /p:BuildProjectReferences=false so it
    #      does NOT rebuild (and mis-platform) those projects; its PreBuildEvent
    #      still runs the prebuilt embedder to generate embedded.h.
    # The embedder locates the installer via SOLUTIONDIR (default "..") relative to
    # its working dir (the libwdi source dir), so the matching /p:SolutionDir on the
    # installer + static-lib builds makes the paths line up.
    Write-Host "==> Building libwdi host tool (embedder, Win32)..." -ForegroundColor Cyan
    & $msbuild $embedderVcx `
        /p:Configuration=$Config /p:Platform=Win32 "/p:SolutionDir=$solnDir" /m /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Failed to build libwdi's embedder host tool." }

    Write-Host "==> Building libwdi installer payload (installer_x64, x64)..." -ForegroundColor Cyan
    & $msbuild $installerX64Vcx `
        /p:Configuration=$Config /p:Platform=x64 "/p:SolutionDir=$solnDir" /m /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Failed to build libwdi's installer_x64 payload." }

    Write-Host "==> Building libwdi static lib (x64-only, co-installer-free)..." -ForegroundColor Cyan
    & $msbuild $staticVcx `
        /p:Configuration=$Config /p:Platform=$Platform /p:BuildProjectReferences=false `
        "/p:SolutionDir=$solnDir" /m /v:minimal
}
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
    $hint = if ($SupportArm64) {
        "In -SupportArm64 mode you also need the 'MSVC v143 - ARM64 build tools' component."
    } else {
        "Re-run from a 'Developer PowerShell for VS' with the v143 x64 toolset installed."
    }
    throw "libwdi did not build. Expected libwdi.lib + libwdi\libwdi.h. $hint"
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

$exe = Get-ChildItem -Path $buildDir -Recurse -Filter "NitTray.DriverSetup.exe" |
    Select-Object -First 1
if (-not $exe) { throw "Build succeeded but NitTray.DriverSetup.exe was not found." }
Write-Host "    Built: $($exe.FullName)" -ForegroundColor Green

# --- 5. Copy next to the app -----------------------------------------------
# Canonical drop location the app's .csproj copies from (gitignored).
$distDir = Join-Path $scriptDir "dist"
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
Copy-Item $exe.FullName (Join-Path $distDir "NitTray.DriverSetup.exe") -Force
Write-Host "==> Staged helper to $distDir" -ForegroundColor Green

if ([string]::IsNullOrWhiteSpace($AppOutputDir)) {
    $AppOutputDir = Join-Path $scriptDir "..\client\bin\$Config\net10.0-windows"
}
if (Test-Path $AppOutputDir) {
    Copy-Item $exe.FullName (Join-Path $AppOutputDir "NitTray.DriverSetup.exe") -Force
    Write-Host "==> Copied helper to $AppOutputDir" -ForegroundColor Green
} else {
    Write-Warning "App output dir '$AppOutputDir' not found; it will be picked up from dist\ next time you build the app."
}

Write-Host "==> Done." -ForegroundColor Cyan
