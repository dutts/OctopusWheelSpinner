using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OctopusWheelSpinner;

/// <summary>
/// Serves:
///   GET /health - 200 if the Octopus API is reachable, 503 if not (cached briefly so frequent checks don't hammer the API)
///   GET /spins  - spin attempts and outcomes as JSON; optional ?limit=(1-1000, default 100) &fuel=electricity|gas &success=true|false
/// </summary>
public sealed class ApiServer(OctopusClient client, SpinStore store, int port, ILogger<ApiServer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTimeOffset _checkedAt;
    private string? _failure;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{port}/");
        listener.Start();
        logger.LogInformation("HTTP endpoint listening on port {Port}", port);

        await using var registration = ct.Register(listener.Stop);
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception) when (ct.IsCancellationRequested) { break; }

            _ = Task.Run(() => HandleAsync(context, ct), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            if (context.Request.HttpMethod is not ("GET" or "HEAD"))
            {
                context.Response.StatusCode = 405;
                return;
            }

            switch (context.Request.Url?.AbsolutePath)
            {
                case "/health":
                    var failure = await CheckAsync(ct);
                    await WriteJsonAsync(context, failure is null ? 200 : 503,
                        new { status = failure is null ? "healthy" : "unhealthy", error = failure }, ct);
                    break;
                case "/spins":
                    await HandleSpinsAsync(context, ct);
                    break;
                default:
                    context.Response.StatusCode = 404;
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Request failed");
            context.Response.StatusCode = 500;
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task HandleSpinsAsync(HttpListenerContext context, CancellationToken ct)
    {
        var query = context.Request.QueryString;

        var limit = 100;
        if (query["limit"] is { } l && (!int.TryParse(l, out limit) || limit is < 1 or > 1000))
            { await WriteJsonAsync(context, 400, new { error = "limit must be an integer between 1 and 1000" }, ct); return; }

        FuelType? fuel = null;
        if (query["fuel"] is { } f)
        {
            if (!Enum.TryParse<FuelType>(f, ignoreCase: true, out var parsed))
                { await WriteJsonAsync(context, 400, new { error = "fuel must be electricity or gas" }, ct); return; }
            fuel = parsed;
        }

        bool? success = null;
        if (query["success"] is { } sv)
        {
            if (!bool.TryParse(sv, out var parsed))
                { await WriteJsonAsync(context, 400, new { error = "success must be true or false" }, ct); return; }
            success = parsed;
        }

        var (summary, attempts) = store.Query(limit, fuel, success);
        await WriteJsonAsync(context, 200, new { summary, attempts }, ct);
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, int status, object body, CancellationToken ct)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.OutputStream, body, JsonOptions, ct);
    }

    private async Task<string?> CheckAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow - _checkedAt < CacheFor) return _failure;

            var failure = await client.CheckConnectivityAsync(ct);
            if (failure is not null && _failure is null)
                logger.LogWarning("Health check failed: {Failure}", failure);
            else if (failure is null && _failure is not null)
                logger.LogInformation("Health check recovered");

            _failure = failure;
            _checkedAt = DateTimeOffset.UtcNow;
            return failure;
        }
        finally
        {
            _lock.Release();
        }
    }
}
