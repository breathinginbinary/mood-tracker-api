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
    }
}
