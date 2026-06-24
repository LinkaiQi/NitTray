using System.Runtime.InteropServices;

namespace NitTray.Services.Native;

internal static class HidNative
{
    // HID Usage Page / Usage for the brightness control on Apple displays.
    //
    // Apple uses two different usage page / usage tuples for brightness depending
    // on the model:
    //
    //   - Studio Display family (PID 0x1114, 0x1116, 0x1118): the standardized
    //     Monitor / Brightness usage from HID Usage Tables 1.5
    //     ("Monitor Page" 0x82, Usage 0x10).
    //
    //   - Pro Display XDR (PID 0x9243): an Apple vendor-defined usage page
    //     (0x8005, Usage 0x1009) instead of the standardized one. Confirmed by
    //     0xcharly/apdbctl, which inspects the raw HID report descriptor and
    //     keys off these exact magic numbers.
    //
    // We accept either tuple as "this is a brightness value cap".
    public const ushort MonitorBrightnessUsagePage = 0x0082;
    public const ushort MonitorBrightnessUsage = 0x0010;
    public const ushort AppleVendorBrightnessUsagePage = 0x8005;
    public const ushort AppleVendorBrightnessUsage = 0x1009;

    public const int HIDP_STATUS_SUCCESS = unchecked((int)0x00110000);

    public enum HIDP_REPORT_TYPE
    {
        Input = 0,
        Output = 1,
        Feature = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        // USHORT Reserved[17]
        public ushort R0; public ushort R1; public ushort R2; public ushort R3;
        public ushort R4; public ushort R5; public ushort R6; public ushort R7;
        public ushort R8; public ushort R9; public ushort R10; public ushort R11;
        public ushort R12; public ushort R13; public ushort R14; public ushort R15;
        public ushort R16;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    // 72-byte struct. The union at the tail starts with a USAGE at offset 56
    // and we only need `Usage` from the NotRange variant (or UsageMin from
    // Range, same offset).
    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;
        public byte HasNull;
        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;
        public ushort Reserved2_0; public ushort Reserved2_1; public ushort Reserved2_2;
        public ushort Reserved2_3; public ushort Reserved2_4;
        public uint UnitsExp;
        public uint Units;
        public int LogicalMin;
        public int LogicalMax;
        public int PhysicalMin;
        public int PhysicalMax;
        // Union (Range / NotRange) — Usage / UsageMin at offset 56.
        public ushort Usage;
        public ushort UsageMaxOrReserved1;
        public ushort StringMinOrIndex;
        public ushort StringMaxOrReserved2;
        public ushort DesignatorMinOrIndex;
        public ushort DesignatorMaxOrReserved3;
        public ushort DataIndexMinOrIndex;
        public ushort DataIndexMaxOrReserved4;
    }

    [DllImport("hid.dll")]
    public static extern void HidD_GetHidGuid(out Guid hidGuid);

    // HidD_* functions return native BOOLEAN (1 byte UCHAR), not Win32 BOOL (4 bytes).
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetFeature(
        HidDeviceSafeHandle hidDeviceObject,
        byte[] reportBuffer,
        int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_SetFeature(
        HidDeviceSafeHandle hidDeviceObject,
        byte[] reportBuffer,
        int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetProductString(
        HidDeviceSafeHandle hidDeviceObject,
        byte[] buffer,
        int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetSerialNumberString(
        HidDeviceSafeHandle hidDeviceObject,
        byte[] buffer,
        int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetPreparsedData(
        HidDeviceSafeHandle hidDeviceObject,
        out IntPtr preparsedData);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("hid.dll")]
    public static extern int HidP_GetValueCaps(
        HIDP_REPORT_TYPE reportType,
        [In, Out] HIDP_VALUE_CAPS[] valueCaps,
        ref ushort valueCapsLength,
        IntPtr preparsedData);
}
