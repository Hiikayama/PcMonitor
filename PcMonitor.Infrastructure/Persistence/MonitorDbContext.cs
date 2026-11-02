using Microsoft.EntityFrameworkCore;
using PcMonitor.Infrastructure.Persistence.Entities;

namespace PcMonitor.Infrastructure.Persistence;

public sealed class MonitorDbContext : DbContext
{
    public DbSet<SnapshotRecord> Snapshots => Set<SnapshotRecord>();
    public DbSet<ProcessRecord> Processes => Set<ProcessRecord>();
    public DbSet<SystemEventRecord> SystemEvents => Set<SystemEventRecord>();
    public DbSet<DailyReportRecord> DailyReports => Set<DailyReportRecord>();
    public DbSet<AnomalyRecord> Anomalies => Set<AnomalyRecord>();
    public DbSet<TopConsumerRecord> TopConsumers => Set<TopConsumerRecord>();

    public MonitorDbContext(DbContextOptions<MonitorDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<SnapshotRecord>(e =>
        {
            e.ToTable("Snapshots");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Timestamp);
            e.Property(x => x.Timestamp)
                .HasConversion(
                    v => v.ToUnixTimeMilliseconds(),
                    v => DateTimeOffset.FromUnixTimeMilliseconds(v))
                .HasColumnType("INTEGER");
            e.HasMany(x => x.Processes)
             .WithOne(p => p.Snapshot)
             .HasForeignKey(p => p.SnapshotId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<ProcessRecord>(e =>
        {
            e.ToTable("Processes");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.SnapshotId);
            e.HasIndex(x => x.Pid);
            e.Property(x => x.GpuPercent).HasDefaultValue(0.0);
        });

        mb.Entity<SystemEventRecord>(e =>
        {
            e.ToTable("SystemEvents");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Timestamp);   // GetRangeAsync фильтрует по нему
            e.Property(x => x.Timestamp)
                .HasConversion(
                    v => v.ToUnixTimeMilliseconds(),
                    v => DateTimeOffset.FromUnixTimeMilliseconds(v))
                .HasColumnType("INTEGER");
            e.Property(x => x.Source).HasMaxLength(200).IsRequired();
            e.Property(x => x.Message).IsRequired();   // без ограничения — события длинные
            e.Property(x => x.Category).HasConversion<int>();
            e.Property(x => x.Severity).HasConversion<int>();
        });

        mb.Entity<DailyReportRecord>(e =>
        {
            e.ToTable("DailyReports");
            e.HasKey(x => x.Id);

            // Один отчёт на дату — иначе upsert в репозитории не имеет смысла.
            e.HasIndex(x => x.Date).IsUnique();

            e.Property(x => x.GeneratedAt)
                .HasConversion(
                    v => v.ToUnixTimeMilliseconds(),
                    v => DateTimeOffset.FromUnixTimeMilliseconds(v))
                .HasColumnType("INTEGER");

            // DateOnly — нативно в EF Core 8. На EF Core 7 раскомментируйте:
            // e.Property(x => x.Date).HasConversion<DateOnlyConverter>();

            e.HasMany(x => x.Anomalies)
             .WithOne(a => a.Report)
             .HasForeignKey(a => a.DailyReportId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(x => x.TopConsumers)
             .WithOne(t => t.Report)
             .HasForeignKey(t => t.DailyReportId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<AnomalyRecord>(e =>
        {
            e.ToTable("Anomalies");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DailyReportId);
            e.HasIndex(x => x.CulpritPid);       // «все аномалии для PID 1234»
            e.Property(x => x.OccurredAt)
                .HasConversion(
                    v => v.ToUnixTimeMilliseconds(),
                    v => DateTimeOffset.FromUnixTimeMilliseconds(v))
                .HasColumnType("INTEGER");
            e.Property(x => x.Reason).IsRequired();
            e.Property(x => x.Metric).HasConversion<int>();
            e.Property(x => x.Severity).HasConversion<int>();
            e.Property(x => x.CulpritName).HasMaxLength(200);
        });

        mb.Entity<TopConsumerRecord>(e =>
        {
            e.ToTable("TopConsumers");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DailyReportId);
            e.Property(x => x.ProcessName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Metric).HasConversion<int>();
        });
    }
}