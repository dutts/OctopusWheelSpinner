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

    [Fact]
    public async Task Root_serves_the_status_page()
    {
        await using var app = await AppHarness.StartAsync(fixture, _ => Task.CompletedTask);

        using var response = await app.GetRawAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<title>Octopus Wheel Spinner</title>", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Health_reports_the_version()
    {
        await using var app = await AppHarness.StartAsync(fixture, _ => Task.CompletedTask);

        var health = await app.GetRawAsync("/health");
        var body = await health.Content.ReadFromJsonAsync<HealthDto>(TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(body?.Version));
    }

    [Fact]
    public async Task Schedule_reports_each_job()
    {
        await using var app = await AppHarness.StartAsync(fixture, _ => Task.CompletedTask);

        using var response = await app.GetRawAsync("/schedule");
        var schedule = await response.Content.ReadFromJsonAsync<ScheduleDto>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["scratch", "spin"], schedule!.Jobs.Select(j => j.Job).Order());
        Assert.All(schedule.Jobs, j =>
        {
            Assert.Equal(AppHarness.Never, j.Cron);
            Assert.Equal(2099, j.NextRun?.Year);
            Assert.Null(j.PreviousRun);
        });
    }
}
