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
            logger.LogInformation("{Fuel}: {Allowed} spin(s) available", fuel, allowed);

            for (var i = 0; i < allowed; i++)
            {
                var result = await client.SpinAsync(fuel, ct);
                store.Record(options.AccountNumber, fuel, result);

                if (result.Success) logger.LogInformation("{Fuel}: won {Points} points", fuel, result.Points);
                else
                {
                    logger.LogWarning("{Fuel}: spin failed: {Error}", fuel, result.Error);
                    break;
                }
            }
        }
    }
}
