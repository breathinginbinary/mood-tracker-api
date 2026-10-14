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
