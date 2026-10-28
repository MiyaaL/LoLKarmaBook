namespace LoLKarmaBook.Core;

/// <summary>
/// In-process provider for the A/B sample rosters, with no I/O or network calls.
/// </summary>
public sealed class SyntheticProvider
{
    public SyntheticProvider(SyntheticScenario? scenario = null)
    {
        Scenario = scenario ?? SyntheticScenario.CreateDefault();
    }

    public SyntheticScenario Scenario { get; }

    public MatchSession GetMatchA() => Scenario.MatchA;

    public MatchSession GetMatchB() => Scenario.MatchB;
}
