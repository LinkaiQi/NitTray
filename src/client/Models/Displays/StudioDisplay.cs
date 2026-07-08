namespace NitTray.Models.Displays;

// Studio Display (1st generation) — USB PID 0x1114. Standards-compliant HID
// interface, so Windows binds it automatically and we probe brightness at runtime.
public static class StudioDisplay
{
    public static readonly DisplayModel Model = new(
        ProductId: 0x1114,
        Name: "Studio Display",
        Transport: DisplayTransport.Hid,
        RequiresWinUsbDriver: false);
}
