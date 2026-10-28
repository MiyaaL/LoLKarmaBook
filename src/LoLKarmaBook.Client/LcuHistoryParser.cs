using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoLKarmaBook.Core;
using LoLKarmaBook.Scoring.Performance;

namespace LoLKarmaBook.Client;

public sealed record LcuHistoryMatch(long GameId, int QueueId, int MapId, string OwnerId,
    MatchStatRow Stats, PerformanceRosterMatch Roster);

/// <summary>Converts the client's match-history shape; display names never enter the scoring dataset.</summary>
public static class LcuHistoryParser
{
    public static bool TryParse(JsonElement game, ClientAccount account, out LcuHistoryMatch? match)
    {
        match = null;
        long gameId = LcuSnapshotParser.Number(game, "gameId"), queue = LcuSnapshotParser.Number(game, "queueId");
        long map = LcuSnapshotParser.Number(game, "mapId"), duration = LcuSnapshotParser.Number(game, "gameDuration");
        string mode = LcuSnapshotParser.Text(game, "gameMode");
        if (gameId <= 0 || queue is <= 0 or > int.MaxValue || duration < 300
            || (map != 11 && !(map is 12 or 14 && (mode.Equals("ARAM", StringComparison.OrdinalIgnoreCase)
                || queue == 2400 && mode.Equals("KIWI", StringComparison.OrdinalIgnoreCase))))) return false;
        JsonElement? identities = LcuSnapshotParser.Child(game, "participantIdentities"), participants = LcuSnapshotParser.Child(game, "participants");
        if (identities is not { ValueKind: JsonValueKind.Array } || participants is not { ValueKind: JsonValueKind.Array }
            || participants.Value.GetArrayLength() != 10) return false;
        var players = new Dictionary<long, JsonElement>();
        foreach (JsonElement identity in identities.Value.EnumerateArray())
        {
            long id = LcuSnapshotParser.Number(identity, "participantId", -1);
            var player = LcuSnapshotParser.Child(identity, "player");
            if (id < 0 || player is not { ValueKind: JsonValueKind.Object } || !players.TryAdd(id, player.Value)) return false;
        }
        var rows = new List<Row>(10);
        foreach (JsonElement participant in participants.Value.EnumerateArray())
        {
            long participantId = LcuSnapshotParser.Number(participant, "participantId", -1);
            if (!players.TryGetValue(participantId, out JsonElement player)) return false;
            long team = LcuSnapshotParser.Number(participant, "teamId");
            if (team is not (100 or 200)) return false;
            string puuid = LcuSnapshotParser.Text(player, "puuid");
            long summoner = LcuSnapshotParser.Number(player, "summonerId");
            bool owner = !PlayerIdentity.IsPlaceholderValue(puuid) ? puuid == account.Puuid
                : account.SummonerId > 0 && summoner == account.SummonerId;
            string stable = owner ? PlayerKey(account.Puuid, account.SummonerId, account.PlatformId) : PlayerKey(puuid, summoner, account.PlatformId);
            if (stable.Length == 0) return false;
            JsonElement? stats = LcuSnapshotParser.Child(participant, "stats");
            JsonElement? win = LcuSnapshotParser.Child(stats, "win");
            if (win is not { ValueKind: JsonValueKind.True or JsonValueKind.False }) return false;
            long[] counters = [Counter(stats, "kills"), Counter(stats, "deaths"), Counter(stats, "assists"),
                Counter(stats, "totalDamageDealtToChampions"), Counter(stats, "totalDamageTaken"), Counter(stats, "goldEarned")];
            if (counters.Any(value => value < 0) || counters.Take(3).Any(value => value > int.MaxValue)) return false;
            rows.Add(new Row(stable, team, owner, win.Value.ValueKind == JsonValueKind.True, counters));
        }
        if (rows.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != 10 || rows.Count(row => row.Owner) != 1) return false;
        Row self = rows.Single(row => row.Owner);
        Row[] own = rows.Where(row => row.Team == self.Team).ToArray(), enemies = rows.Where(row => row.Team != self.Team).ToArray();
        if (own.Length != 5 || enemies.Length != 5 || own.Any(row => row.Win != self.Win) || enemies.Any(row => row.Win == self.Win)) return false;
        long started = LcuSnapshotParser.Number(game, "gameCreation");
        DateTimeOffset timestamp;
        try { timestamp = DateTimeOffset.FromUnixTimeMilliseconds(started); }
        catch (ArgumentOutOfRangeException) { return false; }
        if (started <= 0) return false;
        string matchId = "lcu-" + account.PlatformId + "-" + gameId.ToString(CultureInfo.InvariantCulture);
        long kills = own.Sum(row => row.Counters[0]);
        if (kills > int.MaxValue) return false;
        var stat = new MatchStatRow(matchId, self.Win, (int)self.Counters[0], (int)self.Counters[1], (int)self.Counters[2], (int)kills,
            self.Counters[3], own.Sum(row => row.Counters[3]), self.Counters[4], own.Sum(row => row.Counters[4]), self.Counters[5], own.Sum(row => row.Counters[5]));
        var roster = new PerformanceRosterMatch(matchId, timestamp, own.Where(row => !row.Owner).Select(row => row.Id).ToArray(), enemies.Select(row => row.Id).ToArray());
        match = new(gameId, (int)queue, (int)map, self.Id, stat, roster);
        return true;
    }

    private static long Counter(JsonElement? stats, string name)
    {
        long value = LcuSnapshotParser.Number(stats, name, -1);
        return value is >= 0 and <= 1_000_000_000_000 ? value : -1;
    }

    public static string PlayerKey(string puuid, long summonerId, string platform)
    {
        string value = !PlayerIdentity.IsPlaceholderValue(puuid) ? "puuid:" + puuid
            : summonerId > 0 && platform != "unknown" ? "summoner:" + platform + ":" + summonerId.ToString(CultureInfo.InvariantCulture) : "";
        return value.Length == 0 ? "" : "player-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private sealed record Row(string Id, long Team, bool Owner, bool Win, long[] Counters);
}
