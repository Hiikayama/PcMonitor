using PcMonitor.Domain.Common;

namespace PcMonitor.Domain.ValueObjects;

/// <summary>Загрузка GPU одним процессом в процентах (0–100).</summary>
public readonly record struct GpuProcessUsage
{
    public double Percent { get; }

    public GpuProcessUsage(double percent)
    {
        Guard.AgainstOutOfRange(percent, 0, 100, nameof(percent));
        Percent = percent;
    }

    public static GpuProcessUsage Zero => new(0);
}