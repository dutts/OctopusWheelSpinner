using System.Text.Json;

namespace OctopusWheelSpinner.Tests.Support;

/// <summary>Builders for the JSON bodies Octopus's GraphQL API returns.</summary>
public static class GraphQl
{
    public static string Token() =>
        $$$$$"""{"data":{"obtainKrakenToken":{"token":"test-token","payload":{"exp":{{{{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}}}}}}}""";

    public static string ActiveSession(Guid session, string? scratchcard = null) =>
        $$$$$"""
        {"data":{"octoplusActiveScratchcardData":{"activeSession":{"externalReference":"{{{{{session}}}}}","startsAt":"2026-11-16T00:00:00+00:00","endsAt":"2026-11-16T23:59:59+00:00"},"scratchcard":{{{{{scratchcard ?? "null"}}}}}}}}
        """;

    public static string NoSession() =>
        """{"data":{"octoplusActiveScratchcardData":{"activeSession":null,"scratchcard":null}}}""";

    public static string ScratchMutation(string scratchcard) =>
        $$$$$"""{"data":{"scratchOctoplusScratchcard":{"scratchcard":{{{{{scratchcard}}}}}}}}""";

    public static string Error(string message) =>
        $$$$$"""{"data":null,"errors":[{"message":{{{{{JsonSerializer.Serialize(message)}}}}}}]}""";

    public static string OfferCard(string name = "Free coffee", string slug = "free-coffee", string status = "PRIZE_NOT_YET_CLAIMED") =>
        Card(status, $$$$$"""{"__typename":"OctoplusOfferType","slug":"{{{{{slug}}}}}","name":"{{{{{name}}}}}"}""");

    public static string StampsCard(int stamps, string status = "PRIZE_CLAIMED") =>
        Card(status, $$$$$"""{"__typename":"StampsAwardedType","totalStampsAwarded":{{{{{stamps}}}}}}""");

    public static string NoWinCard() => Card("DID_NOT_WIN", "null");

    private static string Card(string status, string prize) =>
        $$$$$"""{"externalReference":"{{{{{Guid.NewGuid()}}}}}","status":"{{{{{status}}}}}","prize":{{{{{prize}}}}}}""";

    public static string SpinsAllowed(int count) =>
        $$$$$"""{"data":{"wheelOfFortuneSpinsAllowed":{"spinsAllowed":{{{{{count}}}}}}}}""";

    public static string SpinResult(int points) =>
        $$$$$"""{"data":{"spinWheelOfFortune":{"prize":{"value":{{{{{points}}}}}}}}}""";
}
