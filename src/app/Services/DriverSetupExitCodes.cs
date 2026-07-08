namespace NitTray.Services;

// Exit-code contract shared with the native NitTray.DriverSetup.exe helper.
//
// IMPORTANT: keep these values in sync with src/driver/main.c.
// The helper communicates its result purely through its process exit code; any
// human-readable detail is appended to driver-setup.log next to the app log.
public static class DriverSetupExitCodes
{
    public const int Success = 0;
    public const int GenericError = 1;
    public const int BadArguments = 2;
    public const int DeviceNotFound = 3;
    public const int PrepareFailed = 4;
    public const int InstallFailed = 5;
    public const int UninstallFailed = 6;
}
