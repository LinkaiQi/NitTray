namespace DisplayDial.Models;

public enum DisplayTransport
{
    // We open the per-interface HID device path and use HidD_GetFeature/SetFeature.
    // Standard Windows path for any Apple display whose HID descriptor is well-formed
    // (Studio Display family).
    Hid,

    // We open the parent USB device, attach via WinUsb_Initialize, and issue
    // GET_REPORT/SET_REPORT control transfers ourselves. Used when the device's HID
    // descriptor is incompatible with Windows' generic hidclass.sys (e.g. the Apple
    // Pro Display XDR — Code 10 on the HID interface) and the user has bound WinUSB
    // to the device via Zadig.
    WinUsb,
}

// Holds everything we need to talk to a specific Apple display:
// device path + identity + brightness HID capabilities probed at enumeration time.
public sealed record StudioDisplayInfo(
    string DevicePath,
    string ProductName,
    string? SerialNumber,
    ushort ProductId,
    int FeatureReportByteLength,
    byte BrightnessReportId,
    uint MinRawBrightness,
    uint MaxRawBrightness,
    DisplayTransport Transport = DisplayTransport.Hid,
    // bInterfaceNumber of the brightness HID interface — stamped as wIndex of
    // every GET_REPORT / SET_REPORT control transfer. Apple Pro Display XDR
    // exposes 5 HID interfaces and brightness lives on interface 2.
    byte UsbInterfaceNumber = 0,
    // Byte offset of the brightness value inside the feature report (right
    // after the report ID byte at offset 0). Always interpreted as uint32
    // little-endian for both Studio Display and Pro Display XDR.
    int BrightnessByteOffset = 1,
    // WinUSB associated-interface index used to obtain a handle for the
    // brightness interface from the composite device. -1 means "use the
    // primary handle from WinUsb_Initialize directly" (i.e. brightness is
    // on the first interface, no navigation needed). 0 -> interface 1,
    // 1 -> interface 2, etc.
    sbyte WinUsbAssociatedInterfaceIndex = -1);
