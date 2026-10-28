using System.Reflection;
using System.Text.Json;
using LoLKarmaBook.Client;

static class HistorySchemaProbe
{
    public static async Task<int> RunAsync(bool queuesOnly = false)
    {
        var discovery = await new LcuConnectionDiscovery().DiscoverAsync(null, default);
        if (discovery.Connection is null) { Console.WriteLine("Connection=" + discovery.Status); return 1; }
        using var client = new LcuClientService();
        typeof(LcuClientService).GetMethod("EnsureConnection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(client, [discovery.Connection]);
        async Task<JsonElement?> Read(string path) => await (Task<JsonElement?>)typeof(LcuClientService)
            .GetMethod("GetJsonAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(client, [path, CancellationToken.None])!;
        if (queuesOnly)
        {
            object Queue(JsonElement queue) => new { QueueId = queue.TryGetProperty("id", out var id) ? id.ToString() : null,
                MapId = queue.TryGetProperty("mapId", out var map) ? map.ToString() : null,
                Name = queue.TryGetProperty("name", out var name) ? name.GetString() : null,
                Type = queue.TryGetProperty("type", out var type) ? type.GetString() : null,
                GameMode = queue.TryGetProperty("gameMode", out var mode) ? mode.GetString() : null };
            foreach (int id in new[] { 3140, 2400 })
            {
                var queueResponse = await Read("/lol-game-queues/v1/queues/" + id);
                Console.WriteLine(JsonSerializer.Serialize(new { RequestedQueue = id, Queue = queueResponse is null ? null : Queue(queueResponse.Value) }));
            }
            var flow = await Read("/lol-gameflow/v1/session");
            if (flow is { } session && session.TryGetProperty("gameData", out var data) && data.TryGetProperty("queue", out var currentQueue))
                Console.WriteLine(JsonSerializer.Serialize(new { CurrentQueue = Queue(currentQueue) }));
            return 0;
        }
        var account = await Read("/lol-summoner/v1/current-summoner");
        if (account is null || !account.Value.TryGetProperty("puuid", out var puuid)) { Console.WriteLine("AccountDetected=false"); return 1; }
        var history = await Read("/lol-match-history/v1/products/lol/" + Uri.EscapeDataString(puuid.GetString()!) + "/matches?begIndex=0&endIndex=19");
        if (history is null) { Console.WriteLine("HistoryAvailable=false"); return 1; }
        object Shape(JsonElement e) => e.ValueKind == JsonValueKind.Object ? e.EnumerateObject().Select(p => p.Name + ":" + p.Value.ValueKind).ToArray() : [e.ValueKind.ToString()];
        Console.WriteLine(JsonSerializer.Serialize(new { HistoryShape = Shape(history.Value) }));
        if (!history.Value.TryGetProperty("games", out var container)) return 1;
        Console.WriteLine(JsonSerializer.Serialize(new { GamesShape = Shape(container) }));
        if (!container.TryGetProperty("games", out var games) || games.GetArrayLength() == 0) { Console.WriteLine("GameCount=0"); return 0; }
        var game = games[0];
        Console.WriteLine(JsonSerializer.Serialize(new { GameCount = games.GetArrayLength(), SummaryShape = Shape(game) }));
        if (!game.TryGetProperty("gameId", out var gameId)) return 1;
        var detail = await Read("/lol-match-history/v1/games/" + gameId.ToString());
        if (detail is null) { Console.WriteLine("DetailAvailable=false"); return 1; }
        Console.WriteLine(JsonSerializer.Serialize(new { DetailShape = Shape(detail.Value),
            ParticipantCount = detail.Value.TryGetProperty("participants", out var ps) ? ps.GetArrayLength() : 0,
            IdentityCount = detail.Value.TryGetProperty("participantIdentities", out var ids) ? ids.GetArrayLength() : 0,
            MapId = detail.Value.TryGetProperty("mapId", out var map) ? map.GetInt32() : 0,
            QueueId = detail.Value.TryGetProperty("queueId", out var queue) ? queue.GetInt32() : 0 }));
        if (detail.Value.TryGetProperty("participants", out var participants) && participants.GetArrayLength() > 0)
        {
            var p = participants[0];
            Console.WriteLine(JsonSerializer.Serialize(new { ParticipantShape = Shape(p), StatsShape = p.TryGetProperty("stats", out var stats) ? Shape(stats) : null }));
        }
        if (detail.Value.TryGetProperty("participantIdentities", out var identities) && identities.GetArrayLength() > 0)
            Console.WriteLine(JsonSerializer.Serialize(new { IdentityShape = Shape(identities[0]), PlayerShape = identities[0].TryGetProperty("player", out var player) ? Shape(player) : null }));
        return 0;
    }
}
