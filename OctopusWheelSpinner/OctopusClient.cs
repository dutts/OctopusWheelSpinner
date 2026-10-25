using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OctopusWheelSpinner;

public enum FuelType { Electricity, Gas }

public sealed record SpinResult(bool Success, int? Points, string? Error);

// Ordered from best to worst: a check across several hosts reports the worst
public enum Connectivity { Reachable, Slow, Unreachable }

public sealed record ConnectivityCheck(Connectivity Status, string? Error);

public sealed class OctopusClient(HttpClient http, OctopusOptions options)
{
    // Octopus's gateway rejects requests with no User-Agent (HTTP 403)
    public const string UserAgent = "OctopusWheelSpinner/1.0";

    private const string TokenUrl = "https://api.octopus.energy/v1/graphql/";
    private const string BackendUrl = "https://api.backend.octopus.energy/v1/graphql/";

    private static readonly string[] HealthUrls = [TokenUrl, BackendUrl];

    private static readonly TimeSpan ProbeConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeResponseTimeout = TimeSpan.FromSeconds(10);

    // The probe has its own handler with a connect timeout (TCP and TLS) shorter than the overall timeout, so a host
    // that can't be connected to fails first, and the overall timeout only fires for a connected but slow response
    private static readonly HttpClient ProbeHttp =
        new(new SocketsHttpHandler { ConnectTimeout = ProbeConnectTimeout, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            DefaultRequestHeaders = { { "User-Agent", UserAgent } }
        };

    private string? _token;
    private DateTimeOffset _tokenExpiry;

    public async Task<int> GetSpinsAllowedAsync(FuelType fuel, CancellationToken ct)
    {
        var data = await SendAsync($$"""
            query { wheelOfFortuneSpinsAllowed(fuelType: {{fuel.ToString().ToUpperInvariant()}}, accountNumber: "{{options.AccountNumber}}") { spinsAllowed } }
            """, ct);
        return data["wheelOfFortuneSpinsAllowed"]?["spinsAllowed"]?.GetValue<int>() ?? 0;
    }

    public async Task<SpinResult> SpinAsync(FuelType fuel, CancellationToken ct)
    {
        try
        {
            var data = await SendAsync($$"""
                mutation { spinWheelOfFortune(input: { accountNumber: "{{options.AccountNumber}}", fuelType: {{fuel.ToString().ToUpperInvariant()}} }) { prize { value } } }
                """, ct);
            var value = data["spinWheelOfFortune"]?["prize"]?["value"]?.GetValue<int>();
            return value is null ? new SpinResult(false, null, "No prize in response") : new SpinResult(true, value, null);
        }
        catch (OctopusApiException ex)
        {
            return new SpinResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Connectivity probe only: sends an unauthenticated <c>{ __typename }</c> query to each endpoint.
    /// Uses no credentials and has no side effects.
    /// </summary>
    public async Task<ConnectivityCheck> CheckConnectivityAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeResponseTimeout);

        var checks = HealthUrls.Select(async url =>
        {
            var host = new Uri(url).Host;
            try
            {
                using var response = await ProbeHttp.PostAsJsonAsync(url, new { query = "{ __typename }" }, timeout.Token);
                return response.IsSuccessStatusCode
                    ? new ConnectivityCheck(Connectivity.Reachable, null)
                    : new ConnectivityCheck(Connectivity.Unreachable, $"{host}: HTTP {(int)response.StatusCode}");
            }
            // Our own timeout fired, so the connection was made (a connect timeout would have fired first)
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return new ConnectivityCheck(Connectivity.Slow, $"{host}: no response within {ProbeResponseTimeout.TotalSeconds:0}s");
            }
            // The handler's connect timeout throws TaskCanceledException wrapping a TimeoutException
            catch (OperationCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                return new ConnectivityCheck(Connectivity.Unreachable, $"{host}: no connection within {ProbeConnectTimeout.TotalSeconds:0}s");
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                return new ConnectivityCheck(Connectivity.Unreachable, $"{host}: {ex.GetType().Name}");
            }
        });

        var results = await Task.WhenAll(checks);
        var errors = results.Select(r => r.Error).OfType<string>().ToList();
        return new ConnectivityCheck(results.Max(r => r.Status), errors.Count > 0 ? string.Join("; ", errors) : null);
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_token is not null && _tokenExpiry > DateTimeOffset.UtcNow.AddMinutes(5)) return;

        var query = $$"""mutation { obtainKrakenToken(input: { APIKey: "{{options.ApiKey}}" }) { token payload } }""";
        var data = await PostAsync(TokenUrl, query, token: null, ct);
        _token = data["obtainKrakenToken"]?["token"]?.GetValue<string>()
                 ?? throw new OctopusApiException("Failed to obtain token");
        var exp = data["obtainKrakenToken"]?["payload"]?["exp"]?.GetValue<long>();
        _tokenExpiry = exp is null ? DateTimeOffset.UtcNow.AddMinutes(50) : DateTimeOffset.FromUnixTimeSeconds(exp.Value);
    }

    private async Task<JsonNode> SendAsync(string query, CancellationToken ct)
    {
        await EnsureTokenAsync(ct);
        return await PostAsync(BackendUrl, query, _token, ct);
    }

    private async Task<JsonNode> PostAsync(string url, string query, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { query }) };
        if (token is not null) request.Headers.TryAddWithoutValidation("Authorization", token);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        try { json = JsonNode.Parse(body); } catch (JsonException) { }

        if (json?["errors"] is JsonArray { Count: > 0 } errors)
            throw new OctopusApiException(string.Join("; ", errors.Select(e => e?["message"]?.GetValue<string>())));
        if (!response.IsSuccessStatusCode || json?["data"] is not { } data)
            throw new OctopusApiException($"HTTP {(int)response.StatusCode}: {body}");
        return data;
    }
}

public sealed class OctopusApiException(string message) : Exception(message);

public sealed record OctopusOptions(string ApiKey, string AccountNumber);
