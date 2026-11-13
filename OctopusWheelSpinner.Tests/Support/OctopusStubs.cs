namespace OctopusWheelSpinner.Tests.Support;

/// <summary>The Octopus operations the app calls, stubbed by operation name.</summary>
public static class OctopusStubs
{
    // WireMock.Net can't match a scenario's initial state by name, so the initial state is a fallback mapping (no
    // required state) and each later state is a higher priority mapping that overrides it. A state only lasts for one
    // match unless the mapping sets the same state again, hence SetState on the later-state mappings.
    private const string SessionQuery = "octoplusActiveScratchcardData";
    private const string ScratchMutation = "scratchOctoplusScratchcard";

    public static Task StubNoSessionAsync(this MockOctopus octopus) =>
        octopus.StubAsync(new Stub(MockOctopus.BackendPath, SessionQuery, GraphQl.NoSession()));

    public static Task StubAlreadyScratchedAsync(this MockOctopus octopus, Guid session, string card) =>
        octopus.StubAsync(new Stub(MockOctopus.BackendPath, SessionQuery, GraphQl.ActiveSession(session, card)));

    /// <summary>
    /// An unscratched card that the scratch mutation scratches. The mutation moves a scenario on, after which the
    /// session query reports a scratched card, as Octopus does, so the schedule can fire repeatedly and still only scratch once.
    /// </summary>
    public static async Task StubScratchableAsync(this MockOctopus octopus, Guid session, string mutationResponse, string cardAfter)
    {
        const string scenario = "scratchcard";
        await octopus.StubAsync(new Stub(MockOctopus.BackendPath, SessionQuery, GraphQl.ActiveSession(session),
            scenario));
        await octopus.StubAsync(new Stub(MockOctopus.BackendPath, SessionQuery, GraphQl.ActiveSession(session, cardAfter),
            scenario, WhenState: "Scratched", SetState: "Scratched", Priority: 1));
        await octopus.StubAsync(new Stub(MockOctopus.BackendPath, ScratchMutation, mutationResponse,
            scenario, SetState: "Scratched"));
    }

    /// <summary>One spin available for the fuel; it is used up once spun.</summary>
    public static async Task StubOneSpinAsync(this MockOctopus octopus, string fuel, int points)
    {
        var scenario = $"spin-{fuel}";
        await octopus.StubAsync(new Stub(MockOctopus.BackendPath, $"wheelOfFortuneSpinsAllowed(fuelType: {fuel}", GraphQl.SpinsAllowed(1),
            scenario));
        await octopus.StubAsync(new Stub(MockOctopus.BackendPath, $"wheelOfFortuneSpinsAllowed(fuelType: {fuel}", GraphQl.SpinsAllowed(0),
            scenario, WhenState: "Spun", SetState: "Spun", Priority: 1));
        await octopus.StubAsync(new Stub(MockOctopus.BackendPath, $"spinWheelOfFortune*fuelType: {fuel}", GraphQl.SpinResult(points),
            scenario, SetState: "Spun"));
    }
}
