using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OctopusWheelSpinner;
using Quartz;

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } v
        ? v
        : throw new InvalidOperationException($"Environment variable {name} is required");

var options = new OctopusOptions(Required("OCTOPUS_API_KEY"), Required("OCTOPUS_ACCOUNT_NUMBER"));
var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "data/spins.db";
// Quartz cron format: seconds minutes hours day-of-month month day-of-week [year]
// Default: 09:00 on the 1st of every month
var cron = Environment.GetEnvironmentVariable("SPIN_CRON") is { Length: > 0 } c ? c : "0 0 9 1 * ?";
if (!CronExpression.TryParse(cron, out _))
    throw new InvalidOperationException(
        $"SPIN_CRON '{cron}' is not a valid Quartz cron expression (format: sec min hour day-of-month month day-of-week; use ? for one of the two day fields)");

var builder = Host.CreateApplicationBuilder(args);
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
