namespace NitTray.Models;

// How NitTray reaches a display's brightness control.
public enum DisplayTransport
{
    // We open the per-interface HID device path and use HidD_GetFeature/SetFeature.
    // Standard Windows path for any Apple display whose HID descriptor is well-formed
    // (the Studio Display family).
    Hid,

    // We open the parent USB device, attach via WinUsb_Initialize, and issue
    // GET_REPORT/SET_REPORT control transfers ourselves. Used when the device's HID
    // descriptor is incompatible with Windows' generic hidclass.sys (the Pro Display
    // XDR — Code 10 on the HID interface) and WinUSB has been bound to the device.
    WinUsb,
}
