namespace DisplayDial.Services;

public enum DriverInstallStatus
{
    // WinUSB is now bound to the device; brightness control is available.
    Success,

    // The user declined the Windows elevation (UAC) prompt.
    Cancelled,

    // DisplayDial.DriverSetup.exe was not found next to the running app.
    HelperMissing,

    // The target display was not present on the USB bus when setup ran.
    DeviceNotFound,

    // Any other failure (driver preparation/installation error).
    Failed,
}

public sealed record DriverInstallResult(DriverInstallStatus Status, string Message)
{
    public bool IsSuccess => Status == DriverInstallStatus.Success;
}
