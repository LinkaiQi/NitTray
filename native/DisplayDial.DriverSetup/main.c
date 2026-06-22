/*
 * DisplayDial.DriverSetup -- elevated WinUSB installer for Apple displays.
 *
 * This tiny console helper is launched by the DisplayDial tray app (elevated,
 * via the "runas" shell verb) to bind Microsoft's in-box WinUSB driver to an
 * Apple display whose brightness HID interface Windows' generic hidclass.sys
 * refuses (the Apple Pro Display XDR, VID_05AC / PID_9243).
 *
 * It uses libwdi (the engine behind Zadig) to generate a WinUSB INF, self-sign
 * a catalog, install the certificate into the Trusted Publisher store, and bind
 * WinUSB to the *whole composite device* (the parent node, no MI_ suffix). That
 * whole-device binding is what lets DisplayDial open a single WinUSB handle and
 * reach the brightness interface via WinUsb_GetAssociatedInterface.
 *
 * Usage:    DisplayDial.DriverSetup.exe install <VID-hex> <PID-hex>
 * Example:  DisplayDial.DriverSetup.exe install 05AC 9243
 *
 * The result is communicated purely through the process exit code (see the
 * EXIT_* values below -- keep these in sync with the C# DriverSetupExitCodes).
 * Human-readable detail is appended to:
 *     %LOCALAPPDATA%\DisplayDial\driver-setup.log
 */

#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include "libwdi.h"

/* Exit-code contract shared with src/DisplayDial/Services/DriverSetupExitCodes.cs */
#define EXIT_OK_SUCCESS       0
#define EXIT_GENERIC_ERROR    1
#define EXIT_BAD_ARGUMENTS    2
#define EXIT_DEVICE_NOT_FOUND 3
#define EXIT_PREPARE_FAILED   4
#define EXIT_INSTALL_FAILED   5

#define INF_NAME "apple_display.inf"

static FILE* g_log = NULL;

static void log_open(void)
{
    char path[MAX_PATH];
    const char* base = getenv("LOCALAPPDATA");
    if (base == NULL || base[0] == '\0') {
        base = getenv("TEMP");
    }
    if (base == NULL || base[0] == '\0') {
        return;
    }

    /* Ensure %LOCALAPPDATA%\DisplayDial exists (CreateDirectory is a no-op if it does). */
    _snprintf_s(path, sizeof(path), _TRUNCATE, "%s\\DisplayDial", base);
    CreateDirectoryA(path, NULL);

    _snprintf_s(path, sizeof(path), _TRUNCATE, "%s\\DisplayDial\\driver-setup.log", base);
    g_log = fopen(path, "a");
}

static void log_msg(const char* fmt, ...)
{
    va_list args;

    if (g_log == NULL) {
        return;
    }

    {
        time_t now = time(NULL);
        struct tm tmv;
        char stamp[32];
        localtime_s(&tmv, &now);
        strftime(stamp, sizeof(stamp), "%Y-%m-%d %H:%M:%S", &tmv);
        fprintf(g_log, "%s  ", stamp);
    }

    va_start(args, fmt);
    vfprintf(g_log, fmt, args);
    va_end(args);

    fprintf(g_log, "\n");
    fflush(g_log);
}

static void log_close(void)
{
    if (g_log != NULL) {
        fclose(g_log);
        g_log = NULL;
    }
}

/* Case-insensitive substring search. Returns 1 if 'needle' is found in 'hay'. */
static int contains_ci(const char* hay, const char* needle)
{
    size_t nlen, i;
    if (hay == NULL || needle == NULL) {
        return 0;
    }
    nlen = strlen(needle);
    if (nlen == 0) {
        return 1;
    }
    for (i = 0; hay[i] != '\0'; i++) {
        if (_strnicmp(&hay[i], needle, nlen) == 0) {
            return 1;
        }
    }
    return 0;
}

int __cdecl main(int argc, char** argv)
{
    struct wdi_device_info dev = { NULL, 0, 0, FALSE, 0,
                                   "Apple Display (WinUSB)", NULL, NULL, NULL, NULL, NULL, 0 };
    struct wdi_options_create_list ocl = { 0 };
    struct wdi_options_prepare_driver opd = { 0 };
    struct wdi_options_install_driver oid = { 0 };
    struct wdi_device_info *list = NULL, *node = NULL, *match = NULL;
    unsigned long vid = 0, pid = 0;
    char ext_dir[MAX_PATH];
    int r, device_present = 0, exit_code = EXIT_GENERIC_ERROR;

    log_open();

    if (argc < 4 || _stricmp(argv[1], "install") != 0) {
        log_msg("Bad arguments. Usage: DisplayDial.DriverSetup.exe install <VID-hex> <PID-hex>");
        log_close();
        return EXIT_BAD_ARGUMENTS;
    }

    vid = strtoul(argv[2], NULL, 16);
    pid = strtoul(argv[3], NULL, 16);
    if (vid == 0 || vid > 0xFFFF || pid == 0 || pid > 0xFFFF) {
        log_msg("Bad VID/PID arguments: vid='%s' pid='%s'", argv[2], argv[3]);
        log_close();
        return EXIT_BAD_ARGUMENTS;
    }

    dev.vid = (unsigned short)vid;
    dev.pid = (unsigned short)pid;
    log_msg("DisplayDial.DriverSetup starting: install VID_%04X PID_%04X.", dev.vid, dev.pid);

    /* Extraction directory for the generated INF / catalog / signed cert. */
    {
        char temp[MAX_PATH];
        DWORD n = GetTempPathA(sizeof(temp), temp);
        if (n == 0 || n > sizeof(temp)) {
            strcpy_s(temp, sizeof(temp), ".\\");
        }
        _snprintf_s(ext_dir, sizeof(ext_dir), _TRUNCATE, "%sDisplayDial_driver", temp);
    }
    log_msg("Driver staging directory: %s", ext_dir);

    wdi_set_log_level(WDI_LOG_LEVEL_WARNING);

    opd.driver_type = WDI_WINUSB;

    ocl.list_all = TRUE;          /* include devices that already have a driver */
    ocl.list_hubs = TRUE;         /* include composite-parent nodes (our target) */
    ocl.trim_whitespaces = TRUE;

    /*
     * Walk the device list so we can (a) confirm the display is actually present
     * and (b) prefer the composite-parent node (hardware id WITHOUT "MI_") as the
     * install target -- that yields a single WinUSB device spanning all interfaces.
     */
    r = wdi_create_list(&list, &ocl);
    if (r == WDI_SUCCESS) {
        for (node = list; node != NULL; node = node->next) {
            if (node->vid != dev.vid || node->pid != dev.pid) {
                continue;
            }
            device_present = 1;
            log_msg("  candidate: hardware_id='%s' mi=%u is_composite=%d driver='%s' desc='%s'",
                 node->hardware_id ? node->hardware_id : "(null)",
                 (unsigned)node->mi, node->is_composite,
                 node->driver ? node->driver : "(none)",
                 node->desc ? node->desc : "(none)");

            /* Prefer the whole-device parent (no MI_ in its hardware id). */
            if (match == NULL ||
                (node->hardware_id != NULL && !contains_ci(node->hardware_id, "MI_"))) {
                match = node;
            }
        }
    } else {
        log_msg("wdi_create_list failed: %s", wdi_strerror(r));
    }

    if (!device_present) {
        log_msg("No USB device with VID_%04X / PID_%04X is present. Aborting.", dev.vid, dev.pid);
        if (list != NULL) {
            wdi_destroy_list(list);
        }
        log_close();
        return EXIT_DEVICE_NOT_FOUND;
    }

    /*
     * Install WinUSB on the whole composite device:
     *   is_composite = FALSE, mi = 0  -> INF matches "USB\VID_xxxx&PID_xxxx"
     * which is the parent node's hardware id. (Setting is_composite/mi would
     * scope the INF to a single MI_ interface, which we do NOT want.)
     */
    dev.is_composite = FALSE;
    dev.mi = 0;
    if (match != NULL) {
        dev.hardware_id = match->hardware_id;
        dev.device_id = match->device_id;
        log_msg("Selected install target: hardware_id='%s'",
             match->hardware_id ? match->hardware_id : "(null)");
    } else {
        log_msg("No specific node selected; installing by VID/PID directly.");
    }

    log_msg("Preparing WinUSB driver (this generates + self-signs the INF/catalog)...");
    r = wdi_prepare_driver(&dev, ext_dir, INF_NAME, &opd);
    log_msg("  wdi_prepare_driver: %s", wdi_strerror(r));
    if (r != WDI_SUCCESS) {
        if (list != NULL) {
            wdi_destroy_list(list);
        }
        log_close();
        return EXIT_PREPARE_FAILED;
    }

    log_msg("Installing driver...");
    r = wdi_install_driver(&dev, ext_dir, INF_NAME, &oid);
    log_msg("  wdi_install_driver: %s", wdi_strerror(r));

    switch (r) {
    case WDI_SUCCESS:
        exit_code = EXIT_OK_SUCCESS;
        break;
    case WDI_ERROR_NO_DEVICE:
        exit_code = EXIT_DEVICE_NOT_FOUND;
        break;
    default:
        exit_code = EXIT_INSTALL_FAILED;
        break;
    }

    if (list != NULL) {
        wdi_destroy_list(list);
    }

    log_msg("Done. Exit code = %d.", exit_code);
    log_close();
    return exit_code;
}
