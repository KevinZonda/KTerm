namespace KevinZonda.KTerm.Configuration;

internal sealed record AppSettings
{
    internal const string DefaultFontFamily =
        "Cascadia Mono, Cascadia Code, Consolas, monospace";
    internal const double DefaultFontSize = 14;
    internal const double DefaultLineHeight = 1.12;

    public FontSettings Font { get; init; } = new();

    public ThemeSettings Theme { get; init; } = new();

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
            }
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
