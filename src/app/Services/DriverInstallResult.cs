namespace NitTray.Services;

public enum DriverInstallStatus
{
    // WinUSB is now bound to the device; brightness control is available.
    Success,

    // The user declined the Windows elevation (UAC) prompt.
    Cancelled,

    // NitTray.DriverSetup.exe was not found next to the running app.
    HelperMissing,

    // The helper next to the app is not signed by the publisher that signed NitTray,
    // so it was not launched with administrator rights.
    HelperUntrusted,

    // The target display was not present on the USB bus when setup ran.
    DeviceNotFound,

    // Any other failure (driver preparation/installation error).
    Failed,
}

public sealed record DriverInstallResult(DriverInstallStatus Status, string Message)
{
    public bool IsSuccess => Status == DriverInstallStatus.Success;
}
