using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Palkia.Core.Services;

namespace Palkia.Infra.OpenRc;

public sealed class OpenRcClientOptions
{
    /// <summary>
    /// Absolute path to the root-owned allow-listed wrapper script
    /// (see deploy/palkia-rc-ctl.sh). Never point this at a raw shell.
    /// </summary>
    public string WrapperPath { get; set; } = "/usr/local/sbin/palkia-rc-ctl";

    /// <summary>Command used to invoke the wrapper with elevated rights.</summary>
    public string SudoPath { get; set; } = "/usr/bin/sudo";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);
}

public sealed class OpenRcServiceClient : IServiceControlClient
{
    private readonly OpenRcClientOptions _options;
    private readonly ILogger<OpenRcServiceClient> _logger;

    public OpenRcServiceClient(OpenRcClientOptions options, ILogger<OpenRcServiceClient> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<ServiceStatus> GetStatusAsync(ManagedService service, CancellationToken ct = default)
    {
        var (exitCode, output) = await RunWrapperAsync(service.RcName, "status", ct);
        var state = ParseState(exitCode, output);
        return new ServiceStatus(service.Id, state, output);
    }

    public Task<ServiceActionResult> StartAsync(ManagedService service, CancellationToken ct = default) =>
        RunActionAsync(service, "start", ct);

    public Task<ServiceActionResult> StopAsync(ManagedService service, CancellationToken ct = default) =>
        RunActionAsync(service, "stop", ct);

    public Task<ServiceActionResult> RestartAsync(ManagedService service, CancellationToken ct = default) =>
        RunActionAsync(service, "restart", ct);

    private async Task<ServiceActionResult> RunActionAsync(ManagedService service, string action, CancellationToken ct)
    {
        var (exitCode, output) = await RunWrapperAsync(service.RcName, action, ct);
        var succeeded = exitCode == 0;

        if (!succeeded)
        {
            _logger.LogWarning(
                "OpenRC action {Action} on {Service} exited {ExitCode}: {Output}",
                action, service.RcName, exitCode, output);
        }

        return new ServiceActionResult(succeeded, output);
    }

    /// <summary>
    /// Invokes: sudo -n /usr/local/sbin/palkia-rc-ctl &lt;rcName&gt; &lt;action&gt;
    /// Arguments are passed as an argument list (ArgumentList), never
    /// concatenated into a shell string, so there is no injection surface
    /// even though rcName/action ultimately come from our own hardcoded
    /// ManagedServiceCatalog rather than raw user input.
    /// </summary>
    private async Task<(int ExitCode, string Output)> RunWrapperAsync(string rcName, string action, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.SudoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // -n = non-interactive: fail immediately rather than prompting for a
        // password if the sudoers NOPASSWD rule isn't set up correctly.
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(_options.WrapperPath);
        psi.ArgumentList.Add(rcName);
        psi.ArgumentList.Add(action);

        using var process = new Process { StartInfo = psi };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.Timeout);

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return (-1, $"Timed out after {_options.Timeout.TotalSeconds}s waiting for '{rcName} {action}'.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}".Trim();

        return (process.ExitCode, combined.Trim());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // best-effort only
        }
    }

    private static ServiceRunState ParseState(int exitCode, string output)
    {
        var lower = output.ToLowerInvariant();

        if (lower.Contains("crashed")) return ServiceRunState.Crashed;
        if (lower.Contains("starting")) return ServiceRunState.Starting;
        if (lower.Contains("stopping")) return ServiceRunState.Stopping;
        if (lower.Contains("started")) return ServiceRunState.Started;
        if (lower.Contains("stopped")) return ServiceRunState.Stopped;

        // OpenRC's rc-service returns 0 for "started" and non-zero for
        // "stopped" in some versions without a clear string - fall back to
        // exit code when the text match is inconclusive.
        return exitCode == 0 ? ServiceRunState.Started : ServiceRunState.Unknown;
    }
}
