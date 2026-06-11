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
    private const int ErrorNoMoreItems = 259;

    // Models we recognise. Used for friendly product names and as a fast-path filter
    // when matching device paths. Devices not in this list can still work — we fall
    // back to "any Apple HID interface that exposes Monitor/Brightness usage".
    private static readonly (ushort Pid, string Name)[] KnownDisplays =
    {
        (0x1114, "Apple Studio Display"),
        (0x1116, "Apple Studio Display XDR"),
        (0x1118, "Apple Studio Display"),
        (0x9243, "Apple Pro Display XDR"),
    };

    public Task<IReadOnlyList<StudioDisplayInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<StudioDisplayInfo>>(Enumerate, cancellationToken);

    public Task<int> ReadBrightnessPercentAsync(StudioDisplayInfo display, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            using var handle = OpenDevice(display.DevicePath);
            return RawToPercent(display, ReadRawBrightness(handle, display));
        }, cancellationToken);

    public Task SetBrightnessPercentAsync(StudioDisplayInfo display, int percent, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            using var handle = OpenDevice(display.DevicePath);
            WriteRawBrightness(handle, display, PercentToRaw(display, percent));
        }, cancellationToken);

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
                    DiagnosticLog.Write($"[{index}] (null path)");
                    continue;
                }
                if (!IsAppleVendor(path))
                {
                    continue;
                }
                appleSeen++;
                if (!seenPaths.Add(path))
                {
                    DiagnosticLog.Write($"[Apple] DUPLICATE path skipped: {path}");
                    continue;
                }

                DiagnosticLog.Write($"[Apple #{appleSeen}] probing path: {path}");
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
            $"Enumeration done. Total HID devices: {totalSeen}, Apple-vendor: {appleSeen}, " +
            $"raw matches: {found.Count}");

        var result = DeduplicateBySerial(found);
        DiagnosticLog.Write($"After dedup: {result.Count} display(s).");
        foreach (var d in result)
        {
            DiagnosticLog.Write($"  - {d.ProductName} (serial={d.SerialNumber ?? "-"}, pid=0x{d.ProductId:X4})");
        }
        return result;
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

    private static uint ReadRawBrightness(HidDeviceSafeHandle handle, StudioDisplayInfo display)
    {
        var buffer = CreateFeatureBuffer(display, 0);
        if (!HidNative.HidD_GetFeature(handle, buffer, buffer.Length))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "HidD_GetFeature failed while reading brightness.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(1, 4));
    }

    private static void WriteRawBrightness(HidDeviceSafeHandle handle, StudioDisplayInfo display, uint raw)
    {
        var buffer = CreateFeatureBuffer(display, raw);
        if (!HidNative.HidD_SetFeature(handle, buffer, buffer.Length))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "HidD_SetFeature failed while writing brightness.");
        }
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
