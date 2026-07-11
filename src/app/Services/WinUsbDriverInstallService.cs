using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using NitTray.Models;

namespace NitTray.Services;

// Launches the elevated native helper (NitTray.DriverSetup.exe) that uses libwdi to
// install WinUSB onto the whole composite Apple display (parent node, no MI_ suffix)
// — which lets the app open one WinUSB handle and walk every interface to reach the
// brightness one. Started with the "runas" verb for the single UAC prompt.
public sealed class WinUsbDriverInstallService : IDriverInstallService
{
    private const string HelperFileName = "NitTray.DriverSetup.exe";

    // ERROR_CANCELLED: ShellExecute returns this when the user declines UAC.
    private const int ErrorCancelled = 1223;

    public Task<DriverInstallResult> InstallAsync(
        PendingDriverSetup target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return RunHelperAsync(
            "install", target.VendorId, target.ProductId, MapInstallExitCode, cancellationToken);
    }

    public Task<DriverInstallResult> UninstallAsync(
        ushort vendorId,
        ushort productId,
        CancellationToken cancellationToken = default)
    {
        return RunHelperAsync(
            "uninstall", vendorId, productId, MapUninstallExitCode, cancellationToken);
    }

    private Task<DriverInstallResult> RunHelperAsync(
        string verb,
        ushort vendorId,
        ushort productId,
        Func<int, DriverInstallResult> mapExitCode,
        CancellationToken cancellationToken)
    {
        var helperPath = ResolveHelperPath();
        if (helperPath is null)
        {
            DiagnosticLog.Write(
                $"Driver setup: helper '{HelperFileName}' not found next to the app.");
            return Task.FromResult(new DriverInstallResult(
                DriverInstallStatus.HelperMissing,
                $"The driver setup helper ({HelperFileName}) is missing from the " +
                "installation folder. Reinstall NitTray and try again."));
        }

        var psi = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = true, // required so Verb = "runas" can elevate
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add(vendorId.ToString("X4"));
        psi.ArgumentList.Add(productId.ToString("X4"));

        DiagnosticLog.Write(
            $"Driver setup: launching '{helperPath}' {verb} " +
            $"{vendorId:X4} {productId:X4} (elevated).");

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
                    "The operation was cancelled at the Windows permission prompt.");
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
                return mapExitCode(process.ExitCode);
            }
        }, cancellationToken);
    }

    private static DriverInstallResult MapInstallExitCode(int exitCode)
    {
        DiagnosticLog.Write($"Driver setup: install helper exited with code {exitCode}.");
        return exitCode switch
        {
            DriverSetupExitCodes.Success => new DriverInstallResult(
                DriverInstallStatus.Success,
                "Driver installed. NitTray can now control this display."),

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

    private static DriverInstallResult MapUninstallExitCode(int exitCode)
    {
        DiagnosticLog.Write($"Driver setup: uninstall helper exited with code {exitCode}.");
        return exitCode switch
        {
            DriverSetupExitCodes.Success => new DriverInstallResult(
                DriverInstallStatus.Success,
                "Driver uninstalled. The display has been restored to the default Windows driver. " +
                "Run setup again whenever you want NitTray to control its brightness."),

            DriverSetupExitCodes.DeviceNotFound => new DriverInstallResult(
                DriverInstallStatus.DeviceNotFound,
                "No matching Apple display is connected, so there was nothing to uninstall."),

            _ => new DriverInstallResult(
                DriverInstallStatus.Failed,
                $"Driver uninstall failed (code {exitCode}). See the diagnostics log for " +
                $"details:\n{DiagnosticLog.FilePath}"),
        };
    }

    private static string? ResolveHelperPath()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, HelperFileName);
        return File.Exists(candidate) ? candidate : null;
    }
}
