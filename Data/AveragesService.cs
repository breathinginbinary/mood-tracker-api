using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace MoodTracker.Api.Data;

/// <summary>How far back an averages request looks. <see cref="AllTime"/> (0) means every entry.</summary>
public static class AveragesRange
{
    public const int AllTime = 0;
    public static readonly int[] Allowed = [7, 30, 90, AllTime];
}

/// <summary>How a factor's values are summarised, which follows its input type.</summary>
public enum AverageKind
{
    /// <summary>Slider: <c>Average</c> within <c>Min</c>..<c>Max</c>, with a trend.</summary>
    Scale,
    /// <summary>Checkbox: <c>Count</c> of <c>Total</c> days checked.</summary>
    Checkbox,
    /// <summary>Radio: the <c>Mode</c> answer, given on <c>Count</c> of <c>Total</c> days.</summary>
    Choice,
    /// <summary>Free number (custom factors): <c>Average</c> only.</summary>
    Number,
}

public record FactorAverage(
    int FactorId, string Name, string? Unit, string Category, string[] Conditions, AverageKind Kind,
    /// <summary>Days in range with a value for this factor.</summary>
    int Total,
    double? Average, double? Min, double? Max,
    /// <summary>0-100 bar fill, or null when a bar doesn't apply.</summary>
    double? Percent,
    int? Count, string? Mode,
    /// <summary>"up" or "down" when the later half of the range differs from the earlier half; otherwise null.</summary>
    string? Trend);

/// <param name="Days">The "last N days" range that was requested (0 = all time); null for a custom date range.</param>
/// <param name="From">First day of a custom date range; null otherwise.</param>
/// <param name="To">Last day (inclusive) of a custom date range; null otherwise.</param>
/// <param name="DayCount">Distinct days with at least one entry in range.</param>
public record AveragesResponse(int? Days, DateOnly? From, DateOnly? To, int DayCount, List<FactorAverage> Factors);

/// <summary>
/// Summarises tracked factors over a window of days. Each factor contributes one value per day (the day's
/// latest entry), so a day with several check-ins is not weighted more than one with a single check-in.
/// Days are UTC calendar dates, matching how timestamps are stored.
/// </summary>
public class AveragesService(MoodDbContext db)
{
    /// <summary>A trend needs at least this many days of data.</summary>
    private const int MinPointsForTrend = 4;
    /// <summary>Minimum difference between the later and earlier halves' means to call a trend.</summary>
    private const double TrendThreshold = 0.5;

    public async Task<AveragesResponse> GetAsync(int days, CancellationToken ct = default)
    {
        // "Last N days" includes today, so the window starts N-1 days back.
        DateTime? start = days == AveragesRange.AllTime ? null : DateTime.UtcNow.Date.AddDays(-(days - 1));
        var (factors, dayCount) = await SummarizeAsync(start, null, ct);
        return new AveragesResponse(days, null, null, dayCount, factors);
    }

    /// <summary>Averages for every day from <paramref name="from"/> to <paramref name="to"/>, both inclusive.</summary>
    public async Task<AveragesResponse> GetAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var (factors, dayCount) = await SummarizeAsync(
            from.ToDateTime(TimeOnly.MinValue), to.AddDays(1).ToDateTime(TimeOnly.MinValue), ct);
        return new AveragesResponse(null, from, to, dayCount, factors);
    }

    /// <param name="start">Inclusive lower bound, or null for no lower bound.</param>
    /// <param name="endExclusive">Exclusive upper bound, or null for no upper bound.</param>
    private async Task<(List<FactorAverage> Factors, int DayCount)> SummarizeAsync(
        DateTime? start, DateTime? endExclusive, CancellationToken ct)
    {
        var query = db.Entries.AsQueryable();
        if (start is not null) query = query.Where(e => e.Timestamp >= start);
        if (endExclusive is not null) query = query.Where(e => e.Timestamp < endExclusive);

        var entries = await query
            .Include(e => e.Factors).ThenInclude(f => f.Factor)
            .OrderBy(e => e.Timestamp)
            .ToListAsync(ct);

        var symptomIds = entries.SelectMany(e => e.Factors).Select(f => f.Factor!.SymptomItemId)
            .OfType<string>().Distinct().ToList();
        var items = await db.SymptomItems.Include(s => s.Conditions)
            .Where(s => symptomIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);

        // factor -> (date -> value), keeping the latest entry of each day (entries are oldest first).
        var byFactor = new Dictionary<int, (Factor Factor, SortedDictionary<DateTime, double> Days)>();
        foreach (var entry in entries)
            foreach (var ef in entry.Factors)
            {
                if (!byFactor.TryGetValue(ef.FactorId, out var slot))
                    byFactor[ef.FactorId] = slot = (ef.Factor!, []);
                slot.Days[entry.Timestamp.Date] = ef.Value;
            }

        var factors = byFactor.Values
            .Select(slot =>
            {
                SymptomItem? item = null;
                if (slot.Factor.SymptomItemId is not null) items.TryGetValue(slot.Factor.SymptomItemId, out item);
                return Summarize(slot.Factor, item, slot.Days.Values.ToList());
            })
            .OfType<FactorAverage>()
            .OrderBy(f => f.Category).ThenBy(f => f.Name)
            .ToList();

        return (factors, entries.Select(e => e.Timestamp.Date).Distinct().Count());
    }

    private static FactorAverage? Summarize(Factor factor, SymptomItem? item, List<double> values)
    {
        var category = item?.Category ?? "Custom";
        var conditions = item?.Conditions.Select(c => c.ConditionId).Order().ToArray() ?? [];
        FactorAverage Make(AverageKind kind, double? average = null, double? min = null, double? max = null,
            double? percent = null, int? count = null, string? mode = null, string? trend = null) =>
            new(factor.Id, factor.Name, factor.Unit, category, conditions, kind, values.Count,
                average, min, max, percent, count, mode, trend);

        switch (factor.InputType)
        {
            case InputType.Slider:
            {
                var config = JsonDocument.Parse(factor.ConfigJson!).RootElement;
                var min = config.GetProperty("min").GetDouble();
                var max = config.GetProperty("max").GetDouble();
                var avg = values.Average();
                var percent = max > min ? (avg - min) / (max - min) * 100 : 0;
                return Make(AverageKind.Scale, avg, min, max, percent, trend: TrendOf(values));
            }

            case InputType.Checkbox:
            {
                var count = values.Count(v => v != 0);
                return Make(AverageKind.Checkbox, percent: (double)count / values.Count * 100, count: count);
            }

            case InputType.Radio:
            {
                var labels = JsonDocument.Parse(factor.ConfigJson!).RootElement.GetProperty("options").EnumerateArray()
                    .ToDictionary(o => o.GetProperty("value").GetDouble(), o => o.GetProperty("label").GetString()!);
                // Ties go to the answer given first.
                var (answer, count) = values
                    .Select((v, i) => (Value: v, Index: i))
                    .GroupBy(x => x.Value)
                    .Select(g => (Value: g.Key, Count: g.Count(), First: g.Min(x => x.Index)))
                    .OrderByDescending(g => g.Count).ThenBy(g => g.First)
                    .Select(g => (g.Value, g.Count))
                    .First();
                return Make(AverageKind.Choice, count: count, mode: labels.GetValueOrDefault(answer, answer.ToString()));
            }

            default:
                return Make(AverageKind.Number, values.Average());
        }
    }

    /// <summary>Compares the later half of the values with the earlier half (oldest first).</summary>
    private static string? TrendOf(List<double> values)
    {
        if (values.Count < MinPointsForTrend) return null;
        var half = values.Count / 2;
        var change = values.TakeLast(half).Average() - values.Take(half).Average();
        if (change >= TrendThreshold) return "up";
        if (change <= -TrendThreshold) return "down";
        return null;
    }
}
