using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Networks;
using WireMock.Net.Testcontainers;

namespace OctopusWheelSpinner.Tests.Support;

/// <summary>
/// A stub of the Octopus token and backend GraphQL endpoints and of ntfy, backed by a WireMock.Net container.
/// GraphQL operations are matched by path and by the operation name in the request body.
/// </summary>
public sealed class MockOctopus : IAsyncDisposable
{
    public const string Alias = "octopus";
    public const string TokenPath = "/token/graphql/";
    public const string BackendPath = "/backend/graphql/";
    public const string NtfyPath = "/ntfy/test";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WireMockContainer _container;
    private readonly HttpClient _admin;

    private MockOctopus(WireMockContainer container)
    {
        _container = container;
        _admin = new HttpClient { BaseAddress = new Uri(container.GetPublicUrl()) };
    }

    public static async Task<MockOctopus> StartAsync(INetwork network, CancellationToken ct)
    {
        var container = new WireMockContainerBuilder()
            .WithNetwork(network)
            .WithNetworkAliases(Alias)
            .Build();
        await container.StartAsync(ct);

        var mock = new MockOctopus(container);
        await mock.StubTokenAndNtfyAsync(ct);
        return mock;
    }

    private async Task StubTokenAndNtfyAsync(CancellationToken ct)
    {
        await StubAsync(new Stub(TokenPath, "obtainKrakenToken", GraphQl.Token()), ct);
        // The /health probe sends an unauthenticated { __typename } query. Real queries also contain __typename, so the
        // match is for the bare query and it has the lowest priority.
        foreach (var path in new[] { TokenPath, BackendPath })
            await StubAsync(new Stub(path, "{ __typename }", """{"data":{"__typename":"Query"}}""", Priority: 10), ct);
        await StubAsync(new Stub(NtfyPath, null, "ok"), ct);
    }

    public async Task StubAsync(Stub stub, CancellationToken ct = default)
    {
        var mapping = new Dictionary<string, object?>
        {
            ["priority"] = stub.Priority,
            ["request"] = new Dictionary<string, object?>
            {
                ["path"] = stub.Path,
                ["methods"] = new[] { "POST" },
                ["body"] = stub.BodyContains is null
                    ? null
                    : new { matcher = new { name = "WildcardMatcher", pattern = $"*{stub.BodyContains}*" } }
            },
            ["response"] = new { statusCode = 200, body = stub.Response, headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" } },
            ["scenario"] = stub.Scenario,
            ["whenStateIs"] = stub.WhenState,
            ["setStateTo"] = stub.SetState
        };
        using var response = await _admin.PostAsJsonAsync("/__admin/mappings", mapping, Json, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Requests received on <paramref name="path"/>, optionally only those whose body contains <paramref name="bodyContains"/>.</summary>
    public async Task<IReadOnlyList<LoggedRequest>> RequestsAsync(string path, string? bodyContains = null, CancellationToken ct = default)
    {
        var all = await _admin.GetFromJsonAsync<List<LogEntry>>("/__admin/requests", Json, ct) ?? [];
        return all
            .Select(e => e.Request)
            .Where(r => r.Path == path && (bodyContains is null || r.Body?.Contains(bodyContains, StringComparison.Ordinal) == true))
            .ToList();
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _container.DisposeAsync();
    }

    private sealed record LogEntry(LoggedRequest Request);
}

/// <summary>A mapping for a POST. Scenario fields let a response depend on, and change, shared state; lower priority numbers win.</summary>
public sealed record Stub(
    string Path,
    string? BodyContains,
    string Response,
    string? Scenario = null,
    string? WhenState = null,
    string? SetState = null,
    int Priority = 5);
