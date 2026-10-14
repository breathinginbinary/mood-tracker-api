using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace MoodTracker.Api.Data;

/// <summary>
/// Loads Data/conditions.json and Data/symptom_items.json into the database. Safe to run on every startup:
/// rows are upserted by ID, and a condition's user-controlled Enabled flag is only set when it is first created.
/// </summary>
public static class CatalogSeeder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task SeedAsync(MoodDbContext db, string? dataDir = null)
    {
        dataDir ??= Path.Combine(AppContext.BaseDirectory, "Data");
        await SeedConditionsAsync(db, Path.Combine(dataDir, "conditions.json"));
        await SeedItemsAsync(db, Path.Combine(dataDir, "symptom_items.json"));
    }

    private static async Task SeedConditionsAsync(MoodDbContext db, string path)
    {
        var seed = await ReadAsync<SeedCondition>(path);
        var existing = await db.Conditions.ToDictionaryAsync(c => c.Id);
        foreach (var item in seed)
        {
            if (!existing.TryGetValue(item.Id, out var row))
            {
                // Only a new condition takes its default; after that the user's switch wins.
                row = new Condition { Id = item.Id, Name = item.Name, Enabled = item.EnabledByDefault };
                db.Conditions.Add(row);
            }
            row.Name = item.Name;
            row.SortOrder = item.SortOrder;
        }
        await db.SaveChangesAsync();
    }

    private static async Task SeedItemsAsync(MoodDbContext db, string path)
    {
        var seed = await ReadAsync<SeedItem>(path);
        var conditionIds = await db.Conditions.Select(c => c.Id).ToHashSetAsync();
        var existing = await db.SymptomItems.Include(s => s.Conditions).ToDictionaryAsync(s => s.Id);

        foreach (var item in seed)
        {
            var unknown = item.Conditions.FirstOrDefault(c => !conditionIds.Contains(c));
            if (unknown is not null)
                throw new InvalidOperationException($"Item {item.Id} references unknown condition '{unknown}'.");

            if (!existing.TryGetValue(item.Id, out var row))
            {
                row = new SymptomItem { Id = item.Id, Label = item.Label, Category = item.Category, ConfigJson = "{}" };
                db.SymptomItems.Add(row);
            }
            row.Label = item.Label;
            row.Category = item.Category;
            row.InputType = item.InputType;
            row.ConfigJson = JsonSerializer.Serialize(item.Config);
            row.Polarity = item.Polarity;
            row.Pole = item.Pole;
            row.SafetyKind = item.SafetyKind;
            row.SortOrder = item.SortOrder;
            row.RelatedIds = item.RelatedIds is { Length: > 0 } ? string.Join(',', item.RelatedIds) : null;
            row.CrossRefNote = item.CrossRefNote;
            row.SourceNote = item.SourceNote;

            // Make the item's condition links match the seed exactly.
            row.Conditions.RemoveAll(c => !item.Conditions.Contains(c.ConditionId));
            foreach (var id in item.Conditions.Where(id => row.Conditions.All(c => c.ConditionId != id)))
                row.Conditions.Add(new SymptomItemCondition { SymptomItemId = item.Id, ConditionId = id });
        }
        await db.SaveChangesAsync();
    }

    private static async Task<List<T>> ReadAsync<T>(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<T>>(stream, Json)
            ?? throw new InvalidOperationException($"{path} is empty.");
    }

    private record SeedCondition(string Id, string Name, bool EnabledByDefault, int SortOrder);

    private record SeedItem(
        string Id, string Label, string Category, InputType InputType, JsonElement Config,
        Polarity Polarity, string? Pole, SafetyKind? SafetyKind, string[] Conditions, int SortOrder,
        string[]? RelatedIds, string? CrossRefNote, string? SourceNote);
}
