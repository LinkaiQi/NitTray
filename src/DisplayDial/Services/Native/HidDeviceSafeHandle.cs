using Microsoft.Win32.SafeHandles;

namespace DisplayDial.Services.Native;

internal sealed class HidDeviceSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public HidDeviceSafeHandle() : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle()
    {
        return Kernel32Native.CloseHandle(handle);
    }
}
