namespace NitTray.Models.Displays;

// Compile-time description of a known Apple display model, one per file here and
// aggregated by DisplayCatalog. To add a display, add a file and register it in
// DisplayCatalog.All.
public sealed record DisplayModel(
    // USB product id (the vendor id is always DisplayCatalog.AppleVendorId).
    ushort ProductId,
    // Friendly marketing name shown on the display's card in the UI.
    string Name,
    // How NitTray talks to this display's brightness control.
    DisplayTransport Transport,
    // True when Windows' in-box HID driver rejects the brightness interface and
    // NitTray must install the WinUSB function driver first (the Pro Display XDR).
    bool RequiresWinUsbDriver,
    // Explicit brightness feature-report protocol. null means "discover it at
    // runtime from the HID report descriptor" — the Studio Display family exposes
    // a well-formed descriptor, so we read its report id and range live. Models
    // reached over WinUSB must specify it because we bypass the Windows HID parser.
    BrightnessProtocol? Brightness = null);
