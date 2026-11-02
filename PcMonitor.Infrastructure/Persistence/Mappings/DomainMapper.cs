using PcMonitor.Domain.Entities;
using PcMonitor.Domain.Enums;
using PcMonitor.Domain.ValueObjects;
using PcMonitor.Infrastructure.Persistence.Entities;

namespace PcMonitor.Infrastructure.Persistence.Mappings;

/// <summary>
/// Единственное место, где доменные типы пересекаются с persistence-моделью.
/// Явный ручной маппинг — вместо AutoMapper, чтобы:
///   • валидация Domain срабатывала при чтении из БД;
///   • каждое поле прошло через глаза разработчика;
///   • не было «тихих» дефолтов при изменении схемы.
/// </summary>
public static class DomainMapper
{
    // ============================================================
    // ResourceSnapshot ↔ SnapshotRecord
    // ============================================================

    public static SnapshotRecord ToRecord(ResourceSnapshot domain) => new()
    {
        Id = domain.Id,
        Timestamp = domain.Timestamp,

        CpuPercent = domain.Cpu.Percent,

        MemoryUsedBytes = domain.Memory.UsedBytes,
        MemoryTotalBytes = domain.Memory.TotalBytes,

        DiskReadBps = domain.DiskIo.ReadBytesPerSecond,
        DiskWriteBps = domain.DiskIo.WriteBytesPerSecond,

        // Системный GPU — здесь, в SnapshotRecord.
        GpuDeviceName = domain.Gpu?.DeviceName,
        GpuUtilizationPercent = domain.Gpu?.UtilizationPercent,
        GpuTemperatureCelsius = domain.Gpu?.TemperatureCelsius,
        GpuMemoryUsedBytes = domain.Gpu?.MemoryUsedBytes,
        GpuMemoryTotalBytes = domain.Gpu?.MemoryTotalBytes,

        Processes = domain.Processes
            .Select(p => new ProcessRecord
            {
                SnapshotId = domain.Id,
                Pid = p.Process.Pid,
                Name = p.Process.Name,
                CpuPercent = p.Cpu.Percent,
                MemoryWorkingSetBytes = p.Memory.WorkingSetBytes,
                DiskReadBps = p.DiskIo.ReadBytesPerSecond,
                DiskWriteBps = p.DiskIo.WriteBytesPerSecond,

                // Per-process GPU — здесь, в ProcessRecord.
                GpuPercent = p.Gpu.Percent,
            })
            .ToList(),
    };

    public static ResourceSnapshot ToDomain(SnapshotRecord rec) => new(
        timestamp: rec.Timestamp,
        cpu: new CpuUsage(rec.CpuPercent),
        memory: new MemoryUsage(rec.MemoryUsedBytes, rec.MemoryTotalBytes),
        diskIo: new DiskIoRate(rec.DiskReadBps, rec.DiskWriteBps),
        // GpuDeviceName != null — единственный признак «GPU был».
        // Если БД повреждена и GpuUtilizationPercent == null, считаем 0
        // (валидное значение, отчёт не сломается).
        gpu: rec.GpuDeviceName is null
            ? null
            : new GpuMetrics(
                deviceName: rec.GpuDeviceName,
                utilizationPercent: rec.GpuUtilizationPercent ?? 0,
                temperatureCelsius: rec.GpuTemperatureCelsius,
                memoryUsedBytes: rec.GpuMemoryUsedBytes,
                memoryTotalBytes: rec.GpuMemoryTotalBytes),
        processes: rec.Processes.Select(p => new ProcessSnapshot(
            Process: new ProcessIdentifier(p.Pid, p.Name),
            Cpu: new CpuUsage(p.CpuPercent),
            Memory: new ProcessMemoryUsage(p.MemoryWorkingSetBytes),
            DiskIo: new DiskIoRate(p.DiskReadBps, p.DiskWriteBps),
            // Per-process GPU — пятый параметр ProcessSnapshot.
            Gpu: new GpuProcessUsage(p.GpuPercent))).ToList(),
        id: rec.Id);

    // ============================================================
    // SystemEvent ↔ SystemEventRecord
    // ============================================================

    public static SystemEventRecord ToRecord(SystemEvent domain) => new()
    {
        Id = domain.Id,
        Timestamp = domain.Timestamp,
        Category = (int)domain.Category,
        Source = domain.Source,
        Message = domain.Message,
        Severity = (int)domain.Severity,
    };

    public static SystemEvent ToDomain(SystemEventRecord rec) => new(
        timestamp: rec.Timestamp,
        category: (SystemEventCategory)rec.Category,
        source: rec.Source,
        message: rec.Message,
        severity: (SeverityLevel)rec.Severity,
        id: rec.Id);

    // ============================================================
    // DailyReport ↔ DailyReportRecord (+ Anomaly, TopConsumer)
    // ============================================================

    public static DailyReportRecord ToRecord(DailyReport domain) => new()
    {
        Id = domain.Id,
        Date = domain.Date,
        GeneratedAt = domain.GeneratedAt,
        SnapshotsAnalyzed = domain.SnapshotsAnalyzed,

        Anomalies = domain.Anomalies
            .Select(a => AnomalyRecord(a, domain.Id))
            .ToList(),

        TopConsumers = domain.TopConsumers
            .Select(t => TopConsumerRecord(t, domain.Id))
            .ToList(),
    };

    public static DailyReport ToDomain(DailyReportRecord rec) => new(
        date: rec.Date,
        generatedAt: rec.GeneratedAt,
        snapshotsAnalyzed: rec.SnapshotsAnalyzed,
        anomalies: rec.Anomalies.Select(ToDomain).ToList(),
        topConsumers: rec.TopConsumers.Select(ToDomain).ToList(),
        id: rec.Id);

    // ============================================================
    // Anomaly ↔ AnomalyRecord
    // ============================================================

    public static AnomalyRecord AnomalyRecord(Anomaly domain, Guid reportId) => new()
    {
        DailyReportId = reportId,
        OccurredAt = domain.OccurredAt,
        Metric = (int)domain.Metric,
        Severity = (int)domain.Severity,
        Reason = domain.Reason,
        ObservedValue = domain.ObservedValue,
        Threshold = domain.Threshold,
        CulpritPid = domain.Culprit?.Pid,
        CulpritName = domain.Culprit?.Name,
    };

    public static Anomaly ToDomain(AnomalyRecord rec)
    {
        ProcessIdentifier? culprit = null;
        if (rec.CulpritPid is { } pid && !string.IsNullOrWhiteSpace(rec.CulpritName))
        {
            culprit = new ProcessIdentifier(pid, rec.CulpritName);
        }

        return new Anomaly(
            occurredAt: rec.OccurredAt,
            metric: (MetricType)rec.Metric,
            severity: (SeverityLevel)rec.Severity,
            reason: rec.Reason,
            observedValue: rec.ObservedValue,
            threshold: rec.Threshold,
            culprit: culprit);
    }

    // ============================================================
    // TopConsumer ↔ TopConsumerRecord
    // ============================================================

    public static TopConsumerRecord TopConsumerRecord(TopConsumer domain, Guid reportId) => new()
    {
        DailyReportId = reportId,
        ProcessPid = domain.Process.Pid,
        ProcessName = domain.Process.Name,
        Metric = (int)domain.Metric,
        AverageValue = domain.AverageValue,
        PeakValue = domain.PeakValue,
        TotalObservedTime = domain.TotalObservedTime,
    };

    public static TopConsumer ToDomain(TopConsumerRecord rec) => new(
        process: new ProcessIdentifier(rec.ProcessPid, rec.ProcessName),
        metric: (MetricType)rec.Metric,
        averageValue: rec.AverageValue,
        peakValue: rec.PeakValue,
        totalObservedTime: rec.TotalObservedTime);
}