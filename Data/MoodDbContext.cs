using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MoodTracker.Api.Auth;

namespace MoodTracker.Api.Data;

public class MoodDbContext(DbContextOptions<MoodDbContext> options, ICurrentUser currentUser)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Entry> Entries => Set<Entry>();
    public DbSet<Factor> Factors => Set<Factor>();
    public DbSet<EntryFactor> EntryFactors => Set<EntryFactor>();
    public DbSet<SymptomItem> SymptomItems => Set<SymptomItem>();
    public DbSet<Condition> Conditions => Set<Condition>();
    public DbSet<UserCondition> UserConditions => Set<UserCondition>();
    public DbSet<SymptomItemCondition> SymptomItemConditions => Set<SymptomItemCondition>();
    public DbSet<Medication> Medications => Set<Medication>();
    public DbSet<MedicationRegimen> MedicationRegimens => Set<MedicationRegimen>();
    public DbSet<MedicationLog> MedicationLogs => Set<MedicationLog>();

    // Read by the query filters below; EF re-evaluates it per context instance, i.e. per request.
    private string? CurrentUserId => currentUser.Id;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Rows owned by a user are only visible to that user. A request with no user sees nothing.
        modelBuilder.Entity<Entry>().HasQueryFilter(e => CurrentUserId != null && e.UserId == CurrentUserId);
        modelBuilder.Entity<Factor>().HasQueryFilter(f => CurrentUserId != null && f.UserId == CurrentUserId);
        modelBuilder.Entity<Medication>().HasQueryFilter(m => CurrentUserId != null && m.UserId == CurrentUserId);
        modelBuilder.Entity<UserCondition>().HasQueryFilter(c => CurrentUserId != null && c.UserId == CurrentUserId);
        // Child rows follow their parent, which also keeps EF from warning about required navigations.
        modelBuilder.Entity<EntryFactor>().HasQueryFilter(ef => CurrentUserId != null && ef.Entry!.UserId == CurrentUserId);
        modelBuilder.Entity<MedicationRegimen>().HasQueryFilter(r => CurrentUserId != null && r.Medication!.UserId == CurrentUserId);
        modelBuilder.Entity<MedicationLog>().HasQueryFilter(l => CurrentUserId != null && l.Medication!.UserId == CurrentUserId);

        modelBuilder.Entity<UserCondition>().HasKey(c => new { c.UserId, c.ConditionId });
        modelBuilder.Entity<UserCondition>().HasOne<ApplicationUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Entry>().HasOne<ApplicationUser>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Factor>().HasOne<ApplicationUser>().WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Medication>().HasOne<ApplicationUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Entry>().HasIndex(e => e.Timestamp);
        modelBuilder.Entity<Factor>().HasIndex(f => new { f.UserId, f.Name }).IsUnique();
        // SQLite allows many NULLs in a unique index, so custom factors are unaffected.
        modelBuilder.Entity<Factor>().HasIndex(f => new { f.UserId, f.SymptomItemId }).IsUnique();
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

        modelBuilder.Entity<Medication>().HasIndex(m => new { m.UserId, m.Name }).IsUnique();
        modelBuilder.Entity<Medication>().Property(m => m.Status).HasConversion<string>();
        modelBuilder.Entity<MedicationRegimen>().Property(r => r.Unit).HasConversion<string>();
        modelBuilder.Entity<MedicationRegimen>().Property(r => r.Frequency).HasConversion<string>();
        modelBuilder.Entity<MedicationRegimen>().HasOne(r => r.Medication).WithMany(m => m.Regimens).HasForeignKey(r => r.MedicationId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MedicationLog>().Property(l => l.Taken).HasConversion<string>();
        modelBuilder.Entity<MedicationLog>().HasIndex(l => new { l.MedicationId, l.Date }).IsUnique();
        modelBuilder.Entity<MedicationLog>().HasOne(l => l.Medication).WithMany().HasForeignKey(l => l.MedicationId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MedicationLog>().HasOne(l => l.Regimen).WithMany().HasForeignKey(l => l.RegimenId).OnDelete(DeleteBehavior.SetNull);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampOwner();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampOwner();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // New rows belong to whoever is signed in. Rows that already name an owner (the first-user claim) are left alone.
    private void StampOwner()
    {
        if (CurrentUserId is null) return;
        foreach (var entry in ChangeTracker.Entries<IUserOwned>().Where(e => e.State == EntityState.Added && e.Entity.UserId is null))
            entry.Entity.UserId = CurrentUserId;
    }
}
