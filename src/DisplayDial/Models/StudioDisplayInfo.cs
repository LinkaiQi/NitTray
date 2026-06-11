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
    byte UsbInterfaceNumber = 0,
    // Byte offset of the brightness value inside the feature report. For the
    // Studio Display family this is 1 (right after the report ID byte). The
    // Pro Display XDR reports brightness at byte offset 2 (its report begins
    // with an input-select byte). Always interpreted as uint16 little-endian.
    int BrightnessByteOffset = 1);
