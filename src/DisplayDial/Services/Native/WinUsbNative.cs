using System.Runtime.InteropServices;

namespace DisplayDial.Services.Native;

internal static class WinUsbNative
{
    // USB request type bitfields (see USB 2.0 spec § 9.3).
    //   bit 7   : direction (0 = host->device, 1 = device->host)
    //   bits 6-5: type (0 = standard, 1 = class, 2 = vendor)
    //   bits 4-0: recipient (0 = device, 1 = interface, 2 = endpoint)
    public const byte RequestTypeClassInterfaceOut = 0x21;
    public const byte RequestTypeClassInterfaceIn = 0xA1;

    // HID class-specific control requests (HID 1.11 § 7.2).
    public const byte HidRequestGetReport = 0x01;
    public const byte HidRequestSetReport = 0x09;

    // HID report types (HID 1.11 § 7.2.1).
    public const byte HidReportTypeInput = 0x01;
    public const byte HidReportTypeOutput = 0x02;
    public const byte HidReportTypeFeature = 0x03;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WINUSB_SETUP_PACKET
    {
        public byte RequestType;
        public byte Request;
        public ushort Value;
        public ushort Index;
        public ushort Length;
    }

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_Initialize(
        HidDeviceSafeHandle deviceHandle,
        out IntPtr interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_Free(IntPtr interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_ControlTransfer(
        IntPtr interfaceHandle,
        WINUSB_SETUP_PACKET setupPacket,
        byte[] buffer,
        uint bufferLength,
        out uint lengthTransferred,
        IntPtr overlapped);
}
