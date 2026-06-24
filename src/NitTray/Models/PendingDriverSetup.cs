namespace NitTray.Models;

// Describes an Apple display that is physically present on the USB bus but
// cannot be driven yet because the WinUSB function driver is not bound to it.
//
// The Apple Pro Display XDR is the known case: Windows' generic HID driver
// rejects its brightness interface (Code 10), so brightness control requires
// WinUSB to own the whole composite device. NitTray can install that driver
// in one click via the elevated NitTray.DriverSetup helper.
public sealed record PendingDriverSetup(
    ushort VendorId,
    ushort ProductId,
    string ProductName,
    string? SerialNumber,
    string DevicePath);
