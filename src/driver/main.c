/*
 * NitTray.DriverSetup -- elevated WinUSB installer for Apple displays.
 *
 * This tiny console helper is launched by the NitTray tray app (elevated,
 * via the "runas" shell verb) to bind Microsoft's in-box WinUSB driver to an
 * Apple display whose brightness HID interface Windows' generic hidclass.sys
 * refuses (the Pro Display XDR, VID_05AC / PID_9243).
 *
 * It uses libwdi (the engine behind Zadig) to generate a WinUSB INF, self-sign
 * a catalog, install the certificate into the Trusted Publisher store, and bind
 * WinUSB to the *whole composite device* (the parent node, no MI_ suffix). That
 * whole-device binding is what lets NitTray open a single WinUSB handle and
 * reach the brightness interface via WinUsb_GetAssociatedInterface.
 *
 * Usage:    NitTray.DriverSetup.exe install   <VID-hex> <PID-hex>
 *           NitTray.DriverSetup.exe uninstall <VID-hex> <PID-hex>
 * Example:  NitTray.DriverSetup.exe install   05AC 9243
 *           NitTray.DriverSetup.exe uninstall 05AC 9243
 *
 * "uninstall" is the reverse operation (primarily a testing/troubleshooting
 * aid): it removes the WinUSB binding and deletes the generated OEM driver
 * package from the driver store so the device reverts to Windows' in-box
 * default driver. It uses plain SetupAPI (no libwdi), so it is independent of
 * how the driver was installed and will keep working if the install path later
 * moves to a shipped INF+CAT.
 *
 * The result is communicated purely through the process exit code (see the
 * EXIT_* values below -- keep these in sync with the C# DriverSetupExitCodes).
 * Human-readable detail is appended to:
 *     %LOCALAPPDATA%\NitTray\driver-setup.log
 */

#include <windows.h>
#include <setupapi.h>
#include <newdev.h>
#include <cfgmgr32.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include "libwdi.h"

/* Exit-code contract shared with src/app/Services/DriverSetupExitCodes.cs */
#define EXIT_OK_SUCCESS       0
#define EXIT_GENERIC_ERROR    1
#define EXIT_BAD_ARGUMENTS    2
#define EXIT_DEVICE_NOT_FOUND 3
#define EXIT_PREPARE_FAILED   4
#define EXIT_INSTALL_FAILED   5
#define EXIT_UNINSTALL_FAILED 6

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

    /* Ensure %LOCALAPPDATA%\NitTray exists (CreateDirectory is a no-op if it does). */
    _snprintf_s(path, sizeof(path), _TRUNCATE, "%s\\NitTray", base);
    CreateDirectoryA(path, NULL);

    _snprintf_s(path, sizeof(path), _TRUNCATE, "%s\\NitTray\\driver-setup.log", base);
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

static int do_install(unsigned short vid, unsigned short pid)
{
    struct wdi_device_info dev = { NULL, 0, 0, FALSE, 0,
                                   "Apple Display (WinUSB)", NULL, NULL, NULL, NULL, NULL, 0 };
    struct wdi_options_create_list ocl = { 0 };
    struct wdi_options_prepare_driver opd = { 0 };
    struct wdi_options_install_driver oid = { 0 };
    struct wdi_device_info *list = NULL, *node = NULL, *match = NULL;
    char ext_dir[MAX_PATH];
    int r, device_present = 0, exit_code = EXIT_GENERIC_ERROR;

    dev.vid = vid;
    dev.pid = pid;
    log_msg("NitTray.DriverSetup starting: install VID_%04X PID_%04X.", dev.vid, dev.pid);

    /* Extraction directory for the generated INF / catalog / signed cert. */
    {
        char temp[MAX_PATH];
        DWORD n = GetTempPathA(sizeof(temp), temp);
        if (n == 0 || n > sizeof(temp)) {
            strcpy_s(temp, sizeof(temp), ".\\");
        }
        _snprintf_s(ext_dir, sizeof(ext_dir), _TRUNCATE, "%sNitTray_driver", temp);
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

    log_msg("Install done. Exit code = %d.", exit_code);
    return exit_code;
}

/* ------------------------------------------------------------------------- *
 *  uninstall  (driver reset)
 *
 *  Backend-agnostic on purpose: it talks only to SetupAPI / Cfgmgr32, never to
 *  libwdi, so it removes whatever WinUSB binding is present (ours or Zadig's)
 *  and keeps working if the install path later switches to a shipped INF+CAT.
 * ------------------------------------------------------------------------- */

/* Returns 1 if any string in the REG_MULTI_SZ 'multisz' contains 'needle' (ci). */
static int multisz_contains_ci(const char* multisz, const char* needle)
{
    const char* p = multisz;
    while (p != NULL && *p != '\0') {
        if (contains_ci(p, needle)) {
            return 1;
        }
        p += strlen(p) + 1;
    }
    return 0;
}

/*
 * Resolves the OEM INF (e.g. "oem42.inf") a device is currently bound to by
 * reading InfPath from its driver registry key. We need the published name so
 * we can delete that package from the driver store after uninstalling. Returns
 * 1 on success.
 */
static int get_device_inf(HDEVINFO set, SP_DEVINFO_DATA* did, char* inf, DWORD inf_cap)
{
    char driver_key[256];
    char subkey[512];
    HKEY hk;
    DWORD type = 0, size = 0, vtype = 0, vsize = inf_cap;
    LONG rc;

    if (!SetupDiGetDeviceRegistryPropertyA(set, did, SPDRP_DRIVER, &type,
            (PBYTE)driver_key, sizeof(driver_key), &size)) {
        return 0;
    }
    _snprintf_s(subkey, sizeof(subkey), _TRUNCATE,
        "SYSTEM\\CurrentControlSet\\Control\\Class\\%s", driver_key);
    if (RegOpenKeyExA(HKEY_LOCAL_MACHINE, subkey, 0, KEY_QUERY_VALUE, &hk) != ERROR_SUCCESS) {
        return 0;
    }
    rc = RegQueryValueExA(hk, "InfPath", NULL, &vtype, (PBYTE)inf, &vsize);
    RegCloseKey(hk);
    if (rc != ERROR_SUCCESS) {
        return 0;
    }
    inf[inf_cap - 1] = '\0';
    return 1;
}

#define MAX_UNINSTALL_TARGETS 16

static int do_uninstall(unsigned short vid, unsigned short pid)
{
    HDEVINFO set;
    SP_DEVINFO_DATA did = { sizeof(SP_DEVINFO_DATA) };
    SP_DEVINFO_DATA targets[MAX_UNINSTALL_TARGETS];
    DEVINST target_parent[MAX_UNINSTALL_TARGETS];
    char target_inf[MAX_UNINSTALL_TARGETS][MAX_PATH];
    int target_count = 0;
    char needle[32];
    int present = 0, removed = 0, idx, i, j;

    _snprintf_s(needle, sizeof(needle), _TRUNCATE, "VID_%04X&PID_%04X", vid, pid);
    log_msg("NitTray.DriverSetup starting: uninstall %s.", needle);

    set = SetupDiGetClassDevsA(NULL, "USB", NULL, DIGCF_ALLCLASSES | DIGCF_PRESENT);
    if (set == INVALID_HANDLE_VALUE) {
        log_msg("SetupDiGetClassDevs failed: %lu", (unsigned long)GetLastError());
        return EXIT_UNINSTALL_FAILED;
    }

    /*
     * Pass 1: find present USB nodes for this VID/PID that are bound to an OEM
     * (third-party) INF, and remember each one plus its INF name. We must read
     * the INF *before* uninstalling, and must not mutate the device-info set
     * while still enumerating it -- hence the two passes.
     */
    for (idx = 0; SetupDiEnumDeviceInfo(set, idx, &did); idx++) {
        char hwids[1024];
        char inf[MAX_PATH];
        DWORD type = 0, size = 0;

        if (!SetupDiGetDeviceRegistryPropertyA(set, &did, SPDRP_HARDWAREID, &type,
                (PBYTE)hwids, sizeof(hwids), &size)) {
            continue;
        }
        if (!multisz_contains_ci(hwids, needle)) {
            continue;
        }
        present = 1;

        if (!get_device_inf(set, &did, inf, sizeof(inf))) {
            log_msg("  match but no driver INF resolved -- skipping.");
            continue;
        }
        /* OEM packages are named oemNN.inf; in-box drivers keep real names. */
        if (_strnicmp(inf, "oem", 3) != 0) {
            log_msg("  match on in-box driver '%s' -- already default, skipping.", inf);
            continue;
        }
        if (target_count < MAX_UNINSTALL_TARGETS) {
            DEVINST parent = 0;
            targets[target_count] = did;
            strcpy_s(target_inf[target_count], MAX_PATH, inf);
            /* Capture the parent (USB hub / composite parent) now, before the
             * uninstall removes this node -- we re-enumerate it afterwards to
             * make the device reappear without a physical replug. */
            if (CM_Get_Parent(&parent, did.DevInst, 0) != CR_SUCCESS) {
                parent = 0;
            }
            target_parent[target_count] = parent;
            target_count++;
            log_msg("  queued removal: inf='%s'", inf);
        }
    }

    if (!present) {
        SetupDiDestroyDeviceInfoList(set);
        log_msg("No present device matches %s. Connect the display and retry.", needle);
        return EXIT_DEVICE_NOT_FOUND;
    }

    /* Pass 2: uninstall each queued device node (drops the WinUSB binding). */
    for (i = 0; i < target_count; i++) {
        BOOL reboot = FALSE;
        if (DiUninstallDevice(NULL, set, &targets[i], 0, &reboot)) {
            removed = 1;
            log_msg("  DiUninstallDevice OK (inf='%s', reboot=%d).", target_inf[i], (int)reboot);
        } else {
            log_msg("  DiUninstallDevice failed (inf='%s'): %lu",
                target_inf[i], (unsigned long)GetLastError());
        }
    }

    SetupDiDestroyDeviceInfoList(set);

    /*
     * Delete the OEM driver packages from the store so Windows can't silently
     * re-apply WinUSB on the next re-enumeration. Best-effort, deduped by name.
     */
    for (i = 0; i < target_count; i++) {
        wchar_t winf[MAX_PATH];
        int dup = 0;
        for (j = 0; j < i; j++) {
            if (_stricmp(target_inf[i], target_inf[j]) == 0) {
                dup = 1;
                break;
            }
        }
        if (dup) {
            continue;
        }
        MultiByteToWideChar(CP_ACP, 0, target_inf[i], -1, winf, MAX_PATH);
        if (SetupUninstallOEMInfW(winf, SUOI_FORCEDELETE, NULL)) {
            log_msg("  Removed driver package '%s' from the store.", target_inf[i]);
        } else {
            log_msg("  SetupUninstallOEMInf('%s') failed: %lu (non-fatal).",
                target_inf[i], (unsigned long)GetLastError());
        }
    }

    /*
     * Re-detect the still-connected device so it rebinds to the default in-box
     * driver without the user having to unplug it. Re-enumerating the *parent*
     * USB hub synchronously (captured before removal) is far more reliable than
     * a root re-scan: it tells the hub to re-scan the exact port whose child we
     * just removed, and SYNCHRONOUS makes the devnode reappear before we return.
     */
    for (i = 0; i < target_count; i++) {
        DEVINST parent = target_parent[i];
        int dup = 0;
        if (parent == 0) {
            continue;
        }
        for (j = 0; j < i; j++) {
            if (target_parent[j] == parent) {
                dup = 1;
                break;
            }
        }
        if (dup) {
            continue;
        }
        if (CM_Reenumerate_DevNode(parent, CM_REENUMERATE_SYNCHRONOUS) == CR_SUCCESS) {
            log_msg("  Re-enumerated parent hub (synchronous); device should be back.");
        } else {
            log_msg("  Parent re-enumeration returned non-success (non-fatal).");
        }
    }

    /* Fallback: a root re-scan in case a parent handle was unavailable. */
    {
        DEVINST root;
        if (CM_Locate_DevNodeA(&root, NULL, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS) {
            CM_Reenumerate_DevNode(root, 0);
            log_msg("  Triggered root PnP re-enumeration (fallback).");
        }
    }

    if (target_count > 0 && !removed) {
        log_msg("Uninstall failed: matched WinUSB device(s) but none could be removed.");
        return EXIT_UNINSTALL_FAILED;
    }
    if (removed) {
        log_msg("Reset complete: WinUSB removed; device reverted to the default driver.");
    } else {
        log_msg("Nothing to reset: device already on the default driver.");
    }
    return EXIT_OK_SUCCESS;
}

int __cdecl main(int argc, char** argv)
{
    const char* cmd;
    unsigned long vid, pid;
    int code;

    log_open();

    if (argc < 4) {
        log_msg("Bad arguments. Usage:");
        log_msg("  NitTray.DriverSetup.exe install   <VID-hex> <PID-hex>");
        log_msg("  NitTray.DriverSetup.exe uninstall <VID-hex> <PID-hex>");
        log_close();
        return EXIT_BAD_ARGUMENTS;
    }

    cmd = argv[1];
    vid = strtoul(argv[2], NULL, 16);
    pid = strtoul(argv[3], NULL, 16);
    if (vid == 0 || vid > 0xFFFF || pid == 0 || pid > 0xFFFF) {
        log_msg("Bad VID/PID arguments: vid='%s' pid='%s'", argv[2], argv[3]);
        log_close();
        return EXIT_BAD_ARGUMENTS;
    }

    if (_stricmp(cmd, "install") == 0) {
        code = do_install((unsigned short)vid, (unsigned short)pid);
    } else if (_stricmp(cmd, "uninstall") == 0) {
        code = do_uninstall((unsigned short)vid, (unsigned short)pid);
    } else {
        log_msg("Unknown command '%s'. Use 'install' or 'uninstall'.", cmd);
        log_close();
        return EXIT_BAD_ARGUMENTS;
    }

    log_msg("Exit code = %d.", code);
    log_close();
    return code;
}
