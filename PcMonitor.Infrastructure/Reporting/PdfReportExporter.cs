using PcMonitor.Application.Abstractions;
using PcMonitor.Domain.Entities;
using PcMonitor.Domain.Enums;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Document = QuestPDF.Fluent.Document;

namespace PcMonitor.Infrastructure.Reporting;

public sealed class PdfReportExporter : IReportExporter
{
    private readonly IChartRenderer _charts;
    private readonly string _outputDirectory;

    public PdfReportExporter(IChartRenderer charts, string outputDirectory)
    {
        _charts = charts;
        _outputDirectory = outputDirectory;
    }

    public async Task ExportAsync(
    DailyReport report,
    IReadOnlyList<ResourceSnapshot> snapshots,
    CancellationToken ct = default)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        // Pre-render графиков — до создания PDF-документа.
        byte[]? cpuChart = snapshots.Count > 0
            ? _charts.Render(DailyReportChartFactory.CpuTimeline(snapshots))
            : null;
        byte[]? memChart = snapshots.Count > 0
            ? _charts.Render(DailyReportChartFactory.MemoryTimeline(snapshots))
            : null;
        byte[]? gpuChart = snapshots.Any(s => s.Gpu is not null)
            ? _charts.Render(DailyReportChartFactory.GpuTimeline(snapshots))
            : null;

        var pdfBytes = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);

                page.Header().Column(header =>
                {
                    header.Item().Text($"Отчёт за {report.Date:yyyy-MM-dd}")
                        .FontSize(20).Bold();

                    // Явно указываем часовой пояс, чтобы не было двусмысленности.
                    var now = DateTimeOffset.UtcNow.ToLocalTime();
                    header.Item().Text(
                        $"Время указано в локальном часовом поясе (UTC{now.Offset:hh\\:mm})")
                        .FontSize(9).FontColor(Colors.Grey.Darken1);
                });

                page.Content().Column(col =>
                {
                    // Счётчики
                    col.Item().Text($"Снимков проанализировано: {report.SnapshotsAnalyzed}");
                    col.Item().Text($"Критичных аномалий: {report.CriticalAnomalyCount}");
                    col.Item().Text($"Предупреждений: {report.WarningAnomalyCount}");

                    var gpuSummary = BuildGpuSummary(snapshots);
                    if (gpuSummary is not null)
                    {
                        col.Item().PaddingTop(4).Text(gpuSummary).FontSize(9).Italic();
                    }

                    // Графики
                    if (cpuChart is not null)
                    {
                        col.Item().PaddingTop(15).Text("CPU за сутки:")
                            .FontSize(14).Bold();
                        col.Item().Image(cpuChart).FitWidth();
                    }

                    if (memChart is not null)
                    {
                        col.Item().PaddingTop(10).Text("RAM за сутки:")
                            .FontSize(14).Bold();
                        col.Item().Image(memChart).FitWidth();
                    }

                    if (gpuChart is not null)
                    {
                        col.Item().PaddingTop(10).Text("GPU за сутки:")
                            .FontSize(14).Bold();
                        col.Item().Image(gpuChart).FitWidth();
                    }

                    // Топ-потребители
                    col.Item().PaddingTop(15).Text("Топ-потребители:")
                        .FontSize(14).Bold();

                    foreach (var metric in new[] { MetricType.Cpu, MetricType.Memory, MetricType.DiskIo, MetricType.Gpu })
                    {
                        var tops = report.GetTopConsumers(metric);
                        if (tops.Count == 0) continue;

                        col.Item().PaddingTop(8).Text($"По {MetricLabel(metric)}:")
                            .FontSize(12).Bold();

                        foreach (var top in tops)
                        {
                            col.Item().Text(
                                $"  {top.Process} — ср. {FormatValue(top.Metric, top.AverageValue)}, " +
                                $"пик {FormatValue(top.Metric, top.PeakValue)}");
                        }
                    }

                    // Аномалии
                    col.Item().PaddingTop(15).Text("Аномалии:")
                        .FontSize(14).Bold();
                    if (report.Anomalies.Count == 0)
                    {
                        col.Item().Text("Аномалий не обнаружено.").Italic();
                    }
                    else
                    {
                        foreach (var a in report.Anomalies.Take(20))
                        {
                            // Время — в локальном поясе, с явным указанием UTC-offset.
                            var localTime = a.OccurredAt.ToLocalTime();
                            var offsetLabel = $"UTC{localTime.Offset:hh\\:mm}";

                            // Виновник — если известен.
                            var culpritText = a.Culprit is not null
                                ? $" Виновник: {a.Culprit}."
                                : " Виновник не определён.";

                            col.Item().Text(
                                $"{localTime:HH:mm:ss} [{SeverityLabel(a.Severity)}] " +
                                $"{MetricLabel(a.Metric)}: {a.Reason}{culpritText}");

                            // Контекст в момент аномалии — топ-3 процесса по той же метрике.
                            var ctx = FindSnapshotAt(snapshots, a.OccurredAt);
                            if (ctx is not null)
                            {
                                var top3 = GetTop3ForMetric(ctx, a.Metric);
                                if (top3.Count > 0)
                                {
                                    col.Item().PaddingLeft(15).Text(
                                        $"  Нагрузка в этот момент: CPU {ctx.Cpu.Percent:F1}%, " +
                                        $"RAM {ctx.Memory.UtilizationPercent:F1}%, " +
                                        $"диск {FormatBytes(ctx.DiskIo.TotalBytesPerSecond)}/с")
                                        .Italic().FontSize(9);

                                    foreach (var p in top3)
                                    {
                                        col.Item().PaddingLeft(25).Text(
                                            $"• {p.Process} — {FormatValue(a.Metric, ExtractMetric(p, a.Metric))}")
                                            .Italic().FontSize(9);
                                    }
                                }
                            }
                        }
                    }
                });
            });
        }).GeneratePdf();

        var path = Path.Combine(_outputDirectory, $"report-{report.Date:yyyy-MM-dd}.pdf");
        Directory.CreateDirectory(_outputDirectory);
        await File.WriteAllBytesAsync(path, pdfBytes, ct);
        Console.WriteLine($"PDF written: {path} ({pdfBytes.Length:N0} bytes)");
    }

    // В PdfReportExporter.cs
    private static string MetricLabel(MetricType m) => m switch
    {
        MetricType.Cpu => "CPU",
        MetricType.Memory => "RAM",
        MetricType.DiskIo => "Диск",
        MetricType.Gpu => "GPU",
        _ => m.ToString()
    };

    private static string SeverityLabel(SeverityLevel s) => s switch
    {
        SeverityLevel.Info => "Инфо",
        SeverityLevel.Warning => "Внимание",
        SeverityLevel.Critical => "Критично",
        _ => s.ToString()
    };

    private static string FormatValue(MetricType metric, double value) => metric switch
    {
        MetricType.Cpu => $"{value:F1}%",
        MetricType.Memory => FormatBytes((long)value),
        MetricType.Gpu => $"{value:F1}%",
        MetricType.DiskIo => FormatBytes((long)value) + "/с",
        _ => $"{value:F1}"
    };

    private static string FormatBytes(long bytes)
    {
        const long KB = 1024L, MB = KB * 1024, GB = MB * 1024;
        return bytes switch
        {
            >= GB => $"{bytes / (double)GB:F1} ГБ",
            >= MB => $"{bytes / (double)MB:F1} МБ",
            >= KB => $"{bytes / (double)KB:F1} КБ",
            _ => $"{bytes} Б"
        };
    }

    /// <summary>
    /// Находит снимок, ближайший по времени к заданному моменту.
    /// Линейный поиск — приемлемо, потому что вызывается для <20 аномалий
    /// при <10 000 снимков за день.
    /// </summary>
    private static ResourceSnapshot? FindSnapshotAt(
        IReadOnlyList<ResourceSnapshot> snapshots,
        DateTimeOffset target)
    {
        if (snapshots.Count == 0) return null;

        // Снимки отсортированы по Timestamp (это гарантирует репозиторий
        // через OrderBy(s => s.Timestamp)). Используем бинарный поиск.
        var lo = 0;
        var hi = snapshots.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (snapshots[mid].Timestamp < target)
                lo = mid + 1;
            else
                hi = mid;
        }

        // Ближайший — либо lo, либо lo-1.
        var best = snapshots[lo];
        if (lo > 0)
        {
            var prev = snapshots[lo - 1];
            if (Math.Abs((prev.Timestamp - target).TotalSeconds)
                < Math.Abs((best.Timestamp - target).TotalSeconds))
                best = prev;
        }
        return best;
    }

    private static IReadOnlyList<ProcessSnapshot> GetTop3ForMetric(
        ResourceSnapshot snapshot,
        MetricType metric)
    {
        return metric switch
        {
            MetricType.Cpu => snapshot.Processes
                .OrderByDescending(p => p.Cpu.Percent).Take(3).ToArray(),
            MetricType.Memory => snapshot.Processes
                .OrderByDescending(p => p.Memory.WorkingSetBytes).Take(3).ToArray(),
            MetricType.DiskIo => snapshot.Processes
                .OrderByDescending(p => p.DiskIo.TotalBytesPerSecond).Take(3).ToArray(),
            _ => Array.Empty<ProcessSnapshot>()
        };
    }

    private static double ExtractMetric(ProcessSnapshot p, MetricType metric) => metric switch
    {
        MetricType.Cpu => p.Cpu.Percent,
        MetricType.Memory => p.Memory.WorkingSetBytes,
        MetricType.DiskIo => p.DiskIo.TotalBytesPerSecond,
        _ => 0
    };

    /// <summary>
    /// Сводка по GPU за период. null — если ни одного снимка с GPU не было
    /// (Intel fallback, RDP-сессия, машина без дискретной карты).
    /// </summary>
    private static string? BuildGpuSummary(IReadOnlyList<ResourceSnapshot> snapshots)
    {
        var gpuSnapshots = snapshots.Where(s => s.Gpu is not null).ToList();
        if (gpuSnapshots.Count == 0) return null;

        var utils = gpuSnapshots.Select(s => s.Gpu!.UtilizationPercent).ToList();
        var deviceName = gpuSnapshots[0].Gpu!.DeviceName;

        var temps = gpuSnapshots
            .Where(s => s.Gpu!.TemperatureCelsius.HasValue)
            .Select(s => s.Gpu!.TemperatureCelsius!.Value)
            .ToList();

        var vrams = gpuSnapshots
            .Where(s => s.Gpu!.MemoryUsedBytes.HasValue)
            .Select(s => s.Gpu!.MemoryUsedBytes!.Value)
            .ToList();

        var parts = new List<string>
    {
        $"GPU: {deviceName}",
        $"загрузка — ср. {utils.Average():F1}%, пик {utils.Max():F1}%"
    };

        if (temps.Count > 0)
            parts.Add($"температура — ср. {temps.Average():F1}°C, макс. {temps.Max():F0}°C");
        else
            parts.Add("температура недоступна");

        if (vrams.Count > 0)
        {
            var maxVram = vrams.Max();
            var totalVram = gpuSnapshots
                .Where(s => s.Gpu!.MemoryTotalBytes.HasValue)
                .Select(s => s.Gpu!.MemoryTotalBytes!.Value)
                .FirstOrDefault();

            var vramText = $"VRAM пик — {FormatBytes(maxVram)}";
            if (totalVram > 0)
                vramText += $" из {FormatBytes(totalVram)}";
            parts.Add(vramText);
        }

        // Топ-процесс по GPU — если есть данные
        var topGpuProcess = snapshots
            .Where(s => s.Gpu is not null)
            .SelectMany(s => s.Processes)
            .Where(p => p.Gpu.Percent >= 0.5)
            .GroupBy(p => p.Process)
            .Select(g => new
            {
                Process = g.Key,
                Avg = g.Average(p => p.Gpu.Percent),
                Peak = g.Max(p => p.Gpu.Percent)
            })
            .OrderByDescending(x => x.Avg)
            .FirstOrDefault();

        if (topGpuProcess is not null)
        {
            parts.Add(
                $"горячий процесс — {topGpuProcess.Process} " +
                $"(ср. {topGpuProcess.Avg:F1}%, пик {topGpuProcess.Peak:F1}%)");
        }

        return string.Join(" • ", parts) + ".";
    }
}