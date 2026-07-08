namespace NitTray.Models.Displays;

// Studio Display (2nd generation) — USB PID 0x1118.
//
// Same standards-compliant HID brightness interface as the rest of the Studio
// Display family, so the brightness range is auto-probed at runtime. (Its
// brightness interface reports the generic USB product string "HID Relay", which
// is why the curated Name here is preferred over the device-reported string.)
public static class StudioDisplay2ndGen
{
    public static readonly DisplayModel Model = new(
        ProductId: 0x1118,
        Name: "Studio Display (2nd Generation)",
        Transport: DisplayTransport.Hid,
        RequiresWinUsbDriver: false);
}
