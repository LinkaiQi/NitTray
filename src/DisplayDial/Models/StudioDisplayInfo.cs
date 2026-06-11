namespace DisplayDial.Models;

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
    uint MaxRawBrightness);
