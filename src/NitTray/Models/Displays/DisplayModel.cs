namespace NitTray.Models.Displays;

// Static, compile-time description of a known Apple display model. One instance
// per supported product lives in its own file in this folder, and DisplayCatalog
// aggregates them.
//
// To support a new Apple display, add a file here that exposes a DisplayModel and
// register it in DisplayCatalog.All — no other code needs to change for naming or
// identity, and (for WinUSB models) the brightness protocol travels with it.
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
