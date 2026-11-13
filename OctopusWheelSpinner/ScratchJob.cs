using Microsoft.Extensions.Logging;
using Quartz;

namespace OctopusWheelSpinner;

[DisallowConcurrentExecution]
public sealed class ScratchJob(
    OctopusClient client,
    SpinStore store,
    OctopusOptions options,
    Notifier notifier,
    ILogger<ScratchJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken ct)
    {
        try
        {
            var active = await client.GetActiveScratchcardAsync(ct);
            if (active.Session is not { } session)
            {
                logger.LogInformation("No scratch card session is running");
                return;
            }
            if (active.AlreadyScratched)
            {
                logger.LogInformation("Scratch card for session {Session} has already been scratched", session.ExternalReference);
                return;
            }

            var result = await client.ScratchAsync(session.ExternalReference, ct);
            store.RecordScratch(options.AccountNumber, session.ExternalReference, result);

            if (!result.Success)
            {
                logger.LogWarning("Scratch card failed for session {Session}: {Error}", session.ExternalReference, result.Error);
                await notifier.SendAsync("Octopus scratch card problem", $"Scratch failed ({result.Error})", "high", "warning", ct);
            }
            else if (result.Prize is null)
            {
                logger.LogInformation("Scratch card scratched with no prize ({Status})", result.Status);
                await notifier.SendAsync("Octopus scratch card scratched", "No prize this week", "low", "ticket", ct);
            }
            else
            {
                logger.LogInformation("Scratch card won: {Prize} ({Status})", result.Prize, result.Status);
                // Offers are not claimed automatically; stamps are awarded straight away
                var body = result.Status == "PRIZE_NOT_YET_CLAIMED"
                    ? $"Won {result.Prize}. Claim it in the Octoplus tab of the Octopus app."
                    : $"Won {result.Prize}";
                await notifier.SendAsync("Octopus scratch card: you won", body, "default", "tada", ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await notifier.SendAsync("Octopus Wheel Spinner error", ex.Message, "high", "x", ct);
            throw;
        }
    }
}
