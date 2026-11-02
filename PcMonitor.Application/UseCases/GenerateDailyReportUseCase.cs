using PcMonitor.Application.Abstractions;
using PcMonitor.Application.DTOs;
using PcMonitor.Domain.Entities;
using PcMonitor.Domain.Enums;
using PcMonitor.Domain.Events;
using PcMonitor.Domain.Ports;
using PcMonitor.Domain.Services;

namespace PcMonitor.Application.UseCases
{
    public sealed class GenerateDailyReportUseCase
    {
        private readonly IResourceSnapshotRepository _snapshotRepo;
        private readonly ISystemEventRepository _eventRepo;
        private readonly IDailyReportRepository _reportRepo;
        private readonly IAnalysisPipeline _pipeline;
        private readonly IReportBuilder _reportBuilder;
        private readonly IReportExporter _exporter;
        private readonly IEventPublisher _events;
        private readonly IReportCalendar _calendar;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IClock _clock;

        public GenerateDailyReportUseCase(
            IResourceSnapshotRepository snapshotRepo,
            ISystemEventRepository eventRepo,
            IDailyReportRepository reportRepo,
            IAnalysisPipeline pipeline,
            IReportBuilder reportBuilder,
            IReportExporter exporter,
            IEventPublisher events,
            IReportCalendar calendar,
            IUnitOfWork unitOfWork,
            IClock clock)
        {
            _snapshotRepo = snapshotRepo;
            _eventRepo = eventRepo;
            _reportRepo = reportRepo;
            _pipeline = pipeline;
            _reportBuilder = reportBuilder;
            _exporter = exporter;
            _events = events;
            _calendar = calendar;
            _unitOfWork = unitOfWork;
            _clock = clock;
        }

        public async Task<DailyReport> ExecuteAsync(DateOnly date, CancellationToken ct = default)
        {
            var period = _calendar.GetDayRange(date);

            // 1. Загрузить данные за период
            var snapshots = await _snapshotRepo.GetRangeAsync(period, ct);
            var systemEvents = await _eventRepo.GetRangeAsync(period, ct);

            // 2. Прогнать через pipeline анализа и обогащения
            var context = new AnalysisContext(period, snapshots, systemEvents);
            var anomalies = await _pipeline.RunAsync(context, ct);

            // 3. Вычислить топ-потребителей (domain service)
            var topConsumers = new List<TopConsumer>();
            foreach (var metric in new[] { MetricType.Cpu, MetricType.Memory, MetricType.DiskIo, MetricType.Gpu })
            {
                topConsumers.AddRange(TopConsumerCalculator.Calculate(
                    snapshots,
                    metric,
                    samplingInterval: TimeSpan.FromSeconds(10),
                    topN: 5));
            }

            // 4. Собрать отчёт (агрегат domain)
            var report = await _reportBuilder.BuildAsync(
                date, snapshots, anomalies, topConsumers, ct);

            // 5. Сохранить
            await _reportRepo.SaveAsync(report, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            // 6. Экспортировать (PDF/HTML/email)
            await _exporter.ExportAsync(report, snapshots, ct);

            // 7. Опубликовать событие
            await _events.PublishAsync(
                new DailyReportGenerated(report.Id, date, report.Anomalies.Count, _clock.UtcNow),
                ct);

            return report;
        }
    }
}
