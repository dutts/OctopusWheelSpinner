using OctopusWheelSpinner.Tests.Support;
using Xunit;

namespace OctopusWheelSpinner.Tests;

public sealed class ScratchTests(AppImageFixture fixture)
{
    private const string SessionQuery = "octoplusActiveScratchcardData";
    private const string ScratchMutation = "scratchOctoplusScratchcard";

    private readonly Guid _session = Guid.NewGuid();

    private Task<AppHarness> StartAsync(Func<MockOctopus, Task> arrange) =>
        AppHarness.StartAsync(fixture, arrange, scratchCron: AppHarness.EverySecond);

    // Waits for more session queries than have been seen, so "nothing happened" is asserted after several schedule ticks
    private static async Task WaitForMoreQueriesAsync(AppHarness app, int count = 3)
    {
        var seen = (await app.BackendRequestsAsync(SessionQuery)).Count;
        await AppHarness.WaitUntilAsync(async () => (await app.BackendRequestsAsync(SessionQuery)).Count >= seen + count,
            $"{count} more scratch card session queries");
    }

    private static Task WaitForAttemptsAsync(AppHarness app, int count = 1) =>
        AppHarness.WaitUntilAsync(async () => (await app.GetScratchesAsync()).Attempts.Count >= count, "a recorded scratch attempt");

    [Fact]
    public async Task No_active_session_does_nothing()
    {
        await using var app = await StartAsync(o => o.StubNoSessionAsync());

        await WaitForMoreQueriesAsync(app);

        Assert.Empty(await app.BackendRequestsAsync(ScratchMutation));
        Assert.Empty((await app.GetScratchesAsync()).Attempts);
        Assert.Empty(await app.NtfyRequestsAsync());
    }

    [Fact]
    public async Task Offer_prize_is_scratched_once_and_recorded()
    {
        await using var app = await StartAsync(o => o.StubScratchableAsync(
            _session, GraphQl.ScratchMutation(GraphQl.OfferCard()), GraphQl.OfferCard()));

        await WaitForAttemptsAsync(app);
        await WaitForMoreQueriesAsync(app);

        var mutations = await app.BackendRequestsAsync(ScratchMutation);
        var mutation = Assert.Single(mutations);
        Assert.Contains(_session.ToString(), mutation.Body);
        Assert.Contains(AppHarness.AccountNumber, mutation.Body);
        Assert.Equal("test-token", mutation.Header("Authorization"));

        var scratches = await app.GetScratchesAsync();
        var attempt = Assert.Single(scratches.Attempts);
        Assert.True(attempt.Success);
        Assert.Equal("PRIZE_NOT_YET_CLAIMED", attempt.Status);
        Assert.Equal("Free coffee", attempt.Prize);
        Assert.Equal(_session, attempt.SessionReference);
        Assert.Equal(new ScratchSummaryDto(1, 1, 0, 1), scratches.Summary);

        var ntfy = Assert.Single(await app.NtfyRequestsAsync());
        Assert.Equal("Octopus scratch card: you won", ntfy.Header("Title"));
        Assert.Contains("Free coffee", ntfy.Body);
        Assert.Contains("Octoplus tab", ntfy.Body);
    }

    [Fact]
    public async Task Stamps_prize_is_recorded_without_a_claim_hint()
    {
        await using var app = await StartAsync(o => o.StubScratchableAsync(
            _session, GraphQl.ScratchMutation(GraphQl.StampsCard(50)), GraphQl.StampsCard(50)));

        await WaitForAttemptsAsync(app);

        var attempt = Assert.Single((await app.GetScratchesAsync()).Attempts);
        Assert.True(attempt.Success);
        Assert.Equal("PRIZE_CLAIMED", attempt.Status);
        Assert.Equal("50 stamps", attempt.Prize);

        await AppHarness.WaitUntilAsync(async () => (await app.NtfyRequestsAsync()).Count >= 1, "the ntfy notification");
        var ntfy = Assert.Single(await app.NtfyRequestsAsync());
        Assert.Equal("Octopus scratch card: you won", ntfy.Header("Title"));
        Assert.Contains("50 stamps", ntfy.Body);
        Assert.DoesNotContain("Octoplus tab", ntfy.Body);
    }

    [Fact]
    public async Task Card_that_did_not_win_is_recorded_with_a_low_priority_notification()
    {
        await using var app = await StartAsync(o => o.StubScratchableAsync(
            _session, GraphQl.ScratchMutation(GraphQl.NoWinCard()), GraphQl.NoWinCard()));

        await WaitForAttemptsAsync(app);

        var attempt = Assert.Single((await app.GetScratchesAsync()).Attempts);
        Assert.True(attempt.Success);
        Assert.Equal("DID_NOT_WIN", attempt.Status);
        Assert.Null(attempt.Prize);

        await AppHarness.WaitUntilAsync(async () => (await app.NtfyRequestsAsync()).Count >= 1, "the ntfy notification");
        var ntfy = Assert.Single(await app.NtfyRequestsAsync());
        Assert.Equal("Octopus scratch card scratched", ntfy.Header("Title"));
        Assert.Equal("low", ntfy.Header("Priority"));
        Assert.Contains("No prize this week", ntfy.Body);
    }

    [Fact]
    public async Task Mutation_error_is_recorded_as_a_failed_attempt()
    {
        // The failing mutation still moves the scenario on, so the next query sees a scratched card and the job stops retrying
        await using var app = await StartAsync(o => o.StubScratchableAsync(
            _session, GraphQl.Error("Scratchcard session has ended"), GraphQl.OfferCard()));

        await WaitForAttemptsAsync(app);
        await WaitForMoreQueriesAsync(app);

        var scratches = await app.GetScratchesAsync();
        var attempt = Assert.Single(scratches.Attempts);
        Assert.False(attempt.Success);
        Assert.Equal("Scratchcard session has ended", attempt.Error);
        Assert.Equal(1, scratches.Summary.Failed);

        await AppHarness.WaitUntilAsync(async () => (await app.NtfyRequestsAsync()).Count >= 1, "the ntfy notification");
        var ntfy = Assert.Single(await app.NtfyRequestsAsync());
        Assert.Equal("Octopus scratch card problem", ntfy.Header("Title"));
        Assert.Equal("high", ntfy.Header("Priority"));
        Assert.Contains("Scratchcard session has ended", ntfy.Body);
    }

    [Fact]
    public async Task Already_scratched_card_is_left_alone()
    {
        await using var app = await StartAsync(o => o.StubAlreadyScratchedAsync(_session, GraphQl.OfferCard()));

        await WaitForMoreQueriesAsync(app);

        Assert.Empty(await app.BackendRequestsAsync(ScratchMutation));
        Assert.Empty((await app.GetScratchesAsync()).Attempts);
        Assert.Empty(await app.NtfyRequestsAsync());
    }

    [Fact]
    public async Task Invalid_scratch_cron_stops_the_app_at_startup()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = AppHarness.CreateApp(fixture, network: null, AppHarness.Never, "not-a-cron").Build();

        await app.StartAsync(ct);
        // A running container reports exit code 0, so wait for a non-zero one
        await AppHarness.WaitUntilAsync(async () => await app.GetExitCodeAsync(ct) != 0, "the app to exit with an error");

        var (stdout, stderr) = await app.GetLogsAsync(ct: ct);
        Assert.Contains("SCRATCH_CRON", stdout + stderr);
    }
}
