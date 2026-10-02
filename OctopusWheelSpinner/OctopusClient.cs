using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OctopusWheelSpinner;

public enum FuelType { Electricity, Gas }

public sealed record SpinResult(bool Success, int? Points, string? Error);

public sealed class OctopusClient(HttpClient http, OctopusOptions options)
{
    private const string TokenUrl = "https://api.octopus.energy/v1/graphql/";
    private const string BackendUrl = "https://api.backend.octopus.energy/v1/graphql/";

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
