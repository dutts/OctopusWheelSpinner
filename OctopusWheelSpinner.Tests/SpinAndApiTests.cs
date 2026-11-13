using System.Net;
using System.Net.Http.Json;
using OctopusWheelSpinner.Tests.Support;
using Xunit;

namespace OctopusWheelSpinner.Tests;

public sealed class SpinAndApiTests(AppImageFixture fixture)
{
    [Fact]
    public async Task Spins_each_fuel_once_and_records_the_points()
    {
        await using var app = await AppHarness.StartAsync(
            fixture,
            async o =>
            {
                await o.StubOneSpinAsync("ELECTRICITY", 25);
                await o.StubOneSpinAsync("GAS", 25);
            },
            spinCron: AppHarness.EverySecond);

        await AppHarness.WaitUntilAsync(async () => (await app.GetSpinsAsync()).Attempts.Count >= 2, "both spins to be recorded");
        // Later ticks see no spins left, so nothing more is recorded
        var allowedQueries = (await app.BackendRequestsAsync("wheelOfFortuneSpinsAllowed")).Count;
        await AppHarness.WaitUntilAsync(
            async () => (await app.BackendRequestsAsync("wheelOfFortuneSpinsAllowed")).Count >= allowedQueries + 4, "further ticks");

        var spins = await app.GetSpinsAsync();
        Assert.Equal(2, spins.Attempts.Count);
        Assert.All(spins.Attempts, a =>
        {
            Assert.True(a.Success);
            Assert.Equal(25, a.PointsWon);
        });
        Assert.Equal(["Electricity", "Gas"], spins.Attempts.Select(a => a.Fuel).Order());
        Assert.Equal(new SpinSummaryDto(2, 2, 0, 50), spins.Summary);
    }

    [Fact]
    public async Task Scratches_limit_below_the_minimum_is_a_400_problem()
    {
        await using var app = await AppHarness.StartAsync(fixture, _ => Task.CompletedTask);

        using var response = await app.GetRawAsync("/scratches?limit=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal(400, problem?.Status);
    }
}
