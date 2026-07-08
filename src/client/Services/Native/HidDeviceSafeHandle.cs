using Microsoft.Win32.SafeHandles;

namespace NitTray.Services.Native;

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
