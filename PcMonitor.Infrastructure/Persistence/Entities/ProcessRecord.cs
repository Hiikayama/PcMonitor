namespace PcMonitor.Infrastructure.Persistence.Entities;

public sealed class ProcessRecord
{
    public long Id { get; set; }
    public Guid SnapshotId { get; set; }
    public int Pid { get; set; }
    public string Name { get; set; } = null!;
    public double CpuPercent { get; set; }
    public long MemoryWorkingSetBytes { get; set; }
    public long DiskReadBps { get; set; }
    public long DiskWriteBps { get; set; }
    public double GpuPercent { get; set; }

    public SnapshotRecord Snapshot { get; set; } = null!;
}