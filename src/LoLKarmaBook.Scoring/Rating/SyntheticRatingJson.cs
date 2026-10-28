using LoLKarmaBook.Scoring.Json;

namespace LoLKarmaBook.Scoring.Rating;

/// <summary>
/// JSON boundary for rating datasets, with schema, source-label and match validation.
/// </summary>
public static class SyntheticRatingJson
{
    public const int SchemaVersion = 1;
    public const long MaxBytes = ScoringJson.MaxBytes;

    public static SyntheticRatingDataset Parse(string json)
    {
        var dto = ScoringJson.Deserialize<RatingDatasetDto>(json, "rating.json");
        return FromDto(dto);
    }

    public static SyntheticRatingDataset LoadFile(string path)
    {
        var json = ScoringJson.ReadFile(path);
        return Parse(json);
    }

    public static string Export(SyntheticRatingDataset dataset, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        // Validate before emitting so an invalid dataset cannot be persisted.
        _ = SyntheticRatingCalculator.Calculate(dataset);
        return ScoringJson.Serialize(dataset, indented);
    }

    private static SyntheticRatingDataset FromDto(RatingDatasetDto dto)
    {
        if (dto.SchemaVersion is null)
        {
            throw new ScoringValidationException("rating.structure", "Rating JSON requires schemaVersion.");
        }

        if (dto.SchemaVersion != SchemaVersion)
        {
            throw new ScoringValidationException(
                "rating.schema-version",
                $"Unsupported rating schemaVersion {dto.SchemaVersion}; expected {SchemaVersion}.");
        }

        string source = ScoringSource.Validate(dto.Source, "rating.source");

        if (dto.Scope is null)
        {
            throw new ScoringValidationException("rating.structure", "Rating JSON requires a dataset scope.");
        }

        if (dto.Matches is null)
        {
            throw new ScoringValidationException("rating.structure", "Rating JSON requires a matches array.");
        }

        var matches = new List<SyntheticRatingMatch>(dto.Matches.Count);
        for (var i = 0; i < dto.Matches.Count; i++)
        {
            var match = dto.Matches[i]
                ?? throw new ScoringValidationException("rating.structure", $"Rating match {i} is null.");

            matches.Add(new SyntheticRatingMatch(
                Required(match.MatchId, i, "matchId"),
                Required(match.Timestamp, i, "timestamp"),
                match.Scope is null ? null : ToScope(match.Scope, i, "scope"),
                RequiredTeam(match.TeamA, i, "teamA"),
                RequiredTeam(match.TeamB, i, "teamB"),
                Required(match.Winner, i, "winner")));
        }

        var dataset = new SyntheticRatingDataset
        {
            SchemaVersion = SchemaVersion,
            Source = source,
            Scope = ToScope(dto.Scope, null, "scope"),
            Matches = matches
        };

        // Validate scope consistency, teams, duplicates and source at the boundary.
        _ = SyntheticRatingCalculator.Calculate(dataset);
        return dataset;
    }

    private static SyntheticScope ToScope(ScopeDto dto, int? matchIndex, string field)
    {
        return new SyntheticScope(
            Required(dto.Operator, matchIndex, $"{field}.operator"),
            Required(dto.Realm, matchIndex, $"{field}.realm"),
            Required(dto.Queue, matchIndex, $"{field}.queue"),
            Required(dto.Patch, matchIndex, $"{field}.patch"));
    }

    private static List<string> RequiredTeam(List<string?>? team, int index, string field)
    {
        if (team is null)
        {
            throw new ScoringValidationException(
                "rating.structure",
                $"Rating match {index} requires {field}.");
        }

        var result = new List<string>(team.Count);
        for (var i = 0; i < team.Count; i++)
        {
            result.Add(Required(team[i], index, $"{field}[{i}]"));
        }

        return result;
    }

    private static T Required<T>(T? value, int? index, string field)
        where T : struct
    {
        if (!value.HasValue)
        {
            throw new ScoringValidationException(
                "rating.structure",
                index.HasValue
                    ? $"Rating match {index} requires {field}."
                    : $"Rating JSON requires {field}.");
        }

        return value.Value;
    }

    private static string Required(string? value, int? index, string field)
    {
        if (value is null)
        {
            throw new ScoringValidationException(
                "rating.structure",
                index.HasValue
                    ? $"Rating match {index} requires {field}."
                    : $"Rating JSON requires {field}.");
        }

        return value;
    }

    private sealed class RatingDatasetDto
    {
        public int? SchemaVersion { get; init; }
        public string? Source { get; init; }
        public ScopeDto? Scope { get; init; }
        public List<MatchDto?>? Matches { get; init; }
    }

    private sealed class MatchDto
    {
        public string? MatchId { get; init; }
        public DateTimeOffset? Timestamp { get; init; }
        public ScopeDto? Scope { get; init; }
        public List<string?>? TeamA { get; init; }
        public List<string?>? TeamB { get; init; }
        public string? Winner { get; init; }
    }

    private sealed class ScopeDto
    {
        public string? Operator { get; init; }
        public string? Realm { get; init; }
        public string? Queue { get; init; }
        public string? Patch { get; init; }
    }
}
