namespace DisplayDial.Models;

public sealed record StudioDisplayInfo(
    string DevicePath,
    string ProductName,
    string? SerialNumber,
    ushort ProductId);
