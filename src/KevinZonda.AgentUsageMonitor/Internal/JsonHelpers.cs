using System.Globalization;
using System.Text.Json;

namespace KevinZonda.AgentUsageMonitor.Internal;

internal static class JsonHelpers
{
    public static bool TryGetProperty(this JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    public static JsonElement? Property(this JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null)
            {
                return value;
            }
        }

        return null;
    }

    public static string? String(this JsonElement element, params string[] names)
    {
        var value = element.Property(names);
        return value is null ? null : String(value.Value);
    }

    public static string? String(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };

    public static double? Double(this JsonElement element, params string[] names)
    {
        var value = element.Property(names);
        if (value is null)
        {
            return null;
        }

        if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetDouble(out var number))
        {
            return number;
        }

        return double.TryParse(String(value.Value), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    public static long? Int64(this JsonElement element, params string[] names)
    {
        var value = element.Property(names);
        if (value is null)
        {
            return null;
        }

        if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt64(out var number))
        {
            return number;
        }

        return long.TryParse(String(value.Value), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    public static bool? Boolean(this JsonElement element, params string[] names)
    {
        var value = element.Property(names);
        if (value is null)
        {
            return null;
        }

        return value.Value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.Value.GetString(), out var result) => result,
            _ => null,
        };
    }

    public static DateTimeOffset? UnixDate(long? seconds) =>
        seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;

    public static double ClampPercent(double value) => Math.Clamp(value, 0, 100);
}
