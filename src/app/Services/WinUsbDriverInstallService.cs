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

        return Task.Run(() =>
        {
            // Pinned with write and delete sharing denied until the helper exits. Windows
            // loads the image only after the user answers the prompt, so verifying and
            // then starting by path would leave a window to swap the file. FILE_EXECUTE
            // counts as read access, so the pin still lets Windows start it.
            FileStream pinnedHelper;
            try
            {
                pinnedHelper = new FileStream(
                    helperPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(
                    $"Driver setup: could not lock '{helperPath}' for verification: {ex.Message}");
                return new DriverInstallResult(
                    DriverInstallStatus.Failed,
                    $"The driver setup helper ({HelperFileName}) could not be opened for " +
                    $"verification: {ex.Message}\n\nIf security software is scanning it, " +
                    "wait a moment and try again.");
            }

            using (pinnedHelper)
            {
                // Runs elevated out of a user-writable folder, so confirm it is ours.
                if (!HelperTrust.IsTrustedToElevate(helperPath, out var trustReason))
                {
                    DiagnosticLog.Write(
                        $"Driver setup: refusing to elevate '{helperPath}' because {trustReason}.");
                    return new DriverInstallResult(
                        DriverInstallStatus.HelperUntrusted,
                        $"The driver setup helper ({HelperFileName}) is not signed by the " +
                        "same publisher as NitTray, so it was not run with administrator " +
                        "rights.\n\nReinstall NitTray from an official release to repair " +
                        "the installation.");
                }

                DiagnosticLog.Write($"Driver setup: proceeding because {trustReason}.");
                DiagnosticLog.Write(
                    $"Driver setup: launching '{helperPath}' {verb} " +
                    $"{vendorId:X4} {productId:X4} (elevated).");

                return LaunchAndWait(psi, mapExitCode);
            }
        }, cancellationToken);
    }

    // Runs the already-verified helper and maps how it ended.
    private static DriverInstallResult LaunchAndWait(
        ProcessStartInfo psi,
        Func<int, DriverInstallResult> mapExitCode)
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
