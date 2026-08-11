namespace KevinZonda.KTerm.Configuration;

internal sealed record AppSettings
{
    internal const string DefaultFontFamily =
        "Cascadia Mono, Cascadia Code, Consolas, monospace";
    internal const double DefaultFontSize = 14;
    internal const double DefaultLineHeight = 1.12;

    public FontSettings Font { get; init; } = new();

    public ThemeSettings Theme { get; init; } = new();

    public ShellSettings Shell { get; init; } = new();

    internal static AppSettings Normalize(AppSettings? settings)
    {
        var font = settings?.Font ?? new FontSettings();
        var family = font.Family?.Trim();
        if (string.IsNullOrEmpty(family) || family.Length > 256)
        {
            family = DefaultFontFamily;
        }

        var theme = TerminalThemeCatalog.Find(settings?.Theme?.Name);
        return new AppSettings
        {
            Font = new FontSettings
            {
                Family = family,
                Size = double.IsFinite(font.Size)
                    ? Math.Clamp(font.Size, 8, 72)
                    : DefaultFontSize,
                LineHeight = double.IsFinite(font.LineHeight)
                    ? Math.Clamp(font.LineHeight, 0.8, 2)
                    : DefaultLineHeight
            },
            Theme = new ThemeSettings
            {
                Name = theme.Name
            },
            Shell = ShellSettings.Normalize(settings?.Shell)
        };
    }
}

internal sealed record FontSettings
{
    public string Family { get; init; } = AppSettings.DefaultFontFamily;

    public double Size { get; init; } = AppSettings.DefaultFontSize;

    public double LineHeight { get; init; } = AppSettings.DefaultLineHeight;
}

internal sealed record ThemeSettings
{
    public string Name { get; init; } = TerminalThemeCatalog.DefaultName;
}

internal sealed record ShellSettings
{
    public string Profile { get; init; } = ShellProfileCatalog.AutoId;

    public string? Executable { get; init; }

    public string? Arguments { get; init; }

    public string Msys2Environment { get; init; } = ShellProfileCatalog.DefaultMsys2Environment;

    public bool InheritWindowsPath { get; init; } = true;

    internal static ShellSettings Normalize(ShellSettings? settings)
    {
        var profile = ShellProfileCatalog.Find(settings?.Profile);
        var executable = NormalizeText(settings?.Executable, 1_024);
        var arguments = NormalizeText(settings?.Arguments, 4_096, preserveEmpty: true);
        return new ShellSettings
        {
            Profile = profile.Id,
            Executable = executable,
            Arguments = arguments,
            Msys2Environment = ShellProfileCatalog.NormalizeMsys2Environment(
                settings?.Msys2Environment),
            InheritWindowsPath = settings?.InheritWindowsPath ?? true
        };
    }

    private static string? NormalizeText(string? value, int maximumLength, bool preserveEmpty = false)
    {
        if (value is null)
        {
            return null;
        }

        value = value.Trim();
        if (value.Length > maximumLength)
        {
            return null;
        }

        return value.Length == 0 && !preserveEmpty ? null : value;
    }
}
