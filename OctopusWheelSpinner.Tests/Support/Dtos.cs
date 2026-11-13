namespace OctopusWheelSpinner.Tests.Support;

// Deliberately separate from the app's own types: the tests only know the HTTP contract
public sealed record ScratchesDto(ScratchSummaryDto Summary, List<ScratchAttemptDto> Attempts);

public sealed record ScratchSummaryDto(int Attempts, int Successful, int Failed, int Won);

public sealed record ScratchAttemptDto(long Id, Guid SessionReference, bool Success, string? Status, string? Prize, string? Error);

public sealed record SpinsDto(SpinSummaryDto Summary, List<SpinAttemptDto> Attempts);

public sealed record SpinSummaryDto(int Attempts, int Successful, int Failed, long TotalPointsWon);

public sealed record SpinAttemptDto(long Id, string Fuel, bool Success, int? PointsWon, string? Error);

public sealed record ProblemDto(int Status, string? Title, string? Detail);

/// <summary>An entry from WireMock's request log.</summary>
public sealed record LoggedRequest(string Method, string Path, string? Body, Dictionary<string, string[]>? Headers)
{
    public string? Header(string name) =>
        Headers?.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault();
}
