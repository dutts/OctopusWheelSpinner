namespace OctopusWheelSpinner;

/// <summary>
/// Octopus API reachability for /health, from a background probe cached briefly so frequent health checks don't
/// hammer the API and never wait on it.
/// </summary>
public sealed class OctopusHealth(IHostApplicationLifetime lifetime, ILogger<OctopusHealth> logger)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly Lock _lock = new();
    private Task? _probe;
    private DateTimeOffset _checkedAt;
    private ConnectivityCheck? _octopus;

    /// <summary>
    /// Returns the last probe result straight away (null until the first one finishes), starting a fresh probe in the
    /// background if it's stale.
    /// </summary>
    public ConnectivityCheck? Current()
    {
        lock (_lock)
        {
            if (DateTimeOffset.UtcNow - _checkedAt >= CacheFor && _probe is null or { IsCompleted: true })
                _probe = Task.Run(() => ProbeAsync(lifetime.ApplicationStopping), CancellationToken.None);
            return _octopus;
        }
    }

    private async Task ProbeAsync(CancellationToken ct)
    {
        ConnectivityCheck result;
        try { result = await OctopusClient.CheckConnectivityAsync(ct); }
        catch (Exception ex) { result = new ConnectivityCheck(Connectivity.Unreachable, ex.GetType().Name); }

        lock (_lock)
        {
            // Log only changes; a first check that finds Octopus reachable logs nothing
            if (result.Status != (_octopus?.Status ?? Connectivity.Reachable))
            {
                if (result.Status == Connectivity.Reachable)
                    logger.LogInformation("Octopus API reachable again");
                else
                    logger.LogWarning("Octopus API {Connectivity}: {Error}", result.Status.ToString().ToLowerInvariant(), result.Error);
            }

            _octopus = result;
            _checkedAt = DateTimeOffset.UtcNow;
        }
    }
}
