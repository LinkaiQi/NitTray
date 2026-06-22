namespace DisplayDial.Models;

// Well-known Apple USB identities, kept in one place so the enumeration code and
// the driver install/reset code agree on what to look for.
public static class AppleDisplays
{
    public const ushort VendorId = 0x05AC;

    // The Pro Display XDR is the only Apple display that needs the WinUSB function
    // driver: Windows' generic hidclass.sys rejects its brightness HID interface
    // (Code 10), so brightness control requires WinUSB to own the whole composite
    // device. It is therefore the target of both driver install and driver reset.
    public const ushort ProDisplayXdrProductId = 0x9243;
}
