using LoLKarmaBook.Scoring.Json;

namespace LoLKarmaBook.Scoring.Performance;

/// <summary>
/// JSON boundary for completed match data. The parser enforces the byte
/// limit, schema version, source metadata, required fields, uniqueness and numeric validity.
/// </summary>
public static class PerformanceJson
{
    public const int SchemaVersion = 1;
    public const long MaxBytes = ScoringJson.MaxBytes;
    public const string UserOwnedSource = "user-owned-completed-matches";

    public static PerformanceDataset Parse(string json)
    {
        var dto = ScoringJson.Deserialize<PerformanceDatasetDto>(json, "performance.json");
        return FromDto(dto);
    }

    public static PerformanceDataset LoadFile(string path)
    {
        var json = ScoringJson.ReadFile(path);
        return Parse(json);
    }

    public static string Export(PerformanceDataset dataset, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        // Validate before emitting so Desktop cannot persist a malformed dataset.
        _ = PerformanceCalculator.Calculate(dataset);
        return ScoringJson.Serialize(dataset, indented);
    }

    private static PerformanceDataset FromDto(PerformanceDatasetDto dto)
    {
        if (dto.SchemaVersion is null)
        {
            throw new ScoringValidationException(
                "performance.structure",
                "Performance JSON requires schemaVersion.");
        }

        if (dto.SchemaVersion != SchemaVersion)
        {
            throw new ScoringValidationException(
                "performance.schema-version",
                $"Unsupported performance schemaVersion {dto.SchemaVersion}; expected {SchemaVersion}.");
        }

        string source = ScoringSource.Validate(dto.Source, "performance.source");

        if (dto.Matches is null)
        {
            throw new ScoringValidationException(
                "performance.structure",
                "Performance JSON requires a matches array.");
        }

        var rows = new List<MatchStatRow>(dto.Matches.Count);
        for (var i = 0; i < dto.Matches.Count; i++)
        {
            var row = dto.Matches[i]
                ?? throw new ScoringValidationException(
                    "performance.structure",
                    $"Performance match row {i} is null.");

            rows.Add(new MatchStatRow(
                Required(row.MatchId, i, "matchId"),
                Required(row.Win, i, "win"),
                Required(row.Kills, i, "kills"),
                Required(row.Deaths, i, "deaths"),
                Required(row.Assists, i, "assists"),
                Required(row.TeamTotalKills, i, "teamTotalKills"),
                Required(row.TotalDamageDealt, i, "totalDamageDealt"),
                Required(row.TeamTotalDamageDealt, i, "teamTotalDamageDealt"),
                Required(row.TotalDamageTaken, i, "totalDamageTaken"),
                Required(row.TeamTotalDamageTaken, i, "teamTotalDamageTaken"),
                Required(row.GoldEarned, i, "goldEarned"),
                Required(row.TeamTotalGoldEarned, i, "teamTotalGoldEarned")));
        }

        PerformanceRoster? roster = dto.Roster is null ? null : ReadRoster(dto.Roster, rows);

        var dataset = new PerformanceDataset
        {
            SchemaVersion = SchemaVersion,
            Source = source,
            Matches = rows,
            Roster = roster,
            PlayerId = dto.PlayerId is null ? null : ScoringSource.Validate(dto.PlayerId, "performance.playerId")
        };

        // Validate the complete set (unique IDs, finite values, counts) at the boundary.
        _ = PerformanceCalculator.Calculate(dataset);
        return dataset;
    }

    /// <summary>
    /// Optional roster block. It is all-or-nothing: either every match has its
    /// two teams or the file is rejected, so the estimate never runs on a
    /// partial roster.
    /// </summary>
    private static PerformanceRoster ReadRoster(RosterDto dto, IReadOnlyList<MatchStatRow> rows)
    {
        if (dto.Matches is null)
        {
            throw new ScoringValidationException("performance.roster", "Roster requires a matches array.");
        }

        if (dto.Matches.Count != rows.Count)
        {
            throw new ScoringValidationException(
                "performance.roster",
                $"Roster has {dto.Matches.Count} matches but there are {rows.Count} stat rows.");
        }

        var matches = new List<PerformanceRosterMatch>(dto.Matches.Count);
        for (var i = 0; i < dto.Matches.Count; i++)
        {
            RosterMatchDto? match = dto.Matches[i]
                ?? throw new ScoringValidationException("performance.roster", $"Roster match {i} is null.");

            string matchId = Required(match.MatchId, i, "matchId");
            if (!string.Equals(matchId, rows[i].MatchId, StringComparison.Ordinal))
            {
                throw new ScoringValidationException(
                    "performance.roster",
                    $"Roster match {i} is \"{matchId}\" but stat row {i} is \"{rows[i].MatchId}\".");
            }

            IReadOnlyList<string> teammates = Team(match.Teammates, matchId, "teammates");
            IReadOnlyList<string> opponents = Team(match.Opponents, matchId, "opponents");
            if (teammates.Intersect(opponents, StringComparer.Ordinal).Any())
            {
                throw new ScoringValidationException(
                    "performance.roster",
                    $"Match \"{matchId}\" lists a player on both teams.");
            }

            matches.Add(new PerformanceRosterMatch(
                matchId,
                RequiredTimestamp(match.Timestamp, matchId),
                teammates,
                opponents));
        }

        return new PerformanceRoster(
            ScoringSource.Validate(dto.OwnerId, "performance.roster.ownerId"),
            matches);
    }

    /// <summary>Four teammates or five opponents, each a distinct stable ID.</summary>
    private static IReadOnlyList<string> Team(List<string?>? ids, string matchId, string name)
    {
        int expected = name == "teammates" ? 4 : 5;
        if (ids is null || ids.Count != expected)
        {
            throw new ScoringValidationException(
                "performance.roster",
                $"Match \"{matchId}\" requires exactly {expected} {name}.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(expected);
        foreach (string? id in ids)
        {
            string value = ScoringSource.Validate(id, "performance.roster.playerId");
            if (!seen.Add(value))
            {
                throw new ScoringValidationException(
                    "performance.roster",
                    $"Match \"{matchId}\" repeats player \"{value}\" in {name}.");
            }

            result.Add(value);
        }

        return result;
    }

    private static DateTimeOffset RequiredTimestamp(DateTimeOffset? value, string matchId)
        => value ?? throw new ScoringValidationException(
            "performance.roster",
            $"Match \"{matchId}\" roster row requires timestamp.");

    private static T Required<T>(T? value, int index, string name)
        where T : struct
    {
        if (!value.HasValue)
        {
            throw new ScoringValidationException(
                "performance.structure",
                $"Performance match row {index} requires {name}.");
        }

        return value.Value;
    }

    private static string Required(string? value, int index, string name)
    {
        if (value is null)
        {
            throw new ScoringValidationException(
                "performance.structure",
                $"Performance match row {index} requires {name}.");
        }

        return value;
    }

    private sealed class PerformanceDatasetDto
    {
        public int? SchemaVersion { get; init; }
        public string? Source { get; init; }
        public List<MatchStatRowDto?>? Matches { get; init; }
        public RosterDto? Roster { get; init; }
        public string? PlayerId { get; init; }
    }

    private sealed class RosterDto
    {
        public string? OwnerId { get; init; }
        public List<RosterMatchDto?>? Matches { get; init; }
    }

    private sealed class RosterMatchDto
    {
        public string? MatchId { get; init; }
        public DateTimeOffset? Timestamp { get; init; }
        public List<string?>? Teammates { get; init; }
        public List<string?>? Opponents { get; init; }
    }

    private sealed class MatchStatRowDto
    {
        public string? MatchId { get; init; }
        public bool? Win { get; init; }
        public int? Kills { get; init; }
        public int? Deaths { get; init; }
        public int? Assists { get; init; }
        public int? TeamTotalKills { get; init; }
        public double? TotalDamageDealt { get; init; }
        public double? TeamTotalDamageDealt { get; init; }
        public double? TotalDamageTaken { get; init; }
        public double? TeamTotalDamageTaken { get; init; }
        public double? GoldEarned { get; init; }
        public double? TeamTotalGoldEarned { get; init; }
    }
}
