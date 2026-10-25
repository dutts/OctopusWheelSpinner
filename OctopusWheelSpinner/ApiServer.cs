using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OctopusWheelSpinner;

/// <summary>
/// Serves:
///   GET /health - 200 while the service is running, so an Octopus outage doesn't get the container restarted.
///                 Also reports whether the Octopus API is reachable, slow or unreachable, from a background probe cached briefly so
///                 frequent checks don't hammer the API and never wait on it.
///   GET /spins  - spin attempts and outcomes as JSON; optional ?limit=(1-1000, default 100) &fuel=electricity|gas &success=true|false
///   GET /scalar - Scalar API reference page for trying the endpoints in a browser
///   GET /openapi.json - the hand-written OpenAPI document the page is built from (ApiDocs/openapi.json)
/// </summary>
public sealed class ApiServer(OctopusClient client, SpinStore store, int port, ILogger<ApiServer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private static readonly byte[] ScalarPage = Resource("ApiDocs.index.html");
    private static readonly byte[] OpenApiDocument = Resource("ApiDocs.openapi.json");

    private readonly Lock _lock = new();
    private Task? _probe;
    private DateTimeOffset _checkedAt;
    private ConnectivityCheck? _octopus;

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
                    var octopus = OctopusStatus(ct);
                    await WriteJsonAsync(context, 200, new
                    {
                        status = "healthy",
                        octopus = octopus?.Status.ToString().ToLowerInvariant() ?? "unknown",
                        error = octopus?.Error
                    }, ct);
                    break;
                case "/spins":
                    await HandleSpinsAsync(context, ct);
                    break;
                case "/scalar":
                    await WriteBytesAsync(context, "text/html; charset=utf-8", ScalarPage, ct);
                    break;
                case "/openapi.json":
                    await WriteBytesAsync(context, "application/json", OpenApiDocument, ct);
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

    private static async Task WriteBytesAsync(HttpListenerContext context, string contentType, byte[] body, CancellationToken ct)
    {
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body, ct);
    }

    private static byte[] Resource(string name)
    {
        using var stream = typeof(ApiServer).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Embedded resource {name} not found");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Returns the last Octopus probe result straight away, starting a fresh probe in the background if it's stale.
    /// </summary>
    private ConnectivityCheck? OctopusStatus(CancellationToken ct)
    {
        lock (_lock)
        {
            if (DateTimeOffset.UtcNow - _checkedAt >= CacheFor && _probe is null or { IsCompleted: true })
                _probe = Task.Run(() => ProbeAsync(ct), CancellationToken.None);
            return _octopus;
        }
    }

    private async Task ProbeAsync(CancellationToken ct)
    {
        ConnectivityCheck result;
        try { result = await client.CheckConnectivityAsync(ct); }
        catch (Exception ex) { result = new ConnectivityCheck(Connectivity.Unreachable, ex.GetType().Name); }

        lock (_lock)
        {
            // Log only changes; a first check that finds Octopus reachable logs nothing
            if (result.Status != (_octopus?.Status ?? Connectivity.Reachable))
            {
                if (result.Status == Connectivity.Reachable)
                    logger.LogInformation("Octopus API reachable again");
                else
                    logger.LogWarning("Octopus API {Connectivity}: {Error}", result.Status.ToString().ToLowerInvariant(), result.Error);
            }

            _octopus = result;
            _checkedAt = DateTimeOffset.UtcNow;
        }
    }
}
