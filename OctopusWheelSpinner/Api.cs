using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Scalar.AspNetCore;

namespace OctopusWheelSpinner;

public sealed record HealthResponse(string Status, Connectivity Octopus, string? Error);

public sealed record SpinsResponse(SpinSummary Summary, List<SpinAttempt> Attempts);

/// <summary>
/// The HTTP endpoints. The OpenAPI document (/openapi.json) is generated from these definitions, so parameter
/// attributes, summaries and return types are what the docs and the Scalar page (/scalar) show.
/// </summary>
public static class Api
{
    public static IServiceCollection AddApi(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.WriteIndented = true;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        services.AddOpenApi(o =>
        {
            o.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "Octopus Wheel Spinner";
                document.Info.Description = "Health and recorded wheel of fortune spin attempts. The endpoints have no authentication.";
                return Task.CompletedTask;
            });
            // fuel binds through FuelFilter, which the generator can only describe as a string
            o.AddSchemaTransformer((schema, context, _) =>
            {
                if (context.ParameterDescription?.Type == typeof(FuelFilter?))
                    schema.Enum = Enum.GetNames<FuelType>().Select(n => (JsonNode)n.ToLowerInvariant()).ToList();
                return Task.CompletedTask;
            });
        });

        services.AddValidation();
        // A value that can't be bound (e.g. limit=abc) throws, so the exception handler can say which parameter was wrong
        services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
        services.AddProblemDetails(o => o.CustomizeProblemDetails = c =>
        {
            if (c.Exception is BadHttpRequestException e) c.ProblemDetails.Detail = e.Message;
        });
        return services;
    }

    public static WebApplication MapApi(this WebApplication app)
    {
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            StatusCodeSelector = ex => ex is BadHttpRequestException b ? b.StatusCode : StatusCodes.Status500InternalServerError,
            SuppressDiagnosticsCallback = c => c.Exception is BadHttpRequestException
        });

        app.MapOpenApi("/openapi.json");
        app.MapScalarApiReference(o => o.WithOpenApiRoutePattern("/openapi.json"));

        // Always 200 while the service runs, so an Octopus outage doesn't get the container restarted
        app.MapGet("/health", (OctopusHealth octopus) =>
            {
                var check = octopus.Current();
                return new HealthResponse("healthy", check?.Status ?? Connectivity.Unknown, check?.Error);
            })
            .WithSummary("Service health")
            .WithDescription(
                "Returns 200 whenever the service is running. `octopus` reports whether the Octopus API is reachable, from a " +
                "background check cached for 30 seconds: `slow` means a connection was made but no response came within 10 " +
                "seconds, `unreachable` means no connection within 5 seconds, a DNS or TLS failure, or an HTTP error, and " +
                "`unknown` means the first check hasn't finished. `error` lists each endpoint that wasn't reachable.");

        app.MapGet("/spins", (
                [Description("Maximum attempts returned"), Range(1, 1000), DefaultValue(100)] int? limit,
                [Description("Only attempts for this fuel")] FuelFilter? fuel,
                [Description("Only successful (`true`) or failed (`false`) attempts")] bool? success,
                SpinStore store) =>
            {
                var (summary, attempts) = store.Query(limit ?? 100, fuel?.Value, success);
                return new SpinsResponse(summary, attempts);
            })
            .WithSummary("Recorded spin attempts")
            .WithDescription(
                "Returns the recorded spin attempts, newest first, with a summary. The summary applies the `fuel` and " +
                "`success` filters but ignores `limit`.")
            .ProducesValidationProblem();

        return app;
    }
}

/// <summary>A fuel in a query string, matched by name ignoring case so that both ?fuel=gas and ?fuel=Gas work.</summary>
public readonly record struct FuelFilter(FuelType Value)
{
    public static bool TryParse(string? value, out FuelFilter result)
    {
        var name = Enum.GetNames<FuelType>().FirstOrDefault(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase));
        result = name is null ? default : new FuelFilter(Enum.Parse<FuelType>(name));
        return name is not null;
    }
}
