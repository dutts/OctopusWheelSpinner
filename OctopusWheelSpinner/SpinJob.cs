using Microsoft.Extensions.Logging;
using Quartz;

namespace OctopusWheelSpinner;

[DisallowConcurrentExecution]
public sealed class SpinJob(
    OctopusClient client,
    SpinStore store,
    OctopusOptions options,
    ILogger<SpinJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken ct)
    {
        foreach (var fuel in Enum.GetValues<FuelType>())
        {
            var allowed = await client.GetSpinsAllowedAsync(fuel, ct);
            logger.LogInformation("Spins available for {Fuel}: {SpinsAllowed}", fuel, allowed);

            for (var i = 0; i < allowed; i++)
            {
                var result = await client.SpinAsync(fuel, ct);
                store.Record(options.AccountNumber, fuel, result);

                if (result.Success) logger.LogInformation("Spin won for {Fuel}: {PointsWon} points", fuel, result.Points);
                else
                {
                    logger.LogWarning("Spin failed for {Fuel}: {Error}", fuel, result.Error);
                    break;
                }
            }
        }
    }
}
