using NitTray.Models;

namespace NitTray.Services;

public interface IDisplayService
{
    Task<DisplayEnumerationResult> EnumerateAsync(CancellationToken cancellationToken = default);

    Task<int> ReadBrightnessPercentAsync(ConnectedDisplay display, CancellationToken cancellationToken = default);

    Task SetBrightnessPercentAsync(ConnectedDisplay display, int percent, CancellationToken cancellationToken = default);
}
