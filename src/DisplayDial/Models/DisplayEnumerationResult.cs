namespace DisplayDial.Models;

// Outcome of a single enumeration pass:
//  - Displays            : displays we can control right now (HID or WinUSB).
//  - PendingDriverSetups : displays present on the bus that need a one-time
//                          WinUSB driver install before we can control them.
public sealed record DisplayEnumerationResult(
    IReadOnlyList<StudioDisplayInfo> Displays,
    IReadOnlyList<PendingDriverSetup> PendingDriverSetups)
{
    public static readonly DisplayEnumerationResult Empty = new(
        Array.Empty<StudioDisplayInfo>(),
        Array.Empty<PendingDriverSetup>());
}
