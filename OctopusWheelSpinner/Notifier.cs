using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OctopusWheelSpinner;

/// <summary>ntfy settings. Notifications are enabled only when both <see cref="Url"/> and <see cref="Topic"/> are set.</summary>
public sealed record NtfyOptions(string? Url, string? Topic, string? Username, string? ApiKey)
{
    public bool Enabled => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Topic);
}

public sealed class Notifier(HttpClient http, NtfyOptions options, ILogger<Notifier> logger)
{
    public async Task SendAsync(string title, string message, string priority, string tags, CancellationToken ct)
    {
        if (!options.Enabled) return;

        try
        {
            var url = $"{options.Url!.TrimEnd('/')}/{Uri.EscapeDataString(options.Topic!)}";
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(message, Encoding.UTF8, "text/plain") };
            // Header values must be ASCII; ntfy also accepts RFC 2047 but our titles are plain ASCII
            request.Headers.TryAddWithoutValidation("Title", title);
            request.Headers.TryAddWithoutValidation("Priority", priority);
            request.Headers.TryAddWithoutValidation("Tags", tags);

            if (!string.IsNullOrEmpty(options.ApiKey))
                request.Headers.Authorization = string.IsNullOrEmpty(options.Username)
                    ? new AuthenticationHeaderValue("Bearer", options.ApiKey)
                    : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.ApiKey}")));

            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A notification problem must never fail a spin run
            logger.LogWarning(ex, "Failed to send ntfy notification");
        }
    }
}
