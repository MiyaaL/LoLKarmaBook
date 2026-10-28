using LoLKarmaBook.Scoring.Json;

namespace LoLKarmaBook.Scoring.Performance;

/// <summary>
/// Deterministic recent-performance calculator over completed match rows. It performs no I/O.
/// </summary>
public static class PerformanceCalculator
{
    public const string AlgorithmVersion = "performance-1.0.0";
    public const double SamplePriorScore = 60.0;
    public const int SamplePriorGames = 5;

    private const double KdaDenominator = 8.0;
    private const double KillParticipationDenominator = 0.5;
    private const double ConversionDenominator = 1.6;
    private const double DamageDenominator = 0.35;
    private const double TakenDenominator = 0.4;

    private const double WinRateWeight = 20.0;
    private const double KdaWeight = 8.0;
    private const double KillParticipationWeight = 17.0;
    private const double ConversionWeight = 30.0;
    private const double DamageWeight = 20.0;
    private const double TakenWeight = 5.0;
    private const double BaseActiveWeight = WinRateWeight + KdaWeight;

    public static PerformanceScore Calculate(PerformanceDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        if (dataset.SchemaVersion != PerformanceJson.SchemaVersion)
        {
            throw new ScoringValidationException(
                "performance.schema-version",
                $"Unsupported performance schemaVersion {dataset.SchemaVersion}; expected {PerformanceJson.SchemaVersion}.");
        }

        _ = ScoringSource.Validate(dataset.Source, "performance.source");

        return Calculate(dataset.Matches);
    }

    public static PerformanceScore Calculate(IReadOnlyList<MatchStatRow> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ValidateRows(matches);

        // Sorting by stable ID makes floating-point accumulation independent of caller ordering.
        var ordered = matches.OrderBy(m => m.MatchId, StringComparer.Ordinal).ToArray();
        var n = ordered.Length;

        var wins = 0;
        var sumKills = 0L;
        var sumDeaths = 0L;
        var sumAssists = 0L;

        var killParticipationShares = new List<double>(n);
        var damageShares = new List<double>(n);
        var goldShares = new List<double>(n);
        var takenShares = new List<double>(n);

        foreach (var row in ordered)
        {
            if (row.Win)
            {
                wins++;
            }

            sumKills += row.Kills;
            sumDeaths += row.Deaths;
            sumAssists += row.Assists;

            AddIfAvailable(killParticipationShares, PerformanceMath.Ratio(
                (double)row.Kills + row.Assists,
                row.TeamTotalKills));

            AddIfAvailable(damageShares, PerformanceMath.Ratio(
                PerformanceMath.PositiveClean(row.TotalDamageDealt),
                PerformanceMath.PositiveClean(row.TeamTotalDamageDealt)));

            AddIfAvailable(goldShares, PerformanceMath.Ratio(
                PerformanceMath.PositiveClean(row.GoldEarned),
                PerformanceMath.PositiveClean(row.TeamTotalGoldEarned)));

            AddIfAvailable(takenShares, PerformanceMath.Ratio(
                PerformanceMath.PositiveClean(row.TotalDamageTaken),
                PerformanceMath.PositiveClean(row.TeamTotalDamageTaken)));
        }

        var winRate = (double)wins / n;
        var kdaNumerator = sumKills + sumAssists;
        double? aggregateKda = kdaNumerator == 0
            ? null
            : (double)kdaNumerator / Math.Max(1L, sumDeaths);

        var killParticipation = Mean(killParticipationShares);
        var damageShare = Mean(damageShares);
        var goldShare = Mean(goldShares);
        var takenShare = Mean(takenShares);

        // Availability is decided from inputs alone (damage and gold shares present, gold > 0).
        // The legacy algorithm includes the 30 conversion weight whenever those inputs exist, even
        // when the division later overflows and the term clamps to 0.
        var conversionAvailable = damageShare.HasValue && goldShare.HasValue && goldShare.Value > 0;
        var rawConversion = conversionAvailable
            ? damageShare!.Value / goldShare!.Value / ConversionDenominator
            : (double?)null;
        var conversionOverflow = rawConversion.HasValue && !double.IsFinite(rawConversion.Value);

        // Diagnostics never expose a non-finite number: an overflowed conversion is reported as
        // null plus the "conversion-overflow" missing metric.
        double? conversion = conversionOverflow ? null : rawConversion;

        var kdaTerm = aggregateKda.HasValue
            ? PerformanceMath.Clamp01(aggregateKda.Value / KdaDenominator)
            : 0.0;
        var killParticipationTerm = killParticipation.HasValue
            ? PerformanceMath.Clamp01(killParticipation.Value / KillParticipationDenominator)
            : 0.0;

        // Legacy term: clamp01(rawConversion), where clamp01(Infinity) == 0.
        var conversionTerm = rawConversion.HasValue
            ? PerformanceMath.Clamp01(rawConversion.Value)
            : 0.0;
        var damageTerm = damageShare.HasValue
            ? PerformanceMath.Clamp01(damageShare.Value / DamageDenominator)
            : 0.0;
        var takenTerm = takenShare.HasValue
            ? PerformanceMath.Clamp01(takenShare.Value / TakenDenominator)
            : 0.0;

        var weights = new PerformanceWeights(
            WinRateWeight,
            KdaWeight,
            killParticipation.HasValue ? KillParticipationWeight : 0.0,
            conversionAvailable ? ConversionWeight : 0.0,
            damageShare.HasValue ? DamageWeight : 0.0,
            takenShare.HasValue ? TakenWeight : 0.0);

        var contributions = new PerformanceContributions(
            WinRateWeight * winRate,
            KdaWeight * kdaTerm,
            KillParticipationWeight * killParticipationTerm,
            ConversionWeight * conversionTerm,
            DamageWeight * damageTerm,
            TakenWeight * takenTerm);

        var raw = contributions.WinRate
            + contributions.Kda
            + contributions.KillParticipation
            + contributions.Conversion
            + contributions.Damage
            + contributions.Taken;

        var activeWeight = weights.Total;
        var available = aggregateKda.HasValue;

        double? normalized = null;
        double? final = null;
        double? exposedRaw = null;

        if (available && activeWeight > 0)
        {
            exposedRaw = raw;
            normalized = raw * 100.0 / activeWeight;
            var shrunk = (normalized.Value * n + SamplePriorScore * SamplePriorGames) / (n + SamplePriorGames);

            // Every input is finite and every term is clamp01'd, so this cannot trigger with valid
            // rows. It is an explicit fail-closed guard instead of letting NaN/Infinity leak into a
            // displayed score if the algorithm is ever changed.
            if (!double.IsFinite(raw) || !double.IsFinite(normalized.Value) || !double.IsFinite(shrunk))
            {
                throw new ScoringValidationException(
                    "performance.non-finite",
                    "Performance calculation produced a non-finite score.");
            }

            final = Math.Clamp(shrunk, 0.0, 100.0);
        }

        var components = new PerformanceComponents(
            killParticipation,
            damageShare,
            goldShare,
            takenShare,
            conversion);

        return new PerformanceScore(
            available,
            n,
            wins,
            winRate,
            aggregateKda,
            exposedRaw,
            normalized,
            final,
            components,
            contributions,
            weights,
            BuildMissingMetrics(
                aggregateKda,
                killParticipation,
                damageShare,
                goldShare,
                conversionAvailable,
                conversionOverflow,
                takenShare),
            AlgorithmVersion);
    }

    private static void ValidateRows(IReadOnlyList<MatchStatRow> matches)
    {
        if (matches.Count == 0)
        {
            throw new ScoringValidationException(
                "performance.empty",
                "At least one completed match is required.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < matches.Count; i++)
        {
            var row = matches[i];
            if (row is null)
            {
                throw new ScoringValidationException("performance.null-row", $"Match row {i} is null.");
            }

            ValidateMatchId(row.MatchId);
            if (!ids.Add(row.MatchId))
            {
                throw new ScoringValidationException(
                    "performance.duplicate-match-id",
                    $"Duplicate match ID \"{row.MatchId}\" in performance input.");
            }

            ValidateCount(row.Kills, nameof(row.Kills), row.MatchId);
            ValidateCount(row.Deaths, nameof(row.Deaths), row.MatchId);
            ValidateCount(row.Assists, nameof(row.Assists), row.MatchId);
            ValidateCount(row.TeamTotalKills, nameof(row.TeamTotalKills), row.MatchId);

            ValidateFiniteNonNegative(row.TotalDamageDealt, nameof(row.TotalDamageDealt), row.MatchId);
            ValidateFiniteNonNegative(row.TeamTotalDamageDealt, nameof(row.TeamTotalDamageDealt), row.MatchId);
            ValidateFiniteNonNegative(row.TotalDamageTaken, nameof(row.TotalDamageTaken), row.MatchId);
            ValidateFiniteNonNegative(row.TeamTotalDamageTaken, nameof(row.TeamTotalDamageTaken), row.MatchId);
            ValidateFiniteNonNegative(row.GoldEarned, nameof(row.GoldEarned), row.MatchId);
            ValidateFiniteNonNegative(row.TeamTotalGoldEarned, nameof(row.TeamTotalGoldEarned), row.MatchId);
        }
    }

    internal static void ValidateMatchId(string matchId)
    {
        if (string.IsNullOrWhiteSpace(matchId))
        {
            throw new ScoringValidationException(
                "performance.match-id",
                "Every match requires a non-empty match ID.");
        }

        if (matchId.Length > 200 || matchId != matchId.Trim())
        {
            throw new ScoringValidationException(
                "performance.match-id",
                "Match IDs must be at most 200 characters and have no surrounding whitespace.");
        }
    }

    private static void ValidateCount(int value, string field, string matchId)
    {
        if (value < 0)
        {
            throw new ScoringValidationException(
                "performance.invalid-count",
                $"Field {field} for match \"{matchId}\" must be a non-negative integer.");
        }
    }

    private static void ValidateFiniteNonNegative(double value, string field, string matchId)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ScoringValidationException(
                "performance.invalid-number",
                $"Field {field} for match \"{matchId}\" must be finite and non-negative.");
        }
    }

    private static void AddIfAvailable(List<double> values, double? value)
    {
        if (value.HasValue)
        {
            values.Add(value.Value);
        }
    }

    private static double? Mean(List<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sum = 0.0;
        foreach (var value in values)
        {
            sum += value;
        }

        return sum / values.Count;
    }

    private static IReadOnlyList<string> BuildMissingMetrics(
        double? aggregateKda,
        double? killParticipation,
        double? damageShare,
        double? goldShare,
        bool conversionAvailable,
        bool conversionOverflow,
        double? takenShare)
    {
        var missing = new List<string>();

        if (!aggregateKda.HasValue)
        {
            missing.Add("aggregate-kda");
        }

        if (!killParticipation.HasValue)
        {
            missing.Add("kill-participation");
        }

        if (!damageShare.HasValue)
        {
            missing.Add("damage-share");
        }

        if (!goldShare.HasValue)
        {
            missing.Add("gold-share");
        }

        if (conversionOverflow)
        {
            // The 30-point weight stays active (input parity with the legacy algorithm) but the
            // raw value is too large to report, so the diagnostic is unavailable with a warning.
            missing.Add("conversion-overflow");
        }
        else if (!conversionAvailable)
        {
            missing.Add("conversion");
        }

        if (!takenShare.HasValue)
        {
            missing.Add("taken-share");
        }

        return missing;
    }
}
