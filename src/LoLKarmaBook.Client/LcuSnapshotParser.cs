using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoLKarmaBook.Core;

namespace LoLKarmaBook.Client;

public static class LcuSnapshotParser
{
    public static GamePhase ParsePhase(string phase) => phase.ToLowerInvariant() switch
    {
        "lobby" or "none" => GamePhase.Lobby,
        "matchmaking" or "readycheck" => GamePhase.Matchmaking,
        "champselect" => GamePhase.ChampionSelect,
        "gamestart" or "loading" or "reconnect" => GamePhase.Loading,
        "inprogress" or "in_game" => GamePhase.InGame,
        "waitingforstats" or "preendofgame" or "endofgame" => GamePhase.Finished,
        "disconnected" => GamePhase.Disconnected,
        _ => GamePhase.Unknown
    };

    public static ClientAccount ParseAccount(JsonElement current, string platformId)
    {
        string puuid = Text(current, "puuid");
        long id = Number(current, "summonerId");
        string name = Text(current, "gameName", "displayName", "summonerName");
        string tag = Text(current, "tagLine");
        if (name.Length == 0) name = "已登录账号";
        string stable = !PlayerIdentity.IsPlaceholderValue(puuid) ? "puuid:" + puuid : platformId + ":summoner:" + id.ToString(CultureInfo.InvariantCulture);
        return new ClientAccount(puuid, name, tag, platformId, ClientAccount.CreateProfileKey(stable), id);
    }

    public static string GameId(JsonElement? flow, JsonElement? selection)
    {
        JsonElement? data = Child(flow, "gameData");
        long gameId = Number(data, "gameId");
        if (gameId <= 0) gameId = Number(selection, "gameId");
        return gameId > 0 ? gameId.ToString(CultureInfo.InvariantCulture) : "";
    }

    public static IEnumerable<long> VisibleSummonerIds(JsonElement? flow, JsonElement? selection)
    {
        foreach (var row in Rows(flow, selection))
        {
            if (IsHidden(row.Value)) continue;
            long id = Number(row.Value, "summonerId");
            if (id > 0 && (Display(row.Value).Length == 0 || PlayerIdentity.IsPlaceholderValue(Text(row.Value, "puuid")))) yield return id;
        }
    }

    public static MatchSession? ParseSession(ClientAccount account, GamePhase phase, JsonElement? flow, JsonElement? selection,
        IReadOnlyDictionary<long, JsonElement> summoners, string localSessionId, string providerId, long generation, long sequence,
        DateTimeOffset observedAt, IReadOnlyDictionary<int, string>? championNames = null)
    {
        if (flow is null && selection is null) return null;
        var raw = Rows(flow, selection).ToArray();
        // Name-only entries are valid only while the complete observed roster remains unchanged.
        // A slot index alone can silently transfer an editor draft to a different player.
        string rosterKey = Hash(JsonSerializer.Serialize(new
        {
            localSessionId, phase,
            rows = raw.Select(row =>
            {
                bool hidden = IsHidden(row.Value);
                long id = Number(row.Value, "summonerId");
                JsonElement? enriched = !hidden && id > 0 && summoners.TryGetValue(id, out JsonElement value) ? value : null;
                string name = Display(row.Value), puuid = Text(row.Value, "puuid");
                if (name.Length == 0) name = Display(enriched);
                if (PlayerIdentity.IsPlaceholderValue(puuid)) puuid = Text(enriched, "puuid");
                return new { row.Team, row.Index, hidden, name = hidden ? "" : name, puuid = hidden ? "" : puuid,
                    id = hidden ? 0 : id, cell = Number(row.Value, "cellId", -1), observedRow = hidden ? (JsonElement?)null : row.Value };
            })
        }));
        long localCellId = Number(selection, "localPlayerCellId", missing: -1);
        string? selfTeam = raw.FirstOrDefault(row => IsSelf(row.Value, row.Team, account, localCellId)).Team;
        if (selection is not null) selfTeam = "myTeam";
        var participants = new List<Participant>(raw.Length);
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in raw)
        {
            bool self = IsSelf(row.Value, row.Team, account, localCellId);
            ParticipantSide side = selfTeam is null ? ParticipantSide.Unknown : row.Team == selfTeam ? ParticipantSide.Ally : ParticipantSide.Enemy;
            // Unknown/hidden rows have no identity with which to carry state between observations.
            string key = "unverified-" + Hash(JsonSerializer.Serialize(new { providerId, generation, sequence, localSessionId, row.Team, row.Index }));
            if (IsHidden(row.Value)) { participants.Add(Participant.Hidden(key, side, self)); continue; }
            long id = Number(row.Value, "summonerId");
            JsonElement? enriched = id > 0 && summoners.TryGetValue(id, out JsonElement value) ? value : null;
            string name = Display(row.Value);
            if (name.Length == 0) name = Display(enriched);
            if (self && name.Length == 0) name = account.DisplayLabel.Length <= 128 ? account.DisplayLabel : account.DisplayName;
            string puuid = Text(row.Value, "puuid");
            if (PlayerIdentity.IsPlaceholderValue(puuid)) puuid = Text(enriched, "puuid");
            if (self && PlayerIdentity.IsPlaceholderValue(puuid)) puuid = account.Puuid;
            string? champion = ChampionName(Number(row.Value, "championId"), championNames);
            if (name.Length == 0)
            {
                participants.Add(Participant.UnknownVisibility(key, side, self));
                continue;
            }
            PlayerIdentity? identity = null;
            if (!PlayerIdentity.IsPlaceholderValue(puuid))
                PlayerIdentity.TryCreate("riot", "lcu-puuid", "global", "puuid", puuid, out identity, out _);
            else if (id > 0 && account.PlatformId.Length > 0 && account.PlatformId != "unknown")
                PlayerIdentity.TryCreate("riot", "lcu-summoner", account.PlatformId, "summonerId", id.ToString(CultureInfo.InvariantCulture), out identity, out _);
            key = identity is null
                ? "name-" + rosterKey + "-" + row.Team + "-" + row.Index.ToString(CultureInfo.InvariantCulture)
                : "player-" + Hash(JsonSerializer.Serialize(new[] { identity.Operator, identity.IdentityNamespace, identity.RealmScope, identity.Kind, identity.Value }));
            string baseKey = key;
            for (int duplicate = 2; !usedKeys.Add(key); duplicate++) key = baseKey + "-" + duplicate.ToString(CultureInfo.InvariantCulture);
            participants.Add(identity is null ? Participant.VisibleNameOnly(key, side, name, champion, self)
                : Participant.Visible(key, side, name, identity, champion, self));
        }
        JsonElement? data = Child(flow, "gameData");
        long queueId = Number(Child(data, "queue"), "id");
        if (queueId == 0) queueId = Number(data, "queueId");
        long mapId = Number(Child(data, "map"), "id");
        if (mapId == 0) mapId = Number(Child(data, "gameMapInfo"), "mapId");
        string gameId = GameId(flow, selection);
        string? external = gameId.Length == 0 ? null : "lcu:" + account.PlatformId + ":" + gameId;
        return new MatchSession(localSessionId, SourceKind.Live, providerId, generation, sequence, observedAt, phase,
            queueId > 0 && queueId <= int.MaxValue ? (int)queueId : null,
            mapId > 0 && mapId <= int.MaxValue ? (int)mapId : null,
            external, participants.Count >= 10, participants);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static IEnumerable<(string Team, int Index, JsonElement Value)> Rows(JsonElement? flow, JsonElement? selection)
    {
        JsonElement? container = selection ?? Child(flow, "gameData");
        string[] teams = selection is not null ? ["myTeam", "theirTeam"] : ["teamOne", "teamTwo"];
        foreach (string team in teams)
        {
            JsonElement? array = Child(container, team);
            if (array is not { ValueKind: JsonValueKind.Array }) continue;
            int index = 0;
            foreach (JsonElement row in array.Value.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Object) yield return (team, index, row);
                index++;
            }
        }
    }

    private static bool IsSelf(JsonElement row, string team, ClientAccount account, long localCellId) =>
        (!PlayerIdentity.IsPlaceholderValue(account.Puuid) && Text(row, "puuid") == account.Puuid)
        || (account.SummonerId > 0 && Number(row, "summonerId") == account.SummonerId)
        || (team == "myTeam" && localCellId >= 0 && Number(row, "cellId", -2) == localCellId);

    public static bool IsHidden(JsonElement row)
    {
        foreach (string name in new[] { "isObfuscated", "isAnonymized", "isAnonymous" })
            if (Child(row, name) is { ValueKind: JsonValueKind.True }) return true;
        if (Child(row, "isVisible") is { ValueKind: JsonValueKind.False }) return true;
        string visibility = Text(row, "nameVisibilityType", "visibility");
        return visibility.Equals("hidden", StringComparison.OrdinalIgnoreCase)
            || visibility.Equals("anonymous", StringComparison.OrdinalIgnoreCase)
            || visibility.Equals("obfuscated", StringComparison.OrdinalIgnoreCase);
    }

    private static string Display(JsonElement? row)
    {
        string name = Text(row, "gameName", "riotIdGameName", "displayName", "summonerName");
        if (name.Length == 0) return "";
        string tag = Text(row, "tagLine", "riotIdTagline");
        return tag.Length == 0 || name.Contains('#') || name.Length + tag.Length + 1 > 128 ? name : name + "#" + tag;
    }

    internal static JsonElement? Child(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object }) return null;
        return element.Value.TryGetProperty(name, out JsonElement value) ? value : null;
    }

    internal static string Text(JsonElement? element, params string[] names)
    {
        foreach (string name in names)
        {
            JsonElement? value = Child(element, name);
            if (value is not { ValueKind: JsonValueKind.String }) continue;
            string text = value.Value.GetString()?.Trim() ?? "";
            if (text.Length > 0 && text.Length <= 128 && !text.Any(char.IsControl)) return text;
        }
        return "";
    }

    /// <summary>Localized champion name when the client exposes the asset list, otherwise "#id".</summary>
    private static string? ChampionName(long championId, IReadOnlyDictionary<int, string>? championNames)
    {
        if (championId <= 0 || championId > int.MaxValue) return null;
        return championNames is not null && championNames.TryGetValue((int)championId, out string? name)
            ? name
            : "#" + championId.ToString(CultureInfo.InvariantCulture);
    }

    internal static long Number(JsonElement? element, string name, long missing = 0)
    {
        JsonElement? value = Child(element, name);
        if (value is { ValueKind: JsonValueKind.Number } && value.Value.TryGetInt64(out long numeric)) return numeric;
        if (value is { ValueKind: JsonValueKind.String } && long.TryParse(value.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long text)) return text;
        return missing;
    }
}
