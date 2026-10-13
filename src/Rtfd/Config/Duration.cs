using System.Globalization;

namespace Rtfd.Config;

/// <summary>Durations in the config: a number and a unit, such as <c>30s</c>, <c>5m</c> or <c>1h</c>.</summary>
public static class Duration
{
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (!double.TryParse(trimmed[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
        {
            return false;
        }

        TimeSpan? parsed = trimmed[^1] switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            _ => null,
        };
        if (parsed is not { } result)
        {
            return false;
        }

        value = result;
        return true;
    }
}
