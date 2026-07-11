namespace NitTray.Models.Displays;

// Pro Display XDR — USB PID 0x9243. Windows' hidclass.sys rejects its brightness
// HID interface (Code 10), so NitTray installs WinUSB and issues GET_REPORT/
// SET_REPORT itself — which means the report layout must be specified explicitly.
// The brightness interface is matched by the VESA Monitor/Brightness usage
// (0x0082/0x0010). Feature report 0x01 is 7 bytes:
//   byte 0     : report id (0x01)
//   bytes 1..4 : brightness, uint32 little-endian, range 400..50000
//   bytes 5..6 : a second volatile uint16 we preserve via read-modify-write
public static class ProDisplayXdr
{
    public static readonly DisplayModel Model = new(
        ProductId: 0x9243,
        Name: "Pro Display XDR",
        Transport: DisplayTransport.WinUsb,
        RequiresWinUsbDriver: true,
        Brightness: new BrightnessProtocol(
            ReportId: 0x01,
            FeatureReportByteLength: 7,
            ByteOffset: 1,
            MinRaw: 400,
            MaxRaw: 50000,
            UsagePage: 0x0082,
            Usage: 0x0010));
}
