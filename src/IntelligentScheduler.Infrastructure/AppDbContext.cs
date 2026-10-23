using IntelligentScheduler.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace IntelligentScheduler.Infrastructure;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
    public DbSet<TimeBlock> TimeBlocks => Set<TimeBlock>();
    public DbSet<TaskScheduleExclusion> TaskScheduleExclusions => Set<TaskScheduleExclusion>();
    public DbSet<AvailabilityRule> AvailabilityRules => Set<AvailabilityRule>();
    public DbSet<AvailabilityException> AvailabilityExceptions => Set<AvailabilityException>();
    public DbSet<PlanningProfile> PlanningProfiles => Set<PlanningProfile>();
    public DbSet<PlanRevision> PlanRevisions => Set<PlanRevision>();
    public DbSet<TaskExecution> TaskExecutions => Set<TaskExecution>();
    public DbSet<SchedulerDiagnostic> SchedulerDiagnostics => Set<SchedulerDiagnostic>();
    public DbSet<AppSettings> AppSettings => Set<AppSettings>();
    public DbSet<ChatConversation> ChatConversations => Set<ChatConversation>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<UserMemoryEntry> UserMemoryEntries => Set<UserMemoryEntry>();
    public DbSet<EventOutcome> EventOutcomes => Set<EventOutcome>();
    public DbSet<EventFollowUp> EventFollowUps => Set<EventFollowUp>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectStep> ProjectSteps => Set<ProjectStep>();
    public DbSet<ProjectStepCheck> ProjectStepChecks => Set<ProjectStepCheck>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskItem>(entity =>
        {
            entity.ToTable("Tasks");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>();
            entity.Property(x => x.PreferredTimeOfDay).HasConversion<string>();
            entity.HasIndex(x => new { x.Status, x.Priority });
        });

        modelBuilder.Entity<CalendarEvent>(entity =>
        {
            entity.ToTable("CalendarEvents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Category).HasConversion<string>();
            entity.Property(x => x.Priority).HasConversion<string>();
            entity.Property(x => x.Flexibility).HasConversion<string>();
            entity.Property(x => x.Recurrence).HasConversion<string>();
            entity.HasIndex(x => new { x.StartUtc, x.EndUtc });
            entity.HasIndex(x => new { x.Source, x.ExternalId }).IsUnique();
            entity.HasIndex(x => new { x.SeriesId, x.StartUtc });
        });

        modelBuilder.Entity<TimeBlock>(entity =>
        {
            entity.ToTable("TimeBlocks");
            entity.HasKey(x => x.Id);
            entity.Ignore(x => x.DurationMinutes);
            entity.Property(x => x.State).HasConversion<string>();
            entity.Property(x => x.Origin).HasConversion<string>();
            entity.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(x => new { x.StartUtc, x.EndUtc });
        });

        modelBuilder.Entity<TaskScheduleExclusion>(entity =>
        {
            entity.ToTable("TaskScheduleExclusions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasConversion<string>();
            entity.HasOne<TaskItem>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.TaskId, x.StartUtc, x.EndUtc });
        });

        modelBuilder.Entity<AvailabilityRule>(entity =>
        {
            entity.ToTable("AvailabilityRules");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DayOfWeek).HasConversion<int>();
        });

        modelBuilder.Entity<AvailabilityException>(entity =>
        {
            entity.ToTable("AvailabilityExceptions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasConversion<string>();
        });

        modelBuilder.Entity<PlanningProfile>(entity =>
        {
            entity.ToTable("PlanningProfiles");
            entity.HasKey(x => x.Id);
        });

        modelBuilder.Entity<PlanRevision>(entity =>
        {
            entity.ToTable("PlanRevisions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>();
        });

        modelBuilder.Entity<TaskExecution>(entity =>
        {
            entity.ToTable("TaskExecutions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TaskId, x.CompletedAtUtc });
        });

        modelBuilder.Entity<SchedulerDiagnostic>(entity =>
        {
            entity.ToTable("SchedulerDiagnostics");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.RevisionId).IsUnique();
        });

        modelBuilder.Entity<AppSettings>(entity =>
        {
            entity.ToTable("AppSettings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AiProviderMode).HasConversion<string>();
            entity.Property(x => x.AssistantProvider).HasConversion<string>();
            entity.Property(x => x.AssistantAccessLevel).HasConversion<string>();
            entity.Property(x => x.ShellTheme).HasConversion<string>();
            entity.Property(x => x.ShellThemeMode).HasConversion<string>();
            entity.Property(x => x.ThemeColorSource).HasConversion<string>();
            entity.Property(x => x.VisualPalette).HasConversion<string>();
            entity.Property(x => x.RevealMode).HasConversion<string>();
            entity.Property(x => x.CloudSyncProvider).HasConversion<string>();
        });

        modelBuilder.Entity<ChatConversation>(entity =>
        {
            entity.ToTable("ChatConversations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Preview).HasMaxLength(400);
            entity.HasIndex(x => x.UpdatedAtUtc);
        });

        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.ToTable("ChatMessages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Role).HasConversion<string>();
            entity.Property(x => x.Content).IsRequired();
            entity.HasOne<ChatConversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.ConversationId, x.Ordinal });
        });

        modelBuilder.Entity<UserMemoryEntry>(entity =>
        {
            entity.ToTable("UserMemoryEntries");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Key).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Value).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.Category).HasConversion<string>();
            entity.HasIndex(x => x.Key).IsUnique();
        });

        modelBuilder.Entity<EventOutcome>(entity =>
        {
            entity.ToTable("EventOutcomes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasConversion<string>();
            entity.Property(x => x.Note).HasMaxLength(400);
            entity.Ignore(x => x.IsFinished);
            entity.Ignore(x => x.StartDelayMinutes);
            entity.Ignore(x => x.IsPaused);
            entity.Ignore(x => x.IsRunning);
            entity.Ignore(x => x.IsSessionOpen);
            // One outcome per event: recording again replaces the previous verdict.
            entity.HasIndex(x => x.EventId).IsUnique();
            entity.HasIndex(x => x.PlannedStartUtc);
        });

        modelBuilder.Entity<EventFollowUp>(entity =>
        {
            entity.ToTable("EventFollowUps");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.NextStep).HasMaxLength(400);
            entity.Property(x => x.Blocker).HasConversion<string>();
            entity.Ignore(x => x.HasNextStep);
            entity.Ignore(x => x.NeedsBlockerAnswer);
            // One follow-up per event, replaced in place the way a verdict is.
            entity.HasIndex(x => x.EventId).IsUnique();
        });

        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("Projects");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Category).HasConversion<string>();
            entity.Ignore(x => x.Host);
            entity.Ignore(x => x.IsScheduled);
            // Bare ids like a step's slot: the host is looked for, not joined to, and a host that
            // has gone is noticed and its steps let go of rather than enforced by the database.
            entity.HasIndex(x => x.TaskId);
            entity.HasIndex(x => x.EventId);
            entity.HasIndex(x => x.SeriesId);
        });

        modelBuilder.Entity<ProjectStepCheck>(entity =>
        {
            entity.ToTable("ProjectStepChecks");
            entity.HasKey(x => x.Id);
            // One tick per step per sitting: ticking twice is still ticked once.
            entity.HasIndex(x => new { x.StepId, x.EventId }).IsUnique();
            entity.HasIndex(x => x.EventId);
        });

        modelBuilder.Entity<ProjectStep>(entity =>
        {
            entity.ToTable("ProjectSteps");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(300).IsRequired();
            entity.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.ProjectId, x.SortOrder });
            entity.HasIndex(x => x.PlannedDate);
            // A bare id, not a key: deleting the booked event leaves the step planned for its day.
            entity.HasIndex(x => x.EventId);
        });

        var dateTimeOffsetConverter = new ValueConverter<DateTimeOffset, long>(
            value => value.UtcTicks,
            value => new DateTimeOffset(value, TimeSpan.Zero));
        var nullableDateTimeOffsetConverter = new ValueConverter<DateTimeOffset?, long?>(
            value => value.HasValue ? value.Value.UtcTicks : null,
            value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        foreach (var property in entityType.GetProperties())
        {
            if (property.ClrType == typeof(DateTimeOffset)) property.SetValueConverter(dateTimeOffsetConverter);
            if (property.ClrType == typeof(DateTimeOffset?)) property.SetValueConverter(nullableDateTimeOffsetConverter);
        }
    }
}
