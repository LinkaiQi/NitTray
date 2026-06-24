cmake_minimum_required(VERSION 3.20)

project(NitTray.DriverSetup C)

# ---------------------------------------------------------------------------
# NitTray.DriverSetup — elevated WinUSB installer (links libwdi statically).
#
# This target can only be built on Windows with MSVC, because libwdi embeds the
# Microsoft WinUSB co-installer binaries (which require the Windows Driver Kit)
# and links against Windows-only system libraries.
#
# You must point the build at a pre-built libwdi via -DLIBWDI_ROOT=<dir>, where
# <dir> contains:
#     libwdi.h
#     libwdi.lib   (static, built /MT Release x64)
#
# build.ps1 in this folder automates fetching + building libwdi and then
# configuring this project. Prefer running that.
# ---------------------------------------------------------------------------

if(NOT WIN32)
    message(FATAL_ERROR "NitTray.DriverSetup can only be built on Windows (it links libwdi/WinUSB).")
endif()

if(NOT MSVC)
    message(WARNING "NitTray.DriverSetup is designed for MSVC; other toolchains are untested.")
endif()

set(LIBWDI_ROOT "" CACHE PATH "Directory containing libwdi.h and libwdi.lib")

find_path(LIBWDI_INCLUDE_DIR
    NAMES libwdi.h
    HINTS "${LIBWDI_ROOT}" "${LIBWDI_ROOT}/include"
)
find_library(LIBWDI_LIBRARY
    NAMES libwdi wdi
    HINTS "${LIBWDI_ROOT}" "${LIBWDI_ROOT}/lib"
)

if(NOT LIBWDI_INCLUDE_DIR OR NOT LIBWDI_LIBRARY)
    message(FATAL_ERROR
        "Could not find libwdi. Pass -DLIBWDI_ROOT=<dir> pointing at a folder "
        "that contains libwdi.h and libwdi.lib. (LIBWDI_INCLUDE_DIR='${LIBWDI_INCLUDE_DIR}', "
        "LIBWDI_LIBRARY='${LIBWDI_LIBRARY}')")
endif()

add_executable(NitTray.DriverSetup
    main.c
    resources.rc
)

target_include_directories(NitTray.DriverSetup PRIVATE "${LIBWDI_INCLUDE_DIR}")

target_link_libraries(NitTray.DriverSetup PRIVATE
    "${LIBWDI_LIBRARY}"
    setupapi
    ole32
    newdev
    shell32
    advapi32
    user32
    cfgmgr32
    wintrust
    crypt32
)

# Statically link the CRT so the helper is a single self-contained .exe and
# matches a /MT-built libwdi. Keep this consistent with build.ps1.
set_property(TARGET NitTray.DriverSetup
    PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

if(MSVC)
    # We embed our own requireAdministrator manifest via resources.rc, so stop
    # the linker from generating/embedding a second (conflicting) manifest.
    target_link_options(NitTray.DriverSetup PRIVATE "/MANIFEST:NO")
endif()

# 64-bit only (libwdi's embedded installer matches the host architecture).
set_target_properties(NitTray.DriverSetup PROPERTIES OUTPUT_NAME "NitTray.DriverSetup")
