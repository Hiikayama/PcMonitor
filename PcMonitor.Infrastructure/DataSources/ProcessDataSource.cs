using System.Diagnostics;
using System.Runtime.InteropServices;
using PcMonitor.Application.Abstractions;
using PcMonitor.Domain.Entities;
using PcMonitor.Domain.ValueObjects;

namespace PcMonitor.Infrastructure.DataSources;

public sealed class ProcessDataSource : IDataSource<IReadOnlyList<ProcessSnapshot>>
{
    // PROCESS_QUERY_INFORMATION | PROCESS_VM_READ
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private readonly Dictionary<int, ProcessSample> _previous = new();
    private readonly Dictionary<string, PerformanceCounter> _gpuCounters = new();
    private readonly object _gpuGate = new();
    private readonly object _gate = new();
    private long _firstSampleLogged; // для "одноразового" лога

    public string Name => "processes";

    public IReadOnlyList<ProcessSnapshot> Read()
    {
        var now = DateTimeOffset.UtcNow;
        var result = new List<ProcessSnapshot>();
        var currentPids = new HashSet<int>();

        // Диагностика: один раз за жизнь экземпляра — проверка admin
        LogPrivilegeOnce();

        var gpuByPid = ReadGpuByPid();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id <= 0) continue;
                currentPids.Add(p.Id);

                try
                {
                    var name = p.ProcessName;
                    var gpuPercent = gpuByPid.TryGetValue(p.Id, out var g)
                        ? Math.Clamp(g, 0, 100)
                        : 0;
                    var gpuUsage = new GpuProcessUsage(gpuPercent);
                    var (ioRead, ioWrite) = GetProcessIo(p.Id);
                    var (cpu, disk) = ComputeCpuAndDisk(
                        p.Id, p.TotalProcessorTime, ioRead, ioWrite, now);
                    var mem = new ProcessMemoryUsage(p.WorkingSet64);

                    result.Add(new ProcessSnapshot(
                        new ProcessIdentifier(p.Id, name), cpu, mem, disk, gpuUsage));
                }
                catch (Exception ex) when (
                    ex is InvalidOperationException
                        or System.ComponentModel.Win32Exception)
                {
                    // процесс завершился между GetProcesses и чтением
                }
            }
        }

        lock (_gate)
        {
            var toRemove = _previous.Keys.Except(currentPids).ToList();
            foreach (var pid in toRemove) _previous.Remove(pid);
        }

        return result;
    }

    private (CpuUsage Cpu, DiskIoRate Disk) ComputeCpuAndDisk(
    int pid,
    TimeSpan totalProcessorTime,
    long ioRead,
    long ioWrite,
    DateTimeOffset now)
    {
        lock (_gate)
        {
            // Первое наблюдение — только фиксируем baseline.
            if (!_previous.TryGetValue(pid, out var prev))
            {
                _previous[pid] = new ProcessSample(totalProcessorTime, now, ioRead, ioWrite);
                return (CpuUsage.Zero, DiskIoRate.Zero);
            }

            var elapsedSec = (now - prev.Timestamp).TotalSeconds;
            if (elapsedSec <= 0)
                return (CpuUsage.Zero, DiskIoRate.Zero);

            // === CPU ===
            var cpuMs = (totalProcessorTime - prev.CpuTime).TotalMilliseconds;
            var cores = Environment.ProcessorCount;
            var cpuPercent = Math.Clamp(cpuMs / (elapsedSec * 1000.0 * cores) * 100.0, 0, 100);
            var cpu = new CpuUsage(cpuPercent);

            // === Disk ===
            // Если prev ненулевой ИЛИ io ненулевой — считаем delta.
            // Иначе delta = 0 (нет данных для сравнения).
            DiskIoRate disk;
            if (prev.LastReadBytes == 0 && prev.LastWriteBytes == 0
                && ioRead == 0 && ioWrite == 0)
            {
                disk = DiskIoRate.Zero;
            }
            else
            {
                var readBps = Math.Max(0L, (long)((ioRead - prev.LastReadBytes) / elapsedSec));
                var writeBps = Math.Max(0L, (long)((ioWrite - prev.LastWriteBytes) / elapsedSec));
                disk = new DiskIoRate(readBps, writeBps);
            }

            // Одна запись — оба поля и timestamp обновляются синхронно.
            _previous[pid] = new ProcessSample(totalProcessorTime, now, ioRead, ioWrite);
            return (cpu, disk);
        }
    }

    private static (long ReadBytes, long WriteBytes) GetProcessIo(int pid)
    {
        // Пробуем сначала QUERY_INFORMATION, при отказе — LIMITED
        IntPtr handle = OpenProcess(PROCESS_QUERY_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        }

        if (handle == IntPtr.Zero)
            return (0, 0);

        try
        {
            if (!GetProcessIoCounters(handle, out var counters))
                return (0, 0);

            return ((long)counters.ReadTransferCount, (long)counters.WriteTransferCount);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ============================================================
    // Диагностика прав один раз за жизнь — не спамим лог
    // ============================================================

    private void LogPrivilegeOnce()
    {
        if (Interlocked.Exchange(ref _firstSampleLogged, 1) != 0) return;

        // Проверим, работает ли P/Invoke на СВОЁМ процессе.
        // Если да — права есть. Если нет — прав нет.
        var self = Environment.ProcessId;
        var (r, w) = GetProcessIo(self);
        Console.WriteLine($"[DIAG] GetProcessIo(self pid={self}) = read {r} B, write {w} B");
        Console.WriteLine($"[DIAG] IsElevated: {IsElevated()}");
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // ============================================================
    // P/Invoke
    // ============================================================

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    private Dictionary<int, double> ReadGpuByPid()
    {
        var result = new Dictionary<int, double>();
        if (_gpuCounters.Count == 0 && !TrySeedGpuCounters())
            return result;

        lock (_gpuGate)
        {
            foreach (var (instance, counter) in _gpuCounters)
            {
                var pid = ParseGpuInstancePid(instance);
                if (pid is null) continue;

                // NextValue() на прогретом счётчике даёт реальную дельту.
                var value = counter.NextValue();
                if (value <= 0) continue;

                result[pid.Value] = result.GetValueOrDefault(pid.Value) + value;
            }
        }

        return result;
    }

    /// <summary>
    /// Создаёт счётчики для всех GPU Engine инстансов и прогревает их
    /// (первый NextValue возвращает 0). Возвращает false, если категория
    /// недоступна (нет GPU-драйвера, отключены perf counters).
    /// </summary>
    private bool TrySeedGpuCounters()
    {
        lock (_gpuGate)
        {
            if (_gpuCounters.Count > 0) return true;

            try
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                var instances = category.GetInstanceNames();
                if (instances.Length == 0) return false;

                foreach (var instance in instances)
                {
                    var counter = new PerformanceCounter(
                        "GPU Engine", "Utilization Percentage", instance);
                    counter.NextValue();   // прогрев baseline
                    _gpuCounters[instance] = counter;
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GPU-PER-PROCESS] Counters unavailable: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Инстанс выглядит так: pid_1234_luid_0x00000000_..._engtype_3D.
    /// Извлекаем 1234.
    /// </summary>
    private static int? ParseGpuInstancePid(string instance)
    {
        const string prefix = "pid_";
        if (!instance.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var start = prefix.Length;
        var end = instance.IndexOf('_', start);
        if (end < 0) return null;

        return int.TryParse(instance.AsSpan(start, end - start), out var pid)
            ? pid
            : null;
    }

    private record ProcessSample(
        TimeSpan CpuTime,
        DateTimeOffset Timestamp,
        long LastReadBytes,
        long LastWriteBytes);
}