using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using MoodTracker.Api.Data;

var builder = WebApplication.CreateBuilder(args);

// Send enums (InputType, Polarity) as readable strings instead of numbers.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddOpenApi();
builder.Services.AddDbContext<MoodDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

// Allow the local Angular dev server to call this API.
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy => policy
        .WithOrigins("http://localhost:4200")
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();

app.UseCors();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Create/upgrade the local SQLite file on startup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MoodDbContext>();
    db.Database.Migrate();
    await CatalogSeeder.SeedAsync(db);
}

// ---- Conditions ----
// The illnesses the user has chosen to track. Turning one off hides its items from the catalog;
// factors already being tracked, and their history, are left alone.
app.MapGet("/conditions", async (MoodDbContext db) =>
    await db.Conditions
        .OrderBy(c => c.SortOrder)
        .Select(c => new ConditionResponse(c.Id, c.Name, c.Enabled, db.SymptomItemConditions.Count(ic => ic.ConditionId == c.Id)))
        .ToListAsync());

app.MapPut("/conditions/{id}", async (string id, ConditionRequest req, MoodDbContext db) =>
{
    var condition = await db.Conditions.FindAsync(id.ToLower());
    if (condition is null) return Results.NotFound();
    condition.Enabled = req.Enabled;
    await db.SaveChangesAsync();
    return Results.Ok(new ConditionResponse(
        condition.Id, condition.Name, condition.Enabled,
        await db.SymptomItemConditions.CountAsync(ic => ic.ConditionId == condition.Id)));
});

// ---- Catalog ----
// Items offered to the user: only those belonging to at least one enabled condition.
IQueryable<SymptomItem> EnabledItems(MoodDbContext db) =>
    db.SymptomItems.Where(s => s.Conditions.Any(c => c.Condition!.Enabled));

// Browse the symptom catalog for the "add an item" dropdown. `tracked` tells the UI which items
// the user already has active; `factorId` is set when one exists (active or archived).
app.MapGet("/catalog", async (MoodDbContext db, string? condition, string? category, string? q) =>
{
    var query = EnabledItems(db);
    if (!string.IsNullOrWhiteSpace(condition))
        query = query.Where(s => s.Conditions.Any(c => c.ConditionId == condition.ToLower()));
    if (!string.IsNullOrWhiteSpace(category)) query = query.Where(s => s.Category == category);
    if (!string.IsNullOrWhiteSpace(q)) query = query.Where(s => s.Label.Contains(q));

    var items = await query.Include(s => s.Conditions).OrderBy(s => s.SortOrder).ToListAsync();
    var factors = await db.Factors
        .Where(f => f.SymptomItemId != null)
        .ToDictionaryAsync(f => f.SymptomItemId!);

    return Results.Ok(items.Select(s =>
    {
        factors.TryGetValue(s.Id, out var factor);
        return new CatalogItemResponse(
            s.Id, s.Label, s.Category, s.InputType, JsonSerializer.Deserialize<JsonElement>(s.ConfigJson),
            s.Polarity, s.Pole, s.SafetyKind, s.Conditions.Select(c => c.ConditionId).Order().ToArray(),
            string.IsNullOrEmpty(s.RelatedIds) ? [] : s.RelatedIds.Split(','),
            s.CrossRefNote, s.SourceNote,
            Tracked: factor is { ArchivedAt: null }, FactorId: factor?.Id);
    }));
});

app.MapGet("/catalog/categories", async (MoodDbContext db, string? condition) =>
{
    var query = EnabledItems(db);
    if (!string.IsNullOrWhiteSpace(condition))
        query = query.Where(s => s.Conditions.Any(c => c.ConditionId == condition.ToLower()));
    return await query.OrderBy(s => s.SortOrder).Select(s => s.Category).Distinct().ToListAsync();
});

// ---- Factors ----
app.MapGet("/factors", async (MoodDbContext db, bool? includeArchived) =>
    await db.Factors
        .Where(f => includeArchived == true || f.ArchivedAt == null)
        .OrderBy(f => f.Name)
        .ToListAsync());

// Start tracking a catalog item. Copies its settings into a new factor; if the user tracked it
// before and archived it, brings the old factor (and its history) back instead.
app.MapPost("/factors/from-catalog/{symptomItemId}", async (string symptomItemId, MoodDbContext db) =>
{
    var item = await db.SymptomItems.Include(s => s.Conditions).ThenInclude(c => c.Condition)
        .FirstOrDefaultAsync(s => s.Id == symptomItemId);
    if (item is null) return Results.NotFound();
    if (!item.Conditions.Any(c => c.Condition!.Enabled))
        return Results.BadRequest("This item belongs only to conditions that are turned off. Enable one in /conditions first.");

    var existing = await db.Factors.FirstOrDefaultAsync(f => f.SymptomItemId == symptomItemId);
    if (existing is { ArchivedAt: null })
        return Results.Conflict($"'{existing.Name}' is already being tracked.");

    var factor = await StartTrackingAsync(db, item);
    return existing is null ? Results.Created($"/factors/{factor.Id}", factor) : Results.Ok(factor);
});

// ---- Tracking page ----
// What the user has currently selected to track, in the order and shape the tracking page renders it.
// Catalog items come first (catalog order), then the user's custom factors by name.
app.MapGet("/tracking", async (MoodDbContext db) =>
{
    var factors = await db.Factors.Where(f => f.ArchivedAt == null).ToListAsync();
    var ids = factors.Where(f => f.SymptomItemId != null).Select(f => f.SymptomItemId!).ToList();
    var items = await db.SymptomItems.Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id);

    return Results.Ok(factors
        .Select(f =>
        {
            SymptomItem? item = null;
            if (f.SymptomItemId is not null) items.TryGetValue(f.SymptomItemId, out item);
            return new TrackedFactorResponse(
                f.Id, f.Name, f.Unit, f.SymptomItemId, item?.Category ?? "Custom",
                f.InputType, f.ConfigJson is null ? null : JsonSerializer.Deserialize<JsonElement>(f.ConfigJson),
                f.Polarity, item?.Pole, f.SafetyKind, item?.SortOrder ?? int.MaxValue);
        })
        .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
        .ToList());
});

// Replace the selected catalog items in one call: anything listed starts being tracked (or comes back
// from the archive), and any other catalog item currently tracked is archived. Custom factors are left alone.
app.MapPut("/tracking", async (TrackingRequest req, MoodDbContext db) =>
{
    var wanted = (req.SymptomItemIds ?? []).Distinct().ToList();
    var items = await db.SymptomItems.Include(s => s.Conditions).ThenInclude(c => c.Condition)
        .Where(s => wanted.Contains(s.Id)).ToListAsync();

    var unknown = wanted.Except(items.Select(i => i.Id)).ToList();
    if (unknown.Count > 0)
        return Results.BadRequest($"Unknown catalog items: {string.Join(", ", unknown)}.");
    var disabled = items.Where(i => !i.Conditions.Any(c => c.Condition!.Enabled)).Select(i => i.Id).ToList();
    if (disabled.Count > 0)
        return Results.BadRequest($"These items belong only to conditions that are turned off: {string.Join(", ", disabled)}.");

    await using var tx = await db.Database.BeginTransactionAsync();
    var catalogFactors = await db.Factors.Where(f => f.SymptomItemId != null).ToListAsync();
    foreach (var f in catalogFactors.Where(f => f.ArchivedAt is null && !wanted.Contains(f.SymptomItemId!)))
        f.ArchivedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();

    foreach (var item in items.Where(i => !catalogFactors.Any(f => f.SymptomItemId == i.Id && f.ArchivedAt is null)))
        await StartTrackingAsync(db, item);
    await tx.CommitAsync();

    return Results.NoContent();
});

// Stop tracking without losing history. Past entries keep their values.
app.MapPost("/factors/{id:int}/archive", async (int id, MoodDbContext db) =>
{
    var factor = await db.Factors.FindAsync(id);
    if (factor is null) return Results.NotFound();
    factor.ArchivedAt ??= DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(factor);
});

app.MapPost("/factors/{id:int}/unarchive", async (int id, MoodDbContext db) =>
{
    var factor = await db.Factors.FindAsync(id);
    if (factor is null) return Results.NotFound();
    factor.ArchivedAt = null;
    await db.SaveChangesAsync();
    return Results.Ok(factor);
});

app.MapPost("/factors", async (FactorRequest req, MoodDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest("Name is required.");
    if (await db.Factors.AnyAsync(f => f.Name == req.Name))
        return Results.Conflict($"Factor '{req.Name}' already exists.");

    var factor = new Factor { Name = req.Name.Trim(), Unit = req.Unit };
    db.Factors.Add(factor);
    await db.SaveChangesAsync();
    return Results.Created($"/factors/{factor.Id}", factor);
});

app.MapPut("/factors/{id:int}", async (int id, FactorRequest req, MoodDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest("Name is required.");
    var factor = await db.Factors.FindAsync(id);
    if (factor is null) return Results.NotFound();

    var name = req.Name.Trim();
    if (await db.Factors.AnyAsync(f => f.Name == name && f.Id != id))
        return Results.Conflict($"Factor '{name}' already exists.");

    factor.Name = name;
    factor.Unit = string.IsNullOrWhiteSpace(req.Unit) ? null : req.Unit.Trim();
    await db.SaveChangesAsync();
    return Results.Ok(factor);
});

// Also removes this factor's recorded values from every entry (cascade).
app.MapDelete("/factors/{id:int}", async (int id, MoodDbContext db) =>
{
    var deleted = await db.Factors.Where(f => f.Id == id).ExecuteDeleteAsync();
    return deleted == 0 ? Results.NotFound() : Results.NoContent();
});

// ---- Entries ----
app.MapGet("/entries", async (MoodDbContext db, DateTime? from, DateTime? to) =>
{
    var query = db.Entries.AsQueryable();
    if (from is not null) query = query.Where(e => e.Timestamp >= from);
    if (to is not null) query = query.Where(e => e.Timestamp <= to);

    var entries = await query
        .OrderBy(e => e.Timestamp)
        .Select(e => new EntryResponse(
            e.Id, e.Timestamp, e.Mood, e.Notes,
            e.Factors.Select(f => new FactorValueResponse(f.FactorId, f.Factor!.Name, f.Factor.Unit, f.Value)).ToList()))
        .ToListAsync();
    return Results.Ok(entries);
});

app.MapPost("/entries", async (EntryRequest req, MoodDbContext db) =>
{
    if (req.Mood is < 0 or > 5)
        return Results.BadRequest("Mood must be between 0 and 5.");

    var factorValues = req.Factors ?? [];
    var factorIds = factorValues.Select(f => f.FactorId).Distinct().ToList();
    if (factorIds.Count != factorValues.Count)
        return Results.BadRequest("Each factor can only appear once per entry.");
    var factors = await db.Factors.Where(f => factorIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id);
    if (factors.Count != factorIds.Count)
        return Results.BadRequest("One or more factorIds do not exist.");

    var errors = new List<string>();
    foreach (var fv in factorValues)
    {
        var factor = factors[fv.FactorId];
        if (factor.ArchivedAt is not null)
            errors.Add($"'{factor.Name}' is archived. Unarchive it to record new values.");
        else if (ValueValidator.Validate(factor, fv.Value) is { } error)
            errors.Add(error);
    }
    if (errors.Count > 0)
        return Results.BadRequest(errors);

    // A positive answer on a safety item should surface an alert (crisis resources, or a prompt to contact the care team).
    var alerts = factorValues
        .Where(fv => fv.Value > 0 && factors[fv.FactorId].SafetyKind is not null)
        .Select(fv => factors[fv.FactorId].SafetyKind!.Value)
        .Distinct()
        .Select(SafetyAlert.For)
        .ToList();

    var entry = new Entry
    {
        // Treat client-supplied times as UTC so everything is stored consistently.
        Timestamp = req.Timestamp?.ToUniversalTime() ?? DateTime.UtcNow,
        Mood = req.Mood,
        Notes = req.Notes,
        Factors = factorValues.Select(f => new EntryFactor { FactorId = f.FactorId, Value = f.Value }).ToList()
    };
    db.Entries.Add(entry);
    await db.SaveChangesAsync();
    return Results.Created($"/entries/{entry.Id}", new { entry.Id, Alerts = alerts });
});

app.MapDelete("/entries/{id:int}", async (int id, MoodDbContext db) =>
{
    var deleted = await db.Entries.Where(e => e.Id == id).ExecuteDeleteAsync();
    return deleted == 0 ? Results.NotFound() : Results.NoContent();
});

app.Run();

// Starts tracking a catalog item: restores its archived factor (history intact) or creates a new one.
// Saves immediately so the name-collision check below sees factors created earlier in the same batch.
static async Task<Factor> StartTrackingAsync(MoodDbContext db, SymptomItem item)
{
    var existing = await db.Factors.FirstOrDefaultAsync(f => f.SymptomItemId == item.Id);
    if (existing is not null)
    {
        existing.ArchivedAt = null;
        await db.SaveChangesAsync();
        return existing;
    }

    // Names are unique, and a few labels (e.g. "Anxiety or worry") appear in more than one condition's list.
    var name = item.Label;
    if (await db.Factors.AnyAsync(f => f.Name == name))
    {
        var owner = item.Conditions.Select(c => c.Condition!).OrderBy(c => c.SortOrder).First();
        name = $"{item.Label} ({owner.Name})";
    }

    var factor = new Factor
    {
        Name = name,
        SymptomItemId = item.Id,
        InputType = item.InputType,
        ConfigJson = item.ConfigJson,
        Polarity = item.Polarity,
        SafetyKind = item.SafetyKind,
    };
    db.Factors.Add(factor);
    await db.SaveChangesAsync();
    return factor;
}

record TrackingRequest(List<string>? SymptomItemIds);
record TrackedFactorResponse(
    int FactorId, string Name, string? Unit, string? SymptomItemId, string Category, InputType InputType,
    JsonElement? Config, Polarity Polarity, string? Pole, SafetyKind? SafetyKind, int SortOrder);
record ConditionRequest(bool Enabled);
record ConditionResponse(string Id, string Name, bool Enabled, int ItemCount);
record FactorRequest(string Name, string? Unit);
record FactorValueRequest(int FactorId, double Value);
record EntryRequest(int Mood, DateTime? Timestamp, string? Notes, List<FactorValueRequest>? Factors);
record FactorValueResponse(int FactorId, string Name, string? Unit, double Value);
/// <summary>An alert for the front end to show. <c>Phone</c> is set only when there is a number to call or text.</summary>
record SafetyAlert(SafetyKind Kind, string Message, string? Phone)
{
    public static SafetyAlert For(SafetyKind kind) => kind switch
    {
        SafetyKind.SelfHarm => new(kind,
            "If you're thinking about harming yourself, you're not alone. In the US, call or text 988 (Suicide & Crisis Lifeline), or contact your care team.",
            "988"),
        SafetyKind.UnusualExperiences => new(kind,
            "Unusual beliefs or experiences can be a sign of a severe episode. Consider contacting your care team soon.",
            null),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
record CatalogItemResponse(
    string Id, string Label, string Category, InputType InputType, JsonElement Config,
    Polarity Polarity, string? Pole, SafetyKind? SafetyKind, string[] Conditions, string[] RelatedIds,
    string? CrossRefNote, string? SourceNote, bool Tracked, int? FactorId);
record EntryResponse(int Id, DateTime Timestamp, int Mood, string? Notes, List<FactorValueResponse> Factors);
