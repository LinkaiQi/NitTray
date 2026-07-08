namespace NitTray.Models.Displays;

// Central registry of every Apple display NitTray knows about. Enumeration and
// driver code consult this for friendly names, USB identity, and (for WinUSB
// models) the brightness protocol.
//
// To add support for a new Apple display: create a DisplayModel file in this
// folder and add it to All below. Nothing else needs to change.
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
