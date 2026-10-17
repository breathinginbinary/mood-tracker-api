namespace MoodTracker.Api.Data;

/// <summary>One mood check-in. Timestamps are UTC so lagged analysis stays consistent.</summary>
public class Entry
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    /// <summary>Mood score, 0 (lowest) to 5 (highest), matching the front end's scale.</summary>
    public int Mood { get; set; }
    public string? Notes { get; set; }
    public List<EntryFactor> Factors { get; set; } = [];
}

/// <summary>How a factor's value is entered. Every type is stored as a number on <see cref="EntryFactor.Value"/>.</summary>
public enum InputType
{
    /// <summary>Free numeric value (the original behavior; custom factors use this).</summary>
    Number,
    /// <summary>Integer in the range given by the factor's config (min/max).</summary>
    Slider,
    /// <summary>One of the config's option values.</summary>
    Radio,
    /// <summary>0 = no, 1 = yes.</summary>
    Checkbox,
}

/// <summary>Which direction of a score is "worse", so charts can be oriented consistently.</summary>
public enum Polarity
{
    /// <summary>Descriptive or context only; no good/bad direction.</summary>
    Neutral,
    HigherWorse,
    HigherBetter,
    /// <summary>Negative and positive both mean something (e.g. -3 depressed to +3 elevated).</summary>
    Bidirectional,
}

/// <summary>
/// An illness the app can track items for (ADHD, bipolar disorder, ...). Seeded from Data/conditions.json;
/// <see cref="Enabled"/> is the user's switch and is never overwritten by the seed after first insert.
/// </summary>
public class Condition
{
    /// <summary>Stable slug such as "adhd". Never reused.</summary>
    public required string Id { get; set; }
    public required string Name { get; set; }
    /// <summary>When false, the condition's items are hidden from the catalog. Already-tracked factors and history are untouched.</summary>
    public bool Enabled { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>Links a catalog item to a condition it belongs to (many-to-many).</summary>
public class SymptomItemCondition
{
    public required string SymptomItemId { get; set; }
    public SymptomItem? SymptomItem { get; set; }
    public required string ConditionId { get; set; }
    public Condition? Condition { get; set; }
}

/// <summary>What kind of alert a positive answer on a safety item should raise.</summary>
public enum SafetyKind
{
    /// <summary>Thoughts of death or self-harm: show crisis resources (988).</summary>
    SelfHarm,
    /// <summary>Unusual beliefs or perceptions, which can signal a severe episode: suggest contacting the care team.</summary>
    UnusualExperiences,
}

/// <summary>
/// Read-only catalog entry seeded from Data/symptom_items.json. Users pick from this list to add a
/// <see cref="Factor"/>; the catalog row is copied, so later catalog edits never change tracked history.
/// </summary>
public class SymptomItem
{
    /// <summary>Stable key such as "S001" (ADHD list) or "B001" (bipolar list). Never reused.</summary>
    public required string Id { get; set; }
    public required string Label { get; set; }
    public required string Category { get; set; }
    public InputType InputType { get; set; }
    /// <summary>JSON: slider {min,max,minLabel,maxLabel}, radio {options:[{value,label}]}, checkbox {checkedMeans}.</summary>
    public required string ConfigJson { get; set; }
    public Polarity Polarity { get; set; }
    /// <summary>"elevated" or "depressive" for bipolar items tied to one pole.</summary>
    public string? Pole { get; set; }
    /// <summary>Set when a positive answer should trigger an alert. Null for ordinary items.</summary>
    public SafetyKind? SafetyKind { get; set; }
    /// <summary>Conditions this item belongs to. It is offered only while at least one of them is enabled.</summary>
    public List<SymptomItemCondition> Conditions { get; set; } = [];
    public int SortOrder { get; set; }
    /// <summary>Comma-separated IDs of the matching item(s) in the other list, from the Cross-Reference tab.</summary>
    public string? RelatedIds { get; set; }
    /// <summary>How the condition(s) tend to differ for this symptom.</summary>
    public string? CrossRefNote { get; set; }
    public string? SourceNote { get; set; }
}

/// <summary>Something the user tracks: sleep hours, caffeine, a catalog symptom, etc.</summary>
public class Factor
{
    public int Id { get; set; }
    public required string Name { get; set; }
    /// <summary>Optional unit, e.g. "hours" or "cups".</summary>
    public string? Unit { get; set; }

    /// <summary>Catalog item this was added from; null for custom factors.</summary>
    public string? SymptomItemId { get; set; }
    public InputType InputType { get; set; } = InputType.Number;
    /// <summary>Copy of the catalog config at the time it was added (null for plain numbers).</summary>
    public string? ConfigJson { get; set; }
    public Polarity Polarity { get; set; } = Polarity.Neutral;
    public SafetyKind? SafetyKind { get; set; }
    /// <summary>Set when the user stops tracking this; hides it from check-ins but keeps its history.</summary>
    public DateTime? ArchivedAt { get; set; }
}

/// <summary>The value of one factor recorded on one entry.</summary>
public class EntryFactor
{
    public int EntryId { get; set; }
    public Entry? Entry { get; set; }
    public int FactorId { get; set; }
    public Factor? Factor { get; set; }
    public double Value { get; set; }
}

/// <summary>Whether a medication is still being taken. Retired medications keep their full history.</summary>
public enum MedicationStatus { Current, Retired }

/// <summary>Measurement choices offered in the dosage dropdown.</summary>
public enum DoseUnit { Mg, Mcg, G, ML, Units, Iu, Tablets, Capsules, Drops, Puffs, Patches, Percent }

/// <summary>How often a dose is taken, offered in the frequency dropdown.</summary>
public enum DoseFrequency { OnceDaily, TwiceDaily, ThreeTimesDaily, FourTimesDaily, EveryOtherDay, Weekly, AsNeeded }

/// <summary>A medication the user takes. Name is free text; dose and frequency live in <see cref="MedicationRegimen"/> so changes keep history.</summary>
public class Medication
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public MedicationStatus Status { get; set; } = MedicationStatus.Current;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>When the status last changed to Retired; null while current.</summary>
    public DateTime? RetiredAt { get; set; }
    public List<MedicationRegimen> Regimens { get; set; } = [];
}

/// <summary>
/// One dose/frequency setting for a medication. Changing either closes the open row (sets <see cref="EndedAt"/>)
/// and adds a new one, so the list of rows is the medication's dosage history.
/// </summary>
public class MedicationRegimen
{
    public int Id { get; set; }
    public int MedicationId { get; set; }
    public Medication? Medication { get; set; }
    /// <summary>User-entered amount, e.g. 20 for "20 mg".</summary>
    public double Dose { get; set; }
    public DoseUnit Unit { get; set; }
    public DoseFrequency Frequency { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Null for the regimen in effect now.</summary>
    public DateTime? EndedAt { get; set; }
}

/// <summary>How well one day's doses went.</summary>
public enum MedicationTaken { Yes, Partly, No }

/// <summary>Daily record for one medication. One row per medication per date; points at the regimen in effect that day.</summary>
public class MedicationLog
{
    public int Id { get; set; }
    public int MedicationId { get; set; }
    public Medication? Medication { get; set; }
    public int? RegimenId { get; set; }
    public MedicationRegimen? Regimen { get; set; }
    public DateOnly Date { get; set; }
    public MedicationTaken Taken { get; set; }
    public string? Notes { get; set; }
}
