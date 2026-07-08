namespace NitTray.Models.Displays;

// Registry of every Apple display NitTray knows about — names, USB identity, and
// (for WinUSB models) brightness protocol. To add one, create a DisplayModel file
// and add it to All below.
public static class DisplayCatalog
{
    // Every Apple USB display shares this vendor id.
    public const ushort AppleVendorId = 0x05AC;

    public static readonly IReadOnlyList<DisplayModel> All = new[]
    {
        StudioDisplay.Model,
        StudioDisplayXdr.Model,
        StudioDisplay2ndGen.Model,
        ProDisplayXdr.Model,
    };

    // Look up a known model by USB product id, or null if unrecognised.
    public static DisplayModel? TryGet(ushort productId)
    {
        foreach (var model in All)
        {
            if (model.ProductId == productId)
            {
                return model;
            }
        }
        return null;
    }

    // Curated marketing name for a known product id, or null if unrecognised.
    public static string? GetName(ushort productId) => TryGet(productId)?.Name;
}
