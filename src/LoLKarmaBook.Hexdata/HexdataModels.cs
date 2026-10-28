using System.Text.Json;

namespace LoLKarmaBook.Hexdata;

/// <summary>One readiness-level estimate (the player's, or a participant's).</summary>
public sealed record HexdataEstimate(
    double? Score,
    string? Confidence,
    string? Basis,
    double? IntervalHalfWidth,
    double? PercentileBps,
    string? EvidenceTier)
{
    /// <summary>Percentile as 0–100, or null when the server did not provide one.</summary>
    public double? Percentile => PercentileBps is { } bps ? bps / 100d : null;
}

public sealed record HexdataMatchSummary(
    int? ChampionId,
    string? ChampionName,
    int? Kills,
    int? Deaths,
    int? Assists,
    bool? Win,
    int? DurationSeconds,
    double? MatchHexScore);

public sealed record HexdataParticipant(
    string? PublicGameId,
    int? ChampionId,
    string? ChampionName,
    int? Kills,
    int? Deaths,
    int? Assists,
    double? MatchHexScore,
    bool IsTarget,
    bool Hidden,
    HexdataEstimate? CurrentEstimate);

public sealed record HexdataTeam(
    string? Side,
    int Scored,
    int Total,
    IReadOnlyList<HexdataParticipant> Participants);

public sealed record HexdataMatch(
    string GameId,
    DateTimeOffset? StartedAt,
    int? DurationSeconds,
    bool? TargetWin,
    HexdataMatchSummary Summary,
    bool DetailsAvailable,
    int? TeamDelta,
    IReadOnlyList<HexdataTeam> Teams);

/// <summary>
/// A parsed public match report. The raw JSON is kept alongside the parsed fields
/// so a saved copy can be re-read later, even after the site's schema moves on.
/// </summary>
public sealed record HexdataReport(
    string ResolvedGameId,
    string RegionKey,
    DateTimeOffset? GeneratedAt,
    string ModelVersion,
    HexdataEstimate? Estimate,
    string? CollectingReason,
    IReadOnlyList<HexdataMatch> RecentMatches,
    string RawJson)
{
    public bool IsCollecting => Estimate is null;

    public static HexdataReport Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        if (!document.RootElement.TryGetProperty("report", out JsonElement report)
            || report.ValueKind != JsonValueKind.Object)
        {
            throw new HexdataException("Hexdata 返回的数据格式无法识别。", 200, "invalid_response");
        }

        var matches = new List<HexdataMatch>();
        if (report.TryGetProperty("recentMatches", out JsonElement rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement row in rows.EnumerateArray().Take(20))
            {
                matches.Add(ReadMatch(row));
            }
        }

        return new HexdataReport(
            Text(report, "resolvedGameId") ?? string.Empty,
            Text(report, "regionKey") ?? string.Empty,
            Time(report, "generatedAt"),
            Text(report, "modelVersion") ?? string.Empty,
            ReadEstimate(report.TryGetProperty("targetEstimate", out JsonElement estimate) ? estimate : null),
            Collecting(report),
            matches,
            json);
    }

    private static string? Collecting(JsonElement report)
    {
        if (!report.TryGetProperty("targetEstimate", out JsonElement estimate) || estimate.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? status = Text(estimate, "status");
        return status == "collecting_data" ? Text(estimate, "reason") : null;
    }

    private static HexdataMatch ReadMatch(JsonElement row)
    {
        var teams = new List<HexdataTeam>();
        if (row.TryGetProperty("teams", out JsonElement teamRows) && teamRows.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement team in teamRows.EnumerateArray())
            {
                var participants = new List<HexdataParticipant>();
                if (team.TryGetProperty("participants", out JsonElement rows) && rows.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement participant in rows.EnumerateArray())
                    {
                        participants.Add(new HexdataParticipant(
                            Text(participant, "publicGameId"),
                            Integer(participant, "championId"),
                            Text(participant, "championName"),
                            Integer(participant, "kills"),
                            Integer(participant, "deaths"),
                            Integer(participant, "assists"),
                            Number(participant, "matchHexScore"),
                            Flag(participant, "isTarget") ?? false,
                            Flag(participant, "hidden") ?? false,
                            participant.TryGetProperty("currentEstimate", out JsonElement current) ? ReadEstimate(current) : null));
                    }
                }

                int scored = 0;
                int total = 0;
                if (team.TryGetProperty("estimateCoverage", out JsonElement coverage) && coverage.ValueKind == JsonValueKind.Object)
                {
                    scored = Integer(coverage, "scored") ?? 0;
                    total = Integer(coverage, "total") ?? 0;
                }

                teams.Add(new HexdataTeam(Text(team, "side"), scored, total, participants));
            }
        }

        return new HexdataMatch(
            Text(row, "gameId") ?? string.Empty,
            Time(row, "startedAt"),
            Integer(row, "durationSeconds"),
            Flag(row, "targetWin"),
            ReadSummary(row.TryGetProperty("targetMatchSummary", out JsonElement summary) ? summary : null),
            Flag(row, "detailsAvailable") ?? false,
            Integer(row, "teamDelta"),
            teams);
    }

    private static HexdataMatchSummary ReadSummary(JsonElement? summary)
    {
        if (summary is not { ValueKind: JsonValueKind.Object } value)
        {
            return new HexdataMatchSummary(null, null, null, null, null, null, null, null);
        }

        return new HexdataMatchSummary(
            Integer(value, "championId"),
            Text(value, "championName"),
            Integer(value, "kills"),
            Integer(value, "deaths"),
            Integer(value, "assists"),
            Flag(value, "win"),
            Integer(value, "durationSeconds"),
            Number(value, "matchHexScore"));
    }

    private static HexdataEstimate? ReadEstimate(JsonElement? estimate)
    {
        if (estimate is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        string? status = Text(value, "status");
        if (status is not null && status != "ready")
        {
            return null;
        }

        double? score = Number(value, "score");
        if (score is null)
        {
            return null;
        }

        return new HexdataEstimate(
            score,
            Text(value, "confidence"),
            Text(value, "basis"),
            Number(value, "intervalHalfWidth"),
            Number(value, "percentileBps"),
            Text(value, "evidenceTier"));
    }

    internal static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static int? Integer(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int parsed)
            ? parsed
            : null;

    internal static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out double parsed)
            ? parsed
            : null;

    internal static bool? Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static DateTimeOffset? Time(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        // Hexdata sends match times as Unix milliseconds; generatedAt can be ISO.
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long epoch))
        {
            try
            {
                return epoch > 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                    : DateTimeOffset.FromUnixTimeSeconds(epoch);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            ? parsed
            : null;
    }
}

public sealed class HexdataException : Exception
{
    public HexdataException(string message, int status, string? code = null, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
        Code = code;
    }

    public int Status { get; }

    public string? Code { get; }

    public bool IsUnauthorized => Status is 401 or 403;
}
