using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using DisplayDial.Models;

namespace DisplayDial.Services;

// Launches the elevated native helper (DisplayDial.DriverSetup.exe) that uses
// libwdi to install WinUSB onto the composite Apple display. The helper must run
// as Administrator (its manifest requests it), so we start it with the "runas"
// shell verb which raises a single UAC prompt.
//
// The helper installs WinUSB on the *whole* composite device (the parent node,
// no MI_ suffix). That is what lets the app open one WinUSB handle and walk every
// interface via WinUsb_GetAssociatedInterface to reach the brightness interface.
public sealed class WinUsbDriverInstallService : IDriverInstallService
{
    private const string HelperFileName = "DisplayDial.DriverSetup.exe";

    // ERROR_CANCELLED: ShellExecute returns this when the user declines UAC.
    private const int ErrorCancelled = 1223;

    public Task<DriverInstallResult> InstallAsync(
        PendingDriverSetup target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var helperPath = ResolveHelperPath();
        if (helperPath is null)
        {
            DiagnosticLog.Write(
                $"Driver setup: helper '{HelperFileName}' not found next to the app.");
            return Task.FromResult(new DriverInstallResult(
                DriverInstallStatus.HelperMissing,
                $"The driver setup helper ({HelperFileName}) is missing from the " +
                "installation folder. Reinstall DisplayDial and try again."));
        }

        var psi = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = true, // required so Verb = "runas" can elevate
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("install");
        psi.ArgumentList.Add(target.VendorId.ToString("X4"));
        psi.ArgumentList.Add(target.ProductId.ToString("X4"));

        DiagnosticLog.Write(
            $"Driver setup: launching '{helperPath}' install " +
            $"{target.VendorId:X4} {target.ProductId:X4} (elevated).");

        return Task.Run(() =>
        {
            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                DiagnosticLog.Write("Driver setup: user declined the elevation prompt.");
                return new DriverInstallResult(
                    DriverInstallStatus.Cancelled,
                    "Driver setup was cancelled at the Windows permission prompt.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"Driver setup: failed to start helper: {ex.Message}");
                return new DriverInstallResult(
                    DriverInstallStatus.Failed,
                    $"Could not start the driver setup helper: {ex.Message}");
            }

            if (process is null)
            {
                return new DriverInstallResult(
                    DriverInstallStatus.Failed,
                    "Could not start the driver setup helper.");
            }

            using (process)
            {
                process.WaitForExit();
                return MapExitCode(process.ExitCode);
            }
        }, cancellationToken);
    }

    private static DriverInstallResult MapExitCode(int exitCode)
    {
        DiagnosticLog.Write($"Driver setup: helper exited with code {exitCode}.");
        return exitCode switch
        {
            DriverSetupExitCodes.Success => new DriverInstallResult(
                DriverInstallStatus.Success,
                "Driver installed. DisplayDial can now control this display."),

            DriverSetupExitCodes.DeviceNotFound => new DriverInstallResult(
                DriverInstallStatus.DeviceNotFound,
                "The display was not found on the USB bus. Make sure it is connected, " +
                "then try again."),

            _ => new DriverInstallResult(
                DriverInstallStatus.Failed,
                $"Driver setup failed (code {exitCode}). See the diagnostics log for " +
                $"details:\n{DiagnosticLog.FilePath}"),
        };
    }

    private static string? ResolveHelperPath()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, HelperFileName);
        return File.Exists(candidate) ? candidate : null;
    }
}
