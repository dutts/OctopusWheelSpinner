using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OctopusWheelSpinner;
using Quartz;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

// LOG_FORMAT=json emits compact JSON (one structured event per line); anything else is human-readable
var json = string.Equals(Environment.GetEnvironmentVariable("LOG_FORMAT"), "json", StringComparison.OrdinalIgnoreCase);
var logConfig = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
    .MinimumLevel.Override("Quartz", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "OctopusWheelSpinner");
Log.Logger = (json
    ? logConfig.WriteTo.Console(new RenderedCompactJsonFormatter())
    : logConfig.WriteTo.Console()).CreateLogger();

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } v
        ? v
        : throw new InvalidOperationException($"Environment variable {name} is required");

try
{
    var options = new OctopusOptions(Required("OCTOPUS_API_KEY"), Required("OCTOPUS_ACCOUNT_NUMBER"));
    var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "data/spins.db";
    // Quartz cron format: seconds minutes hours day-of-month month day-of-week [year]
    // Default: 09:00 on the 1st of every month
    var cron = Environment.GetEnvironmentVariable("SPIN_CRON") is { Length: > 0 } c ? c : "0 0 9 1 * ?";
    if (!CronExpression.TryParse(cron, out _))
        throw new InvalidOperationException(
            $"SPIN_CRON '{cron}' is not a valid Quartz cron expression (format: sec min hour day-of-month month day-of-week; use ? for one of the two day fields)");

    Log.Information("Starting OctopusWheelSpinner for account {AccountNumber} with schedule {Cron}, database {DbPath}",
        options.AccountNumber, cron, dbPath);

    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSerilog(Log.Logger);
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(new SpinStore(dbPath));
    // Octopus's gateway rejects requests with no User-Agent (HTTP 403)
    builder.Services.AddHttpClient<OctopusClient>(c => c.DefaultRequestHeaders.UserAgent.ParseAdd("OctopusWheelSpinner/1.0"));
    builder.Services.AddQuartz(q =>
    {
        var jobKey = new JobKey("spin");
        q.AddJob<SpinJob>(j => j.WithIdentity(jobKey));
        q.AddTrigger(t => t
            .ForJob(jobKey)
            .WithIdentity("spin-trigger")
            // If the app was down at the scheduled time, run once on startup rather than skipping the month
            .WithCronSchedule(cron, x => x.WithMisfireInstruction(CronTriggerMisfireInstruction.FireAndProceed)));
    });
    builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

    await builder.Build().RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;
