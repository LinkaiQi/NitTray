namespace NitTray.Models.Displays;

// Studio Display (2nd generation) — USB PID 0x1118. HID brightness auto-probed at
// runtime. Its interface reports the generic string "HID Relay", so the curated
// Name below is used instead.
public static class StudioDisplay2ndGen
{
    public static readonly DisplayModel Model = new(
        ProductId: 0x1118,
        Name: "Studio Display (2nd Generation)",
        Transport: DisplayTransport.Hid,
        RequiresWinUsbDriver: false);
}
