namespace NitTray.Models.Displays;

// Studio Display XDR — USB PID 0x1116.
//
// Same standards-compliant HID brightness interface as the rest of the Studio
// Display family, so the brightness range is auto-probed at runtime.
public static class StudioDisplayXdr
{
    public static readonly DisplayModel Model = new(
        ProductId: 0x1116,
        Name: "Studio Display XDR",
        Transport: DisplayTransport.Hid,
        RequiresWinUsbDriver: false);
}
