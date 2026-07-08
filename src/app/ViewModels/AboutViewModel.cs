using System.Reflection;

namespace NitTray.ViewModels;

// Backing data for the About window: version, supported displays, links, and legal text.
public sealed class AboutViewModel
{
    public string AppName => "NitTray";

    public string VersionText { get; }

    public string Tagline => "Adjust brightness for supported Apple displays.";

    public string OpenSourceText => "Free and open source under the GPL-3.0 License.";

    public string FooterText => "© 2026 NitTray";

    public string LegalText =>
        "NitTray is an independent project and is not affiliated with, endorsed by, or " +
        "sponsored by Apple Inc. Apple, Studio Display, Studio Display XDR, and " +
        "Pro Display XDR are trademarks of Apple Inc.";

    // Hand-curated for the About list: the two Studio Display generations share one
    // line, so this is maintained here rather than generated from DisplayCatalog.
    public IReadOnlyList<string> SupportedDisplays { get; } = new[]
    {
        "Studio Display (1st and 2nd Gen)",
        "Studio Display XDR",
        "Pro Display XDR",
    };

    public string RepositoryUrl => "https://github.com/LinkaiQi/NitTray";
    public string ReleasesUrl => "https://github.com/LinkaiQi/NitTray/releases";
    public string DonateUrl => "https://buymeacoffee.com/nittray";

    public AboutViewModel()
    {
        VersionText = $"Version {ReadVersion()}";
    }

    // Use the informational version, stripping any "+<build metadata>" suffix.
    private static string ReadVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0-local";
    }
}
