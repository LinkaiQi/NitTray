using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using NitTray.Models;
using NitTray.Models.Displays;
using NitTray.Services.Native;

namespace NitTray.Services;

// AppleDisplayService — Pro Display XDR probing. Detects the display even when
// its in-box driver has failed, drives it over WinUSB, and parses the raw HID
// report descriptor to locate the brightness usage.
public sealed partial class AppleDisplayService
{
    // Fallback presence scan: enumerate EVERY present USB node by hardware-id
    // (driver-independent) and look for the Pro Display XDR. This is the same
    // technique the native uninstall uses, and it sees the display even when it
    // is bound to Windows' failed in-box HID driver -- the yellow-bang / Code 10
    // state in which the GUID_DEVINTERFACE_USB_DEVICE enumeration above does not
    // surface it at all.
    private static PendingDriverSetup? ProbeProXdrPresenceByHardwareId()
    {
        var devInfoSet = SetupApiNative.SetupDiGetClassDevs(
            IntPtr.Zero,
            "USB",
            IntPtr.Zero,
            SetupApiNative.DIGCF_ALLCLASSES | SetupApiNative.DIGCF_PRESENT);

        if (devInfoSet == SetupApiNative.INVALID_HANDLE_VALUE)
        {
            DiagnosticLog.Write(
                $"  Hardware-id USB scan failed: err={Marshal.GetLastWin32Error()}");
            return null;
        }

        try
        {
            var devInfoData = new SetupApiNative.SP_DEVINFO_DATA
            {
                cbSize = Marshal.SizeOf<SetupApiNative.SP_DEVINFO_DATA>(),
            };

            var wantVid = $"VID_{AppleVendorId:X4}";
            var wantPid = $"PID_{ProDisplayXdrPid:X4}";

            for (uint index = 0;
                 SetupApiNative.SetupDiEnumDeviceInfo(devInfoSet, index, ref devInfoData);
                 index++)
            {
                var hardwareIds = GetHardwareIds(devInfoSet, ref devInfoData);
                if (hardwareIds is null)
                {
                    continue;
                }

                bool isXdr = hardwareIds.Any(h =>
                    h.IndexOf(wantVid, StringComparison.OrdinalIgnoreCase) >= 0
                    && h.IndexOf(wantPid, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!isXdr)
                {
                    continue;
                }

                var instanceId = GetDeviceInstanceId(devInfoSet, ref devInfoData);
                var desc = GetDeviceDescription(devInfoSet, ref devInfoData);
                DiagnosticLog.Write(
                    "  Hardware-id USB scan: Pro Display XDR is present but not WinUSB-bound " +
                    $"(instance='{instanceId ?? "?"}', desc='{desc ?? "?"}'). Offering setup.");

                return new PendingDriverSetup(
                    VendorId: AppleVendorId,
                    ProductId: ProDisplayXdrPid,
                    ProductName: "Pro Display XDR",
                    SerialNumber: instanceId is null ? null : TryParseSerialFromInstanceId(instanceId),
                    DevicePath: instanceId ?? string.Empty);
            }
        }
        finally
        {
            SetupApiNative.SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        return null;
    }

    private static string[]? GetHardwareIds(
        IntPtr devInfoSet,
        ref SetupApiNative.SP_DEVINFO_DATA devInfoData)
    {
        var buffer = new byte[1024];
        if (!SetupApiNative.SetupDiGetDeviceRegistryProperty(
                devInfoSet, ref devInfoData,
                SetupApiNative.SPDRP_HARDWAREID,
                out _, buffer, buffer.Length, out _))
        {
            return null;
        }
        // REG_MULTI_SZ: null-separated, double-null terminated, Unicode.
        return Encoding.Unicode.GetString(buffer)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? GetDeviceInstanceId(
        IntPtr devInfoSet,
        ref SetupApiNative.SP_DEVINFO_DATA devInfoData)
    {
        var sb = new StringBuilder(512);
        return SetupApiNative.SetupDiGetDeviceInstanceId(
                   devInfoSet, ref devInfoData, sb, sb.Capacity, out _)
            ? sb.ToString()
            : null;
    }

    // Instance ids look like USB\VID_05AC&PID_9243\C02XXXXXXXXX; the last
    // backslash-delimited segment is the device's serial (or a synthetic id).
    private static string? TryParseSerialFromInstanceId(string instanceId)
    {
        var parts = instanceId.Split('\\');
        if (parts.Length < 3)
        {
            return null;
        }
        var candidate = parts[^1];
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }

    private static (ConnectedDisplay? Info, bool DriverNotBound) TryProbeProXdrViaWinUsb(string path)
    {
        HidDeviceSafeHandle? handle = null;
        IntPtr primaryWinUsb = IntPtr.Zero;
        var associatedHandles = new List<IntPtr>();
        try
        {
            handle = TryOpenDeviceCore(path, overlapped: true, out var openErr);
            if (handle is null)
            {
                DiagnosticLog.Write(
                    $"  WinUSB probe: CreateFile failed (err={openErr}). " +
                    "This usually means no function driver is bound to the device.");
                return (null, true);
            }

            if (!WinUsbNative.WinUsb_Initialize(handle, out primaryWinUsb))
            {
                var err = Marshal.GetLastWin32Error();
                DiagnosticLog.Write(
                    $"  WinUSB probe: WinUsb_Initialize failed (err={err}). " +
                    "WinUSB is not the bound driver for this device.");
                return (null, true);
            }

            // Pro Display XDR exposes 5 HID interfaces. The brightness control
            // lives on one specific interface (typically bInterfaceNumber == 2),
            // not on whichever one WinUsb_Initialize gives us by default. Walk
            // the composite device via WinUsb_GetAssociatedInterface so we get a
            // handle for every interface, then identify the brightness one by
            // its report descriptor.
            var interfaces = new List<(byte IfaceNum, IntPtr Handle, sbyte AssocIdx)>();

            if (WinUsbNative.WinUsb_QueryInterfaceSettings(primaryWinUsb, 0, out var primaryDesc))
            {
                interfaces.Add((primaryDesc.bInterfaceNumber, primaryWinUsb, -1));
            }
            else
            {
                DiagnosticLog.Write(
                    $"  WinUSB probe: QueryInterfaceSettings(primary) failed " +
                    $"(err={Marshal.GetLastWin32Error()}); assuming iface 0.");
                interfaces.Add((0, primaryWinUsb, -1));
            }

            for (byte ai = 0; ai < 16; ai++)
            {
                if (!WinUsbNative.WinUsb_GetAssociatedInterface(primaryWinUsb, ai, out var assocHandle))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == ErrorNoMoreItems || err == 0)
                    {
                        break;
                    }
                    DiagnosticLog.Write(
                        $"  WinUSB probe: GetAssociatedInterface(idx={ai}) failed (err={err}). " +
                        "Stopping interface enumeration.");
                    break;
                }
                associatedHandles.Add(assocHandle);
                byte ifNum = 0;
                if (WinUsbNative.WinUsb_QueryInterfaceSettings(assocHandle, 0, out var desc))
                {
                    ifNum = desc.bInterfaceNumber;
                }
                interfaces.Add((ifNum, assocHandle, (sbyte)ai));
            }

            DiagnosticLog.Write(
                $"  WinUSB probe: discovered {interfaces.Count} interface(s) on this composite device.");
            foreach (var (n, _, a) in interfaces)
            {
                DiagnosticLog.Write(
                    $"    iface bInterfaceNumber={n}, associatedIdx={(a < 0 ? "primary" : a.ToString())}");
            }

            // For each interface, fetch and dump its HID report descriptor.
            // Identify the brightness one by the canonical VESA Monitor
            // Brightness pair (Usage Page 0x0082, Usage 0x0010) that the Pro
            // Display XDR advertises — see ProDisplayXdr.Model.Brightness.
            foreach (var (ifaceNum, ifaceHandle, assocIdx) in interfaces)
            {
                DiagnosticLog.Write(
                    $"  --- probing iface {ifaceNum} " +
                    $"(associatedIdx={(assocIdx < 0 ? "primary" : assocIdx.ToString())}) ---");
                var reportDesc = FetchReportDescriptor(ifaceHandle, ifaceNum);
                if (reportDesc is null)
                {
                    DiagnosticLog.Write(
                        $"  iface {ifaceNum}: no HID report descriptor, skipping.");
                    continue;
                }

                bool matches = DescriptorContainsUsage(
                    reportDesc, ProXdr.UsagePage, ProXdr.Usage);
                DiagnosticLog.Write(
                    $"  iface {ifaceNum}: HID report descriptor = {reportDesc.Length} bytes; " +
                    $"contains Usage Page 0x{ProXdr.UsagePage:X4} / " +
                    $"Usage 0x{ProXdr.Usage:X4} = {matches}.");
                if (!matches)
                {
                    continue;
                }

                // Found the brightness interface. Read the current brightness
                // value to confirm and surface it in the log.
                var probe = new byte[ProXdr.FeatureReportByteLength];
                probe[0] = ProXdr.ReportId;
                var setup = new WinUsbNative.WINUSB_SETUP_PACKET
                {
                    RequestType = WinUsbNative.RequestTypeClassInterfaceIn,
                    Request = WinUsbNative.HidRequestGetReport,
                    Value = (ushort)((WinUsbNative.HidReportTypeFeature << 8) | ProXdr.ReportId),
                    Index = ifaceNum,
                    Length = (ushort)probe.Length,
                };
                if (!WinUsbNative.WinUsb_ControlTransfer(
                        ifaceHandle, setup, probe, (uint)probe.Length, out var xfer, IntPtr.Zero))
                {
                    var err = Marshal.GetLastWin32Error();
                    DiagnosticLog.Write(
                        $"  iface {ifaceNum}: GET_REPORT failed (err={err}) despite descriptor " +
                        "advertising brightness usage; trying next interface.");
                    continue;
                }
                DiagnosticLog.Write(
                    $"  iface {ifaceNum}: GET_REPORT ok, transferred {xfer} bytes. " +
                    $"Raw report = [{ToHex(probe.AsSpan(0, (int)xfer).ToArray())}]");
                if ((int)xfer >= ProXdr.ByteOffset + 4)
                {
                    var brightness = BinaryPrimitives.ReadUInt32LittleEndian(
                        probe.AsSpan(ProXdr.ByteOffset, 4));
                    DiagnosticLog.Write(
                        $"  iface {ifaceNum}: decoded brightness (uint32-LE @offset " +
                        $"{ProXdr.ByteOffset}) = {brightness} " +
                        $"(range {ProXdr.MinRaw}..{ProXdr.MaxRaw}).");
                }

                return (new ConnectedDisplay(
                    DevicePath: path,
                    ProductName: "Pro Display XDR",
                    SerialNumber: TryParseSerialFromPath(path),
                    ProductId: ProDisplayXdrPid,
                    FeatureReportByteLength: ProXdr.FeatureReportByteLength,
                    BrightnessReportId: ProXdr.ReportId,
                    MinRawBrightness: ProXdr.MinRaw,
                    MaxRawBrightness: ProXdr.MaxRaw,
                    Transport: DisplayTransport.WinUsb,
                    UsbInterfaceNumber: ifaceNum,
                    BrightnessByteOffset: ProXdr.ByteOffset,
                    WinUsbAssociatedInterfaceIndex: assocIdx), false);
            }

            DiagnosticLog.Write(
                "  WinUSB probe: WinUSB is bound but none of the interfaces exposed the Pro XDR " +
                "brightness usage. WinUSB must own the *composite* device (the parent node " +
                "without an MI_NN suffix). Reinstalling via \"Set up display\" targets that node.");
            return (null, false);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"  WinUSB probe EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            return (null, false);
        }
        finally
        {
            foreach (var h in associatedHandles)
            {
                WinUsbNative.WinUsb_Free(h);
            }
            if (primaryWinUsb != IntPtr.Zero)
            {
                WinUsbNative.WinUsb_Free(primaryWinUsb);
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

    // Fetch the full HID report descriptor for a single interface via a
    // standard USB GET_DESCRIPTOR(Report) control transfer. Also dumps every
    // step into the diagnostic log so we can decode the layout later if
    // brightness detection fails.
    private static byte[]? FetchReportDescriptor(IntPtr winUsbForIface, byte interfaceNumber)
    {
        // Step 1: read the 9-byte HID class descriptor so we know how long
        // the report descriptor really is.
        var hidDesc = new byte[9];
        var hidSetup = new WinUsbNative.WINUSB_SETUP_PACKET
        {
            RequestType = WinUsbNative.RequestTypeStandardInterfaceIn,
            Request = WinUsbNative.UsbRequestGetDescriptor,
            Value = (ushort)(WinUsbNative.UsbDescriptorTypeHid << 8),
            Index = interfaceNumber,
            Length = (ushort)hidDesc.Length,
        };
        if (!WinUsbNative.WinUsb_ControlTransfer(
                winUsbForIface, hidSetup, hidDesc, (uint)hidDesc.Length, out var hidLen, IntPtr.Zero))
        {
            var err = Marshal.GetLastWin32Error();
            DiagnosticLog.Write(
                $"  iface {interfaceNumber}: GET_DESCRIPTOR(HID) failed (err={err}).");
            return null;
        }
        DiagnosticLog.Write(
            $"  iface {interfaceNumber}: HID class descriptor ({hidLen} bytes) = " +
            $"[{ToHex(hidDesc.AsSpan(0, (int)hidLen).ToArray())}]");

        if (hidLen < 9 || hidDesc[6] != 0x22)
        {
            DiagnosticLog.Write(
                $"  iface {interfaceNumber}: HID descriptor shape unexpected " +
                $"(bDescriptorType[6]=0x{hidDesc[6]:X2}, expected 0x22).");
            return null;
        }
        ushort reportLen = BinaryPrimitives.ReadUInt16LittleEndian(hidDesc.AsSpan(7, 2));
        if (reportLen == 0 || reportLen > 4096)
        {
            DiagnosticLog.Write(
                $"  iface {interfaceNumber}: report descriptor length {reportLen} out of range.");
            return null;
        }

        // Step 2: read the full report descriptor.
        var reportDesc = new byte[reportLen];
        var reportSetup = new WinUsbNative.WINUSB_SETUP_PACKET
        {
            RequestType = WinUsbNative.RequestTypeStandardInterfaceIn,
            Request = WinUsbNative.UsbRequestGetDescriptor,
            Value = (ushort)(WinUsbNative.UsbDescriptorTypeReport << 8),
            Index = interfaceNumber,
            Length = reportLen,
        };
        if (!WinUsbNative.WinUsb_ControlTransfer(
                winUsbForIface, reportSetup, reportDesc, (uint)reportDesc.Length,
                out var reportXferred, IntPtr.Zero))
        {
            var err = Marshal.GetLastWin32Error();
            DiagnosticLog.Write(
                $"  iface {interfaceNumber}: GET_DESCRIPTOR(Report) failed (err={err}).");
            return null;
        }
        DiagnosticLog.Write(
            $"  iface {interfaceNumber}: report descriptor ({reportXferred}/{reportLen} bytes):");
        const int chunk = 32;
        for (int off = 0; off < (int)reportXferred; off += chunk)
        {
            int n = Math.Min(chunk, (int)reportXferred - off);
            DiagnosticLog.Write(
                $"    {off:X4}: {ToHex(reportDesc.AsSpan(off, n).ToArray())}");
        }

        if ((int)reportXferred < reportDesc.Length)
        {
            Array.Resize(ref reportDesc, (int)reportXferred);
        }
        return reportDesc;
    }

    // Walks a HID report descriptor looking for a (UsagePage, Usage) pair.
    // HID 1.11 § 6.2.2 short item format:
    //   bits 0-1 = size code (0..3 -> 0,1,2,4 bytes of data)
    //   bits 2-3 = type (0 Main, 1 Global, 2 Local, 3 Reserved)
    //   bits 4-7 = tag
    // Usage Page is Global tag 0; Usage is Local tag 0. A 4-byte Usage item
    // packs both page (high 16) and id (low 16) into one item.
    // Long items (prefix 0xFE) are skipped.
    private static bool DescriptorContainsUsage(byte[] desc, ushort wantedPage, ushort wantedUsage)
    {
        if (desc is null || desc.Length == 0) return false;
        int i = 0;
        ushort currentPage = 0;
        while (i < desc.Length)
        {
            byte prefix = desc[i];
            if (prefix == 0xFE)
            {
                if (i + 2 >= desc.Length) break;
                int longSize = desc[i + 1];
                i += 3 + longSize;
                continue;
            }
            int sizeCode = prefix & 0x03;
            int dataSize = sizeCode switch { 0 => 0, 1 => 1, 2 => 2, 3 => 4, _ => 0 };
            i++;
            if (i + dataSize > desc.Length) break;

            uint data = 0;
            for (int k = 0; k < dataSize; k++)
            {
                data |= (uint)desc[i + k] << (k * 8);
            }

            byte itemType = (byte)((prefix >> 2) & 0x03);
            byte itemTag = (byte)((prefix >> 4) & 0x0F);

            if (itemType == 1 && itemTag == 0)
            {
                // Global Usage Page
                currentPage = (ushort)data;
            }
            else if (itemType == 2 && itemTag == 0)
            {
                // Local Usage. A 4-byte usage item carries page in the high
                // word, usage id in the low word; smaller forms reuse the
                // most-recent Global Usage Page.
                ushort page = currentPage;
                ushort id;
                if (dataSize == 4)
                {
                    page = (ushort)(data >> 16);
                    id = (ushort)(data & 0xFFFF);
                }
                else
                {
                    id = (ushort)data;
                }
                if (page == wantedPage && id == wantedUsage)
                {
                    return true;
                }
            }

            i += dataSize;
        }
        return false;
    }

}
