using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using NitTray.Models;
using NitTray.Models.Displays;
using NitTray.Services.Native;

namespace NitTray.Services;

// Reads and writes Apple display brightness over USB. The service is split
// across several files (all `partial class AppleDisplayService`) by concern.
// This file holds the IDisplayService entry points and the brightness
// read/write paths for both HID and WinUSB transports.
public sealed partial class AppleDisplayService : IDisplayService
{
    private const ushort AppleVendorId = DisplayCatalog.AppleVendorId;
    private const int ErrorNoMoreItems = 259;

    // The Pro Display XDR is the one model whose brightness travels over WinUSB.
    // Its identity and feature-report layout live in the catalog
    // (Models/Displays/ProDisplayXdr.cs); we pull them out once here so the WinUSB
    // read/write/probe paths can reference them by their protocol field names.
    private static readonly DisplayModel ProXdrModel = ProDisplayXdr.Model;
    private static readonly ushort ProDisplayXdrPid = ProXdrModel.ProductId;
    private static readonly BrightnessProtocol ProXdr = ProXdrModel.Brightness!;

    public Task<DisplayEnumerationResult> EnumerateAsync(CancellationToken cancellationToken = default)
        => Task.Run(Enumerate, cancellationToken);

    public Task<int> ReadBrightnessPercentAsync(ConnectedDisplay display, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            return RawToPercent(display, ReadRawBrightness(display));
        }, cancellationToken);

    public Task SetBrightnessPercentAsync(ConnectedDisplay display, int percent, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            WriteRawBrightness(display, PercentToRaw(display, percent));
        }, cancellationToken);

    private static uint ReadRawBrightness(ConnectedDisplay display)
    {
        return display.Transport switch
        {
            DisplayTransport.WinUsb => ReadRawBrightnessViaWinUsb(display),
            _ => ReadRawBrightnessViaHid(display),
        };
    }

    private static void WriteRawBrightness(ConnectedDisplay display, uint raw)
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

    private static uint ReadRawBrightnessViaHid(ConnectedDisplay display)
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

    private static void WriteRawBrightnessViaHid(ConnectedDisplay display, uint raw)
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

    private static uint ReadRawBrightnessViaWinUsb(ConnectedDisplay display)
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

    private static void WriteRawBrightnessViaWinUsb(ConnectedDisplay display, uint raw)
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
#if DEBUG
        // Success of a brightness write is on the slider hot path, so keep this
        // per-change confirmation out of Release logs (the failure above still
        // throws and is surfaced). Enumeration diagnostics remain on in Release.
        DiagnosticLog.Write(
            $"WinUSB SET ok: iface={display.UsbInterfaceNumber}, raw={raw} (0x{raw:X8}), " +
            $"bytes=[{ToHex(buffer)}]");
#endif

#if DEBUG
        // Verify by reading back. If the readback doesn't match what we wrote,
        // we're either talking to the wrong interface or the device's firmware
        // is silently rejecting the value — either way we want it in the log. This
        // is a debug-only sanity check: it costs an extra USB round-trip on every
        // brightness change, so it is compiled out of Release builds.
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
#endif
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

    private static WinUsbContext OpenWinUsbBrightnessInterface(ConnectedDisplay display)
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
                        "Pro Display XDR) is not bound to WinUSB. Run NitTray's display setup " +
                        "again so WinUSB is installed on the composite USB device.");
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

    private static byte[] GetFeatureReport(IntPtr winUsb, ConnectedDisplay display)
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

    private static int TrySetReport(IntPtr winUsb, ConnectedDisplay display, byte reportType, byte[] data)
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

}
