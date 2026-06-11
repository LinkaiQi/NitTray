using DisplayDial.Models;

namespace DisplayDial.Services;

public interface IDisplayService
{
    Task<IReadOnlyList<StudioDisplayInfo>> EnumerateAsync(CancellationToken cancellationToken = default);

    Task<int> ReadBrightnessPercentAsync(StudioDisplayInfo display, CancellationToken cancellationToken = default);

    Task SetBrightnessPercentAsync(StudioDisplayInfo display, int percent, CancellationToken cancellationToken = default);
}
