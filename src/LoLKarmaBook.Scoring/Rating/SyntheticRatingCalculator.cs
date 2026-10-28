using LoLKarmaBook.Scoring.Json;

namespace LoLKarmaBook.Scoring.Rating;

/// <summary>
/// Deterministic team-Elo calculation over completed matches. It performs no I/O.
/// </summary>
public static class SyntheticRatingCalculator
{
    public const string AlgorithmVersion = "synthetic-team-elo-1.0.0";
    public const string SyntheticSource = "synthetic";
    public const double InitialRating = 1500.0;
    public const double Scale = 400.0;
    public const double KFactor = 24.0;
    public const int ProvisionalGameThreshold = 10;
    public const int TeamSize = 5;

    public static SyntheticRatingResult Calculate(
        SyntheticRatingDataset dataset,
        IEnumerable<string>? targetPlayerIds = null)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        if (dataset.SchemaVersion != SyntheticRatingJson.SchemaVersion)
        {
            throw new ScoringValidationException(
                "rating.schema-version",
                $"Unsupported rating schemaVersion {dataset.SchemaVersion}; expected {SyntheticRatingJson.SchemaVersion}.");
        }

        _ = ScoringSource.Validate(dataset.Source, "rating.source");

        var datasetScope = dataset.Scope
            ?? throw new ScoringValidationException("rating.scope", "Rating dataset requires a scope.");

        ValidateScope(datasetScope, "dataset");

        if (dataset.Matches is null || dataset.Matches.Count == 0)
        {
            throw new ScoringValidationException(
                "rating.empty",
                "At least one completed match is required.");
        }

        for (var i = 0; i < dataset.Matches.Count; i++)
        {
            ValidateMatch(dataset.Matches[i], i, datasetScope);
        }

        var (uniqueMatches, duplicateCount) = Deduplicate(dataset.Matches);

        var ordered = uniqueMatches
            .OrderBy(m => m.Timestamp.UtcDateTime.Ticks)
            .ThenBy(m => m.MatchId, StringComparer.Ordinal)
            .ToArray();

        var ratings = new Dictionary<string, double>(StringComparer.Ordinal);
        var games = new Dictionary<string, int>(StringComparer.Ordinal);
        var history = new List<SyntheticMatchRating>(ordered.Length);

        foreach (var match in ordered)
        {
            var teamA = match.TeamA.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var teamB = match.TeamB.OrderBy(id => id, StringComparer.Ordinal).ToArray();

            foreach (var player in teamA.Concat(teamB))
            {
                ratings.TryAdd(player, InitialRating);
                games.TryAdd(player, 0);
            }

            var averageA = teamA.Average(id => ratings[id]);
            var averageB = teamB.Average(id => ratings[id]);
            var expectedA = ExpectedScore(averageA, averageB);
            var resultA = string.Equals(match.Winner, "A", StringComparison.Ordinal) ? 1.0 : 0.0;
            var delta = KFactor * (resultA - expectedA);

            if (!double.IsFinite(expectedA) || !double.IsFinite(delta))
            {
                throw new ScoringValidationException(
                    "rating.non-finite",
                    $"Match \"{match.MatchId}\" produced a non-finite Elo update.");
            }

            var changes = new List<RatingChange>(TeamSize * 2);

            foreach (var player in teamA)
            {
                var before = ratings[player];
                var after = before + delta;
                EnsureFinite(after, match.MatchId);
                changes.Add(new RatingChange(player, "A", before, after));
                ratings[player] = after;
                games[player]++;
            }

            foreach (var player in teamB)
            {
                var before = ratings[player];
                var after = before - delta;
                EnsureFinite(after, match.MatchId);
                changes.Add(new RatingChange(player, "B", before, after));
                ratings[player] = after;
                games[player]++;
            }

            history.Add(new SyntheticMatchRating(
                match.MatchId,
                match.Timestamp,
                averageA,
                averageB,
                expectedA,
                delta,
                match.Winner,
                changes));
        }

        var pool = BuildPools(ratings.Keys, ordered);
        var allPlayerRatings = ratings.Keys
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id =>
            {
                var poolInfo = pool.ByPlayer[id];
                var gameCount = games[id];
                return new PlayerRating(
                    id,
                    ratings[id],
                    gameCount,
                    gameCount < ProvisionalGameThreshold,
                    poolInfo.Representative,
                    poolInfo.Size);
            })
            .ToArray();

        IReadOnlyList<PlayerRating> selected = allPlayerRatings;
        if (targetPlayerIds is not null)
        {
            var requested = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in targetPlayerIds)
            {
                if (string.IsNullOrWhiteSpace(target) || target != target.Trim())
                {
                    throw new ScoringValidationException(
                        "rating.target",
                        "Target player IDs must be non-empty and have no surrounding whitespace.");
                }

                if (!ratings.ContainsKey(target))
                {
                    throw new ScoringValidationException(
                        "rating.unknown-target",
                        $"Target player \"{target}\" has no match in this dataset.");
                }

                requested.Add(target);
            }

            selected = allPlayerRatings.Where(r => requested.Contains(r.PlayerId)).ToArray();
        }

        return new SyntheticRatingResult(
            selected,
            history,
            allPlayerRatings.Length,
            pool.Count,
            pool.LargestSize,
            ordered.Length,
            duplicateCount,
            AlgorithmVersion);
    }

    /// <summary>Logistic team Elo expectation for team A.</summary>
    public static double ExpectedScore(double averageA, double averageB)
        => 1.0 / (1.0 + Math.Pow(10.0, (averageB - averageA) / Scale));

    private static void EnsureFinite(double value, string matchId)
    {
        if (!double.IsFinite(value))
        {
            throw new ScoringValidationException(
                "rating.non-finite",
                $"Match \"{matchId}\" produced a non-finite player rating.");
        }
    }

    private static void ValidateScope(SyntheticScope scope, string context)
    {
        ValidateScopeField(scope.Operator, "operator", context);
        ValidateScopeField(scope.Realm, "realm", context);
        ValidateScopeField(scope.Queue, "queue", context);
        ValidateScopeField(scope.Patch, "patch", context);
    }

    private static void ValidateScopeField(string? value, string field, string context)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > 120)
        {
            throw new ScoringValidationException(
                "rating.scope",
                $"{context} scope field {field} must be non-empty, trimmed and at most 120 characters.");
        }
    }

    private static void ValidateMatch(SyntheticRatingMatch? match, int index, SyntheticScope datasetScope)
    {
        if (match is null)
        {
            throw new ScoringValidationException("rating.structure", $"Rating match {index} is null.");
        }

        if (string.IsNullOrWhiteSpace(match.MatchId) || match.MatchId != match.MatchId.Trim() || match.MatchId.Length > 200)
        {
            throw new ScoringValidationException(
                "rating.match-id",
                $"Rating match {index} requires a non-empty, trimmed match ID of at most 200 characters.");
        }

        if (match.Timestamp == default)
        {
            throw new ScoringValidationException(
                "rating.timestamp",
                $"Rating match \"{match.MatchId}\" requires a timestamp.");
        }

        if (!string.Equals(match.Winner, "A", StringComparison.Ordinal)
            && !string.Equals(match.Winner, "B", StringComparison.Ordinal))
        {
            throw new ScoringValidationException(
                "rating.winner",
                $"Rating match \"{match.MatchId}\" winner must be \"A\" or \"B\".");
        }

        if (match.Scope is not null)
        {
            ValidateScope(match.Scope, $"match \"{match.MatchId}\"");
            if (match.Scope != datasetScope)
            {
                throw new ScoringValidationException(
                    "rating.scope",
                    $"Rating match \"{match.MatchId}\" has a scope that differs from the dataset scope.");
            }
        }

        var teamA = ValidateTeam(match.TeamA, match.MatchId, "teamA");
        var teamB = ValidateTeam(match.TeamB, match.MatchId, "teamB");

        if (teamA.Overlaps(teamB))
        {
            throw new ScoringValidationException(
                "rating.teams",
                $"Rating match \"{match.MatchId}\" uses the same player on both teams.");
        }
    }

    private static HashSet<string> ValidateTeam(IReadOnlyList<string>? team, string matchId, string teamName)
    {
        if (team is null || team.Count != TeamSize)
        {
            throw new ScoringValidationException(
                "rating.teams",
                $"Rating match \"{matchId}\" requires exactly {TeamSize} players in {teamName}.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in team)
        {
            if (string.IsNullOrWhiteSpace(id) || id != id.Trim() || id.Length > 128 || id.Any(char.IsControl))
            {
                throw new ScoringValidationException(
                    "rating.teams",
                    $"Rating match \"{matchId}\" contains an invalid player ID in {teamName}.");
            }

            if (!ids.Add(id))
            {
                throw new ScoringValidationException(
                    "rating.teams",
                    $"Rating match \"{matchId}\" contains a duplicate player ID in {teamName}.");
            }
        }

        return ids;
    }

    private static (List<SyntheticRatingMatch> Unique, int DuplicateCount) Deduplicate(
        IReadOnlyList<SyntheticRatingMatch> matches)
    {
        var unique = new List<SyntheticRatingMatch>(matches.Count);
        var byId = new Dictionary<string, SyntheticRatingMatch>(StringComparer.Ordinal);
        var duplicates = 0;

        foreach (var match in matches)
        {
            if (byId.TryGetValue(match.MatchId, out var existing))
            {
                if (!Identical(existing, match))
                {
                    throw new ScoringValidationException(
                        "rating.duplicate-id-conflict",
                        $"Match ID \"{match.MatchId}\" appears more than once with different content.");
                }

                duplicates++;
                continue;
            }

            byId.Add(match.MatchId, match);
            unique.Add(match);
        }

        return (unique, duplicates);
    }

    private static bool Identical(SyntheticRatingMatch left, SyntheticRatingMatch right)
        => left.Timestamp.Equals(right.Timestamp)
           && string.Equals(left.Winner, right.Winner, StringComparison.Ordinal)
           && TeamsEqual(left.TeamA, right.TeamA)
           && TeamsEqual(left.TeamB, right.TeamB);

    private static bool TeamsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var orderedLeft = left.OrderBy(id => id, StringComparer.Ordinal);
        var orderedRight = right.OrderBy(id => id, StringComparer.Ordinal);
        return orderedLeft.SequenceEqual(orderedRight, StringComparer.Ordinal);
    }

    private static PoolInfo BuildPools(
        IEnumerable<string> players,
        IReadOnlyList<SyntheticRatingMatch> matches)
    {
        var ids = players.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ids.Length; i++)
        {
            index[ids[i]] = i;
        }

        var parent = new int[ids.Length];
        for (var i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
        }

        // A match connects all ten participants, so union its two new five-player teams.
        foreach (var match in matches)
        {
            var all = match.TeamA.Concat(match.TeamB).Select(id => index[id]).ToArray();
            for (var i = 1; i < all.Length; i++)
            {
                Union(parent, all[0], all[i]);
            }
        }

        var groups = new Dictionary<int, List<int>>();
        for (var i = 0; i < ids.Length; i++)
        {
            var root = Find(parent, i);
            if (!groups.TryGetValue(root, out var group))
            {
                group = [];
                groups[root] = group;
            }

            group.Add(i);
        }

        var byPlayer = new Dictionary<string, (string Representative, int Size)>(StringComparer.Ordinal);
        var largest = 0;
        foreach (var group in groups.Values)
        {
            var representative = group.Select(i => ids[i]).Min(StringComparer.Ordinal)!;
            largest = Math.Max(largest, group.Count);
            foreach (var i in group)
            {
                byPlayer[ids[i]] = (representative, group.Count);
            }
        }

        return new PoolInfo(byPlayer, groups.Count, largest);
    }

    private static int Find(int[] parent, int value)
    {
        while (parent[value] != value)
        {
            parent[value] = parent[parent[value]];
            value = parent[value];
        }

        return value;
    }

    private static void Union(int[] parent, int left, int right)
    {
        var rootLeft = Find(parent, left);
        var rootRight = Find(parent, right);
        if (rootLeft != rootRight)
        {
            parent[rootRight] = rootLeft;
        }
    }

    private sealed record PoolInfo(
        IReadOnlyDictionary<string, (string Representative, int Size)> ByPlayer,
        int Count,
        int LargestSize);
}
