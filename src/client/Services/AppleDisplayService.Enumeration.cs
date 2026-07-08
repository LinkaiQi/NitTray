using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using NitTray.Models;
using NitTray.Models.Displays;
using NitTray.Services.Native;

namespace NitTray.Services;

// AppleDisplayService — display enumeration. Walks the HID and USB device
// interfaces and probes each Apple candidate for a brightness control.
public sealed partial class AppleDisplayService
{
    private static DisplayEnumerationResult Enumerate()
    {
        DiagnosticLog.Reset("Enumerate()");
        var found = new List<ConnectedDisplay>();
        var pendingSetups = new List<PendingDriverSetup>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int totalSeen = 0;
        int appleSeen = 0;

        HidNative.HidD_GetHidGuid(out var hidGuid);
        DiagnosticLog.Write($"HID class GUID: {hidGuid:B}");

        var devInfoSet = SetupApiNative.SetupDiGetClassDevs(
            ref hidGuid,
            IntPtr.Zero,
            IntPtr.Zero,
            SetupApiNative.DIGCF_PRESENT | SetupApiNative.DIGCF_DEVICEINTERFACE);

        if (devInfoSet == SetupApiNative.INVALID_HANDLE_VALUE)
        {
            var ex = new Win32Exception(Marshal.GetLastWin32Error(), "Failed to enumerate HID devices.");
            DiagnosticLog.Write($"ERROR SetupDiGetClassDevs failed: {ex.Message}");
            throw ex;
        }

        try
        {
            DiagnosticLog.Write("--- All HID class devices ---");
            var ifaceData = new SetupApiNative.SP_DEVICE_INTERFACE_DATA
            {
                cbSize = Marshal.SizeOf<SetupApiNative.SP_DEVICE_INTERFACE_DATA>(),
            };

            for (uint index = 0; ; index++)
            {
                if (!SetupApiNative.SetupDiEnumDeviceInterfaces(
                        devInfoSet, IntPtr.Zero, ref hidGuid, index, ref ifaceData))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err is 0 or ErrorNoMoreItems)
                    {
                        break;
                    }
                    var ex = new Win32Exception(err, "SetupDiEnumDeviceInterfaces failed.");
                    DiagnosticLog.Write($"ERROR SetupDiEnumDeviceInterfaces at index {index}: {ex.Message}");
                    throw ex;
                }

                totalSeen++;
                var path = GetDevicePath(devInfoSet, ref ifaceData);
                if (path is null)
                {
                    DiagnosticLog.Write($"[hid {index}] (null path)");
                    continue;
                }
                bool isApple = IsAppleVendor(path);
                DiagnosticLog.Write($"[hid {index}]{(isApple ? " APPLE" : "")} {path}");

                if (!isApple)
                {
                    continue;
                }
                appleSeen++;
                if (!seenPaths.Add(path))
                {
                    DiagnosticLog.Write("  (duplicate path, skipped)");
                    continue;
                }

                DiagnosticLog.Write($"  -> probing as candidate brightness interface");
                var info = TryProbeDisplay(path);
                if (info is not null)
                {
                    DiagnosticLog.Write(
                        $"  -> MATCH product='{info.ProductName}' serial='{info.SerialNumber ?? "-"}' " +
                        $"pid=0x{info.ProductId:X4} reportId=0x{info.BrightnessReportId:X2} " +
                        $"range={info.MinRawBrightness}..{info.MaxRawBrightness} " +
                        $"featLen={info.FeatureReportByteLength}");
                    found.Add(info);
                }
            }
        }
        finally
        {
            SetupApiNative.SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        DiagnosticLog.Write(
            $"HID enumeration done. Total HID devices: {totalSeen}, Apple-vendor: {appleSeen}, " +
            $"raw matches: {found.Count}");

        // Also enumerate raw USB devices so we can pick up Apple displays whose HID
        // interface failed to bind to hidclass.sys (Code 10 / descriptor mismatch).
        // The Pro Display XDR is the well-known case — its brightness HID interface is
        // rejected by Windows' generic HID driver, but with WinUSB bound by NitTray's
        // setup helper we can still send the same SET_REPORT / GET_REPORT transfers.
        var winUsbResult = EnumerateUsbAndProbeWinUsb();
        foreach (var d in winUsbResult.Displays)
        {
            // Skip if we already found this physical display via HID (avoid double entries
            // when both transports happen to work — unlikely, but be safe).
            if (found.Any(f =>
                    (f.SerialNumber is not null && f.SerialNumber == d.SerialNumber)
                    || (f.ProductId != 0 && f.ProductId == d.ProductId
                        && string.Equals(f.DevicePath, d.DevicePath, StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }
            found.Add(d);
        }

        // A display only needs driver setup if it isn't already controllable.
        foreach (var p in winUsbResult.PendingSetups)
        {
            if (found.Any(f =>
                    (f.SerialNumber is not null && f.SerialNumber == p.SerialNumber)
                    || (f.ProductId != 0 && f.ProductId == p.ProductId)))
            {
                continue;
            }
            pendingSetups.Add(p);
        }

        // Last-resort presence check for the Pro Display XDR. If the device-
        // interface enumeration never saw it (it's stuck on Windows' in-box HID
        // driver -> yellow-bang / Code 10, which suppresses the USB device
        // interface) yet the hardware is physically on the bus, surface it as a
        // pending setup so the user still gets the one-click "Set up display".
        if (!winUsbResult.SawProXdr
            && !found.Any(f => f.ProductId == ProDisplayXdrPid)
            && !pendingSetups.Any(p => p.ProductId == ProDisplayXdrPid))
        {
            var xdrPending = ProbeProXdrPresenceByHardwareId();
            if (xdrPending is not null)
            {
                pendingSetups.Add(xdrPending);
            }
        }

        var result = DeduplicateBySerial(found);
        DiagnosticLog.Write($"After dedup: {result.Count} display(s).");
        foreach (var d in result)
        {
            DiagnosticLog.Write($"  - {d.ProductName} (serial={d.SerialNumber ?? "-"}, pid=0x{d.ProductId:X4})");
        }
        if (pendingSetups.Count > 0)
        {
            DiagnosticLog.Write($"Pending driver setup: {pendingSetups.Count} display(s).");
            foreach (var p in pendingSetups)
            {
                DiagnosticLog.Write(
                    $"  - {p.ProductName} (serial={p.SerialNumber ?? "-"}, " +
                    $"vid=0x{p.VendorId:X4}, pid=0x{p.ProductId:X4}) needs WinUSB.");
            }
        }
        if (result.Count == 0 && pendingSetups.Count == 0)
        {
            DiagnosticLog.Write("No Apple display detected and none pending WinUSB setup.");
        }
        return new DisplayEnumerationResult(result, pendingSetups);
    }

    private readonly record struct UsbProbeResult(
        List<ConnectedDisplay> Displays,
        List<PendingDriverSetup> PendingSetups,
        bool SawProXdr);

    private static UsbProbeResult EnumerateUsbAndProbeWinUsb()
    {
        var results = new List<ConnectedDisplay>();
        var pending = new List<PendingDriverSetup>();
        bool sawProXdr = false;
        DiagnosticLog.Write("--- USB devices (GUID_DEVINTERFACE_USB_DEVICE) ---");
        var usbGuid = SetupApiNative.GUID_DEVINTERFACE_USB_DEVICE;
        var devInfoSet = SetupApiNative.SetupDiGetClassDevs(
            ref usbGuid,
            IntPtr.Zero,
            IntPtr.Zero,
            SetupApiNative.DIGCF_PRESENT | SetupApiNative.DIGCF_DEVICEINTERFACE);

        if (devInfoSet == SetupApiNative.INVALID_HANDLE_VALUE)
        {
            DiagnosticLog.Write(
                $"  SetupDiGetClassDevs(USB) failed: err={Marshal.GetLastWin32Error()}");
            return new UsbProbeResult(results, pending, false);
        }

        int total = 0;
        int apple = 0;
        int winUsbBound = 0;
        try
        {
            var ifaceData = new SetupApiNative.SP_DEVICE_INTERFACE_DATA
            {
                cbSize = Marshal.SizeOf<SetupApiNative.SP_DEVICE_INTERFACE_DATA>(),
            };
            var devInfoData = new SetupApiNative.SP_DEVINFO_DATA
            {
                cbSize = Marshal.SizeOf<SetupApiNative.SP_DEVINFO_DATA>(),
            };

            for (uint index = 0; ; index++)
            {
                if (!SetupApiNative.SetupDiEnumDeviceInterfaces(
                        devInfoSet, IntPtr.Zero, ref usbGuid, index, ref ifaceData))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err is 0 or ErrorNoMoreItems)
                    {
                        break;
                    }
                    DiagnosticLog.Write($"  USB enum stopped: err={err}");
                    break;
                }

                total++;
                var path = GetDevicePathWithInfo(devInfoSet, ref ifaceData, ref devInfoData);
                if (path is null)
                {
                    continue;
                }
                bool isApple = IsAppleVendor(path);
                if (isApple)
                {
                    apple++;
                }
                var desc = GetDeviceDescription(devInfoSet, ref devInfoData);
                DiagnosticLog.Write(
                    $"[usb {index}]{(isApple ? " APPLE" : "")} {path} ({desc ?? "(no description)"})");

                if (!isApple)
                {
                    continue;
                }

                var (pid, _) = ParseIdsFromPath(path);
                if (pid != ProDisplayXdrPid)
                {
                    DiagnosticLog.Write($"  -> not the Pro Display XDR (pid=0x{pid:X4}), skipping WinUSB probe.");
                    continue;
                }
                sawProXdr = true;

                var (winUsbInfo, driverNotBound) = TryProbeProXdrViaWinUsb(path);
                if (winUsbInfo is not null)
                {
                    winUsbBound++;
                    DiagnosticLog.Write(
                        $"  -> WinUSB MATCH product='{winUsbInfo.ProductName}' " +
                        $"serial='{winUsbInfo.SerialNumber ?? "-"}'");
                    results.Add(winUsbInfo);
                }
                else if (driverNotBound)
                {
                    DiagnosticLog.Write(
                        "  -> Pro Display XDR detected on the USB bus but WinUSB is not bound (pending setup).");
                    pending.Add(new PendingDriverSetup(
                        VendorId: AppleVendorId,
                        ProductId: ProDisplayXdrPid,
                        ProductName: "Pro Display XDR",
                        SerialNumber: TryParseSerialFromPath(path),
                        DevicePath: path));
                }
                else
                {
                    DiagnosticLog.Write(
                        "  -> Pro Display XDR found and WinUSB is bound, but no brightness " +
                        "interface responded. Reinstalling the driver will not help; see log above.");
                }
            }
        }
        finally
        {
            SetupApiNative.SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        DiagnosticLog.Write(
            $"USB enumeration done. Total: {total}, Apple-vendor: {apple}, WinUSB-bound: {winUsbBound}.");
        return new UsbProbeResult(results, pending, sawProXdr);
    }

}
