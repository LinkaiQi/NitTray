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
    private const int ControlInterfaceNumber = 7;
    private const byte BrightnessReportId = 0x01;
    private const uint MinRawBrightness = 400;
    private const uint MaxRawBrightness = 60000;
    private const int FeatureReportLength = 7;
    private const int ErrorNoMoreItems = 259;

    private static readonly (ushort Pid, string Name)[] SupportedDisplays =
    {
        (0x1114, "Apple Studio Display"),
        (0x1116, "Apple Studio Display XDR"),
        (0x1118, "Apple Studio Display"),
    };

    public Task<IReadOnlyList<StudioDisplayInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<StudioDisplayInfo>>(Enumerate, cancellationToken);

    public Task<int> ReadBrightnessPercentAsync(StudioDisplayInfo display, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            using var handle = OpenDevice(display.DevicePath);
            return RawToPercent(ReadRawBrightness(handle));
        }, cancellationToken);

    public Task SetBrightnessPercentAsync(StudioDisplayInfo display, int percent, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            using var handle = OpenDevice(display.DevicePath);
            WriteRawBrightness(handle, PercentToRaw(percent));
        }, cancellationToken);

    private static IReadOnlyList<StudioDisplayInfo> Enumerate()
    {
        var results = new List<StudioDisplayInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        HidNative.HidD_GetHidGuid(out var hidGuid);
        var devInfoSet = SetupApiNative.SetupDiGetClassDevs(
            ref hidGuid,
            IntPtr.Zero,
            IntPtr.Zero,
            SetupApiNative.DIGCF_PRESENT | SetupApiNative.DIGCF_DEVICEINTERFACE);

        if (devInfoSet == SetupApiNative.INVALID_HANDLE_VALUE)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to enumerate HID devices.");
        }

        try
        {
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
                    throw new Win32Exception(err, "SetupDiEnumDeviceInterfaces failed.");
                }

                var path = GetDevicePath(devInfoSet, ref ifaceData);
                if (path is null)
                {
                    continue;
                }

                if (!MatchesStudioDisplay(path, out var productId))
                {
                    continue;
                }

                if (!seen.Add(path))
                {
                    continue;
                }

                results.Add(ProbeDisplay(path, productId));
            }
        }
        finally
        {
            SetupApiNative.SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        return results;
    }

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

            // Path string lives right after the cbSize DWORD.
            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool MatchesStudioDisplay(string path, out ushort productId)
    {
        productId = 0;
        var lower = path.ToLowerInvariant();

        if (!lower.Contains($"vid_{AppleVendorId:x4}"))
        {
            return false;
        }

        var interfaceToken = $"&mi_{ControlInterfaceNumber:x2}";
        if (!lower.Contains(interfaceToken))
        {
            return false;
        }

        foreach (var (pid, _) in SupportedDisplays)
        {
            if (lower.Contains($"pid_{pid:x4}"))
            {
                productId = pid;
                return true;
            }
        }

        return false;
    }

    private static StudioDisplayInfo ProbeDisplay(string path, ushort productId)
    {
        var defaultName = LookupName(productId);

        try
        {
            using var handle = OpenDevice(path);
            var product = ReadHidString(handle, HidNative.HidD_GetProductString);
            var serial = ReadHidString(handle, HidNative.HidD_GetSerialNumberString);

            return new StudioDisplayInfo(
                DevicePath: path,
                ProductName: string.IsNullOrWhiteSpace(product) ? defaultName : product!,
                SerialNumber: string.IsNullOrWhiteSpace(serial) ? null : serial,
                ProductId: productId);
        }
        catch
        {
            return new StudioDisplayInfo(path, defaultName, null, productId);
        }
    }

    private static string LookupName(ushort productId)
    {
        foreach (var (pid, name) in SupportedDisplays)
        {
            if (pid == productId)
            {
                return name;
            }
        }

        return "Apple Studio Display";
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
            var err = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(err, $"Could not open Apple Studio Display control interface (path: {path}).");
        }

        return handle;
    }

    private static uint ReadRawBrightness(HidDeviceSafeHandle handle)
    {
        var buffer = CreateFeatureBuffer(0);
        if (!HidNative.HidD_GetFeature(handle, buffer, buffer.Length))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "HidD_GetFeature failed while reading brightness.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(1, 4));
    }

    private static void WriteRawBrightness(HidDeviceSafeHandle handle, uint raw)
    {
        var buffer = CreateFeatureBuffer(raw);
        if (!HidNative.HidD_SetFeature(handle, buffer, buffer.Length))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "HidD_SetFeature failed while writing brightness.");
        }
    }

    private static byte[] CreateFeatureBuffer(uint raw)
    {
        // 7-byte feature report: [ReportId 0x01][uint32 LE brightness][2 padding bytes]
        var buffer = new byte[FeatureReportLength];
        buffer[0] = BrightnessReportId;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(1, 4), raw);
        return buffer;
    }

    private static uint PercentToRaw(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        var raw = MinRawBrightness + (uint)Math.Round((MaxRawBrightness - MinRawBrightness) * (percent / 100.0));
        return Math.Clamp(raw, MinRawBrightness, MaxRawBrightness);
    }

    private static int RawToPercent(uint raw)
    {
        var clamped = Math.Clamp(raw, MinRawBrightness, MaxRawBrightness);
        var percent = (clamped - MinRawBrightness) * 100.0 / (MaxRawBrightness - MinRawBrightness);
        return (int)Math.Round(percent);
    }
}
