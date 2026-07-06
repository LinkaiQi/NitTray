using System.Reflection;
using NitTray.Models.Displays;

namespace NitTray.ViewModels;

// Backing data for the About window. Pure, read-only view state: the app version
// (read from assembly metadata so it tracks the csproj <Version>), the list of
// supported displays (sourced from DisplayCatalog so it stays in sync as models
// are added), the resource/donation links, and the Apple trademark disclaimer.
public sealed class AboutViewModel
{
    public string AppName => "NitTray";

    public string VersionText { get; }

    public string Tagline { get; }

    public string FooterText { get; }

    public string LegalText =>
        "NitTray is an independent app and is not affiliated with, endorsed by, or " +
        "sponsored by Apple Inc. Apple, Studio Display, Studio Display XDR, and " +
        "Pro Display XDR are trademarks of Apple Inc.";

    // Resource + support links. Kept here (not hard-coded in XAML) so they have a
    // single source of truth and can be reused/tested.
    public string RepositoryUrl => "https://github.com/LinkaiQi/NitTray";
    public string ReleasesUrl => "https://github.com/LinkaiQi/NitTray/releases";
    public string IssuesUrl => "https://github.com/LinkaiQi/NitTray/issues";
    public string LicenseUrl => "https://github.com/LinkaiQi/NitTray/blob/main/LICENSE";
    public string ThirdPartyNoticesUrl => "https://github.com/LinkaiQi/NitTray/blob/main/THIRD-PARTY-NOTICES.md";
    public string DonateUrl => "https://buymeacoffee.com/nittray";

    public IReadOnlyList<SupportedDisplayItem> SupportedDisplays { get; }

    public AboutViewModel()
    {
        var assembly = Assembly.GetExecutingAssembly();

        VersionText = $"Version {ReadVersion(assembly)}";
        Tagline = assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description
                  ?? "Control Apple display brightness from Windows.";

        var copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
                        ?? "Copyright © 2026 Linkai Qi";
        FooterText = $"{copyright} · Built with .NET & WPF-UI";

        SupportedDisplays = DisplayCatalog.All
            .Select(model => new SupportedDisplayItem(model.Name, $"0x{model.ProductId:X4}"))
            .ToList();
    }

    // Prefer the informational version ("0.1.0") over the padded assembly version
    // ("0.1.0.0"); strip any "+<build metadata>" suffix if present.
    private static string ReadVersion(Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    }
}

// One supported display row: friendly name plus its USB product id (e.g. "0x9243").
public sealed record SupportedDisplayItem(string Name, string ProductId);
