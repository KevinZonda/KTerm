namespace KevinZonda.Terminal.Configuration;

internal sealed record TerminalThemePreset(
    string Name,
    string Background,
    string Foreground,
    string Cursor,
    string SelectionBackground,
    IReadOnlyList<string> AnsiColors);

internal static class TerminalThemeCatalog
{
    internal const string DefaultName = "KevinZonda Terminal Dark";
    private const string LegacyDefaultName = "KTerm Dark";

    internal static IReadOnlyList<TerminalThemePreset> All { get; } =
    [
        new(
            DefaultName,
            "#0c0f14",
            "#d8dee9",
            "#8fbcbb",
            "#3b5268",
            [
                "#1b2028", "#e06c75", "#98c379", "#e5c07b",
                "#61afef", "#c678dd", "#56b6c2", "#abb2bf",
                "#5c6370", "#e06c75", "#98c379", "#e5c07b",
                "#61afef", "#c678dd", "#56b6c2", "#ffffff"
            ]),
        new(
            "Pro",
            "#000000",
            "#f2f2f2",
            "#4d4d4d",
            "#414141",
            [
                "#000000", "#990000", "#00a600", "#999900",
                "#2009db", "#b200b2", "#00a6b2", "#bfbfbf",
                "#666666", "#e50000", "#00d900", "#e5e500",
                "#0000ff", "#e500e5", "#00e5e5", "#e5e5e5"
            ]),
        new(
            "Ubuntu",
            "#300a24",
            "#eeeeec",
            "#bbbbbb",
            "#b5d5ff",
            [
                "#2e3436", "#cc0000", "#4e9a06", "#c4a000",
                "#3465a4", "#75507b", "#06989a", "#d3d7cf",
                "#555753", "#ef2929", "#8ae234", "#fce94f",
                "#729fcf", "#ad7fa8", "#34e2e2", "#eeeeec"
            ])
    ];

    internal static TerminalThemePreset Find(string? name)
    {
        if (string.Equals(name, LegacyDefaultName, StringComparison.OrdinalIgnoreCase))
        {
            return All[0];
        }

        return All.FirstOrDefault(
            theme => string.Equals(theme.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? All[0];
    }
}
