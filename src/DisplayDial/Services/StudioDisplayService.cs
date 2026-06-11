using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using DisplayDial.Models;
using DisplayDial.Services.Native;

namespace DisplayDial.Services;

public sealed class StudioDisplayService : IDisplayService
{
    private const ushort AppleVendorId = 0x05AC;
    private const ushort ProDisplayXdrPid = 0x9243;
    private const int ErrorNoMoreItems = 259;

    // Pro Display XDR HID report layout, taken from 0xcharly/apdbctl. We hard-code
    // these because the device doesn't expose a usable HID descriptor on Windows
    // (hidclass.sys refuses with Code 10), so we can't probe the values dynamically
    // the way we do for the Studio Display family.
    private const int ProXdrFeatureReportByteLength = 7;
    private const byte ProXdrBrightnessReportId = 0x01;
    private const uint ProXdrMinBrightness = 400;
    private const uint ProXdrMaxBrightness = 50000;

    // Models we recognise. Used for friendly product names and as a fast-path filter
    // when matching device paths. Devices not in this list can still work — we fall
    // back to "any Apple HID interface that exposes Monitor/Brightness usage".
    private static readonly (ushort Pid, string Name)[] KnownDisplays =
    {
        (0x1114, "Apple Studio Display"),
        (0x1116, "Apple Studio Display XDR"),
        (0x1118, "Apple Studio Display"),
        (ProDisplayXdrPid, "Apple Pro Display XDR"),
    };

    public Task<IReadOnlyList<StudioDisplayInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<StudioDisplayInfo>>(Enumerate, cancellationToken);

    public Task<int> ReadBrightnessPercentAsync(StudioDisplayInfo display, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            return RawToPercent(display, ReadRawBrightness(display));
        }, cancellationToken);

    public Task SetBrightnessPercentAsync(StudioDisplayInfo display, int percent, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            WriteRawBrightness(display, PercentToRaw(display, percent));
        }, cancellationToken);

    private static uint ReadRawBrightness(StudioDisplayInfo display)
    {
        return display.Transport switch
        {
            DisplayTransport.WinUsb => ReadRawBrightnessViaWinUsb(display),
            _ => ReadRawBrightnessViaHid(display),
        };
    }

    private static void WriteRawBrightness(StudioDisplayInfo display, uint raw)
    {
        switch (display.Transport)
        {
            case DisplayTransport.WinUsb:
                WriteRawBrightnessViaWinUsb(display, raw);
                break;
            default:
                WriteRawBrightnessViaHid(display, raw);
                break;
        }
    }

    private static uint ReadRawBrightnessViaHid(StudioDisplayInfo display)
    {
        using var handle = OpenDevice(display.DevicePath);
        var buffer = CreateFeatureBuffer(display, 0);
        if (!HidNative.HidD_GetFeature(handle, buffer, buffer.Length))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "HidD_GetFeature failed while reading brightness.");
        }
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(1, 4));
    }

    private static void WriteRawBrightnessViaHid(StudioDisplayInfo display, uint raw)
    {
        using var handle = OpenDevice(display.DevicePath);
        var buffer = CreateFeatureBuffer(display, raw);
        if (!HidNative.HidD_SetFeature(handle, buffer, buffer.Length))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "HidD_SetFeature failed while writing brightness.");
        }
    }

    private static uint ReadRawBrightnessViaWinUsb(StudioDisplayInfo display)
    {
        using var handle = OpenDeviceOverlapped(display.DevicePath);
        IntPtr winUsb = IntPtr.Zero;
        try
        {
            if (!WinUsbNative.WinUsb_Initialize(handle, out winUsb))
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err,
                    $"WinUsb_Initialize failed (err={err}).");
            }

            var buffer = CreateFeatureBuffer(display, 0);
            var setup = new WinUsbNative.WINUSB_SETUP_PACKET
            {
                RequestType = WinUsbNative.RequestTypeClassInterfaceIn,
                Request = WinUsbNative.HidRequestGetReport,
                Value = (ushort)((WinUsbNative.HidReportTypeFeature << 8) | display.BrightnessReportId),
                Index = display.UsbInterfaceNumber,
                Length = (ushort)buffer.Length,
            };

            if (!WinUsbNative.WinUsb_ControlTransfer(
                    winUsb, setup, buffer, (uint)buffer.Length, out _, IntPtr.Zero))
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err,
                    $"WinUsb_ControlTransfer GET_REPORT failed (err={err}).");
            }

            return BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(1, 4));
        }
        finally
        {
            if (winUsb != IntPtr.Zero)
            {
                WinUsbNative.WinUsb_Free(winUsb);
            }
        }
    }

    private static void WriteRawBrightnessViaWinUsb(StudioDisplayInfo display, uint raw)
    {
        using var handle = OpenDeviceOverlapped(display.DevicePath);
        IntPtr winUsb = IntPtr.Zero;
        try
        {
            if (!WinUsbNative.WinUsb_Initialize(handle, out winUsb))
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err,
                    $"WinUsb_Initialize failed (err={err}).");
            }

            // Strategy A (HID 1.11 §7.2.1, hidapi/hidraw convention): data buffer
            // contains the full report INCLUDING the report ID byte at offset 0.
            // wLength == report length including ID.
            var withId = CreateFeatureBuffer(display, raw);
            int errA = TrySetReport(winUsb, display, withId);
            if (errA == 0)
            {
                DiagnosticLog.Write(
                    $"WinUSB SET_REPORT(strategy=withReportId, len={withId.Length}) ok " +
                    $"raw=0x{raw:X8} bytes=[{ToHex(withId)}]");
                return;
            }

            // Strategy B (alternate HID 1.11 §7.2.2 reading, used by some Apple
            // firmwares): data buffer EXCLUDES the report ID byte. wLength is
            // shorter by 1. The report ID still travels in wValue.
            var withoutId = new byte[withId.Length - 1];
            Array.Copy(withId, 1, withoutId, 0, withoutId.Length);
            int errB = TrySetReport(winUsb, display, withoutId);
            if (errB == 0)
            {
                DiagnosticLog.Write(
                    $"WinUSB SET_REPORT(strategy=noReportId, len={withoutId.Length}) ok " +
                    $"raw=0x{raw:X8} bytes=[{ToHex(withoutId)}]");
                return;
            }

            DiagnosticLog.Write(
                $"WinUSB SET_REPORT FAILED both strategies: " +
                $"withReportId(err={errA}), noReportId(err={errB}), raw=0x{raw:X8}");

            throw new Win32Exception(errA,
                $"WinUsb_ControlTransfer SET_REPORT failed (err={errA} with report-id, " +
                $"err={errB} without). raw=0x{raw:X8}");
        }
        finally
        {
            if (winUsb != IntPtr.Zero)
            {
                WinUsbNative.WinUsb_Free(winUsb);
            }
        }
    }

    private static int TrySetReport(IntPtr winUsb, StudioDisplayInfo display, byte[] data)
    {
        var setup = new WinUsbNative.WINUSB_SETUP_PACKET
        {
            RequestType = WinUsbNative.RequestTypeClassInterfaceOut,
            Request = WinUsbNative.HidRequestSetReport,
            Value = (ushort)((WinUsbNative.HidReportTypeFeature << 8) | display.BrightnessReportId),
            Index = display.UsbInterfaceNumber,
            Length = (ushort)data.Length,
        };
        if (!WinUsbNative.WinUsb_ControlTransfer(
                winUsb, setup, data, (uint)data.Length, out _, IntPtr.Zero))
        {
            return Marshal.GetLastWin32Error();
        }
        return 0;
    }

    private static string ToHex(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }

    private static IReadOnlyList<StudioDisplayInfo> Enumerate()
    {
        DiagnosticLog.Reset("Enumerate()");
        var found = new List<StudioDisplayInfo>();
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
        // rejected by Windows' generic HID driver, but with WinUSB bound (via Zadig)
        // we can still send the same SET_REPORT / GET_REPORT control transfers.
        var winUsbDisplays = EnumerateUsbAndProbeWinUsb();
        foreach (var d in winUsbDisplays)
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

        var result = DeduplicateBySerial(found);
        DiagnosticLog.Write($"After dedup: {result.Count} display(s).");
        foreach (var d in result)
        {
            DiagnosticLog.Write($"  - {d.ProductName} (serial={d.SerialNumber ?? "-"}, pid=0x{d.ProductId:X4})");
        }
        if (result.Count == 0)
        {
            DiagnosticLog.Write("");
            DiagnosticLog.Write("Hint: open Device Manager, choose View -> Devices by container.");
            DiagnosticLog.Write("If your Apple display does not appear there at all, the USB / HID");
            DiagnosticLog.Write("control channel is not reaching this PC. Try a different cable, plug");
            DiagnosticLog.Write("directly into the PC (not through a hub), and confirm the cable is");
            DiagnosticLog.Write("USB-C / Thunderbolt (not DisplayPort-only).");
            DiagnosticLog.Write("");
            DiagnosticLog.Write("If your Apple Pro Display XDR appears in Device Manager with a yellow");
            DiagnosticLog.Write("warning (Code 10), Windows' built-in HID driver doesn't understand");
            DiagnosticLog.Write("its descriptor. Install Zadig (https://zadig.akeo.ie/), choose");
            DiagnosticLog.Write("Options -> List All Devices, pick the entry with VID_05AC and");
            DiagnosticLog.Write("PID_9243, then install the WinUSB driver. After that, click Refresh");
            DiagnosticLog.Write("here — the Pro XDR should appear via the WinUSB transport.");
        }
        return result;
    }

    private static List<StudioDisplayInfo> EnumerateUsbAndProbeWinUsb()
    {
        var results = new List<StudioDisplayInfo>();
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
            return results;
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

                var winUsbInfo = TryProbeProXdrViaWinUsb(path);
                if (winUsbInfo is not null)
                {
                    winUsbBound++;
                    DiagnosticLog.Write(
                        $"  -> WinUSB MATCH product='{winUsbInfo.ProductName}' " +
                        $"serial='{winUsbInfo.SerialNumber ?? "-"}'");
                    results.Add(winUsbInfo);
                }
                else
                {
                    DiagnosticLog.Write(
                        "  -> Pro Display XDR detected on the USB bus but WinUSB is not bound. " +
                        "Install WinUSB via Zadig to enable brightness control.");
                }
            }
        }
        finally
        {
            SetupApiNative.SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        DiagnosticLog.Write(
            $"USB enumeration done. Total: {total}, Apple-vendor: {apple}, WinUSB-bound: {winUsbBound}.");
        return results;
    }

    private static StudioDisplayInfo? TryProbeProXdrViaWinUsb(string path)
    {
        HidDeviceSafeHandle? handle = null;
        IntPtr winUsb = IntPtr.Zero;
        try
        {
            handle = TryOpenDeviceOverlapped(path);
            if (handle is null)
            {
                DiagnosticLog.Write(
                    $"  WinUSB probe: CreateFile failed (err={Marshal.GetLastWin32Error()}). " +
                    "This usually means no function driver is bound to the device.");
                return null;
            }

            if (!WinUsbNative.WinUsb_Initialize(handle, out winUsb))
            {
                var err = Marshal.GetLastWin32Error();
                DiagnosticLog.Write(
                    $"  WinUSB probe: WinUsb_Initialize failed (err={err}). " +
                    "WinUSB is not the bound driver for this device.");
                return null;
            }

            // Try reading the current brightness via GET_REPORT to confirm WinUSB really works.
            var probe = new byte[ProXdrFeatureReportByteLength];
            var setup = new WinUsbNative.WINUSB_SETUP_PACKET
            {
                RequestType = WinUsbNative.RequestTypeClassInterfaceIn,
                Request = WinUsbNative.HidRequestGetReport,
                Value = (ushort)((WinUsbNative.HidReportTypeFeature << 8) | ProXdrBrightnessReportId),
                Index = 0,
                Length = (ushort)probe.Length,
            };
            if (!WinUsbNative.WinUsb_ControlTransfer(
                    winUsb, setup, probe, (uint)probe.Length, out var transferred, IntPtr.Zero))
            {
                var err = Marshal.GetLastWin32Error();
                DiagnosticLog.Write(
                    $"  WinUSB probe: GET_REPORT control transfer failed (err={err}). " +
                    "Device responds to WinUSB but rejected the HID feature read.");
                return null;
            }
            DiagnosticLog.Write(
                $"  WinUSB probe: GET_REPORT ok, transferred {transferred} bytes. " +
                $"Raw 7-byte report = [{ToHex(probe)}]. " +
                $"As uint32-LE @offset1 = 0x{BinaryPrimitives.ReadUInt32LittleEndian(probe.AsSpan(1, 4)):X8} " +
                $"({BinaryPrimitives.ReadUInt32LittleEndian(probe.AsSpan(1, 4))}). " +
                $"As uint16-LE @offset1 = 0x{BinaryPrimitives.ReadUInt16LittleEndian(probe.AsSpan(1, 2)):X4} " +
                $"({BinaryPrimitives.ReadUInt16LittleEndian(probe.AsSpan(1, 2))}).");

            return new StudioDisplayInfo(
                DevicePath: path,
                ProductName: "Apple Pro Display XDR",
                SerialNumber: TryParseSerialFromPath(path),
                ProductId: ProDisplayXdrPid,
                FeatureReportByteLength: ProXdrFeatureReportByteLength,
                BrightnessReportId: ProXdrBrightnessReportId,
                MinRawBrightness: ProXdrMinBrightness,
                MaxRawBrightness: ProXdrMaxBrightness,
                Transport: DisplayTransport.WinUsb,
                UsbInterfaceNumber: 0);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"  WinUSB probe EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            if (winUsb != IntPtr.Zero)
            {
                WinUsbNative.WinUsb_Free(winUsb);
            }
            handle?.Dispose();
        }
    }

    private static string? TryParseSerialFromPath(string path)
    {
        // USB device interface paths look like:
        //   \\?\usb#vid_05ac&pid_9243#C02XXXXXXXXX#{a5dcbf10-...}
        // The instance-id segment between the third '#' and the GUID is the serial number
        // (or a synthetic id if the device didn't report one).
        var parts = path.Split('#');
        if (parts.Length < 4)
        {
            return null;
        }
        var candidate = parts[2];
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }

    private static string? GetDeviceDescription(
        IntPtr devInfoSet,
        ref SetupApiNative.SP_DEVINFO_DATA devInfoData)
    {
        var buffer = new byte[512];
        if (SetupApiNative.SetupDiGetDeviceRegistryProperty(
                devInfoSet, ref devInfoData,
                SetupApiNative.SPDRP_FRIENDLYNAME,
                out _, buffer, buffer.Length, out _))
        {
            var s = System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
            if (!string.IsNullOrWhiteSpace(s))
            {
                return s;
            }
        }

        if (SetupApiNative.SetupDiGetDeviceRegistryProperty(
                devInfoSet, ref devInfoData,
                SetupApiNative.SPDRP_DEVICEDESC,
                out _, buffer, buffer.Length, out _))
        {
            var s = System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
            if (!string.IsNullOrWhiteSpace(s))
            {
                return s;
            }
        }
        return null;
    }

    private static string? GetDevicePathWithInfo(
        IntPtr devInfoSet,
        ref SetupApiNative.SP_DEVICE_INTERFACE_DATA ifaceData,
        ref SetupApiNative.SP_DEVINFO_DATA devInfoData)
    {
        uint requiredBytes = 0;
        SetupApiNative.SetupDiGetDeviceInterfaceDetail(
            devInfoSet, ref ifaceData, IntPtr.Zero, 0, ref requiredBytes, IntPtr.Zero);

        if (requiredBytes == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)requiredBytes);
        try
        {
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupApiNative.SetupDiGetDeviceInterfaceDetail(
                    devInfoSet, ref ifaceData, buffer, requiredBytes, ref requiredBytes, ref devInfoData))
            {
                return null;
            }
            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static StudioDisplayInfo? TryProbeDisplay(string path)
    {
        HidDeviceSafeHandle? handle = null;
        IntPtr preparsed = IntPtr.Zero;
        try
        {
            handle = TryOpenDevice(path);
            if (handle is null)
            {
                DiagnosticLog.Write($"  CreateFile failed (err={Marshal.GetLastWin32Error()})");
                return null;
            }

            if (!HidNative.HidD_GetPreparsedData(handle, out preparsed) || preparsed == IntPtr.Zero)
            {
                DiagnosticLog.Write($"  HidD_GetPreparsedData failed (err={Marshal.GetLastWin32Error()})");
                return null;
            }

            var capsStatus = HidNative.HidP_GetCaps(preparsed, out var caps);
            if (capsStatus != HidNative.HIDP_STATUS_SUCCESS)
            {
                DiagnosticLog.Write($"  HidP_GetCaps failed (status=0x{capsStatus:X8})");
                return null;
            }

            DiagnosticLog.Write(
                $"  HIDP_CAPS topUsagePage=0x{caps.UsagePage:X4} topUsage=0x{caps.Usage:X4} " +
                $"featLen={caps.FeatureReportByteLength} featValueCaps={caps.NumberFeatureValueCaps}");

            if (caps.NumberFeatureValueCaps == 0 || caps.FeatureReportByteLength == 0)
            {
                DiagnosticLog.Write("  -> no feature value caps");
                return null;
            }

            var valueCaps = new HidNative.HIDP_VALUE_CAPS[caps.NumberFeatureValueCaps];
            var capsLen = caps.NumberFeatureValueCaps;
            var valStatus = HidNative.HidP_GetValueCaps(
                HidNative.HIDP_REPORT_TYPE.Feature, valueCaps, ref capsLen, preparsed);
            if (valStatus != HidNative.HIDP_STATUS_SUCCESS)
            {
                DiagnosticLog.Write($"  HidP_GetValueCaps failed (status=0x{valStatus:X8})");
                return null;
            }

            int chosen = -1;
            int fallback = -1;
            for (int i = 0; i < capsLen; i++)
            {
                var cap = valueCaps[i];
                DiagnosticLog.Write(
                    $"    cap[{i}] usagePage=0x{cap.UsagePage:X4} usage=0x{cap.Usage:X4} " +
                    $"reportId=0x{cap.ReportID:X2} bitSize={cap.BitSize} reportCount={cap.ReportCount} " +
                    $"logicalMin={cap.LogicalMin} logicalMax={cap.LogicalMax}");

                bool isMonitorBrightness =
                    cap.UsagePage == HidNative.MonitorBrightnessUsagePage
                    && cap.Usage == HidNative.MonitorBrightnessUsage;
                bool isAppleBrightness =
                    cap.UsagePage == HidNative.AppleVendorBrightnessUsagePage
                    && cap.Usage == HidNative.AppleVendorBrightnessUsage;

                if (chosen < 0 && (isMonitorBrightness || isAppleBrightness))
                {
                    chosen = i;
                    DiagnosticLog.Write($"      ^ canonical brightness usage match");
                }

                // Fallback: any 32-bit single-value feature cap whose range looks like an
                // Apple brightness range (LogicalMax >= 400) — covers any future Apple
                // display that uses yet another vendor-specific usage.
                if (fallback < 0
                    && cap.ReportCount == 1
                    && cap.BitSize == 32
                    && cap.LogicalMax >= 400
                    && cap.LogicalMin < cap.LogicalMax)
                {
                    fallback = i;
                }
            }

            if (chosen < 0)
            {
                chosen = fallback;
                if (chosen >= 0)
                {
                    DiagnosticLog.Write($"      no canonical match, using fallback cap[{chosen}]");
                }
            }
            if (chosen < 0)
            {
                DiagnosticLog.Write("  -> no brightness cap found on this interface");
                return null;
            }

            var brightness = valueCaps[chosen];

            var (productId, productNameFromPid) = ParseIdsFromPath(path);
            var productName = ReadHidString(handle, HidNative.HidD_GetProductString)
                              ?? productNameFromPid;
            var serial = ReadHidString(handle, HidNative.HidD_GetSerialNumberString);

            return new StudioDisplayInfo(
                DevicePath: path,
                ProductName: string.IsNullOrWhiteSpace(productName) ? "Apple Display" : productName!,
                SerialNumber: string.IsNullOrWhiteSpace(serial) ? null : serial,
                ProductId: productId,
                FeatureReportByteLength: caps.FeatureReportByteLength,
                BrightnessReportId: brightness.ReportID,
                MinRawBrightness: (uint)Math.Max(0, brightness.LogicalMin),
                MaxRawBrightness: (uint)Math.Max(brightness.LogicalMin + 1, brightness.LogicalMax));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"  PROBE EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            if (preparsed != IntPtr.Zero)
            {
                HidNative.HidD_FreePreparsedData(preparsed);
            }
            handle?.Dispose();
        }
    }

    private static IReadOnlyList<StudioDisplayInfo> DeduplicateBySerial(List<StudioDisplayInfo> all)
    {
        var bySerial = new Dictionary<string, StudioDisplayInfo>(StringComparer.OrdinalIgnoreCase);
        var pidOnly = new Dictionary<ushort, StudioDisplayInfo>();
        var noSerialPaths = new List<StudioDisplayInfo>();

        foreach (var info in all)
        {
            if (!string.IsNullOrEmpty(info.SerialNumber))
            {
                if (!bySerial.ContainsKey(info.SerialNumber!))
                {
                    bySerial[info.SerialNumber!] = info;
                }
            }
            else if (info.ProductId != 0)
            {
                if (!pidOnly.ContainsKey(info.ProductId))
                {
                    pidOnly[info.ProductId] = info;
                }
            }
            else
            {
                noSerialPaths.Add(info);
            }
        }

        var result = new List<StudioDisplayInfo>();
        result.AddRange(bySerial.Values);
        foreach (var kv in pidOnly)
        {
            // A device that lacks a serial but shares a PID with one already added
            // (e.g. multiple Pro XDR interfaces of the same physical display) gets dropped.
            if (!bySerial.Values.Any(s => s.ProductId == kv.Key))
            {
                result.Add(kv.Value);
            }
        }
        result.AddRange(noSerialPaths);
        return result;
    }

    private static (ushort Pid, string? Name) ParseIdsFromPath(string path)
    {
        var lower = path.ToLowerInvariant();
        foreach (var (pid, name) in KnownDisplays)
        {
            if (lower.Contains($"pid_{pid:x4}"))
            {
                return (pid, name);
            }
        }

        var marker = "pid_";
        var idx = lower.IndexOf(marker, StringComparison.Ordinal);
        if (idx >= 0 && idx + marker.Length + 4 <= lower.Length)
        {
            var hex = lower.Substring(idx + marker.Length, 4);
            if (ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var pid))
            {
                return (pid, null);
            }
        }

        return (0, null);
    }

    private static bool IsAppleVendor(string path)
        => path.IndexOf($"vid_{AppleVendorId:x4}", StringComparison.OrdinalIgnoreCase) >= 0;

    private static string? GetDevicePath(IntPtr devInfoSet, ref SetupApiNative.SP_DEVICE_INTERFACE_DATA ifaceData)
    {
        uint requiredBytes = 0;
        SetupApiNative.SetupDiGetDeviceInterfaceDetail(
            devInfoSet, ref ifaceData, IntPtr.Zero, 0, ref requiredBytes, IntPtr.Zero);

        if (requiredBytes == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)requiredBytes);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W cbSize: 8 on 64-bit (4-byte DWORD + 4-byte alignment),
            // 6 on 32-bit (4-byte DWORD + 2-byte WCHAR).
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);

            if (!SetupApiNative.SetupDiGetDeviceInterfaceDetail(
                    devInfoSet, ref ifaceData, buffer, requiredBytes, ref requiredBytes, IntPtr.Zero))
            {
                return null;
            }

            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private delegate bool HidStringReader(HidDeviceSafeHandle handle, byte[] buffer, int bufferLength);

    private static string? ReadHidString(HidDeviceSafeHandle handle, HidStringReader reader)
    {
        var buffer = new byte[256];
        if (!reader(handle, buffer, buffer.Length))
        {
            return null;
        }

        var raw = Encoding.Unicode.GetString(buffer);
        var trimmed = raw.TrimEnd('\0').Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static HidDeviceSafeHandle OpenDevice(string path)
    {
        var handle = TryOpenDevice(path);
        if (handle is null)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not open Apple display control interface (path: {path}).");
        }
        return handle;
    }

    private static HidDeviceSafeHandle? TryOpenDevice(string path)
    {
        var handle = Kernel32Native.CreateFile(
            path,
            Kernel32Native.GENERIC_READ | Kernel32Native.GENERIC_WRITE,
            Kernel32Native.FILE_SHARE_READ | Kernel32Native.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Kernel32Native.OPEN_EXISTING,
            Kernel32Native.FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }

    private static HidDeviceSafeHandle OpenDeviceOverlapped(string path)
    {
        // WinUSB requires the underlying file handle to be opened with FILE_FLAG_OVERLAPPED.
        var handle = Kernel32Native.CreateFile(
            path,
            Kernel32Native.GENERIC_READ | Kernel32Native.GENERIC_WRITE,
            Kernel32Native.FILE_SHARE_READ | Kernel32Native.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Kernel32Native.OPEN_EXISTING,
            Kernel32Native.FILE_ATTRIBUTE_NORMAL | Kernel32Native.FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(err,
                $"Could not open USB device for WinUSB (path: {path}). " +
                "Did you install the WinUSB driver via Zadig?");
        }

        return handle;
    }

    private static HidDeviceSafeHandle? TryOpenDeviceOverlapped(string path)
    {
        var handle = Kernel32Native.CreateFile(
            path,
            Kernel32Native.GENERIC_READ | Kernel32Native.GENERIC_WRITE,
            Kernel32Native.FILE_SHARE_READ | Kernel32Native.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Kernel32Native.OPEN_EXISTING,
            Kernel32Native.FILE_ATTRIBUTE_NORMAL | Kernel32Native.FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }

    private static byte[] CreateFeatureBuffer(StudioDisplayInfo display, uint raw)
    {
        // Feature report layout: [ReportId][uint32 LE brightness][zero padding to FeatureReportByteLength].
        var len = Math.Max(display.FeatureReportByteLength, 5);
        var buffer = new byte[len];
        buffer[0] = display.BrightnessReportId;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(1, 4), raw);
        return buffer;
    }

    private static uint PercentToRaw(StudioDisplayInfo display, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        var span = (double)(display.MaxRawBrightness - display.MinRawBrightness);
        var raw = display.MinRawBrightness + (uint)Math.Round(span * (percent / 100.0));
        return Math.Clamp(raw, display.MinRawBrightness, display.MaxRawBrightness);
    }

    private static int RawToPercent(StudioDisplayInfo display, uint raw)
    {
        var clamped = Math.Clamp(raw, display.MinRawBrightness, display.MaxRawBrightness);
        var span = (double)(display.MaxRawBrightness - display.MinRawBrightness);
        if (span <= 0)
        {
            return 0;
        }
        var percent = (clamped - display.MinRawBrightness) * 100.0 / span;
        return (int)Math.Round(percent);
    }
}
