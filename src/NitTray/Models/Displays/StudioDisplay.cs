namespace NitTray.Models.Displays;

// Studio Display (1st generation) — USB PID 0x1114.
//
// Its brightness control is a standards-compliant HID interface (MI_07), so
// Windows' in-box HID driver binds automatically and we read the brightness
// range live from the HID report descriptor. No explicit BrightnessProtocol
// is needed.
public static class StudioDisplay
{
    public static readonly DisplayModel Model = new(
        ProductId: 0x1114,
        Name: "Studio Display",
        Transport: DisplayTransport.Hid,
        RequiresWinUsbDriver: false);
}
