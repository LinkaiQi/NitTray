using System.Runtime.InteropServices;

namespace DisplayDial.Services.Native;

internal static class HidNative
{
    [DllImport("hid.dll")]
    public static extern void HidD_GetHidGuid(out Guid hidGuid);

    // HidD_* functions return native BOOLEAN (1 byte UCHAR), not Win32 BOOL (4 bytes).
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetFeature(
        HidDeviceSafeHandle hidDeviceObject,
        byte[] reportBuffer,
        int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_SetFeature(
        HidDeviceSafeHandle hidDeviceObject,
        byte[] reportBuffer,
        int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetProductString(
        HidDeviceSafeHandle hidDeviceObject,
        byte[] buffer,
        int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetSerialNumberString(
        HidDeviceSafeHandle hidDeviceObject,
        byte[] buffer,
        int bufferLength);
}
