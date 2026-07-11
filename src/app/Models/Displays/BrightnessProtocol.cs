namespace NitTray.Models.Displays;

// The exact brightness feature-report layout for a display whose protocol we
// cannot discover through the Windows HID parser (i.e. WinUSB models such as the
// Pro Display XDR). Every value is taken from the device's HID report descriptor.
public sealed record BrightnessProtocol(
    // Report id of the brightness Feature report (byte 0 of the report).
    byte ReportId,
    // Total length of the Feature report in bytes.
    int FeatureReportByteLength,
    // Byte offset of the uint32 little-endian brightness value within the report.
    int ByteOffset,
    // Raw brightness range reported by the device.
    uint MinRaw,
    uint MaxRaw,
    // (UsagePage, Usage) that identifies the brightness control in the HID report
    // descriptor, used to pick the right interface on a composite device.
    ushort UsagePage,
    ushort Usage);
