namespace NitTray.Models.Displays;

// Studio Display XDR — USB PID 0x1116. Standards-compliant HID brightness,
// auto-probed at runtime.
public static class StudioDisplayXdr
{
    public static readonly DisplayModel Model = new(
        ProductId: 0x1116,
        Name: "Studio Display XDR",
        Transport: DisplayTransport.Hid,
        RequiresWinUsbDriver: false);
}
