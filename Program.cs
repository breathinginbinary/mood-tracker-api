using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MoodTracker.Api.Auth;
using MoodTracker.Api.Data;

var builder = WebApplication.CreateBuilder(args);

// Send enums (InputType, Polarity) as readable strings instead of numbers.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddScoped<AveragesService>();
builder.Services.AddOpenApi();

// ---- Authentication ----
// Users register with email + password (ASP.NET Core Identity) and call the API with a JWT bearer token.
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (jwt.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be at least 32 characters. Set it with 'dotnet user-secrets set Jwt:Key <value>' or the Jwt__Key environment variable.");

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddIdentityCore<ApplicationUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 10;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<MoodDbContext>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new()
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = jwt.SigningKey,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    });
// Every endpoint needs a signed-in user unless it opts out with AllowAnonymous.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
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
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapAuth();

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
{
    var enabled = await EnabledConditionIdsAsync(db);
    return await db.Conditions
        .OrderBy(c => c.SortOrder)
        .Select(c => new ConditionResponse(c.Id, c.Name, enabled.Contains(c.Id), db.SymptomItemConditions.Count(ic => ic.ConditionId == c.Id)))
        .ToListAsync();
});

app.MapPut("/conditions/{id}", async (string id, ConditionRequest req, MoodDbContext db, ICurrentUser user) =>
{
    var condition = await db.Conditions.FindAsync(id.ToLower());
    if (condition is null) return Results.NotFound();
    var choice = await db.UserConditions.FirstOrDefaultAsync(c => c.ConditionId == condition.Id);
    if (choice is null) db.UserConditions.Add(new UserCondition { UserId = user.Id, ConditionId = condition.Id, Enabled = req.Enabled });
    else choice.Enabled = req.Enabled;
    await db.SaveChangesAsync();
    return Results.Ok(new ConditionResponse(
        condition.Id, condition.Name, req.Enabled,
        await db.SymptomItemConditions.CountAsync(ic => ic.ConditionId == condition.Id)));
});

// ---- Catalog ----
// Items offered to the user: only those belonging to at least one enabled condition.
async Task<IQueryable<SymptomItem>> EnabledItemsAsync(MoodDbContext db)
{
    var enabled = await EnabledConditionIdsAsync(db);
    return db.SymptomItems.Where(s => s.Conditions.Any(c => enabled.Contains(c.ConditionId)));
}

// Browse the symptom catalog for the "add an item" dropdown. `tracked` tells the UI which items
// the user already has active; `factorId` is set when one exists (active or archived).
app.MapGet("/catalog", async (MoodDbContext db, string? condition, string? category, string? q) =>
{
    var query = await EnabledItemsAsync(db);
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
    var query = await EnabledItemsAsync(db);
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
    var enabledConditions = await EnabledConditionIdsAsync(db);
    if (!item.Conditions.Any(c => enabledConditions.Contains(c.ConditionId)))
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
    var enabledConditions = await EnabledConditionIdsAsync(db);
    var disabled = items.Where(i => !i.Conditions.Any(c => enabledConditions.Contains(c.ConditionId))).Select(i => i.Id).ToList();
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
    var factor = await db.Factors.FirstOrDefaultAsync(f => f.Id == id);
    if (factor is null) return Results.NotFound();
    factor.ArchivedAt ??= DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(factor);
});

app.MapPost("/factors/{id:int}/unarchive", async (int id, MoodDbContext db) =>
{
    var factor = await db.Factors.FirstOrDefaultAsync(f => f.Id == id);
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
    var factor = await db.Factors.FirstOrDefaultAsync(f => f.Id == id);
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
    var (factors, invalid) = await CheckValuesAsync(db, factorValues, f => f.ArchivedAt is null);
    if (invalid is not null) return invalid;
    var alerts = AlertsFor(factorValues, factors);

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

// ---- Days ----
// A calendar day as one set of values, for the history and tracking pages. Dates are UTC calendar dates
// (what the front end sends as the user's local date), the same days the averages use. A day shows the
// latest value of each factor recorded on it; saving a day replaces everything recorded on it.
app.MapGet("/days", async (MoodDbContext db, DateOnly? from, DateOnly? to) =>
{
    var query = db.Entries.AsQueryable();
    if (from is { } f) { var start = f.ToDateTime(TimeOnly.MinValue); query = query.Where(e => e.Timestamp >= start); }
    if (to is { } t) { var end = t.AddDays(1).ToDateTime(TimeOnly.MinValue); query = query.Where(e => e.Timestamp < end); }

    var entries = await query
        .Include(e => e.Factors).ThenInclude(ef => ef.Factor)
        .OrderBy(e => e.Timestamp)
        .ToListAsync();
    var categories = await CategoriesAsync(db, entries.SelectMany(e => e.Factors).Select(ef => ef.Factor!));

    return Results.Ok(entries
        .GroupBy(e => DateOnly.FromDateTime(e.Timestamp))
        .Select(g =>
        {
            // Entries are oldest first, so the last value written for a factor is the latest.
            var latest = new Dictionary<int, EntryFactor>();
            foreach (var ef in g.SelectMany(e => e.Factors)) latest[ef.FactorId] = ef;
            return new DayResponse(g.Key, latest.Values
                .Select(ef => DayFactorResponse.From(ef.Factor!, ef.Value, categories))
                .OrderBy(x => x.Category).ThenBy(x => x.Name).ToList());
        })
        .ToList());
});

app.MapPut("/days/{date}", async (DateOnly date, DayRequest req, MoodDbContext db) =>
{
    var start = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
    var end = start.AddDays(1);
    var existing = await db.Entries.Where(e => e.Timestamp >= start && e.Timestamp < end)
        .SelectMany(e => e.Factors).Select(ef => ef.FactorId).Distinct().ToListAsync();

    var factorValues = req.Factors ?? [];
    // A factor archived since this day was logged can keep the value it already has.
    var (factors, invalid) = await CheckValuesAsync(db, factorValues, f => f.ArchivedAt is null || existing.Contains(f.Id));
    if (invalid is not null) return invalid;

    await using var tx = await db.Database.BeginTransactionAsync();
    await db.Entries.Where(e => e.Timestamp >= start && e.Timestamp < end).ExecuteDeleteAsync();
    db.Entries.Add(new Entry
    {
        Timestamp = start.AddHours(12),
        Factors = factorValues.Select(f => new EntryFactor { FactorId = f.FactorId, Value = f.Value }).ToList()
    });
    await db.SaveChangesAsync();
    await tx.CommitAsync();
    return Results.Ok(new { Alerts = AlertsFor(factorValues, factors) });
});

app.MapDelete("/days/{date}", async (DateOnly date, MoodDbContext db) =>
{
    var start = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
    var end = start.AddDays(1);
    var deleted = await db.Entries.Where(e => e.Timestamp >= start && e.Timestamp < end).ExecuteDeleteAsync();
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

// Ids of the conditions this user has turned on: their own choice, else the condition's default.
static async Task<HashSet<string>> EnabledConditionIdsAsync(MoodDbContext db)
{
    var chosen = await db.UserConditions.ToDictionaryAsync(c => c.ConditionId, c => c.Enabled);
    var conditions = await db.Conditions.ToListAsync();
    return conditions.Where(c => chosen.GetValueOrDefault(c.Id, c.Enabled)).Select(c => c.Id).ToHashSet();
}

// Loads the factors a set of values refers to and checks each value. `mayRecord` says whether a factor
// accepts a value right now. Returns the error response to send back, or null when everything is fine.
static async Task<(Dictionary<int, Factor> Factors, IResult? Error)> CheckValuesAsync(
    MoodDbContext db, List<FactorValueRequest> values, Func<Factor, bool> mayRecord)
{
    var ids = values.Select(f => f.FactorId).Distinct().ToList();
    if (ids.Count != values.Count)
        return ([], Results.BadRequest("Each factor can only appear once per entry."));
    var factors = await db.Factors.Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id);
    if (factors.Count != ids.Count)
        return (factors, Results.BadRequest("One or more factorIds do not exist."));

    var errors = new List<string>();
    foreach (var fv in values)
    {
        var factor = factors[fv.FactorId];
        if (!mayRecord(factor))
            errors.Add($"'{factor.Name}' is archived. Unarchive it to record new values.");
        else if (ValueValidator.Validate(factor, fv.Value) is { } error)
            errors.Add(error);
    }
    return (factors, errors.Count > 0 ? Results.BadRequest(errors) : null);
}

// A positive answer on a safety item should surface an alert (crisis resources, or a prompt to contact the care team).
static List<SafetyAlert> AlertsFor(List<FactorValueRequest> values, Dictionary<int, Factor> factors) => values
    .Where(fv => fv.Value > 0 && factors[fv.FactorId].SafetyKind is not null)
    .Select(fv => factors[fv.FactorId].SafetyKind!.Value)
    .Distinct()
    .Select(SafetyAlert.For)
    .ToList();

// Category of each catalog-backed factor, keyed by symptom item id. Custom factors have none.
static async Task<Dictionary<string, string>> CategoriesAsync(MoodDbContext db, IEnumerable<Factor> factors)
{
    var ids = factors.Select(f => f.SymptomItemId).OfType<string>().Distinct().ToList();
    return await db.SymptomItems.Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Category);
}

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
record DayRequest(List<FactorValueRequest>? Factors);
record DayFactorResponse(
    int FactorId, string Name, string? Unit, string Category, InputType InputType, JsonElement? Config, double Value)
{
    public static DayFactorResponse From(Factor f, double value, Dictionary<string, string> categories) => new(
        f.Id, f.Name, f.Unit,
        f.SymptomItemId is not null && categories.TryGetValue(f.SymptomItemId, out var c) ? c : "Custom",
        f.InputType, f.ConfigJson is null ? null : JsonSerializer.Deserialize<JsonElement>(f.ConfigJson), value);
}
record DayResponse(DateOnly Date, List<DayFactorResponse> Factors);
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
        SafetyKind.HarmToOthers => new(kind,
            "Having thoughts of harming someone else can be frightening, and help is available. If anyone is in immediate danger, call 911 (US) now. Otherwise, please contact your care team soon, or call or text 988 to talk it through.",
            "911"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
record CatalogItemResponse(
    string Id, string Label, string Category, InputType InputType, JsonElement Config,
    Polarity Polarity, string? Pole, SafetyKind? SafetyKind, string[] Conditions, string[] RelatedIds,
    string? CrossRefNote, string? SourceNote, bool Tracked, int? FactorId);
record EntryResponse(int Id, DateTime Timestamp, int? Mood, string? Notes, List<FactorValueResponse> Factors);

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
