using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using DisplayDial.Models;
using DisplayDial.Services.Native;

namespace DisplayDial.Services;

public sealed class StudioDisplayService : IDisplayService
{
    private const ushort AppleVendorId = Models.AppleDisplays.VendorId;
    private const ushort ProDisplayXdrPid = Models.AppleDisplays.ProDisplayXdrProductId;
    private const int ErrorNoMoreItems = 259;

    // Pro Display XDR brightness HID protocol.
    //
    // Pro Display XDR exposes 5 HID interfaces. We identify the brightness
    // one by walking each interface's HID report descriptor — the brightness
    // interface (typically interface 2) carries the canonical VESA Monitor
    // Brightness item, identified by the well-known pair:
    //
    //   Usage Page  = 0x0082   (VESA Virtual Controls; HID Usage Tables § 11)
    //   Usage       = 0x0010   (Brightness)
    //
    // That interface's Feature Report 0x01 layout is exactly 7 bytes:
    //
    //   byte 0     : Report ID (0x01)
    //   bytes 1..4 : BRIGHTNESS, uint32 little-endian, range 400..50000
    //   bytes 5..6 : a second volatile uint16 (Page 0x0F / Usage 0x50,
    //                range 0..20000) — preserved verbatim via read-modify-write
    //
    // Previously this code talked to whichever interface WinUSB returned
    // first — typically interface 0, which on Pro XDR is a *sensor* interface
    // (Usage Page 0x0020). Writes there were silently accepted but never
    // affected the panel.
    private const int ProXdrFeatureReportByteLength = 7;
    private const byte ProXdrBrightnessReportId = 0x01;
    private const int ProXdrBrightnessByteOffset = 1;
    private const uint ProXdrMinBrightness = 400;
    private const uint ProXdrMaxBrightness = 50000;
    private const ushort ProXdrBrightnessUsagePage = 0x0082;
    private const ushort ProXdrBrightnessUsage = 0x0010;

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

    public Task<DisplayEnumerationResult> EnumerateAsync(CancellationToken cancellationToken = default)
        => Task.Run(Enumerate, cancellationToken);

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
        using var ctx = OpenWinUsbBrightnessInterface(display);
        var buffer = GetFeatureReport(ctx.BrightnessHandle, display);
        if (display.BrightnessByteOffset + 4 > buffer.Length)
        {
            throw new InvalidOperationException(
                $"GET_REPORT returned {buffer.Length} bytes but brightness needs " +
                $"offset {display.BrightnessByteOffset} + 4.");
        }
        return BinaryPrimitives.ReadUInt32LittleEndian(
            buffer.AsSpan(display.BrightnessByteOffset, 4));
    }

    private static void WriteRawBrightnessViaWinUsb(StudioDisplayInfo display, uint raw)
    {
        using var ctx = OpenWinUsbBrightnessInterface(display);

        // Read the current Feature report so we can preserve any other fields
        // (e.g. on Pro XDR, bytes 5-6 carry a separate volatile uint16 that
        // we don't want to clobber). Fall back to a zero buffer if the read
        // fails so we can still attempt the SET.
        byte[] buffer;
        try
        {
            buffer = GetFeatureReport(ctx.BrightnessHandle, display);
            if (buffer.Length < display.FeatureReportByteLength)
            {
                Array.Resize(ref buffer, display.FeatureReportByteLength);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(
                $"WinUSB SET: pre-read GET_REPORT failed ({ex.Message}); " +
                "falling back to zero-filled template.");
            buffer = new byte[display.FeatureReportByteLength];
        }
        buffer[0] = display.BrightnessReportId;
        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(display.BrightnessByteOffset, 4), raw);

        int err = TrySetReport(ctx.BrightnessHandle, display,
            WinUsbNative.HidReportTypeFeature, buffer);
        if (err != 0)
        {
            throw new Win32Exception(err,
                $"WinUsb_ControlTransfer SET_REPORT failed (err={err}) " +
                $"on iface={display.UsbInterfaceNumber}, raw={raw}.");
        }
        DiagnosticLog.Write(
            $"WinUSB SET ok: iface={display.UsbInterfaceNumber}, raw={raw} (0x{raw:X8}), " +
            $"bytes=[{ToHex(buffer)}]");

        // Verify by reading back. If the readback doesn't match what we wrote,
        // we're either talking to the wrong interface or the device's firmware
        // is silently rejecting the value — either way we want it in the log.
        try
        {
            var verify = GetFeatureReport(ctx.BrightnessHandle, display);
            var got = BinaryPrimitives.ReadUInt32LittleEndian(
                verify.AsSpan(display.BrightnessByteOffset, 4));
            if (got != raw)
            {
                DiagnosticLog.Write(
                    $"WinUSB SET ok but verify mismatch: wrote raw={raw} (0x{raw:X8}), " +
                    $"readback={got} (0x{got:X8}) on iface={display.UsbInterfaceNumber}. " +
                    $"verify=[{ToHex(verify)}]");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"WinUSB SET ok but verify GET failed: {ex.Message}");
        }
    }

    // Holds the handles needed to talk to one Apple display's brightness
    // interface. PrimaryHandle is the WinUsb handle from WinUsb_Initialize
    // (always for the first/default interface of the composite device).
    // BrightnessHandle is the handle for the interface that carries the
    // brightness Feature reports — usually obtained via
    // WinUsb_GetAssociatedInterface unless brightness happens to live on the
    // default interface. Dispose releases everything, in the right order.
    private sealed class WinUsbContext : IDisposable
    {
        public HidDeviceSafeHandle? FileHandle;
        public IntPtr PrimaryHandle;
        public IntPtr BrightnessHandle;
        public bool OwnsBrightnessHandle;

        public void Dispose()
        {
            if (OwnsBrightnessHandle && BrightnessHandle != IntPtr.Zero)
            {
                WinUsbNative.WinUsb_Free(BrightnessHandle);
                BrightnessHandle = IntPtr.Zero;
            }
            if (PrimaryHandle != IntPtr.Zero)
            {
                WinUsbNative.WinUsb_Free(PrimaryHandle);
                PrimaryHandle = IntPtr.Zero;
            }
            FileHandle?.Dispose();
            FileHandle = null;
        }
    }

    private static WinUsbContext OpenWinUsbBrightnessInterface(StudioDisplayInfo display)
    {
        var ctx = new WinUsbContext();
        try
        {
            ctx.FileHandle = OpenDeviceOverlapped(display.DevicePath);
            if (!WinUsbNative.WinUsb_Initialize(ctx.FileHandle, out ctx.PrimaryHandle))
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"WinUsb_Initialize failed (err={err}).");
            }

            if (display.WinUsbAssociatedInterfaceIndex < 0)
            {
                ctx.BrightnessHandle = ctx.PrimaryHandle;
                ctx.OwnsBrightnessHandle = false;
            }
            else
            {
                if (!WinUsbNative.WinUsb_GetAssociatedInterface(
                        ctx.PrimaryHandle,
                        (byte)display.WinUsbAssociatedInterfaceIndex,
                        out ctx.BrightnessHandle))
                {
                    var err = Marshal.GetLastWin32Error();
                    throw new Win32Exception(err,
                        $"WinUsb_GetAssociatedInterface(idx={display.WinUsbAssociatedInterfaceIndex}) " +
                        $"failed (err={err}). The brightness HID interface (typically iface 2 on " +
                        "Pro Display XDR) is not bound to WinUSB. Re-run Zadig and install the " +
                        "WinUSB driver on the *composite* USB device, not a single interface.");
                }
                ctx.OwnsBrightnessHandle = true;
            }
            return ctx;
        }
        catch
        {
            ctx.Dispose();
            throw;
        }
    }

    private static byte[] GetFeatureReport(IntPtr winUsb, StudioDisplayInfo display)
    {
        var buffer = new byte[display.FeatureReportByteLength];
        buffer[0] = display.BrightnessReportId;
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
        return buffer;
    }

    private static int TrySetReport(IntPtr winUsb, StudioDisplayInfo display, byte reportType, byte[] data)
    {
        var setup = new WinUsbNative.WINUSB_SETUP_PACKET
        {
            RequestType = WinUsbNative.RequestTypeClassInterfaceOut,
            Request = WinUsbNative.HidRequestSetReport,
            Value = (ushort)((reportType << 8) | display.BrightnessReportId),
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

    private static DisplayEnumerationResult Enumerate()
    {
        DiagnosticLog.Reset("Enumerate()");
        var found = new List<StudioDisplayInfo>();
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
        // rejected by Windows' generic HID driver, but with WinUSB bound (via Zadig)
        // we can still send the same SET_REPORT / GET_REPORT control transfers.
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
            DiagnosticLog.Write("");
            DiagnosticLog.Write("Hint: open Device Manager, choose View -> Devices by container.");
            DiagnosticLog.Write("If your Apple display does not appear there at all, the USB / HID");
            DiagnosticLog.Write("control channel is not reaching this PC. Try a different cable, plug");
            DiagnosticLog.Write("directly into the PC (not through a hub), and confirm the cable is");
            DiagnosticLog.Write("USB-C / Thunderbolt (not DisplayPort-only).");
            DiagnosticLog.Write("");
            DiagnosticLog.Write("If your Apple Pro Display XDR appears in Device Manager with a yellow");
            DiagnosticLog.Write("warning (Code 10), Windows' built-in HID driver doesn't understand");
            DiagnosticLog.Write("its descriptor and WinUSB is not bound yet. DisplayDial can install");
            DiagnosticLog.Write("the WinUSB driver for you — when the display is detected it shows a");
            DiagnosticLog.Write("\"Set up display\" button that performs a one-time, in-app install.");
        }
        return new DisplayEnumerationResult(result, pendingSetups);
    }

    private readonly record struct UsbProbeResult(
        List<StudioDisplayInfo> Displays,
        List<PendingDriverSetup> PendingSetups);

    private static UsbProbeResult EnumerateUsbAndProbeWinUsb()
    {
        var results = new List<StudioDisplayInfo>();
        var pending = new List<PendingDriverSetup>();
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
            return new UsbProbeResult(results, pending);
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
                        "  -> Pro Display XDR detected on the USB bus but WinUSB is not bound. " +
                        "DisplayDial can install it (use the \"Set up display\" button).");
                    pending.Add(new PendingDriverSetup(
                        VendorId: AppleVendorId,
                        ProductId: ProDisplayXdrPid,
                        ProductName: "Apple Pro Display XDR",
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
        return new UsbProbeResult(results, pending);
    }

    private static (StudioDisplayInfo? Info, bool DriverNotBound) TryProbeProXdrViaWinUsb(string path)
    {
        HidDeviceSafeHandle? handle = null;
        IntPtr primaryWinUsb = IntPtr.Zero;
        var associatedHandles = new List<IntPtr>();
        try
        {
            handle = TryOpenDeviceOverlapped(path);
            if (handle is null)
            {
                DiagnosticLog.Write(
                    $"  WinUSB probe: CreateFile failed (err={Marshal.GetLastWin32Error()}). " +
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
            // Identify the brightness one by looking for the canonical pair
            // Usage Page 0x8005 + Usage 0x1009 (Apple vendor brightness on
            // Pro Display XDR — verified against the Linux apple_bl_usb
            // driver and the macOS apdbctl CLI).
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
                    reportDesc, ProXdrBrightnessUsagePage, ProXdrBrightnessUsage);
                DiagnosticLog.Write(
                    $"  iface {ifaceNum}: HID report descriptor = {reportDesc.Length} bytes; " +
                    $"contains Usage Page 0x{ProXdrBrightnessUsagePage:X4} / " +
                    $"Usage 0x{ProXdrBrightnessUsage:X4} = {matches}.");
                if (!matches)
                {
                    continue;
                }

                // Found the brightness interface. Read the current brightness
                // value to confirm and surface it in the log.
                var probe = new byte[ProXdrFeatureReportByteLength];
                probe[0] = ProXdrBrightnessReportId;
                var setup = new WinUsbNative.WINUSB_SETUP_PACKET
                {
                    RequestType = WinUsbNative.RequestTypeClassInterfaceIn,
                    Request = WinUsbNative.HidRequestGetReport,
                    Value = (ushort)((WinUsbNative.HidReportTypeFeature << 8) | ProXdrBrightnessReportId),
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
                if ((int)xfer >= ProXdrBrightnessByteOffset + 4)
                {
                    var brightness = BinaryPrimitives.ReadUInt32LittleEndian(
                        probe.AsSpan(ProXdrBrightnessByteOffset, 4));
                    DiagnosticLog.Write(
                        $"  iface {ifaceNum}: decoded brightness (uint32-LE @offset " +
                        $"{ProXdrBrightnessByteOffset}) = {brightness} " +
                        $"(range {ProXdrMinBrightness}..{ProXdrMaxBrightness}).");
                }

                return (new StudioDisplayInfo(
                    DevicePath: path,
                    ProductName: "Apple Pro Display XDR",
                    SerialNumber: TryParseSerialFromPath(path),
                    ProductId: ProDisplayXdrPid,
                    FeatureReportByteLength: ProXdrFeatureReportByteLength,
                    BrightnessReportId: ProXdrBrightnessReportId,
                    MinRawBrightness: ProXdrMinBrightness,
                    MaxRawBrightness: ProXdrMaxBrightness,
                    Transport: DisplayTransport.WinUsb,
                    UsbInterfaceNumber: ifaceNum,
                    BrightnessByteOffset: ProXdrBrightnessByteOffset,
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
    internal static bool DescriptorContainsUsage(byte[] desc, ushort wantedPage, ushort wantedUsage)
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
