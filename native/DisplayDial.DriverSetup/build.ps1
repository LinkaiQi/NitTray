<#
.SYNOPSIS
    Builds DisplayDial.DriverSetup.exe (the elevated WinUSB installer helper).

.DESCRIPTION
    1. Clones libwdi (pinned tag) into native/third_party/libwdi if absent.
    2. Builds the libwdi static library (Release|x64) with MSBuild.
    3. Stages libwdi.h + libwdi.lib into a single folder.
    4. Configures and builds this helper with CMake, linking libwdi.
    5. Copies DisplayDial.DriverSetup.exe next to the DisplayDial app output so
       the tray app can find and launch it.

    MUST be run on Windows from a "Developer PowerShell for VS 2022" (or any shell
    where MSBuild + CMake are on PATH).

.PARAMETER Config
    Build configuration (default: Release).

.PARAMETER Platform
    Target platform (default: x64). libwdi's embedded installer is architecture
    specific; x64 is what DisplayDial ships.

.PARAMETER LibwdiTag
    Git tag of libwdi to build (default: v1.5.1).

.PARAMETER AppOutputDir
    Where to copy the built helper. Defaults to the DisplayDial Release output
    (..\..\src\DisplayDial\bin\<Config>\net10.0-windows).

.NOTES
    Building libwdi requires the Windows Driver Kit (WDK). libwdi embeds the
    WinUSB co-installer from the WDK redistributables. If MSBuild fails while
    building libwdi, open native\third_party\libwdi\msvc\config.h and set WDK_DIR
    to your installed kit (e.g. "C:/Program Files (x86)/Windows Kits/10"), or
    install the WDK from https://learn.microsoft.com/windows-hardware/drivers/download-the-wdk
#>
[CmdletBinding()]
param(
    [string]$Config = "Release",
    [string]$Platform = "x64",
    [string]$LibwdiTag = "v1.5.1",
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
        $path = & $vswhere -latest -requires Microsoft.Component.MSBuild `
            -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
        if ($path) { return $path }
    }
    throw "MSBuild.exe not found. Run this from a 'Developer PowerShell for VS 2022' prompt."
}

function Find-CMake {
    $cmd = Get-Command cmake.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "cmake.exe not found. Install CMake or add it to PATH."
}

Write-Host "==> DisplayDial.DriverSetup build" -ForegroundColor Cyan
Write-Host "    Config=$Config Platform=$Platform libwdi=$LibwdiTag"

# --- 1. Fetch libwdi --------------------------------------------------------
New-Item -ItemType Directory -Force -Path $thirdParty | Out-Null
if (-not (Test-Path (Join-Path $libwdiDir "libwdi.sln"))) {
    Write-Host "==> Cloning libwdi $LibwdiTag..." -ForegroundColor Cyan
    git clone --depth 1 --branch $LibwdiTag https://github.com/pbatard/libwdi.git $libwdiDir
} else {
    Write-Host "==> libwdi already present, skipping clone." -ForegroundColor DarkGray
}

# --- 2. Build libwdi static lib --------------------------------------------
$msbuild = Find-MSBuild
Write-Host "==> Building libwdi (this requires the WDK)..." -ForegroundColor Cyan
& $msbuild (Join-Path $libwdiDir "libwdi.sln") `
    /p:Configuration=$Config /p:Platform=$Platform /m /v:minimal
# Don't hard-fail on MSBuild exit code (the 'zadig' project may fail in minimal
# environments); verify the static lib we actually need was produced.

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
          "See the NOTES in this script about WDK_DIR (msvc\config.h)."
}
Write-Host "    libwdi.lib: $($libwdiLib.FullName)" -ForegroundColor DarkGray

# --- 3. Stage headers + lib -------------------------------------------------
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
Copy-Item $libwdiHeader (Join-Path $stageDir "libwdi.h") -Force
Copy-Item $libwdiLib.FullName (Join-Path $stageDir "libwdi.lib") -Force

# --- 4. Configure + build helper with CMake --------------------------------
$cmake = Find-CMake
$generator = "Visual Studio 17 2022"
Write-Host "==> Configuring helper with CMake..." -ForegroundColor Cyan
& $cmake -S $scriptDir -B $buildDir -G $generator -A $Platform "-DLIBWDI_ROOT=$stageDir"
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed." }

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
