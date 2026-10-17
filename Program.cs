using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using MoodTracker.Api.Data;

var builder = WebApplication.CreateBuilder(args);

// Send enums (InputType, Polarity) as readable strings instead of numbers.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddScoped<AveragesService>();
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

// ---- Medications ----
// Name is free text; dose unit and frequency come from fixed dropdown lists (see /medications/options).
// Changing dose, unit or frequency closes the current regimen row and opens a new one, so history is kept.
app.MapGet("/medications/options", () => new
{
    Units = Enum.GetNames<DoseUnit>(),
    Frequencies = Enum.GetNames<DoseFrequency>(),
    Statuses = Enum.GetNames<MedicationStatus>(),
});

app.MapGet("/medications", async (MoodDbContext db, MedicationStatus? status) =>
{
    var query = db.Medications.Include(m => m.Regimens).AsQueryable();
    if (status is not null) query = query.Where(m => m.Status == status);
    var meds = await query.OrderBy(m => m.Name).ToListAsync();
    return meds.Select(MedicationResponse.From).ToList();
});

app.MapGet("/medications/{id:int}", async (int id, MoodDbContext db) =>
{
    var med = await db.Medications.Include(m => m.Regimens).FirstOrDefaultAsync(m => m.Id == id);
    return med is null ? Results.NotFound() : Results.Ok(MedicationResponse.From(med));
});

app.MapPost("/medications", async (MedicationRequest req, MoodDbContext db) =>
{
    var error = req.Validate(requireAll: true);
    if (error is not null) return Results.BadRequest(error);
    var name = req.Name!.Trim();
    if (await db.Medications.AnyAsync(m => m.Name == name))
        return Results.Conflict($"Medication '{name}' already exists.");

    var med = new Medication { Name = name };
    med.Regimens.Add(new MedicationRegimen { Dose = req.Dose!.Value, Unit = req.Unit!.Value, Frequency = req.Frequency!.Value });
    db.Medications.Add(med);
    await db.SaveChangesAsync();
    return Results.Created($"/medications/{med.Id}", MedicationResponse.From(med));
});

// Rename and/or change dose, unit, frequency. Send only what changes; a new history row is added if dose/unit/frequency differ.
app.MapPut("/medications/{id:int}", async (int id, MedicationRequest req, MoodDbContext db) =>
{
    var error = req.Validate(requireAll: false);
    if (error is not null) return Results.BadRequest(error);
    var med = await db.Medications.Include(m => m.Regimens).FirstOrDefaultAsync(m => m.Id == id);
    if (med is null) return Results.NotFound();

    if (req.Name is not null)
    {
        var name = req.Name.Trim();
        if (await db.Medications.AnyAsync(m => m.Name == name && m.Id != id))
            return Results.Conflict($"Medication '{name}' already exists.");
        med.Name = name;
    }

    var current = med.Regimens.FirstOrDefault(r => r.EndedAt == null);
    var dose = req.Dose ?? current?.Dose ?? 0;
    var unit = req.Unit ?? current?.Unit ?? DoseUnit.Mg;
    var freq = req.Frequency ?? current?.Frequency ?? DoseFrequency.OnceDaily;
    if (current is null || current.Dose != dose || current.Unit != unit || current.Frequency != freq)
    {
        var now = DateTime.UtcNow;
        if (current is not null) current.EndedAt = now;
        med.Regimens.Add(new MedicationRegimen { Dose = dose, Unit = unit, Frequency = freq, StartedAt = now });
    }
    await db.SaveChangesAsync();
    return Results.Ok(MedicationResponse.From(med));
});

// Retire a medication (stop taking it) or bring it back. Nothing is deleted; the open regimen is closed on retire
// and reopened on restore.
app.MapPut("/medications/{id:int}/status", async (int id, MedicationStatusRequest req, MoodDbContext db) =>
{
    var med = await db.Medications.Include(m => m.Regimens).FirstOrDefaultAsync(m => m.Id == id);
    if (med is null) return Results.NotFound();
    if (med.Status == req.Status) return Results.Ok(MedicationResponse.From(med));

    var now = DateTime.UtcNow;
    med.Status = req.Status;
    if (req.Status == MedicationStatus.Retired)
    {
        med.RetiredAt = now;
        foreach (var r in med.Regimens.Where(r => r.EndedAt == null)) r.EndedAt = now;
    }
    else
    {
        med.RetiredAt = null;
        var last = med.Regimens.OrderByDescending(r => r.StartedAt).First();
        med.Regimens.Add(new MedicationRegimen { Dose = last.Dose, Unit = last.Unit, Frequency = last.Frequency, StartedAt = now });
    }
    await db.SaveChangesAsync();
    return Results.Ok(MedicationResponse.From(med));
});

// Every dose/frequency this medication has had, newest first.
app.MapGet("/medications/{id:int}/history", async (int id, MoodDbContext db) =>
{
    if (!await db.Medications.AnyAsync(m => m.Id == id)) return Results.NotFound();
    var rows = await db.MedicationRegimens.Where(r => r.MedicationId == id).ToListAsync();
    return Results.Ok(rows.OrderByDescending(r => r.StartedAt).Select(RegimenResponse.From).ToList());
});

// Daily log. GET one date (default today) or a range; PUT records/updates one medication for one date.
app.MapGet("/medication-log", async (MoodDbContext db, DateOnly? date, DateOnly? from, DateOnly? to) =>
{
    var query = db.MedicationLogs.Include(l => l.Medication).AsQueryable();
    if (from is not null || to is not null)
    {
        if (from is not null) query = query.Where(l => l.Date >= from);
        if (to is not null) query = query.Where(l => l.Date <= to);
    }
    else
    {
        var day = date ?? DateOnly.FromDateTime(DateTime.Now);
        query = query.Where(l => l.Date == day);
    }
    var logs = await query.OrderByDescending(l => l.Date).ThenBy(l => l.Medication!.Name).ToListAsync();
    return logs.Select(l => new MedicationLogResponse(l.Id, l.MedicationId, l.Medication!.Name, l.Date, l.Taken, l.RegimenId, l.Notes)).ToList();
});

app.MapPut("/medication-log/{date}/{medicationId:int}", async (DateOnly date, int medicationId, MedicationLogRequest req, MoodDbContext db) =>
{
    var med = await db.Medications.Include(m => m.Regimens).FirstOrDefaultAsync(m => m.Id == medicationId);
    if (med is null) return Results.NotFound();

    var log = await db.MedicationLogs.FirstOrDefaultAsync(l => l.MedicationId == medicationId && l.Date == date);
    if (log is null)
    {
        // Pin the regimen in effect that day (falls back to the earliest one for dates before it began).
        var at = date.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Local).ToUniversalTime();
        var regimen = med.Regimens.Where(r => r.StartedAt <= at && (r.EndedAt == null || r.EndedAt > at))
                          .OrderByDescending(r => r.StartedAt).FirstOrDefault()
                      ?? med.Regimens.OrderBy(r => r.StartedAt).FirstOrDefault();
        log = new MedicationLog { MedicationId = medicationId, Date = date, RegimenId = regimen?.Id };
        db.MedicationLogs.Add(log);
    }
    log.Taken = req.Taken;
    log.Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
    await db.SaveChangesAsync();
    return Results.Ok(new MedicationLogResponse(log.Id, med.Id, med.Name, log.Date, log.Taken, log.RegimenId, log.Notes));
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

// ---- Averages ----
// Per-factor summary of the last 7, 30 or 90 days, or 0 for all time (the Angular "averages" page).
app.MapGet("/averages/{days:int}", async (int days, AveragesService averages, CancellationToken ct) =>
    AveragesRange.Allowed.Contains(days)
        ? Results.Ok(await averages.GetAsync(days, ct))
        : Results.BadRequest($"Days must be one of {string.Join(", ", AveragesRange.Allowed)} (0 = all time)."));

// Same summary for a custom range, e.g. /averages?from=2026-10-01&to=2026-10-15 (both dates inclusive).
app.MapGet("/averages", async (DateOnly? from, DateOnly? to, AveragesService averages, CancellationToken ct) =>
{
    if (from is null || to is null) return Results.BadRequest("Both 'from' and 'to' dates are required (yyyy-MM-dd).");
    if (from > to) return Results.BadRequest("'from' must not be after 'to'.");
    return Results.Ok(await averages.GetAsync(from.Value, to.Value, ct));
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

record MedicationRequest(string? Name, double? Dose, DoseUnit? Unit, DoseFrequency? Frequency)
{
    public string? Validate(bool requireAll)
    {
        if (requireAll && (string.IsNullOrWhiteSpace(Name) || Dose is null || Unit is null || Frequency is null))
            return "Name, dose, unit and frequency are required.";
        if (Name is not null && string.IsNullOrWhiteSpace(Name)) return "Name cannot be blank.";
        if (Dose is { } d && (!double.IsFinite(d) || d <= 0)) return "Dose must be a number greater than 0.";
        return null;
    }
}
record MedicationStatusRequest(MedicationStatus Status);
record MedicationLogRequest(MedicationTaken Taken, string? Notes);
record RegimenResponse(int Id, double Dose, DoseUnit Unit, DoseFrequency Frequency, DateTime StartedAt, DateTime? EndedAt)
{
    public static RegimenResponse From(MedicationRegimen r) => new(r.Id, r.Dose, r.Unit, r.Frequency, r.StartedAt, r.EndedAt);
}
record MedicationResponse(int Id, string Name, MedicationStatus Status, DateTime CreatedAt, DateTime? RetiredAt, RegimenResponse? Current)
{
    public static MedicationResponse From(Medication m) => new(
        m.Id, m.Name, m.Status, m.CreatedAt, m.RetiredAt,
        m.Regimens.Where(r => r.EndedAt == null).Select(RegimenResponse.From).FirstOrDefault()
            ?? m.Regimens.OrderByDescending(r => r.StartedAt).Select(RegimenResponse.From).FirstOrDefault());
}
record MedicationLogResponse(int Id, int MedicationId, string Name, DateOnly Date, MedicationTaken Taken, int? RegimenId, string? Notes);
