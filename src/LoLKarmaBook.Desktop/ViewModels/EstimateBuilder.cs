using LoLKarmaBook.Scoring;
using LoLKarmaBook.Scoring.Performance;
using LoLKarmaBook.Scoring.Rating;

namespace LoLKarmaBook.Desktop.ViewModels;

/// <summary>
/// Turns a performance dataset into an <see cref="EstimatedElo"/> for the player
/// it describes. The ordering and the roster shape are fixed by the importer
/// (ten-player roster plus the owner's row, "player" and "teammates" fields), so
/// this only has to agree with that format.
/// </summary>
internal static class EstimateBuilder
{
    /// <summary>The synthetic demo's player, which has its own roster generator.</summary>
    public static EstimatedElo FromPerformanceDataset(PerformanceDataset dataset)
    {
        if (dataset.Roster is not { } roster)
        {
            throw new ScoringValidationException(
                "estimated-elo.roster",
                "Dataset has no roster.");
        }

        return EstimatedEloCalculator.Calculate(
            dataset.PlayerId ?? roster.OwnerId,
            roster.ToRatingMatches(dataset.Matches),
            dataset.Matches,
            dataset.Source);
    }
}
