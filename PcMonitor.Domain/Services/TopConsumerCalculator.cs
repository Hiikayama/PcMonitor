using PcMonitor.Domain.Entities;
using PcMonitor.Domain.Enums;

namespace PcMonitor.Domain.Services;

/// <summary>
/// Вычисляет топ-потребителей по метрике за набор снимков.
/// Чистая функция без побочных эффектов — легко тестируется.
/// </summary>
public static class TopConsumerCalculator
{
    public static IReadOnlyList<TopConsumer> Calculate(
        IReadOnlyList<ResourceSnapshot> snapshots,
        MetricType metric,
        TimeSpan samplingInterval,
        int topN = 5)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topN);

        if (snapshots.Count == 0)
            return Array.Empty<TopConsumer>();

        return snapshots
            .SelectMany(s => s.Processes)
            .GroupBy(p => p.Process)
            .Select(g =>
            {
                var values = g.Select(p => ExtractMetric(p, metric)).ToArray();
                var avg = values.Average();
                var peak = values.Max();
                var observed = TimeSpan.FromTicks(samplingInterval.Ticks * values.Length);

                return new TopConsumer(g.Key, metric, avg, peak, observed);
            })
            .OrderByDescending(t => t.AverageValue)
            .Take(topN)
            .ToArray();
    }

    private static double ExtractMetric(ProcessSnapshot p, MetricType metric) => metric switch
    {
        MetricType.Cpu => p.Cpu.Percent,
        MetricType.Memory => p.Memory.WorkingSetBytes,
        MetricType.DiskIo => p.DiskIo.TotalBytesPerSecond,
        MetricType.Gpu => p.Gpu.Percent,
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };
}