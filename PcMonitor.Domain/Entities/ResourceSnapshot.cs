using PcMonitor.Domain.Common;
using PcMonitor.Domain.Enums;
using PcMonitor.Domain.ValueObjects;

namespace PcMonitor.Domain.Entities;

/// <summary>
/// Агрегат: единичный замер системы в момент времени.
/// </summary>
public sealed class ResourceSnapshot
{
    private readonly List<ProcessSnapshot> _processes;
    private readonly IReadOnlyList<ProcessSnapshot> _processesReadOnly;

    public Guid Id { get; }
    public DateTimeOffset Timestamp { get; }
    public CpuUsage Cpu { get; }
    public MemoryUsage Memory { get; }
    public DiskIoRate DiskIo { get; }
    public GpuMetrics? Gpu { get; }
    public IReadOnlyList<ProcessSnapshot> Processes => _processesReadOnly;

    public ResourceSnapshot(
        DateTimeOffset timestamp,
        CpuUsage cpu,
        MemoryUsage memory,
        DiskIoRate diskIo,
        GpuMetrics? gpu,
        IEnumerable<ProcessSnapshot> processes,
        Guid? id = null)
    {
        if (timestamp.Offset != TimeSpan.Zero)
            throw new DomainException("Timestamp must be in UTC (Offset == 0).");
        Guard.AgainstNull(processes, nameof(processes));

        var list = processes.ToList();
        if (list.Any(p => p is null))
            throw new DomainException("Process list cannot contain nulls.");
        if (list.GroupBy(p => p.Process.Pid).Any(g => g.Count() > 1))
            throw new DomainException("Duplicate process PIDs in snapshot.");

        Id = id ?? Guid.NewGuid();
        Timestamp = timestamp;
        Cpu = cpu;
        Memory = memory;
        DiskIo = diskIo;
        Gpu = gpu;
        _processes = list;
        _processesReadOnly = _processes.AsReadOnly();
    }

    /// <summary>
    /// Процесс с наибольшим потреблением по указанной метрике
    /// или null, если процессов нет (или метрика не поддерживается
    /// на уровне процессов — например, GPU).
    /// </summary>
    public ProcessSnapshot? GetTopConsumerBy(MetricType metric)
    {
        if (_processes.Count == 0) return null;

        return metric switch
        {
            MetricType.Cpu => _processes
                .Where(p => p.Cpu.Percent >= 0.5)
                .MaxBy(p => p.Cpu.Percent),

            MetricType.Memory => _processes
                .Where(p => p.Memory.WorkingSetBytes >= 1024 * 1024)
                .MaxBy(p => p.Memory.WorkingSetBytes),

            MetricType.DiskIo => _processes
                .Where(p => p.DiskIo.TotalBytesPerSecond >= 10 * 1024)
                .MaxBy(p => p.DiskIo.TotalBytesPerSecond),

            MetricType.Gpu => _processes
                .Where(p => p.Gpu.Percent >= 0.5)
                .MaxBy(p => p.Gpu.Percent),

            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };
    }
}