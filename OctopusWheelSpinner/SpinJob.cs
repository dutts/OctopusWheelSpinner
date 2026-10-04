using Microsoft.Extensions.Logging;
using Quartz;

namespace OctopusWheelSpinner;

[DisallowConcurrentExecution]
public sealed class SpinJob(
    OctopusClient client,
    SpinStore store,
    OctopusOptions options,
    Notifier notifier,
    ILogger<SpinJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken ct)
    {
        var lines = new List<string>();
        var failed = false;

        try
        {
            foreach (var fuel in Enum.GetValues<FuelType>())
            {
                var allowed = await client.GetSpinsAllowedAsync(fuel, ct);
                logger.LogInformation("Spins available for {Fuel}: {SpinsAllowed}", fuel, allowed);

                for (var i = 0; i < allowed; i++)
                {
                    var result = await client.SpinAsync(fuel, ct);
                    store.Record(options.AccountNumber, fuel, result);

                    if (result.Success)
                    {
                        logger.LogInformation("Spin won for {Fuel}: {PointsWon} points", fuel, result.Points);
                        lines.Add($"{fuel}: won {result.Points} points");
                    }
                    else
                    {
                        logger.LogWarning("Spin failed for {Fuel}: {Error}", fuel, result.Error);
                        lines.Add($"{fuel}: spin failed ({result.Error})");
                        failed = true;
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await notifier.SendAsync("Octopus Wheel Spinner error", ex.Message, "high", "x", ct);
            throw;
        }

        // Nothing to report when no spins were available
        if (lines.Count == 0) return;

        await notifier.SendAsync(
            failed ? "Octopus wheel spin problem" : "Octopus wheel spun",
            string.Join('\n', lines),
            failed ? "high" : "default",
            failed ? "warning" : "tada",
            ct);
    }
}
