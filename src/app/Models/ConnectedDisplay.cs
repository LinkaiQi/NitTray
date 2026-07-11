namespace NitTray.Models;

// Holds everything we need to talk to a specific Apple display: device path +
// identity + brightness capabilities resolved at enumeration time (probed from
// the HID descriptor, or taken from the catalog for WinUSB models).
public sealed record ConnectedDisplay(
    string DevicePath,
    string ProductName,
    string? SerialNumber,
    ushort ProductId,
    int FeatureReportByteLength,
    byte BrightnessReportId,
    uint MinRawBrightness,
    uint MaxRawBrightness,
    DisplayTransport Transport = DisplayTransport.Hid,
    // bInterfaceNumber of the brightness interface, sent as wIndex on every
    // GET_REPORT/SET_REPORT. Pro Display XDR: brightness is on interface 2.
    byte UsbInterfaceNumber = 0,
    // Offset of the brightness value in the feature report (after the report-ID
    // byte). Always uint32 little-endian.
    int BrightnessByteOffset = 1,
    // WinUSB associated-interface index for the brightness interface on the
    // composite device. -1 = use the primary WinUsb_Initialize handle directly;
    // 0 -> interface 1, 1 -> interface 2, etc.
    sbyte WinUsbAssociatedInterfaceIndex = -1);
