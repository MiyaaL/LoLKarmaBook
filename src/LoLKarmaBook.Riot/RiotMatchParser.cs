using System.Text.Json;
using LoLKarmaBook.Scoring.Performance;

namespace LoLKarmaBook.Riot;

/// <summary>One completed match, reduced to what an estimate needs.</summary>
public sealed record RiotCompletedMatch(
    string MatchId,
    DateTimeOffset StartedAt,
    RiotMatchRow Roster,
    MatchStatRow Stats);

/// <summary>
/// The roster half of one match: the owner, their four teammates and the five
/// opponents. Kept in the scoring layer's shape so an estimate can consume it
/// without another conversion.
/// </summary>
public sealed record RiotMatchRow(
    string MatchId,
    DateTimeOffset Timestamp,
    string OwnerPuuid,
    IReadOnlyList<string> Teammates,
    IReadOnlyList<string> Opponents);

/// <summary>
/// Turns a match-v5 detail document into a stat row and a roster row, keeping
/// only per-player counters Riot publishes. Anything missing, malformed or
/// inconsistent causes the match to be skipped rather than guessed at.
/// </summary>
public static class RiotMatchParser
{
    /// <summary>Supported 5v5 maps for ranked, ARAM and ARAM: Mayhem.</summary>
    public const int SummonersRiftMapId = 11;
    public const int HowlingAbyssMapId = 12;
    public const int ButchersBridgeMapId = 14;

    /// <summary>Riot's remake marker; a remake is not a game and is skipped.</summary>
    private const int RemakeGameEndId = 0;

    /// <summary>Shorter than this and a remake marker means no real result.</summary>
    private const long RemakeDurationSeconds = 300;

    public static bool TryParse(JsonElement root, string ownerPuuid, out RiotCompletedMatch? match, out string? skipReason)
    {
        match = null;
        skipReason = null;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("info", out JsonElement info)
            || info.ValueKind != JsonValueKind.Object
            || !info.TryGetProperty("participants", out JsonElement participants)
            || participants.ValueKind != JsonValueKind.Array)
        {
            skipReason = "缺少 info.participants。";
            return false;
        }

        string matchId = Text(root, "metadata", "matchId");
        if (matchId.Length == 0)
        {
            skipReason = "缺少 metadata.matchId。";
            return false;
        }

        if (Int(info, "mapId") is not (SummonersRiftMapId or HowlingAbyssMapId or ButchersBridgeMapId))
        {
            skipReason = "不支持该地图。";
            return false;
        }

        if (Int(info, "gameEndId") == RemakeGameEndId
            && Long(info, "gameDuration") is { } duration && duration < RemakeDurationSeconds)
        {
            skipReason = "重开局，不计入评分。";
            return false;
        }

        long gameStart = Long(info, "gameStartTimestamp") ?? 0;
        long created = Long(info, "gameCreation") ?? 0;
        long startedAt = gameStart > 0 ? gameStart : created;
        if (startedAt <= 0)
        {
            skipReason = "缺少对局开始时间。";
            return false;
        }

        var rows = new List<Participant>(10);
        foreach (JsonElement participant in participants.EnumerateArray())
        {
            if (participant.ValueKind != JsonValueKind.Object) continue;
            string puuid = Text(participant, "puuid");
            if (puuid.Length == 0) continue;

            rows.Add(new Participant(
                puuid,
                Int(participant, "teamId") ?? 0,
                Long(participant, "kills") ?? 0,
                Long(participant, "deaths") ?? 0,
                Long(participant, "assists") ?? 0,
                Long(participant, "totalDamageDealtToChampions") ?? 0,
                Long(participant, "totalDamageTaken") ?? 0,
                Long(participant, "goldEarned") ?? 0,
                Bool(participant, "win") ?? false));
        }

        Participant? owner = rows.FirstOrDefault(r => string.Equals(r.Puuid, ownerPuuid, StringComparison.Ordinal));
        if (owner is null)
        {
            skipReason = "本局名单中没有该账号。";
            return false;
        }

        Participant[] own = rows.Where(r => r.TeamId == owner.TeamId).ToArray();
        Participant[] enemies = rows.Where(r => r.TeamId != owner.TeamId).ToArray();
        if (own.Length != 5 || enemies.Length != 5)
        {
            skipReason = "不是 5 对 5 的完整对局。";
            return false;
        }

        // Team totals are the sum over the owner's own side, so every share the
        // performance score computes stays inside 0..1.
        long teamKills = own.Sum(r => r.Kills);
        long teamDamage = own.Sum(r => r.Damage);
        long teamTaken = own.Sum(r => r.Taken);
        long teamGold = own.Sum(r => r.Gold);

        var stats = new MatchStatRow(
            matchId,
            owner.Win,
            (int)Math.Min(owner.Kills, int.MaxValue),
            (int)Math.Min(owner.Deaths, int.MaxValue),
            (int)Math.Min(owner.Assists, int.MaxValue),
            (int)Math.Min(teamKills, int.MaxValue),
            owner.Damage,
            teamDamage,
            owner.Taken,
            teamTaken,
            owner.Gold,
            teamGold);

        var roster = new RiotMatchRow(
            matchId,
            DateTimeOffset.FromUnixTimeMilliseconds(startedAt),
            ownerPuuid,
            own.Where(r => r != owner).Select(r => r.Puuid).ToArray(),
            enemies.Select(r => r.Puuid).ToArray());

        match = new RiotCompletedMatch(matchId, roster.Timestamp, roster, stats);
        return true;
    }

    private sealed record Participant(
        string Puuid,
        int TeamId,
        long Kills,
        long Deaths,
        long Assists,
        long Damage,
        long Taken,
        long Gold,
        bool Win);

    private static string Text(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string Text(JsonElement element, string first, string second)
        => element.TryGetProperty(first, out JsonElement nested) && nested.ValueKind == JsonValueKind.Object
            ? Text(nested, second)
            : string.Empty;

    private static int? Int(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out int i) => i,
            JsonValueKind.Number when value.TryGetDouble(out double d) && d is <= int.MaxValue and >= int.MinValue
                => (int)d,
            _ => null
        };
    }

    private static long? Long(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out long l) => l,
            JsonValueKind.Number when value.TryGetDouble(out double d) && double.IsFinite(d) => (long)d,
            _ => null
        };
    }

    private static bool? Bool(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
