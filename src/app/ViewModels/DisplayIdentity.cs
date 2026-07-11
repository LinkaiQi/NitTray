namespace NitTray.ViewModels;

// Formats the identity line shown beneath a display's name. Some Apple displays
// expose a real marketing serial over USB, others only an internal USB identifier,
// so we inspect the value's shape and label it "Serial: …" or "USB ID: …" accordingly.
internal static class DisplayIdentity
{
    public static string Format(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
        {
            return "USB-C";
        }

        var value = serial.Trim().ToUpperInvariant();
        return IsLikelyMarketingSerial(value)
            ? $"Serial: {value}"
            : $"USB ID: {value}";
    }

    // A real marketing serial is compact and strictly alphanumeric; anything with a
    // separator or an out-of-range length is treated as a USB ID instead.
    private static bool IsLikelyMarketingSerial(string value)
    {
        if (value.Length is < 8 or > 20)
        {
            return false;
        }

        foreach (var c in value)
        {
            bool isAlphaNumeric = c is (>= 'A' and <= 'Z') or (>= '0' and <= '9');
            if (!isAlphaNumeric)
            {
                return false;
            }
        }

        return true;
    }
}
