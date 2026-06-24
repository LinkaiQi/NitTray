using System.Runtime.InteropServices;

namespace NitTray.Services.Native;

internal static class WinUsbNative
{
    // USB request type bitfields (see USB 2.0 spec § 9.3).
    //   bit 7   : direction (0 = host->device, 1 = device->host)
    //   bits 6-5: type (0 = standard, 1 = class, 2 = vendor)
    //   bits 4-0: recipient (0 = device, 1 = interface, 2 = endpoint)
    public const byte RequestTypeClassInterfaceOut = 0x21;
    public const byte RequestTypeClassInterfaceIn = 0xA1;
    public const byte RequestTypeStandardInterfaceIn = 0x81;

    // Standard USB device requests (USB 2.0 § 9.4).
    public const byte UsbRequestGetDescriptor = 0x06;

    // USB HID class descriptor types (HID 1.11 § 7.1).
    public const byte UsbDescriptorTypeHid = 0x21;
    public const byte UsbDescriptorTypeReport = 0x22;

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

    // USB interface descriptor (USB 2.0 § 9.6.5). Returned by WinUsb_QueryInterfaceSettings
    // so we can read bInterfaceNumber (HID class requests need it as wIndex).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct USB_INTERFACE_DESCRIPTOR
    {
        public byte bLength;
        public byte bDescriptorType;
        public byte bInterfaceNumber;
        public byte bAlternateSetting;
        public byte bNumEndpoints;
        public byte bInterfaceClass;
        public byte bInterfaceSubClass;
        public byte bInterfaceProtocol;
        public byte iInterface;
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

    // Composite-device navigation. WinUsb_Initialize returns the handle for the
    // *first* interface (typically interface 0). To talk to any other interface
    // of a composite USB device, ask for its handle by the *associated index*:
    //   associatedIndex = 0 -> handle for the 2nd interface (interface 1)
    //   associatedIndex = 1 -> handle for the 3rd interface (interface 2)
    //   ...
    // Returns ERROR_NO_MORE_ITEMS when there are no more associated interfaces.
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_GetAssociatedInterface(
        IntPtr interfaceHandle,
        byte associatedInterfaceIndex,
        out IntPtr associatedInterfaceHandle);

    // Reads the USB interface descriptor for a given alternate setting on the
    // interface this handle owns. We use alternate setting 0 (the default) and
    // only care about bInterfaceNumber so we can stamp the right wIndex into
    // HID class control transfers.
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_QueryInterfaceSettings(
        IntPtr interfaceHandle,
        byte alternateInterfaceNumber,
        out USB_INTERFACE_DESCRIPTOR usbAltInterfaceDescriptor);
}
