using DisplayDial.Models;

namespace DisplayDial.Services;

// Installs the WinUSB function driver for an Apple display that Windows' generic
// HID driver refuses to bind (the Pro Display XDR). The work itself is performed
// by an elevated native helper; this abstraction exists so the view-model can be
// unit-tested against a fake.
public interface IDriverInstallService
{
    Task<DriverInstallResult> InstallAsync(
        PendingDriverSetup target,
        CancellationToken cancellationToken = default);
}
