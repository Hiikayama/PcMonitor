using PcMonitor.Domain.ValueObjects;

namespace PcMonitor.Domain.Entities;

/// <summary>
/// Снимок состояния одного процесса в конкретный момент.
/// Иммутабельный — это value object внутри агрегата ResourceSnapshot.
/// </summary>
public sealed record ProcessSnapshot(
    ProcessIdentifier Process,
    CpuUsage Cpu,
    ProcessMemoryUsage Memory,
    DiskIoRate DiskIo,
    GpuProcessUsage Gpu = default);