namespace Palkia.Core.Services;

public enum ServiceRunState
{
    Unknown,
    Started,
    Stopped,
    Starting,
    Stopping,
    Crashed,
}

public sealed record ServiceStatus(string ServiceId, ServiceRunState State, string RawOutput);

public sealed record ServiceActionResult(bool Succeeded, string Message);

/// <summary>
/// Abstraction over "control one named long-lived service". The only
/// implementation in phase 1 is the OpenRC client, but keeping this as an
/// interface means the resource broker (phase 2+) never needs to know how
/// services are actually started.
/// </summary>
public interface IServiceControlClient
{
    Task<ServiceStatus> GetStatusAsync(ManagedService service, CancellationToken ct = default);

    Task<ServiceActionResult> StartAsync(ManagedService service, CancellationToken ct = default);

    Task<ServiceActionResult> StopAsync(ManagedService service, CancellationToken ct = default);

    Task<ServiceActionResult> RestartAsync(ManagedService service, CancellationToken ct = default);
}
