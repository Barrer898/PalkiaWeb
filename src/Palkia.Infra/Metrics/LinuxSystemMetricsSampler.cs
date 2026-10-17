using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Palkia.Core.Metrics;

namespace Palkia.Infra.Metrics;

/// <summary>
/// Runs for the lifetime of the app as a hosted background service, taking a
/// fresh reading every <see cref="SampleInterval"/> and caching it for
/// <see cref="GetLatest"/> to hand out instantly. Deliberately never reads
/// on-demand from a request/render - that would mean every dashboard render
/// pays the cost (and, for CPU%, the delay) of a fresh sample.
///
/// Registered as a singleton exposed through two service types (see
/// Program.cs) - one instance, injectable as ISystemMetricsProvider for
/// components to read, and separately started/stopped by the host as an
/// IHostedService.
/// </summary>
public sealed class LinuxSystemMetricsSampler : BackgroundService, ISystemMetricsProvider
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NvidiaSmiTimeout = TimeSpan.FromSeconds(2);

    private readonly ILogger<LinuxSystemMetricsSampler> _logger;
    private volatile SystemMetricsSnapshot _latest = SystemMetricsSnapshot.Empty;
    private (long Idle, long Total)? _lastCpuSample;
    private bool _nvidiaSmiMissingLogged;

    public LinuxSystemMetricsSampler(ILogger<LinuxSystemMetricsSampler> logger)
    {
        _logger = logger;
    }

    public SystemMetricsSnapshot GetLatest() => _latest;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SampleInterval);
        do
        {
            try
            {
                await SampleOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to sample system metrics this cycle - keeping previous values.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SampleOnceAsync(CancellationToken ct)
    {
        var cpuPercent = SampleCpuPercent();
        var (ramUsedMb, ramTotalMb, swapFreeMb, swapTotalMb) = SampleMemory();
        var (gpuPercent, gpuUsedMb, gpuTotalMb) = await SampleGpuAsync(ct);

        _latest = new SystemMetricsSnapshot(
            CpuUsagePercent: cpuPercent,
            RamUsedMb: ramUsedMb,
            RamTotalMb: ramTotalMb,
            SwapFreeMb: swapFreeMb,
            SwapTotalMb: swapTotalMb,
            GpuUsagePercent: gpuPercent,
            GpuVramUsedMb: gpuUsedMb,
            GpuVramTotalMb: gpuTotalMb,
            SampledAtUtc: DateTimeOffset.UtcNow);
    }

    /// <summary>First line of /proc/stat: "cpu  user nice system idle iowait irq softirq ...".
    /// Needs two samples to produce a percentage, so returns null on the very
    /// first call after startup.</summary>
    private double? SampleCpuPercent()
    {
        if (!File.Exists("/proc/stat"))
        {
            return null;
        }

        var line = File.ReadLines("/proc/stat").FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1) // drop the "cpu" label
            .Select(p => long.TryParse(p, out var v) ? v : 0L)
            .ToArray();

        if (values.Length < 4)
        {
            return null;
        }

        var idle = values[3] + (values.Length > 4 ? values[4] : 0); // idle + iowait
        var total = values.Sum();

        double? percent = null;
        if (_lastCpuSample is { } last)
        {
            var idleDelta = idle - last.Idle;
            var totalDelta = total - last.Total;
            if (totalDelta > 0)
            {
                percent = Math.Clamp(100.0 * (1.0 - (double)idleDelta / totalDelta), 0, 100);
            }
        }

        _lastCpuSample = (idle, total);
        return percent;
    }

    private static (long? RamUsedMb, long? RamTotalMb, long? SwapFreeMb, long? SwapTotalMb) SampleMemory()
    {
        if (!File.Exists("/proc/meminfo"))
        {
            return (null, null, null, null);
        }

        long? memTotalKb = null;
        long? memAvailableKb = null;
        long? swapFreeKb = null;
        long? swapTotalKb = null;

        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (memTotalKb is not null && memAvailableKb is not null && swapFreeKb is not null && swapTotalKb is not null)
            {
                break;
            }

            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                memTotalKb = ParseKbValue(line);
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                // Not MemFree: on Linux, MemFree alone excludes reclaimable
                // page cache/buffers and so wildly overstates "used" memory.
                // MemAvailable is the kernel's own best estimate of what a
                // new process could actually get without swapping.
                memAvailableKb = ParseKbValue(line);
            }
            else if (line.StartsWith("SwapTotal:", StringComparison.Ordinal))
            {
                swapTotalKb = ParseKbValue(line);
            }
            else if (line.StartsWith("SwapFree:", StringComparison.Ordinal))
            {
                swapFreeKb = ParseKbValue(line);
            }
        }

        long? ramUsedKb = (memTotalKb is { } total && memAvailableKb is { } avail) ? total - avail : null;

        return (ramUsedKb / 1024, memTotalKb / 1024, swapFreeKb / 1024, swapTotalKb / 1024);
    }

    private static long? ParseKbValue(string line)
    {
        // e.g. "SwapFree:       16777212 kB"
        var parts = line.Split(':', 2);
        if (parts.Length < 2)
        {
            return null;
        }

        var numberPart = parts[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return long.TryParse(numberPart, out var kb) ? kb : null;
    }

    /// <summary>Absent nvidia-smi (no NVIDIA driver, or not an NVIDIA box at
    /// all) is a normal, expected case here - not an error - so this returns
    /// all-null quietly rather than throwing, and only logs once.</summary>
    private async Task<(double? Percent, long? UsedMb, long? TotalMb)> SampleGpuAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "nvidia-smi",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--query-gpu=utilization.gpu,memory.used,memory.total");
        psi.ArgumentList.Add("--format=csv,noheader,nounits");

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            if (!_nvidiaSmiMissingLogged)
            {
                _logger.LogInformation(
                    "nvidia-smi not available ({Reason}) - GPU metrics will show as unavailable. " +
                    "This is expected on a box without the NVIDIA driver installed.", ex.Message);
                _nvidiaSmiMissingLogged = true;
            }
            return (null, null, null);
        }

        using var _ = process;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(NvidiaSmiTimeout);

        string output;
        try
        {
            output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            _logger.LogWarning("nvidia-smi timed out after {Timeout}s.", NvidiaSmiTimeout.TotalSeconds);
            return (null, null, null);
        }

        var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstLine is null)
        {
            return (null, null, null);
        }

        var parts = firstLine.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            return (null, null, null);
        }

        double? percent = double.TryParse(parts[0], out var p) ? p : null;
        long? usedMb = long.TryParse(parts[1], out var u) ? u : null;
        long? totalMb = long.TryParse(parts[2], out var t) ? t : null;

        return (percent, usedMb, totalMb);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best-effort only
        }
    }
}
