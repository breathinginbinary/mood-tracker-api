using Microsoft.EntityFrameworkCore;

namespace MoodTracker.Api.Data;

public class MoodDbContext(DbContextOptions<MoodDbContext> options) : DbContext(options)
{
    public DbSet<Entry> Entries => Set<Entry>();
    public DbSet<Factor> Factors => Set<Factor>();
    public DbSet<EntryFactor> EntryFactors => Set<EntryFactor>();
    public DbSet<SymptomItem> SymptomItems => Set<SymptomItem>();
    public DbSet<Condition> Conditions => Set<Condition>();
    public DbSet<SymptomItemCondition> SymptomItemConditions => Set<SymptomItemCondition>();
    public DbSet<Medication> Medications => Set<Medication>();
    public DbSet<MedicationRegimen> MedicationRegimens => Set<MedicationRegimen>();
    public DbSet<MedicationLog> MedicationLogs => Set<MedicationLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Entry>().HasIndex(e => e.Timestamp);
        modelBuilder.Entity<Factor>().HasIndex(f => f.Name).IsUnique();
        // SQLite allows many NULLs in a unique index, so custom factors are unaffected.
        modelBuilder.Entity<Factor>().HasIndex(f => f.SymptomItemId).IsUnique();
        modelBuilder.Entity<Factor>().Property(f => f.InputType).HasConversion<string>();
        modelBuilder.Entity<Factor>().Property(f => f.Polarity).HasConversion<string>();
        modelBuilder.Entity<Factor>().Property(f => f.SafetyKind).HasConversion<string>();
        modelBuilder.Entity<EntryFactor>().HasKey(ef => new { ef.EntryId, ef.FactorId });

        modelBuilder.Entity<SymptomItem>().HasKey(s => s.Id);
        modelBuilder.Entity<SymptomItem>().Property(s => s.InputType).HasConversion<string>();
        modelBuilder.Entity<SymptomItem>().Property(s => s.Polarity).HasConversion<string>();
        modelBuilder.Entity<SymptomItem>().Property(s => s.SafetyKind).HasConversion<string>();
        modelBuilder.Entity<Condition>().HasKey(c => c.Id);
        modelBuilder.Entity<SymptomItemCondition>().HasKey(ic => new { ic.SymptomItemId, ic.ConditionId });
        modelBuilder.Entity<SymptomItemCondition>().HasOne(ic => ic.SymptomItem).WithMany(s => s.Conditions).HasForeignKey(ic => ic.SymptomItemId);
        modelBuilder.Entity<SymptomItemCondition>().HasOne(ic => ic.Condition).WithMany().HasForeignKey(ic => ic.ConditionId);
        modelBuilder.Entity<SymptomItem>().HasIndex(s => s.Category);

        modelBuilder.Entity<Medication>().HasIndex(m => m.Name).IsUnique();
        modelBuilder.Entity<Medication>().Property(m => m.Status).HasConversion<string>();
        modelBuilder.Entity<MedicationRegimen>().Property(r => r.Unit).HasConversion<string>();
        modelBuilder.Entity<MedicationRegimen>().Property(r => r.Frequency).HasConversion<string>();
        modelBuilder.Entity<MedicationRegimen>().HasOne(r => r.Medication).WithMany(m => m.Regimens).HasForeignKey(r => r.MedicationId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MedicationLog>().Property(l => l.Taken).HasConversion<string>();
        modelBuilder.Entity<MedicationLog>().HasIndex(l => new { l.MedicationId, l.Date }).IsUnique();
        modelBuilder.Entity<MedicationLog>().HasOne(l => l.Medication).WithMany().HasForeignKey(l => l.MedicationId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MedicationLog>().HasOne(l => l.Regimen).WithMany().HasForeignKey(l => l.RegimenId).OnDelete(DeleteBehavior.SetNull);
    }
}
