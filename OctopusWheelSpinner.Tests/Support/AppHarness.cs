using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Xunit;

namespace OctopusWheelSpinner.Tests.Support;

/// <summary>One test's world: a WireMock stand-in for Octopus and ntfy, and the app container pointed at it.</summary>
public sealed class AppHarness : IAsyncDisposable
{
    // Quartz format. Never fires, so a job only runs when a test turns its schedule on
    public const string Never = "0 0 9 1 1 ? 2099";
    public const string EverySecond = "* * * * * ?";
    public const string AccountNumber = "A-TEST1234";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly INetwork _network;
    private readonly IContainer _app;
    private readonly HttpClient _http;

    public MockOctopus Octopus { get; }

    private AppHarness(INetwork network, MockOctopus octopus, IContainer app, HttpClient http)
    {
        _network = network;
        Octopus = octopus;
        _app = app;
        _http = http;
    }

    /// <summary>Starts a mock, lets <paramref name="arrange"/> stub it, then starts the app and waits for /health.</summary>
    public static async Task<AppHarness> StartAsync(
        AppImageFixture fixture,
        Func<MockOctopus, Task> arrange,
        string spinCron = Never,
        string scratchCron = Never)
    {
        var ct = TestContext.Current.CancellationToken;
        // A network per test: every mock has the alias "octopus", which would otherwise resolve across tests running in parallel
        var network = new NetworkBuilder().Build();
        await network.CreateAsync(ct);
        MockOctopus? octopus = null;
        try
        {
            octopus = await MockOctopus.StartAsync(network, ct);
            await arrange(octopus);
            var app = CreateApp(fixture, network, spinCron, scratchCron)
                .WithPortBinding(8080, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPath("/health").ForPort(8080)))
                .Build();
            try
            {
                await app.StartAsync(ct);
            }
            catch
            {
                await WriteLogsAsync(app);
                await app.DisposeAsync();
                throw;
            }
            var http = new HttpClient { BaseAddress = new Uri($"http://{app.Hostname}:{app.GetMappedPublicPort(8080)}") };
            return new AppHarness(network, octopus, app, http);
        }
        catch
        {
            if (octopus is not null) await octopus.DisposeAsync();
            await network.DisposeAsync();
            throw;
        }
    }

    /// <summary>A builder for an app container already configured to talk to the mock; the caller chooses how to start it.</summary>
    public static ContainerBuilder CreateApp(AppImageFixture fixture, INetwork? network, string spinCron, string scratchCron)
    {
        var builder = new ContainerBuilder(fixture.Image)
            .WithEnvironment("OCTOPUS_API_KEY", "test-key")
            .WithEnvironment("OCTOPUS_ACCOUNT_NUMBER", AccountNumber)
            .WithEnvironment("OCTOPUS_API_URL", $"http://{MockOctopus.Alias}{MockOctopus.TokenPath}")
            .WithEnvironment("OCTOPUS_BACKEND_URL", $"http://{MockOctopus.Alias}{MockOctopus.BackendPath}")
            .WithEnvironment("NTFY_URL", $"http://{MockOctopus.Alias}/ntfy")
            .WithEnvironment("NTFY_TOPIC", "test")
            .WithEnvironment("SPIN_CRON", spinCron)
            .WithEnvironment("SCRATCH_CRON", scratchCron)
            // /app isn't writable by the image's non-root user
            .WithEnvironment("DB_PATH", "/tmp/spins.db");
        return network is null ? builder : builder.WithNetwork(network);
    }

    public Task<ScratchesDto> GetScratchesAsync(string query = "") => GetAsync<ScratchesDto>($"/scratches{query}");

    public Task<SpinsDto> GetSpinsAsync(string query = "") => GetAsync<SpinsDto>($"/spins{query}");

    public Task<HttpResponseMessage> GetRawAsync(string path) => _http.GetAsync(path, TestContext.Current.CancellationToken);

    private async Task<T> GetAsync<T>(string path) =>
        (await _http.GetFromJsonAsync<T>(path, Json, TestContext.Current.CancellationToken))!;

    /// <summary>Requests to the backend endpoint whose body contains <paramref name="operation"/>.</summary>
    public Task<IReadOnlyList<LoggedRequest>> BackendRequestsAsync(string operation) =>
        Octopus.RequestsAsync(MockOctopus.BackendPath, operation, TestContext.Current.CancellationToken);

    public Task<IReadOnlyList<LoggedRequest>> NtfyRequestsAsync() =>
        Octopus.RequestsAsync(MockOctopus.NtfyPath, null, TestContext.Current.CancellationToken);

    /// <summary>Polls until <paramref name="condition"/> is true, failing with <paramref name="what"/> on timeout.</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}");
            await Task.Delay(200, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await WriteLogsAsync(_app);
        await _app.DisposeAsync();
        await Octopus.DisposeAsync();
        await _network.DisposeAsync();
    }

    public static async Task WriteLogsAsync(IContainer container)
    {
        var (stdout, stderr) = await container.GetLogsAsync(ct: CancellationToken.None);
        TestContext.Current.TestOutputHelper?.WriteLine($"--- app logs ---\n{stdout}\n{stderr}");
    }
}
