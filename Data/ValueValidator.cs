using System.Text.Json;

namespace MoodTracker.Api.Data;

/// <summary>Checks a recorded value against its factor's input type and config.</summary>
public static class ValueValidator
{
    /// <summary>Returns an error message, or null when the value is acceptable.</summary>
    public static string? Validate(Factor factor, double value)
    {
        if (!double.IsFinite(value))
            return $"'{factor.Name}': value must be a finite number.";

        switch (factor.InputType)
        {
            case InputType.Checkbox:
                return value is 0 or 1 ? null : $"'{factor.Name}': value must be 0 or 1.";

            case InputType.Slider:
            {
                var config = Parse(factor);
                var min = config.GetProperty("min").GetDouble();
                var max = config.GetProperty("max").GetDouble();
                return value >= min && value <= max && value == Math.Floor(value)
                    ? null
                    : $"'{factor.Name}': value must be a whole number from {min} to {max}.";
            }

            case InputType.Radio:
            {
                var allowed = Parse(factor).GetProperty("options").EnumerateArray()
                    .Select(o => o.GetProperty("value").GetDouble()).ToList();
                return allowed.Contains(value)
                    ? null
                    : $"'{factor.Name}': value must be one of {string.Join(", ", allowed)}.";
            }

            default:
                return null;
        }
    }

    private static JsonElement Parse(Factor factor) =>
        JsonDocument.Parse(factor.ConfigJson ?? throw new InvalidOperationException($"Factor {factor.Id} has no config.")).RootElement;
}
